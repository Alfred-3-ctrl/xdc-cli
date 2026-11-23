import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import path from 'node:path';
import { PassThrough, Writable } from 'node:stream';
import { setTimeout as delay } from 'node:timers/promises';
import test from 'node:test';
import { startNativeGlass } from '../desktop/native-glass.js';

function workerFixture(options = {}) {
  const window = new EventEmitter();
  window.isDestroyed = () => false;
  window.getNativeWindowHandle = () => {
    const handle = Buffer.alloc(8);
    handle.writeBigUInt64LE(9007199254740995n);
    return handle;
  };
  const args = [];
  const sent = [];
  const ready = [];
  const failures = [];
  const child = new EventEmitter();
  child.pid = 712;
  child.stdout = new PassThrough();
  child.stdin = new Writable({
    write(chunk, _encoding, complete) {
      sent.push(JSON.parse(chunk.toString()));
      complete();
    },
  });
  child.kill = () => { child.emit('exit', null, 'SIGTERM'); };
  const controller = startNativeGlass({
    window,
    radius: 30,
    onReady: (detail) => ready.push(detail),
    onExit: (failure) => failures.push(failure),
    spawnImpl(...call) {
      args.push(call);
      return child;
    },
    ...options,
  });
  return { window, child, controller, args, sent, ready, failures };
}

test('native glass sends exact handles and radius updates, and closes with its window', async () => {
  const { window, child, controller, args, sent, ready, failures } = workerFixture();
  assert.equal(controller.state, 'starting');
  assert.match(args[0][0], /native-glass[\\/]Xdc\.NativeGlass\.exe$/);
  assert.deepEqual(args[0][1], ['--backdrop', '9007199254740995', String(process.pid)]);
  assert.equal(args[0][2].cwd, path.dirname(args[0][0]));
  assert.equal(args[0][2].windowsHide, true);
  controller.setRadius(27);
  child.emit('spawn');
  child.stdout.write('backdrop 9007199254741013\r\nready\r\nready\r\n');
  assert.equal(controller.state, 'ready');
  assert.deepEqual(ready, [{ handle: '9007199254741013', pid: 712 }]);
  controller.setRadius(12);
  controller.setRadius(12);
  assert.deepEqual(sent, [{ radius: 27 }, { radius: 12 }]);

  window.emit('closed');
  controller.close();
  controller.setRadius(15);
  assert.equal(controller.state, 'closed');
  assert.equal(child.stdin.writableEnded, true);
  child.emit('exit', 0);
  await Promise.resolve();
  assert.deepEqual(failures, []);
});

test('native glass treats a missing or blocked worker as fallback without restart', async () => {
  for (const [code, reason] of [['ENOENT', 'missing'], ['EPERM', 'launch-failed']]) {
    const { child, controller, args, ready, failures } = workerFixture();
    child.emit('error', Object.assign(new Error('Cannot start the binary'), { code }));
    child.emit('exit', 1);
    child.stdout.write('backdrop 99\nready\n');
    await Promise.resolve();
    assert.equal(controller.state, 'unavailable');
    assert.deepEqual(failures, [{ reason, code }]);
    assert.equal(ready.length, 0);
    assert.equal(args.length, 1);
  }
});

test('native glass validates that ready includes the paired window handle', async () => {
  for (const reply of ['ready\n', 'backdrop 0\nready\n', 'backdrop 9007199254740995\nready\n']) {
    const { child, controller, ready, failures } = workerFixture();
    child.emit('spawn');
    child.stdout.write(reply);
    child.emit('exit', 0);
    await Promise.resolve();
    assert.equal(controller.state, 'unavailable');
    assert.deepEqual(failures, [{ reason: 'protocol-error' }]);
    assert.equal(ready.length, 0);
    assert.equal(child.stdin.writableEnded, true);
  }
});

test('a stuck backdrop worker gets EOF and bounded cleanup', async () => {
  const { child, controller, failures } = workerFixture({ startupTimeoutMs: 5, closeTimeoutMs: 5 });
  let killed = false;
  child.kill = () => {
    killed = true;
    child.emit('exit', null, 'SIGTERM');
  };
  child.emit('spawn');
  await delay(40);
  assert.equal(controller.state, 'unavailable');
  assert.deepEqual(failures, [{ reason: 'timed-out' }]);
  assert.equal(child.stdin.writableEnded, true);
  assert.equal(killed, true);
});

test('native glass degrades cleanly if its output pipe fails after ready', async () => {
  const { child, controller, failures } = workerFixture();
  child.emit('spawn');
  child.stdout.write('backdrop 911\nready\n');
  child.stdout.emit('error', new Error('Fixture pipe error'));
  child.emit('exit', 1);
  await Promise.resolve();
  assert.equal(controller.state, 'unavailable');
  assert.deepEqual(failures, [{ reason: 'pipe-failed' }]);
});

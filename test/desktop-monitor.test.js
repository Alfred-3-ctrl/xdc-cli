import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { PassThrough, Writable } from 'node:stream';
import test from 'node:test';
import { bindWindowsMonitor, startTasklistPoll } from '../desktop/windows.js';

function monitorFixture() {
  const commands = [];
  const events = [];
  const child = new EventEmitter();
  child.stdout = new PassThrough();
  child.stdin = new Writable({
    write(chunk, _encoding, callback) {
      commands.push(chunk.toString().trim());
      callback();
    },
  });
  const monitor = bindWindowsMonitor(child, {
    onUu: (active) => events.push(active),
    onExit: (reason) => events.push(reason),
  });
  return { child, monitor, commands, events };
}

test('a canceled UU arming response cannot confirm a later request', async () => {
  const { child, monitor, commands } = monitorFixture();
  const first = monitor.arm();
  assert.equal(monitor.arm(), first);
  monitor.disarm();
  assert.equal(await first, false);

  const second = monitor.arm();
  child.stdout.write('armed 1\n');
  child.stdout.write('arm-aborted 2\n');
  assert.equal(await second, false);
  assert.deepEqual(commands, ['arm 1', 'disarm', 'arm 2']);
  child.stdout.end();
  monitor.disconnect();
});

test('a blocked native launch releases pending requests and triggers fallback once', async () => {
  const { child, monitor, events } = monitorFixture();
  const request = monitor.arm();
  child.emit('error', new Error('Native executable blocked'));
  child.emit('exit', 1);
  child.stdout.write('uu on\n');
  assert.equal(await request, false);
  assert.equal(await monitor.arm(), false);
  assert.deepEqual(events, ['launch-failed']);
  child.stdout.end();
  child.stdin.end();
});

test('tasklist failures preserve UU state and disconnect stops the watcher', async () => {
  const states = [];
  let calls = 0;
  let finish;
  const finished = new Promise((resolve) => { finish = resolve; });
  const stop = startTasklistPoll({
    intervalMs: 1,
    async query() {
      calls++;
      if (calls === 2) throw new Error('Fixture query failed');
      return calls < 4;
    },
    onUu(active) {
      states.push(active);
      if (!active) finish('finished');
    },
  });
  let timeout;
  try {
    const result = await Promise.race([
      finished,
      new Promise((resolve) => { timeout = setTimeout(() => resolve('timeout'), 1000); }),
    ]);
    assert.equal(result, 'finished');
    assert.deepEqual(states, [true, false]);
  } finally {
    clearTimeout(timeout);
    stop();
  }
  await new Promise((resolve) => setTimeout(resolve, 15));
  assert.equal(calls, 4);
});

import { spawn } from 'node:child_process';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';

const workerPath = fileURLToPath(new URL('./native-glass/Xdc.NativeGlass.exe', import.meta.url));
const maxWindowHandle = (1n << 63n) - 1n;

function normalizeRadius(value) {
  return Number.isFinite(value) ? Math.max(0, Math.min(value, 128)) : null;
}

function readHandle(window) {
  if (window.isDestroyed()) return null;
  const buffer = window.getNativeWindowHandle();
  if (!Buffer.isBuffer(buffer)) return null;
  if (buffer.length === 8) return buffer.readBigUInt64LE();
  if (buffer.length === 4) return BigInt(buffer.readUInt32LE());
  return null;
}

/** Own the lifetime of one native backdrop worker for an Electron window. */
export function startNativeGlass({
  window,
  radius,
  onReady = () => {},
  onExit = () => {},
  executable = workerPath,
  spawnImpl = spawn,
  startupTimeoutMs = 6000,
  closeTimeoutMs = 750,
}) {
  let state = 'starting';
  let currentRadius = normalizeRadius(radius) ?? 30;
  let child;
  let started = false;
  let lines;
  let startupTimer;
  let closeTimer;
  let exited = false;
  let backdropHandle;

  function stopWorker() {
    clearTimeout(startupTimer);
    window.removeListener('closed', close);
    lines?.close();
    if (!child || exited || closeTimer) return;
    child.stdin.end();
    closeTimer = setTimeout(() => {
      if (!exited) child.kill();
    }, closeTimeoutMs);
    closeTimer.unref();
  }

  function fail(reason, code) {
    if (state === 'closed' || state === 'unavailable') return;
    state = 'unavailable';
    stopWorker();
    queueMicrotask(() => {
      if (state === 'unavailable') onExit({ reason, ...(code && { code }) });
    });
  }

  function close() {
    if (state === 'closed') return;
    state = 'closed';
    stopWorker();
  }

  function sendRadius() {
    if (!started || (state !== 'starting' && state !== 'ready')) return;
    child.stdin.write(JSON.stringify({ radius: currentRadius }) + '\n');
  }

  const controller = {
    get state() { return state; },
    setRadius(value) {
      const next = normalizeRadius(value);
      if (next === null || next === currentRadius || state === 'closed' || state === 'unavailable') return;
      currentRadius = next;
      sendRadius();
    },
    close,
  };

  let handle;
  try {
    handle = readHandle(window);
  } catch {
    fail('invalid-window');
    return controller;
  }
  if (!handle) {
    fail('invalid-window');
    return controller;
  }
  try {
    const binary = path.resolve(executable);
    child = spawnImpl(binary, ['--backdrop', String(handle), String(process.pid)], {
      cwd: path.dirname(binary),
      windowsHide: true,
      stdio: ['pipe', 'pipe', 'ignore'],
    });
  } catch (error) {
    fail(error?.code === 'ENOENT' ? 'missing' : 'launch-failed', error?.code);
    return controller;
  }

  window.once('closed', close);
  child.stdin.on('error', () => fail('pipe-failed'));
  child.once('spawn', () => {
    started = true;
    sendRadius();
  });
  child.on('error', (error) => {
    if (!started) {
      exited = true;
      fail(error.code === 'ENOENT' ? 'missing' : 'launch-failed', error.code);
    } else {
      fail('worker-failed', error.code);
    }
  });
  child.once('exit', (code) => {
    exited = true;
    clearTimeout(closeTimer);
    fail('worker-exited', Number.isInteger(code) ? String(code) : undefined);
  });

  lines = createInterface({ input: child.stdout, crlfDelay: Infinity });
  lines.on('error', () => fail('pipe-failed'));
  lines.on('line', (line) => {
    if (line.startsWith('backdrop ') && state === 'starting') {
      const value = /^backdrop ([1-9]\d{0,18})$/.exec(line)?.[1];
      if (!value || BigInt(value) > maxWindowHandle || BigInt(value) === handle ||
          (backdropHandle && value !== backdropHandle)) {
        fail('protocol-error');
        return;
      }
      backdropHandle = value;
      return;
    }
    if (line === 'ready' && state === 'starting') {
      if (window.isDestroyed()) {
        close();
        return;
      }
      if (!backdropHandle || !Number.isSafeInteger(child.pid) || child.pid <= 0) {
        fail('protocol-error');
        return;
      }
      clearTimeout(startupTimer);
      state = 'ready';
      onReady({ handle: backdropHandle, pid: child.pid });
    } else if (/^error(?:\s|$)/.test(line)) {
      fail('worker-error');
    }
  });
  startupTimer = setTimeout(() => fail('timed-out'), startupTimeoutMs);
  startupTimer.unref();
  return controller;
}

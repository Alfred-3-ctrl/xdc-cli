import { createHash } from 'node:crypto';
import { execFile, spawn } from 'node:child_process';
import { access, mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { createInterface } from 'node:readline';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const run = promisify(execFile);
const source = fileURLToPath(new URL('./UuMonitor.cs', import.meta.url));

export async function startWindowsMonitor({ userData, appDirectory, onUu, onExit }) {
  let native;
  let stopPolling;
  let disconnected = false;
  let previous;
  function publish(active) {
    if (active === previous || disconnected) return;
    previous = active;
    onUu(active);
  }
  function pollInstead(reason) {
    if (disconnected || stopPolling) return;
    native = undefined;
    stopPolling = startTasklistPoll({ onUu: publish, query: tasklistProbe() });
    onExit(reason);
  }
  try {
    const monitor = await startNativeMonitor({ userData, appDirectory, onUu: publish, onExit: pollInstead });
    if (!stopPolling) native = monitor;
  } catch {
    pollInstead('compile-unavailable');
  }
  return {
    placeOnDesktop: (win) => native?.placeOnDesktop(win),
    setBackdrop: (handle, pid) => native?.setBackdrop(handle, pid),
    releaseDesktop: () => native?.releaseDesktop(),
    arm: () => native?.arm() ?? Promise.resolve(false),
    disarm: () => native?.disarm(),
    disconnect() {
      disconnected = true;
      native?.disconnect();
      stopPolling?.();
    },
  };
}

async function startNativeMonitor({ userData, appDirectory, onUu, onExit }) {
  const code = await readFile(source);
  const tag = createHash('sha256').update(code).digest('hex').slice(0, 12);
  const directory = path.join(userData, 'native');
  const executable = path.join(directory, `desktop-monitor-${tag}.exe`);
  await mkdir(directory, { recursive: true });
  try {
    await access(executable);
  } catch {
    const framework = path.join(process.env.WINDIR || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');
    await run(framework, ['/nologo', '/optimize+', `/out:${executable}`, source], {
      timeout: 15000, windowsHide: true,
    });
  }
  const child = spawn(executable, [process.execPath, appDirectory, String(process.pid), 'uu'], {
    windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'],
  });
  return bindWindowsMonitor(child, { onUu, onExit });
}

function tasklistProbe() {
  const executable = path.join(process.env.WINDIR || 'C:\\Windows', 'System32', 'tasklist.exe');
  let sessionFilter;
  const query = async (filters, signal) => {
    const args = [...filters.flatMap((filter) => ['/FI', filter]), '/FO', 'CSV', '/NH'];
    const { stdout } = await run(executable, args, { windowsHide: true, timeout: 4000, maxBuffer: 65536, signal });
    return stdout;
  };
  return async (signal) => {
    if (!sessionFilter) {
      const self = await query([`PID eq ${process.pid}`], signal);
      const session = /^"[^"\r\n]+","\d+","[^"\r\n]*","(\d+)"/m.exec(self)?.[1];
      if (!session) throw new Error('Windows session unavailable');
      sessionFilter = `SESSION eq ${session}`;
    }
    return /^"uu\.exe","\d+",/im.test(await query(['IMAGENAME eq uu.exe', sessionFilter], signal));
  };
}

export function startTasklistPoll({ onUu, query, intervalMs = 1800 }) {
  const stop = new AbortController();
  let previous;
  let timer;
  async function poll() {
    try {
      const active = await query(stop.signal);
      if (!stop.signal.aborted && active !== previous) {
        previous = active;
        onUu(active);
      }
    } catch {
      // Preserve the last known state on a failed or interrupted query.
    }
    if (!stop.signal.aborted) {
      timer = setTimeout(poll, intervalMs);
      timer.unref();
    }
  }
  void poll();
  return () => {
    stop.abort();
    clearTimeout(timer);
  };
}

export function bindWindowsMonitor(child, { onUu, onExit }) {
  let pendingArm;
  let armSequence = 0;
  let closed = false;
  const finishArming = (result) => {
    if (!pendingArm) return;
    const { resolve, timer } = pendingArm;
    pendingArm = undefined;
    clearTimeout(timer);
    resolve(result);
  };
  const lines = createInterface({ input: child.stdout });
  child.stdin.on('error', () => {});
  lines.on('line', (line) => {
    if (closed) return;
    if (line === 'uu on' || line === 'uu off') onUu(line === 'uu on');
    const reply = /^(armed|arm-aborted) (\d+)$/.exec(line);
    if (reply && Number(reply[2]) === pendingArm?.sequence) finishArming(reply[1] === 'armed');
  });
  const ended = (reason) => {
    if (closed) return;
    closed = true;
    finishArming(false);
    onExit(reason);
  };
  child.on('error', () => ended('launch-failed'));
  child.on('exit', () => ended('exited'));
  return {
    placeOnDesktop(win) {
      if (closed || win.isDestroyed()) return;
      const handle = win.getNativeWindowHandle();
      const value = handle.length === 8 ? handle.readBigInt64LE() : BigInt(handle.readUInt32LE());
      child.stdin.write(`desktop ${value}\n`);
    },
    setBackdrop(handle = '0', pid = 0) {
      if (!closed) child.stdin.write(`backdrop ${handle} ${pid}\n`);
    },
    releaseDesktop() {
      if (!closed) child.stdin.write('desktop 0\n');
    },
    arm() {
      if (closed) return Promise.resolve(false);
      if (pendingArm) return pendingArm.promise;
      const sequence = ++armSequence;
      let resolve;
      const promise = new Promise((done) => { resolve = done; });
      const timer = setTimeout(() => {
        finishArming(false);
        if (!closed) child.stdin.write('disarm\n');
      }, 2000);
      pendingArm = { sequence, resolve, timer, promise };
      child.stdin.write(`arm ${sequence}\n`);
      return promise;
    },
    disarm() {
      finishArming(false);
      if (!closed) child.stdin.write('disarm\n');
    },
    disconnect() {
      if (!closed) child.stdin.end();
    },
  };
}

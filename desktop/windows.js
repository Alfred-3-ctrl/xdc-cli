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
  let finishArming;
  let armingTimer;
  let closed = false;
  const lines = createInterface({ input: child.stdout });
  child.stdin.on('error', () => {});
  lines.on('line', (line) => {
    if (line === 'uu on' || line === 'uu off') onUu(line === 'uu on');
    if (line === 'armed') finishArming?.(true);
  });
  const ended = () => {
    if (closed) return;
    closed = true;
    finishArming?.(false);
    onExit();
  };
  child.on('error', ended);
  child.on('exit', ended);
  return {
    keepAtBottom(win) {
      const handle = win.getNativeWindowHandle();
      const value = handle.length === 8 ? handle.readBigInt64LE() : BigInt(handle.readUInt32LE());
      if (!closed) child.stdin.write(`bottom ${value}\n`);
    },
    arm() {
      if (closed) return Promise.resolve(false);
      return new Promise((resolve) => {
        finishArming = (result) => {
          clearTimeout(armingTimer);
          finishArming = undefined;
          resolve(result);
        };
        armingTimer = setTimeout(() => finishArming?.(false), 2000);
        child.stdin.write('arm\n');
      });
    },
    disconnect() {
      if (!closed) child.stdin.end();
    },
  };
}

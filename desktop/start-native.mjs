import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const desktop = fileURLToPath(new URL('./', import.meta.url));
const windows = process.platform === 'win32';
const executable = windows
  ? fileURLToPath(new URL('./native-bin/Xdc.NativeDesktop.exe', import.meta.url))
  : createRequire(import.meta.url)('electron');
const args = windows ? ['--desktop', desktop, ...process.argv.slice(2)] : [desktop, ...process.argv.slice(2)];
const child = spawn(executable, args, { cwd: desktop, stdio: 'inherit' });
child.once('error', (error) => {
  console.error(windows && error.code === 'ENOENT'
    ? '请先运行 npm --prefix desktop run build:native-desktop。'
    : '无法启动学习桌面：' + error.message);
  process.exitCode = 1;
});
child.once('exit', (code) => { process.exitCode = code ?? 1; });

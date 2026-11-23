import { app, safeStorage, shell } from 'electron';
import fs from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import readline from 'node:readline';
import { createDesktopAuth } from '../auth.js';
import { createDesktopData, DATA_METHODS } from '../data.js';
import { readPreferences } from '../preferences.js';

const AUTH_METHODS = new Set([
  'auth.status', 'auth.login', 'auth.loginSaved', 'auth.logout', 'auth.forget', 'auth.importCookie',
]);
const SERVICE_DATA_METHODS = new Set(DATA_METHODS);
const OFFICIAL = {
  learning: 'https://courses.zju.edu.cn/user/index',
  courses: 'https://courses.zju.edu.cn/user/index',
  classroom: 'https://classroom.zju.edu.cn/',
};

function publicError(error) {
  const code = /^[A-Z][A-Z0-9_]{1,60}$/.test(error?.code ?? '') ? error.code : 'DESKTOP_ERROR';
  const message = code === 'DESKTOP_ERROR'
    ? '操作未完成，请重试。'
    : String(error.message ?? '操作未完成').replace(/https?:\/\/[^\s]+/g, '平台地址').slice(0, 350);
  return { code, message };
}

function denied(message, code = 'USAGE') {
  throw Object.assign(new Error(message), { code });
}

function parseRequest(line) {
  if (line.length > 256 * 1024) return null;
  try {
    const request = JSON.parse(line);
    if (!request || !Number.isSafeInteger(request.id) || request.id < 1) return null;
    return request;
  } catch {
    return null;
  }
}

function launch(userData, channel) {
  const preferenceFile = path.join(userData, 'preferences.json');
  const defaultDownloadDir = path.join(app.getPath('downloads'), '学习桌面');
  const auth = createDesktopAuth({ safeStorage, userData });
  const pending = new Set();
  let accepting = true;
  let connected = true;
  let draining;

  function send(message) {
    if (connected) channel.write(JSON.stringify(message) + '\n');
  }

  const data = createDesktopData({
    auth,
    statePath: path.join(userData, 'course-state.json'),
    defaultDownloadDir,
    getDownloadDirectory() {
      const { downloadDirectory } = readPreferences(preferenceFile);
      return typeof downloadDirectory === 'string' && !downloadDirectory.includes('\0') && path.isAbsolute(downloadDirectory)
        ? downloadDirectory : defaultDownloadDir;
    },
    emit: (event) => send({ event }),
  });

  function idle() {
    return pending.size === 0 && data.canRelease() && auth.canRelease();
  }

  // Input is a private pipe from the native window. All commands still use the
  // same limited method surface and existing domain validation.
  async function invoke(method, payload) {
    if (AUTH_METHODS.has(method)) return auth.handle(method, payload);
    if (SERVICE_DATA_METHODS.has(method)) return data.handle(method, payload);
    switch (method) {
      case 'desktop.openOfficial': {
        const target = Object.hasOwn(OFFICIAL, payload.service) && OFFICIAL[payload.service];
        if (!target) return denied('未知的教学平台。');
        await shell.openExternal(target);
        return { ok: true };
      }
      case 'desktop.showFile': {
        const file = data.fileForJob(payload.id);
        if (!file || !fs.existsSync(file)) return denied('下载文件已移动或删除。', 'FILE_NOT_FOUND');
        shell.showItemInFolder(file);
        return { ok: true };
      }
      case 'service.hasActiveJob':
        return { active: data.hasActiveJob() };
      case 'service.quietPause': {
        const hadActiveJob = data.hasActiveJob();
        await data.pause();
        return { hadActiveJob };
      }
      case 'service.resume':
        data.resume();
        return { ok: true };
      default:
        return denied('未知的操作。');
    }
  }

  function drain() {
    accepting = false;
    if (!draining) {
      // Hold pending jobs first. Requests that are already fetching a course may
      // still finish successfully, but any jobs they add cannot start.
      draining = Promise.allSettled([...pending, data.pause()]);
    }
    return draining;
  }

  function disconnect() {
    if (!connected) return;
    connected = false;
    void drain().finally(() => {
      channel.destroy();
      app.quit();
    });
  }

  const input = readline.createInterface({ input: channel, crlfDelay: Infinity, terminal: false });
  input.on('line', (line) => {
    const request = parseRequest(line);
    if (!request) return;
    const { id, method, payload = {} } = request;
    if (typeof method !== 'string' || !payload || typeof payload !== 'object' || Array.isArray(payload)) {
      send({ id, error: { code: 'USAGE', message: '请求参数无效。' } });
      return;
    }
    if (!accepting) {
      send({ id, error: { code: 'SERVICE_STOPPING', message: '后台服务正在退出，请重试。' } });
      return;
    }
    if (method === 'service.idle') {
      send({ id, result: { idle: idle() } });
      return;
    }
    if (method === 'service.shutdown') {
      if (payload.ifIdle === true && !idle()) {
        send({ id, result: { ok: false } });
        return;
      }
      void drain().then(() => send({ id, result: { ok: true } }));
      return;
    }
    const operation = invoke(method, payload);
    pending.add(operation);
    void operation.then(
      (result) => send({ id, result: result ?? null }),
      (error) => send({ id, error: publicError(error) }),
    ).finally(() => pending.delete(operation));
  });
  input.once('close', disconnect);
  channel.once('close', disconnect);
  channel.on('error', disconnect);
  send({ ready: true });
}

function argument(name) {
  const index = process.argv.indexOf(name, 2);
  return index < 0 ? undefined : process.argv[index + 1];
}

function exitWithStartupError(message, code = 1) {
  try { fs.writeSync(2, message + '\n'); }
  catch { /* The native host may already have closed the diagnostic pipe. */ }
  app.exit(code);
}

const profile = argument('--profile');
const pipe = argument('--pipe');
if (typeof profile !== 'string' || !path.isAbsolute(profile) || profile.includes('\0') ||
    !/^xdc-native-[a-f0-9]{32}$/i.test(pipe ?? '')) {
  exitWithStartupError('A valid absolute --profile and local --pipe are required.', 2);
} else {
  try {
    fs.mkdirSync(profile, { recursive: true });
    app.setName('学习桌面');
    app.setPath('userData', profile);
    app.disableHardwareAcceleration();
    void app.whenReady().then(() => {
      // The native host creates a unique, current-user-only named pipe before
      // starting this GUI-subsystem executable. No input uses console handles.
      const channel = net.createConnection('\\\\.\\pipe\\' + pipe);
      function fail() {
        channel.destroy();
        exitWithStartupError('Unable to connect to the native desktop host.');
      }
      channel.once('error', fail);
      channel.once('connect', () => {
        channel.off('error', fail);
        try { launch(profile, channel); } catch { fail(); }
      });
    }).catch(() => {
      exitWithStartupError('Unable to initialize the desktop service.');
    });
  } catch {
    exitWithStartupError('Unable to initialize the desktop profile.');
  }
}

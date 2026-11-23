import { app, BrowserWindow, dialog, globalShortcut, ipcMain, net, Notification, protocol, safeStorage, screen, shell } from 'electron';
import path from 'node:path';
import fs from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createDesktopAuth } from './auth.js';
import { createDesktopData, DATA_METHODS } from './data.js';
import { DEFAULT_LOOK, normalizeBounds, normalizeLook, normalizeShortcut, readPreferences, savePreferences } from './preferences.js';
import { startWindowsMonitor } from './windows.js';
import { roundedWindowShape } from './window-shape.js';
import { createWindowsLaunchEntries } from './startup.js';

const appDirectory = path.dirname(fileURLToPath(import.meta.url));
const rendererDirectory = path.join(appDirectory, 'renderer');
const rendererUrl = 'xdc://app/index.html';
const AUTH_METHODS = new Set(['auth.status', 'auth.login', 'auth.loginSaved', 'auth.logout', 'auth.forget', 'auth.importCookie', 'auth.connectClassroom']);
const OFFICIAL = {
  learning: 'https://courses.zju.edu.cn/user/index',
  courses: 'https://courses.zju.edu.cn/user/index',
  classroom: 'https://classroom.zju.edu.cn/',
};
const defaultShortcut = 'Ctrl+Shift+L';
const windows = process.platform === 'win32';
const appId = 'Alfred.xdc.Desktop';
app.setName('学习桌面');
app.setPath('userData', path.join(app.getPath('appData'), 'xdc-desktop'));
if (windows) app.setAppUserModelId(appId);
protocol.registerSchemesAsPrivileged([{ scheme: 'xdc', privileges: { standard: true, secure: true, supportFetchAPI: true } }]);

function handleStatic(request) {
  let location;
  try {
    const url = new URL(request.url);
    if (url.host !== 'app' || url.username || url.password) return new Response(null, { status: 404 });
    location = path.resolve(rendererDirectory, '.' + decodeURIComponent(url.pathname));
  } catch {
    return new Response(null, { status: 404 });
  }
  if (!location.startsWith(rendererDirectory + path.sep) || !/\.(?:html|css|js|svg|png|woff2)$/.test(location)) {
    return new Response(null, { status: 404 });
  }
  return net.fetch(pathToFileURL(location).href);
}

function uniquePaths(first, second) {
  if (typeof first !== 'string' || typeof second !== 'string') return false;
  const canonical = value => windows ? path.resolve(value).toLowerCase() : path.resolve(value);
  return canonical(first) === canonical(second);
}

function publicError(error) {
  const code = /^[A-Z][A-Z0-9_]{1,60}$/.test(error?.code ?? '') ? error.code : 'DESKTOP_ERROR';
  const message = code === 'DESKTOP_ERROR'
    ? '操作未完成，请重试。'
    : String(error.message ?? '操作未完成').replace(/https?:\/\/[^\s]+/g, '平台地址').slice(0, 350);
  return { ok: false, error: { code, message } };
}

function denied(message, code = 'USAGE') {
  throw Object.assign(new Error(message), { code });
}

async function launch() {
  protocol.handle('xdc', handleStatic);
  const userData = app.getPath('userData');
  const launchEntries = windows ? createWindowsLaunchEntries({ app, shell, appId, appDirectory }) : null;
  const preferenceFile = path.join(userData, 'preferences.json');
  const viewFile = path.join(userData, 'last-view.json');
  const stored = readPreferences(preferenceFile);
  const previousView = readPreferences(viewFile);
  let view = Date.now() - previousView.savedAt < 300000 ? normalizeView(previousView.view) : undefined;
  const primary = screen.getPrimaryDisplay().workArea;
  const initial = { x: primary.x + primary.width - 484, y: primary.y + 28, width: 460, height: 700 };
  const bound = (geometry, fallback = initial) => normalizeBounds(geometry, screen.getAllDisplays(), fallback);
  const defaultDownloadDir = path.join(app.getPath('downloads'), '学习桌面');
  const isAbsoluteDirectory = (input) => typeof input === 'string' && !input.includes('\0') && path.isAbsolute(input);
  const prefs = {
    look: normalizeLook(stored.look, DEFAULT_LOOK),
    geometry: bound(stored.geometry),
    shortcut: normalizeShortcut(stored.shortcut) ?? defaultShortcut,
    locked: stored.locked !== false,
    startup: stored.startup === true,
    downloadDirectory: isAbsoluteDirectory(stored.downloadDirectory) ? stored.downloadDirectory : defaultDownloadDir,
  };
  let win;
  let monitor;
  let geometryTimer;
  let uuActive = false;
  let pausedAt = 0;
  let revision = 0;
  let quitting = false;
  let shortcutRegistered;
  let lastWindowBounds = prefs.geometry;
  let cornerRadius = prefs.look.radius;
  let shapeKey;
  const pickedDirectories = new Set();
  const emit = (event) => {
    if (win && !win.isDestroyed() && !win.webContents.isDestroyed()) win.webContents.send('xdc:event', event);
  };
  const auth = createDesktopAuth({ safeStorage, userData });
  const data = createDesktopData({
    statePath: path.join(userData, 'course-state.json'), defaultDownloadDir,
    getDownloadDirectory: () => prefs.downloadDirectory, auth, emit,
  });
  const dataMethods = new Set(DATA_METHODS);

  function normalizeView(candidate) {
    const { name, courseId, assignmentId, tab, scroll } = candidate ?? {};
    if (!['overview', 'courses', 'downloads', 'course', 'assignment'].includes(name)) return undefined;
    const result = { name, scroll: Number.isFinite(scroll) ? Math.max(0, Math.min(100000, Math.round(scroll))) : 0 };
    if (name === 'course' || name === 'assignment') {
      if (typeof courseId !== 'string' || !/^[1-9]\d{0,19}$/.test(courseId)) return undefined;
      result.courseId = courseId;
      if (name === 'course') result.tab = ['summary', 'homework', 'materials', 'lecture'].includes(tab) ? tab : 'summary';
    }
    if (name === 'assignment') {
      if (typeof assignmentId !== 'string' || !/^[1-9]\d{0,19}$/.test(assignmentId)) return undefined;
      result.assignmentId = assignmentId;
    }
    return result;
  }
  function storeView() {
    try { savePreferences(viewFile, { savedAt: Date.now(), view }); } catch {}
  }
  const desktopState = () => ({ ...prefs, geometry: win?.getContentBounds() ?? lastWindowBounds, resume: view });
  function applyWindowShape() {
    if (!windows || !win || win.isDestroyed()) return;
    const outer = win.getBounds();
    const content = win.getContentBounds();
    const { scaleFactor } = screen.getDisplayMatching(outer);
    const key = [content.x - outer.x, content.y - outer.y, content.width, content.height, cornerRadius, scaleFactor].join(':');
    if (shapeKey === key) return;
    try {
      win.setShape(roundedWindowShape(outer, content, cornerRadius));
      shapeKey = key;
    } catch {
      // Avoid drawing a system backdrop outside the web view's rounded mask.
      shapeKey = undefined;
      try { win.setBackgroundMaterial('none'); } catch {}
      try { win.setShape([]); } catch {}
    }
  }
  function applyLock(locked) {
    if (!win) return;
    if (win.isMovable() === locked) win.setMovable(!locked);
    if (win.isResizable() === locked) win.setResizable(!locked);
    applyWindowShape();
  }
  function onShortcut() {
    if (!win?.isVisible()) return;
    prefs.locked = !prefs.locked;
    prefs.geometry = win.getContentBounds();
    applyLock(prefs.locked);
    try { savePreferences(preferenceFile, prefs); } catch {}
    emit({ type: 'desktop.locked', locked: prefs.locked, geometry: prefs.geometry });
  }
  function registerShortcut(shortcut) {
    if (shortcut === shortcutRegistered) return true;
    if (!globalShortcut.register(shortcut, onShortcut)) return false;
    if (shortcutRegistered) globalShortcut.unregister(shortcutRegistered);
    shortcutRegistered = shortcut;
    return true;
  }
  function showWindow({ active = false } = {}) {
    if (quitting || uuActive) return;
    if (win && !win.isDestroyed()) {
      if (active) { win.show(); win.focus(); }
      else win.showInactive();
      return;
    }
    const bounds = bound(lastWindowBounds);
    const current = new BrowserWindow({
      ...bounds, frame: false, transparent: true, hasShadow: false, show: false, skipTaskbar: true, useContentSize: true,
      maximizable: false, fullscreenable: false, minimizable: false, alwaysOnTop: false,
      minWidth: Math.min(320, bounds.width), minHeight: Math.min(520, bounds.height),
      backgroundColor: '#00000000',
      title: '学习桌面',
      webPreferences: {
        preload: path.join(appDirectory, 'preload.cjs'), contextIsolation: true, sandbox: true,
        nodeIntegration: false,
      },
    });
    win = current;
    cornerRadius = prefs.look.radius;
    shapeKey = undefined;
    current.removeMenu();
    if (windows && Number(process.getSystemVersion().split('.').at(-1)) >= 22621) {
      try { current.setBackgroundMaterial('acrylic'); } catch {}
    }
    applyLock(prefs.locked);
    current.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
    current.webContents.on('will-navigate', (event, target) => {
      if (target !== rendererUrl) event.preventDefault();
    });
    current.once('ready-to-show', () => {
      if (current.isDestroyed() || uuActive) return;
      applyWindowShape();
      if (active) current.show();
      else current.showInactive();
      monitor?.keepAtBottom(current);
    });
    current.on('blur', () => monitor?.keepAtBottom(current));
    const updateBounds = () => {
      if (current !== win || current.isDestroyed()) return;
      applyWindowShape();
      clearTimeout(geometryTimer);
      geometryTimer = setTimeout(() => {
        if (current.isDestroyed()) return;
        lastWindowBounds = current.getContentBounds();
        emit({ type: 'desktop.geometry', geometry: lastWindowBounds });
      }, 60);
    };
    current.on('move', updateBounds);
    current.on('resize', updateBounds);
    current.on('closed', () => { if (win === current) win = undefined; });
    void current.loadURL(rendererUrl).catch(() => {
      if (!current.isDestroyed()) dialog.showErrorBox('学习桌面', '窗口加载失败，请重新打开组件。');
    });
  }

  function trustedDirectory(directory) {
    return isAbsoluteDirectory(directory) && (uniquePaths(directory, prefs.downloadDirectory)
      || [...pickedDirectories].some(picked => uniquePaths(picked, directory)));
  }
  async function desktop(method, payload) {
    switch (method) {
      case 'desktop.get':
        return desktopState();
      case 'desktop.rememberView':
        view = normalizeView(payload) ?? view;
        return { ok: true };
      case 'desktop.preview': {
        if (Number.isFinite(payload.radius)) {
          cornerRadius = normalizeLook({ radius: payload.radius }, prefs.look).radius;
        }
        if (typeof payload.locked === 'boolean') applyLock(payload.locked);
        if (payload.geometry && win) {
          const before = win.getContentBounds();
          const after = bound(payload.geometry, before);
          if (Object.keys(after).some(key => after[key] !== before[key])) win.setContentBounds(after);
        }
        applyWindowShape();
        return { geometry: win?.getContentBounds() ?? lastWindowBounds, locked: payload.locked ?? prefs.locked };
      }
      case 'desktop.save': {
        if (payload.downloadDirectory !== undefined && payload.downloadDirectory !== null && !trustedDirectory(payload.downloadDirectory)) {
          return denied('请使用文件夹选择器设置下载位置。');
        }
        const shortcut = payload.shortcut === undefined ? prefs.shortcut : normalizeShortcut(payload.shortcut);
        if (!shortcut || (shortcut !== prefs.shortcut && !registerShortcut(shortcut))) {
          return denied('该快捷键无效或已被其他程序占用。', 'SHORTCUT_UNAVAILABLE');
        }
        const next = {
          look: normalizeLook(payload.look, prefs.look),
          geometry: bound(payload.geometry, win?.getContentBounds() ?? lastWindowBounds),
          shortcut,
          locked: typeof payload.locked === 'boolean' ? payload.locked : prefs.locked,
          startup: typeof payload.startup === 'boolean' ? payload.startup : prefs.startup,
          downloadDirectory: payload.downloadDirectory ?? prefs.downloadDirectory,
        };
        try {
          if (next.startup !== prefs.startup) launchEntries?.setStartup(next.startup);
          savePreferences(preferenceFile, next);
        } catch (error) {
          registerShortcut(prefs.shortcut);
          if (next.startup !== prefs.startup) {
            try { launchEntries?.setStartup(prefs.startup); } catch {}
          }
          throw error;
        }
        Object.assign(prefs, next);
        cornerRadius = next.look.radius;
        if (win) win.setContentBounds(next.geometry);
        lastWindowBounds = next.geometry;
        applyLock(next.locked);
        return desktopState();
      }
      case 'desktop.chooseFolder': {
        const selection = await dialog.showOpenDialog(win ?? undefined, {
          title: '选择课件保存位置', properties: ['openDirectory', 'createDirectory'],
          defaultPath: prefs.downloadDirectory,
        });
        if (selection.canceled || !selection.filePaths[0]) return null;
        const chosen = selection.filePaths[0];
        pickedDirectories.add(chosen);
        if (pickedDirectories.size > 32) pickedDirectories.delete(pickedDirectories.values().next().value);
        return chosen;
      }
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
      case 'desktop.close':
        setImmediate(() => void finishAndQuit());
        return { ok: true };
      default:
        return denied('未知的操作。');
    }
  }

  async function finishAndQuit() {
    if (quitting) return;
    quitting = true;
    storeView();
    win?.hide();
    try { await data.pause(); } finally { app.quit(); }
  }

  ipcMain.handle('xdc:invoke', async (event, method, payload) => {
    if (event.sender !== win?.webContents || event.senderFrame?.url !== rendererUrl ||
        event.sender.mainFrame !== event.senderFrame || typeof method !== 'string') {
      return { ok: false, error: { code: 'FORBIDDEN', message: '无法从此页面执行该操作。' } };
    }
    const args = payload && typeof payload === 'object' && !Array.isArray(payload) ? payload : {};
    try {
      if (method.startsWith('desktop.')) return await desktop(method, args);
      if (AUTH_METHODS.has(method)) return await auth.handle(method, args);
      if (dataMethods.has(method)) {
        if (method === 'download.enqueue' && args.directory != null && !trustedDirectory(args.directory)) {
          return denied('请使用文件夹选择器设置保存位置。');
        }
        return await data.handle(method, args);
      }
      return denied('未知的操作。');
    } catch (error) {
      return publicError(error);
    }
  });

  async function onUu(active) {
    if (quitting) return;
    uuActive = active;
    const step = ++revision;
    if (!active) {
      if (pausedAt && Date.now() - pausedAt > 300000) view = undefined;
      pausedAt = 0;
      data.resume();
      showWindow();
      return;
    }
    pausedAt = Date.now();
    if (data.hasActiveJob() && Notification.isSupported()) {
      new Notification({ title: '学习桌面', body: '当前文件仍在处理，剩余队列将在 UU 退出后继续。' }).show();
    }
    if (win) {
      lastWindowBounds = win.getContentBounds();
      storeView();
      win.destroy();
    }
    try {
      await data.pause();
      if (!uuActive || step !== revision || quitting) return;
      if (await monitor?.arm()) {
        quitting = true;
        app.quit();
      }
    } catch {}
  }

  app.on('second-instance', () => showWindow({ active: true }));
  app.on('window-all-closed', () => {});
  app.on('will-quit', () => {
    quitting = true;
    globalShortcut.unregisterAll();
    monitor?.disconnect();
  });
  screen.on('display-removed', () => {
    lastWindowBounds = bound(lastWindowBounds);
    win?.setContentBounds(lastWindowBounds);
  });
  screen.on('display-metrics-changed', applyWindowShape);
  registerShortcut(prefs.shortcut);
  if (windows) {
    try {
      launchEntries.installStartMenu();
    } catch {}
    if (prefs.startup) {
      try { launchEntries.setStartup(true, { respectWindowsApproval: true }); } catch {}
    }
    try {
      monitor = await startWindowsMonitor({
        userData, appDirectory,
        onUu: active => void onUu(active),
        onExit: () => { if (!quitting) void onUu(false); },
      });
      // Fallback for machines where the native monitor cannot report state.
      setTimeout(() => { if (!revision) void onUu(false); }, 3000).unref();
      return;
    } catch {}
  }
  showWindow();
}

if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.whenReady().then(launch).catch(() => {
    dialog.showErrorBox('学习桌面', '应用启动失败，请从项目目录重新运行。');
    app.exit(1);
  });
}

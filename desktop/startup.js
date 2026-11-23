import fs from 'node:fs';
import path from 'node:path';

export function createWindowsLaunchEntries({ app, shell, appId, appDirectory, executable = process.execPath }) {
  const programs = path.join(app.getPath('appData'), 'Microsoft', 'Windows', 'Start Menu', 'Programs');
  const startMenu = path.join(programs, '学习桌面.lnk');
  const startup = path.join(programs, 'Startup', '学习桌面.lnk');
  const details = { target: executable, args: `"${appDirectory}"`, cwd: appDirectory, appUserModelId: appId };
  const samePath = (a, b) => a?.toLowerCase() === b.toLowerCase();

  function setShortcut(file, enabled) {
    const existing = fs.existsSync(file) ? shell.readShortcutLink(file) : null;
    if (existing && existing.appUserModelId !== appId &&
        !(samePath(existing.target, details.target) && existing.args === details.args)) {
      throw new Error('同名的启动快捷方式已被其他应用使用。');
    }
    if (!enabled) {
      if (existing) fs.unlinkSync(file);
      return;
    }
    if (existing && samePath(existing.target, details.target) && existing.args === details.args &&
        samePath(existing.cwd, details.cwd) && existing.appUserModelId === appId) return;

    fs.mkdirSync(path.dirname(file), { recursive: true });
    if (!shell.writeShortcutLink(file, details)) throw new Error('无法写入启动快捷方式。');
  }

  function setStartup(enabled, { respectWindowsApproval = false } = {}) {
    const launch = { path: executable, args: [appDirectory] };
    const settings = app.getLoginItemSettings(launch);
    const previous = settings.launchItems.find(item => item.name === appId && item.scope === 'user');
    if (respectWindowsApproval && enabled && previous?.enabled === false && !fs.existsSync(startup)) return;

    // Windows resolves the shell link's executable, arguments and working directory separately.
    setShortcut(startup, enabled);
    if (settings.openAtLogin || previous) {
      app.setLoginItemSettings({ ...launch, name: appId, openAtLogin: false });
    }
  }

  return { installStartMenu: () => setShortcut(startMenu, true), setStartup };
}

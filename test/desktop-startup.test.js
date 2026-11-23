import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createWindowsLaunchEntries } from '../desktop/startup.js';

test('desktop migrates an enabled Windows login item to a shortcut with a working directory', (t) => {
  const appData = fs.mkdtempSync(path.join(os.tmpdir(), 'xdc-startup-'));
  t.after(() => fs.rmSync(appData, { recursive: true, force: true }));
  const appId = 'Alfred.xdc.Desktop';
  const appDirectory = path.join(appData, 'Project with spaces', 'desktop');
  const executable = path.join(appDirectory, 'node_modules', 'electron.exe');
  const programs = path.join(appData, 'Microsoft', 'Windows', 'Start Menu', 'Programs');
  const startup = path.join(programs, 'Startup', '学习桌面.lnk');
  const startMenu = path.join(programs, '学习桌面.lnk');
  const readShortcutLink = file => JSON.parse(fs.readFileSync(file, 'utf8'));
  const writes = [];
  let previous = { name: appId, scope: 'user', enabled: false };
  const app = {
    getPath: () => appData,
    getLoginItemSettings: () => ({ openAtLogin: previous !== null, launchItems: previous ? [previous] : [] }),
    setLoginItemSettings(options) {
      assert.equal(options.openAtLogin, false);
      assert.equal(options.name, appId);
      assert.ok(fs.existsSync(startup));
      previous = null;
    },
  };
  const shell = {
    readShortcutLink,
    writeShortcutLink(file, details) {
      writes.push(file);
      fs.writeFileSync(file, JSON.stringify(details));
      return true;
    },
  };
  const entries = createWindowsLaunchEntries({ app, shell, appId, appDirectory, executable });
  entries.installStartMenu();
  entries.setStartup(true, { respectWindowsApproval: true });
  assert.equal(fs.existsSync(startup), false);

  previous.enabled = true;
  entries.setStartup(true, { respectWindowsApproval: true });
  assert.deepEqual(readShortcutLink(startup), {
    target: executable, args: `"${appDirectory}"`, cwd: appDirectory, appUserModelId: appId,
  });
  assert.equal(previous, null);
  entries.setStartup(true, { respectWindowsApproval: true });
  assert.deepEqual(writes, [startMenu, startup]);

  entries.setStartup(false);
  assert.equal(fs.existsSync(startup), false);
  assert.ok(fs.existsSync(startMenu));
});

test('Electron leaves startup with an installed native widget', (t) => {
  const appData = fs.mkdtempSync(path.join(os.tmpdir(), 'xdc-native-startup-'));
  t.after(() => fs.rmSync(appData, { recursive: true, force: true }));
  const appId = 'Alfred.xdc.Desktop';
  const appDirectory = path.join(appData, 'Project with spaces', 'desktop');
  const executable = path.join(appDirectory, 'node_modules', 'electron.exe');
  const native = path.join(appDirectory, 'native-bin', 'Xdc.NativeDesktop.exe');
  const programs = path.join(appData, 'Microsoft', 'Windows', 'Start Menu', 'Programs');
  const startup = path.join(programs, 'Startup', '学习桌面.lnk');
  const startMenu = path.join(programs, '学习桌面.lnk');
  const toNative = { target: native, args: '--desktop "' + appDirectory + '"', cwd: appDirectory, appUserModelId: appId };
  fs.mkdirSync(path.dirname(native), { recursive: true });
  fs.writeFileSync(native, '');
  fs.mkdirSync(path.dirname(startup), { recursive: true });
  fs.writeFileSync(startMenu, JSON.stringify(toNative));
  const written = [];
  const app = {
    getPath: () => appData,
    getLoginItemSettings: () => ({ openAtLogin: false, launchItems: [] }),
  };
  const shell = {
    readShortcutLink: file => JSON.parse(fs.readFileSync(file, 'utf8')),
    writeShortcutLink(file, details) {
      written.push(file);
      fs.writeFileSync(file, JSON.stringify(details));
      return true;
    },
  };
  const entries = createWindowsLaunchEntries({ app, shell, appId, appDirectory, executable });
  entries.installStartMenu();
  entries.setStartup(true, { respectWindowsApproval: true });
  assert.deepEqual(shell.readShortcutLink(startup), toNative);
  entries.setStartup(true, { respectWindowsApproval: true });
  assert.deepEqual(written, [startup]);
  entries.setStartup(false);
  assert.equal(fs.existsSync(startup), false);
  assert.deepEqual(shell.readShortcutLink(startMenu), toNative);
});

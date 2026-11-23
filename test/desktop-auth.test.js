import assert from 'node:assert/strict';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createDesktopAuth } from '../desktop/auth.js';
import { jarFromCookieString, saveSession } from '../src/session.js';

const COURSES = 'https://courses.zju.edu.cn';
const CLASSROOM = 'https://classroom.zju.edu.cn';
const CLASSROOM_COOKIE = `PHPSESSID=${encodeURIComponent('a:2:{i:0;s:6:"_token";i:1;s:4:"demo";}')}`;

function sandbox(t) {
  const folder = mkdtempSync(path.join(os.tmpdir(), 'xdc-desktop-auth-'));
  const previous = process.env.XD_CONFIG_DIR;
  process.env.XD_CONFIG_DIR = folder;
  t.after(() => {
    if (previous === undefined) delete process.env.XD_CONFIG_DIR;
    else process.env.XD_CONFIG_DIR = previous;
    rmSync(folder, { recursive: true, force: true });
  });
  const credentialPath = path.join(folder, 'credential.dpapi');
  const safeStorage = {
    isEncryptionAvailable: () => true,
    encryptString: (cleartext) => Buffer.from(Buffer.from(cleartext).toString('base64')),
    decryptString: (encrypted) => Buffer.from(encrypted.toString(), 'base64').toString(),
  };
  const auth = createDesktopAuth({ platform: 'win32', credentialPath, safeStorage });
  return { auth, credentialPath };
}

function storeCourses() {
  saveSession({
    service: 'courses',
    jar: jarFromCookieString('session=course-test-secret'),
    baseUrl: COURSES,
    username: 'test-account',
    meta: { method: 'cas' },
  });
}

function storeClassroom() {
  saveSession({
    service: 'classroom',
    jar: jarFromCookieString(CLASSROOM_COOKIE, 'classroom.zju.edu.cn'),
    baseUrl: CLASSROOM,
    username: 'test-account',
    meta: { method: 'cas' },
  });
}

test('desktop auth can verify shared CLI sessions and returns no credentials or session contents', async (t) => {
  const { auth } = sandbox(t);
  storeCourses();
  storeClassroom();
  t.mock.method(globalThis, 'fetch', async (input) => {
    const url = new URL(input);
    if (url.origin === COURSES && url.pathname === '/api/my-courses') return Response.json({ courses: [] });
    if (url.origin === CLASSROOM && url.pathname === '/userapi/v1/infosimple') return Response.json({ params: { id: 123 } });
    assert.fail('unexpected request');
  });
  assert.deepEqual(await auth.handle('auth.status'), {
    courses: { authenticated: true, state: 'connected' },
    classroom: { authenticated: true, state: 'connected' },
    canRemember: true,
    remembered: false,
  });
  assert.equal(auth.scope('courses'), auth.scope('classroom'));
  assert.doesNotMatch(auth.scope('courses'), /test-account/);
});

test('desktop only remembers a verified login with opt in and preserves it across classroom outages', async (t) => {
  const { auth, credentialPath } = sandbox(t);
  t.mock.method(globalThis, 'fetch', async (input) => {
    const url = new URL(input);
    if (url.origin === COURSES && url.pathname === '/user/index') {
      return new Response('courses', { headers: { 'set-cookie': 'session=new-test-session; Path=/; Secure' } });
    }
    if (url.origin === COURSES && url.pathname === '/api/my-courses') return Response.json({ courses: [] });
    if (url.hostname === 'tgmedia.cmc.zju.edu.cn') return new Response('unavailable', { status: 503 });
    assert.fail('unexpected request');
  });
  const credentials = { username: 'test-account', password: 'test-password' };
  const login = await auth.handle('auth.login', { ...credentials, remember: true });
  assert.equal(login.courses.authenticated, true);
  assert.equal(login.classroom.code, 'SERVICE_UNAVAILABLE');
  assert.equal(login.remembered, true);
  assert.doesNotMatch(readFileSync(credentialPath).toString(), /test-account|test-password/);
  assert.doesNotMatch(JSON.stringify(login), /test-password|new-test-session/);
  const saved = await auth.handle('auth.loginSaved');
  assert.equal(saved.courses.authenticated, true);
  const withoutRemember = await auth.handle('auth.login', { ...credentials, remember: false });
  assert.equal(withoutRemember.remembered, false);
  assert.equal(existsSync(credentialPath), false);
  const signedOut = await auth.handle('auth.logout');
  assert.equal(signedOut.courses.state, 'signed-out');
  assert.equal(auth.sessionKey('courses'), null);
});

test('desktop course reads retry after restoring one expired session through CAS cookies', async (t) => {
  const { auth } = sandbox(t);
  storeCourses();
  let renewals = 0;
  t.mock.method(globalThis, 'fetch', async (input, options) => {
    const url = new URL(input);
    if (url.origin === COURSES && url.pathname === '/user/index') {
      renewals++;
      return new Response('courses', { headers: { 'set-cookie': 'session=renewed; Path=/; Secure' } });
    }
    if (url.origin === COURSES && url.pathname === '/api/my-courses') {
      return new Headers(options.headers).get('cookie') === 'session=renewed'
        ? Response.json({ courses: [] }) : new Response(null, { status: 401 });
    }
    assert.fail('unexpected request');
  });
  const result = await auth.getApi().getJson('/api/my-courses');
  assert.deepEqual(result, { courses: [] });
  assert.equal(renewals, 1);
});

test('the native desktop keeps the in-memory automatic-login retry guard after a challenge', async (t) => {
  const { auth } = sandbox(t);
  const cas = 'https://zjuam.zju.edu.cn';
  let expired = false;
  let captchaChecks = 0;
  t.mock.method(globalThis, 'fetch', async (input) => {
    const url = new URL(input);
    if (url.origin === COURSES && url.pathname === '/user/index') {
      return expired ? Response.redirect(cas + '/cas/login') :
        new Response('courses', { headers: { 'set-cookie': 'session=fixture; Path=/; Secure' } });
    }
    if (url.origin === COURSES && url.pathname === '/api/my-courses') {
      return expired ? new Response(null, { status: 401 }) : Response.json({ courses: [] });
    }
    if (url.origin === cas && url.pathname === '/cas/login') {
      return new Response('<form id="fm1" action="/cas/login"><input name="execution" type="hidden" value="test"></form>');
    }
    if (url.origin === cas && url.pathname.endsWith('/getKaptchaStatus')) {
      captchaChecks++;
      return new Response('true');
    }
    if (url.hostname === 'tgmedia.cmc.zju.edu.cn') return new Response('unavailable', { status: 503 });
    assert.fail('unexpected request');
  });

  await auth.login({ username: 'test-account', password: 'test-password', remember: true });
  assert.equal(auth.canRelease(), true);
  expired = true;
  await assert.rejects(() => auth.getApi().getJson('/api/my-courses'), { code: 'CAPTCHA' });
  assert.equal(auth.canRelease(), false);
  await assert.rejects(() => auth.getApi().getJson('/api/my-courses'), { code: 'AUTH_REQUIRED' });
  assert.equal(captchaChecks, 1);
});

test('a direct desktop classroom import uses the validated session without guessing the account', async (t) => {
  const { auth } = sandbox(t);
  t.mock.method(globalThis, 'fetch', async (input) => {
    const url = new URL(input);
    assert.equal(url.origin, CLASSROOM);
    assert.equal(url.pathname, '/userapi/v1/infosimple');
    return Response.json({ params: { id: 123 } });
  });
  const state = await auth.handle('auth.importCookie', { service: 'classroom', cookie: CLASSROOM_COOKIE });
  assert.equal(state.classroom.authenticated, true);
  assert.equal(state.courses.authenticated, false);
  assert.equal(auth.scope('classroom'), null);
  assert.match(auth.sessionKey('classroom'), /^[a-f0-9]{64}$/);
});

import { createHash, randomUUID } from 'node:crypto';
import { mkdirSync, readFileSync, renameSync, statSync, unlinkSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { Api, ApiError, DEFAULT_BASE } from '../src/api.js';
import { ENTRY, passwordLogin } from '../src/cas.js';
import { CLASSROOM_ORIGIN, ClassroomClient } from '../src/classroom.js';
import { connectClassroom } from '../src/classroom-auth.js';
import { CookieJar, request } from '../src/http.js';
import { jarFromCookieString, loadSession, saveSession, sessionPath } from '../src/session.js';

const ORIGINS = { courses: DEFAULT_BASE, classroom: CLASSROOM_ORIGIN };
const SSO_ORIGINS = new Set(['https://zjuam.zju.edu.cn', 'https://identity.zju.edu.cn']);
const MAX_SECRET_BYTES = 32 * 1024;
const CONNECTED = { authenticated: true, state: 'connected' };
const SIGNED_OUT = { authenticated: false, state: 'signed-out' };
const INTERACTIVE_AUTH_CODES = new Set(['BAD_CREDENTIALS', 'CAPTCHA', 'MFA_REQUIRED', 'LOGIN_REJECTED']);
const ERROR_TEXT = {
  AUTH_REQUIRED: '登录会话已失效，请重新登录。',
  BAD_CREDENTIALS: '统一身份认证未接受账号或密码。',
  CAPTCHA: '统一身份认证要求验证码，请在官网完成验证。',
  MFA_REQUIRED: '统一身份认证要求进一步验证，请在官网继续。',
  LOGIN_REJECTED: '统一身份认证未完成，请到官网检查账号或完成验证。',
  NON_ASCII_PASSWORD: '请通过官网登录，再导入该平台的会话。',
  NO_CAS: '未找到统一身份认证登录页，请尝试官网登录。',
  CAS_FORM: '统一身份认证登录页已变化，请尝试官网登录。',
  CAS_REDIRECT: '统一身份认证跳转到了未识别的地址。',
  CLASSROOM_SSO_REDIRECT: '智云课堂认证跳转到了未识别的地址。',
  NETWORK: '无法连接认证或课程服务，请检查校网连接。',
  FORBIDDEN: '当前账号无权访问此平台。',
  SERVICE_UNAVAILABLE: '平台暂时不可用，请稍后重试。',
  SERVER_ERROR: '平台暂时不可用，请稍后重试。',
  BAD_RESPONSE: '平台返回了无法识别的登录状态。',
  CONFIG_ERROR: '无法更新本地登录信息。',
  CREDENTIAL_ERROR: '无法读取或更新保存在本机的凭据。',
  ACCOUNT_CHANGED: '当前会话已切换账号，请重新打开此页面。',
  COOKIE_INVALID: '会话内容格式不正确。',
};

function error(code, message = ERROR_TEXT[code] ?? '登录暂未完成，请重试。') {
  return new ApiError(message, { code, status: code === 'AUTH_REQUIRED' ? 401 : 0 });
}

function safeError(reason) {
  const code = Object.hasOwn(ERROR_TEXT, reason?.code) ? reason.code
    : reason instanceof TypeError || reason?.name === 'TimeoutError' ? 'NETWORK' : 'AUTH_FAILED';
  return error(code);
}

function asState(reason) {
  const { code, message } = safeError(reason);
  return { authenticated: false, state: code === 'AUTH_REQUIRED' ? 'signed-out' : 'error', code, message };
}

function removeFile(file) {
  try {
    unlinkSync(file);
  } catch (reason) {
    if (reason.code !== 'ENOENT') throw reason;
  }
}

function protectCredentialField(value) {
  return typeof value === 'string' && value.length > 0 && Buffer.byteLength(value) <= MAX_SECRET_BYTES;
}

function validateCourseApiUrl(url) {
  if (SSO_ORIGINS.has(url.origin)) throw error('AUTH_REQUIRED');
  if (url.origin !== DEFAULT_BASE || url.username || url.password) throw error('BAD_RESPONSE');
}

class DesktopCourseApi extends Api {
  constructor(jar, renew) {
    super({ jar, timeoutMs: 15_000, retries: 0 });
    this.renew = renew;
  }

  async call(method, endpoint, options = {}) {
    const scoped = {
      ...options,
      trace: false,
      validateUrl(url) {
        validateCourseApiUrl(url);
        options.validateUrl?.(url);
      },
    };
    try {
      return await super.call(method, endpoint, scoped);
    } catch (reason) {
      if (reason.code !== 'AUTH_REQUIRED' || !this.renew || method.toUpperCase() !== 'GET') throw reason;
      this.jar = await this.renew();
      return super.call(method, endpoint, scoped);
    }
  }
}

async function verifyCourses(jar) {
  const result = await new DesktopCourseApi(jar).getJson('/api/my-courses', {
    query: { page: 1, page_size: 1 },
  }).catch((reason) => {
    if (reason.code === 'NOT_JSON') throw error('BAD_RESPONSE');
    throw reason;
  });
  if (!Array.isArray(result?.courses ?? result?.data?.courses ?? result?.my_courses)) {
    throw error('BAD_RESPONSE');
  }
}

async function renewCourseCookies(jar) {
  const result = await request(ENTRY, {
    jar, trace: false, maxRedirects: 15, signal: AbortSignal.timeout(25_000),
    validateUrl(url) {
      if ((!SSO_ORIGINS.has(url.origin) && url.origin !== DEFAULT_BASE) || url.username || url.password) {
        throw error('CAS_REDIRECT');
      }
    },
  });
  await result.res.body?.cancel();
  if (SSO_ORIGINS.has(new URL(result.url).origin) || result.res.status === 401) throw error('AUTH_REQUIRED');
  if (result.res.status === 403) throw error('FORBIDDEN');
  if (result.res.status >= 500) throw error('SERVICE_UNAVAILABLE');
  if (!result.res.ok) throw error('BAD_RESPONSE');
  await verifyCourses(jar);
  return jar;
}

function sessionKeyFor(service, session, unknown = true) {
  const subject = session?.username
    ? `account\0${session.username}`
    : unknown && session?.savedAt ? `session\0${service}\0${session.savedAt}` : null;
  return subject ? createHash('sha256').update(subject).digest('hex') : null;
}

export function createDesktopAuth({ safeStorage, userData, credentialPath, platform = process.platform } = {}) {
  const credentialFile = credentialPath ?? path.join(userData, 'zju-credential.dpapi');
  let autoLoginBlocked = false;
  let queue = Promise.resolve();

  function enqueue(task) {
    const pending = queue.then(task);
    queue = pending.catch(() => {});
    return pending;
  }

  function canRemember() {
    try {
      return platform === 'win32' && Boolean(safeStorage?.isEncryptionAvailable());
    } catch {
      return false;
    }
  }

  function readCredential() {
    if (!canRemember()) return null;
    try {
      if (statSync(credentialFile).size > MAX_SECRET_BYTES * 4) return null;
      const value = JSON.parse(safeStorage.decryptString(readFileSync(credentialFile)));
      return value.version === 1 && protectCredentialField(value.username) && protectCredentialField(value.password)
        ? { username: value.username, password: value.password } : null;
    } catch {
      return null;
    }
  }

  function writeCredential(credential) {
    if (!canRemember()) throw error('CREDENTIAL_ERROR');
    const buffer = safeStorage.encryptString(JSON.stringify({ version: 1, ...credential }));
    const temporary = `${credentialFile}.${randomUUID()}.tmp`;
    try {
      mkdirSync(path.dirname(credentialFile), { recursive: true, mode: 0o700 });
      writeFileSync(temporary, buffer, { flag: 'wx', mode: 0o600 });
      renameSync(temporary, credentialFile);
    } finally {
      removeFile(temporary);
    }
  }

  function session(service) {
    const existing = loadSession(service);
    return existing?.jar.cookies.size && (!existing.baseUrl || existing.baseUrl === ORIGINS[service]) ? existing : null;
  }

  function save(service, jar, username, method) {
    try {
      const serviceJar = service === 'classroom'
        ? CookieJar.fromJSON(jar.toJSON().filter(({ domain }) => domain === 'classroom.zju.edu.cn' || domain === 'zju.edu.cn'))
        : jar;
      saveSession({ service, jar: serviceJar, username, baseUrl: ORIGINS[service], meta: { method } });
      return serviceJar;
    } catch {
      throw error('CONFIG_ERROR');
    }
  }

  function clear(service) {
    try {
      removeFile(sessionPath(service));
    } catch {
      throw error('CONFIG_ERROR');
    }
  }

  function overview(courses, classroom, warnings) {
    return {
      courses, classroom, canRemember: canRemember(), remembered: Boolean(readCredential()),
      ...(warnings?.length ? { warnings } : {}),
    };
  }

  async function probe(service) {
    const current = session(service);
    if (!current) return SIGNED_OUT;
    try {
      if (service === 'courses') await verifyCourses(current.jar);
      else await new ClassroomClient({ jar: current.jar }).checkSession();
      return CONNECTED;
    } catch (reason) {
      return asState(reason);
    }
  }

  async function status() {
    const [courses, classroom] = await Promise.all([probe('courses'), probe('classroom')]);
    return overview(courses, classroom);
  }

  async function freshCourses(credential) {
    const jar = new CookieJar();
    try {
      await passwordLogin({ ...credential, jar });
      await verifyCourses(jar);
      return jar;
    } catch (reason) {
      throw safeError(reason);
    }
  }

  async function completeLogin(credential, remember) {
    const jar = await freshCourses(credential);
    autoLoginBlocked = false;
    const oldClassroom = session('classroom');
    save('courses', jar, credential.username, 'cas');
    if (oldClassroom && oldClassroom.username !== credential.username) clear('classroom');
    const warnings = [];
    try {
      if (remember) writeCredential(credential);
      else removeFile(credentialFile);
    } catch {
      // A previously remembered account must not be used after a different login.
      if (readCredential()?.username !== credential.username) {
        try { removeFile(credentialFile); } catch {}
      }
      warnings.push({ code: 'CREDENTIAL_ERROR', message: ERROR_TEXT.CREDENTIAL_ERROR });
    }
    let classroom;
    try {
      await establishClassroom(jar, credential.username, 'cas');
      classroom = CONNECTED;
    } catch (reason) {
      const previous = await probe('classroom');
      classroom = previous.authenticated ? previous : asState(reason);
    }
    return overview(CONNECTED, classroom, warnings);
  }

  function login({ username, password, remember } = {}) {
    if (typeof username !== 'string' || !protectCredentialField(username.trim()) || !protectCredentialField(password) ||
        (remember !== undefined && typeof remember !== 'boolean')) {
      throw error('USAGE', '请输入有效的统一身份认证账号和密码。');
    }
    const credential = { username: username.trim(), password };
    return enqueue(() => completeLogin(credential, remember === true));
  }

  function loginSaved() {
    return enqueue(() => {
      const credential = readCredential();
      if (!credential) throw error('CREDENTIAL_ERROR');
      return completeLogin(credential, true);
    });
  }

  async function establishClassroom(jar, username, method) {
    if (method === 'cas') await connectClassroom(jar);
    await new ClassroomClient({ jar }).checkSession();
    return save('classroom', jar, username, method);
  }

  function credentialFor(current) {
    if (autoLoginBlocked || !current?.username || current.meta.method === 'cookie') return null;
    const usable = readCredential();
    return usable?.username === current.username ? usable : null;
  }

  async function freshCoursesAutomatically(credential) {
    try {
      return await freshCourses(credential);
    } catch (reason) {
      if (INTERACTIVE_AUTH_CODES.has(reason.code)) autoLoginBlocked = true;
      throw reason;
    }
  }

  function renewCourses(failedSession) {
    return enqueue(async () => {
      const current = session('courses');
      if (!current) throw error('AUTH_REQUIRED');
      if (sessionKeyFor('courses', current) !== sessionKeyFor('courses', failedSession)) throw error('ACCOUNT_CHANGED');
      if (current.savedAt !== failedSession.savedAt) return current.jar;
      try {
        const jar = await renewCourseCookies(current.jar);
        return save('courses', jar, current.username, current.meta.method);
      } catch (reason) {
        const credential = credentialFor(current);
        if (reason.code !== 'AUTH_REQUIRED' || !credential) throw safeError(reason);
        const jar = await freshCoursesAutomatically(credential);
        return save('courses', jar, credential.username, 'cas');
      }
    });
  }

  function getApi() {
    const current = session('courses');
    if (!current) throw error('AUTH_REQUIRED');
    return new DesktopCourseApi(current.jar, () => renewCourses(current));
  }

  async function withClassroom(operation) {
    const original = session('classroom');
    const coursesBefore = session('courses');
    if (original) {
      try {
        return await operation(new ClassroomClient({ jar: original.jar }));
      } catch (reason) {
        if (reason.code !== 'AUTH_REQUIRED') throw reason;
      }
    }
    const jar = await enqueue(async () => {
      const current = session('classroom');
      const courses = session('courses');
      if (sessionKeyFor('courses', courses) !== sessionKeyFor('courses', coursesBefore)) {
        throw error('ACCOUNT_CHANGED');
      }
      if (current && current.savedAt !== original?.savedAt) {
        if (original && sessionKeyFor('classroom', current) !== sessionKeyFor('classroom', original)) {
          throw error('ACCOUNT_CHANGED');
        }
        return current.jar;
      }
      if (!courses || original?.meta.method === 'cookie' ||
          (original?.username && original.username !== courses.username)) {
        throw error('AUTH_REQUIRED');
      }
      try {
        return await establishClassroom(courses.jar, courses.username, 'cas');
      } catch (reason) {
        const credential = credentialFor(courses);
        if (reason.code !== 'AUTH_REQUIRED' || !credential) throw safeError(reason);
        const fresh = await freshCoursesAutomatically(credential);
        save('courses', fresh, credential.username, 'cas');
        try {
          return await establishClassroom(fresh, credential.username, 'cas');
        } catch (next) {
          throw safeError(next);
        }
      }
    });
    return operation(new ClassroomClient({ jar }));
  }

  function importCookie({ service, cookie } = {}) {
    if (!Object.hasOwn(ORIGINS, service) || !protectCredentialField(cookie) || /[\x00-\x08\x0b\x0c\x0e-\x1f]/.test(cookie)) {
      throw error('COOKIE_INVALID');
    }
    return enqueue(async () => {
      let jar;
      try {
        jar = jarFromCookieString(cookie, new URL(ORIGINS[service]).hostname);
      } catch {
        throw error('COOKIE_INVALID');
      }
      try {
        if (service === 'classroom') {
          await establishClassroom(jar, null, 'cookie');
          return overview(await probe('courses'), CONNECTED);
        }
        await verifyCourses(jar);
        let profile;
        try { profile = await new DesktopCourseApi(jar).whoami(); } catch {}
        const user = profile?.data?.user ?? profile?.user ?? profile?.data ?? profile;
        const account = user?.user_no ?? user?.username ?? user?.account ?? user?.login;
        const username = typeof account === 'string' && account ? account : null;
        const classroom = session('classroom');
        if (classroom && (!username || classroom.username !== username)) clear('classroom');
        if (!username || readCredential()?.username !== username) removeFile(credentialFile);
        save(service, jar, username, 'cookie');
        return overview(CONNECTED, await probe('classroom'));
      } catch (reason) {
        throw safeError(reason);
      }
    });
  }

  function forget() {
    return enqueue(async () => {
      try { removeFile(credentialFile); } catch { throw error('CREDENTIAL_ERROR'); }
      return status();
    });
  }

  function logout() {
    return enqueue(() => {
      const warnings = [];
      try { removeFile(credentialFile); } catch {
        warnings.push({ code: 'CREDENTIAL_ERROR', message: ERROR_TEXT.CREDENTIAL_ERROR });
      }
      clear('courses');
      clear('classroom');
      return overview(SIGNED_OUT, SIGNED_OUT, warnings);
    });
  }

  const handlers = {
    'auth.status': status,
    'auth.login': login,
    'auth.loginSaved': loginSaved,
    'auth.logout': logout,
    'auth.forget': forget,
    'auth.importCookie': importCookie,
  };

  return {
    status, login, loginSaved, logout, forget, importCookie, getApi, withClassroom,
    scope: (service = 'courses') => sessionKeyFor(service, session(service), false),
    sessionKey: (service = 'courses') => sessionKeyFor(service, session(service)),
    handle(method, payload) {
      if (!Object.hasOwn(handlers, method)) throw error('USAGE', '未知的登录操作。');
      return handlers[method](payload ?? {});
    },
  };
}

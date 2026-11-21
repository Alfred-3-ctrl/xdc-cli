import { ApiError } from './api.js';
import { classroomToken } from './classroom.js';
import { request } from './http.js';

export const CLASSROOM_BASE = 'https://classroom.zju.edu.cn';
const CLASSROOM_ENTRY = 'https://tgmedia.cmc.zju.edu.cn/index.php?r=auth/login&auType=cmc&tenant_code=112&forward=https%3A%2F%2Fclassroom.zju.edu.cn%2F';
const CAS_ORIGIN = 'https://zjuam.zju.edu.cn';
const IDENTITY_ORIGIN = 'https://identity.zju.edu.cn';
const CLASSROOM_LOGIN_ORIGINS = new Set([
  CLASSROOM_BASE,
  'https://tgmedia.cmc.zju.edu.cn',
  'https://yjapi.cmc.zju.edu.cn',
  CAS_ORIGIN,
  IDENTITY_ORIGIN,
]);

function validateLoginUrl(url) {
  if (url.username || url.password || !CLASSROOM_LOGIN_ORIGINS.has(url.origin)) {
    throw new ApiError('智云课堂认证跳转到了未识别的地址', { code: 'CLASSROOM_SSO_REDIRECT' });
  }
}

// Exchange a valid CAS session through the classroom's own login entry.
export async function connectClassroom(jar, { requestImpl = request } = {}) {
  let result;
  try {
    result = await requestImpl(CLASSROOM_ENTRY, {
      jar,
      trace: false,
      timeoutMs: 20000,
      signal: AbortSignal.timeout(35000),
      maxRedirects: 15,
      validateUrl: validateLoginUrl,
    });
    await result.res.body?.cancel();
  } catch (error) {
    if (error instanceof ApiError) throw error;
    throw new ApiError('无法连接智云课堂认证服务', { code: 'NETWORK' });
  }
  const origin = new URL(result.url).origin;
  if (origin === CAS_ORIGIN || origin === IDENTITY_ORIGIN || result.res.status === 401) {
    throw new ApiError('智云课堂需要有效的统一身份认证会话', {
      code: 'AUTH_REQUIRED',
      status: 401,
      hint: '运行 xdc login --force --classroom，或在官网登录智云后导入该平台的会话',
    });
  }
  if (result.res.status === 403) {
    throw new ApiError('智云课堂拒绝连接', { code: 'FORBIDDEN', status: 403 });
  }
  if (!result.res.ok) {
    throw new ApiError('智云课堂认证服务返回错误', {
      code: result.res.status >= 500 ? 'SERVICE_UNAVAILABLE' : 'CLASSROOM_SSO_RESPONSE',
      status: result.res.status,
    });
  }
  if (!classroomToken(jar)) {
    throw new ApiError('智云课堂未建立可用会话', {
      code: 'AUTH_REQUIRED',
      hint: '在官网完成智云课堂登录后，用 xdc classroom login --cookie-file <文件> 导入',
    });
  }
  return jar;
}

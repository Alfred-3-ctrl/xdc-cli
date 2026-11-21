import { randomUUID } from 'node:crypto';
import { link, mkdir, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { ApiError } from './api.js';
import { UA } from './http.js';

export const CLASSROOM_ORIGIN = 'https://classroom.zju.edu.cn';

const PAGE_SIZE = 100;
const MAX_PAGES = 1000;
const MAX_JSON_BYTES = 4 * 1024 * 1024;
const MAX_IMAGE_BYTES = 16 * 1024 * 1024;
const MAX_EXPORT_BYTES = 256 * 1024 * 1024;
const MAX_IMAGE_PIXELS = 20_000_000;
const PNG_SIGNATURE = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
const REDIRECT = new Set([301, 302, 303, 307, 308]);

function classroomAuthError(status = 401) {
  return new ApiError('智云课堂会话未建立或已失效', {
    code: 'AUTH_REQUIRED', status, hint: '请重新登录智云课堂并重试',
  });
}

function invalidResponse(message = '智云课堂返回了无法识别的数据') {
  return new ApiError(message, { code: 'BAD_RESPONSE' });
}

function queryError(message) {
  return new ApiError(message, { code: 'USAGE' });
}

/** The classroom login stores a URL-encoded, PHP-serialized token in a cookie. */
export function classroomToken(jar) {
  const cookies = jar?.headerFor(CLASSROOM_ORIGIN) || '';
  for (const cookie of cookies.split(/;\s+/)) {
    const value = cookie.slice(cookie.indexOf('=') + 1);
    let decoded;
    try {
      decoded = decodeURIComponent(value);
    } catch {
      continue;
    }
    const token = decoded.match(/\{i:\d+;s:\d+:"_token";i:\d+;s:\d+:"([A-Za-z0-9._~+/-]+={0,2})";\}/)?.[1];
    if (token && token.length <= 8192) return token;
  }
  return null;
}

function validId(value) {
  return (typeof value === 'string' || Number.isSafeInteger(value)) && /^[1-9]\d{0,19}$/.test(String(value));
}

function requireIds(courseId, sessionId) {
  if (!validId(courseId) || !validId(sessionId)) throw queryError('课程和课堂场次 ID 需要是正整数');
}

function imageUrl(input, base = CLASSROOM_ORIGIN) {
  let url;
  try {
    if (typeof input === 'string' && input) url = new URL(input, base);
  } catch {
    throw invalidResponse('智云课堂返回了无效的幻灯片地址');
  }
  if (url?.protocol !== 'https:' || url.port || url.username || url.password ||
      !url.hostname.endsWith('.zju.edu.cn')) {
    throw invalidResponse('智云课堂返回了未经验证的幻灯片服务地址');
  }
  return url;
}

function platformError(status) {
  if (status === 401) return classroomAuthError();
  const code = {
    403: 'FORBIDDEN',
    404: 'NOT_FOUND',
    429: 'HTTP_ERROR',
    502: 'SERVICE_UNAVAILABLE',
    503: 'SERVICE_UNAVAILABLE',
    504: 'SERVICE_UNAVAILABLE',
  }[status] ?? (status >= 500 ? 'SERVER_ERROR' : 'HTTP_ERROR');
  const message = {
    403: '当前账号无权访问此智云课堂资源',
    404: '智云课堂资源不存在',
    429: '智云课堂限制了请求频率',
  }[status] ?? '智云课堂请求失败';
  return new ApiError(`${message}（HTTP ${status}）`, { code, status });
}

async function readBody(response, limit) {
  const length = Number(response.headers.get('content-length'));
  if (length > limit) {
    await response.body?.cancel();
    throw invalidResponse('智云课堂响应超过大小限制');
  }
  if (!response.body) return Buffer.alloc(0);
  const reader = response.body.getReader();
  const parts = [];
  let size = 0;
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return Buffer.concat(parts, size);
      size += value.byteLength;
      if (size > limit) throw invalidResponse('智云课堂响应超过大小限制');
      parts.push(value);
    }
  } finally {
    // On an oversize or aborted response, release the connection promptly.
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
}

function dateFrom(value) {
  return typeof value === 'string' ? /^\d{4}-\d{2}-\d{2}/.exec(value)?.[0] : undefined;
}

export class ClassroomClient {
  constructor({ jar, timeoutMs = 25_000, fetchImpl = fetch } = {}) {
    this.jar = jar;
    this.timeoutMs = timeoutMs;
    this.fetchImpl = fetchImpl;
  }

  async #get(url, { token, image = false, redirectCount = 0 } = {}) {
    const headers = {
      'user-agent': UA,
      accept: image ? 'image/jpeg, image/png, image/*' : 'application/json',
    };
    if (url.origin === CLASSROOM_ORIGIN) {
      const cookie = this.jar?.headerFor(url.href);
      if (cookie) headers.cookie = cookie;
      if (token) headers.authorization = `Bearer ${token}`;
    }
    let response;
    try {
      response = await this.fetchImpl(url.href, {
        headers, redirect: 'manual', signal: AbortSignal.timeout(this.timeoutMs),
      });
    } catch {
      throw new ApiError('无法连接智云课堂，请检查网络后重试', { code: 'NETWORK' });
    }
    if (REDIRECT.has(response.status)) {
      const location = response.headers.get('location');
      await response.body?.cancel();
      if (image && redirectCount < 4) {
        return this.#get(imageUrl(location, url), { image, redirectCount: redirectCount + 1 });
      }
      if (!image) throw classroomAuthError();
      throw invalidResponse('幻灯片服务返回了过多重定向');
    }
    if (!response.ok) {
      await response.body?.cancel();
      throw platformError(response.status);
    }
    if (url.origin === CLASSROOM_ORIGIN) this.jar?.addFromResponse(url.href, response);
    try {
      return await readBody(response, image ? MAX_IMAGE_BYTES : MAX_JSON_BYTES);
    } catch (error) {
      if (error instanceof ApiError) throw error;
      throw new ApiError('读取智云课堂响应失败，请检查网络后重试', { code: 'NETWORK' });
    }
  }

  async #json(endpoint, query = {}, { bearer = false } = {}) {
    const token = classroomToken(this.jar);
    if (!token) throw classroomAuthError();
    const url = new URL(endpoint, CLASSROOM_ORIGIN);
    url.search = new URLSearchParams(query).toString();
    const body = (await this.#get(url, { token: bearer ? token : undefined })).toString('utf8');
    if (/^\s*</.test(body) && /统一身份认证|登录页面/.test(body)) throw classroomAuthError();
    let payload;
    try {
      payload = JSON.parse(body);
    } catch {
      throw invalidResponse();
    }
    const code = Number(payload?.code);
    if (code === 401 || code === 403) throw platformError(code);
    if (code === 404 || code === 429 || (code >= 500 && code < 600)) throw platformError(code);
    return payload;
  }

  async checkSession() {
    const result = await this.#json('/userapi/v1/infosimple', {}, { bearer: true });
    if (!validId(result?.params?.id)) throw invalidResponse('智云课堂未返回已登录身份');
    return { authenticated: true };
  }

  async sessions(month) {
    if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) throw queryError('月份格式应为 YYYY-MM');
    const result = await this.#json('/courseapi/v2/course-live/get-my-course-month', { month }, { bearer: true });
    if (!Array.isArray(result?.list)) throw invalidResponse('智云课堂未返回课程列表');
    const sessions = [];
    for (const day of result.list) {
      if (!Array.isArray(day?.course)) throw invalidResponse('智云课堂返回了无效的课堂场次列表');
      for (const entry of day.course) {
        if (!validId(entry?.id) || !validId(entry?.sub_id)) throw invalidResponse('智云课堂返回了无效的课堂场次 ID');
        sessions.push({
          course_id: String(entry.id),
          course_name: typeof entry.title === 'string' ? entry.title : '',
          session_id: String(entry.sub_id),
          session_name: typeof entry.sub_title === 'string' ? entry.sub_title : '',
          lecturer: typeof entry.realname === 'string' ? entry.realname : '',
          date: dateFrom(day.date) ?? dateFrom(day.day) ?? dateFrom(entry.sub_title) ?? null,
        });
      }
    }
    return sessions;
  }

  async slides(courseId, sessionId) {
    requireIds(courseId, sessionId);
    const slides = [];
    for (let page = 1; ; page++) {
      const result = await this.#json('/pptnote/v1/schedule/search-ppt', {
        course_id: courseId, sub_id: sessionId, page, per_page: PAGE_SIZE,
      });
      const total = String(result?.total ?? '');
      if (!/^\d+$/.test(total) || Number(total) > MAX_PAGES || !Array.isArray(result?.list)) {
        throw invalidResponse('智云课堂课件页数无效或超过单节课堂的处理限制');
      }
      if (result.list.length !== Math.min(PAGE_SIZE, Number(total) - slides.length)) {
        throw invalidResponse(`智云课堂幻灯片列表第 ${page} 批不完整，请刷新后重试`);
      }
      for (const item of result.list) {
        let content;
        try {
          content = typeof item?.content === 'string' ? JSON.parse(item.content) : item?.content;
        } catch {
          throw invalidResponse(`智云课堂第 ${slides.length + 1} 页幻灯片内容无效`);
        }
        slides.push({ page: slides.length + 1, url: imageUrl(content?.pptimgurl).href });
      }
      if (slides.length === Number(total)) return slides;
    }
  }

  async exportPdf(courseId, sessionId, destPath) {
    if (typeof destPath !== 'string' || path.extname(destPath).toLowerCase() !== '.pdf') {
      throw queryError('导出路径应指定 PDF 文件（以 .pdf 结尾）');
    }
    const slides = await this.slides(courseId, sessionId);
    if (!slides.length) throw new ApiError('该课堂还没有可导出的幻灯片', { code: 'NOT_FOUND' });

    const { PDFDocument } = await import('pdf-lib');
    const pdf = await PDFDocument.create();
    const missingPages = [];
    let totalSize = 0;
    for (const slide of slides) {
      let data;
      try {
        data = await this.#get(imageUrl(slide.url), { image: true });
      } catch (error) {
        if (error.code === 'AUTH_REQUIRED' || error.code === 'FORBIDDEN') {
          error.message = `第 ${slide.page} 页：${error.message}`;
          throw error;
        }
        missingPages.push(slide.page);
        continue;
      }
      totalSize += data.length;
      if (totalSize > MAX_EXPORT_BYTES) throw invalidResponse('幻灯片总大小超过单个 PDF 的处理限制');
      try {
        const embedded = await embedSlide(pdf, data);
        await embedded.embed();
        const { width, height } = embedded.scale(Math.min(1, 1440 / Math.max(embedded.width, embedded.height)));
        const page = pdf.addPage([width, height]);
        page.drawImage(embedded, { x: 0, y: 0, width, height });
      } catch {
        missingPages.push(slide.page);
      }
    }
    if (missingPages.length) {
      const error = new ApiError(`智云课堂幻灯片导出不完整，缺少第 ${missingPages.join('、')} 页`, {
        code: 'CLASSROOM_PARTIAL_EXPORT',
      });
      error.missing_pages = missingPages;
      throw error;
    }

    const file = path.resolve(destPath);
    const bytes = await pdf.save();
    await savePdf(file, bytes);
    return {
      path: file, bytes: bytes.length, page_count: slides.length,
      course_id: String(courseId), session_id: String(sessionId),
    };
  }
}

async function embedSlide(pdf, data) {
  if (data.subarray(0, PNG_SIGNATURE.length).equals(PNG_SIGNATURE) &&
      data.length >= 33 && data.toString('ascii', 12, 16) === 'IHDR') {
    const width = data.readUInt32BE(16);
    const height = data.readUInt32BE(20);
    if (width && height && width * height <= MAX_IMAGE_PIXELS) return pdf.embedPng(data);
  } else if (data[0] === 0xff && data[1] === 0xd8) {
    // pdf-lib's JPEG decoder reads from offset zero of the underlying buffer.
    const jpeg = await pdf.embedJpg(Uint8Array.from(data));
    if (jpeg.width && jpeg.height && jpeg.width * jpeg.height <= MAX_IMAGE_PIXELS) return jpeg;
  }
  throw invalidResponse('幻灯片图片格式不支持或尺寸超出限制');
}

async function savePdf(file, bytes) {
  const dir = path.dirname(file);
  const temp = path.join(dir, `.xdc-classroom-${randomUUID()}.part`);
  try {
    await mkdir(dir, { recursive: true });
    await writeFile(temp, bytes, { flag: 'wx' });
    await link(temp, file);
  } catch (error) {
    if (error.code === 'EEXIST') {
      throw new ApiError('目标 PDF 已存在，请指定其他文件名', { code: 'FILE_EXISTS' });
    }
    throw new ApiError('无法保存智云课堂 PDF，请检查目标目录', { code: 'FILE_ERROR' });
  } finally {
    await unlink(temp).catch(() => {});
  }
}

/*
 * WPS request format adapted from PeiPei233/zju-learning-assistant
 * (src-tauri/src/utils/wps.rs at a759541).
 *
 * MIT License
 *
 * Copyright (c) 2023 PeiPei233
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

import { constants, createCipheriv, createDecipheriv, publicEncrypt, randomInt } from 'node:crypto';
import { ApiError, DEFAULT_BASE } from './api.js';
import { request } from './http.js';

const WPS_HOST = 'course-wps.zju.edu.cn';
const COURSES_HOST = 'courses.zju.edu.cn';
const redirectCodes = new Set([301, 302, 303, 307, 308]);
const alphabet = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz';

function wpsError(code, message, status = 0) {
  return new ApiError(message, { code, status });
}

function checkUrl(input, acceptsHost, base) {
  let url;
  try {
    url = new URL(input, base);
  } catch {
    throw wpsError('WPS_INVALID_URL', 'WPS 返回了无效地址');
  }
  if (url.protocol !== 'https:' || url.port || url.username || url.password || !acceptsHost(url.hostname)) {
    throw wpsError('WPS_INVALID_URL', 'WPS 返回了预期校内服务之外的地址');
  }
  return url;
}

function cancelBody(res) {
  void res.body?.cancel().catch(() => {});
}

function checkStatus(res, step) {
  if (res.ok) return;
  cancelBody(res);
  if (res.status === 401) throw wpsError('AUTH_REQUIRED', `${step}需要重新登录学在浙大`, 401);
  if (res.status === 403) throw wpsError('FORBIDDEN', `${step}被平台拒绝`, 403);
  throw wpsError('WPS_REQUEST_FAILED', `${step}失败（HTTP ${res.status}）`, res.status);
}

// Keep signed URLs, cookies and encrypted gateway parameters out of HTTP debug logs.
async function fetchWps(input, { acceptsHost, jar, signal, step, timeoutMs, method = 'GET', headers, body }) {
  const timeout = AbortSignal.timeout(timeoutMs);
  const pending = signal ? AbortSignal.any([signal, timeout]) : timeout;
  const initial = checkUrl(input, acceptsHost);
  let result;
  try {
    result = await request(initial, {
      method, headers, body, jar, timeoutMs, signal: pending, trace: false,
      follow: method === 'GET', maxRedirects: 4,
      validateUrl(url) {
        if (['zjuam.zju.edu.cn', 'identity.zju.edu.cn'].includes(url.hostname)) {
          throw wpsError('AUTH_REQUIRED', `${step}需要重新登录学在浙大`, 401);
        }
        checkUrl(url, acceptsHost);
      },
    });
  } catch (err) {
    if (err instanceof ApiError) throw err;
    const code = signal?.aborted ? 'ABORTED' : 'NETWORK';
    throw wpsError(code, `${step}${signal?.aborted ? '已取消' : timeout.aborted ? '超时' : '连接失败'}`);
  }
  if (method !== 'GET' && redirectCodes.has(result.res.status)) {
    cancelBody(result.res);
    throw wpsError('WPS_REDIRECT', 'WPS 导出请求收到意外跳转');
  }
  checkStatus(result.res, step);
  return { res: result.res, url: new URL(result.url) };
}

async function readText(res, maxBytes, step, signal) {
  if (!res.body) throw wpsError('WPS_INVALID_RESPONSE', `${step}响应为空`);
  const chunks = [];
  let bytes = 0;
  try {
    for await (const part of res.body) {
      bytes += part.byteLength;
      if (bytes > maxBytes) throw wpsError('WPS_INVALID_RESPONSE', `${step}响应超过大小限制`);
      chunks.push(part);
    }
  } catch (err) {
    if (err instanceof ApiError) throw err;
    throw wpsError(signal?.aborted ? 'ABORTED' : 'NETWORK', `${step}响应读取失败`);
  }
  return Buffer.concat(chunks, bytes).toString('utf8');
}

function readJson(value, step) {
  try {
    return JSON.parse(value);
  } catch {
    throw wpsError('WPS_INVALID_RESPONSE', `${step}响应格式无效`);
  }
}

function parseBootstrap(html, uploadId) {
  const start = /window\.__WPSENV__\s*=\s*\{/.exec(html);
  if (!start) throw wpsError('WPS_INVALID_RESPONSE', 'WPS 预览配置缺失');
  const first = start.index + start[0].length - 1;
  let depth = 0;
  let quoted = false;
  for (let i = first; i < html.length; i++) {
    const ch = html[i];
    if (quoted) {
      if (ch === '\\') i++;
      else if (ch === '"') quoted = false;
    } else if (ch === '"') {
      quoted = true;
    } else if (ch === '{') {
      depth++;
    } else if (ch === '}' && --depth === 0) {
      const data = readJson(html.slice(first, i + 1), 'WPS 预览配置');
      if (String(data.root_file_id) !== uploadId) {
        throw wpsError('WPS_INVALID_RESPONSE', 'WPS 返回的课件与请求不一致');
      }
      return data;
    }
  }
  throw wpsError('WPS_INVALID_RESPONSE', 'WPS 预览配置不完整');
}

const isDenied = (value) => value === false || value === 0;
const isGranted = (value) => value === true || value === 1;

function checkPermission(mode, attachment, ...sources) {
  const action = mode === 'original' ? 'download' : 'print';
  const permissions = sources.flatMap((source) => [
    source?.permission, source?.permissions, source?.data?.permission, source?.data?.permissions,
  ]);
  if (permissions.some((grant) => isDenied(grant?.read) || isDenied(grant?.[action]) ||
      (mode === 'preview_pdf' && isDenied(grant?.export)))) {
    throw wpsError('FORBIDDEN', 'WPS 未授权当前用户执行此操作', 403);
  }
  if (mode === 'original' && attachment.allow_download === true) return;
  if (!permissions.some((grant) => isGranted(grant?.[action]))) {
    throw wpsError('WPS_PERMISSION_UNKNOWN', mode === 'original'
      ? '平台未明确提供 WPS 原文件下载权限'
      : '平台未明确提供 WPS 打印导出权限');
  }
}

function buildGateway(previewUrl, data, mode) {
  const { encryption, app_id: appId, csrf_token: csrfToken, root_file_id: rootId } = data;
  if (encryption?.isEncryption !== true || encryption.algorithm !== 'AES256' ||
      typeof encryption.gateway !== 'string' || !encryption.gateway ||
      typeof encryption.publicKey !== 'string' || !encryption.publicKey ||
      typeof appId !== 'string' || !appId || typeof csrfToken !== 'string' || !csrfToken) {
    throw wpsError('WPS_UNSUPPORTED', 'WPS 网关配置格式不受支持');
  }
  const url = checkUrl(encryption.gateway, (host) => host === WPS_HOST, previewUrl);
  const key = Array.from({ length: 32 }, () => alphabet[randomInt(alphabet.length)]).join('');
  const iv = Array.from({ length: 16 }, () => alphabet[randomInt(alphabet.length)]).join('');
  const encode = (json) => {
    const cipher = createCipheriv('aes-256-cbc', key, iv);
    return Buffer.concat([cipher.update(JSON.stringify(json)), cipher.final()]).toString('base64');
  };
  const original = mode === 'original';
  try {
    const wrapped = publicEncrypt({
      key: Buffer.from(encryption.publicKey, 'base64'),
      padding: constants.RSA_PKCS1_PADDING,
    }, Buffer.from(JSON.stringify({ private_key: key, iv }))).toString('base64');
    const headers = new Headers({
      'content-type': 'application/json',
      origin: previewUrl.origin,
      referer: previewUrl.href,
      'x-app-id': appId,
      'x-csrf-rand': csrfToken,
      'x-user-query': previewUrl.search.slice(1),
      'encryption-algorithm': 'AES256',
      'encryption-parameters': wrapped,
      'api-parameters': encode({
        method: original ? 'GET' : 'POST',
        api: `/api/v3/office/file/${rootId}/${original ? 'download' : 'export'}`,
        query_string: '',
        schema: 'https',
      }),
    });
    const body = original ? undefined : encode({ format: 'pdf', options: {}, print: true, clientId: '', password: '' });
    const decode = (text) => {
      try {
        const decipher = createDecipheriv('aes-256-cbc', key, iv);
        return Buffer.concat([decipher.update(Buffer.from(text, 'base64')), decipher.final()]).toString('utf8');
      } catch {
        throw wpsError('WPS_INVALID_RESPONSE', '无法解密 WPS 导出响应');
      }
    };
    return { url, headers: Object.fromEntries(headers), body, decode };
  } catch {
    throw wpsError('WPS_UNSUPPORTED', '无法生成 WPS 网关请求');
  }
}

function checkGatewayResult(data, mode) {
  const code = data?.error?.code ?? data?.code;
  if (Number(code) === 401 || Number(data?.status) === 401) {
    throw wpsError('AUTH_REQUIRED', 'WPS 会话已过期', 401);
  }
  if (Number(code) === 403 || Number(data?.status) === 403 ||
      /permission.denied|forbidden|无权限/i.test(`${code ?? ''} ${data?.error?.message ?? data?.msg ?? data?.message ?? ''}`)) {
    throw wpsError('FORBIDDEN', 'WPS 未授权当前用户执行此操作', 403);
  }
  const status = Number(data?.status ?? code);
  if (mode === 'original' && [404, 405, 410].includes(status)) {
    throw wpsError('WPS_ORIGINAL_UNAVAILABLE', 'WPS 暂未提供原文件下载', status);
  }
  if ((code != null && code !== 0 && code !== '0' && code !== 200 && code !== '200') ||
      typeof data?.url !== 'string' || !data.url) {
    throw wpsError('WPS_INVALID_RESPONSE', 'WPS 未返回导出文件地址');
  }
}

export async function requestWpsFile(api, attachment, { mode = 'preview_pdf', signal } = {}) {
  if (mode !== 'original' && mode !== 'preview_pdf') {
    throw wpsError('WPS_UNSUPPORTED', '未知的 WPS 导出类型');
  }
  if (api.baseUrl !== DEFAULT_BASE) {
    throw wpsError('WPS_UNSUPPORTED', 'WPS 导出只支持学在浙大正式站点');
  }
  if (attachment.wps_preview !== true ||
      !/^\d+$/.test(String(attachment.id)) || !/^\d+$/.test(String(attachment.activity_id))) {
    throw wpsError('WPS_UNSUPPORTED', '此附件缺少可用的 WPS 预览入口或所属活动');
  }
  if (mode === 'original' && (attachment.allow_download === false || attachment.downloadable === false)) {
    throw wpsError('FORBIDDEN', '平台未开放此附件的原文件下载', 403);
  }

  const previewEntry = new URL(`/api/uploads/${attachment.id}/preview/private-wps`, DEFAULT_BASE);
  previewEntry.search = new URLSearchParams({
    preview: 'true',
    refer_id: String(attachment.activity_id),
    refer_type: 'learning_activity',
  });
  const { res: infoRes } = await fetchWps(previewEntry, {
    acceptsHost: (host) => host === COURSES_HOST, jar: api.jar, signal, step: '查询 WPS 预览入口',
    timeoutMs: 30_000, headers: { accept: 'application/json', 'x-requested-with': 'XMLHttpRequest' },
  });
  const info = readJson(await readText(infoRes, 256 * 1024, 'WPS 预览入口', signal), 'WPS 预览入口');
  if (typeof info?.preview_url !== 'string' || !info.preview_url) {
    throw wpsError('WPS_UNSUPPORTED', '平台未提供 WPS 预览地址');
  }

  const acceptsWpsHost = (host) => host === WPS_HOST;
  const { res: page, url: previewUrl } = await fetchWps(info.preview_url, {
    acceptsHost: acceptsWpsHost, jar: api.jar, signal, step: '加载 WPS 预览页', timeoutMs: 30_000,
  });
  const data = parseBootstrap(await readText(page, 4 * 1024 * 1024, 'WPS 预览页', signal), String(attachment.id));
  checkPermission(mode, attachment, info, data);
  const gateway = buildGateway(previewUrl, data, mode);
  const { res: exported } = await fetchWps(gateway.url, {
    acceptsHost: acceptsWpsHost, jar: api.jar, signal,
    step: '生成 WPS 导出文件', timeoutMs: 90_000, method: 'POST', headers: gateway.headers, body: gateway.body,
  });
  const payload = await readText(exported, 256 * 1024, 'WPS 网关', signal);
  const result = readJson(exported.headers.get('encryption') === '1' ? gateway.decode(payload) : payload, 'WPS 网关');
  checkGatewayResult(result, mode);

  const acceptsFileHost = (host) => host === 'zju.edu.cn' || host.endsWith('.zju.edu.cn');
  const fileUrl = checkUrl(result.url, acceptsFileHost, gateway.url);
  const { res: file } = await fetchWps(fileUrl, {
    acceptsHost: acceptsFileHost, signal, step: '下载 WPS 导出文件',
    timeoutMs: 180_000, headers: { referer: `${previewUrl.origin}/` },
  });
  if (!file.body || /text\/html|application\/json/i.test(file.headers.get('content-type') ?? '')) {
    cancelBody(file);
    throw wpsError('WPS_INVALID_RESPONSE', 'WPS 下载地址返回了非文件响应');
  }
  return file;
}

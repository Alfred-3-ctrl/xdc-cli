import path from 'node:path';
import { ApiError, DEFAULT_BASE, isCourseMaterial, normMaterial } from './api.js';
import { safeDownloadName, saveDownload } from './download.js';
import { request } from './http.js';
import { requestWpsFile } from './wps.js';

const FALLBACK_CODES = new Set(['NOT_FOUND', 'NO_DOWNLOAD_URL', 'DOWNLOAD_UNAVAILABLE']);

function isId(value) {
  return (typeof value === 'number' && Number.isSafeInteger(value) && value > 0)
    || (typeof value === 'string' && /^[1-9]\d*$/.test(value));
}

export function learningActivityAttachments(activity, courseId, kind = 'material') {
  const uploads = Array.isArray(activity?.uploads) ? activity.uploads : [];
  return uploads.filter((upload) => upload && typeof upload === 'object').map((upload) => ({
    id: upload.id ?? null,
    reference_id: upload.reference_id ?? null,
    activity_id: activity.id ?? activity.activity_id ?? upload.activity_id ?? null,
    activity_title: activity.title ?? activity.name ?? null,
    course_id: courseId,
    source: 'learning',
    kind,
    name: upload.name ?? upload.filename ?? null,
    size: upload.size ?? upload.file_size ?? null,
    type: upload.mime_type ?? upload.type ?? null,
    downloadable: upload.allow_download !== false,
    allow_download: typeof upload.allow_download === 'boolean' ? upload.allow_download : null,
    wps_preview: upload.allow_private_wps_office_view === true,
  }));
}

function failure(error) {
  return {
    code: error.code ?? 'ERROR',
    message: error instanceof ApiError ? error.message : '操作失败',
  };
}

async function homeworkAttachments(api, courseId) {
  const activities = await api.homeworkActivities(courseId);
  const attachments = [];
  const errors = [];
  let index = 0;
  const workers = Array.from({ length: Math.min(activities.length, 4) }, async () => {
    while (index < activities.length) {
      const current = index++;
      const activity = activities[current];
      const id = activity?.id ?? activity?.activity_id;
      try {
        let metadata = activity;
        if (!Array.isArray(activity?.uploads) && isId(id)) {
          const raw = await api.homeworkDetail(courseId, id);
          const detail = raw?.homework_activity ?? raw?.activity ?? raw;
          metadata = { ...activity, ...detail };
        }
        attachments[current] = learningActivityAttachments(metadata, courseId, 'homework');
      } catch (error) {
        errors.push({ kind: 'homework', activity_id: id, ...failure(error) });
      }
    }
  });
  await Promise.all(workers);
  return { attachments: attachments.flat(), errors };
}

/** Retain the existing materials view and optionally gather extra activity attachments. */
export async function listLearningAttachments(api, courseId, { includeHomework = false } = {}) {
  const [course, homework] = await Promise.allSettled([
    includeHomework ? api.courseActivities(courseId) : api.courseMaterials(courseId),
    includeHomework ? homeworkAttachments(api, courseId) : { attachments: [], errors: [] },
  ]);
  if (course.status === 'rejected' && (!includeHomework || homework.status === 'rejected')) {
    throw course.reason;
  }
  const materials = [];
  const attachments = [];
  const errors = [];
  if (course.status === 'fulfilled') {
    for (const activity of course.value) {
      const files = learningActivityAttachments(activity, courseId);
      attachments.push(...files);
      if (isCourseMaterial(activity)) {
        materials.push({ ...normMaterial(activity), files });
      }
    }
  } else {
    errors.push({ kind: 'material', ...failure(course.reason) });
  }
  if (homework.status === 'fulfilled') {
    errors.push(...homework.value.errors);
    const homeworkKeys = new Set(homework.value.attachments
      .filter((item) => isId(item.id) && isId(item.activity_id))
      .map((item) => `${item.activity_id}/${item.id}`));
    const courseFiles = attachments.filter((item) => !homeworkKeys.has(`${item.activity_id}/${item.id}`));
    return {
      materials,
      attachments: [...courseFiles, ...homework.value.attachments],
      partial: errors.length > 0,
      errors,
    };
  }
  errors.push({ kind: 'homework', ...failure(homework.reason) });
  return { materials, attachments, partial: true, errors };
}

async function fetchFile(api, value, attachment, signal) {
  const base = new URL(api.baseUrl ?? DEFAULT_BASE);
  const url = new URL(value, base.origin);
  const validateUrl = (next) => {
    if (['zjuam.zju.edu.cn', 'identity.zju.edu.cn'].includes(next.hostname)) {
      throw new ApiError('学在浙大会话已失效，请重新登录', { code: 'AUTH_REQUIRED', status: 401 });
    }
    const allowed = next.origin === base.origin || next.origin === 'https://tcmedia.zju.edu.cn';
    if (!allowed || next.username || next.password) {
      throw new ApiError('课件下载地址不在允许的服务中', { code: 'INVALID_DOWNLOAD_URL' });
    }
  };
  let response;
  try {
    ({ res: response } = await request(url, {
      jar: api.jar,
      signal,
      trace: false,
      timeoutMs: 120000,
      maxRedirects: 5,
      validateUrl,
    }));
  } catch (error) {
    if (error instanceof ApiError) throw error;
    if (signal.aborted) throw signal.reason;
    throw new ApiError('课件下载请求失败', { code: 'NETWORK' });
  }
  if (!response.ok) {
    await response.body?.cancel();
    const status = response.status;
    const code = status === 401 ? 'AUTH_REQUIRED'
      : status === 403 ? 'FORBIDDEN'
      : [404, 405, 410].includes(status) ? 'DOWNLOAD_UNAVAILABLE' : 'DOWNLOAD_FAILED';
    throw new ApiError(`课件下载失败（HTTP ${status}）`, { code, status });
  }
  const mime = response.headers.get('content-type')?.split(';')[0]?.trim().toLowerCase();
  const disposition = response.headers.get('content-disposition');
  if (['text/html', 'application/json'].includes(mime)
      && attachment.type !== mime && !/^attachment\b/i.test(disposition ?? '')) {
    await response.body?.cancel();
    throw new ApiError('课件接口返回了页面或错误数据', { code: 'INVALID_DOWNLOAD' });
  }
  return response;
}

async function originalFile(api, item, signal) {
  try {
    const url = await api.uploadUrl(item.id, { signal, trace: false });
    return await fetchFile(api, url, item, signal);
  } catch (error) {
    if (!FALLBACK_CODES.has(error.code)) throw error;
  }
  if (isId(item.reference_id)) {
    try {
      return await fetchFile(api, `/api/uploads/reference/${item.reference_id}/blob`, item, signal);
    } catch (error) {
      if (!FALLBACK_CODES.has(error.code)) throw error;
    }
  }
  return fetchFile(api, `/api/uploads/${item.id}/blob`, item, signal);
}

async function openAttachment(api, item, signal) {
  if (!isId(item.id)) throw new ApiError('课件缺少有效的附件 ID', { code: 'MISSING_UPLOAD_ID' });
  if (item.downloadable) {
    try {
      const response = await originalFile(api, item, signal);
      return { response, format: 'original' };
    } catch (error) {
      if (!item.wps_preview || !FALLBACK_CODES.has(error.code)) throw error;
    }
    try {
      const response = await requestWpsFile(api, item, { mode: 'original', signal });
      return { response, format: 'original' };
    } catch (error) {
      const canTryPreview = error.code === 'WPS_PERMISSION_UNKNOWN'
        || error.code === 'WPS_ORIGINAL_UNAVAILABLE'
        || [404, 405, 410].includes(error.status);
      if (!canTryPreview) throw error;
    }
  }
  if (!item.wps_preview) {
    throw new ApiError('平台未开放此附件下载', { code: 'not_downloadable' });
  }
  const response = await requestWpsFile(api, item, { mode: 'preview_pdf', signal });
  return { response, format: 'preview_pdf' };
}

export async function downloadLearningAttachments(api, attachments, directory, {
  signal,
  groupByActivity = true,
} = {}) {
  const downloaded = [];
  const failed = [];
  for (const item of attachments) {
    signal?.throwIfAborted();
    const timeout = AbortSignal.timeout(120000);
    const downloadSignal = signal ? AbortSignal.any([signal, timeout]) : timeout;
    const name = safeDownloadName(item.name, `file-${isId(item.id) ? item.id : 'unknown'}`);
    const context = {
      id: item.id,
      name: item.name,
      course_id: item.course_id,
      activity_id: item.activity_id,
      source: item.source,
      kind: item.kind,
    };
    try {
      const dest = groupByActivity
        ? path.join(directory, safeDownloadName(item.activity_title, `material-${isId(item.activity_id) ? item.activity_id : 'unknown'}`))
        : directory;
      const { response, format } = await openAttachment(api, item, downloadSignal);
      const preview = format === 'preview_pdf';
      // Bound the original name separately so sanitization cannot remove the preview suffix.
      const fileName = preview ? `${name.slice(0, 108)}.preview.pdf` : name;
      const saved = await saveDownload(response, dest, fileName, {
        signal: downloadSignal,
        prefix: preview ? '%PDF-' : undefined,
      });
      downloaded.push({ ...context, ...saved, format });
    } catch (error) {
      if (signal?.aborted) throw signal.reason;
      const result = timeout.aborted
        ? { code: 'DOWNLOAD_TIMEOUT', message: '下载超时' }
        : failure(error);
      failed.push({ ...context, reason: result.code, message: result.message });
    }
  }
  return { downloaded, failed, partial: failed.length > 0 };
}

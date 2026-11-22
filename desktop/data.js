import { randomUUID } from 'node:crypto';
import fs from 'node:fs';
import { lstat } from 'node:fs/promises';
import path from 'node:path';
import { ApiError, normAssignment, normAssignmentDetail } from '../src/api.js';
import { safeDownloadName } from '../src/download.js';
import { downloadLearningAttachments, learningActivityAttachments, listLearningAttachments } from '../src/learning.js';
import { compareCourses, pickCurrentSemesterKeys } from '../src/terms.js';

const DAY = 86400000;
const CACHE_MS = 30 * 60 * 1000;
const DATE = new Intl.DateTimeFormat('sv-SE', { timeZone: 'Asia/Shanghai', dateStyle: 'short' });
const MAX_QUEUE = 200;

export const DATA_METHODS = Object.freeze([
  'overview.load',
  'courses.list',
  'courses.visibility',
  'course.detail',
  'assignment.detail',
  'classroom.list',
  'classroom.link',
  'download.enqueue',
  'download.jobs',
  'download.pause',
  'download.retry',
]);

const dayAt = (time) => DATE.format(new Date(time));

const badInput = (message) => new ApiError(message, { code: 'USAGE' });
const notFound = (message) => new ApiError(message, { code: 'NOT_FOUND' });

function requireId(value, label) {
  if ((typeof value !== 'string' && !Number.isSafeInteger(value)) || !/^[1-9]\d{0,19}$/.test(String(value))) {
    throw badInput(`${label}需要是正整数`);
  }
  return String(value);
}

function requireMonth(value, now) {
  if (value === undefined) return dayAt(now).slice(0, 7);
  if (typeof value !== 'string' || !/^\d{4}-(0[1-9]|1[0-2])$/.test(value)) {
    throw badInput('月份格式应为 YYYY-MM');
  }
  return value;
}

function errorInfo(error) {
  return {
    code: typeof error?.code === 'string' ? error.code : 'ERROR',
    message: error instanceof ApiError ? error.message : '请求失败，请重试',
  };
}

async function collect(items, concurrency, read) {
  const results = new Array(items.length);
  let index = 0;
  await Promise.all(Array.from({ length: Math.min(items.length, concurrency) }, async () => {
    while (index < items.length) {
      const current = index++;
      try {
        results[current] = { status: 'fulfilled', value: await read(items[current]) };
      } catch (error) {
        results[current] = { status: 'rejected', reason: error };
      }
    }
  }));
  return results;
}

function readState(file) {
  if (file) {
    try {
      const state = JSON.parse(fs.readFileSync(file, 'utf8'));
      if (state.version === 1 && state.accounts && typeof state.accounts === 'object') return state;
    } catch {}
  }
  return { version: 1, accounts: {} };
}

function freshAccount() {
  return { links: {}, knownMaterials: {}, recentMaterials: [], hiddenCourseIds: [] };
}

function comparable(value) {
  return typeof value === 'string' ? value.normalize('NFKC').trim().replace(/\s+/g, ' ') : '';
}

function courseChoices(sessions, course) {
  const courses = new Map();
  for (const session of sessions) {
    if (!courses.has(session.course_id)) {
      courses.set(session.course_id, {
        course_id: session.course_id,
        course_name: session.course_name,
        lecturer: session.lecturer,
      });
    }
  }
  const title = comparable(course.name);
  const priority = (item) => Number(title !== '' && comparable(item.course_name) === title);
  return [...courses.values()].sort((a, b) =>
    priority(b) - priority(a) || a.course_name.localeCompare(b.course_name, 'zh-CN'));
}

function automaticLink(course, sessions, allCourses) {
  const title = comparable(course.name);
  const instructors = comparable(course.instructor).split(',').map((name) => name.trim()).filter(Boolean);
  const start = Date.parse(course.start_date);
  const end = Date.parse(course.end_date);
  // Course name alone is ambiguous across teachers, class sections, and terms.
  if (!title || !instructors.length || !course.semester_key || !Number.isFinite(start) ||
      !Number.isFinite(end) || end < start) return null;
  const duplicate = allCourses.some((other) => String(other.id) !== String(course.id) &&
    other.semester_key === course.semester_key && comparable(other.name) === title &&
    comparable(other.instructor).split(',').some((name) => instructors.includes(name.trim())));
  if (duplicate) return null;
  const earliest = dayAt(start);
  const latest = dayAt(end);
  const candidates = new Set(sessions.filter((session) =>
    comparable(session.course_name) === title && instructors.includes(comparable(session.lecturer)) &&
    session.date >= earliest && session.date <= latest,
  ).map((session) => session.course_id));
  if (candidates.size !== 1) return null;
  return { classroomCourseId: [...candidates][0], mode: 'automatic' };
}

async function classroomDestination(directory, title) {
  const name = safeDownloadName(title, 'classroom').slice(0, 100);
  for (let suffix = 0; suffix < 1000; suffix++) {
    const target = path.join(directory, `${name}${suffix ? ` (${suffix})` : ''}.pdf`);
    try {
      await lstat(target);
    } catch (error) {
      if (error.code === 'ENOENT') return target;
      throw new ApiError('无法读取下载目录', { code: 'FILE_ERROR' });
    }
  }
  throw new ApiError('同名文件太多，请更换下载目录', { code: 'FILE_EXISTS' });
}

export function createDesktopData(options) {
  return new DesktopData(options);
}

export class DesktopData {
  #auth;
  #statePath;
  #state;
  #transient = new Map();
  #cache = new Map();
  #getDownloadDirectory;
  #defaultDownloadDir;
  #now;
  #listeners = new Set();
  #jobs = [];
  #active = null;
  #manualPaused = false;
  #quietPaused = false;

  constructor({ auth, statePath, defaultDownloadDir, getDownloadDirectory, emit, now = Date.now }) {
    this.#auth = auth;
    this.#statePath = statePath;
    this.#state = readState(statePath);
    this.#defaultDownloadDir = defaultDownloadDir;
    this.#getDownloadDirectory = getDownloadDirectory;
    this.#now = now;
    if (emit) this.subscribe(emit);
  }

  subscribe(listener) {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  #sessionKey(service = 'courses') {
    return this.#auth.sessionKey?.(service) ?? this.#auth.scope?.(service) ?? null;
  }

  #checkOwner(service, previous) {
    if (previous && this.#sessionKey(service) !== previous) {
      throw new ApiError('登录会话已更换，请刷新重试', { code: 'AUTH_REQUIRED' });
    }
  }

  async #cached(service, key, refresh, load) {
    const owner = this.#sessionKey(service);
    if (!owner) return load();
    const fullKey = `${service}/${owner}/${key}`;
    const entry = this.#cache.get(fullKey);
    if (entry && (entry.pending || (!refresh && this.#now() - entry.at < CACHE_MS))) return entry.result;
    // Expire old data opportunistically, including data from a previous user.
    for (const [other, item] of this.#cache) {
      if (!item.pending && this.#now() - item.at >= CACHE_MS) this.#cache.delete(other);
    }
    const current = { pending: true, at: this.#now(), result: null };
    current.result = Promise.resolve().then(load).then((value) => {
      this.#checkOwner(service, owner);
      current.pending = false;
      current.at = this.#now();
      return value;
    }).catch((error) => {
      if (this.#cache.get(fullKey) === current) this.#cache.delete(fullKey);
      throw error;
    });
    this.#cache.set(fullKey, current);
    return current.result;
  }

  #account(key, persistent, create = false) {
    if (!key) return null;
    if (persistent) {
      if (create) return this.#state.accounts[key] ??= freshAccount();
      return this.#state.accounts[key] ?? null;
    }
    if (create && !this.#transient.has(key)) this.#transient.set(key, freshAccount());
    return this.#transient.get(key) ?? null;
  }

  #persist() {
    if (!this.#statePath) return;
    const temp = `${this.#statePath}.${randomUUID()}.tmp`;
    try {
      fs.mkdirSync(path.dirname(this.#statePath), { recursive: true, mode: 0o700 });
      fs.writeFileSync(temp, JSON.stringify(this.#state), { flag: 'wx', mode: 0o600 });
      fs.renameSync(temp, this.#statePath);
    } catch {
      throw new ApiError('无法保存桌面学习设置', { code: 'FILE_ERROR' });
    } finally {
      try { fs.unlinkSync(temp); } catch {}
    }
  }

  async #courses(refresh = false) {
    return this.#cached('courses', 'current', refresh, async () => {
      const api = await this.#auth.getApi();
      const courses = await api.myCourses();
      const current = pickCurrentSemesterKeys(courses, this.#now());
      return courses.filter((course) => current.has(course.semester_key)).sort(compareCourses);
    });
  }

  async #listCourses({ refresh, includeHidden }) {
    const owner = this.#sessionKey();
    const courses = await this.#courses(refresh === true);
    this.#checkOwner('courses', owner);
    const hidden = new Set(this.#courseState()?.hiddenCourseIds ?? []);
    return {
      courses: includeHidden === true ? courses : courses.filter((course) => !hidden.has(String(course.id))),
      hiddenIds: courses.map((course) => String(course.id)).filter((id) => hidden.has(id)),
    };
  }

  async #setCourseVisibility({ hiddenIds }) {
    if (!Array.isArray(hiddenIds)) throw badInput('隐藏课程列表格式无效');
    const requested = new Set(hiddenIds.map((id) => requireId(id, '课程 ID')));
    const owner = this.#sessionKey();
    const courses = await this.#courses();
    this.#checkOwner('courses', owner);
    const account = this.#courseState(true);
    if (!account) throw new ApiError('请先登录学在浙大', { code: 'AUTH_REQUIRED' });
    const knownIds = courses.map((course) => String(course.id));
    const known = new Set(knownIds);
    const previous = account.hiddenCourseIds ?? [];
    const hiddenOutsideTerm = previous.filter((id) => !known.has(id));
    const staleIds = new Set(hiddenOutsideTerm);
    if ([...requested].some((id) => !known.has(id) && !staleIds.has(id))) {
      throw notFound('课程已不在当前列表中，请刷新后重新编辑');
    }
    const hidden = knownIds.filter((id) => requested.has(id));
    account.hiddenCourseIds = [...hiddenOutsideTerm, ...hidden];
    try {
      if (this.#auth.scope?.('courses')) this.#persist();
    } catch (error) {
      account.hiddenCourseIds = previous;
      throw error;
    }
    return { hiddenIds: hidden };
  }

  async #courseContext(rawId) {
    const id = requireId(rawId, '课程 ID');
    const courses = await this.#courses();
    const course = courses.find((item) => String(item.id) === id);
    if (!course) throw notFound('当前学期的课程列表中找不到该课程');
    return { id, course, courses };
  }

  #assignments(course, refresh = false) {
    return this.#cached('courses', `assignments/${course.id}`, refresh, async () => {
      const api = await this.#auth.getApi();
      const activities = await api.homeworkActivities(course.id);
      return activities.map((activity) => normAssignment(activity, course));
    });
  }

  #sessions(month, refresh = false) {
    return this.#cached('classroom', `sessions/${month}`, refresh, () =>
      this.#auth.withClassroom((client) => client.sessions(month)));
  }

  #courseState(create = false) {
    const owner = this.#auth.scope?.('courses');
    return this.#account(owner ?? this.#sessionKey(), Boolean(owner), create);
  }

  #observeMaterials(course, attachments) {
    const account = this.#courseState(true);
    if (!account) return;
    const ids = attachments.map(({ id, activity_id }) => id && `${activity_id}/${id}`);
    const previous = account.knownMaterials[course.id];
    const seen = new Set(previous ?? []);
    const discoveredAt = new Date(this.#now()).toISOString();
    const newItems = previous === undefined ? [] : attachments
      .filter((_file, index) => ids[index] && !seen.has(ids[index]))
      .map((file) => ({ ...file, course_name: course.name, discoveredAt }));
    const known = [...new Set([...seen, ...ids.filter(Boolean)])];
    if (previous && known.length === previous.length) return;
    account.knownMaterials[course.id] = known;
    account.recentMaterials = [...newItems, ...account.recentMaterials].slice(0, 100);
    if (this.#auth.scope?.('courses')) this.#persist();
  }

  async #inventory(course, refresh = false) {
    return this.#cached('courses', `inventory/${course.id}`, refresh, async () => {
      const owner = this.#sessionKey();
      const api = await this.#auth.getApi();
      const result = await listLearningAttachments(api, String(course.id));
      this.#checkOwner('courses', owner);
      if (!result.partial) {
        try {
          this.#observeMaterials(course, result.attachments);
        } catch (error) {
          result.errors.push(errorInfo(error));
          result.partial = true;
        }
      }
      return result;
    });
  }

  async #overview(refresh) {
    const courseOwner = this.#sessionKey();
    const classroomOwner = this.#sessionKey('classroom');
    const now = this.#now();
    const deadlines = [];
    const errors = [];
    const earliest = dayAt(now - 7 * DAY);
    const today = dayAt(now);
    const latest = dayAt(now + 7 * DAY);
    const months = [...new Set([earliest, today, latest].map((day) => day.slice(0, 7)))];
    const classroomResults = collect(months, 2, (month) => this.#sessions(month, refresh));
    const [coursesResult] = await Promise.allSettled([this.#courses(refresh)]);

    if (coursesResult.status === 'fulfilled') {
      const courses = coursesResult.value;
      const assignmentLists = await collect(courses, 4, (course) => this.#assignments(course, refresh));
      for (let index = 0; index < assignmentLists.length; index++) {
        const result = assignmentLists[index];
        if (result.status === 'rejected') {
          errors.push({ source: 'learning', course_id: courses[index].id, ...errorInfo(result.reason) });
          continue;
        }
        for (const assignment of result.value) {
          const due = Date.parse(assignment.due_at);
          if (assignment.submitted !== true && due >= now && due <= now + 7 * DAY) deadlines.push(assignment);
        }
      }
    } else {
      errors.push({ source: 'learning', ...errorInfo(coursesResult.reason) });
    }
    deadlines.sort((a, b) => Date.parse(a.due_at) - Date.parse(b.due_at));

    const classroom = new Map();
    for (const result of await classroomResults) {
      if (result.status === 'rejected') {
        const info = errorInfo(result.reason);
        if (!errors.some((error) => error.source === 'classroom' && error.code === info.code)) {
          errors.push({ source: 'classroom', ...info });
        }
        continue;
      }
      for (const session of result.value) {
        if (session.date && session.date >= earliest && session.date <= latest) {
          classroom.set(`${session.course_id}/${session.session_id}`, session);
        }
      }
    }
    const upcoming = [...classroom.values()].filter((session) => session.date >= today)
      .sort((a, b) => a.date.localeCompare(b.date));
    const recent = [...classroom.values()].filter((session) => session.date < today)
      .sort((a, b) => b.date.localeCompare(a.date));
    this.#checkOwner('courses', courseOwner);
    this.#checkOwner('classroom', classroomOwner);
    const currentCourses = new Set((coursesResult.value ?? []).map((course) => String(course.id)));
    const newMaterials = (this.#courseState()?.recentMaterials ?? [])
      .filter((file) => currentCourses.has(String(file.course_id)) && Date.parse(file.discoveredAt) >= now - 7 * DAY);
    return { deadlines, upcoming, recent, newMaterials, errors, refreshedAt: new Date(now).toISOString() };
  }

  #linkState(create = false) {
    const owner = this.#auth.scope?.('courses');
    const classroom = this.#auth.scope?.('classroom');
    const courseKey = this.#sessionKey();
    const classroomKey = this.#sessionKey('classroom');
    const key = courseKey && classroomKey ? `${courseKey}/${classroomKey}` : null;
    return {
      account: this.#account(key, Boolean(owner && classroom), create),
      persistent: Boolean(owner && classroom),
    };
  }

  #linkedClassroom(context, sessions) {
    const { account } = this.#linkState();
    if (account && Object.hasOwn(account.links, context.id)) return account.links[context.id];
    return automaticLink(context.course, sessions, context.courses);
  }

  async #courseDetail({ courseId, refresh, month: requestedMonth }) {
    const courseOwner = this.#sessionKey();
    const classroomOwner = this.#sessionKey('classroom');
    const context = await this.#courseContext(courseId);
    const month = requireMonth(requestedMonth, this.#now());
    const [assignments, inventory, classroom, info] = await Promise.allSettled([
      this.#assignments(context.course, refresh),
      this.#inventory(context.course, refresh),
      this.#sessions(month, refresh),
      this.#cached('courses', `detail/${context.id}`, refresh, async () => {
        const api = await this.#auth.getApi();
        const { raw, ...detail } = await api.courseDetail(context.id);
        return detail;
      }),
    ]);
    const errors = [];
    for (const [source, result] of Object.entries({ assignments, materials: inventory, classroom, course: info })) {
      if (result.status === 'rejected') errors.push({ source, ...errorInfo(result.reason) });
    }
    if (inventory.value?.errors.length) {
      errors.push(...inventory.value.errors.map((error) => ({ ...error, source: 'materials' })));
    }
    const course = { ...context.course };
    for (const [key, value] of Object.entries(info.value ?? {})) {
      if (value !== undefined && value !== null) course[key] = value;
    }
    const sessions = classroom.value ?? [];
    this.#checkOwner('courses', courseOwner);
    this.#checkOwner('classroom', classroomOwner);
    const link = this.#linkedClassroom({ ...context, course }, sessions);
    return {
      course,
      assignments: assignments.value ?? [],
      materials: inventory.value?.attachments ?? [],
      classroom: {
        link,
        sessions: link ? sessions.filter((session) => session.course_id === link.classroomCourseId) : [],
        choices: courseChoices(sessions, course),
        month,
      },
      errors,
    };
  }

  async #assignmentDetail({ courseId, assignmentId }) {
    const { id, course } = await this.#courseContext(courseId);
    const aid = requireId(assignmentId, '作业 ID');
    return this.#cached('courses', `assignment/${id}/${aid}`, false, async () => {
      const assignments = await this.#assignments(course);
      if (!assignments.some((assignment) => String(assignment.id) === aid)) {
        throw notFound('此作业不在所选课程中');
      }
      const api = await this.#auth.getApi();
      const raw = await api.homeworkDetail(id, aid);
      const activity = raw?.homework_activity ?? raw?.activity ?? raw;
      const { description_html, attachments: _attachments, ...assignment } = normAssignmentDetail(activity, course);
      const attachments = learningActivityAttachments(activity, id, 'homework');
      return { assignment, attachments };
    });
  }

  async #changeLink({ courseId, classroomCourseId, month }) {
    const courseOwner = this.#sessionKey();
    const classroomOwner = this.#sessionKey('classroom');
    const { id } = await this.#courseContext(courseId);
    const classroomId = classroomCourseId === null ? null : requireId(classroomCourseId, '智云课程 ID');
    if (classroomId) {
      const sessions = await this.#sessions(requireMonth(month, this.#now()));
      if (!sessions.some((session) => session.course_id === classroomId)) {
        throw notFound('该智云课程不在所选月份的课堂列表中');
      }
    }
    this.#checkOwner('courses', courseOwner);
    this.#checkOwner('classroom', classroomOwner);
    const { account, persistent } = this.#linkState(true);
    if (!account) throw new ApiError('请先登录两个平台', { code: 'AUTH_REQUIRED' });
    const link = classroomId ? { classroomCourseId: classroomId, mode: 'manual' } : null;
    const previous = account.links[id];
    account.links[id] = link;
    try {
      if (persistent) this.#persist();
    } catch (error) {
      if (previous === undefined) delete account.links[id];
      else account.links[id] = previous;
      throw error;
    }
    return { link };
  }

  #directory(override) {
    const directory = override ?? this.#getDownloadDirectory?.() ?? this.#defaultDownloadDir;
    if (typeof directory !== 'string' || !path.isAbsolute(directory) || directory.includes('\0')) {
      throw badInput('下载目录应是有效的绝对路径');
    }
    return path.resolve(directory);
  }

  #owns(job) {
    return Boolean(job.owner && this.#sessionKey(job.source === 'learning' ? 'courses' : 'classroom') === job.owner);
  }

  #jobSnapshot() {
    return {
      jobs: this.#jobs.filter((job) => this.#owns(job)).map(({ id, source, title, status, result, error, createdAt }) =>
        ({ id, source, title, status, result, error, createdAt })),
      paused: this.#manualPaused || this.#quietPaused,
    };
  }

  #changed() {
    const event = { type: 'download.changed', ...this.#jobSnapshot() };
    for (const listener of this.#listeners) {
      try { listener(event); } catch {}
    }
  }

  async #enqueue(payload) {
    const { source, courseId, attachmentIds, assignmentId, sessionId, month } = payload;
    const service = source === 'learning' ? 'courses' : 'classroom';
    const previousOwner = this.#sessionKey(service);
    const directory = this.#directory(payload.directory);
    let inputs;
    if (source === 'learning') {
      const context = await this.#courseContext(courseId);
      const attachments = assignmentId !== undefined
        ? (await this.#assignmentDetail({ courseId, assignmentId })).attachments
        : (await this.#inventory(context.course)).attachments;
      if (attachmentIds !== undefined && (!Array.isArray(attachmentIds) || attachmentIds.length > MAX_QUEUE)) {
        throw badInput('附件列表格式无效或超过单批上限');
      }
      const ids = attachmentIds?.map((id) => requireId(id, '附件 ID'));
      const selected = ids ? attachments.filter((file) => ids.includes(String(file.id))) : attachments;
      if (ids?.some((id) => !selected.some((file) => String(file.id) === id))) {
        throw notFound('附件不在当前课件列表中，请刷新');
      }
      inputs = selected.map((attachment) => ({
        title: attachment.name ?? `附件 ${attachment.id}`,
        key: `${attachment.course_id}/${attachment.activity_id}/${attachment.id}`,
        attachment,
      }));
    } else if (source === 'classroom') {
      const cid = requireId(courseId, '智云课程 ID');
      const sid = requireId(sessionId, '课堂场次 ID');
      const sessions = await this.#sessions(requireMonth(month, this.#now()));
      const session = sessions.find((item) => item.course_id === cid && item.session_id === sid);
      if (!session) throw notFound('所选月份的课堂列表中找不到此场次');
      inputs = [{
        title: [session.course_name, session.session_name, session.date].filter(Boolean).join(' ') || `智云课堂 ${sid}`,
        key: `${cid}/${sid}`,
        session,
      }];
    } else {
      throw badInput('下载来源无效');
    }
    if (!inputs.length) throw notFound('当前没有可加入队列的附件');

    this.#checkOwner(service, previousOwner);
    const owner = this.#sessionKey(service);
    if (!owner) throw new ApiError('此平台的会话已失效，请重新登录', { code: 'AUTH_REQUIRED' });
    const view = this.#jobs.filter((job) => this.#owns(job));
    const existing = view.filter(({ status }) => status === 'queued' || status === 'running');
    const deduped = new Map(inputs.map((item) => [item.key, item]));
    const jobs = [...deduped.values()].filter(({ key }) =>
      !existing.some((job) => job.source === source && job.key === key && job.directory === directory));
    if (existing.length + jobs.length > MAX_QUEUE) throw badInput('下载队列超过上限，请先完成部分任务');
    // Keep recent completed and failed jobs visible without retaining unlimited metadata.
    const older = this.#jobs.filter((job) => !['queued', 'running'].includes(job.status));
    const retained = new Set(older.slice(-MAX_QUEUE).map(({ id }) => id));
    this.#jobs = this.#jobs.filter((job) => ['queued', 'running'].includes(job.status) || retained.has(job.id));
    for (const job of jobs) {
      this.#jobs.push({
        ...job, id: randomUUID(), directory, source, owner, status: 'queued', result: null, error: null,
        createdAt: new Date(this.#now()).toISOString(),
      });
    }
    this.#pump();
    this.#changed();
    return this.#jobSnapshot();
  }

  #pump() {
    if (this.#active || this.#manualPaused || this.#quietPaused) return;
    const job = this.#jobs.find((item) => item.status === 'queued' && this.#owns(item));
    if (!job) return;
    job.status = 'running';
    this.#active = this.#runJob(job).finally(() => {
      this.#active = null;
      this.#changed();
      this.#pump();
    });
    this.#changed();
  }

  async #runJob(job) {
    try {
      if (job.source === 'learning') {
        const api = await this.#auth.getApi();
        this.#checkOwner('courses', job.owner);
        const result = await downloadLearningAttachments(api, [job.attachment], job.directory, { groupByActivity: false });
        if (result.failed.length) {
          const { reason, message } = result.failed[0];
          throw new ApiError(message, { code: reason });
        }
        const { path: filePath, size, format } = result.downloaded[0];
        job.result = { path: filePath, size, format };
      } else {
        const destination = await classroomDestination(job.directory, job.title);
        const { course_id, session_id } = job.session;
        const result = await this.#auth.withClassroom((client) => {
          this.#checkOwner('classroom', job.owner);
          return client.exportPdf(course_id, session_id, destination);
        });
        job.result = { path: result.path, size: result.bytes, format: 'classroom_pdf', page_count: result.page_count };
      }
      job.status = 'completed';
    } catch (error) {
      job.status = 'failed';
      job.error = errorInfo(error);
    }
  }

  /** Allow a current download to finish and hold pending jobs until resume. */
  async pause() {
    this.#quietPaused = true;
    this.#changed();
    await this.#active;
  }

  hasActiveJob() {
    return this.#active !== null;
  }

  resume() {
    this.#quietPaused = false;
    this.#pump();
    this.#changed();
  }

  fileForJob(id) {
    const job = this.#jobs.find((item) => item.id === id && item.status === 'completed' && this.#owns(item));
    return job?.result.path ?? null;
  }

  async handle(method, payload = {}) {
    if (!payload || typeof payload !== 'object' || Array.isArray(payload)) throw badInput('请求参数无效');
    switch (method) {
      case 'overview.load': return this.#overview(payload.refresh === true);
      case 'courses.list': return this.#listCourses(payload);
      case 'courses.visibility': return this.#setCourseVisibility(payload);
      case 'course.detail': return this.#courseDetail(payload);
      case 'assignment.detail': return this.#assignmentDetail(payload);
      case 'classroom.list': {
        const month = requireMonth(payload.month, this.#now());
        return { month, sessions: await this.#sessions(month, payload.refresh === true) };
      }
      case 'classroom.link': return this.#changeLink(payload);
      case 'download.enqueue': return this.#enqueue(payload);
      case 'download.jobs': return this.#jobSnapshot();
      case 'download.pause':
        if (typeof payload.paused !== 'boolean') throw badInput('暂停状态需要是布尔值');
        this.#manualPaused = payload.paused;
        this.#pump();
        this.#changed();
        return this.#jobSnapshot();
      case 'download.retry': {
        const job = this.#jobs.find((item) => item.id === payload.jobId && this.#owns(item));
        if (!job || job.status !== 'failed') throw notFound('找不到可重试的下载任务');
        job.status = 'queued';
        job.error = null;
        this.#pump();
        this.#changed();
        return this.#jobSnapshot();
      }
      default: throw badInput('未知的桌面数据操作');
    }
  }
}

import {
  DEFAULT_DESKTOP, clamp, isHex, normalizeDesktop, normalizeLook, normalizeShortcut, applyLook,
} from './appearance.js';
import {
  hero, loadState, overview, coursesView, courseEditorView, courseView, assignmentView, sessionView,
  downloadsView, settingsView, appearanceView, authView,
} from './views.js';

const root = document.getElementById('xdc-glass');
const panel = root.querySelector('[data-window]');
const main = root.querySelector('[data-content]');
const notice = root.querySelector('[data-notice]');
const desktop = window.xdc;
const store = new Map();
let route = { name: 'overview' };
let history = [];
let prefs = normalizeDesktop();
let saved = normalizeDesktop();
let toastTimer;
let previewVersion = 0;
let previewTask = Promise.resolve();
let saving = false;
let authBusy = false;
let auth;
let queuedJobs;
let jobUpdating = false;
let jobEventVersion = 0;
let courseEditor;

async function invoke(method, payload) {
  if (!desktop?.invoke) throw new Error('桌面服务未启动');
  const result = await desktop.invoke(method, payload);
  if (result?.ok === false) {
    const error = new Error(result.error?.message || '操作失败');
    error.code = result.error?.code;
    throw error;
  }
  return result;
}

function toast(message) {
  clearTimeout(toastTimer);
  notice.textContent = message;
  notice.hidden = false;
  toastTimer = setTimeout(() => { notice.hidden = true; }, 4200);
}

function attributes(selector, name, value) {
  const element = root.querySelector(selector);
  if (element) element[name] = value;
}

function resourceKey() {
  if (route.name === 'courses' && courseEditor) return 'course-editor';
  if (route.name === 'course') return 'course:' + route.courseId;
  if (route.name === 'assignment') return 'assignment:' + route.courseId + ':' + route.assignmentId;
  return route.name;
}

function focusAfterRender(action, preference) {
  if (!action && !preference) return;
  const controls = [...main.querySelectorAll('button,input,select')];
  controls.find(el => (!el.disabled && (action ? el.dataset.action === action : el.dataset.pref === preference)))
    ?.focus({ preventScroll: true });
}

function appearanceChanged() {
  return JSON.stringify(prefs.look) !== JSON.stringify(saved.look) ||
    JSON.stringify(prefs.geometry) !== JSON.stringify(saved.geometry) ||
    prefs.shortcut !== saved.shortcut || prefs.locked !== saved.locked;
}

function render() {
  const key = resourceKey();
  const current = store.get(key);
  const data = current?.data;
  const active = main.contains(document.activeElement) ? document.activeElement : null;
  const status = loadState(current, '内容');
  const progress = current?.loading && !data;
  const pages = {
    overview: () => data ? overview(data, auth) : hero('学习总览'),
    courses: () => courseEditor ? courseEditorView(data, courseEditor, current?.loading) :
      data ? coursesView(data) : hero('我的课程'),
    course: () => data ? courseView(data, route.tab) : hero('课程详情'),
    assignment: () => data ? assignmentView(data) : hero('作业详情'),
    session: () => sessionView(route.session),
    downloads: () => downloadsView(queuedJobs ?? data ?? {}),
    settings: () => settingsView(prefs, auth),
    appearance: () => appearanceView(prefs, route.tab, !saving && appearanceChanged()),
    auth: () => authView(auth ?? data, authBusy),
  };
  panel.classList.toggle('unlocked', !prefs.locked);
  attributes('[data-action=back]', 'hidden', !history.length && !courseEditor);
  attributes('[data-action=back]', 'disabled', Boolean(courseEditor?.saving));
  attributes('[data-action=refresh]', 'hidden', ['appearance', 'settings', 'session'].includes(route.name));
  attributes('[data-action=refresh]', 'disabled', Boolean(current?.loading || authBusy || courseEditor?.saving));
  const navPage = ['overview', 'courses', 'downloads'].includes(route.name) ? route.name :
    ['course', 'assignment'].includes(route.name) ? 'courses' : '';
  for (const button of root.querySelectorAll('.dock [data-nav]')) {
    if (button.dataset.nav === navPage) button.setAttribute('aria-current', 'page');
    else button.removeAttribute('aria-current');
  }
  main.innerHTML = progress ? pages[route.name]() + status : status + pages[route.name]();
  focusAfterRender(active?.dataset.action, active?.dataset.pref);
}

async function load(key, method, payload, refresh = false) {
  const old = store.get(key);
  if (old?.loading || (old?.data && !refresh)) return;
  const slot = { data: old?.data, loading: true };
  const currentJobEvents = jobEventVersion;
  store.set(key, slot);
  if (key === resourceKey()) render();
  try {
    const result = await invoke(method, payload);
    if (store.get(key) !== slot) return;
    if (method === 'auth.status') auth = result;
    if (method === 'download.jobs' && currentJobEvents === jobEventVersion) queuedJobs = result;
    slot.data = result;
  } catch (error) {
    if (store.get(key) === slot) slot.error = error;
  } finally {
    slot.loading = false;
    if (key === resourceKey() || (method === 'auth.status' && route.name === 'overview')) render();
  }
}

function loadPage(refresh = false) {
  const key = resourceKey();
  const { name, courseId, assignmentId } = route;
  if (name === 'overview') return load(key, 'overview.load', { refresh }, refresh);
  if (name === 'courses') return load(key, 'courses.list', courseEditor ? { refresh, includeHidden: true } : { refresh }, refresh);
  if (name === 'course') return load(key, 'course.detail', { courseId, refresh }, refresh);
  if (name === 'assignment') return load(key, 'assignment.detail', { courseId, assignmentId }, refresh);
  if (name === 'downloads') return load(key, 'download.jobs', undefined, refresh);
  if (name === 'auth') return load(key, 'auth.status', undefined, refresh);
}

function navigate(next, behavior = 'push') {
  discardCourseEditor();
  if (behavior === 'push') history.push(route);
  else if (behavior === 'root') history = [];
  route = next;
  main.scrollTop = 0;
  rememberView();
  render();
  return loadPage();
}

function rememberView() {
  const { name, courseId, assignmentId, tab } = route;
  if (!['overview', 'courses', 'downloads', 'course', 'assignment'].includes(name)) return;
  void invoke('desktop.rememberView', {
    name, courseId, assignmentId, tab, scroll: courseEditor?.scroll ?? main.scrollTop,
  }).catch(() => {});
}

function openCourseEditor() {
  if (courseEditor || route.name !== 'courses') return;
  courseEditor = { visibility: new Map(), scroll: main.scrollTop, saving: false };
  main.scrollTop = 0;
  const pending = loadPage();
  main.querySelector('[data-action=cancel-course-edit]')?.focus({ preventScroll: true });
  return pending;
}

function discardCourseEditor() {
  courseEditor = undefined;
  store.delete('course-editor');
}

async function finishCourseEditor() {
  const scroll = courseEditor?.scroll ?? 0;
  discardCourseEditor();
  render();
  await loadPage();
  if (route.name === 'courses' && !courseEditor) {
    main.scrollTop = scroll;
    main.querySelector('[data-action=edit-courses]')?.focus({ preventScroll: true });
  }
}

async function saveCourseVisibility() {
  const slot = store.get('course-editor');
  if (!courseEditor || courseEditor.saving || slot?.loading || !slot?.data) return;
  const { courses, hiddenIds } = slot.data;
  const hidden = new Set(hiddenIds);
  const initial = new Set(hidden);
  for (const course of courses) {
    const id = String(course.id);
    const visible = courseEditor.visibility.get(id);
    if (visible === true) hidden.delete(id);
    if (visible === false) hidden.add(id);
  }
  if (hidden.size === initial.size && [...hidden].every(id => initial.has(id))) return finishCourseEditor();

  courseEditor.saving = true;
  courseEditor.error = undefined;
  render();
  try {
    await invoke('courses.visibility', { hiddenIds: [...hidden] });
    store.delete('courses');
  } catch (error) {
    courseEditor.saving = false;
    courseEditor.error = error.message || '课程显示设置未保存';
    render();
    return;
  }
  return finishCourseEditor();
}

function updateAppearanceFeedback() {
  const button = main.querySelector('[data-action=save-ui]');
  if (!button) return;
  button.disabled = saving || !appearanceChanged();
  main.querySelector('[data-changes]').textContent = saving ? '正在保存' : appearanceChanged() ? '未保存' : '';
}

function refreshGeometryText() {
  const dims = main.querySelector('[data-dimensions]');
  if (dims) dims.textContent = prefs.geometry.width + ' × ' + prefs.geometry.height;
  updateAppearanceFeedback();
}

function reflectPreferences() {
  applyLook(root, prefs.look);
  panel.classList.toggle('unlocked', !prefs.locked);
  refreshGeometryText();
}

function previewGeometry() {
  const version = ++previewVersion;
  const request = { geometry: { ...prefs.geometry }, locked: prefs.locked, radius: prefs.look.radius };
  previewTask = previewTask.catch(() => {}).then(async () => {
    if (version !== previewVersion) return;
    try {
      const result = await invoke('desktop.preview', request);
      if (version !== previewVersion || !result) return;
      prefs.geometry = { ...prefs.geometry, ...result.geometry };
      prefs.locked = result.locked;
      reflectPreferences();
    } catch (error) {
      if (version === previewVersion) toast(error.message || '暂时无法调整窗口');
    }
  });
  return previewTask;
}

async function commitAppearance() {
  if (saving || !appearanceChanged()) return;
  saving = true;
  updateAppearanceFeedback();
  try {
    await previewTask;
    const snapshot = normalizeDesktop({
      ...saved, look: prefs.look, geometry: prefs.geometry, shortcut: prefs.shortcut, locked: prefs.locked,
    });
    const result = await invoke('desktop.save', snapshot);
    const unchanged = JSON.stringify(prefs.look) === JSON.stringify(snapshot.look) &&
      JSON.stringify(prefs.geometry) === JSON.stringify(snapshot.geometry) &&
      prefs.shortcut === snapshot.shortcut && prefs.locked === snapshot.locked;
    saved = normalizeDesktop(result ?? snapshot);
    if (unchanged) prefs = normalizeDesktop(saved);
    reflectPreferences();
    toast('已保存外观和布局');
  } catch (error) {
    toast(error.message || '暂时无法保存设置');
  } finally {
    saving = false;
    updateAppearanceFeedback();
  }
}

async function saveGeneral(changes) {
  const snapshot = normalizeDesktop({ ...saved, ...changes });
  const result = await invoke('desktop.save', snapshot);
  saved = normalizeDesktop(result ?? snapshot);
  for (const key of Object.keys(changes)) prefs[key] = saved[key];
  toast('设置已保存');
  if (route.name === 'settings') render();
}

function resetAppearance() {
  prefs = normalizeDesktop({
    ...prefs,
    look: DEFAULT_DESKTOP.look,
    geometry: { ...prefs.geometry, width: DEFAULT_DESKTOP.geometry.width, height: DEFAULT_DESKTOP.geometry.height },
    shortcut: DEFAULT_DESKTOP.shortcut,
    locked: DEFAULT_DESKTOP.locked,
  });
  reflectPreferences();
  render();
  return previewGeometry();
}

function asJobPayload(scope, id) {
  if (scope === 'classroom') return {
    source: 'classroom', courseId: String(route.session.course_id), sessionId: String(route.session.session_id),
    month: route.session.date?.slice(0, 7),
  };
  return {
    source: 'learning', courseId: String(route.courseId),
    attachmentIds: [String(id)],
    ...(scope === 'assignment' ? { assignmentId: String(route.assignmentId) } : {}),
  };
}

async function enqueue(scope, id, alternate) {
  const payload = asJobPayload(scope, id);
  if (alternate) {
    const directory = await invoke('desktop.chooseFolder');
    if (!directory) return;
    payload.directory = directory;
  }
  await invoke('download.enqueue', payload);
}

async function updateQueue(method, payload) {
  if (jobUpdating) return;
  jobUpdating = true;
  try {
    queuedJobs = await invoke(method, payload);
  } finally {
    jobUpdating = false;
    if (route.name === 'downloads') render();
  }
}

async function linkClassroom(classroomCourseId) {
  const courseId = route.courseId;
  await invoke('classroom.link', { courseId, classroomCourseId });
  toast(classroomCourseId ? '已关联智云课程' : '已解除关联');
  return load('course:' + courseId, 'course.detail', { courseId, refresh: true }, true);
}

async function runAuth(method, payload) {
  if (authBusy) return;
  authBusy = true;
  if (route.name === 'auth') render();
  try {
    auth = await invoke(method, payload);
    store.clear();
    queuedJobs = undefined;
    store.set('auth', { data: auth, loading: false });
    if (method === 'auth.logout') toast('已退出登录');
    else if (method === 'auth.forget') toast('已清除记住的账号');
    else if (auth.courses?.authenticated || auth.classroom?.authenticated) toast('已连接浙大服务');
  } finally {
    authBusy = false;
    if (route.name === 'auth') render();
    else await loadPage(true);
  }
}

async function runAction(action) {
  const [name, value, third] = action.split(':');
  if (name === 'back') return courseEditor ? finishCourseEditor() :
    navigate(history.pop() ?? { name: 'overview' }, 'replace');
  if (name === 'refresh') return loadPage(true);
  if (name === 'settings') return route.name === 'settings'
    ? navigate(history.pop() ?? { name: 'overview' }, 'replace') : navigate({ name: 'settings' });
  if (name === 'course') return navigate({ name: 'course', courseId: value, tab: third || 'summary' });
  if (name === 'edit-courses') return openCourseEditor();
  if (name === 'cancel-course-edit') return finishCourseEditor();
  if (name === 'finish-course-edit') return saveCourseVisibility();
  if (name === 'tab') return navigate({ ...route, tab: value }, 'replace');
  if (name === 'assignment') return navigate({ name: 'assignment', courseId: value, assignmentId: third });
  if (name === 'overview-session' || name === 'recent-session' || name === 'course-session') {
    const data = store.get(resourceKey())?.data;
    const sessions = name === 'course-session' ? data?.classroom?.sessions :
      name === 'recent-session' ? data?.recent : data?.upcoming;
    const session = sessions?.find(item => String(item.session_id) === value);
    if (session) return navigate({ name: 'session', session });
    return;
  }
  if (name === 'link') return linkClassroom(value);
  if (name === 'unlink') return linkClassroom(null);
  if (name === 'queue' || name === 'save') return enqueue(value, third, name === 'save');
  if (name === 'pause-downloads') return updateQueue('download.pause', { paused: !queuedJobs?.paused });
  if (name === 'retry-job') return updateQueue('download.retry', { jobId: value });
  if (name === 'reveal') return invoke('desktop.showFile', { id: value });
  if (name === 'close') return invoke('desktop.close');
  if (name === 'auth') return navigate({ name: 'auth' });
  if (name === 'login-saved') return runAuth('auth.loginSaved');
  if (name === 'forget') return runAuth('auth.forget');
  if (name === 'logout') return runAuth('auth.logout');
  if (name === 'official') return invoke('desktop.openOfficial', { service: value === 'courses' ? 'learning' : value });
  if (name === 'appearance') return navigate({ name: 'appearance', tab: 'look' });
  if (name === 'settings-tab') return navigate({ ...route, tab: value }, 'replace');
  if (name === 'save-ui') return commitAppearance();
  if (name === 'reset-ui') return resetAppearance();
  if (name === 'lock') {
    prefs.locked = !prefs.locked;
    render();
    return previewGeometry();
  }
  if (name === 'download-folder') {
    const downloadDirectory = await invoke('desktop.chooseFolder');
    if (downloadDirectory) return saveGeneral({ downloadDirectory });
  }
}

function onLookInput(input) {
  const key = input.dataset.look;
  if (key) {
    prefs.look[key] = normalizeLook(key, input.type === 'range' ? input.valueAsNumber : input.value);
    if (key === 'accent') attributes('[data-hex]', 'value', prefs.look.accent.toUpperCase());
    const output = main.querySelector('[data-value=' + key + ']');
    if (output) output.textContent = prefs.look[key] + output.dataset.unit;
    reflectPreferences();
    if (key === 'radius') previewGeometry();
  } else if (input.hasAttribute('data-hex') && isHex(input.value)) {
    prefs.look.accent = input.value.toLowerCase();
    attributes('[data-look=accent]', 'value', prefs.look.accent);
    reflectPreferences();
  }
}

function onGeometryInput(input) {
  const key = input.dataset.geometry;
  if (!['width', 'height'].includes(key)) return false;
  const [min, max] = key === 'width' ? [320, 620] : [520, 820];
  if (Number.isFinite(input.valueAsNumber)) prefs.geometry[key] = clamp(Math.round(input.valueAsNumber), min, max);
  input.value = String(prefs.geometry[key]);
  reflectPreferences();
  previewGeometry();
  return true;
}

root.addEventListener('click', async (event) => {
  const button = event.target.closest('button');
  if (!button || courseEditor?.saving) return;
  if (button.dataset.nav) {
    await navigate({ name: button.dataset.nav }, 'root');
    return;
  }
  if (!button.dataset.action || button.getAttribute('aria-busy') === 'true') return;
  button.setAttribute('aria-busy', 'true');
  try {
    await runAction(button.dataset.action);
  } catch (error) {
    toast(error.message || '操作失败');
  } finally {
    button.removeAttribute('aria-busy');
  }
});

root.addEventListener('input', event => onLookInput(event.target));
root.addEventListener('change', async event => {
  const input = event.target;
  if (input.dataset.courseVisibility) {
    courseEditor?.visibility.set(input.dataset.courseVisibility, input.checked);
    return;
  }
  if (onGeometryInput(input)) return;
  if (input.hasAttribute('data-hex')) input.value = prefs.look.accent.toUpperCase();
  if (input.hasAttribute('data-shortcut')) {
    const shortcut = normalizeShortcut(input.value);
    if (shortcut) prefs.shortcut = shortcut;
    else toast('请输入组合键，例如 Ctrl+Shift+L');
    input.value = prefs.shortcut;
    reflectPreferences();
  }
  if (input.dataset.pref === 'startup') {
    try {
      await saveGeneral({ startup: input.checked });
    } catch (error) {
      input.checked = prefs.startup;
      toast(error.message || '暂时无法修改开机启动');
    }
  }
});

root.addEventListener('submit', async event => {
  const form = event.target;
  if (!form.dataset.form) return;
  event.preventDefault();
  if (authBusy) return;
  const entries = new FormData(form);
  const login = form.dataset.form === 'login';
  const payload = login ? {
    username: entries.get('username'), password: entries.get('password'), remember: entries.has('remember'),
  } : { service: entries.get('service'), cookie: entries.get('cookie') };
  form.reset();
  try {
    await runAuth(login ? 'auth.login' : 'auth.importCookie', payload);
  } catch (error) {
    toast(error.message || '认证未完成');
  }
});

let viewTimer;
main.addEventListener('scroll', () => {
  panel.classList.toggle('scrolled', main.scrollTop > 8);
  clearTimeout(viewTimer);
  viewTimer = setTimeout(rememberView, 180);
});

desktop?.onEvent?.(event => {
  if (event.type === 'download.changed') {
    jobEventVersion++;
    const previous = new Map(queuedJobs?.jobs.map(job => [job.id, job.status]));
    queuedJobs = { jobs: event.jobs, paused: event.paused };
    if (route.name === 'downloads') render();
    const finished = event.jobs.findLast(job =>
      ['queued', 'running'].includes(previous.get(job.id)) && ['completed', 'failed'].includes(job.status));
    if (finished?.status === 'failed') {
      toast(finished.error?.message || '下载失败，请到下载记录查看');
    } else if (finished?.status === 'completed') {
      toast(finished.result?.page_count ? `已导出 ${finished.result.page_count} 页 PDF` : '下载完成');
    } else if (event.jobs.some(job => !previous.has(job.id) && ['queued', 'running'].includes(job.status))) {
      toast(event.paused ? '已加入队列，下载已暂停' : '已加入队列，可在下载记录查看进度');
    }
    return;
  }
  if (event.type === 'desktop.geometry') {
    prefs.geometry = { ...prefs.geometry, ...event.geometry };
    refreshGeometryText();
  } else if (event.type === 'desktop.locked') {
    prefs.locked = event.locked;
    if (event.geometry) prefs.geometry = { ...prefs.geometry, ...event.geometry };
    reflectPreferences();
    if (route.name === 'appearance') render();
  }
});

async function start() {
  let scroll = 0;
  reflectPreferences();
  render();
  try {
    saved = normalizeDesktop(await invoke('desktop.get'));
    prefs = normalizeDesktop(saved);
    if (saved.resume?.name) {
      const { scroll: position, ...next } = saved.resume;
      route = next;
      scroll = position;
      render();
    }
    reflectPreferences();
  } catch (error) {
    toast(error.message || '无法读取桌面配置');
  }
  await Promise.all([loadPage(), load('auth', 'auth.status')]);
  main.scrollTop = scroll;
  rememberView();
  setInterval(() => {
    void load('overview', 'overview.load', { refresh: true }, true);
  }, 30 * 60 * 1000);
}

start();

import { escapeHtml as e, options, ranges, SWATCHES } from './appearance.js';

const dateFormat = new Intl.DateTimeFormat('zh-CN', {
  timeZone: 'Asia/Shanghai', month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit',
});
const dayFormat = new Intl.DateTimeFormat('zh-CN', {
  timeZone: 'Asia/Shanghai', month: 'long', day: 'numeric', weekday: 'long',
});
const formats = { original: '原文件', preview_pdf: '预览 PDF', classroom_pdf: '课堂 PDF' };
const jobStates = { queued: '等待中', running: '正在处理', completed: '已完成', failed: '失败' };
const platforms = { courses: '学在浙大', classroom: '智云课堂' };

export const formatDate = (value) => value && Number.isFinite(Date.parse(value)) ? dateFormat.format(new Date(value)) : '';
const list = rows => '<div class="list">' + rows + '</div>';
const button = (label, action, primary = false) => '<button type="button" class="press' +
  (primary ? ' primary' : '') + '" data-action="' + e(action) + '">' + e(label) + '</button>';

export function hero(title, detail = '') {
  return '<div class="hero"><h2>' + e(title) + '</h2>' +
    (detail ? '<p class="eyebrow">' + e(detail) + '</p>' : '') + '</div>';
}

export function group(title, detail, body) {
  return '<section class="section"><div class="section-head"><h3>' + e(title) + '</h3>' +
    (detail ? '<span class="subline">' + e(detail) + '</span>' : '') + '</div>' + body + '</section>';
}

function row(title, detail, action, trail = '›') {
  return '<button class="row" type="button" data-action="' + e(action) + '"><span class="lead"><strong>' +
    e(title) + '</strong>' + (detail ? '<span class="subline">' + e(detail) + '</span>' : '') +
    '</span>' + (trail ? '<span class="trail">' + e(trail) + '</span>' : '') + '</button>';
}

function tabs(items, current, action) {
  return '<div class="tabs" role="group">' + Object.entries(items).map(([key, label]) =>
    '<button type="button" aria-pressed="' + (current === key) + '" data-action="' + action + ':' + key + '">' +
    e(label) + '</button>').join('') + '</div>';
}

function empty(message) {
  return '<p class="subline panel-note">' + e(message) + '</p>';
}

export function loadState(status, title) {
  if (!status?.loading && !status?.error) return '';
  if (status.loading) return empty('正在更新' + title + '…');
  const error = status.error;
  const signIn = ['AUTH_REQUIRED', 'NO_SESSION', 'CLASSROOM_AUTH_REQUIRED', 'SESSION_NOT_ACCEPTED'].includes(error.code);
  return '<div class="panel-note"><p class="subline error">' + e(error.message || '读取失败') + '</p>' +
    button(signIn ? '查看账号' : '重试', signIn ? 'auth' : 'refresh', true) + '</div>';
}

function errorsFrom(value) {
  const errors = Array.isArray(value) ? value : Object.values(value ?? {}).flat();
  const messages = [...new Set(errors.map(error => error?.message).filter(Boolean))];
  return messages.length ? '<div class="panel-note subline error"><strong>部分内容未更新</strong><ul>' +
    messages.map(message => '<li>' + e(message) + '</li>').join('') + '</ul></div>' : '';
}

function fileSize(size) {
  if (!size || !Number.isFinite(Number(size))) return '';
  if (size >= 1024 ** 2) return (size / (1024 ** 2)).toFixed(1) + ' MB';
  return Math.max(1, Math.round(size / 1024)) + ' KB';
}

function attachment(file, scope = 'materials') {
  const available = file.id != null && (file.downloadable || file.wps_preview);
  const preview = !file.downloadable && file.wps_preview;
  const kind = preview ? '可在线预览' : '';
  const info = [fileSize(file.size), kind].filter(Boolean).join(' · ');
  return '<div class="file"><div class="lead"><strong>' + e(file.name || '附件') + '</strong>' +
    '<span class="subline">' + e(info) + '</span></div><div class="family">' +
    (available ? button(preview ? '尝试导出' : '下载', 'queue:' + scope + ':' + file.id, true) +
      '<button type="button" class="icon-button" aria-label="另存为" title="另存为" data-action="save:' +
      e(scope + ':' + file.id) + '">⋯</button>' : '<span class="subline">暂无下载</span>') +
    '</div></div>';
}

export function overview(data, authStatus) {
  const disconnected = authStatus?.courses?.state === 'signed-out';
  const warning = disconnected ? '<div class="panel-note"><p class="subline">学在浙大尚未登录。</p>' +
    button('登录', 'auth', true) + '</div>' : '';
  const heading = hero('学习总览', dayFormat.format(new Date()));
  if (disconnected && !data.deadlines?.length && !data.newMaterials?.length) return heading + warning;
  return heading + warning + errorsFrom(data.errors) +
    group('未来 7 天', '作业截止', data.deadlines?.length ? list(data.deadlines.map(item =>
      row(item.title || '作业', item.course, 'assignment:' + item.course_id + ':' + item.id, formatDate(item.due_at))).join('')) :
      empty('暂无即将截止的作业')) +
    group('新课件', '学在浙大', data.newMaterials?.length ? list(data.newMaterials.map(file =>
      row(file.name || '附件', file.course_name, 'course:' + file.course_id + ':materials')).join('')) :
      empty('暂无新课件'));
}

export function coursesView(data) {
  return hero('我的课程') + '<div class="actions course-actions">' + button('编辑', 'edit-courses', true) +
    '</div>' + (data.courses?.length ? list(data.courses.map(course =>
    row(course.name || '课程', [course.instructor, course.semester].filter(Boolean).join(' · '), 'course:' + course.id + ':summary')
  ).join('')) : empty('暂无显示的课程'));
}

export function courseEditorView(data, editor, loading) {
  const disabled = editor.saving || loading ? ' disabled' : '';
  const failure = editor.error ? '<p class="subline error course-edit-error" role="alert">' + e(editor.error) + '</p>' : '';
  const toolbar = '<div class="actions course-edit-actions"><button type="button" class="press" data-action="cancel-course-edit"' +
    (editor.saving ? ' disabled' : '') + '>取消</button><button type="button" class="press primary" data-action="finish-course-edit"' +
    (data ? disabled : ' disabled') + '>' + (editor.saving ? '正在保存…' : '完成') + '</button>' + failure + '</div>';
  const heading = hero('编辑课程', '勾选要显示的课程');
  if (!data) return heading + toolbar;
  const hidden = new Set(data.hiddenIds);
  const rows = data.courses.map(course => {
    const id = String(course.id);
    const visible = editor.visibility.get(id) ?? !hidden.has(id);
    const label = '显示 ' + (course.name || '课程');
    return '<label class="row course-visibility"><span class="lead"><strong>' + e(course.name || '课程') +
      '</strong><span class="subline">' + e([course.instructor, course.semester].filter(Boolean).join(' · ')) +
      '</span></span><input type="checkbox" data-course-visibility="' + e(id) + '" aria-label="' + e(label) +
      '"' + (visible ? ' checked' : '') + disabled + '></label>';
  });
  return heading + toolbar + (rows.length ? list(rows.join('')) : empty('暂无课程'));
}

function assignmentRows(data, courseId) {
  return data.length ? list(data.map(item =>
    row(item.title || '作业', item.submitted === true ? '已提交' : (item.overdue ? '已逾期' : ''),
      'assignment:' + courseId + ':' + item.id, formatDate(item.due_at))).join('')) : empty('暂无作业');
}

function classroomRows(data) {
  const link = data?.link;
  if (!link) {
    if (!data?.choices?.length) return empty('未找到可关联的智云课程');
    return group('关联智云课程', '', list(data.choices.map(choice =>
      row(choice.course_name || '课程', choice.lecturer, 'link:' + choice.course_id, '关联')).join('')));
  }
  const sessions = data.sessions ?? [];
  return group('课堂课件', link.mode === 'manual' ? '已手动关联' : '已关联', sessions.length ?
    list(sessions.map(session => row(session.session_name || session.course_name, session.date,
      'course-session:' + session.session_id)).join('')) : empty('本月暂无课堂')) +
    button('解除关联', 'unlink');
}

export function courseView(data, tab) {
  const course = data.course ?? {};
  const assignments = data.assignments ?? [];
  const materials = data.materials ?? [];
  const classroom = data.classroom ?? {};
  const head = hero(course.name || '课程详情', [course.instructor, course.semester].filter(Boolean).join(' · ')) +
    tabs({ summary: '概览', homework: '作业', materials: '资料', lecture: '智云' }, tab, 'tab') + errorsFrom(data.errors);
  if (tab === 'homework') return head + assignmentRows(assignments, course.id);
  if (tab === 'materials') return head + (materials.length ?
    list(materials.map(file => attachment(file)).join('')) : empty('暂无课程资料'));
  if (tab === 'lecture') return head + classroomRows(classroom);
  return head + list(
    row(assignments[0]?.title || '作业', assignments.length + ' 项作业', 'tab:homework') +
    row(materials[0]?.name || '课程资料', materials.length + ' 份资料', 'tab:materials') +
    row(classroom.link ? (classroom.sessions?.[0]?.session_name || '课堂课件') : '关联智云课程', '智云课堂', 'tab:lecture')
  );
}

export function assignmentView(data) {
  const item = data.assignment;
  if (!item) return hero('作业详情');
  const attachments = data.attachments ?? item.attachments ?? [];
  return hero(item.title || '作业详情', [item.course, formatDate(item.due_at)].filter(Boolean).join(' · ')) +
    group('作业要求', '', item.description ? '<p class="description">' + e(item.description) + '</p>' : empty('暂无文字要求')) +
    group('附件', '', attachments.length ? list(attachments.map(file => attachment(file, 'assignment')).join('')) :
      empty('暂无附件'));
}

export function sessionView(session) {
  if (!session) return hero('课堂详情');
  return hero(session.session_name || '课堂详情', [session.course_name, session.date].filter(Boolean).join(' · ')) +
    group('课堂课件', '智云课堂', '<div class="actions">' + button('导出 PDF', 'queue:classroom', true) +
      button('另存为', 'save:classroom') + '</div>');
}

export function downloadsView(data) {
  const jobs = data.jobs ?? [];
  const actions = '<div class="actions">' + button(data.paused ? '继续下载' : '暂停下载', 'pause-downloads') + '</div>';
  return hero('下载记录', data.paused ? '已暂停' : '') + actions + (jobs.length ? list(jobs.map(job => {
    const status = jobStates[job.status] || job.status;
    const info = [status, formats[job.result?.format], job.result?.page_count ? job.result.page_count + ' 页' : '']
      .filter(Boolean).join(' · ');
    const result = job.status === 'completed' ? button('显示位置', 'reveal:' + job.id) :
      job.status === 'failed' ? button('重试', 'retry-job:' + job.id) : '';
    return '<div class="file"><div class="lead"><strong>' + e(job.title || '下载任务') + '</strong>' +
      '<span class="subline">' + e(info) + '</span>' +
      (job.error?.message ? '<span class="subline error">' + e(job.error.message) + '</span>' : '') +
      '</div><div class="family">' + result + '</div></div>';
  }).join('')) : empty('暂无下载'));
}

function accountSummary(auth) {
  if (!auth) return '检查会话';
  const sources = Object.entries(platforms).filter(([key]) => auth[key]?.authenticated).map(([, label]) => label);
  return sources.length ? sources.join('、') + '已连接' : '未连接';
}

export function settingsView(prefs, auth) {
  return hero('设置') + group('桌面', '', list(
    row('外观与布局', '', 'appearance') +
    '<label class="setting-row">登录 Windows 时启动 <input type="checkbox" data-pref="startup"' +
      (prefs.startup ? ' checked' : '') + '></label>'
  )) + group('浙大账号', '', list(row('账号与会话', accountSummary(auth), 'auth'))) + group('下载', '', list(
    '<div class="setting-row"><span class="setting-value">保存位置<span class="subline">' +
      e(prefs.downloadDirectory ? ' · ' + prefs.downloadDirectory : ' · 系统下载文件夹') +
      '</span></span>' + button('更改', 'download-folder') + '</div>' +
    '<div class="setting-row"><span>UU 运行时</span><span class="subline">完成当前文件后暂停</span></div>'
  )) + button('关闭组件', 'close');
}

function selectLook(look, key, label) {
  return '<div class="setting-row"><span>' + e(label) + '</span><div class="seg" role="group" aria-label="' + e(label) + '">' +
    Object.entries(options[key]).map(([value, text]) => '<button type="button" data-look-choice="' + key + ':' + value +
      '" aria-pressed="' + (String(look[key]) === value) + '">' + e(text) + '</button>').join('') + '</div></div>';
}

function rangeLook(look, key, label, unit = '') {
  const [min, max] = ranges[key];
  return '<label class="setting-row"><span>' + e(label) + '</span><span class="setting-control">' +
    '<input type="range" data-look="' + key + '" aria-label="' + e(label) + '" min="' + min + '" max="' + max +
    '" value="' + look[key] + '"><span aria-hidden="true" data-value="' + key + '" data-unit="' + e(unit) + '">' +
    look[key] + e(unit) + '</span></span></label>';
}

export function appearanceView(prefs, tab, canSave) {
  const look = prefs.look;
  const geometry = prefs.geometry;
  const head = hero('外观与布局') + tabs({ look: '外观', layout: '布局' }, tab, 'settings-tab');
  const swatches = SWATCHES.map(color =>
    '<button type="button" class="swatch" data-swatch="' + color + '" data-action="swatch:' + color +
    '" aria-label="强调色 ' + color.toUpperCase() + '" aria-pressed="' + (look.accent.toLowerCase() === color) + '"></button>'
  ).join('') + '<button type="button" class="swatch custom" data-action="open-picker" aria-haspopup="dialog" aria-label="自定义强调色" aria-pressed="' +
    (!SWATCHES.includes(look.accent.toLowerCase())) + '"></button>';
  const controls = tab === 'look' ?
    selectLook(look, 'scheme', '主题') + '<div class="setting-row"><label for="xdc-accent-hex">强调色</label>' +
    '<span class="setting-control accent-controls"><span class="swatches" role="group" aria-label="强调色预设">' + swatches + '</span>' +
    '<input id="xdc-accent-hex" class="compact-input" aria-label="强调色十六进制" type="text" maxlength="7" spellcheck="false" data-hex value="' +
    e(look.accent.toUpperCase()) + '"></span></div>' + rangeLook(look, 'transparency', '玻璃透明度', '%') +
    selectLook(look, 'blur', '玻璃效果') + rangeLook(look, 'scale', '界面缩放', '%') :
    '<div class="setting-row"><span data-dimensions>' + e(geometry.width + ' × ' + geometry.height) + '</span>' +
    button(prefs.locked ? '解锁布局' : '锁定布局', 'lock') + '</div>' +
    (prefs.locked ? '' : '<label class="setting-row">宽度 <input type="number" min="320" max="620" data-geometry="width" value="' +
      e(geometry.width) + '"></label><label class="setting-row">高度 <input type="number" min="520" max="820" data-geometry="height" value="' +
      e(geometry.height) + '"></label>') + '<label class="setting-row">布局快捷键 <input maxlength="28" type="text" value="' +
      e(prefs.shortcut) + '" data-shortcut></label>' +
    selectLook(look, 'density', '列表密度') + selectLook(look, 'navStyle', '底栏显示') +
    selectLook(look, 'navAlign', '底栏位置') + rangeLook(look, 'radius', '窗口圆角', ' px');
  return head + controls + '<div class="pref-footer">' + button('恢复默认', 'reset-ui') +
    '<span class="subline" data-changes aria-live="polite">' + (canSave ? '未保存' : '') +
    '</span><button class="press primary" type="button" data-action="save-ui"' + (canSave ? '' : ' disabled') + '>保存</button></div>';
}

export function authView(status, busy = false) {
  const disabled = busy ? ' disabled' : '';
  const platformsList = Object.entries(platforms).map(([key, label]) => {
    const info = status?.[key];
    const state = info?.authenticated ? '已连接' : (info?.state === 'error' ? info.message || '检查失败' : '未连接');
    return '<div class="setting-row"><span class="setting-value">' + e(label) +
      '<span class="subline"> · ' + e(state) + '</span></span>' + button('官网', 'official:' + key) + '</div>';
  }).join('');
  const login = '<form class="form" data-form="login"><label>学号或用户名 <input name="username" autocomplete="username" required></label>' +
    '<label>密码 <input name="password" type="password" autocomplete="current-password" required></label>' +
    (status?.canRemember ? '<label class="inline-check"><input type="checkbox" name="remember"' +
      (status.remembered ? ' checked' : '') + '>记住账号，自动续登</label>' : '') +
    '<div class="actions"><button class="press primary" type="submit"' + disabled +
    '>' + (busy ? '正在登录…' : '登录') + '</button>' +
    (status?.remembered ? button('使用已记住账号', 'login-saved') + button('清除记住的账号', 'forget') : '') +
    button('退出登录', 'logout') + '</div></form>';
  const cookie = '<details><summary>导入会话</summary><form class="form" data-form="cookie">' +
    '<label>平台<select name="service"><option value="courses">学在浙大</option><option value="classroom">智云课堂</option></select></label>' +
    '<label>Cookie <textarea required name="cookie" rows="2" autocomplete="off" spellcheck="false"></textarea></label>' +
    '<div class="actions"><button class="press primary" type="submit"' + disabled + '>导入</button></div></form></details>';
  return hero('账号与会话') + group('连接状态', '浙大统一身份认证', list(platformsList)) +
    errorsFrom(status?.warnings) + group('登录', '', login) + group('备用方式', '', cookie);
}

// 学习桌面 UI 原型 —— 全部为合成数据，不连接任何学校平台。
const NOW = new Date('2026-11-23T15:40:00+08:00');
const DAY = 864e5;
const at = (days, hh = 23, mm = 59) => {
  const d = new Date(NOW.getTime() + days * DAY);
  d.setHours(hh, mm, 0, 0);
  return d.toISOString();
};

/* ---------------- preferences (mirrors DesktopLook / DesktopPreferences) ---------------- */
const DEFAULTS = Object.freeze({
  scheme: 'dark', accent: '#0a5152', transparency: 74, scale: 92,
  density: 'regular', navStyle: 'both', navAlign: 'center', radius: 30,
  locked: true, startup: true, shortcut: 'Ctrl+Shift+L', width: 460, height: 700,
});
const RANGES = { transparency: [0, 95], scale: [85, 125], radius: [16, 36] };
const SWATCHES = ['#0a5152', '#245dd1', '#7a4fd6', '#c2410c', '#be185d', '#3f6212', '#57534e'];
const STORE = 'xdc-ui-prototype-prefs';
const HIDDEN_STORE = 'xdc-ui-prototype-hidden';

const loadSaved = () => {
  try { return { ...DEFAULTS, ...JSON.parse(localStorage.getItem(STORE) || '{}') }; }
  catch { return { ...DEFAULTS }; }
};

/* ---------------- synthetic data ---------------- */
const BASE_COURSES = [
  { id: 'c1', name: '数字人文导论', teacher: '王思远', term: '2026–2027 秋冬', linked: true },
  { id: 'c2', name: '城市与公共生活', teacher: '陈立', term: '2026–2027 秋冬', linked: true },
  { id: 'c3', name: '世界电影史', teacher: '林晓', term: '2026–2027 秋冬', linked: false },
  { id: 'c4', name: '线性代数', teacher: '赵明', term: '2026–2027 秋冬', linked: true },
  { id: 'c5', name: '数据结构基础', teacher: '刘洋', term: '2026–2027 秋冬', linked: false },
  { id: 'c6', name: '体育（羽毛球）', teacher: '周健', term: '2026–2027 秋冬', linked: false },
  { id: 'c7', name: '大学物理（乙）Ⅰ', teacher: '孙伟', term: '2025–2026 春夏 · 已结束', linked: true },
];
const EXTRA_NAMES = ['概率论与数理统计', '微观经济学', '中国近现代史纲要', '程序设计基础', '社会学概论', '认知心理学', '西方哲学史',
  '离散数学', '数据库系统', '计算机网络', '操作系统', '人工智能导论', '艺术史', '英语写作', '日语（一）', '形势与政策',
  '设计思维', '传播学原理', '有机化学', '分子生物学', '宏观经济学', '统计学习', '信息论', '城市规划原理'];
const TEACHERS = ['李华', '张敏', '吴凡', '郑宇', '何静', '许诺', '冯涛', '韩雪'];

const ASSIGNMENTS = [
  { id: 'a1', cid: 'c2', title: '街区观察报告', due: at(1), desc: '选择杭州一处街区，完成不少于 3000 字的观察报告，附 6 张以上现场照片。', files: ['报告模板.docx'] },
  { id: 'a2', cid: 'c1', title: '文本分析练习', due: at(3), desc: '使用课堂介绍的工具对给定语料做词频与共现分析，提交 notebook 与简短说明。', files: ['语料.zip', '评分标准.pdf'] },
  { id: 'a3', cid: 'c3', title: '镜头分析小记', due: at(5, 20, 0), desc: '任选一部默片，分析其中一个场景的镜头语言。', files: [] },
  { id: 'a4', cid: 'c4', title: '第 9 次习题', due: at(0, 22, 0), desc: '教材 5.3 节习题 2、5、7、11。', files: ['习题9.pdf'] },
  { id: 'a5', cid: 'c5', title: '实验三：二叉搜索树', due: at(9), desc: '实现插入、删除和中序遍历，并通过在线评测。', files: ['实验指导书.pdf'] },
  { id: 'a6', cid: 'c1', title: '阅读回应 #4', due: at(-2), desc: '阅读指定章节并写 500 字回应。', files: [], submitted: true },
];
const MATERIALS = [
  { id: 'm1', cid: 'c1', name: '文本分析入门.pptx', size: 8.4e6, time: at(0, 9, 40), fresh: true },
  { id: 'm2', cid: 'c4', name: '第五章 特征值与特征向量.pdf', size: 2.1e6, time: at(-1, 14, 5), fresh: true },
  { id: 'm3', cid: 'c2', name: '公共空间阅读材料.pdf', size: 5.6e6, time: at(-1, 10, 12), fresh: true, previewOnly: true },
  { id: 'm4', cid: 'c3', name: '第十讲 新浪潮.pptx', size: 3.2e7, time: at(-4, 8, 0) },
  { id: 'm5', cid: 'c5', name: 'Lecture 08 Trees.pdf', size: 1.4e6, time: at(-6, 8, 0) },
  { id: 'm6', cid: 'c1', name: '课程大纲.pdf', size: 3.1e5, time: at(-60, 8, 0) },
];
const SESSIONS = {
  c1: [{ id: 's11', name: '第 12 周 · 数字档案与记忆', date: '11/20' }, { id: 's12', name: '第 11 周 · 网络分析', date: '11/13' }],
  c2: [{ id: 's21', name: '公共空间与城市设计', date: '11/21' }, { id: 's22', name: '街道的社会生活', date: '11/14' }],
  c4: [{ id: 's41', name: '特征值（二）', date: '11/22', partial: true }, { id: 's42', name: '特征值（一）', date: '11/19' }],
  c7: [{ id: 's71', name: '期末复习', date: '06/18' }],
};

function courses(state) {
  if (state.sim.state === 'empty') return [];
  if (!state.sim.many) return BASE_COURSES;
  return BASE_COURSES.concat(EXTRA_NAMES.slice(0, 23).map((name, i) => ({
    id: 'x' + i, name, teacher: TEACHERS[i % TEACHERS.length], term: '2026–2027 秋冬', linked: i % 3 === 0,
  })));
}
const courseById = id => BASE_COURSES.find(c => c.id === id) ||
  { id, name: EXTRA_NAMES[+id.slice(1)] || '课程', teacher: TEACHERS[+id.slice(1) % 8], term: '2026–2027 秋冬' };

/* ---------------- state ---------------- */
const state = {
  saved: loadSaved(),
  draft: null,                       // non-null only inside 外观设置
  hidden: new Set(JSON.parse(localStorage.getItem(HIDDEN_STORE) || '["c7"]')),
  route: { name: 'overview' },
  history: [],
  editing: false,
  jobs: [
    { id: 'j1', title: '第 9 次习题 · 习题9.pdf', fmt: '原文件', status: 'done', where: '下载\\学习桌面\\线性代数' },
    { id: 'j2', title: '特征值（二）· 课堂 PDF', fmt: '课堂 PDF', status: 'partial', pages: '38/41 页', where: '下载\\学习桌面\\线性代数' },
    { id: 'j3', title: '公共空间阅读材料.pdf', fmt: '预览 PDF', status: 'failed', error: '平台未授予打印权限，无法导出' },
  ],
  sim: { state: 'normal', download: false, many: false, noblur: false },
  picker: null,
  confirm: null,
};
const look = () => state.draft ?? state.saved;
const dirty = () => state.draft && JSON.stringify(state.draft) !== JSON.stringify(state.saved);

/* ---------------- color helpers ---------------- */
const hexOk = v => /^#[0-9a-f]{6}$/i.test(v);
const toRgb = h => [1, 3, 5].map(i => parseInt(h.slice(i, i + 2), 16));
const toHex = rgb => '#' + rgb.map(v => Math.round(v).toString(16).padStart(2, '0')).join('');
const lum = rgb => { const L = c => (c /= 255) <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4; return .2126 * L(rgb[0]) + .7152 * L(rgb[1]) + .0722 * L(rgb[2]); };
const contrast = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };
const mix = (a, b, w) => a.map((v, i) => v + (b[i] - v) * w);
function hsvToRgb(h, s, v) {
  s /= 100; v /= 100; const f = n => { const k = (n + h / 60) % 6; return v - v * s * Math.max(0, Math.min(k, 4 - k, 1)); };
  return [f(5), f(3), f(1)].map(x => x * 255);
}
function rgbToHsv([r, g, b]) {
  r /= 255; g /= 255; b /= 255; const max = Math.max(r, g, b), d = max - Math.min(r, g, b);
  let h = 0; if (d) h = max === r ? ((g - b) / d) % 6 : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return [Math.round((h * 60 + 360) % 360), Math.round(max ? d / max * 100 : 0), Math.round(max * 100)];
}

/* ---------------- apply look ---------------- */
const W = document.getElementById('widget');
function isDark(l) { return l.scheme === 'dark' || (l.scheme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches); }
function applyLook() {
  const l = look(), dark = isDark(l);
  W.classList.toggle('dark', dark); W.classList.toggle('light', !dark);
  W.classList.toggle('noblur', state.sim.noblur || state.sim.state === 'uu');
  W.classList.toggle('quiet', state.sim.state === 'uu');
  for (const a of ['left', 'center', 'right']) W.classList.toggle('dock-' + a, l.navAlign === a);
  const sheet = dark ? [32, 33, 36] : [247, 247, 248], fg = dark ? [242, 242, 244] : [29, 30, 32];
  const tWeight = Math.min(1, Math.max(0, (l.transparency - 30) / 65));
  const sec = dark ? mix([182, 184, 194], [236, 240, 248], tWeight) : mix([96, 100, 108], [38, 42, 48], tWeight);
  const accent = toRgb(l.accent);
  const minCr = dark ? 6.0 : 4.5;
  let ink = fg; for (let s = 0; s <= 12; s++) { const t = mix(accent, fg, s / 12); if (contrast(t, sheet) >= minCr) { ink = t; break; } }
  const s = W.style;
  s.setProperty('--fg2', toHex(sec));
  s.setProperty('--tint', ((100 - l.transparency) / 100).toFixed(2));
  s.setProperty('--radius', l.radius + 'px');
  s.setProperty('--zoom', (l.scale / 100).toFixed(2));
  s.setProperty('--row', { compact: 44, regular: 54, comfortable: 66 }[l.density] + 'px');
  s.setProperty('--accent', l.accent);
  s.setProperty('--accent-ink', toHex(ink));
  s.setProperty('--on-accent', contrast(accent, [255, 255, 255]) >= contrast(accent, [0, 0, 0]) ? '#fff' : '#111');
}

/* ---------------- html helpers ---------------- */
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const fmtDue = iso => {
  const d = new Date(iso), days = Math.floor((d - new Date(NOW).setHours(0, 0, 0, 0)) / DAY);
  const hm = d.toTimeString().slice(0, 5);
  if (days === 0) return '今天 ' + hm; if (days === 1) return '明天 ' + hm;
  return (d.getMonth() + 1) + '/' + d.getDate() + ' ' + hm;
};
const size = b => b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB';
const ICON = {
  home: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M3 9.5 10 4l7 5.5V16a1 1 0 0 1-1 1h-3.5v-4.5h-5V17H4a1 1 0 0 1-1-1z"/></svg>',
  book: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M4 4.5A1.5 1.5 0 0 1 5.5 3H16v12H5.5A1.5 1.5 0 0 0 4 16.5zM4 16.5A1.5 1.5 0 0 0 5.5 18H16"/></svg>',
  down: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M10 3v10m-4-4 4 4 4-4M4 17h12"/></svg>',
  gear: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="10" cy="10" r="2.6"/><path d="M10 2.5v2M10 15.5v2M2.5 10h2M15.5 10h2M4.7 4.7l1.4 1.4M13.9 13.9l1.4 1.4M4.7 15.3l1.4-1.4M13.9 6.1l1.4-1.4"/></svg>',
  back: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8"><path d="m12.5 4-6 6 6 6"/></svg>',
  refresh: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M16 10a6 6 0 1 1-1.8-4.3M16 3.5v3h-3"/></svg>',
  move: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="M10 3v14M3 10h14M10 3 8 5m2-2 2 2M10 17l-2-2m2 2 2-2M3 10l2-2m-2 2 2 2M17 10l-2-2m2 2-2 2"/></svg>',
  pipette: '<svg viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6"><path d="m12 5 3 3M4 16l1-3 7-7 2 2-7 7z"/><path d="m13.5 3.5 3 3"/></svg>',
};
const row = ({ title, sub, trail, action, urgent, dot, dim, chev = true, extra = '' }) =>
  `<button class="row${dim ? ' dim' : ''}" type="button" data-act="${esc(action)}">${dot ? '<span class="dot"></span>' : ''}` +
  `<span class="lead"><span class="title">${esc(title)}</span>${sub ? `<span class="sub">${esc(sub)}</span>` : ''}</span>` +
  (trail ? `<span class="trail${urgent ? ' urgent' : ''}">${esc(trail)}</span>` : '') + extra + (chev ? '<span class="chev">›</span>' : '') + '</button>';
const staticRow = (title, sub, right = '') =>
  `<div class="row static"><span class="lead"><span class="title">${esc(title)}</span>${sub ? `<span class="sub">${sub}</span>` : ''}</span>${right}</div>`;
const section = (title, aside, body) => `<section class="section"><div class="section-head"><h3>${esc(title)}</h3>${aside || ''}</div>${body}</section>`;
const list = rows => `<div class="list">${rows}</div>`;
const empty = msg => `<div class="empty">${esc(msg)}</div>`;
const hero = (title, sub, meta) => `<div class="hero"><h2>${esc(title)}</h2>${sub ? `<p>${sub}</p>` : ''}${meta || ''}</div>`;
const seg = (key, options, value, attr = 'data-set') => `<div class="seg" role="group">` +
  Object.entries(options).map(([v, t]) => `<button type="button" ${attr}="${key}" data-val="${v}" aria-pressed="${value === v}">${t}</button>`).join('') + '</div>';
const skeleton = () => '<div class="skeleton"></div>'.repeat(5);

function statusBanner() {
  const s = state.sim.state;
  if (s === 'expired') return `<div class="banner warn"><span class="lead">浙大统一身份认证已失效<small>显示的是 11:20 的缓存，自动更新已暂停</small></span><button class="btn primary" data-act="go:account">重新登录</button></div>`;
  const running = state.jobs.find(j => j.status === 'running');
  if (running && state.route.name !== 'downloads') return `<div class="banner"><span class="lead">正在下载 ${esc(running.title)}<div class="progress"><i style="width:${running.pct}%"></i></div></span><button class="btn quiet" data-act="go:downloads">查看</button></div>`;
  return '';
}

/* ---------------- views ---------------- */
const views = {
  overview() {
    if (state.sim.state === 'loading') return hero('学习总览', '11 月 23 日 · 星期一') + skeleton();
    const visible = new Set(courses(state).filter(c => !state.hidden.has(c.id)).map(c => c.id));
    const isEmpty = state.sim.state === 'empty';
    const deadlines = isEmpty ? [] : ASSIGNMENTS.filter(a => visible.has(a.cid) && !a.submitted && new Date(a.due) > NOW && new Date(a.due) - NOW < 7 * DAY)
      .sort((a, b) => a.due.localeCompare(b.due));
    const fresh = isEmpty ? [] : MATERIALS.filter(m => m.fresh && visible.has(m.cid));
    return hero('学习总览', '11 月 23 日 · 星期一') + statusBanner() +
      section('未来 7 天', `<span>${deadlines.length ? deadlines.length + ' 项作业截止' : ''}</span>`, deadlines.length ? list(deadlines.map(a => row({
        title: a.title, sub: courseById(a.cid).name, trail: fmtDue(a.due), urgent: new Date(a.due) - NOW < 1.5 * DAY,
        action: `assignment:${a.id}`, chev: false,
      })).join('')) : empty('近 7 天没有要交的作业')) +
      section('新课件', `<span>学在浙大</span>`, fresh.length ? list(fresh.map(m => row({
        title: m.name, sub: courseById(m.cid).name + ' · ' + fmtDue(m.time).replace(/^(今天|明天)/, '$1') + ' 更新', dot: true,
        action: `course:${m.cid}:materials`, chev: false,
      })).join('')) : empty('暂无新课件'));
  },

  courses() {
    const all = courses(state);
    if (state.sim.state === 'loading') return hero('我的课程') + skeleton();
    const shown = all.filter(c => !state.hidden.has(c.id)), hidden = all.filter(c => state.hidden.has(c.id));
    const head = hero('我的课程', all.length ? `${shown.length} 门显示中${hidden.length ? ` · ${hidden.length} 门已隐藏` : ''}` : '');
    const edit = `<button class="link" data-act="toggle-edit">${state.editing ? '完成' : '编辑'}</button>`;
    if (!all.length) return head + statusBanner() + empty('还没有读取到课程。登录后会自动同步学在浙大的课程。');
    if (state.editing) {
      const editRow = (c, isHidden) => staticRow(c.name, esc([c.teacher, c.term].join(' · ')),
        `<button class="btn${isHidden ? ' primary' : ''}" data-act="vis:${c.id}">${isHidden ? '恢复显示' : '隐藏'}</button>`);
      return head + section('显示中', edit, list(shown.map(c => editRow(c, false)).join('') || empty('全部课程都已隐藏'))) +
        (hidden.length ? section('已隐藏', '<span>不会出现在总览和课程列表</span>', list(hidden.map(c => editRow(c, true)).join(''))) : '');
    }
    return head + statusBanner() + section('本学期', edit, list(shown.map(c => row({
      title: c.name, sub: c.teacher + ' · ' + c.term, action: `course:${c.id}:summary`,
    })).join('') || empty('全部课程都已隐藏，点「编辑」恢复显示'))) +
      (hidden.length ? `<button class="link" data-act="toggle-edit" style="font-size:12px">查看 ${hidden.length} 门已隐藏的课程</button>` : '');
  },

  course({ id, tab = 'summary' }) {
    const c = courseById(id);
    const work = ASSIGNMENTS.filter(a => a.cid === id).sort((a, b) => b.due.localeCompare(a.due));
    const mats = MATERIALS.filter(m => m.cid === id);
    const sess = SESSIONS[id] || [];
    const head = hero(c.name, null, `<div class="meta"><b>${esc(c.teacher)}</b><span>·</span><span>${esc(c.term)}</span></div>`) +
      seg('tab', { summary: '概览', homework: '作业', materials: '课件', lecture: '智云课堂' }, tab, 'data-tab');
    const dl = (scope, fid, label = '下载') => `<button class="btn primary" data-act="dl:${scope}:${fid}">${label}</button>`;
    if (tab === 'homework') return head + (work.length ? list(work.map(a => row({
      title: a.title, sub: a.submitted ? '已提交' : new Date(a.due) < NOW ? '已逾期' : '未提交', trail: fmtDue(a.due), action: `assignment:${a.id}`,
    })).join('')) : empty('这门课暂时没有作业'));
    if (tab === 'materials') return head + (mats.length ? list(mats.map(m => staticRow(m.name,
      esc(size(m.size) + (m.previewOnly ? ' · 仅在线预览' : '') + ' · ' + fmtDue(m.time)), dl('m', m.id, m.previewOnly ? '尝试导出' : '下载'))).join('')) : empty('这门课还没有课件'));
    if (tab === 'lecture') {
      if (!c.linked) return head + section('关联智云课程', '<span>自动匹配失败时手动选择</span>', list(
        row({ title: c.name, sub: c.teacher + ' · 智云课堂', trail: '关联', action: `link:${id}`, chev: false }) +
        row({ title: c.name + '（重修班）', sub: TEACHERS[2] + ' · 智云课堂', trail: '关联', action: `link:${id}`, chev: false })));
      return head + section('课堂', `<button class="link" data-act="unlink:${id}">解除关联</button>`, sess.length ? list(sess.map(s => staticRow(s.name,
        esc(s.date + (s.partial ? ' · 部分页面缺失' : '')), dl('s', s.id, '导出 PDF'))).join('')) : empty('本月暂无课堂'));
    }
    const next = work.find(a => !a.submitted && new Date(a.due) > NOW);
    return head + list(
      row({ title: next ? next.title : '没有待交作业', sub: next ? '下一项截止 · ' + fmtDue(next.due) : work.length + ' 项作业', action: 'tab:homework' }) +
      row({ title: mats[0]?.name || '暂无课件', sub: mats.length + ' 份课件', action: 'tab:materials' }) +
      row({ title: c.linked ? (sess[0]?.name || '暂无课堂') : '尚未关联智云课堂', sub: c.linked ? sess.length + ' 堂课 · 可导出课件 PDF' : '点击选择对应课程', action: 'tab:lecture' }));
  },

  assignment({ id }) {
    const a = ASSIGNMENTS.find(x => x.id === id), c = courseById(a.cid);
    return hero(a.title, null, `<div class="meta"><b>${esc(c.name)}</b><span>·</span><span>截止 ${fmtDue(a.due)}</span>${a.submitted ? '<span>· 已提交</span>' : ''}</div>`) +
      section('作业要求', '', `<p style="font-size:14px;line-height:1.7;margin:4px 0 0">${esc(a.desc)}</p>`) +
      section('附件', '', a.files.length ? list(a.files.map((f, i) => staticRow(f, '', `<button class="btn primary" data-act="dl:a:${a.id}-${i}">下载</button>`)).join('')) : empty('没有附件'));
  },

  downloads() {
    const paused = state.sim.state === 'uu' || state.queuePaused;
    const label = { running: '正在处理', queued: '等待中', done: '已完成', partial: '部分导出', failed: '失败' };
    return hero('下载记录', paused ? '队列已暂停' : `${state.jobs.filter(j => j.status === 'done').length} 个已完成`) +
      `<div style="display:flex;gap:8px;margin:-6px 0 14px"><button class="btn" data-act="pause">${paused ? '继续队列' : '暂停队列'}</button><button class="btn" data-act="open-folder">打开下载文件夹</button></div>` +
      (state.jobs.length ? list(state.jobs.map(j => {
        const info = [label[j.status], j.fmt, j.pages].filter(Boolean).join(' · ');
        const right = j.status === 'done' || j.status === 'partial' ? '<button class="btn" data-act="reveal">显示位置</button>'
          : j.status === 'failed' ? `<button class="btn" data-act="retry:${j.id}">重试</button>` : '';
        const prog = j.status === 'running' ? `<div class="progress"><i style="width:${j.pct}%"></i></div>` : '';
        const err = j.error ? `<br><span style="color:var(--accent-ink)">${esc(j.error)}</span>` : '';
        return staticRow(j.title, esc(info) + err + prog, right);
      }).join('')) : empty('还没有下载过文件'));
  },

  settings() {
    const s = state.saved;
    const auth = state.sim.state === 'expired' ? '会话已失效' : '学在浙大、智云课堂已连接';
    return hero('设置') +
      section('外观', '', list(row({ title: '外观与布局', sub: `${{ dark: '深色', light: '浅色', system: '跟随系统' }[s.scheme]} · 透明度 ${s.transparency}% · 缩放 ${s.scale}%`, action: 'go:appearance' }))) +
      section('账号', '', list(row({ title: '浙大统一身份认证', sub: auth, action: 'go:account' }))) +
      section('桌面', '', list(
        `<div class="set"><span class="label">登录 Windows 时启动</span><input type="checkbox" class="switch" data-pref="startup"${s.startup ? ' checked' : ''}></div>` +
        `<div class="set"><span class="label">布局锁<small>解锁后可拖动和调整尺寸 · ${esc(s.shortcut)}</small></span><button class="btn" data-act="lock">${s.locked ? '解锁' : '锁定'}</button></div>` +
        `<div class="set"><span class="label">UU 加速器运行时<small>收起组件，处理完当前文件后暂停队列</small></span></div>`)) +
      section('下载', '', list(`<div class="set"><span class="label">保存位置<small>下载\\学习桌面</small></span><button class="btn" data-act="noop">更改</button></div>`)) +
      `<button class="btn" data-act="close" style="margin-top:4px">关闭组件</button>`;
  },

  appearance({ tab = 'look' }) {
    const l = state.draft;
    const sw = SWATCHES.map(c => `<button class="swatch" style="background:${c}" data-act="accent:${c}" aria-label="${c}" aria-pressed="${l.accent === c}"></button>`).join('') +
      `<button class="swatch custom" data-act="picker" aria-label="自定义颜色" aria-pressed="${!SWATCHES.includes(l.accent)}"></button>`;
    const range = (k, label, unit) => `<div class="set"><span class="label">${label}</span><input type="range" data-range="${k}" min="${RANGES[k][0]}" max="${RANGES[k][1]}" value="${l[k]}"><span class="val" data-val-of="${k}">${l[k]}${unit}</span></div>`;
    const body = tab === 'look' ? list(
      `<div class="set"><span class="label">主题</span>${seg('scheme', { system: '跟随系统', light: '浅色', dark: '深色' }, l.scheme)}</div>` +
      `<div class="set" style="flex-wrap:wrap"><span class="label">强调色</span><div class="swatches">${sw}</div>` +
      `<input class="hex" data-hex value="${l.accent.toUpperCase()}" maxlength="7" spellcheck="false" aria-label="强调色 HEX"></div>` +
      range('transparency', '玻璃透明度', '%') + range('scale', '界面缩放', '%') + range('radius', '窗口圆角', ' px'))
      : list(
        `<div class="set"><span class="label">列表密度</span>${seg('density', { compact: '紧凑', regular: '标准', comfortable: '宽松' }, l.density)}</div>` +
        `<div class="set"><span class="label">底栏显示</span>${seg('navStyle', { both: '图标+文字', icons: '图标', text: '文字' }, l.navStyle)}</div>` +
        `<div class="set"><span class="label">底栏位置</span>${seg('navAlign', { left: '靠左', center: '居中', right: '靠右' }, l.navAlign)}</div>` +
        `<div class="set"><span class="label">组件尺寸<small>${l.width} × ${l.height} DIP · ${state.saved.locked ? '已锁定' : '已解锁，可拖动右下角'}</small></span><button class="btn" data-act="lock">${state.saved.locked ? '解锁布局' : '锁定布局'}</button></div>`);
    const st = state.previewingDefaults && dirty() ? '正在预览默认外观，保存后生效' : dirty() ? '有未保存的更改（正在预览）' : '已与保存的外观一致';
    return hero('外观与布局') + seg('tab', { look: '外观', layout: '布局' }, tab, 'data-tab') + body +
      `<div class="footer-bar"><button class="btn" data-act="reset">恢复默认</button><span class="state">${st}</span>` +
      `<button class="btn" data-act="revert"${dirty() ? '' : ' disabled'}>撤销</button><button class="btn primary" data-act="save"${dirty() ? '' : ' disabled'}>保存</button></div>`;
  },

  account() {
    const expired = state.sim.state === 'expired';
    const st = (n) => staticRow(n, expired ? '会话已失效' : '已连接 · 最近验证 15:12', `<button class="btn" data-act="noop">官网</button>`);
    return hero('浙大统一身份认证', '一份本机认证信息用于学在浙大和智云课堂，平台会话各自维持。') +
      section('平台会话', '', list(st('学在浙大') + st('智云课堂'))) +
      section('本机凭据', '<span>Windows 加密存储</span>', list(
        staticRow(expired ? '需要重新登录' : '已记住演示账号', '使用模拟数据演示会话状态', '') )) +
      `<div style="display:flex;gap:8px"><button class="btn primary" data-act="relogin">${expired ? '重新登录' : '重新验证'}</button><button class="btn" data-act="noop">退出登录</button></div>`;
  },
};

/* ---------------- render ---------------- */
const content = document.getElementById('content'), top = document.getElementById('chrome-top'), dock = document.getElementById('dock');
const DOCK = [['overview', '总览', 'home'], ['courses', '我的课程', 'book'], ['downloads', '下载记录', 'down']];
const rootOf = r => ({ course: 'courses', assignment: state.history[0]?.name === 'courses' ? 'courses' : 'overview', appearance: 'settings', account: 'settings' }[r.name] || r.name);

function render(keepScroll = false) {
  applyLook();
  const r = state.route, l = look();
  if (state.sim.state === 'uu') {
    top.innerHTML = `<span class="quiet-bar">UU 加速器运行中 · 组件已收起，更新与下载已暂停</span>`;
    return;
  }
  top.innerHTML = (state.history.length ? `<button class="tool" data-act="back" aria-label="返回">${ICON.back}</button>` : '') +
    (!state.saved.locked ? `<button class="tool" aria-label="拖动组件" style="cursor:move">${ICON.move}</button><span class="unlocked-tag">布局已解锁</span>` : '') +
    '<span class="spacer"></span>' +
    (['overview', 'courses', 'course', 'downloads'].includes(r.name) ? `<button class="tool" data-act="refresh" aria-label="刷新">${ICON.refresh}</button>` : '') +
    (r.name !== 'settings' && r.name !== 'appearance' ? `<button class="tool" data-act="go:settings" aria-label="设置">${ICON.gear}</button>` : '');
  const active = rootOf(r);
  const busy = state.jobs.some(j => j.status === 'running' || j.status === 'queued');
  dock.innerHTML = DOCK.map(([name, text, icon]) => `<button type="button" data-dock="${name}" aria-label="${text}" ${active === name ? 'aria-current="page"' : ''}>` +
    (l.navStyle !== 'text' ? ICON[icon] : '') + (l.navStyle !== 'icons' ? `<span>${text}</span>` : '') +
    (name === 'downloads' && busy ? '<i class="badge"></i>' : '') + '</button>').join('');
  const sc = document.getElementById('scroller'), y = sc.scrollTop;
  content.innerHTML = views[r.name](r);
  sc.scrollTop = keepScroll ? y : (r.scroll || 0);
  renderPicker();
  renderConfirm();
  document.getElementById('r-saved').textContent = JSON.stringify(state.saved, null, 1);
}

function go(route, { replace = false } = {}) {
  const sc = document.getElementById('scroller');
  if (!replace) state.history.push({ ...state.route, scroll: sc.scrollTop });
  state.route = route;
  state.editing = false;
  render();
}
function guardLeave(next) {
  if (state.route.name === 'appearance' && dirty()) { state.confirm = next; render(true); return; }
  if (state.route.name === 'appearance') state.draft = null;
  next();
}

let noticeTimer;
function notice(msg) {
  const n = document.getElementById('notice'); n.textContent = msg; n.classList.add('show');
  clearTimeout(noticeTimer); noticeTimer = setTimeout(() => n.classList.remove('show'), 2600);
}

/* ---------------- color picker (in-widget popover) ---------------- */
const layer = document.getElementById('popover-layer');
function openPicker() { state.picker = { original: state.draft.accent, hsv: rgbToHsv(toRgb(state.draft.accent)) }; render(true); }
function closePicker(commit) {
  if (!state.picker) return;
  if (!commit) state.draft.accent = state.picker.original;   // Esc / 右键 / 取消：回到打开前的草稿
  state.picker = null; render(true);
}
function pickerSet(hsv) {
  state.picker.hsv = hsv; state.draft.accent = toHex(hsvToRgb(...hsv)); applyLook(); paintPicker();
}
function renderPicker() {
  layer.classList.toggle('open', !!state.picker || !!state.confirm);
  if (state.confirm) return;
  if (!state.picker) { layer.innerHTML = ''; return; }
  layer.innerHTML = `<div class="picker" role="dialog" aria-label="自定义强调色">
    <h4>自定义强调色 <span style="font-size:11px;font-weight:400;color:var(--fg2)">Esc 或右键取消</span></h4>
    <div class="sv" data-drag="sv"><span class="knob"></span></div>
    <div class="hue" data-drag="hue"><span class="knob"></span></div>
    <div class="nums">
      <label>色相<input data-hsv="0" type="number" min="0" max="360"></label>
      <label>饱和度<input data-hsv="1" type="number" min="0" max="100"></label>
      <label>明度<input data-hsv="2" type="number" min="0" max="100"></label>
      <label>HEX<input data-phex maxlength="7" spellcheck="false"></label>
    </div>
    <div class="row-actions"><span class="preview-chip"></span>
      <button class="btn" data-act="eyedrop" title="屏幕吸管">${ICON.pipette.replace('<svg', '<svg width="16" height="16" style="vertical-align:-3px"')} 吸管</button>
      <span class="grow"></span><button class="btn" data-act="picker-cancel">取消</button><button class="btn primary" data-act="picker-ok">确定</button></div></div>`;
  paintPicker();
}
function paintPicker() {
  const p = state.picker, el = layer.querySelector('.picker'); if (!p || !el) return;
  const [h, s, v] = p.hsv;
  const sv = el.querySelector('.sv'); sv.style.background = `hsl(${h} 100% 50%)`;
  Object.assign(sv.querySelector('.knob').style, { left: s + '%', top: (100 - v) + '%', background: state.draft.accent });
  el.querySelector('.hue .knob').style.left = (h / 360 * 100) + '%';
  el.querySelector('.hue .knob').style.background = `hsl(${h} 100% 50%)`;
  el.querySelectorAll('[data-hsv]').forEach(i => { if (document.activeElement !== i) i.value = p.hsv[i.dataset.hsv]; });
  const hx = el.querySelector('[data-phex]'); if (document.activeElement !== hx) hx.value = state.draft.accent.toUpperCase();
  el.querySelector('.preview-chip').style.background = state.draft.accent;
}
async function eyedrop() {
  if (!('EyeDropper' in window)) { notice('此浏览器不支持吸管；原生版使用屏幕取色'); return; }
  try {
    const { sRGBHex } = await new EyeDropper().open();      // Esc 由系统取消，不改变草稿
    const hex = sRGBHex.startsWith('#') ? sRGBHex : toHex(sRGBHex.match(/\d+/g).slice(0, 3).map(Number));
    pickerSet(rgbToHsv(toRgb(hex)));
  } catch { notice('已取消取色'); }
}

function renderConfirm() {
  if (!state.confirm) return;
  layer.innerHTML = `<div class="picker" role="alertdialog"><h4>放弃未保存的外观更改？</h4>
    <p style="margin:0 0 4px;color:var(--fg2)">离开后将恢复为已保存的外观。</p>
    <div class="row-actions"><span class="grow"></span><button class="btn" data-act="confirm-stay">继续编辑</button>
    <button class="btn primary" data-act="confirm-leave">放弃更改</button></div></div>`;
}

/* ---------------- events ---------------- */
W.addEventListener('click', e => {
  const t = e.target.closest('[data-act],[data-dock],[data-tab],[data-set]'); if (!t) return;
  if (t.dataset.dock) {
    const name = t.dataset.dock;
    return guardLeave(() => { state.history = []; state.route = { name }; state.editing = false; render(); });
  }
  if (t.dataset.tab) { state.route = { ...state.route, tab: t.dataset.val }; return render(true); }
  if (t.dataset.set) { state.draft[t.dataset.set] = t.dataset.val; state.previewingDefaults = false; return render(true); }
  const [act, a1, a2] = t.dataset.act.split(':');
  const ops = {
    back: () => guardLeave(() => { const prev = state.history.pop(); if (prev) { state.route = prev; render(); } }),
    go: () => {
      if (a1 === 'appearance') state.draft = { ...state.saved };
      go({ name: a1 });
    },
    assignment: () => go({ name: 'assignment', id: a1 }),
    course: () => go({ name: 'course', id: a1, tab: a2 }),
    tab: () => { state.route.tab = a1; render(true); },
    refresh: () => notice('已更新 · 15:40'),
    'toggle-edit': () => { state.editing = !state.editing; render(true); },
    vis: () => {
      state.hidden.has(a1) ? state.hidden.delete(a1) : state.hidden.add(a1);
      localStorage.setItem(HIDDEN_STORE, JSON.stringify([...state.hidden])); render(true);
    },
    dl: () => {
      const label = t.closest('.row')?.querySelector('.title')?.textContent || '文件';
      if (state.sim.state === 'uu') return notice('UU 运行中，下载已暂停');
      if (state.sim.state === 'expired') return notice('登录已失效，请先重新登录');
      state.jobs.unshift({ id: 'j' + Date.now(), title: label, fmt: a1 === 's' ? '课堂 PDF' : '原文件', status: 'running', pct: 4 });
      notice('已加入下载队列'); render(true);
    },
    link: () => { courseById(a1).linked = true; notice('已关联智云课程'); render(true); },
    unlink: () => { courseById(a1).linked = false; render(true); },
    pause: () => { state.queuePaused = !state.queuePaused; render(true); },
    retry: () => { const j = state.jobs.find(x => x.id === a1); j.status = 'running'; j.pct = 3; j.error = null; render(true); },
    reveal: () => notice('将在资源管理器中显示文件'), 'open-folder': () => notice('将打开 下载\\学习桌面'), noop: () => notice('原型中未实现'),
    relogin: () => { if (state.sim.state === 'expired') { state.sim.state = 'normal'; document.getElementById('r-state').value = 'normal'; } notice('已验证，两个平台会话已更新'); render(true); },
    lock: () => { state.saved.locked = !state.saved.locked; persist(); notice(state.saved.locked ? '布局已锁定，位置和尺寸已记住' : '布局已解锁'); render(true); },
    close: () => notice('原生版会退出组件；可从开始菜单再次打开'),
    accent: () => { state.draft.accent = a1; state.previewingDefaults = false; render(true); },
    picker: () => openPicker(),
    'picker-ok': () => closePicker(true), 'picker-cancel': () => closePicker(false), eyedrop,
    reset: () => { state.draft = { ...DEFAULTS, locked: state.saved.locked, startup: state.saved.startup, width: state.saved.width, height: state.saved.height }; state.previewingDefaults = true; render(true); },
    revert: () => { state.draft = { ...state.saved }; render(true); },
    save: () => { state.saved = { ...state.draft }; persist(); state.previewingDefaults = false; notice('外观已保存'); render(true); },
    'confirm-stay': () => { state.confirm = null; render(true); },
    'confirm-leave': () => { const next = state.confirm; state.confirm = null; state.draft = null; next(); },
  };
  ops[act]?.();
});

W.addEventListener('input', e => {
  const t = e.target;
  if (t.dataset.range) {
    state.draft[t.dataset.range] = +t.value; state.previewingDefaults = false; applyLook();
    const unit = t.dataset.range === 'radius' ? ' px' : '%';
    W.querySelector(`[data-val-of="${t.dataset.range}"]`).textContent = t.value + unit;
    const st = W.querySelector('.footer-bar'); st.querySelector('.state').textContent = dirty() ? '有未保存的更改（正在预览）' : '已与保存的外观一致';
    st.querySelectorAll('[data-act=save],[data-act=revert]').forEach(b => b.disabled = !dirty());
  }
  if ('hex' in t.dataset) {
    const v = t.value.startsWith('#') ? t.value : '#' + t.value;
    t.classList.toggle('bad', !hexOk(v));
    if (hexOk(v)) { state.draft.accent = v.toLowerCase(); applyLook(); }
  }
  if (t.dataset.hsv !== undefined && state.picker) {
    const hsv = [...state.picker.hsv]; const max = [360, 100, 100][t.dataset.hsv];
    hsv[t.dataset.hsv] = Math.max(0, Math.min(max, +t.value || 0)); pickerSet(hsv);
  }
  if ('phex' in t.dataset && state.picker) {
    const v = t.value.startsWith('#') ? t.value : '#' + t.value; if (hexOk(v)) pickerSet(rgbToHsv(toRgb(v)));
  }
});
W.addEventListener('change', e => {
  const t = e.target;
  if (t.dataset.pref) { state.saved[t.dataset.pref] = t.checked; persist(); notice(t.checked ? '已开启开机启动' : '已关闭开机启动'); }
  if ('hex' in t.dataset) render(true);
  if (t.dataset.range) render(true);
});
// drag in SV square / hue strip
W.addEventListener('pointerdown', e => {
  const zone = e.target.closest('[data-drag]'); if (!zone || !state.picker || e.button !== 0) return;
  zone.setPointerCapture(e.pointerId);
  const move = ev => {
    const r = zone.getBoundingClientRect(), x = Math.min(1, Math.max(0, (ev.clientX - r.left) / r.width)), y = Math.min(1, Math.max(0, (ev.clientY - r.top) / r.height));
    const [h, s, v] = state.picker.hsv;
    pickerSet(zone.dataset.drag === 'hue' ? [Math.round(x * 360), s, v] : [h, Math.round(x * 100), Math.round((1 - y) * 100)]);
  };
  move(e); zone.onpointermove = move; zone.onpointerup = () => { zone.onpointermove = null; };
});
layer.addEventListener('contextmenu', e => { if (state.picker) { e.preventDefault(); closePicker(false); } });
document.addEventListener('keydown', e => {
  if (e.key === 'Escape') { if (state.picker) closePicker(false); else if (state.confirm) { state.confirm = null; render(true); } }
  if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'l') { e.preventDefault(); W.querySelector('[data-act=lock]') ? W.querySelector('[data-act=lock]').click() : (state.saved.locked = !state.saved.locked, persist(), notice(state.saved.locked ? '布局已锁定' : '布局已解锁'), render(true)); }
});
function persist() { localStorage.setItem(STORE, JSON.stringify(state.saved)); document.getElementById('r-saved').textContent = JSON.stringify(state.saved, null, 1); }

/* ---------------- simulated download progress ---------------- */
setInterval(() => {
  if (state.sim.state === 'uu' || state.queuePaused) return;
  let changed = false;
  for (const j of state.jobs) if (j.status === 'running') {
    j.pct = Math.min(100, j.pct + 7 + Math.random() * 9); changed = true;
    if (j.pct >= 100) { j.status = 'done'; j.where = '下载\\学习桌面'; notice('已完成：' + j.title); }
  }
  if (changed) render(true);
}, 700);

/* ---------------- review console ---------------- */
const $ = id => document.getElementById(id);
$('r-wall').onchange = e => $('wallpaper').dataset.wall = e.target.value;
$('r-state').onchange = e => { state.sim.state = e.target.value; render(true); };
$('r-download').onchange = e => {
  if (e.target.checked) state.jobs.unshift({ id: 'jsim', title: '第十讲 新浪潮.pptx', fmt: '原文件', status: 'running', pct: 10 });
  else state.jobs = state.jobs.filter(j => j.id !== 'jsim'); render(true);
};
$('r-many').onchange = e => { state.sim.many = e.target.checked; render(true); };
$('r-noblur').onchange = e => { state.sim.noblur = e.target.checked; render(true); };
$('r-reset').onclick = () => { localStorage.removeItem(STORE); localStorage.removeItem(HIDDEN_STORE); location.reload(); };
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => render(true));

render();

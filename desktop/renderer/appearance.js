export const DEFAULT_LOOK = Object.freeze({
  scheme: 'dark', accent: '#0a5152', transparency: 74, scale: 92,
  density: 'regular', navStyle: 'both', navAlign: 'center', radius: 30, blur: true,
});

export const SWATCHES = Object.freeze([
  '#0a5152', '#245dd1', '#7a4fd6', '#c2410c', '#be185d', '#3f6212', '#57534e',
]);

export const DEFAULT_DESKTOP = Object.freeze({
  look: DEFAULT_LOOK,
  geometry: Object.freeze({ width: 460, height: 700, x: null, y: null }),
  shortcut: 'Ctrl+Shift+L', locked: true, startup: false, downloadDirectory: null,
});

export const options = {
  scheme: { system: '跟随系统', light: '浅色', dark: '深色' },
  blur: { true: '实时模糊', false: '纯透明' },
  density: { compact: '紧凑', regular: '标准', comfortable: '宽松' },
  navStyle: { both: '图标与文字', text: '仅文字', icons: '仅图标' },
  navAlign: { left: '靠左', center: '居中', right: '靠右' },
};

export const ranges = { transparency: [0, 95], scale: [85, 125], radius: [16, 36] };
export const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
export const isHex = (value) => typeof value === 'string' && /^#[\da-f]{6}$/i.test(value);
export const escapeHtml = (value) => String(value ?? '').replace(/[&<>"']/g, ch =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[ch]);

export function normalizeLook(key, value) {
  if (key === 'blur') return value !== false && value !== 'false';
  if (Object.hasOwn(options, key) && Object.hasOwn(options[key], value)) return value;
  if (key === 'accent' && isHex(value)) return value.toLowerCase();
  if (Object.hasOwn(ranges, key) && Number.isFinite(value)) return clamp(Math.round(value), ...ranges[key]);
  return DEFAULT_LOOK[key];
}

export function normalizeDesktop(saved = {}) {
  return {
    ...DEFAULT_DESKTOP,
    ...saved,
    look: Object.fromEntries(Object.keys(DEFAULT_LOOK).map(key => [key, normalizeLook(key, saved.look?.[key])])),
    geometry: { ...DEFAULT_DESKTOP.geometry, ...saved.geometry },
  };
}

export function normalizeShortcut(value) {
  const parts = value.toLowerCase().replace(/\s/g, '').split('+');
  const key = parts.pop();
  const names = { ctrl: 'Ctrl', alt: 'Alt', shift: 'Shift' };
  if (!parts.length || new Set(parts).size !== parts.length || !parts.every(part => Object.hasOwn(names, part)) ||
      !/^(?:[a-z0-9]|f(?:[1-9]|1[0-2]))$/.test(key)) return null;
  return [...Object.keys(names).filter(part => parts.includes(part)).map(part => names[part]), key.toUpperCase()].join('+');
}

const rgb = (hex) => [1, 3, 5].map(i => Number.parseInt(hex.slice(i, i + 2), 16));

function luminance(channels) {
  return channels.map(value => value / 255).reduce((sum, v, i) =>
    sum + [0.2126, 0.7152, 0.0722][i] * (v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4), 0);
}

function readableAccent(color, surface, target) {
  const original = rgb(color);
  const toward = rgb(target);
  const base = luminance(rgb(surface));
  for (let step = 0; step <= 10; step++) {
    const blend = original.map((value, i) => Math.round(value + (toward[i] - value) * step / 10));
    const light = luminance(blend);
    if ((Math.max(light, base) + 0.05) / (Math.min(light, base) + 0.05) >= 4.5) return 'rgb(' + blend.join(' ') + ')';
  }
  return target;
}

export function applyLook(root, look) {
  root.dataset.scheme = look.scheme;
  root.dataset.blur = String(look.blur);
  root.dataset.navStyle = look.navStyle;
  root.dataset.navAlign = look.navAlign;
  const spacing = { compact: [44, 6], regular: [54, 9], comfortable: [66, 12] }[look.density];
  const light = readableAccent(look.accent, '#f7f7f8', '#232426');
  const dark = readableAccent(look.accent, '#202124', '#f2f2f4');
  const vars = {
    '--blue': 'light-dark(' + light + ',' + dark + ')',
    '--window-opacity': (100 - look.transparency) + '%',
    '--text-contrast-boost': clamp((look.transparency - 30) * 100 / 65, 0, 100) + '%',
    '--glass-opacity': (100 - Math.round(look.transparency * .5)) + '%',
    '--text-size': (14 * look.scale / 100) + 'px',
    '--row-height': (spacing[0] * look.scale / 100) + 'px',
    '--row-pad': spacing[1] + 'px',
    '--window-radius': look.radius + 'px',
    '--selected': 'color-mix(in srgb,' + look.accent + ' 22%, transparent)',
  };
  for (const [key, value] of Object.entries(vars)) root.style.setProperty(key, value);
}

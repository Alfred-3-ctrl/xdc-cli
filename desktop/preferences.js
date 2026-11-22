import fs from 'node:fs';
import path from 'node:path';

export const DEFAULT_LOOK = Object.freeze({
  scheme: 'dark', accent: '#0a5152', transparency: 74, scale: 92,
  density: 'regular', navStyle: 'both', navAlign: 'center', radius: 30,
});

const pick = (value, choices, fallback) => choices.includes(value) ? value : fallback;
const integer = (value, min, max, fallback) => Number.isFinite(value)
  ? Math.max(min, Math.min(max, Math.round(value))) : fallback;

export function normalizeLook(input = {}, fallback = DEFAULT_LOOK) {
  return {
    scheme: pick(input?.scheme, ['system', 'light', 'dark'], fallback.scheme),
    accent: typeof input?.accent === 'string' && /^#[a-f\d]{6}$/i.test(input.accent)
      ? input.accent.toLowerCase() : fallback.accent,
    transparency: integer(input?.transparency, 0, 95, fallback.transparency),
    scale: integer(input?.scale, 85, 125, fallback.scale),
    density: pick(input?.density, ['compact', 'regular', 'comfortable'], fallback.density),
    navStyle: pick(input?.navStyle, ['both', 'text', 'icons'], fallback.navStyle),
    navAlign: pick(input?.navAlign, ['left', 'center', 'right'], fallback.navAlign),
    radius: integer(input?.radius, 16, 36, fallback.radius),
  };
}

export function normalizeBounds(input, displays, fallback) {
  const { x, y, width, height } = input ?? {};
  const point = {
    x: integer(x, -100000, 100000, fallback.x),
    y: integer(y, -100000, 100000, fallback.y),
    width: integer(width, 320, 820, fallback.width),
    height: integer(height, 520, 1100, fallback.height),
  };
  const overlap = (area) => Math.max(0, Math.min(area.x + area.width, point.x + point.width) - Math.max(area.x, point.x))
    * Math.max(0, Math.min(area.y + area.height, point.y + point.height) - Math.max(area.y, point.y));
  const area = displays.map(({ workArea }) => workArea).sort((a, b) => overlap(b) - overlap(a))[0];
  if (!area) return point;
  point.width = Math.min(point.width, area.width);
  point.height = Math.min(point.height, area.height);
  point.x = Math.max(area.x, Math.min(point.x, area.x + area.width - point.width));
  point.y = Math.max(area.y, Math.min(point.y, area.y + area.height - point.height));
  return point;
}

export function normalizeShortcut(value) {
  if (typeof value !== 'string') return null;
  const parts = value.toLowerCase().replace(/\s/g, '').split('+');
  const key = parts.pop();
  const names = { ctrl: 'Ctrl', alt: 'Alt', shift: 'Shift' };
  if (!parts.length || new Set(parts).size !== parts.length || !parts.every(part => Object.hasOwn(names, part)) ||
    !/^(?:[a-z0-9]|f(?:[1-9]|1[0-2]))$/.test(key)) return null;
  return [...Object.keys(names).filter(part => parts.includes(part)).map(part => names[part]), key.toUpperCase()].join('+');
}

export function readPreferences(file) {
  try {
    const parsed = JSON.parse(fs.readFileSync(file, 'utf8'));
    return parsed && typeof parsed === 'object' ? parsed : {};
  } catch {
    return {};
  }
}

export function savePreferences(file, prefs) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const temp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(temp, JSON.stringify(prefs, null, 2), { mode: 0o600 });
  fs.renameSync(temp, file);
}

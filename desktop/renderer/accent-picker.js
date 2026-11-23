import { clamp, isHex } from './appearance.js';

function toHsv(hex) {
  const [red, green, blue] = [1, 3, 5].map(index => Number.parseInt(hex.slice(index, index + 2), 16) / 255);
  const max = Math.max(red, green, blue);
  const delta = max - Math.min(red, green, blue);
  let hue = 0;
  if (delta > 0) {
    if (max === red) hue = (green - blue) / delta;
    else if (max === green) hue = (blue - red) / delta + 2;
    else hue = (red - green) / delta + 4;
  }
  return [(hue * 60 + 360) % 360, max ? delta / max * 100 : 0, max * 100];
}

function toHex([hue, saturation, value]) {
  return '#' + [5, 3, 1].map(channel => {
    const sector = (channel + hue / 60) % 6;
    const fraction = Math.max(0, Math.min(sector, 4 - sector, 1));
    const intensity = Math.round(value / 100 * (1 - saturation / 100 * fraction) * 255);
    return intensity.toString(16).padStart(2, '0');
  }).join('');
}

export function createAccentPicker(dialog, preview, EyeDropper) {
  const sv = dialog.querySelector('.sv');
  const svKnob = sv.querySelector('.knob');
  const hueKnob = dialog.querySelector('.hue .knob');
  const chip = dialog.querySelector('.preview-chip');
  const numbers = [...dialog.querySelectorAll('[data-hsv]')];
  const hex = dialog.querySelector('[data-phex]');
  const eyedrop = dialog.querySelector('[data-eyedrop]');
  const error = dialog.querySelector('[data-picker-error]');
  eyedrop.hidden = typeof EyeDropper !== 'function';
  let session;

  function paint(preserve) {
    const [hue, saturation, value] = session.hsv;
    const pureHue = `hsl(${hue} 100% 50%)`;
    sv.style.backgroundColor = pureHue;
    svKnob.style.left = `${saturation}%`;
    svKnob.style.top = `${100 - value}%`;
    svKnob.style.backgroundColor = session.accent;
    hueKnob.style.left = `${hue / 360 * 100}%`;
    hueKnob.style.backgroundColor = pureHue;
    chip.style.backgroundColor = session.accent;
    for (const [index, field] of numbers.entries()) {
      if (field !== preserve) field.value = String(Math.round(session.hsv[index]));
    }
    if (hex !== preserve) hex.value = session.accent.toUpperCase();
  }

  function select(hsv, accent = toHex(hsv), preserve) {
    const changed = accent !== session.accent;
    Object.assign(session, { hsv, accent });
    if (changed) preview(accent);
    paint(preserve);
  }

  function open(accent, trigger) {
    if (session) return;
    session = { original: accent, accent, hsv: toHsv(accent), trigger };
    error.hidden = true;
    paint();
    dialog.showModal();
    hex.select();
  }

  function close(keep = false) {
    if (!session) return;
    const { original, accent, trigger, sampling } = session;
    session = undefined;
    sampling?.abort();
    eyedrop.disabled = false;
    if (!keep && original !== accent) preview(original);
    dialog.close();
    trigger?.focus({ preventScroll: true });
  }

  async function sample() {
    const current = session;
    if (!current || current.sampling || eyedrop.hidden) return;
    const controller = new AbortController();
    current.sampling = controller;
    eyedrop.disabled = true;
    error.hidden = true;
    try {
      const result = await new EyeDropper().open({ signal: controller.signal });
      if (session !== current || controller.signal.aborted || !isHex(result?.sRGBHex)) return;
      const accent = result.sRGBHex.toLowerCase();
      select(toHsv(accent), accent);
    } catch (cause) {
      if (session === current && cause?.name !== 'AbortError') {
        error.textContent = '吸管暂时不可用，请使用调色板或输入 HEX。';
        error.hidden = false;
      }
    } finally {
      if (session === current) {
        current.sampling = undefined;
        eyedrop.disabled = false;
        eyedrop.focus({ preventScroll: true });
      }
    }
  }

  function drag(event) {
    const zone = session?.drag;
    if (!zone || zone.pointerId !== event.pointerId) return;
    const { left, top, width, height } = zone.element.getBoundingClientRect();
    const x = clamp((event.clientX - left) / Math.max(1, width), 0, 1);
    const y = clamp((event.clientY - top) / Math.max(1, height), 0, 1);
    const [hue, saturation, value] = session.hsv;
    select(zone.element.dataset.drag === 'hue'
      ? [Math.round(x * 360), saturation, value]
      : [hue, Math.round(x * 100), Math.round((1 - y) * 100)]);
  }

  dialog.addEventListener('click', event => {
    const button = event.target.closest('button');
    if (button === eyedrop) void sample();
    else if (button?.hasAttribute('data-picker-close')) close(button.dataset.pickerClose === 'keep');
  });
  dialog.addEventListener('cancel', event => {
    event.preventDefault();
    if (session?.sampling) session.sampling.abort();
    else close();
  });
  dialog.addEventListener('contextmenu', event => {
    event.preventDefault();
    close();
  });
  dialog.addEventListener('input', ({ target }) => {
    if (!session) return;
    if (target === hex) {
      const accent = (hex.value.startsWith('#') ? hex.value : `#${hex.value}`).toLowerCase();
      if (isHex(accent)) select(toHsv(accent), accent, hex);
    } else if (numbers.includes(target) && Number.isFinite(target.valueAsNumber)) {
      const hsv = [...session.hsv];
      const index = Number(target.dataset.hsv);
      hsv[index] = clamp(Math.round(target.valueAsNumber), 0, Number(target.max));
      select(hsv, undefined, target);
    }
  });
  dialog.addEventListener('focusout', ({ target }) => {
    if (session && (target === hex || numbers.includes(target))) paint();
  });
  dialog.addEventListener('pointerdown', event => {
    const element = event.target.closest('[data-drag]');
    if (!session || session.drag || !element || event.button !== 0) return;
    session.drag = { element, pointerId: event.pointerId };
    element.setPointerCapture(event.pointerId);
    drag(event);
  });
  dialog.addEventListener('pointermove', drag);
  dialog.addEventListener('lostpointercapture', event => {
    if (session?.drag?.pointerId === event.pointerId) session.drag = undefined;
  });

  return { open, close };
}

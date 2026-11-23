import assert from 'node:assert/strict';
import test from 'node:test';
import { createAccentPicker } from '../desktop/renderer/accent-picker.js';
import { applyLook, normalizeDesktop, SWATCHES } from '../desktop/renderer/appearance.js';
import { appearanceView } from '../desktop/renderer/views.js';

function pickerFixture(EyeDropper) {
  const callbacks = new Map();
  const node = () => ({ style: {}, focus() {}, select() {} });
  const svKnob = node();
  const hex = node();
  const eyedrop = { ...node(), closest: () => eyedrop };
  const error = node();
  const keep = { closest: () => keep, hasAttribute: () => true, dataset: { pickerClose: 'keep' } };
  const children = {
    '.sv': { ...node(), querySelector: () => svKnob },
    '.hue .knob': node(), '.preview-chip': node(), '[data-phex]': hex,
    '[data-eyedrop]': eyedrop, '[data-picker-error]': error,
  };
  const dialog = {
    querySelector: selector => children[selector], querySelectorAll: () => [],
    addEventListener: (type, callback) => callbacks.set(type, callback),
    showModal() { this.open = true; }, close() { this.open = false; },
  };
  const emit = (type, target = dialog) => {
    let prevented = false;
    callbacks.get(type)({ target, preventDefault() { prevented = true; } });
    return prevented;
  };
  const colors = [];
  const picker = createAccentPicker(dialog, color => colors.push(color), EyeDropper);
  return { picker, dialog, hex, eyedrop, error, keep, colors, emit, chip: children['.preview-chip'] };
}

test('legacy appearance defaults to blur and a saved pure transparency choice is preserved', () => {
  const look = normalizeDesktop({ look: { accent: '#86D6F9', transparency: 59, scale: 95, radius: 36 } }).look;
  assert.equal(look.blur, true);
  const settings = normalizeDesktop({ look: { ...look, blur: false } });
  assert.deepEqual(settings.look, { ...look, blur: false });
  const root = { dataset: {}, style: { setProperty() {} } };
  applyLook(root, settings.look);
  assert.equal(root.dataset.blur, 'false');

  const html = appearanceView(settings, 'look', false);
  assert.match(html, /data-look-choice="blur:false" aria-pressed="true"/);
  assert.match(html, /data-hex value="#86D6F9"/);
  assert.doesNotMatch(html, /\bstyle=/);
  for (const color of SWATCHES) assert.ok(html.includes(`data-swatch="${color}"`));
});

test('picker preserves exact HEX colors and Escape and right click restore the opening draft', () => {
  const { picker, dialog, hex, eyedrop, keep, colors, emit, chip } = pickerFixture();
  assert.ok(eyedrop.hidden);
  let returnedFocus = false;
  picker.open('#86d6f9', { focus() { returnedFocus = true; } });
  assert.equal(hex.value, '#86D6F9');
  assert.equal(chip.style.backgroundColor, '#86d6f9');
  hex.value = '#86D6F8';
  emit('input', hex);
  assert.equal(chip.style.backgroundColor, '#86d6f8');
  hex.value = '#86';
  emit('input', hex);
  emit('focusout', hex);
  assert.equal(hex.value, '#86D6F8');
  assert.equal(emit('cancel'), true);
  assert.equal(dialog.open, false);
  assert.equal(returnedFocus, true);
  assert.deepEqual(colors, ['#86d6f8', '#86d6f9']);

  picker.open('#86d6f9');
  hex.value = 'd7a115';
  emit('input', hex);
  assert.equal(emit('contextmenu'), true);
  assert.deepEqual(colors.slice(-2), ['#d7a115', '#86d6f9']);

  picker.open('#86d6f9');
  hex.value = '#fefdfc';
  emit('input', hex);
  emit('click', keep);
  assert.equal(colors.at(-1), '#fefdfc');
  assert.equal(dialog.open, false);
});

test('screen sampler is explicitly invoked, preserves sRGBHex and ignores a result after cancellation', async () => {
  const requests = [];
  class EyeDropper {
    open({ signal }) {
      return new Promise(resolve => { requests.push({ signal, resolve }); });
    }
  }
  const { picker, dialog, eyedrop, colors, emit } = pickerFixture(EyeDropper);
  picker.open('#86d6f9');
  assert.equal(requests.length, 0);
  emit('click', eyedrop);
  emit('click', eyedrop);
  assert.equal(requests.length, 1);
  assert.equal(emit('cancel'), true);
  assert.equal(requests[0].signal.aborted, true);
  assert.equal(dialog.open, true);
  requests[0].resolve({ sRGBHex: '#abcdef' });
  await Promise.resolve();
  assert.deepEqual(colors, []);

  emit('click', eyedrop);
  requests[1].resolve({ sRGBHex: '#AABCED' });
  await Promise.resolve();
  assert.deepEqual(colors, ['#aabced']);

  emit('click', eyedrop);
  emit('contextmenu');
  picker.open('#245dd1');
  requests[2].resolve({ sRGBHex: '#ebca12' });
  await Promise.resolve();
  assert.deepEqual(colors, ['#aabced', '#86d6f9']);
  assert.equal(dialog.open, true);
});

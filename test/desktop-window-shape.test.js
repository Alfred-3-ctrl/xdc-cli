import assert from 'node:assert/strict';
import test from 'node:test';
import { roundedWindowShape } from '../desktop/window-shape.js';

test('rounded desktop shape clips each corner and follows the logical content bounds', () => {
  const outer = { x: 100, y: 100, width: 466, height: 704 };
  const content = { x: 103, y: 102, width: 460, height: 700 };
  const region = roundedWindowShape(outer, content, 30);
  const hits = (x, y) => region.some(rect =>
    x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height);
  assert.equal(hits(3, 2), false);
  assert.equal(hits(462, 2), false);
  assert.equal(hits(3, 701), false);
  assert.equal(hits(462, 701), false);
  assert.equal(hits(3, 350), true);
  assert.equal(hits(462, 350), true);
  assert.equal(hits(233, 2), true);
  assert.equal(hits(233, 701), true);
  assert.equal(hits(2, 350), false);
  assert.equal(hits(463, 350), false);
});

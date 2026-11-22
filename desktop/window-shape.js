// Electron scales window-local logical coordinates to the monitor's DPI.
// Use horizontal bands to clip the acrylic behind the web view to its CSS radius.
export function roundedWindowShape(windowBounds, contentBounds, radius) {
  const { width, height } = contentBounds;
  const x = contentBounds.x - windowBounds.x;
  const y = contentBounds.y - windowBounds.y;
  const r = Math.min(radius, Math.floor(width / 2), Math.floor(height / 2));
  const shape = [];
  for (let row = 0; row < r; row++) {
    const fromCenter = r - row - 0.5;
    const inset = Math.ceil(r - Math.sqrt(r * r - fromCenter * fromCenter));
    const band = { x: x + inset, width: width - 2 * inset, height: 1 };
    shape.push({ ...band, y: y + row }, { ...band, y: y + height - 1 - row });
  }
  if (height > 2 * r) shape.push({ x, y: y + r, width, height: height - 2 * r });
  return shape;
}

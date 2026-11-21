import { createWriteStream } from 'node:fs';
import { link, mkdir, mkdtemp, rmdir, unlink } from 'node:fs/promises';
import path from 'node:path';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { ApiError } from './api.js';

const WINDOWS_DEVICE = /^(?:con|prn|aux|nul|com[1-9¹²³]|lpt[1-9¹²³])(?:\.|$)/i;

export function safeDownloadName(name, fallback = 'file') {
  const cleaned = String(name ?? '')
    .replace(/[\\/:*?"<>|\u0000-\u001f]/g, '_')
    .replace(/\s+/g, ' ')
    .slice(0, 120)
    .replace(/^[. ]+|[. ]+$/g, '');
  if (!cleaned) return fallback;
  return WINDOWS_DEVICE.test(cleaned) ? `_${cleaned}` : cleaned;
}

/** Stream to a temporary file, then publish it without replacing an existing file. */
export async function saveDownload(response, directory, name, { signal, prefix } = {}) {
  const expected = prefix === undefined ? null : Buffer.from(prefix);
  const invalidContent = () => new ApiError('下载响应不是预期的文件格式', { code: 'INVALID_DOWNLOAD' });
  if (!response.body) throw invalidContent();
  const input = Readable.fromWeb(response.body);
  const dest = path.resolve(directory);
  const fileName = safeDownloadName(name);
  const { name: stem, ext } = path.parse(fileName);
  let tempDir;
  let tempPath;
  try {
    await mkdir(dest, { recursive: true });
    tempDir = await mkdtemp(path.join(dest, '.xdc-'));
    tempPath = path.join(tempDir, 'download');
    const output = createWriteStream(tempPath, { flags: 'wx' });
    if (expected) {
      let checked = 0;
      const validate = new Transform({
        transform(chunk, _encoding, callback) {
          const length = Math.min(chunk.length, expected.length - checked);
          if (!chunk.subarray(0, length).equals(expected.subarray(checked, checked + length))) {
            callback(invalidContent());
            return;
          }
          checked += length;
          callback(null, chunk);
        },
        flush(callback) {
          callback(checked === expected.length ? null : invalidContent());
        },
      });
      await pipeline(input, validate, output, { signal });
    } else {
      await pipeline(input, output, { signal });
    }
    for (let attempt = 0; attempt <= 1000; attempt++) {
      signal?.throwIfAborted();
      const target = path.join(dest, attempt === 0 ? fileName : `${stem} (${attempt})${ext}`);
      try {
        await link(tempPath, target);
        return { path: target, size: output.bytesWritten };
      } catch (error) {
        if (error.code !== 'EEXIST') throw error;
      }
    }
    throw new ApiError('同名文件太多，无法选择新文件名', { code: 'FILE_EXISTS' });
  } finally {
    input.destroy();
    if (tempPath) await unlink(tempPath).catch(() => {});
    if (tempDir) await rmdir(tempDir).catch(() => {});
  }
}

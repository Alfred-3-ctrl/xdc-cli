import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { Api, ApiError } from '../src/api.js';
import { safeDownloadName, saveDownload } from '../src/download.js';
import { downloadLearningAttachments, learningActivityAttachments, listLearningAttachments } from '../src/learning.js';

test('attachment inventory falls back to homework detail and reports failed items', async () => {
  const commonUpload = { id: 21, reference_id: 101, name: '讲义.pdf', allow_download: false };
  const api = {
    courseActivities: async () => [
      { id: 12, type: 'material', uploads: [{ id: 11, name: '课件.pdf', reference_id: 200 }] },
      { id: 24, type: 'homework', uploads: [commonUpload] },
    ],
    homeworkActivities: async () => [{ id: 24, title: '实验' }, { id: 25 }, { id: 26, uploads: [] }],
    homeworkDetail: async (_courseId, id) => {
      assert.notEqual(id, 26, 'empty uploads should not refetch detail');
      if (id === 25) throw new ApiError('无权读取作业详情', { code: 'FORBIDDEN' });
      return { activity: { id, uploads: [{ ...commonUpload, allow_private_wps_office_view: true }] } };
    },
  };
  const result = await listLearningAttachments(api, 678, { includeHomework: true });
  assert.equal(result.materials.length, 1);
  assert.deepEqual(result.attachments.map((file) => [file.id, file.kind]), [[11, 'material'], [21, 'homework']]);
  assert.deepEqual(
    result.attachments.map((file) => [file.activity_id, file.reference_id, file.allow_download, file.wps_preview]),
    [[12, 200, null, false], [24, 101, false, true]],
  );
  assert.equal(result.partial, true);
  assert.deepEqual(result.errors, [{ kind: 'homework', activity_id: 25, code: 'FORBIDDEN', message: '无权读取作业详情' }]);
});

test('original download uses allowed fallback and stops on a server permission denial', async (t) => {
  const routes = [];
  const server = createServer((req, res) => {
    routes.push(req.url);
    switch (req.url) {
      case '/api/uploads/1/url':
      case '/api/uploads/2/url':
        res.writeHead(404).end();
        break;
      case '/api/uploads/reference/11/blob':
        res.writeHead(200, { 'content-type': 'application/pdf' }).end('%PDF-授课资料');
        break;
      case '/api/uploads/reference/12/blob':
        res.writeHead(403).end();
        break;
      default:
        res.writeHead(500).end();
    }
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  t.after(() => server.close());
  const dir = await mkdtemp(path.join(tmpdir(), 'xdc-learning-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const api = new Api({ baseUrl: `http://127.0.0.1:${server.address().port}`, retries: 0 });
  const files = learningActivityAttachments({
    id: 40,
    uploads: [
      { id: 1, reference_id: 11, name: '课件.pdf', allow_download: true },
      { id: 2, reference_id: 12, name: '作业.pdf', allow_download: true, allow_private_wps_office_view: true },
      { id: 3, name: '禁止下载.pdf', allow_download: false },
    ],
  }, 678);
  const result = await downloadLearningAttachments(api, files, dir, { groupByActivity: false });
  assert.equal(result.partial, true);
  assert.equal(result.downloaded.length, 1);
  assert.equal(result.downloaded[0].format, 'original');
  assert.equal(await readFile(result.downloaded[0].path, 'utf8'), '%PDF-授课资料');
  assert.deepEqual(result.failed.map(({ reason }) => reason), ['FORBIDDEN', 'not_downloadable']);
  assert.deepEqual(routes, [
    '/api/uploads/1/url', '/api/uploads/reference/11/blob',
    '/api/uploads/2/url', '/api/uploads/reference/12/blob',
  ]);
});

test('parallel downloads preserve existing files and invalid PDF leaves no file', async (t) => {
  const dir = await mkdtemp(path.join(tmpdir(), 'xdc-learning-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const result = await Promise.all([
    saveDownload(new Response('first'), dir, '课件.pdf'),
    saveDownload(new Response('second'), dir, '课件.pdf'),
  ]);
  assert.notEqual(result[0].path, result[1].path);
  assert.equal(await readFile(result[0].path, 'utf8'), 'first');
  assert.equal(await readFile(result[1].path, 'utf8'), 'second');
  await assert.rejects(
    saveDownload(new Response('Invalid'), dir, '示例.preview.pdf', { prefix: '%PDF-' }),
    { code: 'INVALID_DOWNLOAD' },
  );
  assert.deepEqual((await readdir(dir)).sort(), ['课件 (1).pdf', '课件.pdf']);
  assert.equal(safeDownloadName('CON.pdf'), '_CON.pdf');
});

import assert from 'node:assert/strict';
import { mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { PDFDocument } from 'pdf-lib';
import { ClassroomClient, classroomToken } from '../src/classroom.js';
import { CookieJar } from '../src/http.js';

const image = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jF0sAAAAASUVORK5CYII=', 'base64');
const jpeg = Buffer.from('/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAADAAIBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAAAP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AP//Z', 'base64');

function jarWithSession() {
  const jar = new CookieJar();
  jar.add({
    name: 'classroom_session',
    value: encodeURIComponent('a:1:{i:0;s:6:"_token";i:1;s:13:"example.token";}'),
    domain: 'classroom.zju.edu.cn', path: '/', expires: null,
  });
  return jar;
}

function slideList(names, total = names.length) {
  return Response.json({
    total,
    list: names.map((name) => ({
      content: JSON.stringify({ pptimgurl: `https://cdn.zju.edu.cn/${name}.png` }),
    })),
  });
}

test('classroom uses the classroom cookie for monthly sessions and returns no auth credentials', async () => {
  const jar = jarWithSession();
  assert.equal(classroomToken(jar), 'example.token');
  assert.equal(classroomToken(new CookieJar()), null);
  const client = new ClassroomClient({
    jar,
    fetchImpl: async (input, options) => {
      const url = new URL(input);
      assert.equal(url.origin, 'https://classroom.zju.edu.cn');
      assert.match(options.headers.cookie, /^classroom_session=/);
      assert.equal(options.headers.authorization, 'Bearer example.token');
      if (url.pathname === '/userapi/v1/infosimple') {
        return Response.json({ code: 0, params: { id: 123, account: 'private-account' } });
      }
      assert.equal(url.pathname, '/courseapi/v2/course-live/get-my-course-month');
      assert.equal(url.searchParams.get('month'), '2026-11');
      return Response.json({
        list: [{
          date: '2026-11-17',
          course: [{ id: '77', sub_id: '54', title: '测试课程', sub_title: '第一次课', realname: '测试教师' }],
        }],
      });
    },
  });
  assert.deepEqual(await client.checkSession(), { authenticated: true });
  assert.deepEqual(await client.sessions('2026-11'), [{
    course_id: '77', course_name: '测试课程', session_id: '54', session_name: '第一次课',
    lecturer: '测试教师', date: '2026-11-17',
  }]);
});

test('classroom stops on explicit access denial and malformed slide pagination', async () => {
  const jar = jarWithSession();
  const blocked = new ClassroomClient({
    jar,
    fetchImpl: async () => Response.json({ code: 403, list: [], total: 0 }),
  });
  await assert.rejects(blocked.slides('77', '54'), { code: 'FORBIDDEN' });
  const incomplete = new ClassroomClient({
    jar,
    fetchImpl: async () => slideList(['first'], 2),
  });
  await assert.rejects(incomplete.slides('77', '54'), { code: 'BAD_RESPONSE' });
});

test('classroom paginates slides in API order', async () => {
  const batches = [];
  const client = new ClassroomClient({
    jar: jarWithSession(),
    fetchImpl: async (input) => {
      const page = Number(new URL(input).searchParams.get('page'));
      batches.push(page);
      const names = page === 1 ? Array.from({ length: 100 }, (_, index) => index + 1) : [101];
      return slideList(names, 101);
    },
  });
  const slides = await client.slides('77', '54');
  assert.deepEqual(batches, [1, 2]);
  assert.deepEqual(slides.map(({ page, url }) => [page, url]),
    Array.from({ length: 101 }, (_, index) => [index + 1, `https://cdn.zju.edu.cn/${index + 1}.png`]));
});

test('classroom exports an ordered PDF and leaves no final file for missing pages', async (t) => {
  const dir = await mkdtemp(path.join(tmpdir(), 'xdc-classroom-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  const requestedPages = [];
  let missingSecondPage = false;
  const client = new ClassroomClient({
    jar: jarWithSession(),
    fetchImpl: async (input, { headers }) => {
      const url = new URL(input);
      if (url.hostname === 'classroom.zju.edu.cn') return slideList(['one', 'two']);
      assert.equal(headers.cookie, undefined);
      assert.equal(headers.authorization, undefined);
      requestedPages.push(url.pathname);
      if (missingSecondPage && url.pathname === '/two.png') return new Response('', { status: 404 });
      if (url.pathname === '/two.png') return new Response(jpeg, { headers: { 'content-type': 'image/jpeg' } });
      return new Response(image, { headers: { 'content-type': 'image/png' } });
    },
  });
  const output = path.join(dir, 'lesson.pdf');
  const result = await client.exportPdf('77', '54', output);
  const bytes = await readFile(output);
  assert.deepEqual(result, { path: output, page_count: 2, bytes: bytes.length, course_id: '77', session_id: '54' });
  const pdf = await PDFDocument.load(bytes);
  assert.deepEqual(pdf.getPages().map((page) => page.getSize()), [
    { width: 1, height: 1 }, { width: 2, height: 3 },
  ]);
  assert.deepEqual(requestedPages, ['/one.png', '/two.png']);
  await assert.rejects(client.exportPdf('77', '54', output), { code: 'FILE_EXISTS' });
  assert.deepEqual(await readFile(output), bytes);

  missingSecondPage = true;
  await assert.rejects(client.exportPdf('77', '54', path.join(dir, 'incomplete.pdf')), {
    code: 'CLASSROOM_PARTIAL_EXPORT', missing_pages: [2],
  });
  assert.deepEqual(await readdir(dir), ['lesson.pdf']);
});

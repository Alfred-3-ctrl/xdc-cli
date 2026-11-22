import assert from 'node:assert/strict';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { createDesktopData } from '../desktop/data.js';

const NOW = Date.parse('2026-11-30T02:00:00Z');
const course = {
  id: 14, name: '数据分析', instructor: '陈老师', semester_key: '2026-2027-1',
  start_date: '2026-09-01T00:00:00Z', end_date: '2027-01-15T00:00:00Z',
};
const upcoming = {
  course_id: '23', session_id: '354', date: '2026-12-01',
  course_name: '数据分析', lecturer: '陈老师', session_name: '第 12 节',
};
const recent = { ...upcoming, session_id: '346', date: '2026-11-29', session_name: '第 11 节' };

async function workingDirectory(t) {
  const dir = await mkdtemp(path.join(tmpdir(), 'xdc-desktop-data-'));
  t.after(() => rm(dir, { recursive: true, force: true }));
  return dir;
}

test('dashboard combines upcoming deadlines and nearby classes with newly observed course files', async (t) => {
  const dir = await workingDirectory(t);
  let owner = 'account1';
  let uploads = [{ id: 9, name: '原来.pdf' }];
  const statePath = path.join(dir, 'course-state.json');
  const auth = {
    scope: () => owner,
    getApi: async () => ({
      myCourses: async () => [course],
      homeworkActivities: async () => [
        { id: 400, title: '未交', end_time: '2026-12-01T11:00:00Z', submitted: false },
        { id: 401, title: '已交', end_time: '2026-12-01T11:00:00Z', submitted: true },
        { id: 402, title: '远期', end_time: '2026-12-27T11:00:00Z', submitted: false },
      ],
      courseDetail: async () => ({ ...course, raw: { hidden: true } }),
      courseMaterials: async () => [{ id: 42, type: 'material', uploads }],
    }),
    withClassroom: (run) => run({
      sessions: async (month) => [recent, upcoming].filter(({ date }) => date.startsWith(month)),
    }),
  };
  const data = createDesktopData({ auth, statePath, now: () => NOW, defaultDownloadDir: dir });
  const original = await data.handle('course.detail', { courseId: '14' });
  assert.deepEqual(original.classroom.link, { classroomCourseId: '23', mode: 'automatic' });
  assert.equal(original.course.raw, undefined);
  assert.equal(original.materials.length, 1);
  assert.deepEqual((await data.handle('overview.load')).newMaterials, []);

  uploads = [...uploads, { id: 10, name: '最新.pdf' }];
  await data.handle('course.detail', { courseId: '14', refresh: true });
  const dashboard = await data.handle('overview.load');
  assert.deepEqual(dashboard.deadlines.map(({ id }) => id), [400]);
  assert.deepEqual(dashboard.upcoming, [upcoming]);
  assert.deepEqual(dashboard.recent, [recent]);
  assert.deepEqual(dashboard.newMaterials.map(({ id }) => id), [10]);
  assert.deepEqual(dashboard.errors, []);

  const reopened = createDesktopData({ auth, statePath, now: () => NOW, defaultDownloadDir: dir });
  assert.deepEqual((await reopened.handle('overview.load')).newMaterials.map(({ id }) => id), [10]);
  owner = 'account2';
  assert.deepEqual((await reopened.handle('overview.load')).newMaterials, []);
});

test('ambiguous classroom names require an account-scoped manual link', async (t) => {
  const dir = await workingDirectory(t);
  const sameTeacher = { ...recent, course_id: '26', session_id: '348' };
  const anotherSection = { ...course, id: 15 };
  const auth = {
    scope: () => 'account1',
    getApi: async () => ({
      myCourses: async () => [course, anotherSection],
      homeworkActivities: async () => [],
      courseDetail: async () => course,
      courseMaterials: async () => [],
    }),
    withClassroom: (run) => run({ sessions: async () => [recent, sameTeacher] }),
  };
  const options = {
    auth, statePath: path.join(dir, 'course-state.json'), now: () => NOW, defaultDownloadDir: dir,
  };
  const data = createDesktopData(options);
  assert.equal((await data.handle('course.detail', { courseId: 14 })).classroom.link, null);
  await assert.rejects(data.handle('classroom.link', { courseId: 14, classroomCourseId: '31' }), { code: 'NOT_FOUND' });
  assert.deepEqual(await data.handle('classroom.link', { courseId: 14, classroomCourseId: '26' }), {
    link: { classroomCourseId: '26', mode: 'manual' },
  });
  const reopened = createDesktopData(options);
  const linked = await reopened.handle('course.detail', { courseId: 14 });
  assert.deepEqual(linked.classroom.sessions, [sameTeacher]);
});

test('quiet pause finishes the active file before holding the next queued job', async (t) => {
  const dir = await workingDirectory(t);
  let owner = 'account1';
  const starts = [];
  const resolvers = new Map();
  const auth = {
    scope: () => owner,
    withClassroom: (run) => run({
      sessions: async () => [recent, upcoming],
      async exportPdf(_courseId, sessionId, destination) {
        starts.push(sessionId);
        await new Promise((resolve) => { resolvers.set(sessionId, resolve); });
        return { path: destination, bytes: 22, page_count: 1 };
      },
    }),
  };
  const data = createDesktopData({ auth, now: () => NOW, defaultDownloadDir: dir });
  const enqueue = ({ course_id, session_id }) => data.handle('download.enqueue', {
    source: 'classroom', courseId: course_id, sessionId: session_id, month: '2026-11',
  });
  await enqueue(recent);
  await enqueue(upcoming);
  assert.equal(data.hasActiveJob(), true);
  const drained = data.pause();
  let snapshot = await data.handle('download.jobs');
  assert.equal(snapshot.paused, true);
  assert.deepEqual(snapshot.jobs.map(({ status }) => status), ['running', 'queued']);

  while (!resolvers.has(recent.session_id)) await new Promise((resolve) => setImmediate(resolve));
  resolvers.get(recent.session_id)();
  await drained;
  snapshot = await data.handle('download.jobs');
  assert.equal(data.hasActiveJob(), false);
  assert.deepEqual(starts, [recent.session_id]);
  assert.deepEqual(snapshot.jobs.map(({ status }) => status), ['completed', 'queued']);
  const completed = snapshot.jobs[0];
  assert.equal(data.fileForJob(completed.id), completed.result.path);

  data.resume();
  while (!resolvers.has(upcoming.session_id)) await new Promise((resolve) => setImmediate(resolve));
  await data.handle('download.pause', { paused: true });
  resolvers.get(upcoming.session_id)();
  await data.pause();
  assert.deepEqual(starts, [recent.session_id, upcoming.session_id]);
  assert.deepEqual((await data.handle('download.jobs')).jobs.map(({ status }) => status), ['completed', 'completed']);
  owner = 'account2';
  assert.equal(data.fileForJob(completed.id), null);
  assert.deepEqual((await data.handle('download.jobs')).jobs, []);
});

import assert from 'node:assert/strict';
import test from 'node:test';
import { connectClassroom } from '../src/classroom-auth.js';
import { CookieJar } from '../src/http.js';

test('classroom exchanges an SSO session and rejects unexpected redirect hosts', async () => {
  const jar = new CookieJar();
  const result = await connectClassroom(jar, {
    requestImpl: async (entry, options) => {
      assert.equal(new URL(entry).hostname, 'tgmedia.cmc.zju.edu.cn');
      assert.equal(options.trace, false);
      assert.equal(options.jar, jar);
      options.validateUrl(new URL('https://zjuam.zju.edu.cn/cas/login'));
      assert.throws(() => options.validateUrl(new URL('https://other.example/')), { code: 'CLASSROOM_SSO_REDIRECT' });
      const cookie = encodeURIComponent('{i:0;s:6:"_token";i:1;s:10:"mock-token";}');
      const res = new Response('', { headers: { 'set-cookie': `session=${cookie}; Path=/; Secure` } });
      options.jar.addFromResponse('https://classroom.zju.edu.cn/', res);
      return { res, url: 'https://classroom.zju.edu.cn/' };
    },
  });
  assert.equal(result, jar);
});

test('classroom distinguishes an expired CAS session from a forbidden response', async () => {
  const jar = new CookieJar();
  await assert.rejects(connectClassroom(jar, {
    requestImpl: async () => ({ res: new Response(''), url: 'https://zjuam.zju.edu.cn/cas/login' }),
  }), { code: 'AUTH_REQUIRED' });
  await assert.rejects(connectClassroom(jar, {
    requestImpl: async () => ({ res: new Response('', { status: 403 }), url: 'https://classroom.zju.edu.cn/' }),
  }), { code: 'FORBIDDEN' });
});

test('cookies received during service login retain their host and transport scope', () => {
  const jar = new CookieJar();
  const source = 'https://classroom.zju.edu.cn/auth/finish';
  const headers = new Headers();
  headers.append('set-cookie', 'session=abc; Path=/; Secure');
  headers.append('set-cookie', 'unrelated=xyz; Domain=courses.zju.edu.cn; Path=/');
  headers.append('set-cookie', 'auth=auth-cookie');
  jar.addFromResponse(source, new Response(null, { headers }));
  assert.equal(jar.headerFor('https://classroom.zju.edu.cn/'), 'session=abc');
  assert.equal(jar.headerFor(source), 'auth=auth-cookie; session=abc');
  assert.equal(jar.headerFor('https://classroom.zju.edu.cn/authentication'), 'session=abc');
  for (const url of ['https://courses.zju.edu.cn/', 'https://a.classroom.zju.edu.cn/', 'http://classroom.zju.edu.cn/']) {
    assert.equal(jar.headerFor(url), '');
  }
});

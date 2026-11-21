import assert from 'node:assert/strict';
import { constants, createCipheriv, createDecipheriv, generateKeyPairSync, privateDecrypt } from 'node:crypto';
import test from 'node:test';
import { CookieJar } from '../src/http.js';
import { requestWpsFile } from '../src/wps.js';

const api = { baseUrl: 'https://courses.zju.edu.cn', jar: new CookieJar() };
const attachment = { id: 23, activity_id: 42, wps_preview: true, allow_download: false };
const previewUrl = 'https://course-wps.zju.edu.cn/preview?ticket=preview-fixture';
const gatewayUrl = 'https://course-wps.zju.edu.cn/gateway';
const fileUrl = 'https://tcmedia.zju.edu.cn/out?token=file-fixture';
const { publicKey, privateKey } = generateKeyPairSync('rsa', {
  modulusLength: 2048,
  publicKeyEncoding: { type: 'spki', format: 'pem' },
  privateKeyEncoding: { type: 'pkcs8', format: 'pem' },
});

function previewPage(permission) {
  const data = {
    root_file_id: '23',
    app_id: 'fixture',
    csrf_token: 'csrf-fixture',
    permission,
    encryption: {
      gateway: '/gateway',
      isEncryption: true,
      algorithm: 'AES256',
      publicKey: Buffer.from(publicKey).toString('base64'),
    },
  };
  return new Response(`<script>window.__WPSENV__ = ${JSON.stringify(data)}; after();</script>`);
}

function mockWps(t, permission, gateway, { destination = fileUrl, preview = previewUrl } = {}) {
  const requests = [];
  t.mock.method(globalThis, 'fetch', async (url, opts) => {
    opts = { ...opts, headers: new Headers(opts.headers) };
    requests.push({ url, opts });
    if (url.hostname === 'courses.zju.edu.cn') {
      assert.equal(url.pathname, '/api/uploads/23/preview/private-wps');
      assert.equal(url.searchParams.get('refer_id'), '42');
      return Response.json({ preview_url: preview });
    }
    if (url.href === previewUrl) return previewPage(permission);
    if (url.href === gatewayUrl) return gateway(opts, destination);
    if (url.href === fileUrl) return new Response('%PDF-1.7\nmock content', {
      headers: { 'content-type': 'application/pdf' },
    });
    throw new Error('Unexpected network request in WPS mock');
  });
  return requests;
}

function getGatewayCrypto(headers) {
  // Inspect the fixture's PKCS#1 v1.5 block with its generated private key.
  // Newer OpenSSL builds disable privateDecrypt with v1.5 unpadding.
  const wrapped = Buffer.from(headers.get('encryption-parameters'), 'base64');
  const encoded = privateDecrypt({ key: privateKey, padding: constants.RSA_NO_PADDING }, wrapped);
  assert.equal(encoded[0], 0);
  assert.equal(encoded[1], 2);
  const offset = encoded.indexOf(0, 2);
  assert.ok(offset >= 10);
  const { private_key: key, iv } = JSON.parse(encoded.subarray(offset + 1).toString());
  return {
    decode(text) {
      const decipher = createDecipheriv('aes-256-cbc', key, iv);
      return JSON.parse(Buffer.concat([decipher.update(Buffer.from(text, 'base64')), decipher.final()]));
    },
    encode(data) {
      const cipher = createCipheriv('aes-256-cbc', key, iv);
      return Buffer.concat([cipher.update(JSON.stringify(data)), cipher.final()]).toString('base64');
    },
  };
}

test('WPS uses an explicit printing grant and preserves the encrypted export format', async (t) => {
  const requests = mockWps(t, { read: 1, print: 1, download: 0 }, (opts) => {
    assert.equal(opts.method, 'POST');
    assert.equal(opts.headers.get('x-user-query'), 'ticket=preview-fixture');
    const crypto = getGatewayCrypto(opts.headers);
    assert.deepEqual(crypto.decode(opts.headers.get('api-parameters')), {
      method: 'POST', api: '/api/v3/office/file/23/export', query_string: '', schema: 'https',
    });
    assert.equal(crypto.decode(opts.body).format, 'pdf');
    return new Response(crypto.encode({ url: fileUrl }), { headers: { encryption: '1' } });
  });
  const result = await requestWpsFile(api, attachment);
  assert.match(await result.text(), /^%PDF-/);
  assert.equal(requests.length, 4);
  assert.equal(requests.at(-1).opts.headers.get('referer'), 'https://course-wps.zju.edu.cn/');
  assert.equal(requests.at(-1).opts.headers.has('cookie'), false);
});

test('WPS uses the download action for an authorized original file', async (t) => {
  const requests = mockWps(t, { read: true, download: true }, (opts) => {
    const crypto = getGatewayCrypto(opts.headers);
    assert.equal(crypto.decode(opts.headers.get('api-parameters')).api, '/api/v3/office/file/23/download');
    assert.equal(opts.body, undefined);
    return Response.json({ url: fileUrl });
  });
  const result = await requestWpsFile(api, { ...attachment, allow_download: true }, { mode: 'original' });
  assert.match(await result.text(), /^%PDF-/);
  assert.equal(requests.length, 4);
});

test('a preview flag alone does not authorize printing', async (t) => {
  const requests = mockWps(t, { read: 1 }, () => assert.fail('export gateway must not be used'));
  await assert.rejects(requestWpsFile(api, attachment), { code: 'WPS_PERMISSION_UNKNOWN' });
  assert.equal(requests.length, 2);
});

test('WPS enforces a denied printing flag before export', async (t) => {
  const requests = mockWps(t, { print: 0 }, () => assert.fail('export gateway must not be used'));
  await assert.rejects(requestWpsFile(api, attachment), { code: 'FORBIDDEN' });
  assert.equal(requests.length, 2);
});

test('a denied gateway export is never retried or downloaded', async (t) => {
  const requests = mockWps(t, { print: 1 }, () => Response.json({ message: 'Permission denied' }, { status: 403 }));
  await assert.rejects(requestWpsFile(api, attachment), { code: 'FORBIDDEN', status: 403 });
  assert.equal(requests.length, 3);
});

test('WPS rejects a signed download outside ZJU before making the request', async (t) => {
  const requests = mockWps(t, { print: 1 }, () => Response.json({ url: 'https://course-wps.zju.edu.cn.example.test/' }));
  await assert.rejects(requestWpsFile(api, attachment), { code: 'WPS_INVALID_URL' });
  assert.equal(requests.length, 3);
});

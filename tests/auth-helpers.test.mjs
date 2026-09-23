import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';

test('returnTo จำกัดอยู่ใน Portal และปฏิเสธ URL ที่ไม่ปลอดภัย', () => {
  const { safeReturnPath } = loadTs('lib/auth/safe-return-path.ts');
  for (const [input, expected] of [
    [null, '/portal'], [['/portal', '//evil'], '/portal'], ['/portal', '/portal'], ['/portal/account?tab=mfa', '/portal/account?tab=mfa'],
    ['https://evil.example/portal', '/portal'], ['//evil.example/portal', '/portal'],
    ['/portal/../../login', '/portal'], ['/portal\\evil', '/portal'], ['/portal/%2f%2fevil', '/portal'],
    ['/portal/%zz', '/portal'], ['http://[', '/portal'], ['/portal-x', '/portal'],
    ['/portal/account#secret', '/portal/account'], ['/portal/\naccount', '/portal']
  ]) assert.equal(safeReturnPath(input), expected, String(input));
});

test('stage ที่ยังไม่ครบมาก่อน returnTo', () => {
  const { sessionDestination } = loadTs('lib/auth/session-view.ts');
  assert.equal(sessionDestination({ stage: 'PasswordChangeRequired' }, '/portal/account'), '/auth/change-password?returnTo=%2Fportal%2Faccount');
  assert.equal(sessionDestination({ stage: 'MfaEnrollmentRequired' }, '/portal/account'), '/auth/mfa?returnTo=%2Fportal%2Faccount');
  assert.equal(sessionDestination({ stage: 'MfaChallengeRequired' }, '/portal'), '/auth/mfa?returnTo=%2Fportal');
  assert.equal(sessionDestination({ stage: 'Active' }, '//evil'), '/portal');
});

test('session fetch ไม่ cache ส่ง cookie เฉพาะ origin ที่ตั้งค่า และไม่ตาม redirect', async () => {
  const { fetchSession } = loadTs('lib/auth/session-fetch.ts');
  let calls = 0;
  const actual = await fetchSession('http://127.0.0.1:5080', '__Host-tpr10_session=fixture', async (url, options) => {
    calls++;
    assert.equal(url, 'http://127.0.0.1:5080/api/v1/auth/session');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.redirect, 'error');
    assert.equal(options.headers.Cookie, '__Host-tpr10_session=fixture');
    return Response.json({ userId: 'user-a', stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
  });
  assert.equal(calls, 1);
  assert.equal(actual.userId, 'user-a');
});

test('401 เท่านั้นเป็นไม่มี session; outage/ข้อมูลผิดไม่ยอมให้ผ่าน', async () => {
  const { fetchSession } = loadTs('lib/auth/session-fetch.ts');
  assert.equal(await fetchSession('http://localhost:5080', '', async () => new Response(null, { status: 401 })), null);
  for (const status of [403, 500, 503, 302])
    await assert.rejects(fetchSession('http://localhost:5080', '', async () => new Response(null, { status })));
  await assert.rejects(fetchSession('http://localhost:5080', '', async () => Response.json({ stage: 'Active' })));
  await assert.rejects(fetchSession('https://user:pass@evil.example', '', async () => assert.fail('ห้ามส่ง cookie')));
});

test('mutation ดึง CSRF ใหม่ทุกครั้งและไม่ส่งไป endpoint ภายนอก', async () => {
  const { authMutation } = loadTs('lib/auth/auth-client.ts');
  const original = globalThis.fetch;
  const paths = [];
  let issued = 0;
  globalThis.fetch = async (path, options) => {
    paths.push(path);
    assert.equal(options.credentials, 'same-origin');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.redirect, 'error');
    if (path.endsWith('/csrf')) return Response.json({ token: `csrf-${++issued}` });
    assert.equal(options.headers['X-CSRF-Token'], `csrf-${issued}`);
    return new Response(null, { status: 204 });
  };
  try {
    await authMutation('/api/v1/auth/logout', {});
    await authMutation('/api/v1/auth/logout-all', {});
    await assert.rejects(authMutation('https://evil.example', {}));
    assert.deepEqual(paths, ['/api/v1/auth/csrf', '/api/v1/auth/logout', '/api/v1/auth/csrf', '/api/v1/auth/logout-all']);
  } finally { globalThis.fetch = original; }
});

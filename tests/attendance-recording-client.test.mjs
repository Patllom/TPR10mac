import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const id = '12345678-1234-1234-1234-123456789abc';
const site = { workspaceId: id, projectId: id, siteId: id };
const metadata = { challengeId: id, idempotencyKey: id, site, deviceCapturedAtUtc: '2026-09-26T00:00:00Z', gps: { latitude: 13, longitude: 100, accuracyMeters: 10, sampledAtUtc: '2026-09-26T00:00:00Z' }, confirmContinuousWork: false, expectedDayVersion: null };
function client() { assert.ok(existsSync('lib/attendance/recording-client.ts'), 'recording transport is implemented'); return loadTs('lib/attendance/recording-client.ts'); }
test('server clock/GPS problems map to actionable allowlisted text, never server-supplied HTML', async () => {
  const api = client();
  for (const code of ['invalid-capture-time', 'invalid-gps']) {
    assert.match(await api.recordingResponseError(Response.json({ type: `urn:tpr10:attendance:${code}` }, { status: 400 })), /เวลาอัตโนมัติ/);
  }
  assert.match(await api.recordingResponseError(Response.json({ type: 'urn:tpr10:attendance:challenge-expired' }, { status: 400 })), /หมดอายุ/);
  for (const body of [{ type: 'https://untrusted.invalid/invalid-gps', detail: '<script>secret</script>' }, { type: 123 }]) {
    assert.equal(await api.recordingResponseError(Response.json(body, { status: 400 })), api.recordingError(400));
  }
  assert.equal(await api.recordingResponseError(new Response('not-json', { status: 400 })), api.recordingError(400));
  assert.equal(await api.recordingResponseError(Response.json({ type: 'urn:tpr10:attendance:invalid-gps' }, { status: 503 })), api.recordingError(503));
});
test('capture transport uses exact routes, fresh CSRF and browser multipart boundary', async () => {
  const api = client(); const original = global.fetch, calls = [];
  global.fetch = async (path, options) => { calls.push([path, options]); return path.endsWith('/csrf') ? Response.json({ token: 'synthetic' }) : Response.json({}, { status: 201 }); };
  try {
    const signal = new AbortController().signal;
    await api.issueCaptureChallenge({ action: 'CheckIn', site, idempotencyKey: id }, signal);
    await api.submitCapture('CheckOut', metadata, new Blob(['image'], { type: 'image/jpeg' }), signal);
    assert.deepEqual(calls.map(x => x[0]), ['/api/v1/auth/csrf', '/api/v1/attendance/capture-challenges', '/api/v1/auth/csrf', '/api/v1/attendance/check-outs']);
    const upload = calls[3][1]; assert.ok(upload.body instanceof FormData);
    assert.equal(new Headers(upload.headers).get('Content-Type'), null);
    assert.equal(new Headers(upload.headers).get('X-CSRF-Token'), 'synthetic');
    assert.deepEqual([...upload.body.keys()], ['metadata', 'photo']);
    assert.deepEqual(JSON.parse(await upload.body.get('metadata').text()), metadata);
    for (const [, o] of calls) { assert.equal(o.credentials, 'same-origin'); assert.equal(o.redirect, 'error'); assert.equal(o.cache, 'no-store'); }
  } finally { global.fetch = original; }
});
test('capture rejects arbitrary action/key before requests and does not widen authMutation', async () => {
  const api = client(); const original = global.fetch; let calls = 0; global.fetch = async () => { calls++; throw Error('unexpected'); };
  try {
    const signal = new AbortController().signal;
    await assert.rejects(api.submitCapture('../evidence', metadata, new Blob(), signal));
    await assert.rejects(api.getCaptureOperation('../secret', 'CheckIn', signal));
    await assert.rejects(api.getCaptureOperation(id, 'CheckIn&ownerId=x', signal));
    await assert.rejects(api.issueCaptureChallenge({ action: 'bad', site, idempotencyKey: id }, signal));
    const { authMutation } = loadTs('lib/auth/auth-client.ts');
    await assert.rejects(authMutation('/api/v1/attendance/check-ins', {}));
    assert.equal(calls, 0);
  } finally { global.fetch = original; }
});
test('capture preserves 429 Retry-After and 503 without retry or logout', async () => {
  const api = client(); const original = global.fetch;
  try {
    for (const status of [429, 503]) {
      let requests = 0;
      global.fetch = async path => { requests++; return path.endsWith('/csrf') ? Response.json({ token: 'synthetic' }) : new Response(null, { status, headers: { 'Retry-After': '5' } }); };
      const response = await api.submitCapture('CheckIn', metadata, new Blob(['image']), new AbortController().signal);
      assert.equal(response.status, status); assert.equal(response.headers.get('Retry-After'), '5'); assert.equal(requests, 2);
      assert.match(api.recordingError(status), /[ก-๙]/); assert.doesNotMatch(api.recordingError(503), /เข้าสู่ระบบใหม่/);
    }
  } finally { global.fetch = original; }
});
test('account cancellation after CSRF prevents submitting old capture', async () => {
  const api = client(); const original = global.fetch, controller = new AbortController(); let count = 0;
  global.fetch = async () => { count++; controller.abort(); return Response.json({ token: 'synthetic' }); };
  try { await assert.rejects(api.submitCapture('CheckIn', metadata, new Blob(), controller.signal)); assert.equal(count, 1); }
  finally { global.fetch = original; }
});
test('capture timeout is uncertain, bounded to 75s and never resubmitted', async () => {
  const api = client(); const original = global.fetch, timeout = AbortSignal.timeout, durations = []; let count = 0;
  AbortSignal.timeout = ms => { durations.push(ms); return new AbortController().signal; };
  global.fetch = async path => { count++; if (path.endsWith('/csrf')) return Response.json({ token: 'synthetic' }); throw new DOMException('timeout', 'TimeoutError'); };
  try {
    await assert.rejects(api.submitCapture('CheckIn', metadata, new Blob(), new AbortController().signal), error => error.name === 'CaptureUncertainError' && error.idempotencyKey === id);
    assert.deepEqual(durations, [10000, 75000]); assert.equal(count, 2);
  } finally { global.fetch = original; AbortSignal.timeout = timeout; }
});
test('operation status uses bound read route and caller cancellation', async () => {
  const api = client(); const original = global.fetch; const calls = [];
  global.fetch = async (url, options) => { calls.push([url, options]); return Response.json({ state: 'Processing', receipt: null }); };
  try {
    await api.getCaptureOperation(id, 'CheckOut', new AbortController().signal);
    assert.equal(calls[0][0], `/api/v1/attendance/operations/${id}?action=CheckOut`);
    assert.equal(calls[0][1].cache, 'no-store');
    const c = new AbortController(); c.abort(); await assert.rejects(api.getCaptureOperation(id, 'CheckIn', c.signal)); assert.equal(calls.length, 1);
  } finally { global.fetch = original; }
});

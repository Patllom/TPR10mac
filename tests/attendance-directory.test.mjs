import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';
import { spawnSync } from 'node:child_process';
const id = '12345678-1234-1234-1234-123456789abc';
const row = { id, userId: id, workspaceId: id, departmentId: id, validFromUtc: '2026-01-01T00:00:00Z', validToUtc: null, version: 1 };
const page = items => ({ items, page: 1, pageSize: 25, total: items.length });
test('directory discriminated parser rejects malformed and surplus data', () => {
  const { isDirectoryPage } = loadTs('lib/attendance/directory-view.ts');
  assert.equal(isDirectoryPage(page([row]), 'memberships'), true);
  assert.equal(isDirectoryPage(page([{ ...row, photo: 'secret' }]), 'memberships'), false);
  assert.equal(isDirectoryPage(page([row]), 'reporting-lines'), false);
  for (const change of [{ id: 'bad' }, { version: 0 }, { version: Number.MAX_SAFE_INTEGER + 1 }, { validToUtc: 'bad' }, { validFromUtc: 'bad' }])
    assert.equal(isDirectoryPage(page([{ ...row, ...change }]), 'memberships'), false);
  assert.equal(isDirectoryPage({ ...page([]), extra: 1 }, 'memberships'), false);
  assert.equal(isDirectoryPage({ ...page([]), pageSize: 101 }, 'memberships'), false);
  assert.equal(isDirectoryPage(page([{ id, label: 'คนทดสอบ' }]), 'options'), true);
});
test('ended or future rows are read-only', () => {
  const { isCurrentRow } = loadTs('lib/attendance/directory-view.ts');
  const now = Date.parse('2026-02-01T00:00:00Z');
  assert.equal(isCurrentRow(row, now), true);
  assert.equal(isCurrentRow({ ...row, validToUtc: '2026-02-01T00:00:00Z' }, now), false);
  assert.equal(isCurrentRow({ ...row, validFromUtc: '2027-01-01T00:00:00Z' }, now), false);
});
test('only exact directory POST mutations reach transport', async () => {
  const { authMutation } = loadTs('lib/auth/auth-client.ts');
  const original = global.fetch; const calls = [];
  global.fetch = async (path, options) => { calls.push([path, options]); return path.endsWith('/csrf') ? Response.json({ token: 'synthetic' }) : new Response(null, { status: 204 }); };
  try {
    for (const resource of ['memberships', 'reporting-lines', 'hr-assignments'])
      for (const suffix of ['', `/${id}/end`]) assert.equal((await authMutation(`/api/v1/attendance/directory/${resource}${suffix}`, {})).status, 204);
    assert.equal(calls.length, 12); calls.length = 0;
    for (const path of [`/api/v1/attendance/directory/memberships/${id}/delete`, '/api/v1/attendance/directory/../auth/logout', 'https://localhost/api/v1/attendance/directory/memberships', '/api/v1/attendance/directory/memberships?x=1', '/api/v1/attendance/directory/options/users'])
      await assert.rejects(authMutation(path, {}));
    await assert.rejects(authMutation('/api/v1/attendance/directory/memberships', {}, 'PATCH'));
    assert.equal(calls.length, 0);
  } finally { global.fetch = original; }
});
test('query rejects an invalid response and preserves abort', async () => {
  const { readDirectory } = loadTs('lib/attendance/directory-client.ts');
  const original = global.fetch;
  global.fetch = async () => Response.json(page([{ ...row, extra: true }]));
  try {
    await assert.rejects(readDirectory('memberships', new URLSearchParams(), new AbortController().signal));
    const controller = new AbortController(); controller.abort();
    await assert.rejects(readDirectory('memberships', new URLSearchParams(), controller.signal));
  } finally { global.fetch = original; }
});
test('HTTPS entrypoints reject arbitrary specs and unsupported ports before starting services', () => {
  for (const args of [
    ['infra/nginx/smoke-identity-https.mjs', '4000', '--spec', 'tests/e2e/attendance-directory.spec.ts'],
    ['infra/nginx/smoke-attendance-directory-https.mjs', '4000', '--spec', 'arbitrary.ts'],
    ['infra/nginx/smoke-attendance-directory-https.mjs', '5999']
  ]) {
    const result = spawnSync(process.execPath, args, { encoding: 'utf8', timeout: 5000 });
    assert.equal(result.status, 1);
    assert.match(result.stderr, /allowlist|ไม่รับ --spec|4000 หรือ 4001/);
    assert.equal(result.stdout, '');
  }
});

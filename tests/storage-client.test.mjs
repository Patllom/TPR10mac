import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';
const id = '12345678-1234-1234-1234-123456789abc';
const location = { id, alias: 'local-a', kind: 'local-folder', version: 1, acceptWrites: true, health: 'unknown', checkedAtUtc: null };
test('storage POST transport permits only the five documented mutation shapes', async () => {
  const { authMutation } = loadTs('lib/auth/auth-client.ts');
  const original = global.fetch, calls = [];
  global.fetch = async (path, options) => { calls.push([path, options]); return path.endsWith('/csrf') ? Response.json({ token: 'synthetic' }) : new Response(null, { status: 204 }); };
  try {
    for (const suffix of ['locations', `locations/${id}/probe`, 'write-target', 'migrations', `migrations/${id}/resume`])
      assert.equal((await authMutation('/api/v1/attendance/storage/' + suffix, {})).status, 204);
    assert.equal(calls.length, 10); calls.length = 0;
    for (const suffix of ['health', 'options', `locations/${id}`, `locations/${id}/delete`, `migrations/${id}`, 'locations?x=1', '../evidence'])
      await assert.rejects(authMutation('/api/v1/attendance/storage/' + suffix, {}));
    await assert.rejects(authMutation('/api/v1/attendance/storage/locations', {}, 'PATCH'));
    assert.equal(calls.length, 0);
  } finally { global.fetch = original; }
});
test('storage views fail closed for unknown variants, extra paths and unsafe versions', () => {
  const { parseStorage, storageReady } = loadTs('lib/attendance/storage-view.ts');
  const page = items => ({ items, offset: 0, hasMore: false });
  assert.equal(parseStorage('locations', page([location])).items[0].id, id);
  assert.equal(storageReady(location), false);
  for (const change of [{ health: 'healthy' }, { kind: 'cloud' }, { rootPath: '/private' }, { version: 0 }, { version: 1e20 }])
    assert.throws(() => parseStorage('locations', page([{ ...location, ...change }])));
  const ready = { ...location, health: 'ready', checkedAtUtc: new Date().toISOString() };
  assert.equal(storageReady(ready), true);
  assert.equal(storageReady({ ...ready, acceptWrites: false }), false);
  assert.equal(storageReady({ ...ready, checkedAtUtc: '2000-01-01T00:00:00Z' }), false);
  assert.equal(parseStorage('write-target', { storageId: null, version: 0 }).storageId, null);
  assert.throws(() => parseStorage('write-target', { storageId: '../secret', version: 1 }));
  assert.equal(parseStorage('migrations', page([{ id, status: 'Blocked', version: 1, total: 2, verified: 1, blocked: 1 }])).items[0].status, 'Blocked');
  assert.throws(() => parseStorage('migrations', page([{ id, status: 'Deleted', version: 1, total: 2, verified: 1, blocked: 1 }])));
});
test('storage reads reject arbitrary routes and malformed responses and preserve cancellation', async () => {
  const { readStorage } = loadTs('lib/attendance/storage-client.ts');
  const original = global.fetch; let calls = 0;
  global.fetch = async () => { calls++; return Response.json({ storageId: id, version: 2, rootPath: '/secret' }); };
  try {
    await assert.rejects(readStorage('../evidence', 0, new AbortController().signal));
    assert.equal(calls, 0);
    await assert.rejects(readStorage('write-target', 0, new AbortController().signal));
    const controller = new AbortController(); controller.abort();
    await assert.rejects(readStorage('locations', 0, controller.signal));
    assert.equal(calls, 1);
  } finally { global.fetch = original; }
});

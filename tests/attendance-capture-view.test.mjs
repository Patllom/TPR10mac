import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';
const load = () => loadTs('lib/attendance/capture-view.ts');
const id = '11111111-1111-4111-8111-111111111111';
test('capture context rejects malformed dates, versions and non-own open rounds', () => {
  const { parseCurrent } = load();
  const input = { openDay: null, todayCompleted: false, serverNowUtc: '2026-09-26T01:00:00Z' };
  assert.equal(parseCurrent(input).openDay, null);
  for (const invalid of [{ ...input, todayCompleted: 'false' }, { ...input, serverNowUtc: 'yesterday' }, { ...input, openDay: { id, version: 0 } }]) assert.throws(() => parseCurrent(invalid));
  const openDay = { id, employeeId: id, version: 1, workDate: '2026-09-25', readBasis: 1, out: null, effectiveOutUtc: null, effectiveInUtc: '2026-09-25T01:00:00Z' };
  assert.deepEqual(parseCurrent({ ...input, openDay }).openDay, { id, version: 1, workDate: '2026-09-25' });
  for (const change of [{ readBasis: 2 }, { readBasis: 3 }, { workDate: '2026-02-30' }, { version: Number.MAX_SAFE_INTEGER + 1 }, { effectiveOutUtc: '2026-09-26T01:00:00Z' }]) assert.throws(() => parseCurrent({ ...input, openDay: { ...openDay, ...change } }));
});
test('GPS accepts poor accuracy but refuses old future nonfinite and invalid coordinates', () => {
  const { gpsSample } = load(); const now = Date.parse('2026-09-26T01:00:00Z');
  const position = { timestamp: now, coords: { latitude: 13, longitude: 100, accuracy: 150 } };
  assert.equal(gpsSample(position, now).accuracyMeters, 150);
  assert.ok(gpsSample({ ...position, timestamp: now - 60000 }, now));
  for (const value of [{ ...position, timestamp: now - 60001 }, { ...position, timestamp: now + 1 }, { ...position, coords: { ...position.coords, accuracy: 0 } }, { ...position, coords: { ...position.coords, latitude: NaN } }, { ...position, coords: { ...position.coords, longitude: 181 } }]) assert.throws(() => gpsSample(value, now));
});
test('challenge validates UUID and bounded lifetime; receipt/status cannot invent success', () => {
  const { parseChallenge, parseOperation, parseReceipt, bangkokDate } = load();
  assert.equal(parseChallenge({ id, serverNowUtc: '2026-09-26T01:00:00Z', expiresAtUtc: '2026-09-26T01:02:00Z' }).id, id);
  assert.throws(() => parseChallenge({ id, serverNowUtc: '2026-09-26T01:00:00Z', expiresAtUtc: '2026-09-26T01:03:00Z' }));
  assert.throws(() => parseOperation({ state: 'Committed', receipt: null }));
  assert.throws(() => parseOperation({ state: 'Unknown', receipt: null }));
  assert.throws(() => parseReceipt({ operationId: id, occurredAtUtc: 'bad' }));
  assert.equal(bangkokDate('2026-09-25T17:00:00Z'), '2026-09-26');
});
test('Site choices use exact assignment capability not parent or system permission', () => {
  const { parseSitePage } = load();
  const row = { scope: { workspaceId: id, projectId: id, siteId: id, isValid: true }, workspaceName: 'หน่วย', projectName: 'โครงการ', siteName: 'ไซต์', capabilities: ['attendance:record'] };
  const value = { items: [row, { ...row, scope: { ...row.scope, siteId: null } }, { ...row, capabilities: ['attendance:storage-manage'] }], total: 3, pageNumber: 1, pageSize: 100 };
  assert.equal(parseSitePage(value, 1).items.length, 1);
  assert.throws(() => parseSitePage({ ...value, pageNumber: 2 }, 1));
});

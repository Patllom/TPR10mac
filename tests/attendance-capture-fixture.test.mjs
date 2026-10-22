import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';

test('synthetic challenge stays within parser lifetime when clock advances between reads', () => {
  const { captureChallenge } = loadTs('tests/e2e/fixtures/capture-challenge.ts');
  const { parseChallenge } = loadTs('lib/attendance/capture-view.ts');
  const id = '55555555-5555-4555-8555-555555555555';
  let tick = Date.parse('2026-09-26T01:00:00Z');
  const payload = captureChallenge(id, 120000, () => tick++);
  assert.doesNotThrow(() => parseChallenge(payload));
  assert.equal(payload.serverNowUtc, '2026-09-26T01:00:00.000Z');
  assert.equal(payload.expiresAtUtc, '2026-09-26T01:02:00.000Z');
  assert.throws(() => parseChallenge({ ...payload, expiresAtUtc: '2026-09-26T01:02:00.001Z' }));
});

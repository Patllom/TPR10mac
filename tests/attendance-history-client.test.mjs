import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';

test('history transport uses allowlisted same-origin GET, no-store and abort signal', async t => {
  const { readAttendanceHistory } = loadTs('lib/attendance/history-client.ts');
  const calls = [];
  t.mock.method(globalThis, 'fetch', async (url, options) => { calls.push({ url, options }); return new Response(JSON.stringify({ items: [], nextCursor: null })); });
  const controller = new AbortController();
  assert.deepEqual(await readAttendanceHistory('Hr', 'from=2026-09-01&to=2026-09-30', controller.signal), { items: [], nextCursor: null });
  assert.equal(calls[0].url, '/api/v1/attendance/hr?from=2026-09-01&to=2026-09-30');
  for (const [key, value] of Object.entries({ credentials: 'same-origin', cache: 'no-store', redirect: 'error' })) assert.equal(calls[0].options[key], value);
  controller.abort(); assert.equal(calls[0].options.signal.aborted, true);
  await assert.rejects(() => readAttendanceHistory('//evil.example', '', new AbortController().signal));
  assert.equal(calls.length, 1);
});
test('history errors retain status without interpreting 503 as logout or returning malformed data', async t => {
  const { readAttendanceHistory, HistoryError } = loadTs('lib/attendance/history-client.ts');
  let response = new Response('{}', { status: 503 });
  t.mock.method(globalThis, 'fetch', async () => response);
  await assert.rejects(() => readAttendanceHistory('Mine', '', new AbortController().signal), e => e instanceof HistoryError && e.status === 503);
  response = new Response('{}');
  await assert.rejects(() => readAttendanceHistory('Mine', '', new AbortController().signal));
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const api = existsSync('lib/field-advances/request-client.ts') ? loadTs('lib/field-advances/request-client.ts') : {};
const user = '11111111-1111-4111-8111-111111111111', id = '22222222-2222-4222-8222-222222222222';
const scope = { workspaceId: user, projectId: id };
const content = { kind: 'Individual', purpose: 'Survey', fieldStart: '2026-10-24', fieldEnd: '2026-10-25', neededOn: '2026-10-23', teamPersonIds: [], lines: [{ id, categoryId: id, description: 'Travel', mode: 'LumpSum', quantity: null, unitRate: null, lumpSum: '9999999999999999.99' }] };
const row = { id, revisionId: user, version: 1, state: 'Draft', scope, ownerPersonId: user, delegatePersonId: null, content, estimate: { total: '9999999999999999.99', lines: [{ id, categoryId: id, description: 'Travel', mode: 'LumpSum', quantity: null, unitRate: null, amount: '9999999999999999.99' }] }, holdCode: null, currentStep: null };
const meta = { commandId: id, expectedVersion: 0, reason: '' };
const view = existsSync('lib/field-advances/request-view.ts') ? loadTs('lib/field-advances/request-view.ts') : {};
test('Queue mode only selects a capability supported by the current destination and scope', () => {
  for (const [capabilities, review, preferred, expected] of [
    [['request.create'], true, 'review', null],
    [['request.approve'], true, 'review', 'review'],
    [['request.approve'], false, 'mine', null],
    [['request.create'], false, 'delegated', 'mine'],
    [['request.delegate'], false, 'mine', 'delegated'],
    [['report.read'], false, 'mine', 'report'],
    [['request.create', 'request.delegate'], false, 'delegated', 'delegated'],
    [[], false, 'report', null],
  ]) assert.equal(view.requestQueueMode(capabilities, review, preferred), expected);
});
test('Approved is awaiting payment and content diff detects lexical money edits without floating point', () => {
  assert.equal(typeof view.requestStateLabel, 'function');
  assert.equal(view.requestStateLabel('Approved'), 'อนุมัติแล้ว รอดำเนินการจ่าย');
  assert.deepEqual(view.diffRequestContent(content, structuredClone(content)), []);
  const reordered = { ...content, lines: [Object.fromEntries(Object.entries(content.lines[0]).reverse())] };
  assert.deepEqual(view.diffRequestContent(content, reordered), []);
  assert.deepEqual(view.diffRequestContent(content, { ...content, purpose: 'งานใหม่', lines: [{ ...content.lines[0], lumpSum: '9999999999999999.98' }] }), ['รายละเอียดงาน: Survey → งานใหม่', 'รายการประมาณการ 1 เปลี่ยนแปลง']);
});
test('Team picker uses separate scoped bounded endpoint and abort signal', async () => {
  const calls = [];
  const c = client(async (url, options) => { calls.push({ url, ...options }); return Response.json([{ id, name: 'พนักงาน' }]); });
  assert.equal(typeof c.teamPeople, 'function');
  assert.deepEqual(await c.teamPeople(scope, 'พน', 2, 7, new AbortController().signal), [{ id, name: 'พนักงาน' }]);
  assert.match(calls[0].url, /\/team-people\?workspaceId=.*&projectId=.*&prefix=.*&page=2&pageSize=7/);
  assert.ok(calls[0].signal instanceof AbortSignal);
});
test('Screen projection must match current request version before displaying previous content', async () => {
  const c = client(async url => Response.json(url.endsWith('/screen') ? { requestId: id, revisionId: user, version: 2, revisionNumber: 1, previousContent: null, frozenPolicy: null, actions: ['edit'] } : row));
  assert.equal(typeof c.screen, 'function');
  await assert.rejects(() => c.screen(row, new AbortController().signal), e => e.status === 409);
});
function client(handler, account = () => user) {
  assert.equal(typeof api.createRequestClient, 'function');
  return api.createRequestClient(user, async (url, options) => url.endsWith('/session') ? Response.json({ userId: account(), stage: 'Active', permissions: [], mfaVerifiedAtUtc: null }) : url.endsWith('/csrf') ? Response.json({ token: 'csrf' }) : handler(url, options));
}
const revision = { requestId: id, revisionId: user, revisionNumber: 1, scope, submittedAtUtc: '2026-10-23T00:00:00Z', content, estimate: row.estimate, frozenPolicy: null,
  steps: [{ id, levelCode: 'supervisor', order: 1, outcome: 'Returned', decidedByPersonId: user, decidedAtUtc: '2026-10-23T00:00:00Z', decisionReason: 'แก้ไข' }] };
test('Historical revision reads are scoped GETs preserving exact money and step outcomes', async () => {
  const calls = [];
  const c = client(async (url, options) => { calls.push({ url, ...options }); return Response.json(url.includes('/revisions?') ? [{ requestId: id, revisionId: user, revisionNumber: 1, submittedAtUtc: revision.submittedAtUtc }] : revision); });
  assert.equal(typeof c.revision, 'function');
  assert.equal(typeof c.revisions, 'function');
  assert.equal((await c.revision(row, user)).estimate.total, '9999999999999999.99');
  assert.equal((await c.revisions(id, 2, 7))[0].revisionId, user);
  assert.equal(calls[0].url, `/api/v1/field-advances/requests/${id}/revisions/${user}`);
  assert.match(calls[1].url, /page=2&pageSize=7$/);
  assert.ok(calls.every(x => x.method === 'GET' && x.cache === 'no-store'));
});
test('Historical revision rejects wrong request, wrong revision, scope and numeric money', async () => {
  for (const patch of [{ requestId: user }, { revisionId: id }, { scope: { workspaceId: id, projectId: user } }, { estimate: { ...row.estimate, total: 123 } }, { steps: [{ ...revision.steps[0], outcome: 'Paid' }] }]) {
    const c = client(async () => Response.json({ ...revision, ...patch }));
    assert.equal(typeof c.revision, 'function');
    await assert.rejects(() => c.revision(row, user), e => [403, 503].includes(e.status));
  }
});
test('Historical read drops account-switched or revoked results and bounds revision list', async () => {
  let account = user;
  const c = client(async () => { account = id; return Response.json(revision); }, () => account);
  assert.equal(typeof c.revision, 'function');
  await assert.rejects(() => c.revision(row, user), e => e.status === 403 && e.clearProtectedData);
  const revoked = client(async () => Response.json({}, { status: 403 }));
  await assert.rejects(() => revoked.revision(row, user), e => e.clearProtectedData);
  await assert.rejects(() => revoked.revisions(id, 1, 101), e => e.status === 400);
});
test('Drops_response_after_account_switch', async () => {
  let current = user;
  const c = client(async () => { current = id; return Response.json(row); }, () => current);
  await assert.rejects(() => c.details(id), e => e.status === 403 && e.clearProtectedData === true);
});
test('Unknown_result_keeps_original_command_id', async () => {
  const sent = [];
  const c = client(async (_, o) => { sent.push(JSON.parse(o.body).meta.commandId); throw new TypeError('network'); });
  for (let i = 0; i < 2; i++) await assert.rejects(() => c.create({ scope, ownerPersonId: user, content }, meta), e => e.commandId === id && e.unknownResult === true);
  assert.deepEqual(sent, [id, id]);
});
for (const failure of ['account-switch', 401, 403]) test(`POST 201 followed by account check ${failure} retains uncertainty and original payload`, async () => {
  const sent = []; let failAccount = false; let simulateFailure = true;
  const input = { scope, ownerPersonId: user, content };
  const original = JSON.stringify({ input, meta });
  const c = api.createRequestClient(user, async (url, options) => {
    if (url.endsWith('/session')) {
      if (failAccount && failure !== 'account-switch') return Response.json({}, { status: failure });
      return Response.json({ userId: failAccount ? id : user, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    }
    if (url.endsWith('/csrf')) return Response.json({ token: 'csrf' });
    if (options.method === 'POST') { sent.push(options.body); failAccount = simulateFailure; return Response.json(row, { status: 201 }); }
    return Response.json(row);
  });
  await assert.rejects(() => c.create(input, meta), e => {
    assert.equal(e.status, failure === 'account-switch' ? 403 : failure);
    assert.equal(e.commandId, id);
    assert.equal(e.unknownResult, true);
    assert.equal(e.clearProtectedData, true);
    assert.equal(e.refreshRequired, true);
    assert.equal('receipt' in e, false);
    return true;
  });
  assert.deepEqual(sent, [original]); // No automatic retry or replacement ID.
  assert.equal(JSON.stringify({ input, meta }), original);
  failAccount = false; simulateFailure = false;
  await c.create(input, meta); // Explicit reconciliation only after the original account is restored.
  assert.deepEqual(sent, [original, original]);
});
test('Authoritative mutation endpoint 403 is not an unknown result', async () => {
  let sent = 0;
  const c = client(async () => { sent++; return Response.json({}, { status: 403 }); });
  await assert.rejects(() => c.create({ scope, ownerPersonId: user, content }, meta), e =>
    e.status === 403 && e.commandId === id && e.clearProtectedData && !e.unknownResult && !e.refreshRequired);
  assert.equal(sent, 1);
});
for (const failure of ['session-401', 'csrf-403', 'account-switch-before-send']) test(`Pre-send ${failure} is not an unknown result`, async () => {
  let sent = 0; let csrfFetched = false;
  const c = api.createRequestClient(user, async (url) => {
    if (url.endsWith('/session')) {
      if (failure === 'session-401') return Response.json({}, { status: 401 });
      return Response.json({ userId: csrfFetched ? id : user, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    }
    if (url.endsWith('/csrf')) { csrfFetched = true; return Response.json({ token: 'csrf' }, { status: failure === 'csrf-403' ? 403 : 200 }); }
    sent++; return Response.json(row, { status: 201 });
  });
  await assert.rejects(() => c.create({ scope, ownerPersonId: user, content }, meta), e =>
    e.commandId === id && e.clearProtectedData && !e.unknownResult && !e.refreshRequired);
  assert.equal(sent, 0);
});
test('Large_money_remains_string and successful commands refresh current state', async () => {
  const calls = [];
  const c = client(async (url, o) => { calls.push({ url, ...o }); return Response.json(o.method === 'POST' ? row : { ...row, version: 2 }); });
  const result = await c.create({ scope, ownerPersonId: user, content }, meta);
  assert.equal(result.receipt.estimate.total, '9999999999999999.99');
  assert.equal(result.current.version, 2);
  assert.equal(JSON.parse(calls[0].body).input.content.lines[0].lumpSum, '9999999999999999.99');
  assert.equal(calls[0].headers['X-CSRF-Token'], 'csrf');
  assert.equal(calls[0].cache, 'no-store');
  assert.equal(calls[1].url, `/api/v1/field-advances/requests/${id}`);
});
test('Aborted reads never expose a response', async () => {
  const abort = new AbortController();
  const c = client(async () => { abort.abort(); return Response.json(row); });
  await assert.rejects(() => c.details(id, abort.signal));
});
test('Numeric wire money is rejected', async () => {
  const c = client(async () => Response.json({ ...row, estimate: { ...row.estimate, total: 100 } }));
  await assert.rejects(() => c.details(id), e => e.status === 503);
});
for (const status of [401, 403, 409]) test(`HTTP ${status} exposes only safe recovery instructions`, async () => {
  const c = client(async () => Response.json({ detail: 'secret' }, { status }));
  await assert.rejects(() => c.details(id), e => e.status === status && !e.message.includes('secret') && (status === 409 ? e.refreshRequired : e.clearProtectedData));
});
test('A failed current refresh retains the confirmed command ID without returning a stale receipt', async () => {
  const c = client(async (_, options) => options.method === 'POST' ? Response.json(row) : Promise.reject(new TypeError('offline')));
  await assert.rejects(() => c.create({ scope, ownerPersonId: user, content }, meta), e => e.commandId === id && e.refreshRequired && !e.unknownResult);
});
test('Malformed success payload retains command ID because its outcome cannot be verified', async () => {
  const c = client(async () => Response.json({ invalid: true }));
  await assert.rejects(() => c.create({ scope, ownerPersonId: user, content }, meta), e => e.commandId === id && e.unknownResult);
});
test('Quote cannot pair new revision/version with stale content', async () => {
  const c = client(async url => Response.json(url.endsWith('/quote') ? { requestId: id, revisionId: user, version: 2 } : row));
  await assert.rejects(() => c.quote(id), e => e.status === 409 && e.refreshRequired);
});
test('Numeric input is rejected before any transport call', async () => {
  let calls = 0;
  const c = client(async () => { calls++; return Response.json(row); });
  await assert.rejects(() => c.create({ scope, ownerPersonId: user, content: { ...content, lines: [{ ...content.lines[0], lumpSum: 100 }] } }, meta), e => e.status === 400);
  assert.equal(calls, 0);
});
test('Every command uses its exact route, supplied identity and abort signal, then refreshes', async () => {
  const cases = [
    ['update', [id, content, { ...meta, expectedVersion: 1 }], `/${id}`, 'PUT'],
    ['sendToOwner', [id, meta], `/${id}/send-to-owner`, 'POST'],
    ['confirm', [id, 'a'.repeat(64), meta], `/${id}/confirm`, 'POST'],
    ['returnToAuthor', [id, meta], `/${id}/return-to-author`, 'POST'],
    ['review', [id, { outcome: 'Approve', stepId: user }, meta], `/${id}/review`, 'POST'],
    ['revise', [id, meta], `/${id}/revise`, 'POST'],
    ['withdraw', [id, meta], `/${id}/withdraw`, 'POST'],
    ['copy', [id, meta], `/${id}/copy`, 'POST']
  ];
  for (const [method, args, suffix, verb] of cases) {
    const calls = [];
    const c = client(async (url, options) => { calls.push({ url, ...options }); return Response.json(row); });
    await c[method](...args, new AbortController().signal);
    assert.equal(calls.length, 2);
    assert.equal(calls[0].url, '/api/v1/field-advances/requests' + suffix);
    assert.equal(calls[0].method, verb);
    assert.ok(calls[0].signal instanceof AbortSignal);
    const sent = JSON.parse(calls[0].body);
    assert.equal((sent.meta ?? sent).commandId, id);
    assert.equal(calls[1].method, 'GET');
  }
});
test('All discovery reads return one bounded page and forward abort', async () => {
  const calls = [];
  const c = client(async (url, options) => { calls.push({ url, ...options }); return Response.json([]); });
  const signal = new AbortController().signal;
  assert.deepEqual(await c.list(scope, 'review', 2, 7, signal), []);
  assert.deepEqual(await c.scopes(2, 7, signal), []);
  assert.deepEqual(await c.people(scope, 'A B', 2, 7, signal), []);
  assert.deepEqual(await c.history(id, 2, 7, signal), []);
  assert.equal(calls.length, 4);
  assert.match(calls[0].url, /review-queue/);
  for (const call of calls) { assert.match(call.url, /page=2&pageSize=7/); assert.ok(call.signal instanceof AbortSignal); }
  const abort = new AbortController(); abort.abort();
  await assert.rejects(() => c.scopes(1, 20, abort.signal));
  assert.equal(calls.length, 4);
});

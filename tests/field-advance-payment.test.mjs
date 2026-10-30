import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const api = existsSync('lib/field-advances/payment-client.ts') ? loadTs('lib/field-advances/payment-client.ts') : {};
const user = '11111111-1111-4111-8111-111111111111', id = '22222222-2222-4222-8222-222222222222';
const row = { requestId: id, installmentId: null, familyVersion: 1, requestVersion: 1, installmentVersion: null, state: 'Approved', approved: '9999999999999999.99', spendable: '9999999999999999.99', reserved: '0.00', paid: '0.00', available: '9999999999999999.99', holdCodes: [] };
const meta = { commandId: user, familyVersion: 1, requestVersion: 1, installmentVersion: null, reason: 'ทดสอบ' };
function client(handler, account = () => user) {
  assert.equal(typeof api.createPaymentClient, 'function');
  return api.createPaymentClient(user, async (url, options) => url.endsWith('/session') ? Response.json({ userId: account(), stage: 'Active', permissions: [], mfaVerifiedAtUtc: null }) : url.endsWith('/csrf') ? Response.json({ token: 'csrf' }) : handler(url, options));
}
test('Summary uses v1 protected transport and preserves exact decimal strings', async () => {
  const calls = []; const c = client(async (url, options) => { calls.push({ url, ...options }); return Response.json(row); });
  assert.deepEqual(await c.getPaymentSummary(id), row);
  assert.equal(calls[0].url, `/api/v1/field-advances/requests/${id}/payments`);
  assert.equal(calls[0].cache, 'no-store'); assert.equal(calls[0].credentials, 'same-origin');
});
test('Runtime summary boundary rejects numeric money, invalid IDs and unsafe versions', async () => {
  for (const patch of [{ paid: 0 }, { requestId: 12 }, { installmentId: 'wrong' }, { requestVersion: 9007199254740992 }, { familyVersion: '1' }, { installmentVersion: -1 }, { paid: '1.234' }]) {
    const c = client(async () => Response.json({ ...row, ...patch }));
    await assert.rejects(() => c.getPaymentSummary(id));
  }
});
test('Unknown actions and fields, numeric input and empty reasons never send commands', async () => {
  let sent = 0; const c = client(async () => { sent++; return Response.json(row); });
  const input = { amount: '100.00', destination: { channel: 'Cash', recipientPersonId: user, bankCode: null, accountNumber: null, accountHolder: null } };
  for (const [action, payload] of [['drop', meta], ['installments', { input: { ...input, amount: 100 }, meta }], ['installments', { input, meta, paid: '0' }], ['ceiling-activate', { ...meta, reason: '' }], ['start', { ...meta, installmentId: id, familyVersion: Number.MAX_SAFE_INTEGER + 1 }]])
    await assert.rejects(() => c.sendPaymentCommand(id, action, payload), e => e.status === 400);
  assert.equal(sent, 0);
});
test('Account changes while response is pending discard protected data', async () => {
  let actor = user; const c = client(async () => { actor = id; return Response.json(row); }, () => actor);
  await assert.rejects(() => c.getPaymentSummary(id), e => e.status === 403 && e.clearProtectedData);
});
test('Aborted reads never return stale data', async () => {
  const controller = new AbortController(); const c = client(async () => { controller.abort(); return Response.json(row); });
  await assert.rejects(() => c.getPaymentSummary(id, controller.signal));
});
test('Lost command response keeps command ID and never automatically retries', async () => {
  let calls = 0; const c = client(async () => { calls++; throw new TypeError('network'); });
  await assert.rejects(() => c.sendPaymentCommand(id, 'ceiling-activate', meta), e => e.commandId === user && e.unknownResult);
  assert.equal(calls, 1);
});
test('Every supported command selects exact route and refreshes instead of displaying retained receipt', async () => {
  const actual = { amount: '100.00', recipientPersonId: user, paidOn: '2026-10-27', reference: null, evidenceIds: [id] };
  const destination = { channel: 'Cash', recipientPersonId: user, bankCode: null, accountNumber: null, accountHolder: null };
  for (const [action, payload, path] of [
    ['installments', { input: { amount: '100.00', destination }, meta }, `requests/${id}/installments`],
    ['update', { installmentId: user, input: { amount: '100.00', destination }, meta }, `installments/${user}/update`],
    ...['submit', 'start', 'cancel'].map(action => [action, { ...meta, installmentId: user }, `installments/${user}/${action}`]),
    ['review', { installmentId: user, outcome: 'Approve', meta }, `installments/${user}/review`],
    ...['record', 'mismatch'].map(action => [action, { installmentId: user, input: actual, meta }, `installments/${user}/${action}`]),
    ['outcome', { installmentId: user, input: { outcome: 'Unknown', actual: null, evidenceIds: [id] }, meta }, `installments/${user}/outcome`],
    ['metadata', { installmentId: user, input: { paidOn: actual.paidOn, reference: null, evidenceIds: [id] }, meta }, `installments/${user}/metadata`],
    ['takeover', { installmentId: user, evidenceIds: [id], meta }, `installments/${user}/takeover`],
    ...['confirm', 'finalize'].map(action => [action, { ...meta, proposalId: user }, `payment-proposals/${user}/${action}`]),
    ['ceiling-activate', meta, `requests/${id}/ceiling-activate`],
    ['disposition', { outcome: 'TerminateRemaining', meta }, `requests/${id}/disposition`],
  ]) {
    const installment = ['installments', 'update', 'submit', 'review', 'start', 'record', 'outcome', 'mismatch', 'cancel', 'metadata', 'takeover'].includes(action);
    const response = installment ? { ...row, installmentId: user, installmentVersion: 1 } : row;
    const calls = []; const c = client(async (url, options) => { calls.push({ url, ...options }); return Response.json(options.method === 'POST' ? response : installment ? { summary: { ...response, requestVersion: 2 } } : { ...response, requestVersion: 2 }); });
    assert.equal((await c.sendPaymentCommand(id, action, payload)).requestVersion, 2);
    assert.equal(calls[0].url, `/api/v1/field-advances/${path}`); assert.equal(calls[0].headers['X-CSRF-Token'], 'csrf');
    assert.equal(calls[1].method, 'GET'); const sent = JSON.parse(calls[0].body); assert.ok(!('installmentId' in sent) && !('proposalId' in sent));
  }
});
test('Request basis boundary rejects numeric money and invalid source GUID without granting source access', async () => {
  const requests = loadTs('lib/field-advances/request-client.ts');
  const content = { kind: 'Individual', purpose: 'สำรวจ', fieldStart: '2026-10-27', fieldEnd: '2026-10-28', neededOn: '2026-10-27', teamPersonIds: [], lines: [{ id, categoryId: id, description: 'เดินทาง', mode: 'LumpSum', quantity: null, unitRate: null, lumpSum: '100.00' }] };
  const request = { id, revisionId: user, version: 1, state: 'Draft', scope: { workspaceId: user, projectId: id }, ownerPersonId: user, delegatePersonId: null, content, estimate: null, holdCode: null, currentStep: null };
  for (const basis of [{ amount: 100, items: [] }, { amount: '100.00', items: [{ requestId: 10, revisionId: user, approvedAmount: '100.00' }] }, { amount: '100.00', items: [{ requestId: user, revisionId: id, approvedAmount: 100 }] }]) {
    const c = requests.createRequestClient(user, async url => url.endsWith('/session') ? Response.json({ userId: user, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null }) : Response.json({ ...request, basis }));
    await assert.rejects(() => c.details(id));
  }
});
test('Installment catalog exposes scoped proposal IDs and validates review proposal shape', async () => {
  const change = { outcome: null, state: null, reserved: null, metadata: null, payment: null, revisionId: null, approved: null, basis: null };
  const proposal = { id: user, requestId: id, installmentId: user, kind: 'InstallmentReview', state: 'Approved', familyVersion: 1, requestVersion: 1, installmentVersion: 1, reason: 'ตรวจแล้ว', before: change, after: { ...change, outcome: 'Approve' }, evidence: [] };
  const c = client(async () => Response.json({ installments: [], proposals: [proposal], evidence: [] }));
  assert.deepEqual(await c.installments(id), { installments: [], proposals: [proposal], evidence: [] });
});
test('Start refresh retains the current installment state and rejects a different installment', async () => {
  const started = { ...row, installmentId: user, installmentVersion: 2, state: 'Paying' };
  const c = client(async (url, options) => Response.json(options.method === 'POST' ? started : url.endsWith(`/installments/${user}`) ? { summary: { ...started, installmentVersion: 3 } } : row));
  assert.deepEqual(await c.sendPaymentCommand(id, 'start', { ...meta, installmentId: user, installmentVersion: 1 }), { ...started, installmentVersion: 3 });
  const wrong = client(async () => Response.json({ ...started, installmentId: id }));
  await assert.rejects(() => wrong.sendPaymentCommand(id, 'start', { ...meta, installmentId: user, installmentVersion: 1 }), e => e.status === 403);
});
test('Disposition confirmation refreshes actor-bound command status without requesting source-read rights', async () => {
  const calls = []; const c = client(async (url, options) => {
    calls.push(url);
    if (url.endsWith('/payments')) return Response.json({}, { status: 403 });
    return Response.json({ ...row, requestVersion: options.method === 'GET' ? 3 : 2 });
  });
  assert.equal((await c.sendPaymentCommand(id, 'confirm', { ...meta, proposalId: user })).requestVersion, 3);
  assert.equal(calls[1], `/api/v1/field-advances/payment-commands/${user}`);
});
test('Ceiling amendment and supplement use exact request routes and retain decimal input', async () => {
  const content = { kind: 'Individual', purpose: 'สำรวจ', fieldStart: '2026-10-27', fieldEnd: '2026-10-28', neededOn: '2026-10-27', teamPersonIds: [], lines: [{ id, categoryId: id, description: 'เดินทาง', mode: 'LumpSum', quantity: null, unitRate: null, lumpSum: '100.01' }] };
  const calls = [];
  const c = client(async (url, options) => { calls.push({ url, ...options }); return Response.json(row); });
  await c.sendPaymentCommand(id, 'ceiling-amendment', { content, meta });
  assert.equal(calls[0].url, `/api/v1/field-advances/requests/${id}/ceiling-amendment`);
  assert.equal(JSON.parse(calls[0].body).content.lines[0].lumpSum, '100.01');
  const created = { id: user, revisionId: id, version: 1, state: 'Draft', scope: { workspaceId: user, projectId: id }, ownerPersonId: user, delegatePersonId: null, content, estimate: null, holdCode: null, currentStep: null, basis: null };
  const supplementCalls = [];
  const s = client(async (url, options) => { supplementCalls.push({ url, ...options }); return Response.json(url.endsWith('/payments') ? { ...row, requestId: user } : created); });
  assert.equal((await s.sendPaymentCommand(id, 'supplements', { content, meta: { commandId: user, expectedVersion: 0, reason: 'เพิ่มงาน' } })).requestId, user);
  assert.equal(supplementCalls[0].url, `/api/v1/field-advances/requests/${id}/supplements`);
});

test('Payment UI context is account-bound and validates scoped actions', async () => {
  const context = { requestId: id, familyVersion: 1, requestVersion: 1, allowedActions: ['installments'], installments: [], proposals: [] };
  const c = client(async () => Response.json(context));
  assert.deepEqual(await c.getPaymentContext(id), context);
  for (const patch of [{ requestId: user }, { allowedActions: ['admin'] }, { familyVersion: -1 }, { installments: [{ installmentId: user, installmentVersion: 1, allowedActions: ['confirm'] }] }])
    await assert.rejects(() => client(async () => Response.json({ ...context, ...patch })).getPaymentContext(id));
});
test('Payment scope discovery validates capabilities and bounds', async () => {
  const choice = { scope: { workspaceId: user, projectId: id }, workspaceName: 'บริษัท', projectName: 'โครงการ', capabilities: ['payment.record'] };
  assert.deepEqual(await client(async () => Response.json([choice])).paymentScopes(), [choice]);
  await assert.rejects(() => client(async () => Response.json([{ ...choice, capabilities: ['admin'] }])).paymentScopes());
  await assert.rejects(() => client(async () => Response.json([])).paymentScopes(0));
});
test('UI context discards account switches and aborted reads without delivering hints', async () => {
  const value = { requestId: id, familyVersion: 1, requestVersion: 1, allowedActions: ['installments'], installments: [], proposals: [] };
  let actor = user;
  await assert.rejects(() => client(async () => { actor = id; return Response.json(value); }, () => actor).getPaymentContext(id), e => e.clearProtectedData);
  const abort = new AbortController();
  await assert.rejects(() => client(async () => { abort.abort(); return Response.json(value); }).getPaymentContext(id, abort.signal));
  await assert.rejects(() => client(async () => Response.json({ ...value, installments: [{ installmentId: user, installmentVersion: 1, allowedActions: ['start', 'start'] }] })).getPaymentContext(id));
});
test('Mismatch incident remains readable as an incident without changing the paid summary', async () => {
  const change = { outcome: null, state: null, reserved: null, metadata: null, payment: null, revisionId: null, approved: null, basis: null };
  const incident = { id: user, requestId: id, installmentId: user, kind: 'PaymentMismatch', state: 'Pending', familyVersion: 2, requestVersion: 2, installmentVersion: 2, reason: 'พบความคลาดเคลื่อน', before: change, after: change, evidence: [] };
  assert.equal((await client(async () => Response.json({ installments: [], proposals: [incident], evidence: [] })).installments(id)).proposals[0].kind, 'PaymentMismatch');
});

test('Task14 limited workflow context accepts only disposition fields and rejects unknown variants and leaks', async () => {
  const limited = { variant: 'disposition', requestId: id, familyVersion: 1, requestVersion: 2, proposals: [{ proposalId: user, allowedActions: ['confirm'] }] };
  const c = client(async () => Response.json(limited));
  assert.deepEqual(await c.getWorkflowContext(id), limited);
  for (const patch of [{ variant: 'other' }, { approved: '100.00' }, { installments: [] }, { evidence: [] }, { workflow: {} }, { proposals: [{ proposalId: user, allowedActions: ['start'] }] }])
    await assert.rejects(() => client(async () => Response.json({ ...limited, ...patch })).getWorkflowContext(id));
  await assert.rejects(() => c.getPaymentContext(id));
});

test('Task14 full context preserves exact actor hints and rejects extra fields and malformed locks', async () => {
  const value = { variant: 'request', requestId: id, familyVersion: 1, requestVersion: 1, allowedActions: ['supplements', 'terminate-remaining'], installments: [], proposals: [], workflow: { everPaid: true, outcomeUnclear: false, relationKind: 'Normal', pendingSupplementState: null, amendmentState: null, amendmentApproved: true, installmentsCleared: false, ownerPersonId: user, scope: { workspaceId: user, projectId: id } } };
  assert.deepEqual(await client(async () => Response.json(value)).getWorkflowContext(id), value);
  assert.deepEqual((await client(async () => Response.json(value)).getPaymentContext(id)).allowedActions, []);
  for (const patch of [{ extra: 'secret' }, { workflow: { ...value.workflow, history: ['secret'] } }, { workflow: { ...value.workflow, ownerPersonId: 'wrong' } }, { workflow: { ...value.workflow, pendingSupplementState: 'Unknown' } }, { allowedActions: ['grant'] }, { proposals: [{ proposalId: user, allowedActions: ['finalize', 'finalize'] }] }])
    await assert.rejects(() => client(async () => Response.json({ ...value, ...patch })).getWorkflowContext(id));
});

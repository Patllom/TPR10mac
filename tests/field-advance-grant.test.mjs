import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const api = existsSync('lib/field-advances/grant-client.ts') ? loadTs('lib/field-advances/grant-client.ts') : {};
const w = '11111111-1111-4111-8111-111111111111', id = '22222222-2222-4222-8222-222222222222';
const scope = { workspaceId: w, projectId: null };
const spec = { recipientId: id, scope, capabilities: ['report.read'], delegableCapabilities: [], approvalLimit: '9999999999999999.99', startsAt: '2026-10-21T00:00:00Z', endsAt: '2026-10-22T00:00:00Z' };
const meta = { commandId: id, expectedVersion: 1, reason: 'ทดสอบ' };
const row = { id, state: 'Pending', version: 1, spec, assignmentId: null, assignmentVersion: null, effectiveState: 'Pending', proposedBy: w, recipientName: 'ผู้รับ', canReview: false, canApprove: false, canRevoke: false };
function transport(handler) { return async (url, options) => url.endsWith('/session') ? Response.json({ userId: w, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null }) : url.endsWith('/csrf') ? Response.json({ token: 'fixture' }) : handler(url, options); }
function client(handler) { assert.equal(typeof api.createGrantClient, 'function', 'grant client must implement the workflow'); return api.createGrantClient(scope, w, undefined, transport(handler)); }
test('proposal preserves exact string money, CSRF and no-store without retries', async () => {
  let sent;
  const c = client(async (_, options) => { sent = options; return Response.json({ id, state: 'Pending', version: 1, spec }); });
  await c.proposeGrant(spec, meta);
  assert.equal(JSON.parse(sent.body).spec.approvalLimit, '9999999999999999.99');
  assert.equal(sent.headers['X-CSRF-Token'], 'fixture'); assert.equal(sent.cache, 'no-store');
});
for (const bad of [{ endsAt: spec.startsAt }, { startsAt: 'bad' }, { approvalLimit: 100 }, { approvalLimit: '0.001' }, { capabilities: [] }, { capabilities: ['admin'] }]) test(`reject malformed spec ${JSON.stringify(bad)}`, async () => {
  let calls = 0; const c = client(async () => { calls++; return Response.json(row); });
  await assert.rejects(() => c.proposeGrant({ ...spec, ...bad }, meta), e => e.status === 400); assert.equal(calls, 0);
});
test('blank reason and unsafe version do not send commands', async () => {
  let calls = 0; const c = client(async () => { calls++; return Response.json(row); });
  for (const bad of [{ reason: ' ' }, { reason: 'secret\nline' }, { expectedVersion: 0 }]) await assert.rejects(() => c.proposeGrant(spec, { ...meta, ...bad }), e => e.status === 400);
  assert.equal(calls, 0);
});
for (const status of [401, 403, 409, 503]) test(`error ${status} never exposes server payload or retries`, async () => {
  let calls = 0; const c = client(async () => { calls++; return Response.json({ detail: 'OTHER COMPANY SECRET' }, { status }); });
  await assert.rejects(() => c.proposeGrant(spec, meta), e => e.status === status && !e.message.includes('SECRET'));
  assert.equal(calls, 1);
});
test('wrong scope response and numeric money are rejected before rendering', async () => {
  for (const altered of [{ ...spec, scope: { workspaceId: id, projectId: null } }, { ...spec, approvalLimit: 100 }]) {
    await assert.rejects(() => client(async () => Response.json([{ ...row, spec: altered }])).list(), e => [403, 503].includes(e.status));
  }
});
test('review fences seen proposal version and revoke uses separate assignment version', async () => {
  let posts = 0;
  const c = client(async (_, options) => { if (options.method !== 'GET') posts++; return Response.json({ ...row, assignmentId: w, assignmentVersion: 3 }); });
  await assert.rejects(() => c.reviewGrant(id, 'approve', { ...meta, expectedVersion: 2 }), e => e.status === 409);
  await assert.rejects(() => c.revokeGrant(w, { ...meta, expectedVersion: 2 }, id), e => e.status === 409);
  assert.equal(posts, 0);
});
test('account changes during read discard the response', async () => {
  assert.equal(typeof api.createGrantClient, 'function'); let changed = false;
  const c = api.createGrantClient(scope, w, undefined, async url => {
    if (url.endsWith('/session')) return Response.json({ userId: changed ? id : w, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    changed = true; return Response.json([row]);
  });
  await assert.rejects(() => c.list(), e => e.status === 403);
});
test('diff names amount, expiry and capabilities without float conversion', () => {
  assert.equal(typeof api.grantDifferences, 'function');
  const diff = api.grantDifferences(spec, { ...spec, approvalLimit: null, endsAt: null, capabilities: ['payment.record'] }).join('\n');
  assert.match(diff, /9999999999999999.99/); assert.match(diff, /2026-10-22/); assert.match(diff, /report.read.*payment.record/);
});

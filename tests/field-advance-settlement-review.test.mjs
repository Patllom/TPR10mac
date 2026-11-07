import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';

const modulePath = 'lib/field-advances/settlement-work-client.ts';
const work = existsSync(modulePath) ? loadTs(modulePath) : {};
const settlement = loadTs('lib/field-advances/settlement-client.ts');
const view = loadTs('lib/field-advances/settlement-view.ts');
const scopesModule = existsSync('lib/field-advances/settlement-scope-discovery.ts')
  ? loadTs('lib/field-advances/settlement-scope-discovery.ts') : {};
const account = '11111111-1111-4111-8111-111111111111';
const workspace = '22222222-2222-4222-8222-222222222222';
const project = '33333333-3333-4333-8333-333333333333';
const request = '44444444-4444-4444-8444-444444444444';
const entity = '55555555-5555-4555-8555-555555555555';
const item = {
  workspaceId: workspace,
  projectId: project,
  requestId: request,
  entityId: entity,
  roundId: entity,
  revisionId: entity,
  entityVersion: 1,
  requestVersion: 2,
  familyVersion: 3,
  state: 'Pending',
  allowedActions: ['round-approve'],
  detail: null
};

test('review scope discovery reaches a project beyond page one with identity checks per page', async () => {
  assert.equal(typeof scopesModule.discoverSettlementScopes, 'function');
  const calls = [];
  const desired = '66666666-6666-4666-8666-666666666666';
  const load = async (source, page, pageSize) => {
    calls.push(`${source}:${page}:${pageSize}`);
    if (source === 'payment') return [];
    if (page === 1) return Array.from({ length: 100 }, (_, index) => ({
      scope: { workspaceId: workspace, projectId: `70000000-0000-4000-8000-${String(index).padStart(12, '0')}` },
      workspaceName: 'บริษัท', projectName: `โครงการ ${index}`
    }));
    return [{ scope: { workspaceId: workspace, projectId: desired },
      workspaceName: 'บริษัท', projectName: 'โครงการปลายหน้า' }];
  };
  let checks = 0;
  const result = await scopesModule.discoverSettlementScopes(account, 2,
    (page, size) => load('request', page, size),
    (page, size) => load('payment', page, size),
    async () => { checks++; return account; });
  assert.equal(result.choices.length, 101);
  assert.equal(result.choices.at(-1).scope.projectId, desired);
  assert.deepEqual(calls, ['request:1:100', 'request:2:100', 'payment:1:100']);
  assert.equal(checks, calls.length);
});

test('pending review receipt remains discoverable after reload without queue membership or payload', () => {
  const values = new Map();
  const storage = {
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => values.set(key, value),
    removeItem: key => values.delete(key),
    key: index => [...values.keys()][index] ?? null,
    get length() { return values.size; }
  };
  const original = view.createPendingStore(() => storage);
  original.begin(account, request, 'corrections/return', { entityId: entity, secret: 'เงินลับ' },
    { commandId: project, requestVersion: 1, familyVersion: 1, entityVersion: 1, reason: 'เหตุผลลับ' });
  const reloaded = view.createPendingStore(() => storage);
  const [pending] = reloaded.list(account);
  assert.equal(pending.requestId, request);
  assert.equal(pending.action, 'corrections/return');
  assert.equal(pending.entityId, entity);
  assert.equal(pending.command.commandId, project);
  assert.equal(pending.body, undefined);
  assert.equal(reloaded.list(workspace).length, 0);
  assert.equal([...values.values()].join('').includes('เงินลับ'), false);
  reloaded.resolve(account, request, project);
  assert.deepEqual(reloaded.list(account), []);
});

function client(reply, identity = () => account) {
  assert.equal(typeof work.createSettlementWorkClient, 'function');
  return work.createSettlementWorkClient(account, async (url, options) => {
    if (url.endsWith('/session')) return Response.json({ userId: identity(), stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    return reply(url, options);
  });
}

test('assigned queue uses bounded scoped GET and never POSTs on load', async () => {
  const calls = [];
  const c = client(async (url, options) => {
    calls.push([url, options]);
    return Response.json({ items: [item], nextCursor: null });
  });
  const page = await c.queue({ workspaceId: workspace, projectId: project }, 'rounds');
  assert.equal(page.items[0].requestId, request);
  assert.equal(calls.length, 1);
  assert.match(calls[0][0], /settlement-work\?workspaceId=/);
  assert.equal(calls[0][1].method, 'GET');
  assert.equal(calls[0][1].cache, 'no-store');
  assert.equal(calls[0][1].redirect, 'error');
});

test('work client rejects foreign and stale data and clears on account change', async () => {
  await assert.rejects(() => client(async () => Response.json({ items: [{ ...item, workspaceId: project }], nextCursor: null }))
    .queue({ workspaceId: workspace, projectId: project }, 'rounds'));
  await assert.rejects(() => client(async () => Response.json({ items: [{ ...item, entityVersion: -1 }], nextCursor: null }))
    .queue({ workspaceId: workspace, projectId: project }, 'rounds'));
  await assert.rejects(() => client(async () => Response.json({ items: [item], nextCursor: null }), () => project)
    .queue({ workspaceId: workspace, projectId: project }, 'rounds'), error => error.status === 403);
});

test('work client refuses unknown lane, invalid scope and cursor before fetching', async () => {
  let calls = 0;
  const c = client(async () => { calls++; return Response.json({ items: [], nextCursor: null }); });
  await assert.rejects(() => c.queue({ workspaceId: workspace, projectId: project }, '../rounds'));
  await assert.rejects(() => c.queue({ workspaceId: 'bad', projectId: project }, 'rounds'));
  await assert.rejects(() => c.queue({ workspaceId: workspace, projectId: project }, 'rounds', 51));
  assert.equal(calls, 0);
});

test('round review receipt lookup is scoped GET with exact operation identity', async () => {
  const calls = [];
  const c = settlement.createSettlementClient(account, async (url, options) => {
    if (url.endsWith('/session')) return Response.json({ userId: account, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    calls.push([url, options]);
    return Response.json({ commandId: account, operation: 'rounds/review', operationEntityId: entity,
      result: { requestId: request, entityId: workspace, state: 'Submitted', requestVersion: 2, familyVersion: 3, entityVersion: 2 } });
  });
  const result = await c.checkSettlementReceipt(request, 'rounds/review', entity, account);
  assert.equal(result.entityId, workspace);
  assert.equal(calls.length, 1);
  assert.match(calls[0][0], /operation=rounds%2Freview/);
  assert.equal(calls[0][1].method, 'GET');
});

test('finance review receipt lookup retains exact request action and entity', async () => {
  const calls = [];
  const c = settlement.createSettlementClient(account, async (url, options) => {
    if (url.endsWith('/session')) {
      return Response.json({ userId: account, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    }
    calls.push([url, options]);
    return Response.json({
      commandId: account,
      operation: 'incidents/confirm',
      operationEntityId: entity,
      result: {
        requestId: request,
        entityId: entity,
        state: 'Accepted',
        requestVersion: 2,
        familyVersion: 3,
        entityVersion: 2
      }
    });
  });
  const result = await c.checkSettlementReceipt(request, 'incidents/confirm', entity, account);
  assert.equal(result.entityId, entity);
  assert.equal(calls.length, 1);
  assert.match(calls[0][0], /operation=incidents%2Fconfirm/);
  assert.equal(calls[0][1].method, 'GET');
});

test('duplicate comparison uses exact case GET and rejects a foreign document', async () => {
  const calls = [];
  const comparison = {
    caseId: entity,
    caseVersion: 1,
    current: {
      requestId: request, documentId: workspace, version: 1, issuer: 'ร้าน ก',
      documentNumber: '1', hasNoNumber: false, issuedOn: '2026-10-20', amount: '12.00'
    },
    other: {
      requestId: project, documentId: account, version: 1, issuer: 'ร้าน ข',
      documentNumber: '2', hasNoNumber: false, issuedOn: '2026-10-20', amount: '12.00'
    }
  };
  const c = client(async (url, options) => {
    calls.push([url, options]);
    return Response.json(comparison);
  });
  const value = await c.duplicateComparison(request, entity);
  assert.equal(value.other.requestId, project);
  assert.equal(calls.length, 1);
  assert.match(calls[0][0], new RegExp(`settlement-work/duplicates/${request}/${entity}/comparison`));
  assert.equal(calls[0][1].method, 'GET');
  await assert.rejects(() => client(async () => Response.json({ ...comparison, caseId: project }))
    .duplicateComparison(request, entity));
});

test('review proof download uses exact lane, entity and account with no POST', async () => {
  const calls = [];
  const c = client(async (url, options) => {
    calls.push([url, options]);
    return new Response(new Uint8Array([1]), { headers: { 'content-type': 'image/png' } });
  });
  const blob = await c.downloadEvidence('refunds', request, entity, workspace);
  assert.equal(blob.size, 1);
  assert.equal(calls.length, 1);
  assert.match(calls[0][0], new RegExp(`settlement-work/refunds/${request}/${entity}/evidence/${workspace}`));
  assert.equal(calls[0][1].method, 'GET');
  assert.equal(calls[0][1].cache, 'no-store');
  await assert.rejects(() => c.downloadEvidence('duplicates', request, entity, workspace));
});

test('excess disposition receipt lookup stays bound to exact operation and entity', async () => {
  const c = settlement.createSettlementClient(account, async (url) => {
    if (url.endsWith('/session')) return Response.json({ userId: account, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    return Response.json({ commandId: account, operation: 'excess/disposition', operationEntityId: entity,
      result: { requestId: request, entityId: entity, state: 'Resolved', requestVersion: 2,
        familyVersion: 3, entityVersion: 2 } });
  });
  assert.equal((await c.checkSettlementReceipt(request, 'excess/disposition', entity, account)).entityId, entity);
});

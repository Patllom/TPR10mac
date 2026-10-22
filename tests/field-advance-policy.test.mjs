import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';
const view = loadTs('lib/field-advances/policy-view.ts');

test('เงินยอมรับขอบสตางค์ขนาดใหญ่โดยไม่ใช้ float', () => {
  assert.equal(view.money('9999999999999999.99'), true);
  for (const value of ['10000000000000000.00', '0.001', '1e3', '-1', '', 'Infinity', 100]) assert.equal(view.money(value), false, String(value));
});
test('ชื่อสถานะและที่มาเป็นภาษาไทยตรงสัญญา', () => {
  assert.equal(view.statusLabel('Pending'), 'รอตรวจ');
  assert.equal(view.statusLabel('Returned'), 'ส่งกลับแก้ไข');
  assert.equal(view.sourceLabel('Override'), 'กำหนดเฉพาะโครงการ');
  assert.equal(view.sourceLabel('Inherit'), 'ใช้กติกาบริษัทล่าสุด');
});
test('diff อ่านรู้เรื่องเมื่อวัน วงเงิน และสถานะหมวดเปลี่ยน', () => {
  const before = { clearingDays: 7, bands: [{ order: 1, levelCode: 'director', upperInclusive: '1000.00' }], categories: [{ id: 'a', name: 'เดินทาง', active: true, unit: 'วัน', standardRate: '100.00', evidenceTypes: ['receipt'] }] };
  const after = { ...before, clearingDays: 9, bands: [{ ...before.bands[0], upperInclusive: '9999999999999999.99' }], categories: [{ ...before.categories[0], active: false }] };
  const diff = view.differences(before, after).join('\n');
  assert.match(diff, /7 → 9/);
  assert.match(diff, /1000.00 → 9999999999999999.99/);
  assert.match(diff, /เดินทาง.*เปิดใช้ → ปิดใช้/);
  assert.deepEqual(view.differences(before, structuredClone(before)), []);
});
for (const status of [401, 403, 409]) test(`${status} ล้างข้อมูลและบังคับโหลดใหม่`, () => {
  const result = view.failureState({ policies: [{ content: 'ส่วนตัว' }], selected: { id: 'a' }, context: { private: true }, reason: 'ลับ', blocked: false }, status);
  assert.deepEqual(result.policies, []);
  assert.equal(result.selected, null);
  assert.equal(result.context, null);
  assert.equal(result.reason, '');
  assert.equal(result.blocked, true);
});
test('503 ไม่แกล้ง logout แต่ป้องกันส่งซ้ำโดยอัตโนมัติ', () => {
  const state = { policies: [], selected: null, context: null, reason: '', blocked: false };
  assert.equal(view.failureState(state, 503).blocked, true);
});

const { createPolicyClient } = loadTs('lib/field-advances/policy-client.ts');
const w = '11111111-1111-4111-8111-111111111111', p = '22222222-2222-4222-8222-222222222222';
const scope = { workspaceId: w, projectId: null };
const content = { clearingDays: 7, bands: [{ order: 1, levelCode: 'director', upperInclusive: '9999999999999999.99' }], categories: [{ id: p, name: 'เดินทาง', active: true, unit: 'วัน', standardRate: '0.01', evidenceTypes: ['receipt'] }] };
const meta = { commandId: p, expectedVersion: 1, reason: 'จำลอง', policy: { mode: 'Company', sourcePolicyRevisionId: null, baseCompanyVersionId: null, expectedActiveRevisionId: null, expectedActiveVersion: 0, expectedContentHash: null } };
const policy = { id: p, scope, state: 'Draft', version: 1, baseCompanyVersionId: null, content, contentHash: 'a'.repeat(64), mode: 'Company', sourcePolicyRevisionId: null, expectedActiveRevisionId: null, expectedActiveVersion: 0, activeRevisionId: null, activeVersion: 0 };
function transport(handler) { return async (url, options) => url.endsWith('/session') ? Response.json({ userId: w, stage: 'Active', permissions: [], mfaVerifiedAtUtc: '2026-10-21T00:00:00Z' }) : url.endsWith('/csrf') ? Response.json({ token: 'fixture-token' }) : handler(url, options); }
test('client อ่าน exact scope และปฏิเสธ decimal number ที่เสีย precision', async () => {
  let seen;
  const client = createPolicyClient(scope, w, undefined, transport(async url => { seen = url; return Response.json([{ ...policy, content: { ...content, bands: [{ ...content.bands[0], upperInclusive: 100 }] } }]); }));
  await assert.rejects(() => client.loadPolicies());
  assert.match(seen, new RegExp(`workspaceId=${w}`));
});
test('client ส่ง stringmoney และ CSRF โดยไม่เก็บ token', async () => {
  let sent;
  const client = createPolicyClient(scope, w, undefined, transport(async (url, options) => { sent = options; return Response.json(policy); }));
  await client.savePolicy(null, scope, content, meta);
  assert.equal(JSON.parse(sent.body).content.bands[0].upperInclusive, '9999999999999999.99');
  assert.equal(sent.headers['X-CSRF-Token'], 'fixture-token');
  assert.equal(sent.cache, 'no-store');
});
for (const status of [403, 409, 503]) test(`client ${status} ไม่ retry และส่งสถานะให้ UI invalidate`, async () => {
  let mutations = 0;
  const client = createPolicyClient(scope, w, undefined, transport(async () => { mutations++; return Response.json({ code: 'fixture' }, { status }); }));
  await assert.rejects(() => client.savePolicy(null, scope, content, meta), error => error.status === status);
  assert.equal(mutations, 1);
});
test('client scope/account fence หยุดก่อนส่ง mutation', async () => {
  let calls = 0;
  const client = createPolicyClient(scope, w, undefined, async () => { calls++; return Response.json({ userId: p, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null }); });
  await assert.rejects(() => client.savePolicy(null, scope, content, meta), error => error.status === 403);
  assert.equal(calls, 1);
});
test('client update ตรวจ ID ใน scope ก่อน PUT แม้บัญชีมีหลาย scope', async () => {
  let mutations = 0;
  const client = createPolicyClient(scope, w, undefined, transport(async (url, opts) => {
    if (opts.method === 'GET') return Response.json({}, { status: 403 });
    mutations++; return Response.json(policy);
  }));
  await assert.rejects(() => client.savePolicy(p, scope, content, { ...meta, policy: { ...meta.policy, expectedContentHash: policy.contentHash } }), e => e.status === 403);
  assert.equal(mutations, 0);
});
test('client ไม่คืน response บัญชีเดิมเมื่อบัญชีเปลี่ยนระหว่างอ่าน', async () => {
  let changed = false;
  const client = createPolicyClient(scope, w, undefined, async url => {
    if (url.endsWith('/session')) return Response.json({ userId: changed ? p : w, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null });
    changed = true; return Response.json([policy]);
  });
  await assert.rejects(() => client.loadPolicies(), e => e.status === 403);
});
test('อัตราศูนย์ตาม contract เดิมต้องอ่านได้โดยไม่ปฏิเสธฉบับที่ถูกต้อง', () => {
  assert.doesNotThrow(() => view.validateContent({ ...content, categories: [{ ...content.categories[0], standardRate: '0.00' }] }));
});
test('network failure เป็นข้อความไทย ไม่ retry และไม่คืน error ภายใน', async () => {
  let calls = 0;
  const client = createPolicyClient(scope, w, undefined, transport(async () => { calls++; throw new Error('private network internals'); }));
  await assert.rejects(() => client.savePolicy(null, scope, content, meta), e => e.status === 503 && !e.message.includes('private network internals'));
  assert.equal(calls, 1);
});
test('local validation ส่ง400 ให้แก้ข้อมูลโดยไม่ต้อง logout หรือส่ง request', async () => {
  let calls = 0;
  const client = createPolicyClient(scope, w, undefined, async () => { calls++; return Response.json(policy); });
  await assert.rejects(() => client.savePolicy(null, scope, { ...content, clearingDays: 0 }, meta), e => e.status === 400);
  assert.equal(calls, 0);
});

import { currentAccount } from './policy-client';
import { money, sameScope, validId } from './policy-view';
import type { FinanceScope, ScopeChoice } from './policy-types';

export type CommandMeta = { commandId: string; expectedVersion: number; reason: string };

export type GrantSpec = { recipientId: string; scope: FinanceScope; capabilities: string[]; delegableCapabilities: string[]; approvalLimit: string | null; startsAt: string; endsAt: string | null };
export type GrantView = { id: string; state: string; version: number; spec: GrantSpec };
export type GrantRecord = GrantView & { proposedBy: string; recipientName: string; assignmentId: string | null; assignmentVersion: number | null; effectiveState: string; canReview: boolean; canApprove: boolean; canRevoke: boolean };
export type GrantHistory = { proposalId: string; outcome: string; actorId: string; at: string; reason: string };
export type Recipient = { id: string; name: string };
export const grantCapabilities = ['policy.edit.company', 'policy.edit.project', 'policy.review', 'grant.propose', 'grant.review', 'grant.revoke', 'request.approve', 'finance.check', 'payment.record', 'report.read'];
const management = ['grant.propose', 'grant.review', 'grant.revoke'];
const states = ['Pending', 'Draft', 'Rejected', 'Approved', 'Revoked'];
const root = '/api/v1/field-advances/grants';
const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
export class GrantError extends Error {
  constructor(readonly status: number) { super(status === 400 ? 'ข้อมูลไม่ถูกต้อง กรุณาตรวจช่วงเวลา ความสามารถ วงเงิน และเหตุผล' : status === 401 ? 'session หมดอายุ กรุณาเข้าสู่ระบบใหม่' : status === 403 ? 'ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่' : status === 409 ? 'สถานะหรือรุ่นข้อมูลเปลี่ยน กรุณาโหลดใหม่' : 'บริการไม่พร้อม หรือยังยืนยันผลไม่ได้ กรุณาโหลดใหม่ก่อนทำรายการอีกครั้ง'); }
}
function scopeQuery(scope: FinanceScope) {
  if (!scope || !validId(scope.workspaceId) || !(scope.projectId === null || validId(scope.projectId))) throw new GrantError(400);
  return `workspaceId=${scope.workspaceId}${scope.projectId ? `&projectId=${scope.projectId}` : ''}`;
}
function time(v: unknown): v is string { return typeof v === 'string' && /T.*(?:Z|[+-]\d\d:\d\d)$/.test(v) && Number.isFinite(Date.parse(v)); }
export function validateGrant(spec: GrantSpec) {
  if (!spec) throw new GrantError(400);
  scopeQuery(spec.scope);
  if (!validId(spec.recipientId) || !Array.isArray(spec.capabilities) || !spec.capabilities.length || !Array.isArray(spec.delegableCapabilities)
    || [spec.capabilities, spec.delegableCapabilities].some(a => a.length > 10 || new Set(a).size !== a.length || a.some(c => !grantCapabilities.includes(c)))
    || !(spec.approvalLimit === null || money(spec.approvalLimit)) || !time(spec.startsAt)
    || !(spec.endsAt === null || time(spec.endsAt) && Date.parse(spec.endsAt) > Date.parse(spec.startsAt))) throw new GrantError(400);
}
function command(meta: CommandMeta) {
  if (!meta || !validId(meta.commandId) || !Number.isSafeInteger(meta.expectedVersion) || meta.expectedVersion < 1 || typeof meta.reason !== 'string' || !meta.reason.trim() || meta.reason.length > 1000 || /[\x00-\x1f\x7f]/.test(meta.reason)) throw new GrantError(400);
}
function connection(userId: string, signal: AbortSignal | undefined, transport: typeof fetch) {
  const bounded = () => signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
  const account = async () => { signal?.throwIfAborted(); if (await currentAccount(transport, bounded()) !== userId) throw new GrantError(403); };
  return async (path: string, method = 'GET', body?: unknown): Promise<unknown> => {
    try {
      const serialized = body === undefined ? undefined : JSON.stringify(body);
      if (serialized && serialized.length > 65536) throw new GrantError(400);
      await account(); const headers: Record<string, string> = {};
      if (method !== 'GET') {
        const csrf = await transport('/api/v1/auth/csrf', { ...options, signal: bounded() });
        if (!csrf.ok) throw new GrantError(csrf.status);
        const token: unknown = (await csrf.json()).token;
        if (typeof token !== 'string' || !token) throw new GrantError(503);
        headers['Content-Type'] = 'application/json'; headers['X-CSRF-Token'] = token; await account();
      }
      const r = await transport(root + path, { ...options, method, headers, body: serialized, signal: bounded() });
      if (!r.ok) throw new GrantError(r.status);
      const value: unknown = await r.json(); await account(); return value;
    } catch (e) {
      if (signal?.aborted || e instanceof GrantError) throw e;
      if (e && typeof e === 'object' && 'status' in e && typeof e.status === 'number') throw new GrantError(e.status);
      throw new GrantError(503);
    }
  };
}
export async function discoverGrants(userId: string, signal?: AbortSignal): Promise<ScopeChoice[]> {
  const request = connection(userId, signal, fetch); const rows: ScopeChoice[] = [];
  for (let page = 1; ; page++) {
    const values = await request(`/scopes?page=${page}&pageSize=100`) as ScopeChoice[];
    if (!Array.isArray(values)) throw new GrantError(503);
    for (const v of values) {
      scopeQuery(v.scope);
      if (typeof v.workspaceName !== 'string' || !(v.projectName === null || typeof v.projectName === 'string') || !Array.isArray(v.capabilities) || !v.capabilities.length || v.capabilities.some(c => !management.includes(c))) throw new GrantError(503);
    }
    rows.push(...values); if (values.length < 100) return rows;
  }
}
export function createGrantClient(scope: FinanceScope, userId: string, signal?: AbortSignal, transport: typeof fetch = fetch) {
  const bound = Object.freeze({ ...scope }), query = scopeQuery(bound), request = connection(userId, signal, transport);
  const path = (id: string) => { if (!validId(id)) throw new GrantError(400); return '/' + id; };
  function parse(value: unknown): GrantView {
    const v = value as GrantView;
    try { validateGrant(v?.spec); } catch { throw new GrantError(503); }
    if (!sameScope(v.spec.scope, bound)) throw new GrantError(403);
    if (!validId(v.id) || !states.includes(v.state) || !Number.isSafeInteger(v.version) || v.version < 1) throw new GrantError(503);
    return v;
  }
  function record(value: unknown): GrantRecord {
    const v = parse(value) as GrantRecord;
    if (!validId(v.proposedBy) || typeof v.recipientName !== 'string' || !(v.assignmentId === null && v.assignmentVersion === null || validId(v.assignmentId) && Number.isSafeInteger(v.assignmentVersion) && v.assignmentVersion! > 0)
      || ![...states, 'Active', 'Scheduled', 'Expired', 'Unavailable'].includes(v.effectiveState) || [v.canReview, v.canApprove, v.canRevoke].some(x => typeof x !== 'boolean')) throw new GrantError(503);
    return v;
  }
  const client = {
    async list(queue = false): Promise<GrantRecord[]> {
      const rows: GrantRecord[] = [];
      for (let page = 1; ; page++) { const values = await request(`${queue ? '/review-queue' : ''}?${query}&page=${page}&pageSize=100`); if (!Array.isArray(values)) throw new GrantError(503); rows.push(...values.map(record)); if (values.length < 100) return rows; }
    },
    async details(id: string): Promise<GrantRecord> { const v = record(await request(`${path(id)}?${query}`)); if (v.id !== id) throw new GrantError(403); return v; },
    async recipients(prefix: string, page = 1): Promise<Recipient[]> {
      if (prefix.length > 100 || /[\x00-\x1f\x7f]/.test(prefix) || !Number.isSafeInteger(page) || page < 1) throw new GrantError(400);
      const rows = await request(`/recipients?${query}&prefix=${encodeURIComponent(prefix)}&page=${page}&pageSize=20`) as Recipient[];
      if (!Array.isArray(rows) || rows.some(x => !validId(x.id) || typeof x.name !== 'string')) throw new GrantError(503); return rows;
    },
    async history(id: string): Promise<GrantHistory[]> {
      const rows: GrantHistory[] = [];
      for (let page = 1; ; page++) {
        const values = await request(`${path(id)}/history?${query}&page=${page}&pageSize=100`) as GrantHistory[];
        if (!Array.isArray(values) || values.some(v => v.proposalId !== id || !validId(v.actorId) || !time(v.at) || typeof v.reason !== 'string' || !['Proposed', 'Approved', 'Returned', 'Rejected', 'Revoked'].includes(v.outcome))) throw new GrantError(503);
        rows.push(...values); if (values.length < 100) return rows;
      }
    },
    async proposeGrant(spec: GrantSpec, meta: CommandMeta): Promise<GrantView> {
      command(meta); validateGrant(spec); if (meta.expectedVersion !== 1) throw new GrantError(400);
      if (!sameScope(spec.scope, bound)) throw new GrantError(403);
      return parse(await request('', 'POST', { spec, meta }));
    },
    async reviewGrant(id: string, decision: 'approve' | 'return' | 'reject', meta: CommandMeta): Promise<GrantView> {
      command(meta); if (!['approve', 'return', 'reject'].includes(decision)) throw new GrantError(400);
      const seen = await client.details(id); if (seen.version !== meta.expectedVersion || seen.state !== 'Pending') throw new GrantError(409);
      const v = parse(await request(`${path(id)}/review`, 'POST', { decision, meta })); if (v.id !== id) throw new GrantError(403); return v;
    },
    async revokeGrant(id: string, meta: CommandMeta, proposalId?: string): Promise<GrantView> {
      command(meta);
      const seen = proposalId ? await client.details(proposalId) : (await client.list()).find(x => x.assignmentId === id);
      if (!seen || seen.assignmentId !== id) throw new GrantError(403);
      if (seen.assignmentVersion !== meta.expectedVersion) throw new GrantError(409);
      const v = parse(await request(`${path(id)}/revoke`, 'POST', meta)); if (v.id !== id) throw new GrantError(403); return v;
    }
  }; return client;
}
export function grantDifferences(before: GrantSpec, after: GrantSpec): string[] {
  const fields: [string, string, string][] = [
    ['ผู้รับ', before.recipientId, after.recipientId], ['บริษัท', before.scope.workspaceId, after.scope.workspaceId], ['โครงการ', before.scope.projectId ?? 'ระดับบริษัท', after.scope.projectId ?? 'ระดับบริษัท'],
    ['ความสามารถ', before.capabilities.join(', '), after.capabilities.join(', ')], ['มอบต่อได้', before.delegableCapabilities.join(', '), after.delegableCapabilities.join(', ')],
    ['วงเงิน', before.approvalLimit ?? 'ไม่มีอำนาจอนุมัติยอดเงิน', after.approvalLimit ?? 'ไม่มีอำนาจอนุมัติยอดเงิน'], ['เริ่ม', before.startsAt, after.startsAt], ['สิ้นสุด', before.endsAt ?? 'ไม่กำหนด', after.endsAt ?? 'ไม่กำหนด']
  ]; return fields.filter(([, a, b]) => a !== b).map(([label, a, b]) => `${label}: ${a || 'ไม่มี'} → ${b || 'ไม่มี'}`);
}
export function grantStatus(state: string): string { return ({ Pending: 'รอตรวจ', Draft: 'ส่งกลับ — เสนอใหม่', Rejected: 'ปฏิเสธ', Approved: 'อนุมัติ', Active: 'เริ่มใช้แล้ว', Scheduled: 'รอเวลาเริ่มใช้', Expired: 'หมดอายุ', Revoked: 'ถอนแล้ว', Unavailable: 'ยังยืนยันสิทธิ์ไม่ได้', Proposed: 'เสนอ', Returned: 'ส่งกลับ' } as Record<string, string>)[state] ?? 'ไม่ทราบสถานะ'; }

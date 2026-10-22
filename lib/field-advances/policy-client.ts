import { isSessionView } from '../auth/session-view';
import { validId, sameScope, parsePolicy, validateContent, money } from './policy-view';
import type { FinanceScope, PolicyContent, CommandMeta, PolicyView, ScopeChoice, PolicySnapshot, CompanyContext, PolicyHistory } from './policy-types';
const root = '/api/v1/field-advances/policies';
const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
export class PolicyError extends Error {
  constructor(readonly status: number) {
    super(status === 409 ? 'ข้อมูลหรือฉบับเปลี่ยนแล้ว กรุณาโหลดใหม่ก่อนส่งตรวจ' : status === 401 ? 'session หมดอายุ กรุณาเข้าสู่ระบบใหม่' : status === 403 ? 'ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่' : status === 400 ? 'ข้อมูลไม่ถูกต้อง กรุณาตรวจวัน วงเงิน หมวด และเหตุผล' : 'บริการไม่พร้อม หรือยังยืนยันผลไม่ได้ กรุณาโหลดใหม่เพื่อตรวจผลก่อนทำรายการอีกครั้ง');
  }
}
function query(scope: FinanceScope): string {
  if (!validId(scope.workspaceId) || !(scope.projectId === null || validId(scope.projectId))) throw new PolicyError(400);
  return `workspaceId=${scope.workspaceId}${scope.projectId ? `&projectId=${scope.projectId}` : ''}`;
}
export async function currentAccount(transport: typeof fetch = fetch, signal?: AbortSignal): Promise<string> {
  try {
    const bounded = signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
    const r = await transport('/api/v1/auth/session', { ...options, signal: bounded });
    if (!r.ok) throw new PolicyError(r.status);
    const value: unknown = await r.json();
    if (!isSessionView(value) || value.stage !== 'Active') throw new PolicyError(403);
    return value.userId;
  } catch (error) { if (signal?.aborted || error instanceof PolicyError) throw error; throw new PolicyError(503); }
}
export async function discoverPolicies(userId: string, signal?: AbortSignal): Promise<ScopeChoice[]> {
  const result: ScopeChoice[] = [];
  for (let page = 1; ; page++) {
    if (await currentAccount(fetch, signal) !== userId) throw new PolicyError(403);
    const r = await fetch(`${root}/scopes?page=${page}&pageSize=100`, { ...options, signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000) });
    if (!r.ok) throw new PolicyError(r.status);
    const values: ScopeChoice[] = await r.json();
    if (!Array.isArray(values)) throw new PolicyError(503);
    for (const v of values) {
      query(v.scope);
      if (typeof v.workspaceName !== 'string' || !(v.projectName === null || typeof v.projectName === 'string') || !Array.isArray(v.capabilities) || !v.capabilities.length || v.capabilities.some(c => ![v.scope.projectId ? 'policy.edit.project' : 'policy.edit.company', 'policy.review'].includes(c))) throw new PolicyError(503);
    }
    if (await currentAccount(fetch, signal) !== userId) throw new PolicyError(403);
    result.push(...values);
    if (values.length < 100) return result;
  }
}
export function createPolicyClient(scope: FinanceScope, userId: string, signal?: AbortSignal, transport: typeof fetch = fetch) {
  const bound = Object.freeze({ ...scope }); const search = query(bound);
  const bounded = () => signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
  const account = async () => { signal?.throwIfAborted(); if (await currentAccount(transport, bounded()) !== userId) throw new PolicyError(403); };
  const idPath = (id: string) => { if (!validId(id)) throw new PolicyError(400); return '/' + id; };
  async function fetchRequest(path: string, method = 'GET', body?: unknown): Promise<unknown> {
    const serialized = body === undefined ? undefined : JSON.stringify(body);
    if (serialized && serialized.length > 65536) throw new PolicyError(400);
    await account();
    const headers: Record<string, string> = {};
    if (method !== 'GET') {
      const csrf = await transport('/api/v1/auth/csrf', { ...options, signal: bounded() });
      if (!csrf.ok) throw new PolicyError(csrf.status);
      const token: unknown = (await csrf.json()).token;
      if (typeof token !== 'string' || !token) throw new PolicyError(503);
      headers['Content-Type'] = 'application/json'; headers['X-CSRF-Token'] = token;
      await account();
    }
    const r = await transport(root + path, { ...options, method, headers, body: serialized, signal: bounded() });
    if (!r.ok) throw new PolicyError(r.status);
    const value: unknown = await r.json();
    await account(); return value;
  }
  async function request(path: string, method = 'GET', body?: unknown): Promise<unknown> {
    try { return await fetchRequest(path, method, body); }
    catch (error) { if (signal?.aborted || error instanceof PolicyError) throw error; throw new PolicyError(503); }
  }
  const parse = (v: unknown) => { try { return parsePolicy(v, bound); } catch { throw new PolicyError(503); } };
  const command = (meta: CommandMeta) => {
    if (!validId(meta.commandId) || !Number.isSafeInteger(meta.expectedVersion) || meta.expectedVersion < 1 || !meta.reason?.trim() || meta.reason.length > 1000 || /[\x00-\x1f\x7f]/.test(meta.reason) || !meta.policy) throw new PolicyError(400);
  };
  const client = {
    async loadPolicies(target: FinanceScope = bound, queue = false): Promise<PolicyView[]> {
      if (!sameScope(target, bound)) throw new PolicyError(403);
      const rows: PolicyView[] = [];
      for (let page = 1; ; page++) {
        const data = await request(`${queue ? '/review-queue' : ''}?${search}&page=${page}&pageSize=100`);
        if (!Array.isArray(data)) throw new PolicyError(503);
        rows.push(...data.map(parse)); if (data.length < 100) return rows;
      }
    },
    async details(id: string) { const v = parse(await request(`${idPath(id)}?${search}`)); if (v.id !== id) throw new PolicyError(403); return v; },
    async savePolicy(id: string | null, target: FinanceScope, content: PolicyContent, meta: CommandMeta): Promise<PolicyView> {
      if (!sameScope(target, bound)) throw new PolicyError(403);
      try { validateContent(content); } catch { throw new PolicyError(400); }
      command(meta);
      if (id) {
        const seen = await client.details(id);
        if (seen.version !== meta.expectedVersion || seen.contentHash !== meta.policy.expectedContentHash) throw new PolicyError(409);
      }
      return parse(await request(id ? idPath(id) : '', id ? 'PUT' : 'POST', id ? { content, meta } : { scope: bound, baseCompanyVersionId: meta.policy.baseCompanyVersionId, content, meta }));
    },
    async submitPolicy(id: string, meta: CommandMeta) {
      command(meta); const seen = await client.details(id);
      if (seen.version !== meta.expectedVersion || seen.contentHash !== meta.policy.expectedContentHash) throw new PolicyError(409);
      return parse(await request(`${idPath(id)}/submit`, 'POST', meta));
    },
    async reviewPolicy(id: string, decision: 'approve' | 'return' | 'reject', meta: CommandMeta): Promise<PolicyView> {
      command(meta);
      const seen = await client.details(id);
      if (seen.version !== meta.expectedVersion || seen.contentHash !== meta.policy.expectedContentHash) throw new PolicyError(409);
      return parse(await request(`${idPath(id)}/review`, 'POST', { decision, meta }));
    },
    async effective(): Promise<PolicySnapshot> {
      const v = await request(`/effective?${search}`) as PolicySnapshot;
      if (!v || !v.scope || !sameScope(v.scope, bound) || !validId(v.companyVersionId) || typeof v.requiresReview !== 'boolean') throw new PolicyError(503);
      validateContent(v.content); return v;
    },
    async companyContext(id?: string): Promise<CompanyContext> {
      const v = await request(`/company-context?${search}${id ? `&policyId=${idPath(id).slice(1)}` : ''}`) as CompanyContext;
      for (const r of [v?.basis, v?.latest]) { if (!r || !validId(r.id) || !/^[a-f0-9]{64}$/.test(r.contentHash)) throw new PolicyError(503); validateContent(r.content); }
      return v;
    },
    async history(id: string): Promise<PolicyHistory[]> {
      const rows: PolicyHistory[] = [];
      for (let page = 1; ; page++) {
        const data = await request(`${idPath(id)}/history?${search}&page=${page}&pageSize=100`) as PolicyHistory[];
        if (!Array.isArray(data) || data.some(x => x.policyRevisionId !== id || typeof x.reason !== 'string' || !Number.isFinite(Date.parse(x.reviewedAt)))) throw new PolicyError(503);
        for (const row of data) if (row.content) validateContent(row.content);
        rows.push(...data); if (data.length < 100) return rows;
      }
    },
    async preview(total: string): Promise<string[]> {
      if (!money(total)) throw new PolicyError(400);
      const values = await request('/preview', 'POST', { scope: bound, total });
      if (!Array.isArray(values) || values.some(x => typeof x !== 'string')) throw new PolicyError(503);
      return values;
    }
  };
  return client;
}

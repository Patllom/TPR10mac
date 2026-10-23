import { currentAccount } from './policy-client';
import type { PolicySnapshot } from './policy-types';
import type { RequestScreen } from './request-types';
import { validId, sameScope, validateContent as validatePolicyContent } from './policy-view';
import type { FinanceScope, RequestMode, RequestCommand, RequestContent, CreateRequest, RequestView, Estimate, ConfirmationQuote, RequestDecision, RequestHistory, RequestScopeChoice, RequestPersonChoice, RequestCommandResult, QuotedRequest } from './request-types';

const root = '/api/v1/field-advances/requests';
const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
const states = ['Draft', 'AwaitingOwner', 'PendingApproval', 'Held', 'Returned', 'Approved', 'Rejected', 'Withdrawn'];
export class RequestError extends Error {
  readonly clearProtectedData: boolean;
  readonly refreshRequired: boolean;
  constructor(readonly status: number, readonly commandId?: string, readonly unknownResult = false, refreshRequired = false) {
    super(status === 401 ? 'session หมดอายุ กรุณาเข้าสู่ระบบใหม่' : status === 403 ? 'ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่' : status === 409 ? 'ข้อมูลเปลี่ยน กรุณาโหลดใหม่' : status === 400 ? 'ข้อมูลคำขอไม่ถูกต้อง' : 'ยังยืนยันผลไม่ได้ กรุณาตรวจสถานะด้วยรหัสคำสั่งเดิม');
    this.clearProtectedData = status === 401 || status === 403;
    this.refreshRequired = status === 409 || refreshRequired;
  }
}
const fail = () => { throw new RequestError(503); };
function scopeQuery(scope: FinanceScope) {
  if (!scope || !validId(scope.workspaceId) || !validId(scope.projectId)) throw new RequestError(400);
  return `workspaceId=${scope.workspaceId}&projectId=${scope.projectId}`;
}
function pageQuery(page: number, pageSize: number) {
  if (!Number.isSafeInteger(page) || page < 1 || !Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100 || (page - 1) * pageSize > 2147483647) throw new RequestError(400);
  return `page=${page}&pageSize=${pageSize}`;
}
function path(id: string) { if (!validId(id)) throw new RequestError(400); return '/' + id; }
const nullableId = (v: unknown) => v === null || validId(v);
const decimal = (v: unknown, precision: number) => typeof v === 'string' && new RegExp(`^\\d+(?:\\.\\d{1,${precision}})?$`).test(v);
function content(v: RequestContent) {
  if (!v || !['Individual', 'Team'].includes(v.kind) || typeof v.purpose !== 'string' || !v.purpose.trim()
    || [v.fieldStart, v.fieldEnd, v.neededOn].some(x => typeof x !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(x))
    || !Array.isArray(v.teamPersonIds) || v.teamPersonIds.some(x => !validId(x)) || !Array.isArray(v.lines) || !v.lines.length
    || v.lines.some(x => !x || !validId(x.id) || !validId(x.categoryId) || typeof x.description !== 'string' || !['QuantityRate', 'LumpSum'].includes(x.mode)
      || !(x.quantity === null || decimal(x.quantity, 4)) || !(x.unitRate === null || decimal(x.unitRate, 2)) || !(x.lumpSum === null || decimal(x.lumpSum, 2)))) throw new RequestError(400);
}
function estimate(v: Estimate) {
  if (!v || !decimal(v.total, 2) || !Array.isArray(v.lines) || !v.lines.length || v.lines.some(x => !validId(x.id) || !validId(x.categoryId)
    || !decimal(x.amount, 2) || !(x.quantity === null || decimal(x.quantity, 4)) || !(x.unitRate === null || decimal(x.unitRate, 2)))) fail();
}
function parse(value: unknown): RequestView {
  const v = value as RequestView;
  try { scopeQuery(v.scope); content(v.content); } catch { return fail(); }
  if (!validId(v.id) || !validId(v.revisionId) || !validId(v.ownerPersonId) || !nullableId(v.delegatePersonId)
    || !Number.isSafeInteger(v.version) || v.version < 1 || !states.includes(v.state) || !(v.holdCode === null || typeof v.holdCode === 'string')) fail();
  if (v.estimate !== null) estimate(v.estimate);
  if (v.currentStep !== null && (!v.currentStep || !validId(v.currentStep.id) || !nullableId(v.currentStep.assigneePersonId)
    || !Number.isInteger(v.currentStep.order) || v.currentStep.order < 1 || typeof v.currentStep.levelCode !== 'string'
    || !(v.currentStep.holdCode === null || typeof v.currentStep.holdCode === 'string') || !['PendingApproval', 'Held'].includes(v.state))) fail();
  return v;
}
function command(v: RequestCommand) {
  if (!v || !validId(v.commandId) || !Number.isSafeInteger(v.expectedVersion) || v.expectedVersion < 0 || typeof v.reason !== 'string' || v.reason.length > 2000) throw new RequestError(400);
}

export function createRequestClient(userId: string, transport: typeof fetch = fetch) {
  if (!validId(userId)) throw new RequestError(400);
  async function request(suffix: string, signal?: AbortSignal, method = 'GET', body?: unknown, meta?: RequestCommand): Promise<unknown> {
    let sent = false, endpointRejected = false;
    const bounded = () => signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
    const account = async () => { signal?.throwIfAborted(); if (await currentAccount(transport, bounded()) !== userId) throw new RequestError(403); signal?.throwIfAborted(); };
    try {
      const serialized = body === undefined ? undefined : JSON.stringify(body);
      if (serialized && serialized.length > 65536) throw new RequestError(400);
      await account(); const headers: Record<string, string> = {};
      if (method !== 'GET') {
        const csrf = await transport('/api/v1/auth/csrf', { ...options, signal: bounded() });
        if (!csrf.ok) throw new RequestError(csrf.status);
        const token: unknown = (await csrf.json()).token;
        if (typeof token !== 'string' || !token) throw new RequestError(503);
        headers['Content-Type'] = 'application/json'; headers['X-CSRF-Token'] = token; await account();
      }
      signal?.throwIfAborted(); sent = method !== 'GET';
      const response = await transport(root + suffix, { ...options, signal: bounded(), method, headers, body: serialized });
      if (!response.ok) {
        // Only a rejection from the mutation endpoint settles this command. A later
        // session/account failure cannot tell us whether the mutation committed.
        endpointRejected = response.status >= 400 && response.status < 500;
        throw new RequestError(response.status);
      }
      const value: unknown = await response.json(); await account(); return value;
    } catch (e) {
      const unknownResult = sent && !endpointRejected;
      if (e instanceof RequestError) throw new RequestError(e.status, meta?.commandId, unknownResult, unknownResult || e.refreshRequired);
      if (e && typeof e === 'object' && 'status' in e && typeof e.status === 'number') throw new RequestError(e.status, meta?.commandId, unknownResult, unknownResult);
      if (signal?.aborted && !sent) throw e;
      throw new RequestError(503, meta?.commandId, unknownResult, unknownResult);
    }
  }
  async function details(id: string, signal?: AbortSignal): Promise<RequestView> {
    const v = parse(await request(path(id), signal)); if (v.id !== id) throw new RequestError(403); return v;
  }
  async function mutate(suffix: string, body: unknown, meta: RequestCommand, signal?: AbortSignal, method = 'POST', expectedId?: string): Promise<RequestCommandResult> {
    command(meta);
    const value = await request(suffix, signal, method, body, meta);
    let receipt: RequestView;
    try { receipt = parse(value); } catch { throw new RequestError(503, meta.commandId, true); }
    if (expectedId && receipt.id !== expectedId) throw new RequestError(403, meta.commandId);
    // A stale receipt is never the current UI model. A lost right to read clears the whole result.
    try {
      const current = await details(receipt.id, signal);
      return { receipt, current };
    } catch (e) {
      throw new RequestError(e instanceof RequestError ? e.status : 503, meta.commandId, false, true);
    }
  }
  const simple = (operation: string, id: string, meta: RequestCommand, signal?: AbortSignal) => mutate(`${path(id)}/${operation}`, meta, meta, signal, 'POST', operation === 'copy' ? undefined : id);
  return {
    details,
    async screen(row: RequestView, signal?: AbortSignal): Promise<RequestScreen> {
      const v = await request(`${path(row.id)}/screen`, signal) as RequestScreen;
      if (!v || v.requestId !== row.id || v.revisionId !== row.revisionId || v.version !== row.version) throw new RequestError(409);
      if (!Number.isSafeInteger(v.revisionNumber) || v.revisionNumber < 1 || !Array.isArray(v.actions) || v.actions.some(x => !['edit', 'send-to-owner', 'confirm', 'return-to-author', 'review', 'revise', 'withdraw', 'copy'].includes(x))) return fail();
      if (v.previousContent !== null) { try { content(v.previousContent); } catch { return fail(); } }
      if (v.frozenPolicy !== null) {
        try { validatePolicyContent(v.frozenPolicy.content); } catch { return fail(); }
        if (!sameScope(v.frozenPolicy.scope, row.scope)) throw new RequestError(403);
      }
      return v;
    },
    async configuration(scope: FinanceScope, signal?: AbortSignal): Promise<PolicySnapshot> {
      const p = await request(`/configuration?${scopeQuery(scope)}`, signal) as PolicySnapshot;
      try { validatePolicyContent(p.content); } catch { return fail(); }
      if (!sameScope(p.scope, scope)) throw new RequestError(403);
      return p;
    },
    async teamPeople(scope: FinanceScope, prefix = '', page = 1, pageSize = 20, signal?: AbortSignal): Promise<RequestPersonChoice[]> {
      if (typeof prefix !== 'string' || prefix.length > 100 || /[\x00-\x1f\x7f]/.test(prefix)) throw new RequestError(400);
      const rows = await request(`/team-people?${scopeQuery(scope)}&prefix=${encodeURIComponent(prefix)}&${pageQuery(page, pageSize)}`, signal) as RequestPersonChoice[];
      if (!Array.isArray(rows) || rows.length > pageSize || rows.some(x => !validId(x.id) || typeof x.name !== 'string')) return fail();
      return rows;
    },
    async list(scope: FinanceScope, mode: RequestMode = 'mine', page = 1, pageSize = 20, signal?: AbortSignal): Promise<RequestView[]> {
      if (!['mine', 'delegated', 'review', 'report'].includes(mode)) throw new RequestError(400);
      const values = await request(`${mode === 'review' ? '/review-queue' : ''}?${scopeQuery(scope)}&mode=${mode}&${pageQuery(page, pageSize)}`, signal);
      if (!Array.isArray(values) || values.length > pageSize) return fail();
      return values.map(value => { const row = parse(value); if (!sameScope(row.scope, scope)) throw new RequestError(403); return row; });
    },
    async quote(id: string, signal?: AbortSignal): Promise<QuotedRequest> {
      const current = await details(id, signal);
      const q = await request(`${path(id)}/quote`, signal) as ConfirmationQuote;
      if (!q || q.requestId !== current.id || q.revisionId !== current.revisionId || q.version !== current.version) throw new RequestError(409);
      if (typeof q.confirmationHash !== 'string' || !/^[a-f0-9]{64}$/.test(q.confirmationHash) || !Array.isArray(q.levels) || !q.levels.length || q.levels.some(x => typeof x !== 'string')) fail();
      estimate(q.estimate);
      try { validatePolicyContent(q.policy.content); } catch { return fail(); }
      if (!sameScope(q.policy.scope, current.scope)) throw new RequestError(403);
      return { request: current, quote: q };
    },
    async create(input: CreateRequest, meta: RequestCommand, signal?: AbortSignal) {
      scopeQuery(input.scope); content(input.content); if (!validId(input.ownerPersonId) || meta.expectedVersion !== 0) throw new RequestError(400);
      return mutate('', { input, meta }, meta, signal);
    },
    async update(id: string, value: RequestContent, meta: RequestCommand, signal?: AbortSignal) { content(value); return mutate(path(id), { content: value, meta }, meta, signal, 'PUT', id); },
    sendToOwner: (id: string, meta: RequestCommand, signal?: AbortSignal) => simple('send-to-owner', id, meta, signal),
    confirm: (id: string, confirmationHash: string, meta: RequestCommand, signal?: AbortSignal) => mutate(`${path(id)}/confirm`, { confirmationHash, meta }, meta, signal, 'POST', id),
    returnToAuthor: (id: string, meta: RequestCommand, signal?: AbortSignal) => simple('return-to-author', id, meta, signal),
    review: (id: string, decision: RequestDecision, meta: RequestCommand, signal?: AbortSignal) => mutate(`${path(id)}/review`, { decision, meta }, meta, signal, 'POST', id),
    revise: (id: string, meta: RequestCommand, signal?: AbortSignal) => simple('revise', id, meta, signal),
    withdraw: (id: string, meta: RequestCommand, signal?: AbortSignal) => simple('withdraw', id, meta, signal),
    copy: (id: string, meta: RequestCommand, signal?: AbortSignal) => simple('copy', id, meta, signal),
    async history(id: string, page = 1, pageSize = 20, signal?: AbortSignal): Promise<RequestHistory[]> {
      const rows = await request(`${path(id)}/history?${pageQuery(page, pageSize)}`, signal) as RequestHistory[];
      if (!Array.isArray(rows) || rows.length > pageSize || rows.some(x => !validId(x.eventId) || !validId(x.revisionId) || !nullableId(x.actor) || !nullableId(x.stepId)
        || typeof x.action !== 'string' || typeof x.reason !== 'string' || typeof x.time !== 'string' || !Number.isFinite(Date.parse(x.time)) || !Number.isSafeInteger(x.version)
        || !x.metadata || typeof x.metadata !== 'object' || x.action === 'field-advance.request.reevaluate' && x.actor !== null)) return fail();
      return rows;
    },
    async scopes(page = 1, pageSize = 20, signal?: AbortSignal): Promise<RequestScopeChoice[]> {
      const rows = await request(`/scopes?${pageQuery(page, pageSize)}`, signal) as RequestScopeChoice[];
      if (!Array.isArray(rows) || rows.length > pageSize) return fail();
      for (const row of rows) { scopeQuery(row.scope); if (typeof row.workspaceName !== 'string' || typeof row.projectName !== 'string' || !Array.isArray(row.capabilities) || !row.capabilities.length || row.capabilities.some(x => !['request.create', 'request.delegate', 'request.approve', 'report.read'].includes(x))) fail(); }
      return rows;
    },
    async people(scope: FinanceScope, prefix = '', page = 1, pageSize = 20, signal?: AbortSignal): Promise<RequestPersonChoice[]> {
      if (typeof prefix !== 'string' || prefix.length > 100 || /[\x00-\x1f\x7f]/.test(prefix)) throw new RequestError(400);
      const rows = await request(`/people?${scopeQuery(scope)}&prefix=${encodeURIComponent(prefix)}&${pageQuery(page, pageSize)}`, signal) as RequestPersonChoice[];
      if (!Array.isArray(rows) || rows.length > pageSize || rows.some(x => !validId(x.id) || typeof x.name !== 'string')) return fail();
      return rows;
    }
  };
}

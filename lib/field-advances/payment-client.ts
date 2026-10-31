import { currentAccount } from './policy-client';
import { validId } from './policy-view';
import { parsePaymentSummary, paymentMoney, paymentVersion } from './payment-view';
import { createRequestClient } from './request-client';
import type { FinanceScope, RequestContent } from './request-types';
import type { PaymentAction, PaymentSummary, PaymentCommand, EvidenceTicket, PaymentProposal, InstallmentDetail, PaymentCatalog, PaymentUiContext, PaymentScopeChoice, PaymentWorkflowContext } from './payment-types';
const root = '/api/v1/field-advances';
const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
const actions: PaymentAction[] = ['installments', 'update', 'submit', 'review', 'start', 'record', 'outcome', 'mismatch', 'cancel', 'metadata', 'takeover', 'confirm', 'ceiling-amendment', 'ceiling-activate', 'disposition', 'finalize', 'supplements'];
export class PaymentError extends Error {
  readonly clearProtectedData: boolean;
  readonly refreshRequired: boolean;
  constructor(readonly status: number, readonly commandId?: string, readonly unknownResult = false) {
    super(status === 401 ? 'กรุณาเข้าสู่ระบบใหม่' : status === 403 ? 'ไม่มีสิทธิ์หรือบัญชีเปลี่ยน กรุณาโหลดใหม่' : status === 400 ? 'ข้อมูลการจ่ายเงินไม่ถูกต้อง' : status === 409 ? 'ข้อมูลเปลี่ยน กรุณาโหลดใหม่' : 'ยังยืนยันผลไม่ได้ กรุณาตรวจสถานะด้วยรหัสคำสั่งเดิม');
    this.clearProtectedData = status === 401 || status === 403; this.refreshRequired = status === 409 || unknownResult;
  }
}
const invalid = (): never => { throw new PaymentError(400); };
const fail = (): never => { throw new PaymentError(503); };
function object(v: unknown): Record<string, unknown> { if (!v || typeof v !== 'object' || Array.isArray(v)) return invalid(); return v as Record<string, unknown>; }
function fields(v: unknown, names: string[]): Record<string, unknown> { const r = object(v); if (Object.keys(r).some(k => !names.includes(k)) || names.some(k => !(k in r))) return invalid(); return r; }
const nullableText = (v: unknown) => v === null || typeof v === 'string';
const ids = (v: unknown) => Array.isArray(v) && v.every(validId);
const date = (v: unknown) => typeof v === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(v) && Number.isFinite(Date.parse(v));
function meta(v: unknown): PaymentCommand {
  const r = fields(v, ['commandId', 'familyVersion', 'requestVersion', 'installmentVersion', 'reason']);
  if (!validId(r.commandId) || !paymentVersion(r.familyVersion) || !paymentVersion(r.requestVersion)
    || !(r.installmentVersion === null || paymentVersion(r.installmentVersion)) || typeof r.reason !== 'string' || !r.reason.trim() || r.reason.length > 2000) return invalid();
  return r as PaymentCommand;
}
function destination(v: unknown) {
  const r = fields(v, ['channel', 'recipientPersonId', 'bankCode', 'accountNumber', 'accountHolder']);
  if (!['Cash', 'Transfer'].includes(r.channel as string) || !validId(r.recipientPersonId) || ![r.bankCode, r.accountNumber, r.accountHolder].every(nullableText)) invalid();
}
function metadata(v: unknown, actual = false) {
  const r = fields(v, actual ? ['paidOn', 'reference', 'evidenceIds', 'amount', 'recipientPersonId'] : ['paidOn', 'reference', 'evidenceIds']);
  if (!date(r.paidOn) || !nullableText(r.reference) || !ids(r.evidenceIds) || actual && (!paymentMoney(r.amount) || !validId(r.recipientPersonId))) invalid();
}
function outcome(v: unknown) {
  const r = fields(v, ['outcome', 'actual', 'evidenceIds']);
  if (typeof r.outcome !== 'string' || !ids(r.evidenceIds)) invalid();
  if (r.actual !== null) metadata(r.actual, true);
}
function content(v: unknown) {
  const r = fields(v, ['kind', 'purpose', 'fieldStart', 'fieldEnd', 'neededOn', 'teamPersonIds', 'lines']);
  if (!['Individual', 'Team'].includes(r.kind as string) || typeof r.purpose !== 'string' || !r.purpose.trim() || ![r.fieldStart, r.fieldEnd, r.neededOn].every(date) || !ids(r.teamPersonIds) || !Array.isArray(r.lines) || !r.lines.length) invalid();
  for (const line of r.lines as unknown[]) {
    const x = fields(line, ['id', 'categoryId', 'description', 'mode', 'quantity', 'unitRate', 'lumpSum']);
    if (!validId(x.id) || !validId(x.categoryId) || typeof x.description !== 'string' || !['QuantityRate', 'LumpSum'].includes(x.mode as string)
      || !(x.quantity === null || typeof x.quantity === 'string' && /^\d+(?:\.\d{1,4})?$/.test(x.quantity)) || ![x.unitRate, x.lumpSum].every(n => n === null || paymentMoney(n))) invalid();
  }
}
function input(action: PaymentAction, payload: unknown): { body: unknown; command: PaymentCommand; target?: string } {
  const r = object(payload); const targetKey = ['confirm', 'finalize'].includes(action) ? 'proposalId' : ['update', 'submit', 'review', 'start', 'record', 'outcome', 'mismatch', 'cancel', 'metadata', 'takeover'].includes(action) ? 'installmentId' : null;
  const target = targetKey ? r[targetKey] : undefined;
  if (targetKey && !validId(target)) invalid();
  const body = { ...r }; if (targetKey) delete body[targetKey];
  if (['submit', 'start', 'cancel', 'ceiling-activate', 'confirm', 'finalize'].includes(action)) return { body, command: meta(body), target: target as string | undefined };
  const key = action === 'review' || action === 'disposition' ? 'outcome' : action === 'takeover' ? 'evidenceIds' : action === 'ceiling-amendment' ? 'content' : 'input';
  fields(body, [key, 'meta']); const command = meta(body.meta);
  if (action === 'installments' || action === 'update') { const x = fields(body.input, ['amount', 'destination']); if (!paymentMoney(x.amount)) invalid(); destination(x.destination); }
  else if (action === 'record' || action === 'mismatch') metadata(body.input, true);
  else if (action === 'metadata') metadata(body.input);
  else if (action === 'outcome') outcome(body.input);
  else if (action === 'takeover') { if (!ids(body.evidenceIds)) invalid(); }
  else if (action === 'ceiling-amendment') content(body.content);
  else if (typeof body.outcome !== 'string' || !body.outcome.trim()) invalid();
  return { body, command, target: target as string | undefined };
}
function ticket(v: unknown): EvidenceTicket {
  const r = object(v);
  if (!validId(r.id) || typeof r.sha256 !== 'string' || !/^[a-f0-9]{64}$/.test(r.sha256) || !paymentVersion(r.length) || !['image/png', 'image/jpeg', 'application/pdf'].includes(r.contentType as string) || typeof r.state !== 'string') return fail();
  return { id: r.id, sha256: r.sha256, length: r.length, contentType: r.contentType as string, state: r.state };
}
function proposal(v: unknown, requestId: string): PaymentProposal {
  const r = object(v); if (r.requestId !== requestId || !validId(r.id) || !(r.installmentId === null || validId(r.installmentId))) throw new PaymentError(403);
  if (!paymentVersion(r.familyVersion) || !paymentVersion(r.requestVersion) || !(r.installmentVersion === null || paymentVersion(r.installmentVersion)) || !['InstallmentReview', 'PaymentCancellation', 'PaymentMetadata', 'PaymentOutcome', 'PaymentTakeover', 'PaymentMismatch', 'PaymentDisposition'].includes(r.kind as string) || typeof r.state !== 'string' || typeof r.reason !== 'string' || !Array.isArray(r.evidence)) return fail();
  for (const side of [r.before, r.after]) { const x = fields(side, ['outcome', 'state', 'reserved', 'metadata', 'payment', 'revisionId', 'approved', 'basis']); if (!nullableText(x.outcome) || !nullableText(x.state) || !(x.revisionId === null || validId(x.revisionId)) || ![x.reserved, x.approved, x.basis].every(n => n === null || paymentMoney(n))) return fail(); if (x.metadata !== null) metadata(x.metadata); if (x.payment !== null) outcome(x.payment); }
  r.evidence.forEach(ticket); return r as PaymentProposal;
}
function workflowContext(value: unknown, requestId: string): PaymentWorkflowContext {
  const raw = object(value);
  const common = ['variant', 'requestId', 'familyVersion', 'requestVersion', 'proposals'];
  const r = fields(value, raw.variant === 'request' ? [...common, 'allowedActions', 'installments', 'workflow'] : common);
  if (r.requestId !== requestId) throw new PaymentError(403);
  if (!['request', 'disposition'].includes(r.variant as string) || !paymentVersion(r.familyVersion) || !paymentVersion(r.requestVersion) || !Array.isArray(r.proposals)) return fail();
  const allowed = (v: unknown, names: string[]) => Array.isArray(v) && new Set(v).size === v.length && v.every(x => typeof x === 'string' && names.includes(x));
  for (const value of r.proposals) { const x = fields(value, ['proposalId', 'allowedActions']); if (!validId(x.proposalId) || !allowed(x.allowedActions, ['confirm', 'finalize'])) return fail(); }
  if (new Set(r.proposals.map(x => x.proposalId)).size !== r.proposals.length) return fail();
  if (r.variant === 'request') {
    if (!allowed(r.allowedActions, ['installments', 'ceiling-amendment', 'ceiling-activate', 'supplements', 'resume-original', 'terminate-remaining']) || !Array.isArray(r.installments)) return fail();
    for (const value of r.installments) { const x = fields(value, ['installmentId', 'installmentVersion', 'allowedActions']); if (!validId(x.installmentId) || !paymentVersion(x.installmentVersion) || !allowed(x.allowedActions, ['update', 'submit', 'review', 'start', 'record', 'outcome', 'mismatch', 'cancel', 'metadata', 'takeover'])) return fail(); }
    if (new Set(r.installments.map(x => x.installmentId)).size !== r.installments.length) return fail();
    const w = fields(r.workflow, ['everPaid', 'outcomeUnclear', 'relationKind', 'pendingSupplementState', 'amendmentState', 'amendmentApproved', 'installmentsCleared', 'ownerPersonId', 'scope']);
    const scope = fields(w.scope, ['workspaceId', 'projectId']);
    const state = (v: unknown) => v === null || ['Draft', 'AwaitingOwner', 'PendingApproval', 'Held', 'Returned', 'Approved', 'Rejected', 'Withdrawn'].includes(v as string);
    if (![w.everPaid, w.outcomeUnclear, w.amendmentApproved, w.installmentsCleared].every(x => typeof x === 'boolean') || !['Normal', 'Supplement'].includes(w.relationKind as string)
      || !state(w.pendingSupplementState) || !state(w.amendmentState) || !validId(w.ownerPersonId) || !validId(scope.workspaceId) || !validId(scope.projectId)) return fail();
  }
  return r as PaymentWorkflowContext;
}
export function createPaymentClient(userId: string, transport: typeof fetch = fetch) {
  if (!validId(userId)) invalid();
  const bounded = (signal?: AbortSignal) => signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
  async function account(signal?: AbortSignal) { signal?.throwIfAborted(); if (await currentAccount(transport, bounded(signal)) !== userId) throw new PaymentError(403); signal?.throwIfAborted(); }
  async function request(path: string, signal?: AbortSignal, body?: unknown, commandId?: string, binary = false): Promise<unknown> {
    let sent = false, rejected = false;
    try {
      await account(signal); const headers: Record<string, string> = {};
      const serialized = body instanceof Blob ? body : body === undefined ? undefined : JSON.stringify(body);
      if (typeof serialized === 'string' && serialized.length > 65536) invalid();
      if (body !== undefined) {
        const response = await transport('/api/v1/auth/csrf', { ...options, signal: bounded(signal) });
        if (!response.ok) throw new PaymentError(response.status);
        const csrf = await response.json(); if (typeof csrf.token !== 'string' || !csrf.token) return fail();
        headers['X-CSRF-Token'] = csrf.token; headers['Content-Type'] = body instanceof Blob ? body.type : 'application/json'; await account(signal);
      }
      signal?.throwIfAborted(); sent = body !== undefined;
      const response = await transport(root + path, { ...options, signal: bounded(signal), method: sent ? 'POST' : 'GET', headers, body: serialized });
      if (!response.ok) { rejected = response.status >= 400 && response.status < 500; throw new PaymentError(response.status); }
      const value: unknown = binary ? await response.blob() : await response.json(); await account(signal); return value;
    } catch (error) {
      if (signal?.aborted && !sent) throw error;
      const status = error && typeof error === 'object' && 'status' in error && typeof error.status === 'number' ? error.status : 503;
      throw new PaymentError(status, commandId, sent && !rejected);
    }
  }
  function parse(v: unknown): PaymentSummary { try { return parsePaymentSummary(v); } catch { return fail(); } }
  async function getPaymentSummary(requestId: string, signal?: AbortSignal): Promise<PaymentSummary> {
    if (!validId(requestId)) invalid(); const v = parse(await request(`/requests/${requestId}/payments`, signal));
    if (v.requestId !== requestId) throw new PaymentError(403); return v;
  }
  return {
    getPaymentSummary,
    async getPaymentContext(requestId: string, signal?: AbortSignal): Promise<PaymentUiContext> {
      if (!validId(requestId)) invalid();
      const value = await request(`/requests/${requestId}/payment-context`, signal);
      const raw = object(value);
      if ('variant' in raw) {
        const context = workflowContext(value, requestId);
        if (context.variant !== 'request') throw new PaymentError(403);
        // Existing payment screens keep their strict, installment-only interface.
        return { requestId, familyVersion: context.familyVersion, requestVersion: context.requestVersion,
          allowedActions: context.allowedActions.filter((x): x is 'installments' => x === 'installments'), installments: context.installments,
          proposals: context.proposals.map(p => ({ ...p, allowedActions: p.allowedActions.filter(x => x === 'confirm') })) };
      }
      const r = fields(value, ['requestId', 'familyVersion', 'requestVersion', 'allowedActions', 'installments', 'proposals']);
      if (r.requestId !== requestId) throw new PaymentError(403);
      const allowed = (v: unknown, names: string[]) => Array.isArray(v) && new Set(v).size === v.length && v.every(x => typeof x === 'string' && names.includes(x));
      if (!paymentVersion(r.familyVersion) || !paymentVersion(r.requestVersion) || !allowed(r.allowedActions, ['installments']) || !Array.isArray(r.installments) || !Array.isArray(r.proposals)) return fail();
      for (const value of r.installments) { const x = fields(value, ['installmentId', 'installmentVersion', 'allowedActions']); if (!validId(x.installmentId) || !paymentVersion(x.installmentVersion) || !allowed(x.allowedActions, ['update', 'submit', 'review', 'start', 'record', 'outcome', 'mismatch', 'cancel', 'metadata', 'takeover'])) return fail(); }
      for (const value of r.proposals) { const x = fields(value, ['proposalId', 'allowedActions']); if (!validId(x.proposalId) || !allowed(x.allowedActions, ['confirm'])) return fail(); }
      if (new Set(r.installments.map(x => x.installmentId)).size !== r.installments.length || new Set(r.proposals.map(x => x.proposalId)).size !== r.proposals.length) return fail();
      return r as PaymentUiContext;
    },
    async getWorkflowContext(requestId: string, signal?: AbortSignal): Promise<PaymentWorkflowContext> {
      if (!validId(requestId)) invalid();
      return workflowContext(await request(`/requests/${requestId}/payment-context`, signal), requestId);
    },
    async paymentScopes(page = 1, pageSize = 25, signal?: AbortSignal): Promise<PaymentScopeChoice[]> {
      if (!Number.isSafeInteger(page) || page < 1 || !Number.isInteger(pageSize) || pageSize < 1 || pageSize > 100 || (page - 1) * pageSize > 2147483647) invalid();
      const rows = await request(`/payment-scopes?page=${page}&pageSize=${pageSize}`, signal);
      if (!Array.isArray(rows) || rows.length > pageSize) return fail();
      for (const value of rows) { const r = fields(value, ['scope', 'workspaceName', 'projectName', 'capabilities']); const scope = fields(r.scope, ['workspaceId', 'projectId']);
        if (!validId(scope.workspaceId) || !validId(scope.projectId) || typeof r.workspaceName !== 'string' || typeof r.projectName !== 'string' || !Array.isArray(r.capabilities) || !r.capabilities.length || new Set(r.capabilities).size !== r.capabilities.length || r.capabilities.some(x => !['finance.check', 'payment.record'].includes(x))) return fail(); }
      return rows as PaymentScopeChoice[];
    },
    async sendPaymentCommand(requestId: string, action: PaymentAction, payload: unknown, signal?: AbortSignal): Promise<PaymentSummary> {
      if (!validId(requestId) || !actions.includes(action)) return invalid();
      if (action === 'supplements') {
        const r = fields(payload, ['content', 'meta']); content(r.content); const m = fields(r.meta, ['commandId', 'expectedVersion', 'reason']);
        if (!validId(m.commandId) || m.expectedVersion !== 0 || typeof m.reason !== 'string' || !m.reason.trim()) return invalid();
        const result = await createRequestClient(userId, transport).supplement(requestId, r.content as RequestContent, { commandId: m.commandId, expectedVersion: 0, reason: m.reason }, signal);
        return getPaymentSummary(result.current.id, signal);
      }
      const parsed = input(action, payload);
      const path = ['confirm', 'finalize'].includes(action) ? `/payment-proposals/${parsed.target}/${action}` : parsed.target ? `/installments/${parsed.target}/${action}` : `/requests/${requestId}/${action}`;
      const wire = await request(path, signal, parsed.body, parsed.command.commandId);
      let result: PaymentSummary;
      try { result = parse(wire); } catch { throw new PaymentError(503, parsed.command.commandId, true); }
      if (result.requestId !== requestId || parsed.target && !['confirm', 'finalize'].includes(action) && result.installmentId !== parsed.target)
        throw new PaymentError(403, parsed.command.commandId);
      if (action === 'installments' && result.installmentId === null) throw new PaymentError(503, parsed.command.commandId, true);
      // A replay is historical. Reload current state; never retry the mutation or invent a new command ID.
      try {
        if (result.installmentId !== null) {
          const detail = object(await request(`/installments/${result.installmentId}`, signal)); const current = parse(detail.summary);
          if (current.requestId !== requestId || current.installmentId !== result.installmentId) throw new PaymentError(403);
          return current;
        }
        if (action === 'confirm' || action === 'finalize') {
          const current = parse(await request(`/payment-commands/${parsed.command.commandId}`, signal));
          if (current.requestId !== requestId || current.installmentId !== null) throw new PaymentError(403);
          return current;
        }
        return await getPaymentSummary(requestId, signal);
      } catch (error) { throw new PaymentError(error instanceof PaymentError ? error.status : 503, parsed.command.commandId); }
    },
    async lookupCommand(commandId: string, signal?: AbortSignal): Promise<PaymentSummary> { if (!validId(commandId)) invalid(); return parse(await request(`/payment-commands/${commandId}`, signal)); },
    async queue(scope: FinanceScope, skip = 0, take = 25, signal?: AbortSignal): Promise<PaymentSummary[]> {
      if (!scope || !validId(scope.workspaceId) || !validId(scope.projectId) || !Number.isSafeInteger(skip) || skip < 0 || skip > 2147483647 || !Number.isInteger(take) || take < 1 || take > 100) invalid();
      const rows = await request(`/payments?workspaceId=${scope.workspaceId}&projectId=${scope.projectId}&skip=${skip}&take=${take}`, signal);
      if (!Array.isArray(rows) || rows.length > take) return fail(); return rows.map(parse);
    },
    async installments(requestId: string, signal?: AbortSignal): Promise<PaymentCatalog> {
      if (!validId(requestId)) invalid(); const catalog = object(await request(`/requests/${requestId}/installments`, signal));
      if (!Array.isArray(catalog.installments) || !Array.isArray(catalog.proposals) || !Array.isArray(catalog.evidence)) return fail();
      return { installments: catalog.installments.map(x => { const v = parse(x); if (v.requestId !== requestId || !v.installmentId) throw new PaymentError(403); return v; }),
        proposals: catalog.proposals.map(x => proposal(x, requestId)), evidence: catalog.evidence.map(ticket) };
    },
    async installment(requestId: string, installmentId: string, signal?: AbortSignal): Promise<InstallmentDetail> {
      if (!validId(requestId) || !validId(installmentId)) invalid(); const r = object(await request(`/installments/${installmentId}`, signal)); const summary = parse(r.summary);
      if (summary.requestId !== requestId || summary.installmentId !== installmentId) throw new PaymentError(403);
      if (!Array.isArray(r.revisions) || !Array.isArray(r.proposals) || !Array.isArray(r.evidence)) return fail();
      for (const revision of r.revisions) { const x = object(revision); if (!validId(x.id) || !paymentVersion(x.revisionNumber) || typeof x.isCurrent !== 'boolean' || !paymentMoney(x.amount) || !nullableText(x.reviewedAtUtc) || typeof x.reason !== 'string') return fail(); destination(x.destination); }
      r.proposals.forEach(x => proposal(x, requestId)); r.evidence.forEach(ticket); return { ...r, summary } as InstallmentDetail;
    },
    async proposal(requestId: string, proposalId: string, signal?: AbortSignal): Promise<PaymentProposal> { if (!validId(requestId) || !validId(proposalId)) invalid(); const v = proposal(await request(`/payment-proposals/${proposalId}`, signal), requestId); if (v.id !== proposalId) throw new PaymentError(403); return v; },
    async uploadEvidence(requestId: string, file: Blob, signal?: AbortSignal): Promise<EvidenceTicket> {
      if (!validId(requestId) || !(file instanceof Blob) || !['image/png', 'image/jpeg', 'application/pdf'].includes(file.type) || file.size <= 0 || file.size > 10 * 1024 * 1024) invalid();
      return ticket(await request(`/requests/${requestId}/payment-evidence`, signal, file));
    },
    async downloadEvidence(evidenceId: string, signal?: AbortSignal): Promise<Blob> { if (!validId(evidenceId)) invalid(); return await request(`/payment-evidence/${evidenceId}`, signal, undefined, undefined, true) as Blob; }
  };
}

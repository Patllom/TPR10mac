import { currentAccount } from './policy-client';
import { validId } from './policy-view';
import type { SettlementCommand, SettlementCommandResult, SettlementSummary, SettlementPage, SettlementPreview, SettlementFamily, SettlementRound } from './settlement-types';
const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
export class SettlementError extends Error {
 readonly clearProtectedData: boolean; readonly refreshRequired: boolean;
 constructor(readonly status: number, readonly commandId?: string, readonly unknownResult = false) {
  super(status === 401 ? 'กรุณาเข้าสู่ระบบใหม่' : status === 403 ? 'ไม่มีสิทธิ์หรือบัญชีเปลี่ยน กรุณาโหลดใหม่' : status === 409 ? 'ข้อมูลเปลี่ยน กรุณาโหลดใหม่' : 'ยังยืนยันผลไม่ได้ กรุณาตรวจสถานะด้วยรหัสคำสั่งเดิม');
  this.clearProtectedData = status === 401 || status === 403; this.refreshRequired = status === 409 || unknownResult;
 }
}
const invalid = (): never => { throw new SettlementError(400); };
function object(v: unknown): Record<string, unknown> { if (!v || typeof v !== 'object' || Array.isArray(v)) return invalid(); return v as Record<string, unknown>; }
const version = (v: unknown) => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0;
const money = (v: unknown) => typeof v === 'string' && /^-?\d+(?:\.\d{1,2})?$/.test(v);
type Schema = Record<string, string>;
const schemas: Record<string, Schema> = {
 SettlementEmpty: {}, SettlementDate: { actualEnd: 'date' }, SettlementDeclaration: { complete: 'boolean' },
 SettlementDuplicate: { sameDocument: 'boolean' }, SettlementTrack: { documentId: 'id' }, SettlementAmount: { amount: 'money' },
 SettlementRecovery: { amount: 'money', source: 'string', evidenceIds: 'ids' },
 RoundInput: { lines: 'lines' }, ReviewInput: { decision: 'string', reason: 'string' },
 DocumentInput: { issuer: 'string', number: 'nullableString', hasNoNumber: 'boolean', issuedOn: 'date', amount: 'money', evidenceIds: 'ids', evidenceType: '?nullableString' },
 RefundInput: { amount: 'money', returnedOn: 'date', channel: 'string', companyRecipient: 'string', evidenceIds: 'ids', adjustmentCaseId: 'nullableId' },
 RefundCorrectionInput: { refundId: 'id', correctAmount: 'money', evidenceIds: 'ids', supersedesId: '?nullableId' },
 CasePaymentInput: { caseId: 'id', amount: 'money', paidOn: 'date', evidenceIds: 'ids', supersedesId: '?nullableId' },
 ExcessPaymentInput: { excessId: 'id', recipientPersonId: 'id', amount: 'money', paidOn: 'date', evidenceIds: 'ids', supersedesId: '?nullableId' },
 DispositionInput: { disposition: 'string', evidenceIds: 'ids', paymentIds: 'ids' },
 IncidentInput: { attemptId: 'id', ownerReceived: 'money', wrongRecipientReceived: 'money', evidenceIds: 'ids' },
};
const detailSchemas: Record<string, [string, Schema]> = {
 rounds: ['round', {kind:'string',sourceRoundId:'nullableId',adjustmentCaseId:'nullableId',revisionId:'nullableId',revisionVersion:'nullableVersion',total:'nullableMoney'}],
 documents: ['document', {id:'id',version:'version',state:'string',issuer:'string',documentNumber:'nullableString',hasNoNumber:'boolean',issuedOn:'date',amount:'money',evidenceIds:'ids',evidenceType:'?nullableString'}],
 refunds: ['refund', {amount:'money',returnedOn:'date',channel:'string',companyRecipient:'string',evidenceIds:'ids',adjustmentCaseId:'nullableId'}],
 adjustments: ['adjustment', {kind:'string',rootRefundId:'nullableId',rootExpenseRoundId:'nullableId',baselineVersion:'nullableVersion',baselineAmount:'nullableMoney',externalPaidAmount:'money',evidenceIds:'ids'}],
 incidents: ['incident', {installmentId:'id',attemptId:'id',ownerReceived:'money',wrongRecipientReceived:'money',evidenceIds:'ids'}],
 corrections: ['correction', {refundId:'id',supersedesId:'nullableId',baseVersion:'version',beforeAmount:'money',afterAmount:'money',delta:'money',adjustmentCaseId:'nullableId',evidenceIds:'ids'}],
 'case-payments': ['payment', {parentId:'id',supersedesId:'nullableId',amount:'money',paidOn:'date',evidenceIds:'ids'}],
 'excess-payments': ['payment', {parentId:'id',supersedesId:'nullableId',amount:'money',paidOn:'date',evidenceIds:'ids'}],
 excess: ['excess', {documentId:'id',sourceRevisionId:'id',amount:'money',claimedAmount:'money',capAmount:'money',acceptedPaymentIds:'ids'}],
 recoveries: ['recovery', {resolutionId:'id',returnKind:'string',amount:'money',returnedOn:'date',evidenceIds:'ids'}],
 completion: ['completion', {completedOn:'date',clearingDueOn:'date',refundDueOn:'date',clearingDays:'version',supersedesId:'nullableId',previousCompletedOn:'nullableDate'}],
 duplicates: ['duplicate', {documentId:'id',documentVersion:'version'}]
};
function validate(v: unknown, schema: Schema): Record<string, unknown> {
 const r = object(v); if (Object.keys(r).some(k => !Object.hasOwn(schema, k))) invalid();
 for (const [key, kind] of Object.entries(schema)) {
  if (kind.startsWith('?') && !(key in r)) continue;
  const type = kind.replace(/^\?/, ''); const value = r[key];
  let valid = false;
  switch (type) {
   case 'id': valid = validId(value); break;
   case 'nullableId': valid = value === null || validId(value); break;
   case 'ids': valid = Array.isArray(value) && value.length <= 100 && value.every(validId) && new Set(value).size === value.length; break;
   case 'money': valid = money(value); break;
   case 'nullableMoney': valid = value === null || money(value); break;
   case 'quantity': valid = value === null || typeof value === 'string' && /^\d+(?:\.\d{1,4})?$/.test(value); break;
   case 'string': valid = typeof value === 'string' && value.length <= 2000; break;
   case 'nullableString': valid = value === null || typeof value === 'string' && value.length <= 2000; break;
   case 'boolean': valid = typeof value === 'boolean'; break;
   case 'date': valid = typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) && Number.isFinite(Date.parse(value)); break;
   case 'nullableDate': valid = value === null || typeof value === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(value) && Number.isFinite(Date.parse(value)); break;
   case 'timestamp': valid = typeof value === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(value) && Number.isFinite(Date.parse(value)); break;
   case 'nullableBoolean': valid = value === null || typeof value === 'boolean'; break;
   case 'version': valid = version(value); break;
   case 'nullableVersion': valid = value === null || version(value); break;
   case 'lines':
    valid = Array.isArray(value) && value.length > 0 && value.length <= 1000;
    if (valid) for (const line of value as unknown[]) validate(line, { sourceLineId: 'nullableId', categoryId: 'id', description: 'string', incurredOn: 'date', quantity: 'quantity', rate: 'nullableMoney', lumpSum: 'nullableMoney', allocations: 'allocations' });
    break;
   case 'allocations':
    valid = Array.isArray(value) && value.length <= 100;
    if (valid) for (const allocation of value as unknown[]) validate(allocation, { documentId: 'id', amount: 'money' });
    break;
  }
  if (!valid) invalid();
 }
 return r;
}
const operations: Record<string, { path: string; schema: string; entity: boolean }> = {
 'rounds/create': { path: 'rounds/create', schema: 'RoundInput', entity: false },
 'rounds/update': { path: 'rounds/{entityId}/update', schema: 'RoundInput', entity: true },
 'rounds/submit': { path: 'rounds/{entityId}/submit', schema: 'SettlementEmpty', entity: true },
 'rounds/review': { path: 'rounds/{entityId}/review', schema: 'ReviewInput', entity: true },
 'documents/create': { path: 'documents/create', schema: 'DocumentInput', entity: false },
 'documents/update': { path: 'documents/{entityId}/update', schema: 'DocumentInput', entity: true },
 'rounds/documents': { path: 'rounds/{entityId}/documents', schema: 'DocumentInput', entity: true },
 'duplicates/review': { path: 'duplicates/{entityId}/review', schema: 'SettlementDuplicate', entity: true },
 'completion/confirm': { path: 'completion/confirm', schema: 'SettlementDate', entity: false },
 'completion/correct': { path: 'completion/correct', schema: 'SettlementDate', entity: false },
 'completion/correction-confirm': { path: 'completion/{entityId}/confirm', schema: 'SettlementEmpty', entity: true },
 'declaration': { path: 'declaration', schema: 'SettlementDeclaration', entity: false },
 'refunds/create': { path: 'refunds/create', schema: 'RefundInput', entity: false },
 'refunds/update': { path: 'refunds/{entityId}/update', schema: 'RefundInput', entity: true },
 'refunds/confirm': { path: 'refunds/{entityId}/confirm', schema: 'SettlementEmpty', entity: true },
 'refunds/cancel': { path: 'refunds/{entityId}/cancel', schema: 'SettlementEmpty', entity: true },
 'refunds/review': { path: 'refunds/{entityId}/review', schema: 'ReviewInput', entity: true },
 'corrections/create': { path: 'corrections/create', schema: 'RefundCorrectionInput', entity: false },
 'case-payments/create': { path: 'case-payments/create', schema: 'CasePaymentInput', entity: false },
 'excess-payments/create': { path: 'excess-payments/create', schema: 'ExcessPaymentInput', entity: false },
 'adjustments/close': { path: 'adjustments/{entityId}/close', schema: 'SettlementEmpty', entity: true },
 'rounds/adjust': { path: 'rounds/{entityId}/adjust', schema: 'RoundInput', entity: true },
 'rounds/excess': { path: 'rounds/{entityId}/excess', schema: 'SettlementTrack', entity: true },
 'excess/disposition': { path: 'excess/{entityId}/disposition', schema: 'DispositionInput', entity: true },
 'incidents/create': { path: 'incidents/create', schema: 'IncidentInput', entity: false },
 'incidents/confirm': { path: 'incidents/{entityId}/confirm', schema: 'SettlementEmpty', entity: true },
 'incidents/recover': { path: 'incidents/{entityId}/recover', schema: 'SettlementRecovery', entity: true },
 'recoveries/confirm': { path: 'recoveries/{entityId}/confirm', schema: 'SettlementEmpty', entity: true },
 'incidents/release': { path: 'incidents/{entityId}/release', schema: 'SettlementEmpty', entity: true },
 'incidents/shortfall': { path: 'incidents/{entityId}/shortfall', schema: 'SettlementAmount', entity: true },
 'corrections/confirm': { path: 'corrections/{entityId}/confirm', schema: 'SettlementEmpty', entity: true },
 'corrections/return': { path: 'corrections/{entityId}/return', schema: 'SettlementEmpty', entity: true },
 'corrections/reject': { path: 'corrections/{entityId}/reject', schema: 'SettlementEmpty', entity: true },
 'corrections/cancel': { path: 'corrections/{entityId}/cancel', schema: 'SettlementEmpty', entity: true },
 'case-payments/confirm': { path: 'case-payments/{entityId}/confirm', schema: 'SettlementEmpty', entity: true },
 'case-payments/return': { path: 'case-payments/{entityId}/return', schema: 'SettlementEmpty', entity: true },
 'case-payments/reject': { path: 'case-payments/{entityId}/reject', schema: 'SettlementEmpty', entity: true },
 'case-payments/cancel': { path: 'case-payments/{entityId}/cancel', schema: 'SettlementEmpty', entity: true },
 'excess-payments/confirm': { path: 'excess-payments/{entityId}/confirm', schema: 'SettlementEmpty', entity: true },
 'excess-payments/return': { path: 'excess-payments/{entityId}/return', schema: 'SettlementEmpty', entity: true },
 'excess-payments/reject': { path: 'excess-payments/{entityId}/reject', schema: 'SettlementEmpty', entity: true },
 'excess-payments/cancel': { path: 'excess-payments/{entityId}/cancel', schema: 'SettlementEmpty', entity: true },
};
export function createSettlementClient(userId: string, transport: typeof fetch = fetch) {
 if (!validId(userId)) invalid();
 const bounded = (signal?: AbortSignal) => signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
 async function account(signal?: AbortSignal) { signal?.throwIfAborted(); if (await currentAccount(transport, bounded(signal)) !== userId) throw new SettlementError(403); signal?.throwIfAborted(); }
 async function request(requestId: string, suffix: string, signal?: AbortSignal, body?: unknown, commandId?: string, binary = false): Promise<unknown> {
  if (!validId(requestId)) invalid(); let sent = false;
  try {
   await account(signal); const headers: Record<string, string> = {};
   const serialized = body instanceof Blob ? body : body === undefined ? undefined : JSON.stringify(body);
   if (typeof serialized === 'string' && new TextEncoder().encode(serialized).length > 65536) invalid();
   if (body !== undefined) {
    const response = await transport('/api/v1/auth/csrf', { ...options, signal: bounded(signal) });
    if (!response.ok) throw new SettlementError(response.status);
    const csrf = await response.json(); if (typeof csrf.token !== 'string' || !csrf.token) throw new SettlementError(503);
    headers['X-CSRF-Token'] = csrf.token; headers['Content-Type'] = body instanceof Blob ? body.type : 'application/json'; await account(signal);
   }
   signal?.throwIfAborted(); sent = body !== undefined;
   const response = await transport(`/api/v1/field-advances/requests/${requestId}/settlement${suffix}`, { ...options, signal: bounded(signal), method: sent ? 'POST' : 'GET', headers, body: serialized });
   // A delivery guard can return 4xx after commit. Status alone never proves a dispatched mutation failed.
   if (!response.ok) throw new SettlementError(response.status);
   const result: unknown = binary ? await response.blob() : await response.json(); await account(signal); return result;
  } catch (error) {
   if (signal?.aborted && !sent) throw error;
   const status = error && typeof error === 'object' && 'status' in error && typeof error.status === 'number' ? error.status : 503;
   throw new SettlementError(status, commandId, sent);
  }
 }
 async function loadSettlement(requestId: string, signal?: AbortSignal): Promise<SettlementSummary> {
  const r = object(await request(requestId, '', signal)); if (r.requestId !== requestId) throw new SettlementError(403);
  if (!version(r.requestVersion) || !version(r.familyVersion) || typeof r.state !== 'string' || !Array.isArray(r.holds) || !r.holds.every(x => typeof x === 'string') || !Array.isArray(r.allowedActions) || !r.allowedActions.every(x => typeof x === 'string' && x !== 'close')) throw new SettlementError(503);
  const totals = object(r.totals);
  if (!['ownerReceived','approvedExpense','confirmedRefund','pendingRefund','ownerOutstanding','wrongRecipientOutstanding'].every(k => money(totals[k])) || typeof totals.hasOpenCase !== 'boolean') throw new SettlementError(503);
  if (r.declaration != null) validate(r.declaration, {id:'id',version:'version',isWithdrawal:'boolean',declaredAtUtc:'timestamp',workCompletionId:'id',completionOnTime:'nullableBoolean'});
  return r as SettlementSummary;
 }
 function totalsValid(value: unknown): boolean {
  const r = object(value); return ['ownerReceived','approvedExpense','confirmedRefund','pendingRefund','ownerOutstanding','wrongRecipientOutstanding'].every(k=>money(r[k])) && typeof r.hasOpenCase === 'boolean';
 }
 function employeeFields(r: Record<string, unknown>) {
  if (r.allowedActions !== undefined && (!Array.isArray(r.allowedActions) || !r.allowedActions.every(a => ['round-draft','round-confirm','refund-update','refund-confirm','refund-cancel'].includes(String(a))))) throw new SettlementError(503);
  if (r.submittedAtUtc != null) validate({at:r.submittedAtUtc},{at:'timestamp'});
  if (r.submissionOnTime !== undefined && r.submissionOnTime !== null && typeof r.submissionOnTime !== 'boolean') throw new SettlementError(503);
 }
 function roundContent(r: Record<string, unknown>) {
  if (!money(r.total) || !Array.isArray(r.lines) || r.lines.length > 1000 || !Array.isArray(r.documents) || !Array.isArray(r.evidence) || 'snapshotJson' in r) throw new SettlementError(503);
  for (const line of r.lines) {
   validate(line, {id:'id',sourceLineId:'nullableId',categoryId:'id',description:'string',incurredOn:'date',quantity:'quantity',rate:'nullableMoney',lumpSum:'nullableMoney',amount:'money',allocations:'allocations'});
  }
  for (const document of r.documents) validate(document, {id:'id',version:'version',state:'string',issuer:'string',documentNumber:'nullableString',hasNoNumber:'boolean',issuedOn:'date',amount:'money',evidenceIds:'ids',evidenceType:'?nullableString'});
  for (const proof of r.evidence) {
   const x=object(proof); if (!validId(x.id) || x.state !== 'Ready' || typeof x.sha256 !== 'string' || !/^[a-f0-9]{64}$/i.test(x.sha256) || !version(x.length) || !['image/png','image/jpeg','application/pdf'].includes(String(x.contentType))) throw new SettlementError(503);
  }
  const documents=new Set(r.documents.map(d=>object(d).id)), evidence=new Set(r.evidence.map(e=>object(e).id));
  for(const line of r.lines) for(const allocation of object(line).allocations as unknown[]) if(!documents.has(object(allocation).documentId)) throw new SettlementError(503);
  for(const document of r.documents) for(const proof of object(document).evidenceIds as unknown[]) if(!evidence.has(proof)) throw new SettlementError(503);
 }
 return {
  loadSettlement,
  async loadSettlementFamily(requestId: string, take = 25, cursor?: string, signal?: AbortSignal): Promise<SettlementFamily> {
   if (!Number.isInteger(take) || take < 1 || take > 100 || cursor !== undefined && (typeof cursor !== 'string' || cursor.length > 2048)) invalid();
   const suffix = `/family?take=${take}${cursor ? '&cursor=' + encodeURIComponent(cursor) : ''}`;
   const r = object(await request(requestId, suffix, signal));
   if (r.requestId !== requestId) throw new SettlementError(403);
   if (!Array.isArray(r.requests) || r.requests.length > take || !totalsValid(r.pageTotals) || r.totalsScope !== 'visible-page'
    || !version(r.familyVersion) || typeof r.pageComplete !== 'boolean' || typeof r.pageSettled !== 'boolean'
    || !r.pageComplete && r.pageSettled || !(r.nextCursor === null || typeof r.nextCursor === 'string' && r.nextCursor.length > 0 && r.nextCursor.length <= 2048)) {
    throw new SettlementError(503);
   }
   const ids = new Set<string>();
   for (const item of r.requests) {
    const x = object(item);
    if (!validId(x.requestId) || typeof x.state !== 'string' || !totalsValid(x.totals)
     || !Array.isArray(x.holds) || !x.holds.every(v => typeof v === 'string')
     || !Array.isArray(x.allowedActions) || !x.allowedActions.every(v => typeof v === 'string' && v !== 'close')) throw new SettlementError(503);
    const id = x.requestId as string;
    if (ids.has(id)) throw new SettlementError(503);
    ids.add(id);
   }
   // The root need not occur on this page. Empty pages may still have an opaque continuation.
   return r as SettlementFamily;
  },
  async checkSettlementReceipt(requestId:string,operation:string,entityId:string|null,commandId:string,signal?:AbortSignal):Promise<SettlementCommandResult> {
   if(!validId(commandId)||!['rounds/create','rounds/update','rounds/submit','rounds/review','documents/create','documents/update','refunds/create','refunds/update','refunds/confirm','refunds/cancel','refunds/review','incidents/confirm','incidents/recover','incidents/release','incidents/shortfall','recoveries/confirm','corrections/confirm','corrections/return','corrections/reject','case-payments/confirm','case-payments/return','case-payments/reject','excess-payments/confirm','excess-payments/return','excess-payments/reject','completion/correction-confirm','duplicates/review','excess/disposition','declaration'].includes(operation)||!(entityId===null||validId(entityId)))invalid();
   const query=new URLSearchParams({operation});if(entityId)query.set('entityId',entityId);
   const wire=object(await request(requestId,`/commands/${commandId}/receipt?${query}`,signal));
   if(wire.commandId!==commandId||wire.operation!==operation||wire.operationEntityId!==entityId)throw new SettlementError(403);
   const r=object(wire.result);
   if(r.requestId!==requestId)throw new SettlementError(403);
   if(!validId(r.entityId)||!version(r.requestVersion)||!version(r.familyVersion)||!(r.entityVersion===null||version(r.entityVersion))||typeof r.state!=='string')throw new SettlementError(503);
   return r as SettlementCommandResult;
  },
  async sendSettlementCommand(requestId: string, action: string, body: unknown, command: SettlementCommand, signal?: AbortSignal): Promise<SettlementCommandResult> {
   if (!validId(requestId) || !Object.hasOwn(operations, action)) invalid();
   validate(command, { commandId: 'id', requestVersion: 'version', familyVersion: 'version', entityVersion: 'nullableVersion', reason: 'string', confirmationHash: '?string' });
   if (!command.reason.trim()) invalid();
   const op = operations[action]; const raw = { ...object(body) }; let path = op.path;
   if (op.entity) { if (!validId(raw.entityId)) invalid(); path = path.replace('{entityId}', raw.entityId as string); delete raw.entityId; }
   validate(raw, schemas[op.schema]);
   const wire = await request(requestId, '/' + path, signal, { input: raw, meta: command }, command.commandId);
   let r: Record<string, unknown>;
   try { r = object(wire); } catch { throw new SettlementError(503, command.commandId, true); }
   if (r.requestId !== requestId) throw new SettlementError(403, command.commandId, true);
   if (!(r.entityId === null || validId(r.entityId)) || !version(r.requestVersion) || !version(r.familyVersion) || !(r.entityVersion === null || version(r.entityVersion)) || typeof r.state !== 'string') throw new SettlementError(503, command.commandId, true);
   // This is a historical command receipt. Call loadSettlement for current state; never retry automatically.
   return r as SettlementCommandResult;
  },
  async listSettlement(requestId: string, collection: string, take = 25, cursor?: string, signal?: AbortSignal): Promise<SettlementPage> {
   if (!Object.hasOwn(detailSchemas, collection) || !Number.isInteger(take) || take < 1 || take > 100 || cursor !== undefined && (typeof cursor !== 'string' || cursor.length > 512)) invalid();
   const r = object(await request(requestId, `/${collection}?take=${take}${cursor ? '&cursor=' + encodeURIComponent(cursor) : ''}`, signal));
   if (!Array.isArray(r.items) || r.items.length > take || !version(r.requestVersion) || !version(r.familyVersion) || !(r.nextCursor === null || typeof r.nextCursor === 'string')) throw new SettlementError(503);
   for (const item of r.items) { const x=object(item); if (x.requestId !== requestId) throw new SettlementError(403); if (!validId(x.id) || !version(x.version) || typeof x.state !== 'string' || typeof x.reason !== 'string') throw new SettlementError(503); employeeFields(x); const [key,schema]=detailSchemas[collection]; validate(x[key],schema); if(key==='document'&&object(x[key]).id!==x.id) throw new SettlementError(403); }
   return r as SettlementPage;
  },
  async loadSettlementRound(requestId: string, roundId: string, revisionId: string, signal?: AbortSignal): Promise<SettlementRound> {
   if (!validId(roundId) || !validId(revisionId)) invalid();
   const r=object(await request(requestId, `/rounds/${roundId}/revisions/${revisionId}`, signal));
   if(r.requestId!==requestId || r.roundId!==roundId || r.revisionId!==revisionId) throw new SettlementError(403);
   if(!version(r.requestVersion)||!version(r.familyVersion)||!version(r.roundVersion)||!version(r.revisionVersion)||typeof r.state!=='string'||!(r.currentStepId===null||validId(r.currentStepId))||!(r.currentStepVersion===null||version(r.currentStepVersion))||((r.currentStepId===null)!==(r.currentStepVersion===null))) throw new SettlementError(503);
   roundContent(r); employeeFields(r);
   if (r.returnReview != null) {
    const review=object(r.returnReview);
    validate({revisionId:review.revisionId,stepId:review.stepId,stepVersion:review.stepVersion,reason:review.reason,affectedLineIds:review.affectedLineIds}, {revisionId:'id',stepId:'id',stepVersion:'version',reason:'string',affectedLineIds:'ids'});
    if (review.revisionId!==revisionId || !(review.affectedLineIds as string[]).every(id=>(r.lines as {id:string}[]).some(l=>l.id===id))) throw new SettlementError(403);
    if(review.returnedAtUtc!==null) validate({at:review.returnedAtUtc},{at:'timestamp'});
    if(review.correctionDueOn!==null) validate({date:review.correctionDueOn},{date:'date'});
   }
   return r as SettlementRound;
  },
  async downloadRoundEvidence(requestId: string, roundId: string, revisionId: string, evidenceId: string, signal?: AbortSignal): Promise<Blob> {
   if(!validId(roundId)||!validId(revisionId)||!validId(evidenceId)) invalid();
   return await request(requestId, `/rounds/${roundId}/revisions/${revisionId}/evidence/${evidenceId}`, signal, undefined, undefined, true) as Blob;
  },
  async previewSettlementRound(requestId: string, roundId: string, signal?: AbortSignal): Promise<SettlementPreview> {
   if (!validId(roundId)) invalid(); const r = object(await request(requestId, `/rounds/${roundId}/preview`, signal));
   if (r.requestId !== requestId || r.roundId !== roundId) throw new SettlementError(403);
   if (!validId(r.revisionId) || !version(r.revisionVersion) || !version(r.roundVersion) || !version(r.requestVersion) || !version(r.familyVersion) || typeof r.confirmationHash !== 'string' || !/^[a-f0-9]{64}$/.test(r.confirmationHash) || !money(r.approved)) throw new SettlementError(503);
   roundContent(r); return r as SettlementPreview;
  },
  async uploadAdjustmentEvidence(requestId: string, target: string, entityId: string, file: Blob, signal?: AbortSignal): Promise<unknown> {
   if (!['rounds','refunds','adjustments','case-payments','excess-payments','excess','incidents'].includes(target) || !validId(entityId) || !(file instanceof Blob) || !['image/png','image/jpeg','application/pdf'].includes(file.type) || file.size <= 0 || file.size > 10 * 1024 * 1024) invalid();
   return request(requestId, `/${target}/${entityId}/evidence`, signal, file);
  },
  async downloadEvidence(requestId: string, evidenceId: string, signal?: AbortSignal): Promise<Blob> {
   if (!validId(evidenceId)) invalid(); return await request(requestId, `/evidence/${evidenceId}`, signal, undefined, undefined, true) as Blob;
  },
  async uploadEvidence(requestId: string, file: Blob, signal?: AbortSignal): Promise<unknown> {
   if (!(file instanceof Blob) || !['image/png','image/jpeg','application/pdf'].includes(file.type) || file.size <= 0 || file.size > 10 * 1024 * 1024) invalid();
   return request(requestId, '/evidence', signal, file);
  }
 };
}

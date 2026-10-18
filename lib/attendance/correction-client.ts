import type { CorrectionAction, CorrectionAudience, CorrectionCommand, CreateCorrection, ReviseCorrection } from './correction-types';
import { correctionId, parseCorrection, parseCorrectionPage, validateCorrectionBody } from './correction-view';

const root = '/api/v1/attendance/corrections';
const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
function bounded(signal?: AbortSignal): AbortSignal {
  return signal ? AbortSignal.any([signal, AbortSignal.timeout(15000)]) : AbortSignal.timeout(15000);
}
function checkId(id: unknown): asserts id is string { if (!correctionId(id)) throw new Error('เลขคำร้องไม่ถูกต้อง'); }
export class CorrectionUncertainError extends Error {
  constructor(readonly commandId: string) { super('ยังยืนยันผลไม่ได้ กรุณาตรวจคำร้องก่อนส่งซ้ำโดยใช้รหัสเดิม'); this.name = 'CorrectionUncertainError'; }
}
export class CorrectionConflictError extends Error {
  readonly status = 409;
  constructor() { super('ข้อมูลเปลี่ยนแล้ว กรุณาโหลดใหม่ก่อนดำเนินการ'); }
}
export async function getCorrection(id: string, signal?: AbortSignal): Promise<Response> {
  checkId(id); return fetch(`${root}/${id}`, { ...options, signal: bounded(signal) });
}
export async function listCorrections(audience: CorrectionAudience, page = 1, pageSize = 20, signal?: AbortSignal): Promise<Response> {
  if (!['mine', 'supervisor', 'hr', 'routing'].includes(audience) || !Number.isSafeInteger(page) || page < 1 || !Number.isSafeInteger(pageSize) || pageSize < 1 || pageSize > 100 || page > Math.floor(2147483647 / pageSize)) throw new Error('ตัวกรองคำร้องไม่ถูกต้อง');
  return fetch(`${root}${audience === 'mine' ? '?' : `/approvals?stage=${audience}&`}page=${page}&pageSize=${pageSize}`, { ...options, signal: bounded(signal) });
}
export async function sendCorrection(action: 'create' | CorrectionAction, id: string | null, body: unknown, signal?: AbortSignal, beforeDispatch?: () => Promise<void>): Promise<Response> {
  const commandId = validateCorrectionBody(action, body);
  if (action === 'create') { if (id !== null) throw new Error('ข้อมูลคำร้องไม่ถูกต้อง'); } else checkId(id);
  // Freeze the validated payload before an await; caller edits cannot change a command in flight.
  const serialized = JSON.stringify(body);
  if (new TextEncoder().encode(serialized).length > 65536) throw new Error('ข้อมูลคำร้องใหญ่เกินกำหนด');
  const requestSignal = bounded(signal); requestSignal.throwIfAborted();
  const csrf = await fetch('/api/v1/auth/csrf', { ...options, signal: requestSignal });
  if (!csrf.ok) return csrf;
  if (!csrf.headers.get('content-type')?.includes('application/json')) throw new Error('ไม่สามารถยืนยันการส่งคำร้อง');
  const token: unknown = (await csrf.json()).token;
  if (typeof token !== 'string' || token.length === 0) throw new Error('ไม่สามารถยืนยันการส่งคำร้อง');
  await beforeDispatch?.();
  requestSignal.throwIfAborted();
  try {
    return await fetch(`${root}${action === 'create' ? '' : `/${id}/${action === 'save' ? 'revisions' : action}`}`, {
      ...options, method: 'POST', signal: requestSignal, headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token }, body: serialized
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    throw new CorrectionUncertainError(commandId);
  }
}
// Called only by an explicit user retry. A changed/inaccessible request needs review, not replay.
export async function retryCorrection(action: CorrectionAction, id: string, body: CorrectionCommand | ReviseCorrection, signal?: AbortSignal, beforeDispatch?: () => Promise<void>): Promise<Response> {
  validateCorrectionBody(action, body); checkId(id);
  const snapshot = JSON.parse(JSON.stringify(body)) as CorrectionCommand | ReviseCorrection;
  const current = await getCorrection(id, signal);
  if (!current.ok) return current;
  if (!current.headers.get('content-type')?.includes('application/json')) throw new Error('ไม่สามารถตรวจสถานะคำร้อง');
  const view = parseCorrection(await current.json()); signal?.throwIfAborted();
  const command = action === 'save' ? (snapshot as ReviseCorrection).command : snapshot as CorrectionCommand;
  if (view.id !== id || view.version !== command.expectedVersion || view.currentRevision !== command.revision || !view.actions.includes(action)) throw new CorrectionConflictError();
  return sendCorrection(action, id, snapshot, signal, beforeDispatch);
}

// Create has no known request id/version after a lost response. Inspect mine first, then an
// explicit retry uses the original command/payload; only the server's actor-bound receipt is proof.
export async function retryCreateCorrection(body: CreateCorrection, signal?: AbortSignal, beforeDispatch?: () => Promise<void>): Promise<Response> {
  validateCorrectionBody('create', body);
  const snapshot = JSON.parse(JSON.stringify(body)) as CreateCorrection;
  const current = await listCorrections('mine', 1, 20, signal);
  if (!current.ok) return current;
  if (!current.headers.get('content-type')?.includes('application/json')) throw new Error('ไม่สามารถตรวจรายการคำร้อง');
  parseCorrectionPage(await current.json()); signal?.throwIfAborted();
  return sendCorrection('create', null, snapshot, signal, beforeDispatch);
}

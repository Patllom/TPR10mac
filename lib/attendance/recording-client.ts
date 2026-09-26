import { isUuid } from './directory-view';
import type { CaptureAction, CaptureChallengeRequest, CaptureMetadata } from './recording-types';

const root = '/api/v1/attendance/';
const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
const actionValid = (action: unknown): action is CaptureAction => action === 'CheckIn' || action === 'CheckOut';
const bounded = (signal: AbortSignal, ms: number) => AbortSignal.any([signal, AbortSignal.timeout(ms)]);
export class CaptureUncertainError extends Error {
  constructor(public idempotencyKey: string) { super('ยังไม่ทราบผลลงเวลา กรุณาตรวจสถานะก่อนส่งใหม่'); this.name = 'CaptureUncertainError'; }
}
async function csrf(signal: AbortSignal): Promise<string | Response> {
  signal.throwIfAborted();
  const response = await fetch('/api/v1/auth/csrf', { ...options, signal: bounded(signal, 10000) });
  if (!response.ok) return response;
  const body: unknown = await response.json(); signal.throwIfAborted();
  if (!body || typeof body !== 'object' || !('token' in body) || typeof body.token !== 'string' || !body.token) throw new Error('บริการตรวจสอบคำขอไม่พร้อมใช้งาน');
  return body.token;
}
export async function issueCaptureChallenge(input: CaptureChallengeRequest, signal: AbortSignal): Promise<Response> {
  if (!actionValid(input?.action) || !isUuid(input?.idempotencyKey)) throw new Error('ข้อมูลการถ่ายภาพไม่ถูกต้อง');
  const token = await csrf(signal); if (token instanceof Response) return token;
  signal.throwIfAborted();
  return fetch(root + 'capture-challenges', { ...options, method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token }, body: JSON.stringify(input), signal: bounded(signal, 10000) });
}
export async function submitCapture(action: CaptureAction, metadata: CaptureMetadata, photo: Blob, signal: AbortSignal): Promise<Response> {
  if (!actionValid(action) || !isUuid(metadata?.idempotencyKey) || !(photo instanceof Blob)) throw new Error('ข้อมูลลงเวลาไม่ถูกต้อง');
  const token = await csrf(signal); if (token instanceof Response) return token;
  signal.throwIfAborted();
  const body = new FormData();
  body.append('metadata', new Blob([JSON.stringify(metadata)], { type: 'application/json' }));
  body.append('photo', photo, 'capture.jpg');
  try {
    return await fetch(root + (action === 'CheckIn' ? 'check-ins' : 'check-outs'), { ...options, method: 'POST', headers: { 'X-CSRF-Token': token }, body, signal: bounded(signal, 75000) });
  } catch (error) {
    if (signal.aborted) throw error;
    // A transport failure after dispatch does not prove the server rolled back.
    throw new CaptureUncertainError(metadata.idempotencyKey);
  }
}
export async function getCaptureOperation(key: string, action: CaptureAction, signal: AbortSignal): Promise<Response> {
  if (!isUuid(key) || !actionValid(action)) throw new Error('ข้อมูลคำขอไม่ถูกต้อง');
  signal.throwIfAborted();
  return fetch(`${root}operations/${key}?action=${action}`, { ...options, signal: bounded(signal, 10000) });
}
export function recordingError(status: number): string {
  if (status === 401) return 'session หมดอายุ กรุณาเข้าสู่ระบบใหม่';
  if (status === 403) return 'ไม่มีสิทธิ์หรือจำเป็นต้องยืนยันตัวตนเพิ่มเติม';
  if (status === 409) return 'ข้อมูลเปลี่ยนแปลงหรือกำลังประมวลผล กรุณาตรวจสถานะก่อนส่งใหม่';
  if (status === 413) return 'ภาพหรือข้อมูลมีขนาดใหญ่เกินกำหนด กรุณาถ่ายใหม่';
  if (status === 415) return 'ภาพไม่ถูกต้องหรือไม่รองรับ กรุณาถ่ายใหม่';
  if (status === 429) return 'คำขอมากเกินไป กรุณารอตามเวลาที่ระบบแจ้ง';
  if (status >= 500) return 'บริการลงเวลาไม่พร้อมใช้งาน กรุณาตรวจสถานะก่อนลองใหม่';
  return 'ลงเวลาไม่สำเร็จ กรุณาตรวจข้อมูลและถ่ายใหม่';
}

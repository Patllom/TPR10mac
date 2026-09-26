import { isUuid } from './directory-view';
import type { CaptureChallengeView, CaptureOperationView, CaptureReceipt, CaptureSite, GpsSample } from './recording-types';
export type CaptureCurrent = { openDay: { id: string; version: number; workDate: string } | null; todayCompleted: boolean; serverNowUtc: string };
export type CaptureChoice = { site: CaptureSite; label: string };
const obj = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const instant = (v: unknown): v is string => typeof v === 'string' && /^\d{4}-\d\d-\d\dT.*(?:Z|[+-]\d\d:\d\d)$/.test(v) && Number.isFinite(Date.parse(v));
const day = (v: unknown): v is string => typeof v === 'string' && /^\d{4}-\d\d-\d\d$/.test(v) && Number.isFinite(Date.parse(v)) && new Date(v).toISOString().slice(0, 10) === v;
const positive = (v: unknown): v is number => typeof v === 'number' && Number.isSafeInteger(v) && v > 0;
const invalid = () => new Error('ข้อมูลจากบริการไม่ถูกต้อง กรุณาโหลดใหม่');
export function bangkokDate(utc: string): string { return new Date(Date.parse(utc) + 7 * 3600000).toISOString().slice(0, 10); }
export function thaiTime(utc: string): string { return new Intl.DateTimeFormat('th-TH', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Asia/Bangkok' }).format(new Date(utc)); }
export function parseCurrent(v: unknown): CaptureCurrent {
  if (!obj(v) || !instant(v.serverNowUtc) || typeof v.todayCompleted !== 'boolean') throw invalid();
  if (v.openDay === null) return { openDay: null, todayCompleted: v.todayCompleted, serverNowUtc: v.serverNowUtc };
  const d = v.openDay;
  if (!obj(d) || !isUuid(d.id) || !positive(d.version) || !day(d.workDate) || d.readBasis !== 1 || d.out !== null || d.effectiveOutUtc !== null || !isUuid(d.employeeId) || !instant(d.effectiveInUtc)) throw invalid();
  // The capture screen needs pairing metadata, not historical GPS/photo payloads.
  return { openDay: { id: d.id, version: d.version, workDate: d.workDate }, todayCompleted: v.todayCompleted, serverNowUtc: v.serverNowUtc };
}
export function parseChallenge(v: unknown): CaptureChallengeView {
  if (!obj(v) || !isUuid(v.id) || !instant(v.serverNowUtc) || !instant(v.expiresAtUtc)) throw invalid();
  const duration = Date.parse(v.expiresAtUtc) - Date.parse(v.serverNowUtc);
  if (duration <= 0) throw new Error('การถ่ายภาพหมดอายุ กรุณาถ่ายใหม่');
  if (duration > 120000) throw invalid();
  return { id: v.id, serverNowUtc: v.serverNowUtc, expiresAtUtc: v.expiresAtUtc };
}
export function parseReceipt(v: unknown): CaptureReceipt {
  if (!obj(v) || !isUuid(v.operationId) || !isUuid(v.dayId) || !isUuid(v.eventId) || !isUuid(v.evidenceId) || !positive(v.dayVersion) || !day(v.workDate) || !instant(v.occurredAtUtc)) throw invalid();
  return { operationId: v.operationId, dayId: v.dayId, eventId: v.eventId, evidenceId: v.evidenceId, dayVersion: v.dayVersion, workDate: v.workDate, occurredAtUtc: v.occurredAtUtc };
}
export function parseOperation(v: unknown): CaptureOperationView {
  if (!obj(v)) throw invalid();
  if (v.state === 'Committed') return { state: 'Committed', receipt: parseReceipt(v.receipt) };
  if (!['Processing', 'Expired', 'Failed'].includes(String(v.state)) || v.receipt !== null) throw invalid();
  return { state: v.state as 'Processing' | 'Expired' | 'Failed', receipt: null };
}
export function gpsSample(position: GeolocationPosition, now: number): GpsSample {
  const { latitude, longitude, accuracy } = position.coords;
  if (![latitude, longitude, accuracy, position.timestamp, now].every(Number.isFinite) || Math.abs(latitude) > 90 || Math.abs(longitude) > 180 || accuracy <= 0 || now - position.timestamp < 0 || now - position.timestamp > 60000)
    throw new Error('GPS เก่าหรือไม่ถูกต้อง กรุณาเปิดเวลาอัตโนมัติและหาพิกัดใหม่');
  return { latitude, longitude, accuracyMeters: accuracy, sampledAtUtc: new Date(position.timestamp).toISOString() };
}
export function parseSitePage(v: unknown, page: number): { items: CaptureChoice[]; more: boolean } {
  if (!obj(v) || !Array.isArray(v.items) || v.items.length > 100 || v.pageNumber !== page || v.pageSize !== 100 || typeof v.total !== 'number' || !Number.isSafeInteger(v.total) || v.total < 0) throw invalid();
  const items: CaptureChoice[] = [];
  for (const row of v.items) {
    if (!obj(row) || !obj(row.scope) || !isUuid(row.scope.workspaceId) || !(row.scope.projectId === null || isUuid(row.scope.projectId)) || !(row.scope.siteId === null || isUuid(row.scope.siteId)) || !Array.isArray(row.capabilities) || !row.capabilities.every(x => typeof x === 'string') || typeof row.workspaceName !== 'string' || !(row.projectName === null || typeof row.projectName === 'string') || !(row.siteName === null || typeof row.siteName === 'string')) throw invalid();
    if (isUuid(row.scope.projectId) && isUuid(row.scope.siteId) && row.capabilities.includes('attendance:record')) items.push({ site: { workspaceId: row.scope.workspaceId, projectId: row.scope.projectId, siteId: row.scope.siteId }, label: [row.workspaceName, row.projectName, row.siteName].join(' / ') });
  }
  return { items, more: page * 100 < v.total };
}

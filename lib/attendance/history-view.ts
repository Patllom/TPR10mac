import type { AttendanceDayView, AttendanceEventView, AttendancePage, AttendanceAdjustmentView, CaptureSite } from './recording-types';

export type HistoryAudience = 'Mine' | 'Team' | 'Hr';
const uuid = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) && value !== '00000000-0000-0000-0000-000000000000';
const instant = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d{1,7})?(?:Z|[+-]\d\d:\d\d)$/.test(value) && Number.isFinite(Date.parse(value));
const date = (value: unknown): value is string => typeof value === 'string' && /^\d{4}-\d\d-\d\d$/.test(value) && Number.isFinite(Date.parse(value + 'T12:00:00Z')) && new Date(value + 'T12:00:00Z').toISOString().slice(0, 10) === value;
const object = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value);
const invalid = (): never => { throw new Error('ข้อมูลประวัติลงเวลาไม่ถูกต้อง'); };
const parseSite = (value: unknown): CaptureSite => {
  if (!object(value) || !uuid(value.workspaceId) || !uuid(value.projectId) || !uuid(value.siteId)) return invalid();
  return { workspaceId: value.workspaceId, projectId: value.projectId, siteId: value.siteId };
};
function parseAdjustments(value: unknown): AttendanceAdjustmentView[] {
  if (value === undefined) return [];
  if (!Array.isArray(value)) return invalid();
  return value.map(a => {
    if (!object(a) || !uuid(a.requestId) || !Number.isSafeInteger(a.revision) || (a.revision as number) < 1 || !instant(a.appliedAtUtc) || !instant(a.afterInUtc)
      || ![a.beforeInUtc, a.beforeOutUtc, a.afterOutUtc].every(x => x === null || instant(x))) return invalid();
    return { requestId: a.requestId, revision: a.revision as number, appliedAtUtc: a.appliedAtUtc, beforeInUtc: a.beforeInUtc as string | null,
      beforeOutUtc: a.beforeOutUtc as string | null, afterInUtc: a.afterInUtc, afterOutUtc: a.afterOutUtc as string | null };
  });
}

function parseEvent(value: unknown, audience: HistoryAudience): AttendanceEventView {
  if (!object(value) || !uuid(value.id) || !instant(value.occurredAtUtc) || !object(value.site) || !uuid(value.site.workspaceId) || !uuid(value.site.projectId) || !uuid(value.site.siteId) || !object(value.gps)) return invalid();
  const gps = value.gps;
  if (![gps.latitude, gps.longitude, gps.accuracyMeters].every(x => typeof x === 'number' && Number.isFinite(x)) || (gps.latitude as number) < -90 || (gps.latitude as number) > 90 || (gps.longitude as number) < -180 || (gps.longitude as number) > 180 || (gps.accuracyMeters as number) <= 0 || !instant(gps.sampledAtUtc)) return invalid();
  let photo: AttendanceEventView['photo'];
  if (audience !== 'Team' && value.photo !== undefined && value.photo !== null) {
    if (!object(value.photo) || !uuid(value.photo.evidenceId)) return invalid();
    photo = { evidenceId: value.photo.evidenceId };
  }
  return { id: value.id, occurredAtUtc: value.occurredAtUtc, site: { workspaceId: value.site.workspaceId, projectId: value.site.projectId, siteId: value.site.siteId }, gps: { latitude: gps.latitude as number, longitude: gps.longitude as number, accuracyMeters: gps.accuracyMeters as number, sampledAtUtc: gps.sampledAtUtc as string }, ...(photo ? { photo } : {}) };
}

export function parseAttendancePage(value: unknown, audience: HistoryAudience): AttendancePage {
  if (!object(value) || !Array.isArray(value.items) || value.items.length > 100 || !(value.nextCursor === null || (typeof value.nextCursor === 'string' && value.nextCursor.length > 0 && value.nextCursor.length <= 4096))) return invalid();
  const items = value.items.map((raw): AttendanceDayView => {
    if (!object(raw) || !uuid(raw.id) || !uuid(raw.employeeId) || !date(raw.workDate) || !Number.isSafeInteger(raw.version) || (raw.version as number) < 1 || ![1, 2, 3].includes(raw.readBasis as number) || !instant(raw.effectiveInUtc) || !(raw.effectiveOutUtc === null || instant(raw.effectiveOutUtc))) return invalid();
    const adjustments = parseAdjustments(raw.adjustments);
    const entered = raw.in === null ? null : parseEvent(raw.in, audience);
    const exited = raw.out === null ? null : parseEvent(raw.out, audience);
    if (entered && exited && Date.parse(exited.occurredAtUtc) < Date.parse(entered.occurredAtUtc)) return invalid();
    if ((!entered || raw.effectiveOutUtc !== null && !exited) && adjustments.length === 0) return invalid();
    if (raw.effectiveOutUtc !== null && Date.parse(raw.effectiveOutUtc as string) < Date.parse(raw.effectiveInUtc)) return invalid();
    const effectiveInSite = adjustments.length ? parseSite(raw.effectiveInSite) : entered?.site ?? null;
    const effectiveOutSite = raw.effectiveOutUtc === null ? null : adjustments.length ? parseSite(raw.effectiveOutSite) : exited?.site ?? null;
    return { id: raw.id, employeeId: raw.employeeId, workDate: raw.workDate, version: raw.version as number, in: entered, out: exited, readBasis: raw.readBasis as 1 | 2 | 3, effectiveInUtc: raw.effectiveInUtc, effectiveOutUtc: raw.effectiveOutUtc as string | null, effectiveInSite, effectiveOutSite, adjustments };
  });
  return { items, nextCursor: value.nextCursor as string | null };
}

export function attendanceDayLabel(workDate: string): string {
  if (!date(workDate)) return invalid();
  return new Intl.DateTimeFormat('th-TH-u-ca-buddhist', { timeZone: 'UTC', day: 'numeric', month: 'long', year: 'numeric' }).format(new Date(workDate + 'T12:00:00Z'));
}

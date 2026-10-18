import type { CorrectionAcknowledgment, CorrectionPage, CorrectionView } from './correction-types';

type Row = Record<string, unknown>;
export const correctionActions = ['save', 'submit', 'supervisor-approve', 'hr-approve', 'return', 'reroute'] as const;
function invalid(): never { throw new Error('ข้อมูลคำร้องไม่ถูกต้อง กรุณาโหลดใหม่'); }
function row(value: unknown, required: string[], optional: string[] = []): Row {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return invalid();
  const r = value as Row;
  if (required.some(k => !Object.hasOwn(r, k)) || Object.keys(r).some(k => !required.includes(k) && !optional.includes(k))) return invalid();
  return r;
}
export function correctionId(value: unknown): value is string {
  return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
}
const positive = (x: unknown): x is number => Number.isSafeInteger(x) && (x as number) > 0;
const text = (x: unknown): x is string => typeof x === 'string' && x.trim().length > 0 && x.trim().length <= 2000;
const day = (x: unknown) => typeof x === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(x) && Number.isFinite(Date.parse(x)) && new Date(x).toISOString().slice(0, 10) === x;
const utc = (x: unknown) => typeof x === 'string' && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|\+00:00)$/.test(x) && Number.isFinite(Date.parse(x));
const nullable = (x: unknown, check: (v: unknown) => boolean) => x === null || check(x);
function site(value: unknown): boolean { const r = row(value, ['workspaceId', 'projectId', 'siteId']); return Object.values(r).every(correctionId); }
function proposal(value: unknown): boolean {
  const r = row(value, ['workDate', 'inUtc', 'outUtc', 'inSite', 'outSite', 'reason']);
  return day(r.workDate) && utc(r.inUtc) && nullable(r.outUtc, utc) && site(r.inSite) && nullable(r.outSite, site) && text(r.reason)
    && (r.outUtc === null ? r.outSite === null : r.outSite !== null && Date.parse(r.outUtc as string) > Date.parse(r.inUtc as string));
}
function save(value: unknown): boolean {
  const r = row(value, ['dayId', 'originalWorkDate', 'expectedDayVersion', 'proposal']);
  return nullable(r.dayId, correctionId) && day(r.originalWorkDate) && nullable(r.expectedDayVersion, positive) && proposal(r.proposal);
}
function command(value: unknown, action: string): boolean {
  const r = row(value, ['commandId', 'expectedVersion', 'revision', 'reason']);
  return correctionId(r.commandId) && positive(r.expectedVersion) && positive(r.revision)
    && (action === 'return' || action === 'reroute' ? text(r.reason) : nullable(r.reason, text));
}
export function validateCorrectionBody(action: string, value: unknown): string {
  if (action === 'create') {
    const r = row(value, ['commandId', 'input']); if (!correctionId(r.commandId) || !save(r.input)) return invalid(); return r.commandId;
  }
  if (action === 'save') {
    const r = row(value, ['input', 'command']); if (!save(r.input) || !command(r.command, action)) return invalid(); return (r.command as Row).commandId as string;
  }
  if (!correctionActions.includes(action as typeof correctionActions[number]) || !command(value, action)) return invalid();
  return (value as Row).commandId as string;
}
function history(value: unknown): boolean {
  const h = row(value, ['revisions', 'routes', 'approvals', 'reroutes']);
  if (!Object.values(h).every(Array.isArray)) return false;
  return (h.revisions as unknown[]).every(v => { const r = row(v, ['revision', 'baselineDayVersion', 'proposal', 'createdAtUtc']); return positive(r.revision) && nullable(r.baselineDayVersion, positive) && proposal(r.proposal) && utc(r.createdAtUtc); })
    && (h.routes as unknown[]).every(v => { const r = row(v, ['revision', 'supervisorId', 'hrCandidateIds', 'createdAtUtc']); return positive(r.revision) && correctionId(r.supervisorId) && Array.isArray(r.hrCandidateIds) && r.hrCandidateIds.every(correctionId) && utc(r.createdAtUtc); })
    && (h.approvals as unknown[]).every(v => { const r = row(v, ['revision', 'step', 'actorId', 'outcome', 'reason', 'occurredAtUtc']); return positive(r.revision) && ['Supervisor', 'Hr'].includes(r.step as string) && correctionId(r.actorId) && ['Approved', 'Returned'].includes(r.outcome as string) && nullable(r.reason, text) && utc(r.occurredAtUtc); })
    && (h.reroutes as unknown[]).every(v => { const r = row(v, ['revision', 'actorId', 'reason', 'occurredAtUtc']); return positive(r.revision) && correctionId(r.actorId) && text(r.reason) && utc(r.occurredAtUtc); });
}
export function parseCorrection(value: unknown): CorrectionView {
  const r = row(value, ['id', 'employeeId', 'originalWorkDate', 'version', 'state', 'deadlineUtc', 'currentRevision', 'proposal', 'baselineDayVersion', 'actions', 'readBasis'], ['history', 'comparison']);
  if (!correctionId(r.id) || !correctionId(r.employeeId) || !day(r.originalWorkDate) || !positive(r.version) || ![0, 1, 2, 3, 4, 5].includes(r.state as number)
    || !utc(r.deadlineUtc) || !positive(r.currentRevision) || !proposal(r.proposal) || !nullable(r.baselineDayVersion, positive)
    || !Array.isArray(r.actions) || !r.actions.every(x => correctionActions.includes(x)) || new Set(r.actions).size !== r.actions.length
    || !['Own', 'Supervisor', 'Hr', 'WorkflowSupervisor', 'WorkflowHr'].includes(r.readBasis as string)
    || Object.hasOwn(r, 'history') && !history(r.history)
    || Object.hasOwn(r, 'comparison') && !comparison(r.comparison)) return invalid();
  return r as CorrectionView;
}
export function parseCorrectionPage(value: unknown): CorrectionPage {
  const r = row(value, ['items', 'total', 'pageNumber', 'pageSize']);
  if (!Array.isArray(r.items) || !Number.isSafeInteger(r.total) || (r.total as number) < 0 || !positive(r.pageNumber) || !positive(r.pageSize) || r.pageSize > 100 || r.items.length > r.pageSize || (r.total as number) < r.items.length) return invalid();
  r.items.forEach(v => { const item = parseCorrection(v); if (Object.hasOwn(item, 'history') || Object.hasOwn(item, 'comparison')) invalid(); });
  return r as CorrectionPage;
}
function comparison(value: unknown): boolean {
  const c = row(value, ['dayId', 'baselineMatches', 'original', 'current']);
  if (!nullable(c.dayId, correctionId) || typeof c.baselineMatches !== 'boolean') return false;
  if (c.original !== null) { const o = row(c.original, ['inUtc', 'outUtc']); if (!nullable(o.inUtc, utc) || !nullable(o.outUtc, utc)) return false; }
  if (c.current !== null) {
    const t = row(c.current, ['version', 'workDate', 'inUtc', 'outUtc', 'inSite', 'outSite']);
    if (!positive(t.version) || !day(t.workDate) || !utc(t.inUtc) || !nullable(t.outUtc, utc) || !nullable(t.inSite, site) || !nullable(t.outSite, site)) return false;
  }
  return (c.dayId === null) === (c.current === null);
}
export function parseCorrectionAck(value: unknown): CorrectionAcknowledgment {
  const r = row(value, ['id', 'commandId']); if (!correctionId(r.id) || !correctionId(r.commandId)) return invalid(); return r as CorrectionAcknowledgment;
}
export function correctionError(status: number): string {
  switch (status) {
    case 401: return 'กรุณาเข้าสู่ระบบใหม่';
    case 403: return 'ไม่มีสิทธิ์หรือจำเป็นต้องยืนยัน MFA';
    case 404: return 'ไม่พบคำร้องที่เข้าถึงได้ กรุณากลับหน้าคิว';
    case 409: return 'ข้อมูลเปลี่ยนแล้ว กรุณาโหลดใหม่ก่อนดำเนินการ';
    case 400: return 'ข้อมูลไม่ถูกต้องหรือพ้นกำหนดยื่นคำร้อง';
    case 429: return 'ทำรายการบ่อยเกินไป กรุณารอแล้วตรวจสถานะ';
    case 503: return 'ระบบไม่พร้อม กรุณาตรวจสถานะก่อนส่งซ้ำ';
    default: return 'ยังยืนยันผลไม่ได้ กรุณาตรวจสถานะก่อนส่งซ้ำ';
  }
}

import type { FinanceScope, PolicyContent, PolicyView } from './policy-types';
export const validId = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
export const sameScope = (a: FinanceScope, b: FinanceScope) => a.workspaceId === b.workspaceId && a.projectId === b.projectId;
export function money(value: unknown): value is string {
  return typeof value === 'string' && /^(0|[1-9]\d{0,15})(\.\d{1,2})?$/.test(value);
}
export function cents(value: string): bigint {
  if (!money(value)) throw new Error('จำนวนเงินต้องเป็นบาทและสตางค์ ไม่เกินขนาดที่ระบบรองรับ');
  const [whole, fraction = ''] = value.split('.'); return BigInt(whole) * BigInt(100) + BigInt(fraction.padEnd(2, '0'));
}
export function statusLabel(state: string): string {
  return ({ Draft: 'ร่าง', Pending: 'รอตรวจ', Returned: 'ส่งกลับแก้ไข', Rejected: 'ปฏิเสธ', Active: 'เปิดใช้', Retired: 'ฉบับเก่า', Approved: 'อนุมัติ' } as Record<string, string>)[state] ?? 'ไม่ทราบสถานะ';
}
export function sourceLabel(mode: string): string {
  return ({ Company: 'กติกาบริษัท', Override: 'กำหนดเฉพาะโครงการ', Inherit: 'ใช้กติกาบริษัทล่าสุด' } as Record<string, string>)[mode] ?? 'ไม่ทราบที่มา';
}
export function differences(before: PolicyContent | null, after: PolicyContent): string[] {
  if (!before) return ['ฉบับแรก ยังไม่มีฉบับก่อนหน้า'];
  const result: string[] = [];
  const change = (name: string, a: unknown, b: unknown) => { if (a !== b) result.push(`${name}: ${a ?? 'ไม่กำหนด'} → ${b ?? 'ไม่กำหนด'}`); };
  change('วันเคลียร์', before.clearingDays, after.clearingDays);
  const bands = new Map(before.bands.map(x => [x.order, x]));
  for (const band of after.bands) { const old = bands.get(band.order); change(`ระดับ ${band.order}`, old?.levelCode, band.levelCode); change(`วงเงิน ${band.levelCode}`, old?.upperInclusive, band.upperInclusive); bands.delete(band.order); }
  for (const band of bands.values()) result.push(`นำระดับ ${band.levelCode} ออกในฉบับใหม่`);
  const categories = new Map(before.categories.map(x => [x.id, x]));
  for (const category of after.categories) {
    const old = categories.get(category.id);
    change(`หมวด ${category.name}`, old?.name, category.name);
    change(`สถานะ ${category.name}`, old ? old.active ? 'เปิดใช้' : 'ปิดใช้' : null, category.active ? 'เปิดใช้' : 'ปิดใช้');
    change(`หน่วย ${category.name}`, old?.unit, category.unit);
    change(`อัตรา ${category.name}`, old?.standardRate, category.standardRate);
    change(`หลักฐาน ${category.name}`, old?.evidenceTypes.join(', '), category.evidenceTypes.join(', '));
    categories.delete(category.id);
  }
  for (const category of categories.values()) result.push(`หมวด ${category.name} ไม่อยู่ในฉบับใหม่ (ประวัติเดิมยังคงอยู่)`);
  return result;
}
export function failureState<T>(state: T, status: number): T & { blocked: boolean } {
  return { ...state, ...([401, 403, 409].includes(status) ? { policies: [], selected: null, context: null, reason: '' } : {}), blocked: true };
}
export function validateContent(content: PolicyContent): void {
  if (!content || !Number.isSafeInteger(content.clearingDays) || content.clearingDays <= 0 || content.clearingDays > 2147483647 || !Array.isArray(content.bands) || !content.bands.length || !Array.isArray(content.categories) || !content.categories.length) throw new Error('กรอกวันเคลียร์ ระดับวงเงิน และหมวดให้ครบ');
  let prior = BigInt(0); const levels = new Set(['supervisor', 'project-manager']); const ids = new Set<string>();
  for (const [i, band] of content.bands.entries()) {
    if (!band || band.order !== i + 1 || !band.levelCode?.trim() || band.levelCode !== band.levelCode.trim() || band.levelCode.length > 64 || levels.has(band.levelCode.toLowerCase()) || cents(band.upperInclusive) <= prior) throw new Error('ระดับวงเงินต้องเรียงเพิ่ม ไม่ซ้ำ และมีขอบบนเป็นบาทและสตางค์');
    prior = cents(band.upperInclusive); levels.add(band.levelCode.toLowerCase());
  }
  for (const c of content.categories) {
    if (!c || !validId(c.id) || ids.has(c.id) || !c.name?.trim() || typeof c.active !== 'boolean' || !Array.isArray(c.evidenceTypes) || !c.evidenceTypes.length || c.evidenceTypes.some(x => typeof x !== 'string' || !x.trim()) || !(c.standardRate === null || money(c.standardRate) && !!c.unit?.trim())) throw new Error('หมวดต้องมีชื่อ หลักฐาน และหน่วยเมื่อกำหนดอัตรา');
    ids.add(c.id);
  }
}
export function parsePolicy(value: unknown, scope: FinanceScope): PolicyView {
  const v = value as PolicyView;
  if (!v || !validId(v.id) || !v.scope || !sameScope(v.scope, scope) || !Number.isSafeInteger(v.version) || v.version < 1 || !Number.isSafeInteger(v.expectedActiveVersion) || v.expectedActiveVersion < 0 || !['Company', 'Override', 'Inherit'].includes(v.mode) || !['Draft', 'Pending', 'Returned', 'Rejected', 'Active', 'Retired', 'Approved'].includes(v.state) || !/^[a-f0-9]{64}$/.test(v.contentHash)) throw new Error('ข้อมูลกติกาที่ได้รับไม่ถูกต้อง');
  validateContent(v.content); return v;
}

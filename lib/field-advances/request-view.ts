import type { RequestContent, RequestMode } from './request-types';

export function requestQueueMode(capabilities: string[], review: boolean, preferred: RequestMode): RequestMode | null {
  if (review) return capabilities.includes('request.approve') ? 'review' : null;
  const modes = ['mine', 'delegated', 'report'] as const;
  const capability = { mine: 'request.create', delegated: 'request.delegate', report: 'report.read' };
  const allowed = modes.filter(mode => capabilities.includes(capability[mode]));
  return allowed.find(mode => mode === preferred) ?? allowed[0] ?? null;
}

export function requestStateLabel(state: string): string {
  return ({ Draft: 'ร่าง', AwaitingOwner: 'รอผู้รับผิดชอบยืนยัน', PendingApproval: 'รออนุมัติ', Held: 'พักสายอนุมัติ', Returned: 'ส่งกลับเพื่อแก้ไข', Approved: 'อนุมัติแล้ว รอดำเนินการจ่าย', Rejected: 'ปฏิเสธแล้ว', Withdrawn: 'ถอนแล้ว' } as Record<string, string>)[state] ?? 'ไม่ทราบสถานะ';
}
export function diffRequestContent(previous: RequestContent, current: RequestContent): string[] {
  const changes: string[] = [];
  for (const [key, label] of [['purpose', 'รายละเอียดงาน'], ['kind', 'รูปแบบ'], ['fieldStart', 'วันเริ่มออกสนาม'], ['fieldEnd', 'วันสิ้นสุดออกสนาม'], ['neededOn', 'วันที่ต้องการเงิน']] as const)
    if (previous[key] !== current[key]) changes.push(`${label}: ${previous[key]} → ${current[key]}`);
  if (JSON.stringify([...previous.teamPersonIds].sort()) !== JSON.stringify([...current.teamPersonIds].sort())) changes.push('สมาชิกทีมเปลี่ยนแปลง');
  for (let i = 0; i < Math.max(previous.lines.length, current.lines.length); i++)
    if ((['id', 'categoryId', 'description', 'mode', 'quantity', 'unitRate', 'lumpSum'] as const).some(key => previous.lines[i]?.[key] !== current.lines[i]?.[key])) changes.push(`รายการประมาณการ ${i + 1} เปลี่ยนแปลง`);
  return changes;
}

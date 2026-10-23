'use client';
import type { RequestView, RequestScreen, RequestHistory, ConfirmationQuote } from '@/lib/field-advances/request-types';
import { diffRequestContent, requestStateLabel } from '@/lib/field-advances/request-view';

export default function RequestDetails({ row, screen, history, quote }: { row: RequestView; screen: RequestScreen; history: RequestHistory[]; quote: ConfirmationQuote | null }) {
  const policy = screen.frozenPolicy ?? quote?.policy;
  const differences = screen.previousContent ? diffRequestContent(screen.previousContent, row.content) : [];
  const holdReasons: Record<string, string> = { 'route-no-candidate': 'ไม่มีผู้อนุมัติที่เข้าเงื่อนไข', 'route-cycle': 'สายบังคับบัญชาวนซ้ำ', 'route-ambiguous-role': 'ผู้ดำรงบทบาทอนุมัติไม่ชัดเจน', 'route-identity-unavailable': 'ตรวจสอบตัวบุคคลผู้อนุมัติไม่ได้', 'route-invalid-policy': 'กติกาฉบับนี้ใช้กำหนดสายอนุมัติไม่ได้', 'route-invalid-level': 'ระดับอนุมัติไม่ตรงกับกติกาฉบับนี้', 'route-invalid-context': 'ข้อมูลสายอนุมัติไม่พร้อม' };
  const actions: Record<string, string> = { DraftCreated: 'สร้างร่าง', DraftUpdated: 'แก้ไขร่าง', ReturnedToAuthor: 'ส่งกลับผู้จัดทำ', SentToOwner: 'ส่งให้ผู้รับผิดชอบ', Confirmed: 'ยืนยันและส่ง', Approved: 'อนุมัติขั้น', Returned: 'ส่งกลับ', Rejected: 'ปฏิเสธ', Withdrawn: 'ถอนคำขอ', Revised: 'สร้างฉบับแก้ไข', Copied: 'คัดลอก', 'field-advance.request.reevaluate': 'ประเมินสายอนุมัติใหม่' };
  return <section className="space-y-4" aria-label="รายละเอียดคำขอ">
    <p>รหัสคำขอ <span data-testid="request-id" className="break-all">{row.id}</span></p>
    <p>ฉบับที่ {screen.revisionNumber}</p><p data-testid="request-state" className="font-semibold">{requestStateLabel(row.state)}</p>
    <p>{row.content.purpose}</p><p>ออกสนาม {row.content.fieldStart} ถึง {row.content.fieldEnd} · ต้องการเงิน {row.content.neededOn}</p>
    <p>รูปแบบ {row.content.kind === 'Team' ? 'ทีม' : 'รายบุคคล'} · สกุลเงิน THB</p>
    <p className="break-all">ผู้รับผิดชอบเงิน (รหัสบุคคล): {row.ownerPersonId}</p>
    {row.delegatePersonId && <p className="break-all">ผู้ยื่นแทน (รหัสบุคคล): {row.delegatePersonId}</p>}
    {row.content.teamPersonIds.length > 0 && <ul aria-label="รหัสบุคคลสมาชิกทีม" className="break-all">{row.content.teamPersonIds.map(id => <li key={id}>{id}</li>)}</ul>}
    {row.content.teamPersonIds.length > 0 && <p>สมาชิกทีมที่เลือก: {row.content.teamPersonIds.length} คน (ไม่มีสิทธิ์การเงินอัตโนมัติ)</p>}
    {row.estimate && <><p className="font-semibold">ยอดจากเซิร์ฟเวอร์: {row.estimate.total} บาท</p><ul>{row.estimate.lines.map(line => <li key={line.id}>{line.description} · {line.quantity !== null ? `${line.quantity} × ${line.unitRate} = ` : ''}{line.amount} บาท</li>)}</ul></>}
    {row.currentStep && <p>ขั้นปัจจุบัน: {row.currentStep.levelCode}</p>}
    {row.holdCode && <p>เหตุผลพักสายอนุมัติ: {holdReasons[row.holdCode] ?? row.holdCode}</p>}
    {screen.previousContent && <section><h2 className="font-semibold">ความต่างจากฉบับก่อน</h2>{differences.length ? <ul>{differences.map(x => <li key={x}>{x}</li>)}</ul> : <p>เนื้อหาไม่เปลี่ยนแปลง</p>}</section>}
    {policy && <section className="space-y-2 rounded-xl border p-4"><h2 className="font-semibold">{screen.frozenPolicy ? 'กติกาที่ตรึงไว้' : 'กติกาสำหรับทบทวน'}</h2>
      <p className="break-all">ฉบับบริษัท {policy.companyVersionId} · ฉบับโครงการ {policy.projectVersionId ?? 'ใช้บริษัท'}</p>
      <p>วันเคลียร์ {policy.content.clearingDays} วัน</p><ul>{policy.content.categories.map(c => <li key={c.id}>{c.name} · อัตรา {c.standardRate ?? 'ตามรายการ'} · หลักฐาน {c.evidenceTypes.join(', ')}</li>)}</ul>
      <p>ระดับวงเงิน: {policy.content.bands.map(b => `${b.levelCode} ไม่เกิน ${b.upperInclusive} บาท`).join(' → ')}</p>
    </section>}
    <section><h2 className="font-semibold">ประวัติ (เวลาเอเชีย/กรุงเทพฯ)</h2><ol className="space-y-3">{history.map(h => <li key={h.eventId} className="border-l-2 pl-3"><p>{actions[h.action] ?? h.action} · {h.actor === null ? 'ระบบ' : `ผู้ดำเนินการ ${h.actor}`}</p><time dateTime={h.time}>{new Intl.DateTimeFormat('th-TH', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Asia/Bangkok' }).format(new Date(h.time))}</time><p>{h.reason}</p><p className="break-all text-sm">ฉบับ {h.revisionId}</p></li>)}</ol></section>
  </section>;
}

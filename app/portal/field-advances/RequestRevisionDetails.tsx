import type { RequestRevisionDetail } from '@/lib/field-advances/request-types';

export default function RequestRevisionDetails({ revision: r }: { revision: RequestRevisionDetail }) {
  const outcomes: Record<string, string> = { Approved: 'อนุมัติ', Returned: 'ส่งกลับ', Rejected: 'ปฏิเสธ' };
  return <section aria-label="หลักฐานฉบับคำขอ" className="space-y-3 rounded-xl border p-4">
    <h2 className="font-semibold">ฉบับที่ {r.revisionNumber} · อ่านอย่างเดียว</h2>
    <p>{r.content.purpose}</p><p>ออกสนาม {r.content.fieldStart} ถึง {r.content.fieldEnd} · ต้องการเงิน {r.content.neededOn}</p>
    <p>เวลาส่ง {r.submittedAtUtc ? new Intl.DateTimeFormat('th-TH', { dateStyle: 'medium', timeStyle: 'medium', timeZone: 'Asia/Bangkok' }).format(new Date(r.submittedAtUtc)) : 'ยังไม่ส่ง'} (เอเชีย/กรุงเทพฯ)</p>
    {r.estimate && <><p>ยอดที่บันทึก {r.estimate.total} บาท</p><ul>{r.estimate.lines.map(l => <li key={l.id}>{l.description} · {l.quantity !== null ? `${l.quantity} × ${l.unitRate} = ` : ''}{l.amount} บาท</li>)}</ul></>}
    {r.frozenPolicy && <section><h3 className="font-semibold">กติกาที่ตรึงไว้ของฉบับนี้</h3><p className="break-all">บริษัท {r.frozenPolicy.companyVersionId} · โครงการ {r.frozenPolicy.projectVersionId ?? 'ใช้บริษัท'}</p>
      <p>วันเคลียร์ {r.frozenPolicy.content.clearingDays} วัน</p><ul>{r.frozenPolicy.content.categories.map(c => <li key={c.id}>{c.name} · อัตรา {c.standardRate ?? 'ตามรายการ'} · หลักฐาน {c.evidenceTypes.join(', ')}</li>)}</ul>
      <p>ระดับวงเงิน {r.frozenPolicy.content.bands.map(b => `${b.levelCode} ไม่เกิน ${b.upperInclusive} บาท`).join(' → ')}</p></section>}
    <h3 className="font-semibold">ผลแต่ละขั้นในฉบับนี้</h3><ol>{r.steps.map(s => <li key={s.id} className="break-words">{s.order}. {s.levelCode} · {s.outcome ? outcomes[s.outcome] : 'ไม่มีผลอนุมัติ'} · {s.decisionReason ?? ''}{s.decidedByPersonId && <span> · ผู้ลงผล {s.decidedByPersonId}</span>}</li>)}</ol>
    <p>กลับฉบับปัจจุบันก่อนดำเนินการคำขอ</p>
  </section>;
}

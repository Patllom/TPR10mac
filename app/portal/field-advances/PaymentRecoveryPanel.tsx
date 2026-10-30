 'use client';
import { useState } from 'react';
import type { InstallmentDetail, PaymentAction, PaymentProposal, PaymentUiContext, EvidenceTicket } from '@/lib/field-advances/payment-types';
export default function PaymentRecoveryPanel({ detail, actions, context, busy, send, upload, download }: {
  detail: InstallmentDetail; actions: PaymentAction[]; context: PaymentUiContext; busy: boolean;
  send: (action: PaymentAction, input: unknown, reason: string, proposal?: PaymentProposal) => void;
  upload: (file: File) => Promise<EvidenceTicket | undefined>; download: (id: string) => void;
}) {
  const [reason, setReason] = useState(''), [date, setDate] = useState(''), [reference, setReference] = useState('');
  const [evidence, setEvidence] = useState<EvidenceTicket[]>([]), [uploading, setUploading] = useState(false);
  const [outcome, setOutcome] = useState('Unknown'), [observed, setObserved] = useState(''), [recipient, setRecipient] = useState('');
  const revision = detail.revisions.find(x => x.isCurrent)!;
  const metadata = { paidOn: date, reference: reference || null, evidenceIds: evidence.map(x => x.id) };
  const actual = { ...metadata, amount: revision.amount, recipientPersonId: revision.destination.recipientPersonId };
  const proposals = detail.proposals.filter(p => context.proposals.some(x => x.proposalId === p.id && x.allowedActions.includes('confirm')));
  const canWrite = actions.some(x => ['record', 'outcome', 'takeover', 'metadata', 'mismatch', 'cancel'].includes(x));
  const disabled = busy || uploading || !reason.trim(), needsEvidence = disabled || evidence.length === 0;
  return <section className="space-y-3 rounded border p-4" aria-label="บันทึกผลและสะสาง">
    <h2 className="font-semibold">บันทึกผลและสะสาง</h2>
    {(canWrite || proposals.length > 0) && <label className="block">เหตุผลและหลักฐานประกอบ<textarea className="block w-full border p-2" value={reason} maxLength={2000} onChange={e => setReason(e.target.value)} /></label>}
    {canWrite && <>
      <label className="block">วันที่จ่ายจริง<input className="block w-full border p-2" type="date" value={date} onChange={e => setDate(e.target.value)} /></label>
      <label className="block">เลขอ้างอิง<input className="block w-full border p-2" value={reference} onChange={e => setReference(e.target.value)} /></label>
      <label className="block">เพิ่มหลักฐาน<input className="block w-full" type="file" accept="image/png,image/jpeg,application/pdf" disabled={busy || uploading} onChange={async e => { const file = e.target.files?.[0]; e.target.value = ''; if (!file) return; setUploading(true); try { const ticket = await upload(file); if (ticket) setEvidence(x => [...x, ticket]); } finally { setUploading(false); } }} /></label>
      <p role="status">{uploading ? 'กำลังจัดเตรียมหลักฐาน' : `หลักฐานพร้อมใช้ ${evidence.length} ไฟล์`}</p>
      {actions.includes('record') && <button disabled={needsEvidence || !date} onClick={() => send('record', actual, reason)}>บันทึกจ่ายตรงงวด</button>}
      {actions.includes('outcome') && <><label className="block">ผลที่เสนอ<select className="block w-full border p-2" value={outcome} onChange={e => setOutcome(e.target.value)}><option value="Unknown">ยังไม่ชัดเจน</option><option value="Unpaid">ไม่ได้จ่ายเงินจริง</option><option value="Matched">จ่ายสำเร็จตรงงวด</option></select></label><button disabled={needsEvidence || outcome === 'Matched' && !date} onClick={() => send('outcome', { outcome, actual: outcome === 'Matched' ? actual : null, evidenceIds: metadata.evidenceIds }, reason)}>เสนอผลเพื่อให้ผู้ตรวจยืนยัน</button></>}
      {actions.includes('takeover') && <button disabled={needsEvidence} onClick={() => send('takeover', metadata.evidenceIds, reason)}>เสนอรับช่วงสะสาง</button>}
      {actions.includes('cancel') && <button disabled={disabled} onClick={() => send('cancel', null, reason)}>เสนอยกเลิกงวดที่ยังไม่จ่าย</button>}
      {actions.includes('metadata') && <button disabled={needsEvidence || !date} onClick={() => send('metadata', metadata, reason)}>เสนอแก้วันจ่ายและหลักฐาน</button>}
      {actions.includes('mismatch') && <fieldset className="space-y-2 border p-3"><legend>พบจ่ายผิดยอดหรือผิดคน</legend><p>ระงับเริ่มจ่ายทั้งชุด ไม่คืนวงเงินหรือเปิดจ่ายซ้ำ</p><label className="block">ยอดที่พบจริง<input className="block w-full border p-2" inputMode="decimal" value={observed} onChange={e => setObserved(e.target.value)} /></label><label className="block">รหัสบุคคลผู้รับที่พบจริง<input className="block w-full border p-2" value={recipient} onChange={e => setRecipient(e.target.value)} /></label><button disabled={needsEvidence || !date || !observed || !recipient} onClick={() => send('mismatch', { ...metadata, amount: observed, recipientPersonId: recipient }, reason)}>รายงานเหตุจ่ายไม่ตรง</button></fieldset>}
    </>}
    {proposals.map(p => <section className="space-y-2 border p-3" key={p.id}><h3>ข้อเสนอรอตรวจ: {({ PaymentCancellation: 'ยกเลิกงวด', PaymentMetadata: 'แก้วันจ่ายและหลักฐาน', PaymentOutcome: 'สะสางผล', PaymentTakeover: 'รับช่วง' } as Record<string, string>)[p.kind] ?? p.kind}</h3><p>{p.reason}</p><p>ผลเสนอ: {p.after.payment?.outcome ?? p.after.state ?? p.after.outcome ?? 'ตรวจหลักฐานประกอบ'}</p><p>วันจ่ายเดิม {p.before.metadata?.paidOn ?? '—'} → ใหม่ {p.after.metadata?.paidOn ?? p.after.payment?.actual?.paidOn ?? '—'}</p>{(p.before.metadata || p.after.metadata) && <><p>เลขอ้างอิงเดิม: {p.before.metadata?.reference ?? 'ไม่ระบุ'}</p><p>เลขอ้างอิงใหม่: {p.after.metadata?.reference ?? 'ไม่ระบุ'}</p></>}{p.after.payment?.actual && <p>ยอด {p.after.payment.actual.amount} · ผู้รับ {p.after.payment.actual.recipientPersonId}</p>}{p.evidence.map(e => <button key={e.id} disabled={busy} onClick={() => download(e.id)}>อ่านหลักฐานข้อเสนอ {e.id}</button>)}<button disabled={disabled} onClick={() => send('confirm', null, reason, p)}>ยืนยันข้อเสนอนี้</button></section>)}
    {detail.evidence.map(e => <button className="block break-all underline" key={e.id} disabled={busy} onClick={() => download(e.id)}>ดาวน์โหลดหลักฐาน {e.id}</button>)}
  </section>;
}

'use client';

import type { SettlementWorkItem } from '@/lib/field-advances/settlement-work-client';

export default function SettlementAdjustmentPanel({ item, onDownload, disabled }: {
  item: SettlementWorkItem;
  onDownload: (evidenceId: string) => void;
  disabled: boolean;
}) {
  const detail = item.detail;
  if (!detail) return null;
  const ids = detail.refund?.evidenceIds ?? detail.incident?.evidenceIds
    ?? detail.recovery?.evidenceIds ?? detail.correction?.evidenceIds ?? detail.payment?.evidenceIds ?? [];

  return <section className="space-y-2 rounded border p-3">
    {detail.refund && <>
      <h3 className="font-semibold">เงินคืนที่รอตรวจ</h3>
      <p>จำนวน {detail.refund.amount} บาท · วันที่คืน {detail.refund.returnedOn}</p>
      <p>ช่องทาง {detail.refund.channel} · ผู้รับ {detail.refund.companyRecipient}</p>
      <p>หลักฐาน {detail.refund.evidenceIds.length} ไฟล์</p>
    </>}
    {detail.correction && <>
      <h3 className="font-semibold">แก้ยอดคืน</h3>
      <p>ยอดเดิม {detail.correction.beforeAmount} บาท → ยอดใหม่ {detail.correction.afterAmount} บาท</p>
      <p>ส่วนต่าง {detail.correction.delta} บาท · เวอร์ชันฐาน {detail.correction.baseVersion}</p>
      <p>หลักฐาน {detail.correction.evidenceIds.length} ไฟล์</p>
    </>}
    {detail.adjustment && <>
      <h3 className="font-semibold">เคสปรับปรุงหลังปิด</h3>
      <p>ฐานเดิม {detail.adjustment.baselineAmount ?? 'ยังไม่มี'} บาท · จ่ายภายนอก {detail.adjustment.externalPaidAmount} บาท</p>
      <p>หลักฐาน {detail.adjustment.evidenceIds.length} ไฟล์</p>
    </>}
    {detail.payment && <>
      <h3 className="font-semibold">ผลจ่ายที่รอตรวจ</h3>
      <p>ยอด {detail.payment.amount} บาท · วันที่จ่าย {detail.payment.paidOn}</p>
      <p>หลักฐาน {detail.payment.evidenceIds.length} ไฟล์</p>
    </>}
    {detail.completion && <>
      <h3 className="font-semibold">แก้วันจบงาน</h3>
      <p>วันจบเดิม {detail.completion.previousCompletedOn ?? 'ยังไม่มี'} → วันจบใหม่ {detail.completion.completedOn}</p>
      <p>กำหนดเคลียร์ใหม่ {detail.completion.clearingDueOn} · กำหนดคืนใหม่ {detail.completion.refundDueOn}</p>
    </>}
    {detail.recovery && <>
      <h3 className="font-semibold">เงินเรียกคืน</h3>
      <p>{detail.recovery.returnKind} · {detail.recovery.amount} บาท · {detail.recovery.returnedOn}</p>
      <p>หลักฐาน {detail.recovery.evidenceIds.length} ไฟล์</p>
    </>}
    {detail.excess && <>
      <h3 className="font-semibold">ยอดส่วนเกินจากเอกสาร</h3>
      <p>ยอดเอกสาร {detail.excess.amount} บาท · ขอเบิก {detail.excess.claimedAmount} บาท · เพดาน {detail.excess.capAmount} บาท</p>
      <p>ผลจ่ายภายนอกที่ผู้ตรวจยืนยันแล้ว {detail.excess.acceptedPaymentIds.length} รายการ</p>
    </>}
    {detail.duplicate && <>
      <h3 className="font-semibold">เอกสารที่ต้องตรวจซ้ำ</h3>
      <p>เอกสารคำขอนี้ {detail.duplicate.documentId} · เวอร์ชัน {detail.duplicate.documentVersion}</p>
    </>}
    {ids.map((id, index) => <button key={id} className="mr-3 underline" disabled={disabled}
      onClick={() => onDownload(id)}>ดาวน์โหลดหลักฐาน {index + 1}</button>)}
  </section>;
}

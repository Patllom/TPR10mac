import { validId } from './policy-view';
import type { PaymentSummary } from './payment-types';
export const paymentMoney = (v: unknown): v is string => typeof v === 'string' && /^\d{1,16}(?:\.\d{1,2})?$/.test(v);
export const paymentVersion = (v: unknown): v is number => typeof v === 'number' && Number.isSafeInteger(v) && v >= 0;
export function parsePaymentSummary(value: unknown): PaymentSummary {
  if (!value || typeof value !== 'object') throw new Error('ข้อมูลการจ่ายเงินไม่ถูกต้อง');
  const v = value as PaymentSummary;
  if (!validId(v.requestId) || !(v.installmentId === null || validId(v.installmentId))
    || !paymentVersion(v.familyVersion) || !paymentVersion(v.requestVersion) || !(v.installmentVersion === null || paymentVersion(v.installmentVersion))
    || (v.installmentId === null) !== (v.installmentVersion === null) || typeof v.state !== 'string' || !v.state
    || [v.approved, v.spendable, v.reserved, v.paid, v.available].some(x => !paymentMoney(x))
    || !Array.isArray(v.holdCodes) || v.holdCodes.some(x => typeof x !== 'string')) throw new Error('ข้อมูลการจ่ายเงินไม่ถูกต้อง');
  // Project the wire allowlist; never retain unexpected server data in a protected view.
  return { requestId: v.requestId, installmentId: v.installmentId, familyVersion: v.familyVersion, requestVersion: v.requestVersion,
    installmentVersion: v.installmentVersion, state: v.state, approved: v.approved, spendable: v.spendable, reserved: v.reserved, paid: v.paid, available: v.available, holdCodes: [...v.holdCodes] };
}
export function paymentStateLabel(state: string): string {
  return ({ Draft: 'ร่างงวด', PendingReview: 'รอตรวจงวด', Returned: 'ส่งกลับแก้ไข', Reviewed: 'ตรวจผ่าน รอเริ่มจ่าย', Paying: 'เริ่มจ่ายแล้ว รอยืนยันผล', Paid: 'บันทึกจ่ายแล้ว', CancelPending: 'รอตรวจยกเลิก', Cancelled: 'ยกเลิกงวด', Approved: 'อนุมัติแล้ว รอดำเนินการจ่าย' } as Record<string, string>)[state] ?? 'กรุณาตรวจสอบสถานะล่าสุด';
}

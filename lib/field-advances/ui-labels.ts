const holds: Record<string, string> = {
  PaymentMismatch: 'พบการจ่ายไม่ตรง',
  CeilingAmendment: 'กำลังแก้ไขวงเงิน',
  PaymentTerminated: 'ยุติการจ่าย'
};
const outcomes: Record<string, string> = {
  Unknown: 'ยังไม่ชัดเจน', Unpaid: 'ไม่ได้จ่ายเงินจริง', Matched: 'จ่ายสำเร็จตรงงวด',
  Cancelled: 'ยกเลิกแล้ว', Paid: 'จ่ายแล้ว', Pending: 'รอตรวจ'
};
const states: Record<string, string> = {
  Draft: 'ร่าง', Pending: 'รอตรวจ', Accepted: 'รับรองแล้ว', Approved: 'อนุมัติแล้ว',
  Returned: 'ส่งกลับ', Rejected: 'ปฏิเสธ', Cancelled: 'ยกเลิกแล้ว',
  Closed: 'ปิดแล้ว', Confirmed: 'ยืนยันแล้ว', Resolved: 'สะสางแล้ว',
  AwaitingOwner: 'รอผู้รับผิดชอบยืนยัน', PendingApproval: 'รออนุมัติ',
  Held: 'พักสายอนุมัติ', Withdrawn: 'ถอนแล้ว', Submitted: 'ส่งตรวจแล้ว'
};
const sources: Record<string, string> = { Owner: 'ผู้รับผิดชอบเงิน', WrongRecipient: 'ผู้รับเงินผิดคน' };
const proposalKinds: Record<string, string> = {
  PaymentCancellation: 'ยกเลิกงวด', PaymentMetadata: 'แก้วันจ่ายและหลักฐาน',
  PaymentOutcome: 'สะสางผล', PaymentTakeover: 'รับช่วง',
  InstallmentReview: 'ตรวจงวดจ่าย', PaymentMismatch: 'รายงานจ่ายไม่ตรง',
  PaymentDisposition: 'กำหนดแนวทางจ่ายคงเหลือ'
};
const own = (labels: Record<string, string>, code: string): string | undefined =>
  Object.prototype.hasOwnProperty.call(labels, code) ? labels[code] : undefined;
export const holdLabel = (code: string): string => own(holds, code) ?? 'เหตุระงับที่ต้องตรวจสอบ';
export const paymentOutcomeLabel = (code: string): string => own(outcomes, code) ?? own(states, code) ?? 'ผลที่ต้องตรวจสอบ';
export const reviewStateLabel = (code: string): string => own(states, code) ?? 'สถานะที่ต้องตรวจสอบ';
export const recoverySourceLabel = (code: string): string => own(sources, code) ?? 'แหล่งเงินที่ต้องตรวจสอบ';
export const paymentProposalKindLabel = (code: string): string =>
  own(proposalKinds, code) ?? 'ประเภทข้อเสนอที่ต้องตรวจสอบ';

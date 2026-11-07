import type { SettlementSummary } from '@/lib/field-advances/settlement-types';
import { stateLabel,timeLabel } from '@/lib/field-advances/settlement-view';
export default function SettlementStatus({summary}:{summary:SettlementSummary}){
 const values=[['เงินจริงที่เจ้าของได้รับ',summary.totals.ownerReceived],['ค่าใช้จ่ายรับรองแล้ว',summary.totals.approvedExpense],['คืนเงินยืนยันแล้ว',summary.totals.confirmedRefund],['คืนเงินรอตรวจ',summary.totals.pendingRefund],['ยอดคงเหลือที่เจ้าของต้องสะสาง',summary.totals.ownerOutstanding],['เงินผิดผู้รับที่ติดตามแยก',summary.totals.wrongRecipientOutstanding]];
 return <section className="space-y-3" aria-label="สถานะเวลาและยอดเงิน"><p>สถานะคำขอ: {stateLabel(summary.state)}</p><dl className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">{values.map(([label,value])=><div className="rounded-xl border p-3" key={label}><dt>{label}</dt><dd className="font-semibold break-all">{value} บาท</dd></div>)}</dl><p>ยอดรอตรวจแยกจากคืนเงินยืนยันแล้ว และยังไม่ลดหนี้ที่รับรอง</p>
 <h2 className="font-semibold">สถานะเวลาและการส่งครบ</h2><p>{summary.declaration&&!summary.declaration.isWithdrawal?'ส่งครบแล้ว รอตรวจรับรอง':'ยังไม่ยืนยันส่งครบ หรือถอนแล้ว'}</p>{summary.declaration&&<p>{timeLabel(summary.declaration.completionOnTime)} · บันทึกระบบ {new Intl.DateTimeFormat('th-TH',{dateStyle:'medium',timeStyle:'short',timeZone:'Asia/Bangkok'}).format(new Date(summary.declaration.declaredAtUtc))}</p>}
 {summary.holds.length>0&&<p>มีเหตุพัก: {summary.holds.join(', ')}</p>}{summary.totals.hasOpenCase&&<p>มีรายการปรับปรุงค้างตรวจ</p>}</section>;
}

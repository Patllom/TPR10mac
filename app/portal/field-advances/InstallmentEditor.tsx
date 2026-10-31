 'use client';
import { useId, useState } from 'react';
import type { PaymentDestination } from '@/lib/field-advances/payment-types';
export default function InstallmentEditor({ ownerPersonId, initial, busy, save }: { ownerPersonId: string; initial?: { amount: string; destination: PaymentDestination }; busy: boolean; save: (input: { amount: string; destination: PaymentDestination }, reason: string) => void }) {
  const [amount, setAmount] = useState(initial?.amount ?? ''), [channel, setChannel] = useState<'Cash' | 'Transfer'>(initial?.destination.channel ?? 'Cash');
  const [bank, setBank] = useState(initial?.destination.bankCode ?? ''), [account, setAccount] = useState(initial?.destination.accountNumber ?? ''), [holder, setHolder] = useState(initial?.destination.accountHolder ?? ''), [reason, setReason] = useState('');
  const amountErrorId = useId(), [amountTouched, setAmountTouched] = useState(false);
  const validAmount = /^[0-9]+(?:\.[0-9]{1,2})?$/.test(amount) && /[1-9]/.test(amount);
  const amountError = amountTouched && !validAmount ? 'กรอกยอดงวดมากกว่าศูนย์และทศนิยมไม่เกิน 2 ตำแหน่ง' : '';
  return <form className="space-y-3 rounded border p-4" onSubmit={e => { e.preventDefault(); setAmountTouched(true); if (!busy && validAmount) save({ amount, destination: { channel, recipientPersonId: ownerPersonId, bankCode: channel === 'Cash' ? null : bank, accountNumber: channel === 'Cash' ? null : account, accountHolder: channel === 'Cash' ? null : holder } }, reason); }}>
    <h2 className="font-semibold">{initial ? 'แก้ไขงวดและตรวจใหม่' : 'จัดงวดใหม่'}</h2><p className="break-all">ผู้รับผิดชอบเงิน / ผู้รับ: {ownerPersonId}</p>
    <label className="block">ยอดงวด (บาท)<input className="block w-full border p-2" required inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,2})?" value={amount} aria-invalid={!!amountError} aria-describedby={amountError ? amountErrorId : undefined} onBlur={() => setAmountTouched(true)} onInvalid={e => { e.preventDefault(); setAmountTouched(true); e.currentTarget.focus(); }} onChange={e => setAmount(e.target.value)} /></label>
    {amountError && <p id={amountErrorId} role="alert">{amountError}</p>}
    <label className="block">ช่องทาง<select className="block w-full border p-2" value={channel} onChange={e => setChannel(e.target.value as 'Cash' | 'Transfer')}><option value="Cash">เงินสด</option><option value="Transfer">โอนเงิน</option></select></label>
    {channel === 'Transfer' && <><label className="block">ธนาคาร<input className="block w-full border p-2" required value={bank} onChange={e => setBank(e.target.value)} /></label><label className="block">เลขบัญชี<input className="block w-full border p-2" required value={account} onChange={e => setAccount(e.target.value)} /></label><label className="block">ชื่อบัญชี<input className="block w-full border p-2" required value={holder} onChange={e => setHolder(e.target.value)} /></label><p>ตรวจเจ้าของบัญชีจากหลักฐานและผู้ตรวจ ระบบไม่ได้ตรวจชื่อกับธนาคาร</p></>}
    <label className="block">เหตุผลจัดงวด<textarea className="block w-full border p-2" required maxLength={2000} value={reason} onChange={e => setReason(e.target.value)} /></label>
    <button className="rounded border px-4 py-2" disabled={busy || !reason.trim()}>{initial ? 'บันทึกแก้ไขงวด' : 'สร้างงวด'}</button>
  </form>;
}

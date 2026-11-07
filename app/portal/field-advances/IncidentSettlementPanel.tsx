'use client';

import { useState } from 'react';
import type { SettlementWorkItem } from '@/lib/field-advances/settlement-work-client';

type Props = {
  item: SettlementWorkItem;
  disabled: boolean;
  reasonReady: boolean;
  onUpload: (file: File) => Promise<string>;
  onRecover: (amount: string, source: 'Owner' | 'WrongRecipient', evidenceIds: string[]) => void;
  onRelease: () => void;
  onShortfall: (amount: string) => void;
};

export default function IncidentSettlementPanel({ item, disabled, reasonReady, onUpload, onRecover,
  onRelease, onShortfall }: Props) {
  const [amount, setAmount] = useState('');
  const [shortfall, setShortfall] = useState('');
  const [source, setSource] = useState<'Owner' | 'WrongRecipient'>('WrongRecipient');
  const [evidenceIds, setEvidenceIds] = useState<string[]>([]);
  const [uploading, setUploading] = useState(false);
  const incident = item.detail?.incident;
  if (!incident) return null;

  return <section className="space-y-2 rounded border border-amber-600 p-3">
    <h3 className="font-semibold">ข้อเท็จจริงเหตุจ่ายผิด</h3>
    <p>เจ้าของได้รับจริง {incident.ownerReceived} บาท</p>
    <p>ผู้รับผิดคนได้รับ {incident.wrongRecipientReceived} บาท</p>
    <p>ยอดผู้รับผิดคนเป็นลูกหนี้เรียกคืน แยกจากยอดค้างของเจ้าของ</p>
    <p>หลักฐาน {incident.evidenceIds.length} ไฟล์</p>
    {item.allowedActions.includes('recovery-propose') && <div className="space-y-2 border-t pt-3">
      <h4 className="font-semibold">บันทึกเงินเรียกคืน (ยังไม่ยืนยันรับเงิน)</h4>
      <label className="block">ผู้คืนเงิน
        <select className="block w-full rounded border p-2" value={source}
          onChange={event => setSource(event.target.value as 'Owner' | 'WrongRecipient')}>
          <option value="WrongRecipient">ผู้รับผิดคน</option>
          <option value="Owner">เจ้าของเงิน</option>
        </select>
      </label>
      <label className="block">ยอดที่รับคืนจริง
        <input className="block w-full rounded border p-2" inputMode="decimal" value={amount}
          onChange={event => setAmount(event.target.value)} />
      </label>
      <label className="block">หลักฐานรับคืน
        <input className="block w-full" type="file" accept="image/png,image/jpeg,application/pdf"
          disabled={disabled || uploading} onChange={async event => {
            const file = event.target.files?.[0];
            event.target.value = '';
            if (!file) return;
            setUploading(true);
            try {
              const id = await onUpload(file);
              setEvidenceIds(previous => [...previous, id]);
            } catch {
              // The parent shows the protected error and disables the command.
            } finally {
              setUploading(false);
            }
          }} />
      </label>
      <p role="status">หลักฐานรับคืนพร้อมใช้ {evidenceIds.length} ไฟล์</p>
      <button className="underline" disabled={disabled || !reasonReady || uploading || evidenceIds.length === 0
        || !/^\d+(?:\.\d{1,2})?$/.test(amount) || Number(amount) <= 0}
        onClick={() => onRecover(amount, source, evidenceIds)}>
        บันทึกข้อเสนอเงินเรียกคืน
      </button>
    </div>}
    {item.allowedActions.includes('incident-release') && <div className="space-y-2 border-t pt-3">
      <h4 className="font-semibold">ปลดเหตุระงับรายการนี้</h4>
      <p>ต้องตรวจรับเงินเรียกคืนครบก่อน การปลดเหตุนี้ไม่ล้างเหตุระงับอื่นหรือเปิดจ่ายซ้ำ</p>
      <button className="underline" disabled={disabled || !reasonReady} onClick={onRelease}>
        ยืนยันปลดเหตุระงับนี้
      </button>
    </div>}
    {item.allowedActions.includes('shortfall-prepare') && <div className="space-y-2 border-t pt-3">
      <h4 className="font-semibold">เตรียมส่วนจ่ายขาด</h4>
      <p>สร้างงวดใหม่ให้ตรวจอีกครั้ง ไม่มีการสั่งโอนธนาคารจากหน้านี้</p>
      <label className="block">ยอดส่วนขาด
        <input className="block w-full rounded border p-2" inputMode="decimal" value={shortfall}
          onChange={event => setShortfall(event.target.value)} />
      </label>
      <button className="underline" disabled={disabled || !reasonReady || !/^\d+(?:\.\d{1,2})?$/.test(shortfall)
        || Number(shortfall) <= 0} onClick={() => onShortfall(shortfall)}>
        เตรียมงวดส่วนขาดเพื่อส่งตรวจใหม่
      </button>
    </div>}
  </section>;
}

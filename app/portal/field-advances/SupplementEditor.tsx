'use client';
import { useState } from 'react';
import type { RequestContent, RequestView } from '@/lib/field-advances/request-types';
import type { PolicySnapshot } from '@/lib/field-advances/policy-types';
const input = 'block w-full min-w-0 rounded border p-2';
export function money(value: string) { const [whole, fraction = ''] = value.split('.'); return `${whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',')}.${fraction.padEnd(2, '0')}`; }
export default function SupplementEditor({ source, policy, amendment = false, disabled, onSave, onCancel }: {
  source: RequestView; policy: PolicySnapshot; amendment?: boolean; disabled: boolean;
  onSave: (content: RequestContent, reason: string) => void; onCancel: () => void;
}) {
  const [content, setContent] = useState<RequestContent>(() => amendment ? structuredClone(source.content) : {
    ...structuredClone(source.content), purpose: '', lines: [{ id: crypto.randomUUID(), categoryId: policy.content.categories.find(c => c.active)?.id ?? '',
      description: 'ค่าใช้จ่ายส่วนเพิ่ม', mode: 'QuantityRate', quantity: '', unitRate: null, lumpSum: null }]
  });
  const [reason, setReason] = useState('');
  const label = amendment ? 'ปรับวงเงิน' : 'ส่วนเพิ่ม';
  const activeCategory = policy.content.categories.some(c => c.active);
  const update = (i: number, patch: Partial<RequestContent['lines'][number]>) => setContent(c => ({ ...c, lines: c.lines.map((x, n) => n === i ? { ...x, ...patch } : x) }));
  return <form onSubmit={e => { e.preventDefault(); if (activeCategory) onSave(structuredClone(content), reason); }} className="space-y-4 rounded border p-4">
    {!activeCategory && <p role="alert">ไม่มีหมวดค่าใช้จ่ายที่เปิดใช้ กรุณาตรวจสอบกติกาปัจจุบัน</p>}
    <fieldset disabled={disabled} className="min-w-0 space-y-3"><legend>{amendment ? 'เตรียมฉบับปรับวงเงิน' : 'ขอเฉพาะส่วนเพิ่ม'}</legend>
      <p className="break-all">โครงการเดิม {source.scope.projectId} · ผู้รับผิดชอบเดิม {source.ownerPersonId} (ล็อกจากเซิร์ฟเวอร์)</p>
      <p>{amendment ? 'ร่างไม่รับรองสิทธิ์ปรับ ตรวจใหม่เมื่อส่ง และระงับเริ่มจ่ายเมื่อบันทึกฉบับปรับวงเงินสำเร็จ' : 'ประมาณการเฉพาะส่วนเพิ่ม ไม่ยกยอดเดิมมาขอซ้ำ ยังจ่ายไม่ได้จนอนุมัติครบ'}</p>
      <label className="block">รายละเอียดงาน{label}<textarea required className={input} value={content.purpose} onChange={e => setContent(c => ({ ...c, purpose: e.target.value }))} /></label>
      {([['fieldStart', 'วันเริ่มออกสนาม'], ['fieldEnd', 'วันสิ้นสุดออกสนาม'], ['neededOn', 'วันที่ต้องการเงิน']] as const).map(([key, text]) => <label key={key} className="block">{text}{label}<input required type="date" className={input} value={content[key]} onChange={e => setContent(c => ({ ...c, [key]: e.target.value }))} /></label>)}
      <p>รูปแบบและทีมเริ่มจากคำขอเดิม แก้ไขเพิ่มเติมได้ในร่างก่อนผู้รับผิดชอบยืนยัน</p>
      {content.lines.map((line, i) => {
        const category = policy.content.categories.find(c => c.id === line.categoryId), standard = category?.standardRate != null;
        return <fieldset key={line.id} className="min-w-0 space-y-2 border p-3"><legend>รายการ{label} {i + 1}</legend>
          <label>หมวด{label} {i + 1}<select className={input} value={line.categoryId} onChange={e => { const c = policy.content.categories.find(x => x.id === e.target.value)!; update(i, { categoryId: c.id, mode: 'QuantityRate', quantity: '', unitRate: c.standardRate === null ? '' : null, lumpSum: null }); }}>{policy.content.categories.filter(c => c.active || c.id === line.categoryId).map(c => <option key={c.id} value={c.id} disabled={!c.active}>{c.name}</option>)}</select></label>
          <label>รายละเอียด{label} {i + 1}<input required className={input} value={line.description} onChange={e => update(i, { description: e.target.value })} /></label>
          {standard ? <p>อัตรามาตรฐาน {category?.standardRate} บาท</p> : <label>วิธีคำนวณ{label} {i + 1}<select className={input} value={line.mode} onChange={e => update(i, e.target.value === 'LumpSum' ? { mode: 'LumpSum', quantity: null, unitRate: null, lumpSum: '' } : { mode: 'QuantityRate', quantity: '', unitRate: '', lumpSum: null })}><option value="QuantityRate">จำนวน × อัตรา</option><option value="LumpSum">เหมารวม</option></select></label>}
          {line.mode === 'QuantityRate' ? <><label>จำนวน{label} {i + 1}<input required inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,4})?" className={input} value={line.quantity ?? ''} onChange={e => update(i, { quantity: e.target.value })} /></label>{!standard && <label>อัตรา{label} {i + 1}<input required inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,2})?" className={input} value={line.unitRate ?? ''} onChange={e => update(i, { unitRate: e.target.value })} /></label>}</> : <label>ยอดเหมารวม{label} {i + 1}<input required inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,2})?" className={input} value={line.lumpSum ?? ''} onChange={e => update(i, { lumpSum: e.target.value })} /></label>}
          <button type="button" disabled={content.lines.length === 1} onClick={() => setContent(c => ({ ...c, lines: c.lines.filter((_, n) => n !== i) }))}>ลบรายการ{label} {i + 1}</button>
        </fieldset>;
      })}
      <button type="button" onClick={() => { const c = policy.content.categories.find(c => c.active); if (c) setContent(v => ({ ...v, lines: [...v.lines, { id: crypto.randomUUID(), categoryId: c.id, description: '', mode: 'QuantityRate', quantity: '', unitRate: c.standardRate === null ? '' : null, lumpSum: null }] })); }}>เพิ่มรายการ{label}</button>
      <label className="block">เหตุผล{label}<textarea required maxLength={2000} className={input} value={reason} onChange={e => setReason(e.target.value)} /></label>
      <p>จำนวนไม่เกิน 4 ตำแหน่ง เงินไม่เกิน 2 ตำแหน่ง ยอดตัดสินจากเซิร์ฟเวอร์</p>
      <button disabled={!activeCategory} className="rounded border px-4 py-2">{amendment ? 'บันทึกฉบับปรับวงเงินและพักจ่าย' : 'บันทึกร่างส่วนเพิ่ม'}</button>
      <button type="button" className="ml-3 rounded border px-4 py-2" onClick={onCancel}>ยกเลิกการเตรียมร่าง</button>
    </fieldset>
  </form>;
}

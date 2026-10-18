'use client';
import { useState } from 'react';
import type { CaptureChoice } from '@/lib/attendance/capture-view';
import type { SaveCorrection } from '@/lib/attendance/correction-types';
const local = (utc: string | null) => utc ? new Date(Date.parse(utc) + 7 * 3600000).toISOString().slice(0, 16) : '';
export default function CorrectionForm({ initial, choices, busy, save }: { initial: SaveCorrection; choices: CaptureChoice[]; busy: boolean; save: (input: SaveCorrection) => void }) {
  const [date, setDate] = useState(initial.proposal.workDate);
  const [entered, setEntered] = useState(local(initial.proposal.inUtc));
  const [exited, setExited] = useState(local(initial.proposal.outUtc));
  const [inSite, setInSite] = useState(initial.proposal.inSite.siteId);
  const [outSite, setOutSite] = useState(initial.proposal.outSite?.siteId ?? '');
  const [reason, setReason] = useState(initial.proposal.reason);
  const [error, setError] = useState('');
  const options = [...choices];
  for (const site of [initial.proposal.inSite, initial.proposal.outSite]) if (site?.siteId && !options.some(x => x.site.siteId === site.siteId)) options.push({ site, label: `สถานที่ตามคำร้องเดิม ${site.siteId}` });
  return <form className="min-w-0 space-y-4" onSubmit={e => {
    e.preventDefault(); setError('');
    const input = options.find(x => x.site.siteId === inSite)?.site, output = options.find(x => x.site.siteId === outSite)?.site;
    const a = Date.parse(entered + ':00+07:00'), b = exited ? Date.parse(exited + ':00+07:00') : null;
    if (!input || !Number.isFinite(a) || b !== null && (!Number.isFinite(b) || b <= a || !output) || !reason.trim() || reason.trim().length > 2000) { setError('ตรวจเวลา สถานที่ และเหตุผลให้ครบ เวลาออกต้องหลังเวลาเข้า'); return; }
    save({ ...initial, originalWorkDate: initial.dayId === null && !initial.originalWorkDate ? date : initial.originalWorkDate,
      proposal: { workDate: date, inUtc: new Date(a).toISOString(), outUtc: b === null ? null : new Date(b).toISOString(), inSite: input, outSite: b === null ? null : output!, reason: reason.trim() } });
  }}>
    <fieldset disabled={busy} className="min-w-0 space-y-4">
      <legend className="font-bold">ข้อมูลเวลาที่เสนอ</legend>
      <label className="block min-w-0">วันทำงานที่ขอแก้<input required type="date" className="auth-input w-full min-w-0 max-w-full" value={date} onChange={e => setDate(e.target.value)} /></label>
      <label className="block min-w-0">เวลาเข้า (ประเทศไทย)<input required type="datetime-local" className="auth-input w-full min-w-0 max-w-full" value={entered} onChange={e => setEntered(e.target.value)} /></label>
      <label className="block min-w-0">เวลาออก (ประเทศไทย)<input type="datetime-local" className="auth-input w-full min-w-0 max-w-full" value={exited} onChange={e => setExited(e.target.value)} /></label>
      <p className="text-sm">เว้นเวลาออกได้หากยังทำงานอยู่ เวลาทั้งหมดใช้ประเทศไทย (UTC+7) รองรับงานข้ามวัน</p>
      <label className="block">สถานที่เข้า<select required className="auth-input w-full" value={inSite} onChange={e => setInSite(e.target.value)}><option value="">เลือกสถานที่</option>{options.map(x => <option key={x.site.siteId} value={x.site.siteId}>{x.label}</option>)}</select></label>
      <label className="block">สถานที่ออก<select required={!!exited} className="auth-input w-full" value={outSite} onChange={e => setOutSite(e.target.value)}><option value="">เลือกสถานที่</option>{options.map(x => <option key={x.site.siteId} value={x.site.siteId}>{x.label}</option>)}</select></label>
      <label className="block">เหตุผลคำร้อง<textarea required maxLength={2000} className="auth-input w-full" value={reason} onChange={e => setReason(e.target.value)} /></label>
      {error && <p role="alert">{error}</p>}
      <button className="auth-button" type="submit">บันทึกร่าง</button>
    </fieldset>
  </form>;
}

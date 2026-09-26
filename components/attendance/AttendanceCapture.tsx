'use client';
/* eslint-disable @next/next/no-img-element -- Memory-only camera preview must never pass through an image optimizer. */
import { useState } from 'react';
import { useLiveCapture } from './useLiveCapture';
import { bangkokDate, thaiTime } from '@/lib/attendance/capture-view';
export default function AttendanceCapture() {
  const live = useLiveCapture(); const [siteId, setSiteId] = useState(''); const [continuous, setContinuous] = useState(false);
  const site = live.choices.find(x => x.site.siteId === siteId)?.site;
  const action = live.current?.openDay ? 'CheckOut' : 'CheckIn';
  const overnight = !!(live.current?.openDay && live.serverTime && live.current.openDay.workDate !== bangkokDate(live.serverTime));
  const canStart = !live.loading && !!site && !!live.current && (action === 'CheckOut' || !live.current.todayCompleted) && ['idle', 'error'].includes(live.phase);
  const locked = !['idle', 'error'].includes(live.phase);
  return <div className="space-y-5" onDrop={e => e.preventDefault()} onDragOver={e => e.preventDefault()}>
    <p>ถ่ายสดจากกล้องทั้งเข้าและออก พร้อม GPS ทุกตำแหน่ง ไม่มีการเลือกรูปหรือเก็บคิวออฟไลน์</p>
    {live.loading && <p role="status">กำลังตรวจสอบบัญชีและสถานะลงเวลา…</p>}
    {live.error && <p role="alert" className="text-red-700 dark:text-red-300">{live.error}</p>}
    {!live.loading && !live.current && <button className="auth-button" onClick={() => void live.reload()}>โหลดข้อมูลใหม่</button>}
    {live.current && <p>{live.current.openDay ? `มีรอบเปิดของวันที่ ${live.current.openDay.workDate} — ลงเวลาออก` : live.current.todayCompleted ? 'วันนี้ลงเวลาครบหนึ่งคู่แล้ว' : 'ยังไม่มีรอบเปิด — ลงเวลาเข้า'}</p>}
    {live.serverTime && <p>เวลาเซิร์ฟเวอร์ล่าสุด (ประเทศไทย): <time>{thaiTime(live.serverTime)}</time></p>}
    {!live.loading && live.current && live.choices.length === 0 && <p>ไม่มี Site ที่ได้รับสิทธิ์ลงเวลา กรุณาติดต่อผู้ดูแลการมอบหมายพื้นที่</p>}
    <label className="block">สถานที่ลงเวลา<select className="mt-2 block w-full rounded-xl border p-3 text-slate-950" value={siteId} disabled={locked || live.loading} onChange={e => { setSiteId(e.target.value); setContinuous(false); live.reset(); }}>
      <option value="">เลือก Site ที่ได้รับมอบหมาย</option>{live.choices.map(x => <option key={x.site.siteId} value={x.site.siteId}>{x.label}</option>)}
    </select></label>
    <button className="auth-button" disabled={!canStart} onClick={() => { setContinuous(false); if (site) void live.start(action, site); }}>เปิดกล้อง</button>
    <video ref={live.video} autoPlay muted playsInline aria-label="ภาพจากกล้องสด" className={`w-full max-h-[55vh] rounded-xl bg-black object-contain ${['permissions', 'ready'].includes(live.phase) ? '' : 'hidden'}`} />
    {live.phase === 'ready' && <button className="auth-button" onClick={() => void live.capture()}>ถ่ายภาพ</button>}
    {live.previewUrl && <img src={live.previewUrl} alt="ภาพรอยืนยัน ยังไม่ประทับเวลา" className="max-h-[55vh] w-full rounded-xl object-contain" />}
    {live.gps && <p role="status">ได้รับ GPS แล้ว · ความคลาดเคลื่อนประมาณ {Math.round(live.gps.accuracyMeters)} เมตร{live.gps.accuracyMeters > 100 ? ' — มากกว่า 100 เมตร แต่ยังลงเวลาได้' : ''}</p>}
    <p>ภาพตัวอย่างยังไม่ประทับเวลา เซิร์ฟเวอร์จะประทับเวลาไทยลงไฟล์เมื่อรับลงเวลาสำเร็จ ไม่ใช่เวลาชัตเตอร์ที่รับรองได้</p>
    {overnight && <label className="flex items-start gap-3"><input type="checkbox" checked={continuous} disabled={live.phase !== 'captured'} onChange={e => setContinuous(e.target.checked)} />ยืนยันว่าทำงานต่อเนื่องจากรอบเดิม</label>}
    <div className="flex flex-wrap gap-4">
      <button className="auth-button" disabled={live.phase !== 'captured' || (overnight && !continuous)} onClick={() => void live.submit(continuous)}>ยืนยันลงเวลา</button>
      {['ready', 'captured'].includes(live.phase) && <button className="rounded-xl border px-5 py-3" onClick={() => { setContinuous(false); live.reset(); }}>ถ่ายใหม่</button>}
      {live.phase === 'uncertain' && <button className="auth-button" disabled={live.loading} onClick={() => void live.checkOperation()}>ตรวจผลลงเวลา</button>}
    </div>
    {live.phase === 'submitting' && <p role="status">กำลังส่ง กรุณารอ ไม่ต้องกดซ้ำ…</p>}
    {live.phase === 'uncertain' && <p role="status">ยังไม่ทราบผล ห้ามเริ่มรายการใหม่จนกว่าจะตรวจผลคำขอเดิม</p>}
    {live.phase === 'success' && live.receipt && <div role="status"><p>ลงเวลาสำเร็จ: {thaiTime(live.receipt.occurredAtUtc)} · วันทำงาน {live.receipt.workDate}</p><button className="underline" onClick={() => { setContinuous(false); void live.reload(); }}>โหลดสถานะล่าสุด</button></div>}
    <p>หากลืมลงเวลา ต้องยื่นคำร้องผ่านหัวหน้าและ HR ใน Module 6D (ยังไม่เปิดใช้งาน) ไม่สามารถแก้เวลาย้อนหลังจากหน้านี้ และต้องปิดรอบเดิมก่อนเริ่มรอบใหม่</p>
  </div>;
}

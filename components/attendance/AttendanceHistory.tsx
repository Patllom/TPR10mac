'use client';
/* eslint-disable @next/next/no-img-element -- Private evidence must use the authorized endpoint, never an image optimizer. */
/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation discards private client state. */
import { useState } from 'react';
import { useAttendanceHistory } from './useAttendanceHistory';
import { attendanceDayLabel, type HistoryAudience } from '@/lib/attendance/history-view';
import { bangkokDate, thaiTime } from '@/lib/attendance/capture-view';
import type { AttendanceEventView } from '@/lib/attendance/recording-types';

function EventDetail({ event, label, audience }: { event: AttendanceEventView | null; label: string; audience: HistoryAudience }) {
  return <div className="min-w-0 space-y-2 rounded-xl border border-slate-300 p-4">
    <h4 className="font-semibold">{label}</h4>
    {!event ? <p>ยังไม่มีเวลาออก</p> : <>
      <time dateTime={event.occurredAtUtc}>{thaiTime(event.occurredAtUtc)}</time>
      <dl className="space-y-1 text-sm">
        <dt>รหัสสถานที่{label}</dt><dd className="break-all">{event.site.siteId}</dd>
        <dt>รหัสโครงการ</dt><dd className="break-all">{event.site.projectId}</dd>
        <dt>รหัสหน่วยงาน</dt><dd className="break-all">{event.site.workspaceId}</dd>
        <dt>พิกัด GPS</dt><dd>{event.gps.latitude}, {event.gps.longitude}</dd>
        <dt>ความคลาดเคลื่อน</dt><dd>{event.gps.accuracyMeters} เมตร</dd>
      </dl>
      {audience !== 'Team' && event.photo && <img src={`/api/v1/attendance/evidence/${event.photo.evidenceId}/thumbnail`} alt={`รูป${label} วันที่ ${thaiTime(event.occurredAtUtc)}`} className="max-h-64 w-full rounded-lg object-contain" />}
    </>}
  </div>;
}

export default function AttendanceHistory({ audience }: { audience: HistoryAudience }) {
  const [range, setRange] = useState(() => ({ from: bangkokDate(new Date(Date.now() - 30 * 86400000).toISOString()), to: bangkokDate(new Date().toISOString()) }));
  const [filter, setFilter] = useState(range);
  const [cursors, setCursors] = useState<(string | null)[]>([null]);
  const [formError, setFormError] = useState('');
  const params = new URLSearchParams({ from: filter.from, to: filter.to, pageSize: '20' });
  const cursor = cursors.at(-1); if (cursor) params.set('cursor', cursor);
  const history = useAttendanceHistory(audience, params.toString());
  const basis = { 1: 'ข้อมูลของตนเอง', 2: 'สายบังคับบัญชา', 3: 'HR ที่ได้รับมอบหมาย' };
  return <section id="history" aria-labelledby="history-title" className="min-w-0 space-y-5 border-t border-slate-300 pt-6">
    <h2 id="history-title" className="text-xl font-bold">ประวัติการลงเวลา</h2>
    <p>{audience === 'Mine' ? 'ประวัติของฉัน' : audience === 'Team' ? 'ประวัติลูกทีมตามสายบังคับบัญชา · ดูพิกัดได้' : 'ประวัติหน่วยงานที่ HR ได้รับมอบหมาย · ดูรูปและพิกัดได้'}</p>
    <nav aria-label="เลือกประวัติลงเวลา" className="flex flex-wrap gap-4 underline">
      <a href="/portal/attendance#history">ประวัติของฉัน</a>
      {history.canReadTeam && <a href="/portal/attendance/team">ประวัติลูกทีม</a>}
      {history.canReadHr && <a href="/portal/attendance/hr">ประวัติสำหรับ HR</a>}
    </nav>
    <form className="flex flex-wrap items-end gap-4" onSubmit={event => {
      event.preventDefault();
      const days = (Date.parse(range.to) - Date.parse(range.from)) / 86400000;
      if (!range.from || !range.to || !Number.isFinite(days) || days < 0 || days >= 31) { setFormError('เลือกช่วงวันที่เรียงจากเก่าไปใหม่ ไม่เกิน 31 วัน'); return; }
      setFormError(''); setCursors([null]); setFilter({ ...range }); history.reload();
    }}>
      <label className="block min-w-0">วันที่เริ่มต้น<input required type="date" value={range.from} onChange={e => setRange({ ...range, from: e.target.value })} className="auth-input max-w-full" /></label>
      <label className="block min-w-0">วันที่สิ้นสุด<input required type="date" value={range.to} onChange={e => setRange({ ...range, to: e.target.value })} className="auth-input max-w-full" /></label>
      <button type="submit" className="auth-button">ค้นหาประวัติ</button>
    </form>
    {formError && <p role="alert">{formError}</p>}
    <button type="button" className="underline" onClick={history.reload}>โหลดประวัติใหม่</button>
    {history.loading && <p role="status">กำลังตรวจสอบสิทธิ์และโหลดประวัติ…</p>}
    {history.error && <p role="alert" className="text-red-700 dark:text-red-300">{history.error}</p>}
    {history.data && <>
      {history.data.items.length === 0 && <p>ไม่พบประวัติในช่วงวันที่เลือก</p>}
      <ol className="space-y-5">{history.data.items.map(day => <li key={day.id} className="min-w-0 space-y-3 rounded-xl border border-slate-300 p-4">
        <h3 className="font-bold">{attendanceDayLabel(day.workDate)}</h3>
        {audience !== 'Mine' && <p className="break-all">รหัสพนักงาน: {day.employeeId}</p>}
        <p>สิทธิ์: {basis[day.readBasis]}</p>
        {day.out && bangkokDate(day.in.occurredAtUtc) !== bangkokDate(day.out.occurredAtUtc) && <p>ทำงานข้ามวัน</p>}
        <div className="grid min-w-0 gap-4 sm:grid-cols-2"><EventDetail event={day.in} label="เข้า" audience={audience} /><EventDetail event={day.out} label="ออก" audience={audience} /></div>
        <p className="text-sm">เวลาที่ใช้คำนวณ: เข้า {thaiTime(day.effectiveInUtc)} · ออก {day.effectiveOutUtc ? thaiTime(day.effectiveOutUtc) : 'ยังไม่มี'}</p>
      </li>)}</ol>
      <div className="flex flex-wrap gap-4">
        <button className="rounded-xl border px-4 py-2" disabled={cursors.length < 2} onClick={() => setCursors(x => x.slice(0, -1))}>ประวัติหน้าก่อนหน้า</button>
        <button className="rounded-xl border px-4 py-2" disabled={!history.data.nextCursor} onClick={() => { if (history.data?.nextCursor) setCursors(x => [...x, history.data!.nextCursor]); }}>ประวัติหน้าถัดไป</button>
      </div>
    </>}
    <p className="text-sm">เวลาแสดงตามประเทศไทย ประวัตินี้อ่านอย่างเดียว</p>
  </section>;
}

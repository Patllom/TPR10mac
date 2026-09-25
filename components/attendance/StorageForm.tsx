'use client';
import { useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { mutateStorage } from '@/lib/attendance/storage-client';
import { storageReady, type MigrationView, type StorageView } from '@/lib/attendance/storage-view';
import { scopeError } from '@/lib/scopes/scope-view';
import { useStorageQuery } from './useStorageQuery';

export default function StorageForm() {
  const [offset, setOffset] = useState(0), [alias, setAlias] = useState(''), [targetId, setTarget] = useState(''), [sourceId, setSource] = useState(''), [reason, setReason] = useState('');
  const [sourceOffset, setSourceOffset] = useState(0), [targetOffset, setTargetOffset] = useState(0);
  const [busy, setBusy] = useState(false), [invalidated, setInvalidated] = useState(false), [status, setStatus] = useState<number>(), [message, setMessage] = useState('');
  const [uncertain, setUncertain] = useState(false), [now, setNow] = useState(0);
  const generation = useRef(0), controller = useRef<AbortController | null>(null), locked = useRef(false), dom = useRef<HTMLDivElement>(null);
  const query = useStorageQuery(offset, sourceOffset, targetOffset);
  useEffect(() => {
    setNow(Date.now()); const timer = window.setInterval(() => setNow(Date.now()), 1000);
    const cancel = () => { generation.current++; controller.current?.abort(); };
    const clear = () => { generation.current++; controller.current?.abort(); if (dom.current) dom.current.style.display = 'none'; setInvalidated(true); setReason(''); setAlias(''); setTarget(''); setSource(''); setMessage(''); };
    const unsubscribe = subscribeAuthChanges(clear); window.addEventListener('pagehide', clear);
    return () => { cancel(); clearInterval(timer); unsubscribe(); window.removeEventListener('pagehide', clear); };
  }, []);
  const data = query.data;
  const failure = query.status ?? status;
  const hidden = !data || (status !== undefined && ![400, 409].includes(status));
  const locations = data?.locations.items ?? [], choices = data?.choices ?? [], target = choices.find(x => x.id === targetId), source = choices.find(x => x.id === sourceId);
  const validReason = !!reason.trim() && [...reason].length <= 500 && !/[\u0000-\u001f\u007f-\u009f]/u.test(reason);
  const disabled = !now || busy || hidden || !validReason;
  function reload() { setStatus(undefined); query.reload(); }
  async function save(action: 'register' | 'probe' | 'switch' | 'start' | 'resume', row?: StorageView | MigrationView) {
    if (disabled || locked.current || invalidated || !data) return;
    let body: unknown;
    if (action === 'register') { if (!data.options.items.some(x => x.alias === alias)) return; body = { alias, reason }; }
    else if (action === 'probe' || action === 'resume') { if (!row || action === 'resume' && (!('status' in row) || row.status !== 'Blocked')) return; body = { expectedVersion: row.version, reason }; }
    else if (action === 'switch') { if (!target || !storageReady(target, Date.now())) return; body = { storageId: target.id, expectedVersion: data['write-target'].version, reason }; }
    else { if (uncertain || !source || !target || source.id === target.id || source.id === data['write-target'].storageId || !storageReady(target, Date.now())) return; body = { requestId: crypto.randomUUID(), sourceId: source.id, targetId: target.id, expectedSourceVersion: source.version, expectedTargetVersion: target.version, reason }; }
    locked.current = true; setBusy(true); setStatus(undefined); setMessage('');
    const current = ++generation.current; controller.current = new AbortController();
    try {
      const response = await mutateStorage(action, body, controller.current.signal, row?.id);
      if (current !== generation.current) return;
      if (!response.ok) {
        setStatus(response.status);
        if (response.status === 401) window.location.replace('/login');
        if (response.status === 409) query.reload();
        if (response.status >= 500 && action === 'start') setUncertain(true);
        return;
      }
      setMessage('บันทึกสำเร็จ'); query.reload();
    } catch { if (current === generation.current) { setStatus(503); if (action === 'start') setUncertain(true); } }
    finally { if (current === generation.current) { locked.current = false; setBusy(false); } }
  }
  if (invalidated) return null;
  const healthLabel: Record<string, string> = { unknown: 'ยังไม่ทราบความพร้อม', unavailable: 'ไม่พร้อมใช้งาน', ready: 'พร้อมใช้งาน', warning: 'พร้อมแต่มีคำเตือน' };
  const jobLabel = { Pending: 'รอดำเนินการ', Running: 'กำลังย้าย', Blocked: 'ติดขัด', Completed: 'ตรวจครบแล้ว' };
  return <div ref={dom} className="space-y-5">
    <p>เปลี่ยนปลายทางเฉพาะรูปใหม่ รูปเก่ายังอยู่ที่เก่า ห้ามถอดหรือลบต้นทางจนกว่าจะย้ายและตรวจครบ ระบบไม่ลบรูปอัตโนมัติ</p>
    {failure && <p role="alert">{scopeError(failure)}{hidden && ' ข้อมูลถูกซ่อนไว้ กรุณาโหลดใหม่'}</p>}
    {message && <p role="status">{message}</p>}
    {uncertain && <p role="alert">ยังยืนยันผลเริ่มย้ายไม่ได้ โปรดตรวจรายการงานกับผู้ดูแลก่อนเริ่มงานใหม่ ไม่มีการส่งซ้ำอัตโนมัติ</p>}
    <button type="button" disabled={busy || !now} className="underline" onClick={reload}>โหลดข้อมูลใหม่</button>
    {!data && !query.status && <p role="status">กำลังโหลดข้อมูลที่เก็บรูป…</p>}
    <div style={{ display: hidden ? 'none' : undefined }} inert={hidden} className="space-y-5">
      <label className="block">เหตุผล<textarea aria-label="เหตุผล" className="auth-input" value={reason} disabled={busy} onChange={e => setReason(e.target.value)} /></label>
      <p>ต้องระบุเหตุผลไม่เกิน 500 ตัวอักษรและไม่มีอักขระควบคุม</p>
      <label className="block">ที่เก็บที่เตรียมไว้<select aria-label="ที่เก็บที่เตรียมไว้" className="auth-input" value={alias} disabled={busy} onChange={e => setAlias(e.target.value)}><option value="">เลือกชื่อที่เก็บ</option>{data?.options.items.map(x => <option key={x.alias} value={x.alias}>{x.alias} ({x.kind})</option>)}</select></label>
      <button className="auth-button" disabled={disabled || !alias} onClick={() => void save('register')}>ลงทะเบียนที่เก็บ</button>
      <p>ปลายทางปัจจุบัน: {locations.find(x => x.id === data?.['write-target'].storageId)?.alias ?? data?.['write-target'].storageId ?? 'ยังไม่กำหนด'}</p>
      <p>เวอร์ชันปลายทาง {data?.['write-target'].version}</p>
      <ul className="space-y-3">{locations.map(row => <li className="rounded-xl border p-4" key={row.id}>
        <p>{row.alias} · {row.kind} · เวอร์ชัน {row.version}</p><p>{healthLabel[row.health]}{!row.acceptWrites && ' · ปิดรับรูปใหม่'}</p>
        {row.checkedAtUtc && <p>ตรวจล่าสุด {row.checkedAtUtc}</p>}
        <button className="underline" disabled={disabled} onClick={() => void save('probe', row)}>ตรวจความพร้อม {row.alias}</button>
      </li>)}</ul>
      <label className="block">ปลายทางรูปใหม่<select aria-label="ปลายทางรูปใหม่" className="auth-input" value={targetId} disabled={busy} onChange={e => { setTarget(e.target.value); setTargetOffset(offset); }}><option value="">เลือกปลายทาง</option>{target && !locations.some(x => x.id === target.id) && <option value={target.id}>{target.alias} (เลือกจากหน้าอื่น)</option>}{locations.map(x => <option key={x.id} value={x.id}>{x.alias}</option>)}</select></label>
      <button className="auth-button" disabled={disabled || !target || !storageReady(target, now)} onClick={() => void save('switch')}>เปลี่ยนที่เก็บรูปใหม่</button>
      <label className="block">ต้นทางย้ายรูปเก่า<select aria-label="ต้นทางย้ายรูปเก่า" className="auth-input" value={sourceId} disabled={busy} onChange={e => { setSource(e.target.value); setSourceOffset(offset); }}><option value="">เลือกต้นทาง</option>{source && !locations.some(x => x.id === source.id) && <option value={source.id}>{source.alias} (เลือกจากหน้าอื่น)</option>}{locations.filter(x => x.id !== targetId && x.id !== data?.['write-target'].storageId).map(x => <option key={x.id} value={x.id}>{x.alias}</option>)}</select></label>
      <p>เริ่มย้ายจะปิดรับรูปใหม่ที่ต้นทาง ต้องเปลี่ยนปลายทางรูปใหม่ออกจากต้นทางก่อน</p>
      <button className="auth-button" disabled={disabled || uncertain || !source || !target || !storageReady(target, now)} onClick={() => void save('start')}>เริ่มย้ายรูปเก่า</button>
      <h2 className="text-xl font-semibold">งานย้ายรูป</h2>
      {data?.migrations.items.map(job => <article className="rounded-xl border p-4" key={job.id}><p>{jobLabel[job.status]} · {job.id}</p><p>ตรวจแล้ว {job.verified} / {job.total} · ติดขัด {job.blocked}</p>{job.status === 'Blocked' && <button className="underline" disabled={disabled} onClick={() => void save('resume', job)}>ดำเนินการย้ายต่อ</button>}</article>)}
      <h2 className="text-xl font-semibold">สุขภาพที่เก็บ</h2>
      {data?.health.items.map(x => <p key={x.storageId}>{locations.find(y => y.id === x.storageId)?.alias ?? x.storageId}: {healthLabel[x.status]} · พื้นที่ว่าง {x.freeBytes ?? 'ไม่ทราบ'} ไบต์ · ไฟล์หาย {x.missingObjects} · ไฟล์ไม่ผูกข้อมูล {x.orphanObjects}</p>)}
      <p>ตัวเลือกต้นทางและปลายทางที่เลือกไว้ยังคงอยู่เมื่อเปลี่ยนหน้า และจะตรวจข้อมูลใหม่พร้อมกันทุกครั้งที่โหลด</p>
      <div className="flex gap-5"><button disabled={busy || offset === 0} onClick={() => { setOffset(x => Math.max(0, x - 25)); setAlias(''); }}>หน้าก่อนหน้า</button><span>หน้าที่ {offset / 25 + 1}</span><button disabled={busy || !data || ![data.locations, data.options, data.health, data.migrations].some(x => x.hasMore)} onClick={() => { setOffset(x => x + 25); setAlias(''); }}>หน้าถัดไป</button></div>
    </div>
  </div>;
}

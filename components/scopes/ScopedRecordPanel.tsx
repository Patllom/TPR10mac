'use client';
import { useState } from 'react';
import { useAuthMutation } from '@/components/auth/useAuthMutation';
import { recordPath } from '@/lib/scopes/scope-path';
import type { Page, ScopeChoice } from '@/lib/scopes/scope-view';
import { useScopeQuery } from './useScopeQuery';
type Row = {id:string; note:string; restrictedNote?:string|null; version:number; createdAtUtc:string};
export default function ScopedRecordPanel({ choice }: { choice: ScopeChoice }) {
  const path=recordPath(choice.scope);
  const [page,setPage]=useState(1);
  const read=choice.capabilities.includes('scope-probe:read');
  const query=useScopeQuery<Page<Row>>(read?`${path}?page=${page}&pageSize=25`:null);
  const {run,busy,error}=useAuthMutation();
  const [note,setNote]=useState(''); const [restricted,setRestricted]=useState(''); const [writeRestricted,setWriteRestricted]=useState(false);
  const [editing,setEditing]=useState<Row|null>(null); const [status,setStatus]=useState('');
  const [from,setFrom]=useState(''); const [to,setTo]=useState('');
  const [exported,setExported]=useState<Row[]|null>(null);
  const canRestricted=choice.capabilities.includes('scope-probe:restricted-read');
  function clear() {setExported(null); setStatus(''); setEditing(null); setNote(''); setRestricted(''); setWriteRestricted(false);}
  return <div className="space-y-6">
    <p>ข้อมูลทดสอบเท่านั้น · {choice.workspaceName} / {choice.projectName} / {choice.siteName}</p>
    <button className="auth-button" disabled={query.pending||busy} onClick={()=>{clear();query.reload();}}>โหลดข้อมูลใหม่</button>
    {query.pending && <p>กำลังโหลดข้อมูล…</p>}{query.error && <p role="alert">{query.error}</p>}{error && <p role="alert">{error}</p>}{status && <p role="status">{status}</p>}
    {!read && <p>ไม่มีสิทธิ์อ่านรายการในพื้นที่นี้</p>}
    <ul className="space-y-4">{query.data?.items.map(row=><li key={row.id} className="rounded-xl border border-slate-300 p-4">
      <p>{row.note}</p>{Object.hasOwn(row,'restrictedNote') && <p>{row.restrictedNote ?? 'ไม่มีข้อความจำกัด'}</p>}
      <p className="my-2 text-sm">เวอร์ชัน {row.version} · {new Date(row.createdAtUtc).toLocaleString('th-TH',{timeZone:'Asia/Bangkok'})}</p>
      {choice.capabilities.includes('scope-probe:write') && <button className="underline" disabled={busy} onClick={()=>{setEditing(row);setNote(row.note);setRestricted(row.restrictedNote??'');setWriteRestricted(false);setStatus('');}}>แก้ไขรายการ</button>}
    </li>)}</ul>
    {query.data && <div className="flex gap-4"><button disabled={busy||page===1} onClick={()=>{clear();setPage(page-1);}}>หน้าก่อนหน้า</button><span>หน้า {page} · {query.data.total} รายการ</span><button disabled={busy||page*25>=query.data.total} onClick={()=>{clear();setPage(page+1);}}>หน้าถัดไป</button></div>}
    {choice.capabilities.includes('scope-probe:write') && <form onSubmit={event=>{event.preventDefault();setStatus('');setExported(null);const body={note,...(editing?{expectedVersion:editing.version}:{}),...(writeRestricted?{restrictedNote:restricted||null}:{})};void run(path+(editing?'/'+editing.id:''),body,()=>{clear();setStatus('บันทึกสำเร็จ');query.reload();},editing?'PATCH':'POST');}}>
      <fieldset disabled={busy} className="space-y-4"><legend className="font-semibold">{editing?'แก้ไขข้อมูลทดสอบ':'สร้างข้อมูลทดสอบ'}</legend>
        <label className="block">ข้อความ<input className="auth-input" value={note} maxLength={500} onChange={e=>setNote(e.target.value)}/></label>
        {canRestricted && <><label className="block"><input type="checkbox" checked={writeRestricted} onChange={e=>setWriteRestricted(e.target.checked)}/> เปลี่ยนข้อความจำกัด (ต้องมี MFA ล่าสุด)</label><label className="block">ข้อความจำกัด<input className="auth-input" disabled={!writeRestricted} value={restricted} maxLength={500} onChange={e=>setRestricted(e.target.value)}/></label><p>เลือกเปลี่ยนแล้วเว้นว่างเพื่อล้างค่า ไม่เลือกเพื่อคงค่าเดิม</p></>}
        <button className="auth-button" type="submit">{editing?'บันทึกการแก้ไข':'สร้างรายการ'}</button>{editing && <button type="button" className="ml-4 underline" onClick={clear}>ยกเลิกแก้ไข</button>}
      </fieldset>
    </form>}
    {choice.capabilities.includes('scope-probe:export') && <form onSubmit={event=>{event.preventDefault();setExported(null);setStatus('');void run(path+'/export-simulation',{createdFrom:from||null,createdTo:to||null},async response=>{const data=await response.json();setExported(data.items);setStatus(`ส่งออก ${data.rowCount} รายการ`);});}}>
      <fieldset disabled={busy} className="space-y-4"><legend className="font-semibold">ส่งออก JSON ทดสอบ สูงสุด 100 รายการ (ต้องมี MFA ล่าสุด)</legend>
        <label className="block">ตั้งแต่เวลา UTC<input className="auth-input" placeholder="2026-09-24T00:00:00Z" value={from} onChange={e=>setFrom(e.target.value)}/></label>
        <label className="block">ก่อนเวลา UTC<input className="auth-input" placeholder="2026-09-25T00:00:00Z" value={to} onChange={e=>setTo(e.target.value)}/></label>
        <button className="auth-button" type="submit">ส่งออกข้อมูลทดสอบ</button>
      </fieldset>
    </form>}
    {exported && <pre aria-label="ผลส่งออก" className="max-h-80 overflow-auto whitespace-pre-wrap break-all rounded-xl bg-slate-100 p-4 dark:bg-slate-950">{JSON.stringify(exported,null,2)}</pre>}
  </div>;
}

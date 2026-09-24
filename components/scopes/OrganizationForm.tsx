'use client';
import { useState } from 'react';
import { useAuthMutation } from '@/components/auth/useAuthMutation';
import { isUuid } from '@/lib/scopes/scope-path';
import { useScopeQuery } from './useScopeQuery';
type Row={id:string;workspaceId:string;projectId:string|null;code:string;name:string;isActive:boolean;version:number};
type Data={items:Row[];total:number;page:number;pageSize:number};
export default function OrganizationForm() {
  const [kind,setKind]=useState('workspaces'); const [workspace,setWorkspace]=useState(''); const [project,setProject]=useState(''); const [page,setPage]=useState(1);
  const [code,setCode]=useState(''); const [name,setName]=useState(''); const [reason,setReason]=useState(''); const [active,setActive]=useState(true); const [editing,setEditing]=useState<Row|null>(null); const [status,setStatus]=useState('');
  const {run,busy,error}=useAuthMutation();
  const base='/api/v1/organization/workspaces';
  const path=kind==='workspaces'?base:!isUuid(workspace)?null:kind==='sites'?(isUuid(project)?`${base}/${workspace}/projects/${project}/sites`:null):`${base}/${workspace}/${kind}`;
  const query=useScopeQuery<Data>(path?`${path}?page=${page}&pageSize=25`:null);
  function reset() {setEditing(null);setCode('');setName('');setReason('');setActive(true);setStatus('');}
  return <div className="space-y-6">
    <p>การปิดพื้นที่ถอน assignment และ session ที่เกี่ยวข้อง การเปิดกลับไม่คืนสิทธิ์เดิม ต้องยืนยัน MFA ล่าสุด</p>
    <fieldset disabled={busy} className="grid gap-4 sm:grid-cols-2">
      <label>ระดับโครงสร้าง<select className="auth-input" value={kind} onChange={e=>{reset();setPage(1);setKind(e.target.value);}}><option value="workspaces">Workspace</option><option value="projects">Project</option><option value="sites">Site</option><option value="departments">Department</option></select></label>
      {kind!=='workspaces'&&<label>Workspace UUID<input className="auth-input" value={workspace} onChange={e=>{reset();setPage(1);setWorkspace(e.target.value);}}/></label>}
      {kind==='sites'&&<label>Project UUID<input className="auth-input" value={project} onChange={e=>{reset();setPage(1);setProject(e.target.value);}}/></label>}
    </fieldset>
    <button className="auth-button" disabled={busy||query.pending||!path} onClick={()=>{reset();query.reload();}}>โหลดข้อมูลใหม่</button>
    {!path&&<p role="alert">กรุณาระบุ UUID ของพื้นที่แม่ให้ครบ</p>}{query.pending&&<p>กำลังโหลดโครงสร้าง…</p>}{query.error&&<p role="alert">{query.error}</p>}{error&&<p role="alert">{error}</p>}{status&&<p role="status">{status}</p>}
    <ul className="space-y-3">{query.data?.items.map(row=><li key={row.id} className="rounded-xl border border-slate-300 p-4">
      <p className="font-semibold">{row.code} · {row.name}</p><p>{row.isActive?'เปิดใช้งาน':'ปิดใช้งาน'} · เวอร์ชัน {row.version}</p><p className="break-all text-xs">{row.id}</p>
      <div className="mt-3 flex flex-wrap gap-4"><button disabled={busy} className="underline" onClick={()=>{reset();setEditing(row);setCode(row.code);setName(row.name);setActive(row.isActive);}}>แก้ไขโครงสร้าง</button>
        {kind==='workspaces'&&<button className="underline" disabled={busy} onClick={()=>{reset();setWorkspace(row.id);setKind('projects');setPage(1);}}>ดูโครงการ</button>}
        {kind==='projects'&&<button className="underline" disabled={busy} onClick={()=>{reset();setProject(row.id);setKind('sites');setPage(1);}}>ดูไซต์</button>}
      </div>
    </li>)}</ul>
    {query.data&&<div className="flex flex-wrap gap-4"><button disabled={busy||page===1} onClick={()=>{reset();setPage(page-1);}}>หน้าก่อนหน้า</button><span>หน้า {page} · {query.data.total} รายการ</span><button disabled={busy||page*25>=query.data.total} onClick={()=>{reset();setPage(page+1);}}>หน้าถัดไป</button></div>}
    <form onSubmit={event=>{event.preventDefault();if(!path)return;setStatus('');void run(path+(editing?'/'+editing.id:''),editing?{name,isActive:active,expectedVersion:editing.version,reason}:{code,name},()=>{reset();setStatus('บันทึกสำเร็จ');query.reload();},editing?'PATCH':'POST');}}>
      <fieldset disabled={busy||!path} className="space-y-4"><legend className="font-semibold">{editing?'แก้ไขโครงสร้าง':'เพิ่มโครงสร้าง'}</legend>
        <label className="block">รหัส<input className="auth-input" required disabled={!!editing} maxLength={64} pattern="[A-Za-z0-9_-]{1,64}" value={code} onChange={e=>setCode(e.target.value)}/></label>
        <label className="block">ชื่อ<input className="auth-input" required maxLength={200} value={name} onChange={e=>setName(e.target.value)}/></label>
        {editing&&<><label className="block"><input type="checkbox" checked={active} onChange={e=>setActive(e.target.checked)}/> เปิดใช้งาน</label><label className="block">เหตุผล<input className="auth-input" required maxLength={500} value={reason} onChange={e=>setReason(e.target.value)}/></label><p>ห้ามใส่ข้อมูลลับในเหตุผล</p></>}
        <button type="submit" className="auth-button">{editing?'บันทึกโครงสร้าง':'สร้างโครงสร้าง'}</button>{editing&&<button type="button" className="ml-4 underline" onClick={reset}>ยกเลิก</button>}
      </fieldset>
    </form>
  </div>;
}

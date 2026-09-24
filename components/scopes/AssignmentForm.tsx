'use client';
import { useState } from 'react';
import { useAuthMutation } from '@/components/auth/useAuthMutation';
import { isUuid } from '@/lib/scopes/scope-path';
import type { Page, ScopeKey } from '@/lib/scopes/scope-view';
import { useScopeQuery } from './useScopeQuery';
type User={id:string;username:string;isActive:boolean};
type Role={id:string;name:string;roleClass:string;businessCapabilities:string[]};
type Assignment={id:string;userId:string;scope:ScopeKey;roleId:string;version:number;revokedAtUtc:string|null};
export default function AssignmentForm({actorId}:{actorId:string}) {
  const base='/api/v1/scope-assignments';
  const [userPage,setUserPage]=useState(1); const [rolePage,setRolePage]=useState(1); const [page,setPage]=useState(1);
  const users=useScopeQuery<Page<User>>(`${base}/options/users?page=${userPage}&pageSize=100`);
  const roles=useScopeQuery<Page<Role>>(`${base}/options/roles?page=${rolePage}&pageSize=100`);
  const assignments=useScopeQuery<Page<Assignment>>(`${base}?page=${page}&pageSize=25`);
  const [user,setUser]=useState(''); const [role,setRole]=useState(''); const [workspace,setWorkspace]=useState(''); const [project,setProject]=useState(''); const [site,setSite]=useState(''); const [reason,setReason]=useState('');
  const [editing,setEditing]=useState<Assignment|null>(null); const [status,setStatus]=useState('');
  const {run,busy,error}=useAuthMutation();
  const scope={workspaceId:workspace,projectId:project||null,siteId:site||null};
  const valid=isUuid(workspace)&&(!project||isUuid(project))&&(!site||!!project&&isUuid(site));
  const unchanged=!!editing&&role===editing.roleId&&workspace.toLowerCase()===editing.scope.workspaceId.toLowerCase()&&project.toLowerCase()===(editing.scope.projectId??'').toLowerCase()&&site.toLowerCase()===(editing.scope.siteId??'').toLowerCase();
  const roleName=(id:string)=>roles.data?.items.find(item=>item.id===id)?.name??id;
  function reset() {setEditing(null);setUser('');setRole('');setReason('');setStatus('');}
  const reload=()=>{reset();users.reload();roles.reload();assignments.reload();};
  return <div className="space-y-6">
    <p>มอบหมายได้เฉพาะผู้อื่น ทุกการเปลี่ยนสิทธิ์จะยกเลิก session ของผู้ได้รับผล ห้ามใส่ข้อมูลลับในเหตุผล และต้องมี MFA ล่าสุด</p>
    <p>ใช้ Workspace/Project/Site UUID จากผู้ดูแลโครงสร้าง เว้น Project และ Site ว่างสำหรับระดับ Workspace; เว้น Site ว่างสำหรับระดับ Project</p>
    <button className="auth-button" disabled={busy} onClick={reload}>โหลดข้อมูลใหม่</button>
    {[users.error,roles.error,assignments.error,error].filter(Boolean).map((message,index)=><p role="alert" key={index}>{message}</p>)}{status&&<p role="status">{status}</p>}
    <form onSubmit={event=>{event.preventDefault();if(!valid||user===actorId||unchanged)return;setStatus('');void run(base+(editing?'/'+editing.id+'/replace':''),{...(editing?{expectedVersion:editing.version}:{userId:user}),scope,roleId:role,reason},()=>{reset();setStatus('เปลี่ยนสิทธิ์สำเร็จ และยกเลิก session ของผู้ได้รับผลแล้ว');assignments.reload();});}}>
      <fieldset disabled={busy||users.pending||roles.pending||!!users.error||!!roles.error} className="space-y-4"><legend className="font-semibold">{editing?'เปลี่ยนการมอบหมาย':'มอบหมายสิทธิ์'}</legend>
        <label className="block">ผู้ใช้<select aria-label="ผู้ใช้" className="auth-input" required value={user} disabled={!!editing} onChange={e=>setUser(e.target.value)}><option value="">เลือกผู้ใช้</option><option value={actorId} disabled>บัญชีของฉัน (ห้ามมอบหมายให้ตนเอง)</option>{editing&&!users.data?.items.some(x=>x.id===user)&&<option value={user}>{user}</option>}{users.data?.items.filter(x=>x.id!==actorId).map(x=><option key={x.id} value={x.id} disabled={!x.isActive}>{x.username}</option>)}</select></label>
        <div className="flex gap-4"><button type="button" disabled={userPage===1||!!editing} onClick={()=>{setUser('');setUserPage(userPage-1);}}>ผู้ใช้หน้าก่อน</button><button type="button" disabled={!!editing||!users.data||userPage*100>=users.data.total} onClick={()=>{setUser('');setUserPage(userPage+1);}}>ผู้ใช้หน้าถัดไป</button></div>
        <label className="block">Workspace UUID<input className="auth-input" required value={workspace} onChange={e=>setWorkspace(e.target.value)}/></label>
        <label className="block">Project UUID<input className="auth-input" value={project} onChange={e=>{setProject(e.target.value);setSite('');}}/></label>
        <label className="block">Site UUID<input className="auth-input" disabled={!project} value={site} onChange={e=>setSite(e.target.value)}/></label>
        <label className="block">บทบาท<select aria-label="บทบาท" className="auth-input" required value={role} onChange={e=>setRole(e.target.value)}><option value="">เลือกบทบาทธุรกิจ</option>{roles.data?.items.filter(x=>x.roleClass!=='system-administration').map(x=><option key={x.id} value={x.id}>{x.name}</option>)}</select></label>
        <div className="flex gap-4"><button type="button" disabled={rolePage===1} onClick={()=>{setRole('');setRolePage(rolePage-1);}}>บทบาทหน้าก่อน</button><button type="button" disabled={!roles.data||rolePage*100>=roles.data.total} onClick={()=>{setRole('');setRolePage(rolePage+1);}}>บทบาทหน้าถัดไป</button></div>
        <label className="block">เหตุผล<input className="auth-input" required maxLength={500} value={reason} onChange={e=>setReason(e.target.value)}/></label>
        <button type="submit" className="auth-button" disabled={!valid||!user||user===actorId||!role||unchanged}>{editing?'บันทึกการมอบหมายใหม่':'มอบหมายสิทธิ์'}</button>{unchanged&&<p>ยังไม่มีการเปลี่ยนพื้นที่หรือบทบาท จึงไม่เปลี่ยนสิทธิ์หรือยกเลิก session</p>}{editing&&<button type="button" className="ml-4 underline" onClick={reset}>ยกเลิก</button>}
      </fieldset>
    </form>
    <h2 className="text-xl font-semibold">รายการและประวัติการมอบหมาย</h2>
    <p>การถอนใช้เหตุผลในฟอร์มด้านบน ไม่คืนสิทธิ์เดิมเมื่อเปิดบัญชี/พื้นที่กลับ</p>
    <ul className="space-y-4">{assignments.data?.items.map(row=><li className="rounded-xl border border-slate-300 p-4" key={row.id}>
      <p className="break-all">ผู้ใช้ {users.data?.items.find(x=>x.id===row.userId)?.username??row.userId}</p><p className="break-all">พื้นที่ {row.scope.workspaceId} / {row.scope.projectId??'ระดับ Workspace'} / {row.scope.siteId??'ไม่มี Site'}</p><p>เวอร์ชัน {row.version} · {row.revokedAtUtc?'ถอนแล้ว':'ใช้งานอยู่'}</p>
      <p className="break-all">บทบาท {roleName(row.roleId)}</p><p className="break-all text-xs">รหัสบทบาท {row.roleId}</p>
      {!row.revokedAtUtc&&<div className="mt-3 flex gap-4"><button className="underline" disabled={busy||row.userId===actorId} onClick={()=>{reset();setEditing(row);setUser(row.userId);setWorkspace(row.scope.workspaceId);setProject(row.scope.projectId??'');setSite(row.scope.siteId??'');setRole(row.roleId);}}>เปลี่ยนการมอบหมาย</button>
        <button className="underline" aria-label={`ถอนสิทธิ์บทบาท ${roleName(row.roleId)} (${row.roleId})`} disabled={busy||row.userId===actorId||!reason.trim()} onClick={()=>{setStatus('');void run(`${base}/${row.id}/revoke`,{expectedVersion:row.version,reason},()=>{reset();setStatus('ถอนสิทธิ์และยกเลิก session สำเร็จ');assignments.reload();});}}>ถอนสิทธิ์</button></div>}
    </li>)}</ul>
    {assignments.data&&<div className="flex gap-4"><button disabled={busy||page===1} onClick={()=>setPage(page-1)}>หน้าก่อนหน้า</button><span>หน้า {page} · {assignments.data.total} รายการ</span><button disabled={busy||page*25>=assignments.data.total} onClick={()=>setPage(page+1)}>หน้าถัดไป</button></div>}
  </div>;
}

'use client';
import { useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { mutateDirectory } from '@/lib/attendance/directory-client';
import { type DirectoryResource, type DirectoryRow, type DirectoryOption, isCurrentRow } from '@/lib/attendance/directory-view';
import { scopeError } from '@/lib/scopes/scope-view';
import { useDirectoryQuery } from './useDirectoryQuery';

const labels: Record<DirectoryResource, string> = { memberships: 'ต้นสังกัด', 'reporting-lines': 'สายบังคับบัญชา', 'hr-assignments': 'ผู้รับผิดชอบ HR' };
export default function DirectoryForm({ actorId }: { actorId: string }) {
  const [resource, setResource] = useState<DirectoryResource>('memberships');
  return <><p>จัดการทะเบียนบุคลากรเท่านั้น ยังไม่ใช่หน้าลงเวลา รูปถ่ายหรือ GPS</p>
    <label className="block">ประเภทข้อมูล<select aria-label="ประเภทข้อมูล" className="auth-input" value={resource} onChange={e => setResource(e.target.value as DirectoryResource)}>
      {Object.entries(labels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
    </select></label><ResourceForm key={resource} resource={resource} actorId={actorId} /></>;
}
function ResourceForm({ resource, actorId }: { resource: DirectoryResource; actorId: string }) {
  const [page, setPage] = useState(1), [history, setHistory] = useState(false);
  const [userId, setUser] = useState(''), [workspaceId, setWorkspace] = useState(''), [departmentId, setDepartment] = useState('');
  const [supervisorUserId, setSupervisor] = useState(''), [reason, setReason] = useState('');
  const [editing, setEditing] = useState<DirectoryRow>();
  const [busy, setBusy] = useState(false), [ready, setReady] = useState(false), [invalidated, setInvalidated] = useState(false);
  const [status, setStatus] = useState<number>(), [message, setMessage] = useState('');
  const [recovering, setRecovering] = useState(false);
  const [usersPrefix, setUsersPrefix] = useState(''), [usersPage, setUsersPage] = useState(1);
  const [workspacePrefix, setWorkspacePrefix] = useState(''), [workspacePage, setWorkspacePage] = useState(1);
  const [departmentPrefix, setDepartmentPrefix] = useState(''), [departmentPage, setDepartmentPage] = useState(1);
  const generation = useRef(0), controller = useRef<AbortController | null>(null), locked = useRef(false);
  const dom = useRef<HTMLDivElement>(null);
  const rows = useDirectoryQuery(resource, new URLSearchParams({ page: String(page), pageSize: '25', includeEnded: String(history) }).toString());
  const users = useDirectoryQuery('options/users', new URLSearchParams({ prefix: usersPrefix, page: String(usersPage), pageSize: '25' }).toString());
  const workspaces = useDirectoryQuery('options/workspaces', resource === 'reporting-lines' ? null : new URLSearchParams({ prefix: workspacePrefix, page: String(workspacePage), pageSize: '25' }).toString());
  const departments = useDirectoryQuery('options/departments', !workspaceId || resource === 'reporting-lines' ? null : new URLSearchParams({ workspaceId, prefix: departmentPrefix, page: String(departmentPage), pageSize: '25' }).toString());
  useEffect(() => {
    setReady(true);
    const cancel = () => { generation.current++; controller.current?.abort(); };
    const clear = () => {
      cancel(); locked.current = false;
      if (dom.current) dom.current.style.display = 'none';
      setInvalidated(true); setUser(''); setWorkspace(''); setDepartment(''); setSupervisor(''); setReason(''); setEditing(undefined); setMessage('');
    };
    const unsubscribe = subscribeAuthChanges(clear); window.addEventListener('pagehide', clear);
    return () => { cancel(); unsubscribe(); window.removeEventListener('pagehide', clear); };
  }, []);
  const options = (query: typeof users) => query.data?.items.filter((x): x is DirectoryOption => 'label' in x) ?? [];
  const currentRows = rows.data?.items.filter((x): x is DirectoryRow => 'version' in x) ?? [];
  const failures = [rows.status, users.status, workspaces.status, departments.status, status];
  const privacyFailure = failures.find(value => value !== undefined && value !== 409 && value !== 400);
  const failure = privacyFailure ?? failures.find(value => value !== undefined);
  const pending = rows.pending || users.pending || workspaces.pending || departments.pending;
  const recovered = !pending && !!rows.data && !!users.data && (resource === 'reporting-lines' || (!!workspaces.data && (!workspaceId || !!departments.data)));
  const hidden = privacyFailure !== undefined || recovering;
  useEffect(() => { if (recovered && privacyFailure === undefined) setRecovering(false); }, [recovered, privacyFailure]);
  function reload() { setRecovering(true); setStatus(undefined); setEditing(undefined); rows.reload(); users.reload(); workspaces.reload(); departments.reload(); }
  function edit(row: DirectoryRow) {
    setEditing(row); setStatus(undefined); setMessage('');
    if ('employeeUserId' in row) { setUser(row.employeeUserId); setSupervisor(row.supervisorUserId); }
    else { setUser(row.userId); setWorkspace(row.workspaceId); setDepartment(row.departmentId); }
  }
  async function save(end?: DirectoryRow) {
    if (!ready || locked.current || invalidated || hidden || pending || status === 409 || !reason.trim() || [...reason].length > 500 || /[\u0000-\u001f\u007f-\u009f]/u.test(reason)) return;
    if (!end && (!userId || (resource === 'reporting-lines' ? !supervisorUserId : !workspaceId || !departmentId))) return;
    if (end && !isCurrentRow(end)) return;
    locked.current = true; setBusy(true); setStatus(undefined); setMessage('');
    const current = ++generation.current; controller.current = new AbortController();
    const body = end ? { expectedVersion: end.version, reason } : resource === 'reporting-lines'
      ? { employeeUserId: userId, supervisorUserId, expectedVersion: editing?.version ?? null, reason }
      : { userId, workspaceId, departmentId, ...(resource === 'memberships' ? { expectedVersion: editing?.version ?? null } : {}), reason };
    try {
      const response = await mutateDirectory(resource, body, controller.current.signal, end?.id);
      if (current !== generation.current) return;
      if (!response.ok) { setStatus(response.status); if (response.status === 401) window.location.replace('/login'); return; }
      setEditing(undefined); setMessage('บันทึกสำเร็จ — session ของผู้ได้รับผลอาจถูกยกเลิก'); rows.reload();
    } catch { if (current === generation.current) setStatus(503); }
    finally { if (current === generation.current) { locked.current = false; setBusy(false); } }
  }
  if (invalidated) return null;
  const chosen = (value: string, list: DirectoryOption[]) => value && !list.some(x => x.id === value) ? <option value={value}>รหัสที่เลือก {value}</option> : null;
  const pager = (name: string, query: typeof users, number: number, change: (page: number) => void) => <div className="flex flex-wrap gap-3">
    <button type="button" disabled={!ready || busy || query.pending || number <= 1} onClick={() => change(number - 1)}>{name}หน้าก่อนหน้า</button>
    <span>หน้า {number}</span><button type="button" disabled={!ready || busy || query.pending || !query.data || number * 25 >= query.data.total} onClick={() => change(number + 1)}>{name}หน้าถัดไป</button>
  </div>;
  return <div ref={dom} className="space-y-5">
    {failure && <p role="alert">{scopeError(failure)}{hidden && ' ข้อมูลถูกซ่อนไว้ ร่างยังอยู่จนกว่าจะเปลี่ยนบัญชี'}</p>}
    {message && <p role="status">{message}</p>}
    <button type="button" className="underline" disabled={busy || !ready} onClick={reload}>โหลดข้อมูลใหม่</button>
    <div style={{ display: hidden ? 'none' : undefined }} inert={hidden}>
      <form className="space-y-4" onSubmit={e => { e.preventDefault(); void save(); }}>
        <fieldset disabled={!ready || busy} className="space-y-4">
          <legend>{editing ? 'เปลี่ยนรายการปัจจุบัน (ประวัติเดิมคงอยู่)' : 'เพิ่มรายการใหม่'}</legend>
          <label className="block">ค้นหาผู้ใช้<input className="auth-input" value={usersPrefix} maxLength={100} onChange={e => { setUsersPrefix(e.target.value); setUsersPage(1); }} /></label>
          <label className="block">ผู้ใช้<select aria-label="ผู้ใช้" className="auth-input" required value={userId} disabled={!!editing || users.pending} onChange={e => setUser(e.target.value)}>
            <option value="">เลือกผู้ใช้</option>{chosen(userId, options(users))}{options(users).map(x => <option key={x.id} value={x.id}>{x.label}{x.id === actorId ? ' (บัญชีของฉัน)' : ''}</option>)}
          </select></label>{pager('ผู้ใช้', users, usersPage, setUsersPage)}
          {resource === 'reporting-lines' ? <label className="block">หัวหน้า<select aria-label="หัวหน้า" className="auth-input" required value={supervisorUserId} disabled={users.pending} onChange={e => setSupervisor(e.target.value)}>
            <option value="">เลือกหัวหน้า</option>{chosen(supervisorUserId, options(users))}{options(users).map(x => <option key={x.id} value={x.id}>{x.label}</option>)}
          </select></label> : <>
            <label className="block">ค้นหาหน่วยงาน<input className="auth-input" value={workspacePrefix} maxLength={100} onChange={e => { setWorkspacePrefix(e.target.value); setWorkspacePage(1); }} /></label>
            <label className="block">หน่วยงาน<select aria-label="หน่วยงาน" className="auth-input" required value={workspaceId} disabled={workspaces.pending} onChange={e => { setWorkspace(e.target.value); setDepartment(''); setDepartmentPage(1); }}>
              <option value="">เลือกหน่วยงาน</option>{chosen(workspaceId, options(workspaces))}{options(workspaces).map(x => <option key={x.id} value={x.id}>{x.label}</option>)}
            </select></label>{pager('หน่วยงาน', workspaces, workspacePage, setWorkspacePage)}
            <label className="block">ค้นหาแผนก<input className="auth-input" value={departmentPrefix} maxLength={100} onChange={e => { setDepartmentPrefix(e.target.value); setDepartmentPage(1); }} /></label>
            <label className="block">แผนก<select aria-label="แผนก" className="auth-input" required value={departmentId} disabled={!workspaceId || departments.pending} onChange={e => setDepartment(e.target.value)}>
              <option value="">เลือกแผนก</option>{chosen(departmentId, options(departments))}{options(departments).map(x => <option key={x.id} value={x.id}>{x.label}</option>)}
            </select></label>{pager('แผนก', departments, departmentPage, setDepartmentPage)}
          </>}
          <label className="block">เหตุผล<textarea aria-label="เหตุผล" className="auth-input" required value={reason} onChange={e => setReason(e.target.value)} /></label>
          <p>ไม่เกิน 500 ตัวอักษร ไม่มีอักขระควบคุม ต้องระบุเหตุผลเมื่อสิ้นสุดรายการด้วย</p>
          <button className="auth-button" disabled={pending || status === 409 || !reason.trim()} type="submit">{resource === 'memberships' ? 'บันทึกต้นสังกัด' : resource === 'reporting-lines' ? 'บันทึกสายบังคับบัญชา' : 'มอบหมาย HR'}</button>
          {editing && <button type="button" className="ml-4 underline" onClick={() => setEditing(undefined)}>ยกเลิกการเปลี่ยน</button>}
        </fieldset>
      </form>
      <label className="my-5 block"><input type="checkbox" checked={history} disabled={busy} onChange={e => { setHistory(e.target.checked); setPage(1); }} /> แสดงประวัติที่สิ้นสุดแล้ว (อ่านอย่างเดียว)</label>
      {rows.pending && <p>กำลังโหลดทะเบียน…</p>}
      <ul className="space-y-3">{currentRows.map(row => <li className="rounded-xl border p-4 break-all" key={row.id}>
        <p>ผู้ใช้ {'employeeUserId' in row ? row.employeeUserId : row.userId}</p>
        {'supervisorUserId' in row ? <p>หัวหน้า {row.supervisorUserId}</p> : <p>หน่วยงาน {row.workspaceId} · แผนก {row.departmentId}</p>}
        <p>{isCurrentRow(row) ? 'ใช้งานอยู่' : 'สิ้นสุดแล้ว'} · เวอร์ชัน {row.version}</p><p>เริ่ม {row.validFromUtc}{row.validToUtc && ` · สิ้นสุด ${row.validToUtc}`}</p>
        {isCurrentRow(row) && <div className="mt-3 flex flex-wrap gap-4">
          {resource !== 'hr-assignments' && <button type="button" disabled={!ready || busy || pending || status === 409} onClick={() => edit(row)}>เปลี่ยนรายการ</button>}
          <button type="button" disabled={!ready || busy || pending || status === 409 || !reason.trim()} onClick={() => void save(row)}>สิ้นสุดรายการ</button>
        </div>}
      </li>)}</ul>{pager('รายการ', rows, page, setPage)}
    </div>
  </div>;
}

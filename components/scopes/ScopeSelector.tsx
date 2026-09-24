import { scopePath } from '@/lib/scopes/scope-path';
import type { ScopePage } from '@/lib/scopes/scope-view';
export default function ScopeSelector({ data, technical }: { data: ScopePage; technical: boolean }) {
  return <>
    <p>สิทธิ์แต่ละพื้นที่แยกจากกัน พื้นที่แม่ไม่ครอบคลุมพื้นที่ลูก</p>
    {data.total === 0 && <p>ยังไม่ได้รับมอบหมายพื้นที่</p>}
    <ul className="grid gap-4 sm:grid-cols-2">{data.items.map(item => <li key={scopePath(item.scope)} className="rounded-2xl border border-slate-300 p-5">
      <p className="mb-2 text-sm text-orange-700 dark:text-orange-400">{item.scope.siteId ? 'ระดับ Site' : item.scope.projectId ? 'ระดับ Project' : 'ระดับ Workspace'}</p>
      {technical ? <a className="font-semibold underline" href={scopePath(item.scope)}>{item.siteName ?? item.projectName ?? item.workspaceName}</a> : <p className="font-semibold">{item.siteName ?? item.projectName ?? item.workspaceName}</p>}
      <p className="mt-2 text-sm">{[item.workspaceName,item.projectName,item.siteName].filter(Boolean).join(' / ')}</p>
      <p className="mt-2 break-all text-xs">สิทธิ์: {item.capabilities.join(', ') || 'ยังไม่มีสิทธิ์ข้อมูล'}</p>
    </li>)}</ul>
    {!technical && <p>พื้นที่ที่ได้รับมอบหมายพร้อมใช้กับโมดูลธุรกิจในลำดับถัดไป หน้าข้อมูลทดสอบไม่เปิดใน Production</p>}
    <nav aria-label="แบ่งหน้าพื้นที่" className="flex flex-wrap gap-5">
      {data.pageNumber > 1 && <a className="underline" href={`?page=${data.pageNumber-1}`}>หน้าก่อนหน้า</a>}
      <span>หน้า {data.pageNumber} · ทั้งหมด {data.total} พื้นที่</span>
      {data.pageNumber * data.pageSize < data.total && <a className="underline" href={`?page=${data.pageNumber+1}`}>หน้าถัดไป</a>}
    </nav>
  </>;
}

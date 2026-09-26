/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation intentionally discards scoped client state. */
import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { ServiceUnavailable } from '@/components/auth/AuthFrame';
import PrivateView from '@/components/auth/PrivateView';
import { readScopes } from '@/lib/scopes/server-scopes';

export default async function PortalPage() {
  let session;
  try { session = await requirePortalSession(); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ServiceUnavailable />; throw error; }
  let canRecord = false;
  try {
    for (let page = 1; ; page++) {
      const scopes = await readScopes(page);
      canRecord = scopes.items.some(x => x.scope.siteId && x.scope.projectId && x.capabilities.includes('attendance:record'));
      if (canRecord || page * scopes.pageSize >= scopes.total) break;
    }
  } catch { /* Fail closed; the existing portal remains usable during a scope outage. */ }
  return <PrivateView><section className="mx-auto w-full max-w-xl rounded-3xl border border-orange-500/30 p-6 sm:p-10">
    <p className="text-orange-600">TPR10 PORTAL</p><h1 className="my-5 text-3xl font-bold">ระบบปฏิบัติการภายใน</h1>
    <p>รหัสบัญชีที่เข้าสู่ระบบ</p><p data-testid="current-user" className="mt-2 break-all font-mono">{session.userId}</p>
    <p className="my-5">เลือกพื้นที่ที่ได้รับมอบหมายเพื่อเริ่มทำงาน</p>
    <nav className="my-5 flex flex-col gap-4 underline" aria-label="เมนูระบบ">
      <a href="/portal/scopes">เลือกพื้นที่ทำงาน</a>
      {canRecord && <a href="/portal/attendance">ลงเวลาเข้า–ออก</a>}
      {session.permissions.includes('organization:manage') && <a href="/portal/admin/organization">จัดการโครงสร้างองค์กร</a>}
      {session.permissions.includes('scope-assignments:manage') && <a href="/portal/admin/assignments">จัดการการมอบหมายสิทธิ์</a>}
      {session.permissions.includes('attendance:directory-manage') && <a href="/portal/admin/attendance-directory">จัดการบุคลากรและสายบังคับบัญชา</a>}
      {session.permissions.includes('attendance:storage-manage') && <a href="/portal/admin/attendance-storage">จัดการที่เก็บรูปลงเวลา</a>}
    </nav>
    <a className="underline" href="/portal/account">บัญชีของฉัน</a>
  </section></PrivateView>;
}

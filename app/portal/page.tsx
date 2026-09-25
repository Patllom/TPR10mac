/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation intentionally discards scoped client state. */
import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { ServiceUnavailable } from '@/components/auth/AuthFrame';
import PrivateView from '@/components/auth/PrivateView';

export default async function PortalPage() {
  let session;
  try { session = await requirePortalSession(); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ServiceUnavailable />; throw error; }
  return <PrivateView><section className="mx-auto w-full max-w-xl rounded-3xl border border-orange-500/30 p-6 sm:p-10">
    <p className="text-orange-600">TPR10 PORTAL</p><h1 className="my-5 text-3xl font-bold">ระบบปฏิบัติการภายใน</h1>
    <p>รหัสบัญชีที่เข้าสู่ระบบ</p><p data-testid="current-user" className="mt-2 break-all font-mono">{session.userId}</p>
    <p className="my-5">เลือกพื้นที่ที่ได้รับมอบหมายเพื่อเริ่มทำงาน</p>
    <nav className="my-5 flex flex-col gap-4 underline" aria-label="เมนูระบบ">
      <a href="/portal/scopes">เลือกพื้นที่ทำงาน</a>
      {session.permissions.includes('organization:manage') && <a href="/portal/admin/organization">จัดการโครงสร้างองค์กร</a>}
      {session.permissions.includes('scope-assignments:manage') && <a href="/portal/admin/assignments">จัดการการมอบหมายสิทธิ์</a>}
      {session.permissions.includes('attendance:directory-manage') && <a href="/portal/admin/attendance-directory">จัดการบุคลากรและสายบังคับบัญชา</a>}
    </nav>
    <a className="underline" href="/portal/account">บัญชีของฉัน</a>
  </section></PrivateView>;
}

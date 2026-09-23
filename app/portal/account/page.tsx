import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { ServiceUnavailable } from '@/components/auth/AuthFrame';
import LogoutButtons from '@/components/auth/LogoutButtons';
import PrivateView from '@/components/auth/PrivateView';

export default async function AccountPage() {
  let session;
  try { session = await requirePortalSession('/portal/account'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ServiceUnavailable />; throw error; }
  return <PrivateView><section className="mx-auto w-full max-w-xl space-y-5 rounded-3xl border border-orange-500/30 p-6 sm:p-10">
    <h1 className="text-2xl font-bold">บัญชีของฉัน</h1>
    <p data-testid="current-user" className="break-all font-mono">{session.userId}</p>
    <a className="block underline" href="/auth/change-password?returnTo=%2Fportal%2Faccount">เปลี่ยนรหัสผ่าน</a>
    {session.mfaVerifiedAtUtc ? <p>เปิดใช้งาน MFA แล้ว</p> : <a className="block underline" href="/auth/mfa?enroll=1&returnTo=%2Fportal%2Faccount">ตั้งค่า MFA เพิ่มความปลอดภัย</a>}
    <LogoutButtons all />
    <a className="block underline" href="/portal">กลับ Portal</a>
  </section></PrivateView>;
}

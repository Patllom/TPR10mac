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
    <p className="my-5">โมดูลธุรกิจจะเปิดใช้งานตามแผนพัฒนาลำดับถัดไป</p>
    <a className="underline" href="/portal/account">บัญชีของฉัน</a>
  </section></PrivateView>;
}

import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import PaymentQueue from '../PaymentQueue';
import Link from 'next/link';
export const dynamic = 'force-dynamic';
export default async function Page() {
  let session;
  try { session = await requirePortalSession('/portal/field-advances/payments'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="งานจ่ายเงินทดรอง"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }

  return <ScopeFrame title="งานจ่ายเงินทดรอง" session={session}>
    <div className="space-y-4">
      <Link className="underline" href="/portal/field-advances/settlements">งานตรวจเคลียร์และสะสางเหตุจ่ายผิด</Link>
      <PaymentQueue userId={session.userId} />
    </div>
  </ScopeFrame>;
}

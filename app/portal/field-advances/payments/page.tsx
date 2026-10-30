import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import PaymentQueue from '../PaymentQueue';
export const dynamic = 'force-dynamic';
export default async function Page() {
  let session;
  try { session = await requirePortalSession('/portal/field-advances/payments'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="งานจ่ายเงินทดรอง"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }

  return <ScopeFrame title="งานจ่ายเงินทดรอง" session={session}><PaymentQueue userId={session.userId} /></ScopeFrame>;
}

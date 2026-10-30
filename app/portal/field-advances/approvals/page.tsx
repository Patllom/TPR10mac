import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import RequestQueue from '../RequestQueue';
import CeilingAmendmentPanel from '../CeilingAmendmentPanel';
import { DispositionQueue } from '../PaymentDispositionPanel';
import { validId } from '@/lib/field-advances/policy-view';
export const dynamic = 'force-dynamic';
export default async function Page({ searchParams }: { searchParams: Promise<{ disposition?: string }> }) {
  const { disposition } = await searchParams;
  let session;
  try { session = await requirePortalSession('/portal/field-advances'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="งานรออนุมัติเงินทดรอง"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }

  return <ScopeFrame title="งานรออนุมัติเงินทดรอง" session={session}>{disposition && validId(disposition) ? <CeilingAmendmentPanel userId={session.userId} requestId={disposition} /> : <><RequestQueue userId={session.userId} review /><DispositionQueue userId={session.userId} /></>}</ScopeFrame>;
}

import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import RequestQueue from './RequestQueue';
export const dynamic = 'force-dynamic';
export default async function Page() {
  let session;
  try { session = await requirePortalSession('/portal/field-advances'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="คำขอเงินทดรองออกสนาม"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }

  return <ScopeFrame title="คำขอเงินทดรองออกสนาม" session={session}><RequestQueue userId={session.userId} /></ScopeFrame>;
}

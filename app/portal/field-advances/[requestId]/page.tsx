import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import RequestEditor from '../RequestEditor';
export const dynamic = 'force-dynamic';
export default async function Page({ params }: { params: Promise<{ requestId: string }> }) {
  let session;
  try { session = await requirePortalSession('/portal/field-advances'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="รายละเอียดคำขอเงินทดรอง"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  const { requestId: id } = await params;
  return <ScopeFrame title="รายละเอียดคำขอเงินทดรอง" session={session}><RequestEditor userId={session.userId} requestId={id} /></ScopeFrame>;
}

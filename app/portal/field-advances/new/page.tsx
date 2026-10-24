import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import RequestEditor from '../RequestEditor';
export const dynamic = 'force-dynamic';
export default async function Page({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  let session;
  try { session = await requirePortalSession('/portal/field-advances'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="จัดทำคำขอเงินทดรอง"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  const query = await searchParams;
  return <ScopeFrame title="จัดทำคำขอเงินทดรอง" session={session}><RequestEditor userId={session.userId} scope={{ workspaceId: typeof query.workspaceId === "string" ? query.workspaceId : "", projectId: typeof query.projectId === "string" ? query.projectId : null }} /></ScopeFrame>;
}

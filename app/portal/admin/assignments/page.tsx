import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import AssignmentForm from '@/components/scopes/AssignmentForm';
export const dynamic='force-dynamic';
export default async function AssignmentsPage() {
  let content, session;
  try {session=await requirePortalSession('/portal/admin/assignments');content=session.permissions.includes('scope-assignments:manage')?<AssignmentForm actorId={session.userId}/>:<p role="alert">ไม่มีสิทธิ์จัดการการมอบหมาย</p>;}
  catch(error) {if(error instanceof SessionUnavailableError)content=<p role="alert">{error.message}</p>;else throw error;}
  return <ScopeFrame title="จัดการการมอบหมายสิทธิ์" session={session}>{content}</ScopeFrame>;
}

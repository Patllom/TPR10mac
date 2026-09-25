import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import DirectoryForm from '@/components/attendance/DirectoryForm';
export const dynamic = 'force-dynamic';
export default async function AttendanceDirectoryPage() {
  let content, session;
  try {
    session = await requirePortalSession('/portal/admin/attendance-directory');
    content = session.permissions.includes('attendance:directory-manage') ? <DirectoryForm actorId={session.userId} /> : <p role="alert">ไม่มีสิทธิ์จัดการทะเบียนบุคลากร</p>;
  } catch (error) { if (error instanceof SessionUnavailableError) content = <p role="alert">{error.message}</p>; else throw error; }
  return <ScopeFrame title="จัดการบุคลากรและสายบังคับบัญชา" session={session}>{content}</ScopeFrame>;
}

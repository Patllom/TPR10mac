import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import AttendanceHistory from '@/components/attendance/AttendanceHistory';
export const dynamic = 'force-dynamic';
export default async function TeamAttendancePage() {
  let session;
  try { session = await requirePortalSession('/portal/attendance/team'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="ประวัติลูกทีม"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  return <ScopeFrame title="ประวัติลูกทีม" session={session}><AttendanceHistory audience="Team" /></ScopeFrame>;
}

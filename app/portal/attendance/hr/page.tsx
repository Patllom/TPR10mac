import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import AttendanceHistory from '@/components/attendance/AttendanceHistory';
export const dynamic = 'force-dynamic';
export default async function HrAttendancePage() {
  let session;
  try { session = await requirePortalSession('/portal/attendance/hr'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="ประวัติสำหรับ HR"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  return <ScopeFrame title="ประวัติสำหรับ HR" session={session}><AttendanceHistory audience="Hr" /></ScopeFrame>;
}

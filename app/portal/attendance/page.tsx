import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import AttendanceCapture from '@/components/attendance/AttendanceCapture';
export const dynamic = 'force-dynamic';
export default async function AttendancePage() {
  let session;
  try { session = await requirePortalSession('/portal/attendance'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="ลงเวลาเข้า–ออก"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  return <ScopeFrame title="ลงเวลาเข้า–ออก" session={session}><AttendanceCapture /></ScopeFrame>;
}

import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import CorrectionPortal from '@/components/attendance/CorrectionPortal';
export const dynamic = 'force-dynamic';
export default async function CorrectionsPage() {
  let session;
  try { session = await requirePortalSession('/portal/attendance/corrections'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="คำร้องแก้ไขเวลา"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  return <ScopeFrame title="คำร้องแก้ไขเวลา" session={session}><CorrectionPortal /></ScopeFrame>;
}

import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import ApprovalPortal from '@/components/attendance/ApprovalPortal';
export const dynamic = 'force-dynamic';
export default async function ApprovalsPage() {
  let session;
  try { session = await requirePortalSession('/portal/attendance/approvals'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="คิวพิจารณาคำร้อง"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  return <ScopeFrame title="คิวพิจารณาคำร้อง" session={session}><ApprovalPortal /></ScopeFrame>;
}

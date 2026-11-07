import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import SettlementReviewQueue from '../SettlementReviewQueue';

export const dynamic = 'force-dynamic';

export default async function Page() {
  let session;
  try {
    session = await requirePortalSession('/portal/field-advances/settlements');
  } catch (error) {
    if (error instanceof SessionUnavailableError) {
      return <ScopeFrame title="ตรวจเคลียร์ค่าใช้จ่ายและสะสาง"><p role="alert">{error.message}</p></ScopeFrame>;
    }
    throw error;
  }

  return <ScopeFrame title="ตรวจเคลียร์ค่าใช้จ่ายและสะสาง" session={session}>
    <SettlementReviewQueue userId={session.userId} />
  </ScopeFrame>;
}

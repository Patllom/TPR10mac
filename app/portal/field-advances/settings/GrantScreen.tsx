import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import GrantEditor from './GrantEditor';
export default async function GrantScreen({ searchParams, screen }: { searchParams: Promise<Record<string, string | string[] | undefined>>; screen: string }) {
  let session;
  try { session = await requirePortalSession('/portal/field-advances/settings/grants'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="มอบหมายสิทธิ์การเงิน"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  const params = await searchParams;
  return <ScopeFrame title="มอบหมายสิทธิ์การเงิน" session={session}><GrantEditor userId={session.userId}
    workspaceId={typeof params.workspaceId === 'string' ? params.workspaceId : null}
    projectId={typeof params.projectId === 'string' ? params.projectId : null} screen={screen} /></ScopeFrame>;
}

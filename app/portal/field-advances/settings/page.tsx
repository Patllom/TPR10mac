import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import PolicyEditor from './PolicyEditor';
export const dynamic = 'force-dynamic';
export default async function SettingsPage({ searchParams }: { searchParams: Promise<Record<string, string | string[] | undefined>> }) {
  let session;
  try { session = await requirePortalSession('/portal/field-advances/settings'); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ScopeFrame title="กติกาเงินทดรองออกสนาม"><p role="alert">{error.message}</p></ScopeFrame>; throw error; }
  const params = await searchParams;
  return <ScopeFrame title="กติกาเงินทดรองออกสนาม" session={session}><PolicyEditor userId={session.userId}
    workspaceId={typeof params.workspaceId === 'string' ? params.workspaceId : null}
    projectId={typeof params.projectId === 'string' ? params.projectId : null}
    screen={typeof params.view === 'string' ? params.view : 'settings'} /></ScopeFrame>;
}

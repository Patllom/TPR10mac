import { redirect } from 'next/navigation';
import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { ScopeHttpError } from '@/lib/scopes/scope-fetch';
import { readScopes, technicalScopeUiEnabled } from '@/lib/scopes/server-scopes';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import ScopeSelector from '@/components/scopes/ScopeSelector';
export const dynamic='force-dynamic';
export default async function ScopesPage({searchParams}:{searchParams:Promise<{page?:string}>}) {
  let content;
  try {
    await requirePortalSession('/portal/scopes');
    const raw=(await searchParams).page; const page=raw&&/^\d+$/.test(raw)?Number(raw):1;
    content=<ScopeSelector data={await readScopes(page)} technical={technicalScopeUiEnabled()}/>;
  } catch(error) {
    if(error instanceof ScopeHttpError && error.status===401) redirect('/login?returnTo=%2Fportal%2Fscopes');
    if(error instanceof ScopeHttpError || error instanceof SessionUnavailableError) content=<p role="alert">{error.message}</p>; else throw error;
  }
  return <ScopeFrame title="เลือกพื้นที่ทำงาน">{content}</ScopeFrame>;
}

import { notFound, redirect } from 'next/navigation';
import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { ScopeHttpError } from '@/lib/scopes/scope-fetch';
import { parseScopePath, scopePath } from '@/lib/scopes/scope-path';
import { readScopeChoice, technicalScopeUiEnabled } from '@/lib/scopes/server-scopes';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import ScopedRecordPanel from '@/components/scopes/ScopedRecordPanel';
export const dynamic='force-dynamic';
export default async function ScopedPage({params}:{params:Promise<{workspaceId:string;scopePath?:string[]}>}) {
  if(!technicalScopeUiEnabled()) notFound();
  const route=await params; let key;
  try {key=parseScopePath(route.workspaceId,route.scopePath);} catch {notFound();}
  let content, session;
  try {
    session=await requirePortalSession(scopePath(key));
    const choice=await readScopeChoice(key);
    content=choice?<ScopedRecordPanel key={scopePath(key)} choice={choice}/>:<p role="alert">พื้นที่หรือข้อมูลไม่พร้อมใช้งาน</p>;
  } catch(error) {
    if(error instanceof ScopeHttpError && error.status===401) redirect('/login');
    if(error instanceof ScopeHttpError || error instanceof SessionUnavailableError) content=<p role="alert">{error.message}</p>; else throw error;
  }
  return <ScopeFrame title="ข้อมูลทดสอบตามพื้นที่" session={session}>{content}</ScopeFrame>;
}

import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import OrganizationForm from '@/components/scopes/OrganizationForm';
export const dynamic='force-dynamic';
export default async function OrganizationPage() {
  let content;
  try {const session=await requirePortalSession('/portal/admin/organization');content=session.permissions.includes('organization:manage')?<OrganizationForm/>:<p role="alert">ไม่มีสิทธิ์จัดการโครงสร้างองค์กร</p>;}
  catch(error) {if(error instanceof SessionUnavailableError)content=<p role="alert">{error.message}</p>;else throw error;}
  return <ScopeFrame title="จัดการโครงสร้างองค์กร">{content}</ScopeFrame>;
}

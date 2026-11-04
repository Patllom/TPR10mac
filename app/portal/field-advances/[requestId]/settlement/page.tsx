import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import SettlementDetails from '../../SettlementDetails';
export const dynamic = 'force-dynamic';
export default async function Page({params}:{params:Promise<{requestId:string}>}){
 let session;
 try{session=await requirePortalSession('/portal/field-advances');}
 catch(error){if(error instanceof SessionUnavailableError)return <ScopeFrame title="เคลียร์ค่าใช้จ่ายและคืนเงิน"><p role="alert">{error.message}</p></ScopeFrame>;throw error;}
 const {requestId}=await params;
 return <ScopeFrame title="เคลียร์ค่าใช้จ่ายและคืนเงิน" session={session}><SettlementDetails requestId={requestId}/></ScopeFrame>;
}

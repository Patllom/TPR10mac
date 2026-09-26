import { requirePortalSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import ScopeFrame from '@/components/scopes/ScopeFrame';
import StorageForm from '@/components/attendance/StorageForm';
export const dynamic = 'force-dynamic';
export default async function AttendanceStoragePage() {
  let content, session;
  try {
    session = await requirePortalSession('/portal/admin/attendance-storage');
    content = session.permissions.includes('attendance:storage-manage') ? <StorageForm /> : <p role="alert">ไม่มีสิทธิ์จัดการที่เก็บรูป</p>;
  } catch (error) { if (error instanceof SessionUnavailableError) content = <p role="alert">{error.message}</p>; else throw error; }
  return <ScopeFrame title="จัดการที่เก็บรูปลงเวลา" session={session}>{content}</ScopeFrame>;
}

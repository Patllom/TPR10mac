import { redirect } from 'next/navigation';
import AuthFrame, { ServiceUnavailable } from '@/components/auth/AuthFrame';
import { readServerSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { sessionDestination } from '@/lib/auth/session-view';
import { safeReturnPath } from '@/lib/auth/safe-return-path';
import ChangePasswordForm from './ChangePasswordForm';

export const dynamic = 'force-dynamic';
export default async function ChangePasswordPage({ searchParams }: { searchParams: Promise<{ returnTo?: string }> }) {
  const target = safeReturnPath((await searchParams).returnTo ?? null);
  let session;
  try { session = await readServerSession(); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ServiceUnavailable />; throw error; }
  if (!session) redirect('/login?returnTo=' + encodeURIComponent(target));
  if (session.stage !== 'Active' && session.stage !== 'PasswordChangeRequired') redirect(sessionDestination(session, target));
  return <AuthFrame title="เปลี่ยนรหัสผ่าน"><ChangePasswordForm returnTo={target} /></AuthFrame>;
}

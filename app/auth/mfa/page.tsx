import { redirect } from 'next/navigation';
import AuthFrame, { ServiceUnavailable } from '@/components/auth/AuthFrame';
import { readServerSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { safeReturnPath } from '@/lib/auth/safe-return-path';
import MfaForm from './MfaForm';

export const dynamic = 'force-dynamic';
export default async function MfaPage({ searchParams }: { searchParams: Promise<{ returnTo?: string; enroll?: string }> }) {
  const params = await searchParams; const target = safeReturnPath(params.returnTo ?? null);
  let session;
  try { session = await readServerSession(); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ServiceUnavailable />; throw error; }
  if (!session) redirect('/login?returnTo=' + encodeURIComponent(target));
  if (session.stage === 'PasswordChangeRequired') redirect('/auth/change-password?returnTo=' + encodeURIComponent(target));
  if (session.stage === 'Active' && (params.enroll !== '1' || session.mfaVerifiedAtUtc)) redirect(target);
  return <AuthFrame title="ยืนยันตัวตนสองขั้นตอน"><MfaForm enrollment={session.stage !== 'MfaChallengeRequired'} returnTo={target} /></AuthFrame>;
}

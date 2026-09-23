import { redirect } from 'next/navigation';
import AuthFrame, { ServiceUnavailable } from '@/components/auth/AuthFrame';
import { readServerSession } from '@/lib/auth/server-session';
import { SessionUnavailableError } from '@/lib/auth/session-fetch';
import { sessionDestination } from '@/lib/auth/session-view';
import { safeReturnPath } from '@/lib/auth/safe-return-path';
import LoginForm from './LoginForm';

export const dynamic = 'force-dynamic';
export const metadata = { title: 'เข้าสู่ระบบ | TPR-10', robots: { index: false, follow: false } };
export default async function LoginPage({ searchParams }: { searchParams: Promise<{ returnTo?: string }> }) {
  const returnTo = safeReturnPath((await searchParams).returnTo ?? null);
  let session;
  try { session = await readServerSession(); }
  catch (error) { if (error instanceof SessionUnavailableError) return <ServiceUnavailable />; throw error; }
  if (session) redirect(sessionDestination(session, returnTo));
  return <AuthFrame title="เข้าสู่ระบบพนักงาน"><LoginForm returnTo={returnTo} /></AuthFrame>;
}

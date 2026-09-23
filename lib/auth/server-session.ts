import 'server-only';
import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import { fetchSession } from './session-fetch';
import { sessionDestination } from './session-view';
import { safeReturnPath } from './safe-return-path';

export async function readServerSession() {
  const token = (await cookies()).get('__Host-tpr10_session')?.value;
  const cookie = token && /^[A-Za-z0-9_-]{43}$/.test(token) ? '__Host-tpr10_session=' + token : '';
  return fetchSession(process.env.TPR10_API_ORIGIN, cookie);
}
export async function requirePortalSession(returnTo = '/portal') {
  const session = await readServerSession();
  if (!session) redirect('/login?returnTo=' + encodeURIComponent(safeReturnPath(returnTo)));
  if (session.stage !== 'Active') redirect(sessionDestination(session, returnTo));
  return session;
}

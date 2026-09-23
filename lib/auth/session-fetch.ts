import { isSessionView, type SessionView } from './session-view';

export class SessionUnavailableError extends Error {
  constructor() { super('บริการเข้าสู่ระบบไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง'); }
}
export async function fetchSession(origin: string | undefined, cookie: string, transport: typeof fetch = fetch): Promise<SessionView | null> {
  try {
    if (!origin) throw new SessionUnavailableError();
    const url = new URL(origin);
    if (!['http:', 'https:'].includes(url.protocol) || url.pathname !== '/' || url.username || url.password || url.search || url.hash)
      throw new SessionUnavailableError();
    const response = await transport(url.origin + '/api/v1/auth/session', {
      cache: 'no-store', redirect: 'error', headers: { Cookie: cookie }, signal: AbortSignal.timeout(5000)
    });
    if (response.status === 401) return null;
    if (!response.ok) throw new SessionUnavailableError();
    const session: unknown = await response.json();
    if (!isSessionView(session)) throw new SessionUnavailableError();
    return session;
  } catch { throw new SessionUnavailableError(); }
}

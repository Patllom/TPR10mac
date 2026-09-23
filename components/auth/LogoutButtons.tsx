'use client';
import { useAuthMutation } from './useAuthMutation';

export default function LogoutButtons({ all = false }: { all?: boolean }) {
  const { run, busy, error } = useAuthMutation();
  return <div className="mt-6 space-y-3">
    <button className="auth-button" disabled={busy} onClick={() => run('/api/v1/auth/logout', {}, () => { window.location.replace('/login'); })}>ออกจากระบบ</button>
    {all && <button className="block rounded-xl border border-orange-600 px-5 py-3 focus-visible:outline disabled:opacity-60" disabled={busy}
      onClick={() => run('/api/v1/auth/logout-all', {}, () => { window.location.replace('/login'); })}>ออกจากระบบทุกอุปกรณ์</button>}
    {error && <p role="alert">{error}</p>}
  </div>;
}

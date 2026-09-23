'use client';

import Link from 'next/link';
import { type FormEvent } from 'react';
import { useAuthMutation } from '@/components/auth/useAuthMutation';
import { isSessionView, sessionDestination } from '@/lib/auth/session-view';

export default function LoginForm({ returnTo }: { returnTo: string }) {
  const { run, busy, error } = useAuthMutation();
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    await run('/api/v1/auth/login', { username: data.get('username'), password: data.get('password') }, async response => {
      const session: unknown = await response.json();
      if (!isSessionView(session)) throw new Error();
      // Full navigation discards client router entries from another authentication state.
      window.location.replace(sessionDestination(session, returnTo));
    });
  }
  return <form onSubmit={submit} aria-busy={busy}><fieldset disabled={busy} className="space-y-5">
    <label className="block">ชื่อผู้ใช้<input name="username" autoComplete="username" required maxLength={128} className="auth-input" /></label>
    <label className="block">รหัสผ่าน<input name="password" type="password" autoComplete="current-password" required maxLength={256} className="auth-input" /></label>
    {error && <p role="alert" className="text-red-600 dark:text-red-300">{error}</p>}
    <button disabled={busy} className="auth-button" type="submit">{busy ? 'กำลังตรวจสอบ…' : 'เข้าสู่ระบบ'}</button>
    <Link href="/auth/reset" className="block underline">ลืมรหัสผ่าน</Link>
  </fieldset></form>;
}

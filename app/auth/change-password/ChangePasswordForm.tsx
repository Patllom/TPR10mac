'use client';
import { type FormEvent } from 'react';
import { useAuthMutation } from '@/components/auth/useAuthMutation';
import LogoutButtons from '@/components/auth/LogoutButtons';

export default function ChangePasswordForm({ returnTo }: { returnTo: string }) {
  const { run, busy, error } = useAuthMutation();
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const data = new FormData(event.currentTarget);
    void run('/api/v1/auth/password/change', { currentPassword: data.get('current'), newPassword: data.get('new') }, () => {
      window.location.replace('/login?returnTo=' + encodeURIComponent(returnTo));
    });
  }
  return <><p className="mb-5">ใช้รหัสผ่าน 15–128 ตัวอักษร หลังเปลี่ยนสำเร็จต้องเข้าสู่ระบบใหม่ทุกอุปกรณ์</p>
    <form onSubmit={submit} aria-busy={busy}><fieldset className="space-y-5" disabled={busy}>
      <label className="block">รหัสผ่านปัจจุบัน<input name="current" type="password" autoComplete="current-password" required className="auth-input" /></label>
      <label className="block">รหัสผ่านใหม่<input name="new" type="password" autoComplete="new-password" required minLength={15} maxLength={256} className="auth-input" /></label>
      {error && <p role="alert">{error}</p>}
      <button className="auth-button" disabled={busy}>เปลี่ยนรหัสผ่าน</button>
    </fieldset></form><LogoutButtons /></>;
}

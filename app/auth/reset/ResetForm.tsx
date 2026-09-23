'use client';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useAuthMutation } from '@/components/auth/useAuthMutation';

export default function ResetForm() {
  const initialized = useRef(false);
  const token = useRef<string | null>(null);
  const [complete, setComplete] = useState(false);
  const [ready, setReady] = useState(false);
  const [message, setMessage] = useState('');
  const { run, busy, error } = useAuthMutation();
  useEffect(() => {
    const capture = () => {
      const fragment = window.location.hash.slice(1);
      // Remove before any mutation; also handle another reset link opened in this document.
      window.history.replaceState(null, '', window.location.pathname);
      const value = new URLSearchParams(fragment).get('token');
      token.current = value && /^[A-Za-z0-9_-]{43}$/.test(value) ? value : null;
      setComplete(token.current !== null); setMessage(''); setReady(true);
    };
    if (!initialized.current) { initialized.current = true; capture(); }
    window.addEventListener('hashchange', capture);
    return () => window.removeEventListener('hashchange', capture);
  }, []);
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const form = event.currentTarget; const data = new FormData(form);
    const path = '/api/v1/auth/password-reset/' + (complete ? 'complete' : 'request');
    const body = complete ? { token: token.current, password: data.get('password') } : { username: data.get('username') };
    void run(path, body, () => {
      form.reset(); token.current = null;
      setMessage(complete ? 'ตั้งรหัสผ่านสำเร็จ กรุณาเข้าสู่ระบบใหม่' : 'หากบัญชีรองรับการกู้คืน ระบบจะดำเนินการตามช่องทางที่กำหนด');
    });
  }
  if (!ready) return <p role="status">กำลังเตรียมแบบฟอร์ม…</p>;
  return <div className="space-y-5">
    {!complete && <p>อีเมลกู้คืนสำหรับ production ยังไม่เปิดใช้งาน โปรดติดต่อผู้ดูแลเพื่อรับรหัสผ่านชั่วคราวผ่านช่องทางที่ยืนยันตัวตน</p>}
    {message ? <p role="status">{message}</p> : <form onSubmit={submit} aria-busy={busy}><fieldset className="space-y-5" disabled={busy}>
      {complete ? <label className="block">รหัสผ่านใหม่<input className="auth-input" name="password" type="password" autoComplete="new-password" required minLength={15} maxLength={256} /></label>
        : <label className="block">ชื่อผู้ใช้<input className="auth-input" name="username" autoComplete="username" required maxLength={128} /></label>}
      {error && <p role="alert">{error}</p>}
      <button className="auth-button" disabled={busy}>{complete ? 'ตั้งรหัสผ่านใหม่' : 'ขอกู้คืนรหัสผ่าน'}</button>
    </fieldset></form>}
    <a href="/login" className="block underline">กลับเข้าสู่ระบบ</a>
  </div>;
}

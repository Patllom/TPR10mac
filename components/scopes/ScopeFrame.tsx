'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation intentionally discards scoped client state. */
import { useEffect, useRef, useState } from 'react';
import { isSessionView, type SessionView } from '@/lib/auth/session-view';
import PrivateView from '@/components/auth/PrivateView';
import LogoutButtons from '@/components/auth/LogoutButtons';
export default function ScopeFrame({ title, children, session }: { title: string; children: React.ReactNode; session?: SessionView }) {
  const content = useRef<HTMLDivElement>(null);
  const [checked, setChecked] = useState(!session);
  const [error, setError] = useState('');
  useEffect(() => {
    if (!session) return;
    let generation = 0;
    let controller: AbortController | undefined;
    const hide = () => { if (content.current) content.current.style.visibility = 'hidden'; setChecked(false); };
    const fingerprint = (value: SessionView) => JSON.stringify([value.userId, value.stage, [...value.permissions].sort(), value.mfaVerifiedAtUtc]);
    const revalidate = async () => {
      hide(); setError(''); const current = ++generation;
      controller?.abort(); controller = new AbortController();
      try {
        const response = await fetch('/api/v1/auth/session', { cache: 'no-store', credentials: 'same-origin', redirect: 'error', signal: AbortSignal.any([controller.signal, AbortSignal.timeout(5000)]) });
        if (current !== generation) return;
        if (response.status === 401) { window.location.replace('/login'); return; }
        if (!response.ok) throw new Error('unavailable');
        const value: unknown = await response.json();
        if (current !== generation) return;
        if (!isSessionView(value)) throw new Error('invalid');
        if (fingerprint(value) !== fingerprint(session)) { window.location.reload(); return; }
        if (!document.hidden) { setChecked(true); if (content.current) content.current.style.visibility = 'visible'; }
      } catch { if (current === generation) setError('บริการตรวจสอบบัญชีไม่พร้อมใช้งาน ข้อมูลถูกซ่อนไว้ กรุณาลองใหม่'); }
    };
    const visibility = () => {
      if (document.hidden) { generation++; controller?.abort(); hide(); }
      else void revalidate();
    };
    const focus = () => { if (!document.hidden) void revalidate(); };
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('focus', focus);
    void revalidate();
    return () => { generation++; controller?.abort(); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('focus', focus); };
  }, [session]);
  return <PrivateView><noscript>ต้องเปิด JavaScript เพื่อตรวจสอบบัญชีก่อนแสดงพื้นที่ทำงาน</noscript>{error && <p role="alert">{error} <button onClick={() => window.dispatchEvent(new Event('focus'))} className="underline">ตรวจสอบอีกครั้ง</button></p>}<div ref={content} style={{ visibility: checked ? 'visible' : 'hidden' }} inert={!checked} className="mx-auto w-full max-w-5xl space-y-6 break-words">
    <header className="rounded-3xl border border-orange-500/30 bg-white p-6 shadow-sm dark:bg-slate-900">
      <p className="text-sm font-semibold text-orange-700 dark:text-orange-400">TPR10 · พื้นที่ทำงาน</p><h1 className="mt-3 text-2xl font-bold">{title}</h1>
      <nav aria-label="เมนูพื้นที่ทำงาน" className="mt-5 flex flex-wrap gap-5 underline"><a href="/portal">Portal</a><a href="/portal/scopes">เลือกพื้นที่</a><a href="/portal/account">บัญชีของฉัน</a></nav>
    </header>
    <section className="space-y-5 rounded-3xl border border-slate-300 bg-white p-5 sm:p-8 dark:bg-slate-900">{children}</section>
    <LogoutButtons />
  </div></PrivateView>;
}

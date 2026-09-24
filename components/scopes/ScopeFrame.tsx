'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation intentionally discards scoped client state. */
import { useEffect, useRef } from 'react';
import PrivateView from '@/components/auth/PrivateView';
import LogoutButtons from '@/components/auth/LogoutButtons';
export default function ScopeFrame({ title, children }: { title: string; children: React.ReactNode }) {
  const content = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const visibility = () => {
      if (document.hidden) { if (content.current) content.current.style.visibility = 'hidden'; }
      else window.location.reload(); // Revalidate account/scope after returning from another tab.
    };
    document.addEventListener('visibilitychange', visibility);
    return () => document.removeEventListener('visibilitychange', visibility);
  }, []);
  return <PrivateView><div ref={content} className="mx-auto w-full max-w-5xl space-y-6 break-words">
    <header className="rounded-3xl border border-orange-500/30 bg-white p-6 shadow-sm dark:bg-slate-900">
      <p className="text-sm font-semibold text-orange-700 dark:text-orange-400">TPR10 · พื้นที่ทำงาน</p><h1 className="mt-3 text-2xl font-bold">{title}</h1>
      <nav aria-label="เมนูพื้นที่ทำงาน" className="mt-5 flex flex-wrap gap-5 underline"><a href="/portal">Portal</a><a href="/portal/scopes">เลือกพื้นที่</a><a href="/portal/account">บัญชีของฉัน</a></nav>
    </header>
    <section className="space-y-5 rounded-3xl border border-slate-300 bg-white p-5 sm:p-8 dark:bg-slate-900">{children}</section>
    <LogoutButtons />
  </div></PrivateView>;
}

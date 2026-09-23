'use client';
import { useEffect, useRef } from 'react';

export default function PrivateView({ children }: { children: React.ReactNode }) {
  const content = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const hide = () => { if (content.current) content.current.style.visibility = 'hidden'; };
    const restore = (event: PageTransitionEvent) => { if (event.persisted) window.location.reload(); };
    window.addEventListener('pagehide', hide);
    window.addEventListener('pageshow', restore);
    return () => { window.removeEventListener('pagehide', hide); window.removeEventListener('pageshow', restore); };
  }, []);
  return <div ref={content} className="w-full">{children}</div>;
}

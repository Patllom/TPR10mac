'use client';
import { useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';

export default function PrivateView({ children }: { children: React.ReactNode }) {
  const content = useRef<HTMLDivElement>(null);
  const [invalidated, setInvalidated] = useState(false);
  useEffect(() => {
    const hide = () => { if (content.current) content.current.style.display = 'none'; };
    const restore = (event: PageTransitionEvent) => { if (event.persisted) window.location.reload(); };
    const unsubscribe = subscribeAuthChanges(() => { hide(); setInvalidated(true); window.location.reload(); });
    window.addEventListener('pagehide', hide);
    window.addEventListener('pageshow', restore);
    return () => { unsubscribe(); window.removeEventListener('pagehide', hide); window.removeEventListener('pageshow', restore); };
  }, []);
  return <div ref={content} className="w-full">{invalidated ? null : children}</div>;
}

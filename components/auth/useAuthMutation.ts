'use client';
import { useEffect, useRef, useState } from 'react';
import { authError, authMutation } from '@/lib/auth/auth-client';
import { scopeError } from '@/lib/scopes/scope-view';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';

export function useAuthMutation() {
  const lock = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [ready, setReady] = useState(false);
  const active = useRef(false);
  const generation = useRef(0);
  const controller = useRef<AbortController | null>(null);
  useEffect(() => {
    active.current = true; setReady(true);
    const invalidate = () => { generation.current++; controller.current?.abort(); lock.current = false; if (active.current) setBusy(false); };
    const unsubscribe = subscribeAuthChanges(invalidate);
    window.addEventListener('pagehide', invalidate);
    return () => { active.current = false; invalidate(); unsubscribe(); window.removeEventListener('pagehide', invalidate); };
  }, []);
  async function run(path: string, body: unknown, success: (response: Response, isCurrent: () => boolean) => void | Promise<void>, method: 'POST' | 'PATCH' = 'POST') {
    if (!ready || lock.current) return;
    lock.current = true; setBusy(true); setError('');
    const current = generation.current;
    const isCurrent = () => active.current && current === generation.current;
    controller.current = new AbortController();
    try {
      const response = await authMutation(path, body, method, controller.current.signal);
      if (!isCurrent()) return;
      if (!response.ok) {
        if (!path.startsWith('/api/v1/auth/') && response.status === 401) { window.location.replace('/login'); return; }
        setError(path.startsWith('/api/v1/auth/') ? authError(response.status) : scopeError(response.status)); return;
      }
      await success(response, isCurrent);
    } catch { if (isCurrent()) setError('บริการเข้าสู่ระบบไม่พร้อมใช้งาน กรุณาตรวจสถานะก่อนลองใหม่'); }
    finally { if (isCurrent()) { lock.current = false; controller.current = null; setBusy(false); } }
  }
  return { run, busy: !ready || busy, error };
}

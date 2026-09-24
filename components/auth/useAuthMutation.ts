'use client';
import { useEffect, useRef, useState } from 'react';
import { authError, authMutation } from '@/lib/auth/auth-client';
import { scopeError } from '@/lib/scopes/scope-view';

export function useAuthMutation() {
  const lock = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [ready, setReady] = useState(false);
  const active = useRef(false);
  useEffect(() => { active.current = true; setReady(true); return () => { active.current = false; }; }, []);
  async function run(path: string, body: unknown, success: (response: Response) => void | Promise<void>, method: 'POST' | 'PATCH' = 'POST') {
    if (!ready || lock.current) return;
    lock.current = true; setBusy(true); setError('');
    try {
      const response = await authMutation(path, body, method);
      if (!active.current) return;
      if (!response.ok) {
        if (!path.startsWith('/api/v1/auth/') && response.status === 401) { window.location.replace('/login'); return; }
        setError(path.startsWith('/api/v1/auth/') ? authError(response.status) : scopeError(response.status)); return;
      }
      await success(response);
    } catch { setError('บริการเข้าสู่ระบบไม่พร้อมใช้งาน กรุณาตรวจสถานะก่อนลองใหม่'); }
    finally { lock.current = false; setBusy(false); }
  }
  return { run, busy: !ready || busy, error };
}

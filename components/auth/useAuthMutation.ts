'use client';
import { useEffect, useRef, useState } from 'react';
import { authError, authMutation } from '@/lib/auth/auth-client';

export function useAuthMutation() {
  const lock = useRef(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [ready, setReady] = useState(false);
  useEffect(() => { setReady(true); }, []);
  async function run(path: string, body: unknown, success: (response: Response) => void | Promise<void>) {
    if (!ready || lock.current) return;
    lock.current = true; setBusy(true); setError('');
    try {
      const response = await authMutation(path, body);
      if (!response.ok) { setError(authError(response.status)); return; }
      await success(response);
    } catch { setError('บริการเข้าสู่ระบบไม่พร้อมใช้งาน กรุณาตรวจสถานะก่อนลองใหม่'); }
    finally { lock.current = false; setBusy(false); }
  }
  return { run, busy: !ready || busy, error };
}

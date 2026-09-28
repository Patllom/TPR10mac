'use client';
import { useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { isSessionView } from '@/lib/auth/session-view';
import { HistoryError, readAttendanceHistory } from '@/lib/attendance/history-client';
import type { HistoryAudience } from '@/lib/attendance/history-view';
import type { AttendancePage } from '@/lib/attendance/recording-types';

type HistoryState = { key: string; data?: AttendancePage; error?: string; loading: boolean; canReadTeam?: boolean; canReadHr?: boolean };
export function useAttendanceHistory(audience: HistoryAudience, query: string) {
  const generation = useRef(0);
  const refresh = useRef<() => void>(() => {});
  const key = audience + '?' + query;
  const [state, setState] = useState<HistoryState>({ key: '', loading: true });
  useEffect(() => {
    let disposed = false;
    let controller: AbortController | undefined;
    const cancel = () => { generation.current++; controller?.abort(); };
    const clear = () => { cancel(); setState({ key, loading: false }); };
    const load = async () => {
      clear();
      if (disposed || document.hidden) return;
      const current = generation.current;
      controller = new AbortController();
      const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(10000)]);
      const valid = () => !disposed && generation.current === current && !signal.aborted && !document.hidden;
      setState({ key, loading: true });
      const get = async (path: string): Promise<unknown> => {
        const response = await fetch(path, { credentials: 'same-origin', redirect: 'error', cache: 'no-store', signal });
        if (!response.ok) throw new HistoryError(response.status);
        return response.json();
      };
      try {
        const before = await get('/api/v1/auth/session');
        if (!isSessionView(before)) throw new HistoryError(503);
        if (before.stage !== 'Active') throw new HistoryError(403);
        const [data, access] = await Promise.all([
          readAttendanceHistory(audience, query, signal), get('/api/v1/attendance/access'),
        ]);
        // Recheck the account after a slow response as well as listening for auth events.
        const after = await get('/api/v1/auth/session');
        if (!valid()) return;
        if (!isSessionView(after)) throw new HistoryError(503);
        if (after.userId !== before.userId || after.stage !== 'Active') { clear(); window.location.reload(); return; }
        if (!access || typeof access !== 'object' || !('canReadTeam' in access) || typeof access.canReadTeam !== 'boolean' || !('canReadHr' in access) || typeof access.canReadHr !== 'boolean') throw new HistoryError(503);
        setState({ key, data, loading: false, canReadTeam: access.canReadTeam, canReadHr: access.canReadHr });
      } catch (error) {
        if (disposed || current !== generation.current || document.hidden) return;
        controller?.abort();
        const status = error instanceof HistoryError ? error.status : 503;
        const message = status === 403 || status === 404 ? 'ไม่มีสิทธิ์อ่านข้อมูลนี้ หรือสิทธิ์เปลี่ยนแปลง กรุณาตรวจสอบบัญชีและ MFA'
          : status === 400 ? 'ช่วงวันที่หรือหน้าประวัติไม่ถูกต้อง กรุณาค้นหาใหม่ (ไม่เกิน 31 วัน)'
            : status === 429 ? 'เรียกดูบ่อยเกินไป กรุณารอสักครู่แล้วลองใหม่'
              : status === 401 ? 'กรุณาเข้าสู่ระบบใหม่' : 'บริการประวัติไม่พร้อมใช้งาน กรุณาลองใหม่';
        setState({ key, loading: false, error: message });
        if (status === 401) window.location.replace('/login');
      }
    };
    const reload = () => { void load(); };
    const visibility = () => { if (document.hidden) clear(); else reload(); };
    const unsubscribe = subscribeAuthChanges(clear);
    refresh.current = reload;
    window.addEventListener('focus', reload);
    window.addEventListener('pagehide', clear);
    document.addEventListener('visibilitychange', visibility);
    reload();
    return () => {
      disposed = true; cancel(); unsubscribe();
      window.removeEventListener('focus', reload); window.removeEventListener('pagehide', clear);
      document.removeEventListener('visibilitychange', visibility);
    };
  }, [audience, query, key]);
  const visible = state.key === key ? state : { key, loading: true };
  return { ...visible, reload: () => refresh.current() };
}

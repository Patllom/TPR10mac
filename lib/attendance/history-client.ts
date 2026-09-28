import { parseAttendancePage, type HistoryAudience } from './history-view';

export class HistoryError extends Error {
  constructor(public status: number) { super(`Attendance history ${status}`); }
}

export async function readAttendanceHistory(audience: HistoryAudience, query: string, signal: AbortSignal) {
  if (!['Mine', 'Team', 'Hr'].includes(audience)) throw new Error('เส้นทางไม่ถูกต้อง');
  signal.throwIfAborted();
  const response = await fetch(`/api/v1/attendance/${audience.toLowerCase()}?${query}`, {
    cache: 'no-store', credentials: 'same-origin', redirect: 'error',
    signal: AbortSignal.any([signal, AbortSignal.timeout(10000)]),
  });
  if (!response.ok) throw new HistoryError(response.status);
  const body: unknown = await response.json();
  signal.throwIfAborted();
  return parseAttendancePage(body, audience);
}

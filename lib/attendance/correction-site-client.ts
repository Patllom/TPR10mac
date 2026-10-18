import type { CaptureChoice } from './capture-view';
import { correctionId } from './correction-view';

const exact = (value: unknown, keys: string[]): value is Record<string, unknown> => !!value && typeof value === 'object'
  && !Array.isArray(value) && Object.keys(value).length === keys.length && keys.every(k => Object.hasOwn(value, k));
const submillisecond = (value: string) => (value.match(/\.(\d+)(?:Z|[+-]\d{2}:\d{2})$/)?.[1] ?? '').padEnd(7, '0').slice(3);
export async function loadCorrectionSites(inUtc: string, outUtc: string | null, signal: AbortSignal): Promise<CaptureChoice[]> {
  const choices = new Map<string, CaptureChoice>();
  for (const instant of new Set([inUtc, outUtc].filter((x): x is string => !!x))) {
    if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/.test(instant) || !Number.isFinite(Date.parse(instant))) throw new Error('กรุณากรอกเวลาก่อนโหลดสถานที่');
    const at = instant;
    for (let page = 1; ; page++) {
      signal.throwIfAborted();
      const response = await fetch(`/api/v1/attendance/corrections/sites?at=${encodeURIComponent(at)}&page=${page}&pageSize=100`,
        { cache: 'no-store', credentials: 'same-origin', redirect: 'error', signal });
      if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) throw new Error('โหลดสถานที่ไม่สำเร็จ กรุณาตรวจสิทธิ์หรือเวลาแล้วลองใหม่');
      const body: unknown = await response.json(); signal.throwIfAborted();
      if (!exact(body, ['items', 'total', 'pageNumber', 'pageSize', 'atUtc']) || !Array.isArray(body.items)
          || body.pageNumber !== page || body.pageSize !== 100 || typeof body.atUtc !== 'string' || Date.parse(body.atUtc) !== Date.parse(at)
          || submillisecond(body.atUtc) !== submillisecond(at)
          || !Number.isSafeInteger(body.total) || (body.total as number) < 0 || (body.total as number) > 100000
          || body.items.length !== Math.min(100, Math.max(0, (body.total as number) - (page - 1) * 100))) throw new Error('ข้อมูลสถานที่ไม่ถูกต้อง');
      for (const row of body.items) {
        if (!exact(row, ['site', 'label']) || !exact(row.site, ['workspaceId', 'projectId', 'siteId'])
            || !Object.values(row.site).every(correctionId) || typeof row.label !== 'string' || !row.label.trim() || row.label.length > 2000) throw new Error('ข้อมูลสถานที่ไม่ถูกต้อง');
        const site = row.site as CaptureChoice['site']; choices.set(site.siteId, { site, label: row.label });
      }
      if (page * 100 >= (body.total as number)) break;
    }
  }
  return [...choices.values()];
}

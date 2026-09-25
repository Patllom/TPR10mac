import { isUuid } from './directory-view';
export type StorageView = { id: string; alias: string; kind: 'local-folder' | 'nas-mounted-folder'; version: number; acceptWrites: boolean; health: string; checkedAtUtc: string | null };
export type StorageTarget = { storageId: string | null; version: number };
export type StorageOption = Pick<StorageView, 'alias' | 'kind'>;
export type MigrationView = { id: string; status: 'Pending' | 'Running' | 'Blocked' | 'Completed'; version: number; total: number; verified: number; blocked: number };
export type StorageHealth = { storageId: string; status: string; freeBytes: number | null; totalBytes: number | null; missingObjects: number; orphanObjects: number; checkedAtUtc: string; errorCode: string | null };
export type StoragePage<T> = { items: T[]; offset: number; hasMore: boolean };
export type StorageData = { locations: StoragePage<StorageView>; options: StoragePage<StorageOption>; 'write-target': StorageTarget; migrations: StoragePage<MigrationView>; health: StoragePage<StorageHealth> };
const object = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const exact = (v: Record<string, unknown>, keys: string[]) => Object.keys(v).length === keys.length && keys.every(k => Object.hasOwn(v, k));
const integer = (v: unknown, min = 0): v is number => typeof v === 'number' && Number.isSafeInteger(v) && v >= min;
const date = (v: unknown): v is string => typeof v === 'string' && /^\d{4}-\d\d-\d\dT/.test(v) && /(?:Z|[+-]\d\d:\d\d)$/.test(v) && Number.isFinite(Date.parse(v));
const health = (v: unknown) => typeof v === 'string' && ['unknown', 'ready', 'warning', 'unavailable'].includes(v);
function row(v: unknown, resource: keyof StorageData): boolean {
  if (!object(v)) return false;
  if (resource === 'write-target') return exact(v, ['storageId', 'version']) && (v.storageId === null || isUuid(v.storageId)) && integer(v.version);
  if (resource === 'options' || resource === 'locations') {
    if (typeof v.alias !== 'string' || !v.alias || !['local-folder', 'nas-mounted-folder'].includes(String(v.kind))) return false;
    return resource === 'options' ? exact(v, ['alias', 'kind']) : exact(v, ['id', 'alias', 'kind', 'version', 'acceptWrites', 'health', 'checkedAtUtc']) && isUuid(v.id) && integer(v.version, 1) && typeof v.acceptWrites === 'boolean' && health(v.health) && (v.checkedAtUtc === null || date(v.checkedAtUtc));
  }
  if (resource === 'migrations') return exact(v, ['id', 'status', 'version', 'total', 'verified', 'blocked']) && isUuid(v.id) && ['Pending', 'Running', 'Blocked', 'Completed'].includes(String(v.status)) && integer(v.version, 1) && integer(v.total) && integer(v.verified) && integer(v.blocked) && v.verified + v.blocked <= v.total;
  return exact(v, ['storageId', 'status', 'freeBytes', 'totalBytes', 'missingObjects', 'orphanObjects', 'checkedAtUtc', 'errorCode']) && isUuid(v.storageId) && health(v.status) && [v.freeBytes, v.totalBytes].every(x => x === null || integer(x)) && integer(v.missingObjects) && integer(v.orphanObjects) && date(v.checkedAtUtc) && (v.errorCode === null || typeof v.errorCode === 'string');
}
export function parseStorage<K extends keyof StorageData>(resource: K, value: unknown): StorageData[K] {
  if (!['locations', 'options', 'write-target', 'migrations', 'health'].includes(resource)) throw new Error('เส้นทางไม่ถูกต้อง');
  const valid = resource === 'write-target' ? row(value, resource) : object(value) && exact(value, ['items', 'offset', 'hasMore']) && integer(value.offset) && typeof value.hasMore === 'boolean' && Array.isArray(value.items) && value.items.length <= 100 && value.items.every(x => row(x, resource));
  if (!valid) throw new Error('รูปแบบข้อมูลที่เก็บรูปไม่ถูกต้อง');
  return value as StorageData[K];
}
export function storageReady(value: StorageView, now = Date.now()) {
  const checked = value.checkedAtUtc ? Date.parse(value.checkedAtUtc) : NaN;
  return value.acceptWrites && ['ready', 'warning'].includes(value.health) && checked <= now && now - checked < 60000;
}

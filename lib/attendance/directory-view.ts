export type DirectoryResource = 'memberships' | 'reporting-lines' | 'hr-assignments';
export type TemporalRow = { id: string; validFromUtc: string; validToUtc: string | null; version: number };
export type MembershipRow = TemporalRow & { userId: string; workspaceId: string; departmentId: string };
export type ReportingRow = TemporalRow & { employeeUserId: string; supervisorUserId: string };
export type DirectoryRow = MembershipRow | ReportingRow;
export type DirectoryOption = { id: string; label: string };
export type DirectoryPage<T = DirectoryRow> = { items: T[]; page: number; pageSize: number; total: number };
export const isUuid = (v: unknown): v is string => typeof v === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(v);
const object = (v: unknown): v is Record<string, unknown> => !!v && typeof v === 'object' && !Array.isArray(v);
const exact = (v: Record<string, unknown>, keys: string[]) => Object.keys(v).length === keys.length && keys.every(k => Object.hasOwn(v, k));
const integer = (v: unknown, min: number): v is number => typeof v === 'number' && Number.isSafeInteger(v) && v >= min;
const date = (v: unknown): v is string => typeof v === 'string' && /^\d{4}-\d\d-\d\dT/.test(v) && /(?:Z|[+-]\d\d:\d\d)$/.test(v) && Number.isFinite(Date.parse(v));
export function isDirectoryPage(value: unknown, resource: DirectoryResource): value is DirectoryPage;
export function isDirectoryPage(value: unknown, resource: 'options'): value is DirectoryPage<DirectoryOption>;
export function isDirectoryPage(value: unknown, resource: DirectoryResource | 'options'): value is DirectoryPage | DirectoryPage<DirectoryOption> {
  if (!object(value) || !exact(value, ['items', 'page', 'pageSize', 'total']) || !integer(value.page, 1) || !integer(value.pageSize, 1) || value.pageSize > 100 || !integer(value.total, 0) || !Array.isArray(value.items) || value.items.length > value.pageSize || value.items.length > value.total) return false;
  return value.items.every((row: unknown) => {
    if (!object(row) || !isUuid(row.id)) return false;
    if (resource === 'options') return exact(row, ['id', 'label']) && typeof row.label === 'string';
    const keys = resource === 'reporting-lines' ? ['employeeUserId', 'supervisorUserId'] : ['userId', 'workspaceId', 'departmentId'];
    return exact(row, ['id', 'validFromUtc', 'validToUtc', 'version', ...keys]) && keys.every(k => isUuid(row[k])) && integer(row.version, 1)
      && date(row.validFromUtc) && (row.validToUtc === null || date(row.validToUtc) && Date.parse(row.validToUtc) > Date.parse(row.validFromUtc));
  });
}
export function isCurrentRow(row: TemporalRow, now = Date.now()) { return row.validToUtc === null && Date.parse(row.validFromUtc) <= now; }

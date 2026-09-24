import type { ScopeKey } from './scope-view';
export const uuidPattern = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
export function isUuid(value: unknown): value is string {
  return typeof value === 'string' && new RegExp(`^${uuidPattern}$`).test(value) && value !== '00000000-0000-0000-0000-000000000000';
}
export function scopePath(key: ScopeKey): string {
  if (!isUuid(key.workspaceId) || (key.projectId !== null && !isUuid(key.projectId))
    || (key.siteId !== null && (!isUuid(key.siteId) || key.projectId === null))) throw Error('พื้นที่ไม่ถูกต้อง');
  return `/portal/scopes/${key.workspaceId}` + (key.projectId ? `/projects/${key.projectId}` : '') + (key.siteId ? `/sites/${key.siteId}` : '');
}
export function parseScopePath(workspaceId: string, parts: string[] = []): ScopeKey {
  if (!(parts.length === 0 || parts.length === 2 && parts[0] === 'projects' || parts.length === 4 && parts[0] === 'projects' && parts[2] === 'sites')) throw Error('พื้นที่ไม่ถูกต้อง');
  const key = { workspaceId, projectId: parts[1] ?? null, siteId: parts[3] ?? null };
  scopePath(key); return key;
}
export function recordPath(key: ScopeKey): string { return scopePath(key).replace('/portal/scopes/', '/api/v1/workspaces/') + '/scope-probe-records'; }

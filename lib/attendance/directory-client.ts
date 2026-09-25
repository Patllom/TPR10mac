import { authMutation } from '../auth/auth-client';
import { type DirectoryResource, type DirectoryPage, type DirectoryOption, isDirectoryPage, isUuid } from './directory-view';
export class DirectoryError extends Error { constructor(public status: number) { super(`Directory ${status}`); } }
const root = '/api/v1/attendance/directory/';
export type DirectoryQuery = DirectoryResource | 'options/users' | 'options/workspaces' | 'options/departments';
export async function readDirectory(resource: DirectoryQuery, params: URLSearchParams, signal: AbortSignal): Promise<DirectoryPage | DirectoryPage<DirectoryOption>> {
  if (!['memberships', 'reporting-lines', 'hr-assignments', 'options/users', 'options/workspaces', 'options/departments'].includes(resource)) throw new Error('เส้นทางไม่ถูกต้อง');
  signal.throwIfAborted();
  const response = await fetch(root + resource + '?' + params, { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.any([signal, AbortSignal.timeout(10000)]) });
  if (!response.ok) throw new DirectoryError(response.status);
  const body: unknown = await response.json(); signal.throwIfAborted();
  if (resource.startsWith('options/')) { if (isDirectoryPage(body, 'options')) return body; }
  else if (isDirectoryPage(body, resource as DirectoryResource)) return body;
  throw new DirectoryError(503);
}
export function mutateDirectory(resource: DirectoryResource, body: unknown, signal: AbortSignal, endId?: string) {
  if (endId !== undefined && !isUuid(endId)) throw new Error('รหัสไม่ถูกต้อง');
  return authMutation(root + resource + (endId ? '/' + endId + '/end' : ''), body, 'POST', signal);
}

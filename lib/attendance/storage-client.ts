import { authMutation } from '../auth/auth-client';
import { isUuid } from './directory-view';
import { parseStorage, type StorageData } from './storage-view';
export class StorageError extends Error { constructor(public status: number) { super(`Storage ${status}`); } }
const root = '/api/v1/attendance/storage/';
export async function readStorage<K extends keyof StorageData>(resource: K, offset: number, signal: AbortSignal): Promise<StorageData[K]> {
  if (!['locations', 'options', 'write-target', 'migrations', 'health'].includes(resource) || !Number.isSafeInteger(offset) || offset < 0) throw new Error('เส้นทางไม่ถูกต้อง');
  signal.throwIfAborted();
  const response = await fetch(root + resource + (resource === 'write-target' ? '' : `?offset=${offset}&limit=25`), { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: AbortSignal.any([signal, AbortSignal.timeout(10000)]) });
  if (!response.ok) throw new StorageError(response.status);
  const body: unknown = await response.json(); signal.throwIfAborted();
  try { return parseStorage(resource, body); } catch { throw new StorageError(503); }
}
export function mutateStorage(action: 'register' | 'probe' | 'switch' | 'start' | 'resume', body: unknown, signal: AbortSignal, id?: string) {
  if (['probe', 'resume'].includes(action) && !isUuid(id)) throw new Error('รหัสไม่ถูกต้อง');
  const paths = { register: 'locations', probe: `locations/${id}/probe`, switch: 'write-target', start: 'migrations', resume: `migrations/${id}/resume` };
  if (!Object.hasOwn(paths, action)) throw new Error('เส้นทางไม่ถูกต้อง');
  return authMutation(root + paths[action], body, 'POST', signal);
}

import { publishAuthChange } from './auth-change';
const paths = new Set([
  '/api/v1/auth/login', '/api/v1/auth/logout', '/api/v1/auth/logout-all',
  '/api/v1/auth/password/change', '/api/v1/auth/password-reset/request', '/api/v1/auth/password-reset/complete',
  '/api/v1/auth/mfa/enroll', '/api/v1/auth/mfa/confirm', '/api/v1/auth/mfa/challenge', '/api/v1/auth/mfa/recover'
]);
const id = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
const organization = `/api/v1/organization/workspaces(?:/${id}/(?:departments|projects)|/${id}/projects/${id}/sites)?`;
const records = `/api/v1/workspaces/${id}(?:/projects/${id}(?:/sites/${id})?)?/scope-probe-records`;
export async function authMutation(path: string, body: unknown, method: 'POST' | 'PATCH' = 'POST', signal?: AbortSignal): Promise<Response> {
  const allowed = method === 'POST' && (paths.has(path) || new RegExp(`^(?:${organization}|${records}(?:/export-simulation)?|/api/v1/scope-assignments(?:/${id}/(?:replace|revoke))?)$`).test(path))
    || method === 'PATCH' && new RegExp(`^(?:${organization}|${records})/${id}$`).test(path);
  if (!allowed) throw new Error('เส้นทางคำขอไม่ถูกต้อง');
  signal?.throwIfAborted();
  const bounded = (milliseconds: number) => signal ? AbortSignal.any([signal, AbortSignal.timeout(milliseconds)]) : AbortSignal.timeout(milliseconds);
  const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
  const issued = await fetch('/api/v1/auth/csrf', { ...options, signal: bounded(10000) });
  if (!issued.ok) return issued;
  const token = (await issued.json()).token;
  signal?.throwIfAborted();
  if (typeof token !== 'string' || !token) throw new Error('บริการเข้าสู่ระบบไม่พร้อมใช้งาน');
  const response = await fetch(path, {
    ...options, method, headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token },
    body: JSON.stringify(body), signal: bounded(15000)
  });
  if (response.ok && paths.has(path) && path !== '/api/v1/auth/password-reset/request' && path !== '/api/v1/auth/mfa/enroll') publishAuthChange();
  return response;
}
export function authError(status: number): string {
  if (status === 429) return 'ทำรายการบ่อยเกินไป กรุณารอสักครู่แล้วลองใหม่';
  if (status === 401) return 'ข้อมูลยืนยันตัวตนไม่ถูกต้อง หรือ session หมดอายุ กรุณาเข้าสู่ระบบใหม่';
  if (status >= 500) return 'บริการเข้าสู่ระบบไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง';
  return 'ทำรายการไม่สำเร็จ กรุณาตรวจข้อมูลและลองใหม่';
}

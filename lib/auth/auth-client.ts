const paths = new Set([
  '/api/v1/auth/login', '/api/v1/auth/logout', '/api/v1/auth/logout-all',
  '/api/v1/auth/password/change', '/api/v1/auth/password-reset/request', '/api/v1/auth/password-reset/complete',
  '/api/v1/auth/mfa/enroll', '/api/v1/auth/mfa/confirm', '/api/v1/auth/mfa/challenge', '/api/v1/auth/mfa/recover'
]);
export async function authMutation(path: string, body: unknown): Promise<Response> {
  if (!paths.has(path)) throw new Error('เส้นทางคำขอไม่ถูกต้อง');
  const options = { credentials: 'same-origin', cache: 'no-store', redirect: 'error' } as const;
  const issued = await fetch('/api/v1/auth/csrf', { ...options, signal: AbortSignal.timeout(10000) });
  if (!issued.ok) return issued;
  const token = (await issued.json()).token;
  if (typeof token !== 'string' || !token) throw new Error('บริการเข้าสู่ระบบไม่พร้อมใช้งาน');
  return fetch(path, {
    ...options, method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': token },
    body: JSON.stringify(body), signal: AbortSignal.timeout(15000)
  });
}
export function authError(status: number): string {
  if (status === 429) return 'ทำรายการบ่อยเกินไป กรุณารอสักครู่แล้วลองใหม่';
  if (status === 401) return 'ข้อมูลยืนยันตัวตนไม่ถูกต้อง หรือ session หมดอายุ กรุณาเข้าสู่ระบบใหม่';
  if (status >= 500) return 'บริการเข้าสู่ระบบไม่พร้อมใช้งาน กรุณาลองใหม่ภายหลัง';
  return 'ทำรายการไม่สำเร็จ กรุณาตรวจข้อมูลและลองใหม่';
}

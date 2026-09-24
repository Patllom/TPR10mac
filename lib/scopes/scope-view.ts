export type ScopeKey = { workspaceId: string; projectId: string | null; siteId: string | null };
export type ScopeChoice = { scope: ScopeKey; workspaceName: string; projectName: string | null; siteName: string | null; capabilities: string[] };
export type Page<T> = { items: T[]; total: number; pageNumber: number; pageSize: number };
export type ScopePage = Page<ScopeChoice>;
export function scopeError(status: number): string {
  if (status === 401) return 'session หมดอายุ กรุณาเข้าสู่ระบบใหม่';
  if (status === 403) return 'ไม่มีสิทธิ์ หรือจำเป็นต้องยืนยัน MFA เพิ่มเติม';
  if (status === 404) return 'พื้นที่หรือข้อมูลไม่พร้อมใช้งาน';
  if (status === 409) return 'ข้อมูลเปลี่ยนแปลง กรุณาโหลดข้อมูลใหม่ก่อนทำรายการ';
  if (status === 429) return 'ทำรายการบ่อยเกินไป กรุณารอสักครู่';
  if (status >= 500) return 'บริการไม่พร้อมใช้งาน กรุณาตรวจสถานะก่อนลองใหม่';
  return 'ข้อมูลไม่ถูกต้อง กรุณาตรวจคำขอ (ส่งออกได้ไม่เกิน 100 รายการ)';
}

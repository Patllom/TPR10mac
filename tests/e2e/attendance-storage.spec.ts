import { test, expect, type Page } from '@playwright/test';
import { createHmac } from 'node:crypto';
const url = '/portal/admin/attendance-storage';
const root = '/api/v1/attendance/storage/';
const id = '11111111-1111-4111-8111-111111111111';
const targetId = '22222222-2222-4222-8222-222222222222';
const jobId = '33333333-3333-4333-8333-333333333333';
async function login(page: Page, user: string) {
  await page.goto('/login');
  await page.getByLabel('ชื่อผู้ใช้', { exact: true }).fill(user);
  await page.getByLabel('รหัสผ่าน', { exact: true }).fill('e2e-isolated-password-123');
  await page.getByRole('button', { name: 'เข้าสู่ระบบ', exact: true }).click();
  await page.getByRole('button', { name: 'เริ่มตั้งค่า MFA', exact: true }).click();
  const secret = await page.getByLabel('คีย์สำหรับกรอกเอง').inputValue();
  const bits = [...secret].map(c => 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(c).toString(2).padStart(5, '0')).join('');
  const key = Buffer.from(bits.match(/.{8}/g)!.map(x => parseInt(x, 2)));
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const digest = createHmac('sha1', key).update(counter).digest();
  await page.getByLabel('รหัสยืนยัน 6 หลัก').fill(((digest.readUInt32BE(digest[19] & 15) & 0x7fffffff) % 1000000).toString().padStart(6, '0'));
  await page.getByRole('button', { name: 'ยืนยัน MFA', exact: true }).click();
  await page.getByRole('button', { name: 'บันทึกรหัสกู้คืนแล้ว ไปต่อ' }).click();
  await expect(page.getByRole('heading', { name: 'ระบบปฏิบัติการภายใน', exact: true })).toBeVisible();
}

test('ผู้ไม่มี session ต้องเข้าสู่ระบบก่อน', async ({ page }) => {
  await page.goto('/portal/admin/attendance-storage');
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByLabel('พาธโฟลเดอร์')).toHaveCount(0);
});
test('admin ไม่มี capability ไม่ได้แบบฟอร์มหรือเมนู storage', async ({ page }) => {
  await login(page, 'e2e-admin');
  await expect(page.getByRole('link', { name: 'จัดการที่เก็บรูปลงเวลา' })).toHaveCount(0);
  await page.goto(url);
  await expect(page.locator('section').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
  await expect(page.getByLabel('เหตุผล')).toHaveCount(0);
});
test('storage UI ปิด unknown รองรับ stale version และซ่อนร่างเมื่อบริการหรือบัญชีเปลี่ยน', async ({ page }) => {
  test.setTimeout(60000);
  await login(page, 'e2e-other');
  await expect(page.getByRole('link', { name: 'จัดการที่เก็บรูปลงเวลา' })).toBeVisible();
  // Transport fixture exercises client/UI only; backend mutation security has integration tests.
  let ready = false, version = 1, unavailable = false, paginated = false;
  let hold = false, entered = false, release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  const requests: { path: string; body: Record<string, unknown> }[] = [];
  await page.route('**/api/v1/attendance/storage/**', async route => {
    const path = new URL(route.request().url()).pathname.slice(root.length);
    if (route.request().method() === 'POST') {
      requests.push({ path, body: route.request().postDataJSON() });
      if (path === 'write-target') { version = 2; return route.fulfill({ status: 409, json: {} }); }
      if (path.endsWith('/probe')) ready = true;
      return route.fulfill({ status: 200, json: {} });
    }
    if (unavailable) return route.fulfill({ status: 503, json: {} });
    const offset = Number(new URL(route.request().url()).searchParams.get('offset'));
    const ids = paginated ? offset === 0 ? [id] : [targetId] : [id, targetId];
    const locations = ids.map(value => ({ id: value, alias: value === targetId ? 'nas-test' : 'local-test', kind: value === targetId ? 'nas-mounted-folder' : 'local-folder', version: 3, acceptWrites: true, health: ready ? 'ready' : 'unknown', checkedAtUtc: ready ? new Date().toISOString() : null }));
    const items = path === 'locations' ? locations : path === 'options' ? [{ alias: 'new-test', kind: 'local-folder' }] : path === 'migrations' ? [{ id: jobId, status: 'Blocked', version: 4, total: 10, verified: 6, blocked: 1 }] : [];
    if (hold && path === 'locations') { entered = true; await held; }
    await route.fulfill({ json: path === 'write-target' ? { storageId: paginated ? null : id, version } : { items, offset, hasMore: paginated && path === 'locations' && offset === 0 } }).catch(() => {});
  });
  await page.goto(url);
  await expect(page.getByRole('heading', { name: 'จัดการที่เก็บรูปลงเวลา' })).toBeVisible();
  await expect(page.getByLabel('พาธโฟลเดอร์')).toHaveCount(0);
  await expect(page.locator('img')).toHaveCount(0);
  await page.getByLabel('ปลายทางรูปใหม่').selectOption(targetId);
  await page.getByLabel('เหตุผล', { exact: true }).fill('ตรวจการเปลี่ยนที่เก็บ');
  await expect(page.getByRole('button', { name: 'เปลี่ยนที่เก็บรูปใหม่', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'ตรวจความพร้อม nas-test', exact: true }).click();
  await expect(page.getByRole('button', { name: 'เปลี่ยนที่เก็บรูปใหม่', exact: true })).toBeEnabled();
  await page.getByRole('button', { name: 'เปลี่ยนที่เก็บรูปใหม่', exact: true }).click();
  await expect(page.locator('section').getByRole('alert')).toContainText('ข้อมูลเปลี่ยนแปลง');
  await expect(page.getByText('เวอร์ชันปลายทาง 2', { exact: true })).toBeVisible();
  expect(requests.find(x => x.path === 'write-target')?.body.expectedVersion).toBe(1);
  await expect(page.getByText('ตรวจแล้ว 6 / 10 · ติดขัด 1')).toBeVisible();
  await page.getByRole('button', { name: 'ดำเนินการย้ายต่อ', exact: true }).click();
  await expect.poll(() => requests.find(x => x.path.endsWith('/resume'))?.body.expectedVersion).toBe(4);
  unavailable = true;
  await page.getByRole('button', { name: 'โหลดข้อมูลใหม่', exact: true }).click();
  await expect(page.getByLabel('เหตุผล', { exact: true })).not.toBeVisible();
  await expect(page).toHaveURL(new RegExp(url));
  unavailable = false;
  await page.getByRole('button', { name: 'โหลดข้อมูลใหม่', exact: true }).click();
  await expect(page.getByLabel('เหตุผล', { exact: true })).toBeVisible();
  await expect(page.getByLabel('เหตุผล', { exact: true })).toHaveValue('ตรวจการเปลี่ยนที่เก็บ');
  paginated = true;
  await page.getByRole('button', { name: 'โหลดข้อมูลใหม่', exact: true }).click();
  await page.getByLabel('ต้นทางย้ายรูปเก่า').selectOption(id);
  await page.getByRole('button', { name: 'หน้าถัดไป', exact: true }).click();
  await expect(page.getByLabel('ต้นทางย้ายรูปเก่า')).toHaveValue(id);
  await page.getByLabel('ปลายทางรูปใหม่').selectOption(targetId);
  await page.getByRole('button', { name: 'เริ่มย้ายรูปเก่า', exact: true }).click();
  await expect.poll(() => requests.find(x => x.path === 'migrations')?.body.sourceId).toBe(id);
  expect(requests.find(x => x.path === 'migrations')?.body.targetId).toBe(targetId);
  hold = true;
  await page.getByRole('button', { name: 'โหลดข้อมูลใหม่', exact: true }).click();
  await expect.poll(() => entered).toBe(true);
  const other = await page.context().newPage();
  try {
    await other.goto('/portal/account'); await other.getByRole('button', { name: 'ออกจากระบบ', exact: true }).click();
    await expect(page).toHaveURL(/\/login(?:\?|$)/);
    await other.goto('/login');
    await other.getByLabel('ชื่อผู้ใช้', { exact: true }).fill('e2e-staff');
    await other.getByLabel('รหัสผ่าน', { exact: true }).fill('e2e-isolated-password-123');
    await other.getByRole('button', { name: 'เข้าสู่ระบบ', exact: true }).click();
    await expect(other.getByRole('heading', { name: 'ระบบปฏิบัติการภายใน', exact: true })).toBeVisible();
    release(); await page.goto(url);
    await expect(page.locator('section').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
    await expect(page.getByText('nas-test', { exact: false })).toHaveCount(0);
    await expect(page.getByLabel('เหตุผล')).toHaveCount(0);
  } finally { release(); await other.close(); }
});

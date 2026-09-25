import { test, expect, type Page } from '@playwright/test';
import { createHmac } from 'node:crypto';
const url = '/portal/admin/attendance-directory';
const root = '/api/v1/attendance/directory';
const workspaceId = '11111111-1111-4111-8111-111111111111';
const departmentA = '22222222-2222-4222-8222-222222222222';
const departmentB = '33333333-3333-4333-8333-333333333333';
function totp(secret: string) {
  const bits = [...secret].map(c => 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(c).toString(2).padStart(5, '0')).join('');
  const key = Buffer.from(bits.match(/.{8}/g)!.map(x => parseInt(x, 2)));
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const digest = createHmac('sha1', key).update(counter).digest();
  return ((digest.readUInt32BE(digest[19] & 15) & 0x7fffffff) % 1000000).toString().padStart(6, '0');
}
async function login(page: Page, user: string, mfa = false) {
  await page.goto('/login');
  await page.getByLabel('ชื่อผู้ใช้', { exact: true }).fill('e2e-directory-' + user);
  await page.getByLabel('รหัสผ่าน', { exact: true }).fill('e2e-isolated-password-123');
  await page.getByRole('button', { name: 'เข้าสู่ระบบ', exact: true }).click();
  if (mfa) {
    await page.getByRole('button', { name: 'เริ่มตั้งค่า MFA', exact: true }).click();
    const secret = await page.getByLabel('คีย์สำหรับกรอกเอง').inputValue();
    await page.getByLabel('รหัสยืนยัน 6 หลัก').fill(totp(secret));
    await page.getByRole('button', { name: 'ยืนยัน MFA', exact: true }).click();
    await page.getByRole('button', { name: 'บันทึกรหัสกู้คืนแล้ว ไปต่อ' }).click();
  }
  await expect(page.getByRole('heading', { name: 'ระบบปฏิบัติการภายใน', exact: true })).toBeVisible();
  await page.waitForLoadState('load');
}
async function post(page: Page, path: string, body: unknown) {
  return page.evaluate(async ({ path, body }) => {
    const csrf = await (await fetch('/api/v1/auth/csrf', { cache: 'no-store' })).json();
    return (await fetch(path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf.token }, body: JSON.stringify(body) })).status;
  }, { path, body });
}
test('ร่างยังซ่อนระหว่างรอ API ทะเบียนฟื้นตัวครบทุกคำขอ', async ({ page }) => {
  await login(page, 'recovery', true); await page.goto(url);
  await page.getByLabel('เหตุผล', { exact: true }).fill('ร่างระหว่างกู้คืน');
  await expect(page.getByLabel('เหตุผล', { exact: true })).toHaveValue('ร่างระหว่างกู้คืน');
  const pattern = '**/api/v1/attendance/directory/options/users?*';
  await page.route(pattern, route => route.fulfill({ status: 503, json: {} }));
  await page.getByLabel('ค้นหาผู้ใช้').fill('e2e-directory-');
  await expect(page.getByLabel('เหตุผล', { exact: true })).not.toBeVisible();
  await expect(page.getByLabel('เหตุผล', { exact: true })).toHaveValue('ร่างระหว่างกู้คืน');
  await page.unroute(pattern);
  let release!: () => void, entered!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  const requested = new Promise<void>(resolve => { entered = resolve; });
  await page.route(pattern, async route => { const response = await route.fetch(); entered(); await held; await route.fulfill({ response }); });
  try {
    await page.getByRole('button', { name: 'โหลดข้อมูลใหม่', exact: true }).click();
    await requested;
    await expect(page.getByLabel('เหตุผล', { exact: true })).not.toBeVisible();
  } finally { release(); }
  await expect(page.getByLabel('เหตุผล', { exact: true })).toBeVisible();
  await expect(page.getByLabel('เหตุผล', { exact: true })).toHaveValue('ร่างระหว่างกู้คืน');
});
test('ข้อผิดพลาด 400 เดิมต้องไม่บังการปฏิเสธสิทธิ์จากคำขอใหม่', async ({ page }) => {
  await login(page, 'masking', true); await page.goto(url);
  await page.getByLabel('ประเภทข้อมูล').selectOption('reporting-lines');
  await page.getByLabel('ค้นหาผู้ใช้').fill('e2e-directory-staff');
  const employee = page.getByLabel('ผู้ใช้', { exact: true });
  await expect(employee.locator('option')).toHaveCount(2);
  const id = await employee.locator('option').nth(1).getAttribute('value');
  await employee.selectOption(id!); await page.getByLabel('หัวหน้า', { exact: true }).selectOption(id!);
  await page.getByLabel('เหตุผล', { exact: true }).fill('ห้ามหัวหน้าเป็นคนเดียวกัน');
  const mutation = page.waitForResponse(r => r.url().endsWith(root + '/reporting-lines') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'บันทึกสายบังคับบัญชา' }).click();
  expect((await mutation).status()).toBe(400);
  await page.route('**/api/v1/attendance/directory/options/users?*', route => route.fulfill({ status: 403, json: {} }));
  await page.getByLabel('ค้นหาผู้ใช้').fill('e2e-directory-');
  await expect(page.getByLabel('เหตุผล', { exact: true })).not.toBeVisible();
});
test('พนักงานไม่ได้สิทธิ์จัดการและไม่มีแบบฟอร์ม', async ({ page }) => {
  await login(page, 'staff'); await page.goto(url);
  await expect(page.locator('section').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
  await expect(page.locator('form')).toHaveCount(0);
});
test('จัดการประวัติ ป้องกัน self elevation และ stale version ผ่าน API จริง', async ({ page, browser }) => {
  await login(page, 'operator', true); await page.goto(url);
  await expect(page.getByRole('heading', { name: 'จัดการบุคลากรและสายบังคับบัญชา' })).toBeVisible();
  const otherContext = await browser.newContext(); const other = await otherContext.newPage();
  try {
    await login(other, 'second', true);
    const users: { id: string; label: string }[] = await page.evaluate(async root => (await (await fetch(root + '/options/users?prefix=e2e-directory-')).json()).items, root);
    const user = (name: string) => users.find(x => x.label === 'e2e-directory-' + name)!.id;
    await page.getByLabel('ค้นหาผู้ใช้').fill('e2e-directory-');
    for (const name of ['employee', 'manager']) {
      await page.getByLabel('ผู้ใช้', { exact: true }).selectOption(user(name));
      await page.getByLabel('หน่วยงาน', { exact: true }).selectOption(workspaceId);
      await page.getByLabel('แผนก', { exact: true }).selectOption(departmentA);
      await page.getByLabel('เหตุผล', { exact: true }).fill('สร้างต้นสังกัดทดสอบ');
      await page.getByRole('button', { name: 'บันทึกต้นสังกัด', exact: true }).click();
      await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    }
    const employee = page.getByRole('listitem').filter({ hasText: user('employee') }).filter({ hasText: 'ใช้งานอยู่' });
    await employee.getByRole('button', { name: 'เปลี่ยนรายการ', exact: true }).click();
    expect(await post(other, root + '/memberships', { userId: user('employee'), workspaceId, departmentId: departmentB, expectedVersion: 1, reason: 'อีกผู้ดูแลแก้ไข' })).toBe(200);
    await page.getByLabel('แผนก', { exact: true }).selectOption(departmentB);
    await page.getByLabel('เหตุผล', { exact: true }).fill('version เก่าต้องไม่ทับ');
    await page.getByRole('button', { name: 'บันทึกต้นสังกัด', exact: true }).click();
    await expect(page.getByRole('alert').filter({ hasText: 'ข้อมูลเปลี่ยนแปลง' })).toBeVisible();
    await page.getByRole('button', { name: 'โหลดข้อมูลใหม่', exact: true }).click();
    await expect(employee).toContainText('เวอร์ชัน 3');
    await page.getByLabel('ประเภทข้อมูล').selectOption('reporting-lines');
    await page.getByLabel('ค้นหาผู้ใช้').fill('e2e-directory-');
    await page.getByLabel('ผู้ใช้', { exact: true }).selectOption(user('employee'));
    await page.getByLabel('หัวหน้า', { exact: true }).selectOption(user('manager'));
    await page.getByLabel('เหตุผล', { exact: true }).fill('กำหนดหัวหน้า');
    await page.getByRole('button', { name: 'บันทึกสายบังคับบัญชา' }).click();
    await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await page.getByLabel('เหตุผล', { exact: true }).fill('สิ้นสุดหัวหน้า');
    await page.getByRole('listitem').filter({ hasText: user('employee') }).getByRole('button', { name: 'สิ้นสุดรายการ', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await page.getByLabel('ประเภทข้อมูล').selectOption('hr-assignments');
    await page.getByLabel('ค้นหาผู้ใช้').fill('e2e-directory-');
    await page.getByLabel('ผู้ใช้', { exact: true }).selectOption(user('hr'));
    await page.getByLabel('หน่วยงาน', { exact: true }).selectOption(workspaceId);
    await page.getByLabel('แผนก', { exact: true }).selectOption(departmentA);
    await page.getByLabel('เหตุผล', { exact: true }).fill('มอบหมาย HR');
    await page.getByRole('button', { name: 'มอบหมาย HR', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    expect(await post(page, root + '/hr-assignments', { userId: user('operator'), workspaceId, departmentId: departmentA, reason: 'ห้ามเพิ่มสิทธิ์เอง' })).toBe(403);
    await page.getByLabel('เหตุผล', { exact: true }).fill('สิ้นสุด HR');
    await page.getByRole('listitem').filter({ hasText: user('hr') }).getByRole('button', { name: 'สิ้นสุดรายการ', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
  } finally { await otherContext.close().catch(() => {}); }
});
test('ตรวจบัญชีไม่ได้ต้องซ่อนร่าง แบ่งหน้าได้ และ logout/back ไม่เปิดข้อมูลเก่า', async ({ page, browser }) => {
    await login(page, 'privacy', true); await page.goto(url);
    await expect(page.getByRole('heading', { name: 'จัดการบุคลากรและสายบังคับบัญชา' })).toBeVisible();
    const noJs = await browser.newContext({ javaScriptEnabled: false, storageState: await page.context().storageState() });
    try {
      const staticPage = await noJs.newPage(); await staticPage.goto(url);
      await expect(staticPage.getByRole('button', { name: 'บันทึกต้นสังกัด', exact: true, includeHidden: true })).toBeDisabled();
    } finally { await noJs.close(); }
    await page.getByLabel('เหตุผล', { exact: true }).fill('ร่างที่ต้องเก็บไว้');
    await page.route('**/api/v1/auth/session', route => route.fulfill({ status: 503, json: {} }));
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await expect(page.getByRole('alert').filter({ hasText: 'ข้อมูลถูกซ่อนไว้' })).toBeVisible();
    await expect(page.getByLabel('เหตุผล', { exact: true })).not.toBeVisible();
    await page.unroute('**/api/v1/auth/session'); await page.getByRole('button', { name: 'ตรวจสอบอีกครั้ง' }).click();
    await expect(page.getByLabel('เหตุผล', { exact: true })).toBeVisible();
    await expect(page.getByLabel('เหตุผล', { exact: true })).toHaveValue('ร่างที่ต้องเก็บไว้');
    await page.getByLabel('ค้นหาผู้ใช้').fill('pagination-');
    await expect(page.getByLabel('ผู้ใช้', { exact: true }).locator('option').filter({ hasText: /^pagination-/ })).toHaveCount(25);
    const firstPage = await page.getByLabel('ผู้ใช้', { exact: true }).locator('option').allTextContents();
    await page.getByRole('button', { name: 'ผู้ใช้หน้าถัดไป', exact: true }).click();
    await expect.poll(async () => { const values = (await page.getByLabel('ผู้ใช้', { exact: true }).locator('option').allTextContents()).filter(x => x.startsWith('pagination-')); return values.length === 25 && values.every(x => !firstPage.includes(x)); }).toBe(true);
    await page.getByRole('button', { name: 'ออกจากระบบ', exact: true }).click();
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByLabel('ชื่อผู้ใช้', { exact: true })).toBeEnabled();
    await page.waitForLoadState('load');
    await page.goBack({ waitUntil: 'domcontentloaded' });
    await expect(page.getByRole('heading', { name: 'เข้าสู่ระบบพนักงาน', exact: true })).toBeVisible();
    await expect(page.getByLabel('ชื่อผู้ใช้', { exact: true })).toBeEnabled();
    await expect(page.getByLabel('เหตุผล', { exact: true })).not.toBeVisible();
    await login(page, 'staff'); await page.goto(url); await expect(page.locator('form')).toHaveCount(0);
});
test('เปลี่ยนหัวหน้าและสิ้นสุดต้นสังกัด เก็บประวัติอ่านอย่างเดียว', async ({ page }) => {
  await login(page, 'history', true); await page.goto(url);
  const users: { id: string; label: string }[] = await page.evaluate(async root => (await (await fetch(root + '/options/users?prefix=e2e-directory-')).json()).items, root);
  const user = (name: string) => users.find(x => x.label === 'e2e-directory-' + name)!.id;
  for (const name of ['employee-history', 'manager-history', 'manager-history-two'])
    expect(await post(page, root + '/memberships', { userId: user(name), workspaceId, departmentId: departmentA, expectedVersion: null, reason: 'เตรียมประวัติทดสอบ' })).toBe(201);
  expect(await post(page, root + '/reporting-lines', { employeeUserId: user('employee-history'), supervisorUserId: user('manager-history'), expectedVersion: null, reason: 'หัวหน้าเดิม' })).toBe(201);
  await page.getByLabel('ประเภทข้อมูล').selectOption('reporting-lines');
  await page.getByLabel('ค้นหาผู้ใช้').fill('e2e-directory-');
  const active = page.getByRole('listitem').filter({ hasText: user('employee-history') }).filter({ hasText: 'ใช้งานอยู่' });
  await active.getByRole('button', { name: 'เปลี่ยนรายการ', exact: true }).click();
  await page.getByLabel('หัวหน้า', { exact: true }).selectOption(user('manager-history-two'));
  await page.getByLabel('เหตุผล', { exact: true }).fill('เปลี่ยนหัวหน้า');
  await page.getByRole('button', { name: 'บันทึกสายบังคับบัญชา' }).click();
  await expect(active).toContainText('เวอร์ชัน 3');
  await page.getByLabel('แสดงประวัติที่สิ้นสุดแล้ว (อ่านอย่างเดียว)').check();
  const ended = page.getByRole('listitem').filter({ hasText: user('employee-history') }).filter({ hasText: 'สิ้นสุดแล้ว' });
  await expect(ended).toContainText('เวอร์ชัน 2'); await expect(ended.getByRole('button')).toHaveCount(0);
  await page.getByLabel('เหตุผล', { exact: true }).fill('สิ้นสุดหัวหน้า');
  await active.getByRole('button', { name: 'สิ้นสุดรายการ', exact: true }).click(); await expect(active).toHaveCount(0);
  await page.getByLabel('ประเภทข้อมูล').selectOption('memberships');
  await page.getByLabel('เหตุผล', { exact: true }).fill('สิ้นสุดต้นสังกัด');
  await active.getByRole('button', { name: 'สิ้นสุดรายการ', exact: true }).click(); await expect(active).toHaveCount(0);
  await page.getByLabel('แสดงประวัติที่สิ้นสุดแล้ว (อ่านอย่างเดียว)').check();
  await expect(ended).toContainText('เวอร์ชัน 2'); await expect(ended.getByRole('button')).toHaveCount(0);
});
test('คำตอบทะเบียนที่มาช้าหลังเปลี่ยนบัญชีไม่กลับมาใน DOM', async ({ page }) => {
  await login(page, 'late', true); await page.goto(url);
  await expect(page.getByLabel('ค้นหาผู้ใช้')).toBeVisible();
  let release!: () => void; const gate = new Promise<void>(r => { release = r; }); let arrived = false;
  await page.route('**/attendance/directory/memberships?*', async route => { const response = await route.fetch(); arrived = true; await gate; await route.fulfill({ response }).catch(() => {}); });
  const other = await page.context().newPage();
  try {
    await page.getByRole('button', { name: 'โหลดข้อมูลใหม่', exact: true }).click(); await expect.poll(() => arrived).toBe(true);
    await other.goto('/portal/account'); await other.getByRole('button', { name: 'ออกจากระบบ', exact: true }).click();
    await expect(page).toHaveURL(/\/login(?:\?|$)/); release();
    await login(other, 'staff'); await page.goto(url);
    await expect(page.locator('form')).toHaveCount(0); await expect(page.getByRole('listitem')).toHaveCount(0);
  } finally { release(); await page.unroute('**/attendance/directory/memberships?*'); await other.close(); }
});

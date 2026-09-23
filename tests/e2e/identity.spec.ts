import { test, expect, type Page } from '@playwright/test';
import { createHash, createHmac, randomBytes, randomUUID } from 'node:crypto';
import { spawnSync } from 'node:child_process';

const password = 'e2e-isolated-password-123';
test.beforeEach(async ({ page }) => {
  page.on('pageerror', error => console.log('browser script error:', error.name));
  page.on('requestfailed', request => {
    const url = new URL(request.url());
    if (url.pathname.startsWith('/_next/')) console.log('failed application asset:', url.pathname, request.failure()?.errorText);
  });
});
async function login(page: Page, username = 'e2e-staff', secret = password, target = '/login') {
  const pending = new Set<string>();
  const started = (request: import('@playwright/test').Request) => {
    // Paths/types only: never query, body, credentials or response contents.
    pending.add(request.resourceType() + ' ' + new URL(request.url()).pathname);
  };
  const ended = (request: import('@playwright/test').Request) => {
    pending.delete(request.resourceType() + ' ' + new URL(request.url()).pathname);
  };
  page.on('request', started); page.on('requestfinished', ended); page.on('requestfailed', ended);
  try {
    await page.goto(target, { waitUntil: 'domcontentloaded' });
  } catch (error) {
    console.log('login navigation pending resource types/paths:', [...pending]);
    throw error;
  } finally {
    page.off('request', started); page.off('requestfinished', ended); page.off('requestfailed', ended);
  }
  await page.getByLabel('ชื่อผู้ใช้', { exact: true }).fill(username);
  await page.getByLabel('รหัสผ่าน', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'เข้าสู่ระบบ', exact: true }).click();
}
function database(sql: string) {
  const container = process.env.TPR10_E2E_DATABASE;
  if (!container || !/^[a-f0-9]{64}$/.test(container)) throw new Error('ต้องรันผ่าน isolated HTTPS fixture');
  const result = spawnSync('docker', ['exec', '-i', container, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input: sql, encoding: 'utf8' });
  if (result.status !== 0) throw new Error('ฐานข้อมูล fixture ไม่พร้อม');
}
function totp(secret: string, offset = 0) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bits = [...secret].map(c => alphabet.indexOf(c).toString(2).padStart(5, '0')).join('');
  const key = Buffer.from(bits.match(/.{8}/g)!.map(b => parseInt(b, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000) + offset));
  const digest = createHmac('sha1', key).update(counter).digest();
  const index = digest[19] & 15;
  return ((digest.readUInt32BE(index) & 0x7fffffff) % 1000000).toString().padStart(6, '0');
}

test('ผู้ใช้ไม่เข้าสู่ระบบถูกส่งไปหน้า login', async ({ page }) => {
  await page.goto('/portal');
  await expect(page).toHaveURL(/\/login\?returnTo=%2Fportal$/);
  await expect(page.getByRole('button', { name: 'เข้าสู่ระบบ', exact: true })).toBeVisible();
});

test('รหัสผ่านผิดไม่เข้า Portal', async ({ page }) => {
  await login(page, 'e2e-staff', 'incorrect-password-123');
  await expect(page.locator('form').getByRole('alert')).toContainText('ไม่ถูกต้อง');
  await expect(page).toHaveURL(/\/login$/);
});

test('ก่อน JavaScript พร้อม ฟอร์มต้องไม่ส่งข้อมูลลับด้วย native GET', async ({ browser }) => {
  const context = await browser.newContext({ javaScriptEnabled: false });
  try {
    const page = await context.newPage();
    await page.goto('/login');
    await expect(page.getByLabel('รหัสผ่าน', { exact: true })).toBeDisabled();
    await expect(page.locator('form').getByRole('button')).toBeDisabled();
  } finally { await context.close(); }
});

test('login แยกข้อมูลสองบัญชี cookie ปลอดภัย logout และปุ่มย้อนกลับ', async ({ browser }) => {
  const first = await browser.newContext(); const second = await browser.newContext();
  try {
    const a = await first.newPage(); const b = await second.newPage();
    await login(a); await expect(a).toHaveURL(/\/portal$/);
    await login(b, 'e2e-other'); await expect(b).toHaveURL(/\/portal$/);
    const firstId = await a.getByTestId('current-user').textContent();
    const secondId = await b.getByTestId('current-user').textContent();
    expect(firstId).not.toEqual(secondId);
    await expect(a.locator('body')).not.toContainText(secondId!);
    const cookie = (await first.cookies()).find(c => c.name === '__Host-tpr10_session')!;
    expect(cookie.secure).toBe(true); expect(cookie.httpOnly).toBe(true);
    expect(cookie.sameSite).toBe('Lax'); expect(cookie.path).toBe('/'); expect(cookie.domain).toBe('localhost');
    expect(await a.evaluate(() => document.cookie)).not.toContain('__Host-tpr10_session');
    await a.getByRole('link', { name: 'บัญชีของฉัน' }).click();
    await a.getByRole('button', { name: 'ออกจากระบบ', exact: true }).click();
    await expect(a).toHaveURL(/\/login$/);
    await a.goBack();
    await expect(a.getByTestId('current-user')).toHaveCount(0);
    await b.reload(); await expect(b.getByTestId('current-user')).toHaveText(secondId!);
  } finally { await first.close(); await second.close(); }
});

test('บังคับเปลี่ยนรหัสผ่านก่อนใช้ returnTo', async ({ page }) => {
  await login(page, 'e2e-forced', password, '/login?returnTo=%2Fportal%2Faccount');
  await expect(page).toHaveURL(/\/auth\/change-password/);
  await page.getByLabel('รหัสผ่านปัจจุบัน', { exact: true }).fill(password);
  await page.getByLabel('รหัสผ่านใหม่', { exact: true }).fill('replacement-password-123');
  await page.getByRole('button', { name: 'เปลี่ยนรหัสผ่าน', exact: true }).click();
  await expect(page).toHaveURL(/\/login/);
  await login(page, 'e2e-forced', 'replacement-password-123');
  await expect(page).toHaveURL(/\/portal$/);
});

test('MFA enrollment challenge และ recovery ผ่าน API จริง', async ({ page }) => {
  await login(page, 'e2e-admin');
  await expect(page).toHaveURL(/\/auth\/mfa/);
  await page.getByRole('button', { name: 'เริ่มตั้งค่า MFA', exact: true }).click();
  const secret = await page.getByLabel('คีย์สำหรับกรอกเอง', { exact: true }).inputValue();
  await expect(page.getByText('ห้ามแชร์คีย์หรือรหัสกู้คืนกับผู้อื่น', { exact: true })).toBeVisible();
  await page.getByLabel('รหัสยืนยัน 6 หลัก').fill(totp(secret));
  await page.getByRole('button', { name: 'ยืนยัน MFA', exact: true }).click();
  await expect(page.getByTestId('recovery-code')).toHaveCount(10);
  const recovery = await page.getByTestId('recovery-code').first().textContent();
  await page.getByRole('button', { name: 'บันทึกรหัสกู้คืนแล้ว ไปต่อ' }).click();
  await expect(page).toHaveURL(/\/portal$/);
  await page.goto('/portal/account');
  await page.getByRole('button', { name: 'ออกจากระบบ', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
  await login(page, 'e2e-admin');
  await page.getByLabel('รหัสยืนยัน 6 หลัก').fill(totp(secret, 1));
  await page.getByRole('button', { name: 'ยืนยัน MFA', exact: true }).click();
  await expect(page).toHaveURL(/\/portal$/);
  await page.goto('/portal/account');
  await page.getByRole('button', { name: 'ออกจากระบบ', exact: true }).click();
  await expect(page).toHaveURL(/\/login$/);
  await login(page, 'e2e-admin');
  await page.getByRole('button', { name: 'ใช้รหัสกู้คืน' }).click();
  await page.getByLabel('รหัสกู้คืน', { exact: true }).fill(recovery!);
  await page.getByRole('button', { name: 'ยืนยันรหัสกู้คืน', exact: true }).click();
  await expect(page.getByRole('button', { name: 'เริ่มตั้งค่า MFA' })).toBeVisible();
});

test('returnTo ภายนอกและ malformed ไม่พาออกจาก Portal', async ({ browser }) => {
  test.setTimeout(60000);
  for (const path of ['https://evil.example/portal', '//evil.example/portal', '/portal\\evil', 'http://[', '/portal/%zz']) {
    const context = await browser.newContext();
    try {
      const page = await context.newPage();
      await login(page, 'e2e-staff', password, '/login?returnTo=' + encodeURIComponent(path));
      await expect(page).toHaveURL('https://localhost:4443/portal');
    } finally { await context.close(); }
  }
});

test('session หมดอายุถูกปฏิเสธหลัง reload', async ({ page }) => {
  await login(page); await expect(page).toHaveURL(/\/portal$/);
  database("UPDATE sessions SET last_seen_at_utc=now()-interval '31 minutes' WHERE user_id IN (SELECT id FROM users WHERE username='e2e-staff')");
  await page.reload(); await expect(page).toHaveURL(/\/login\?returnTo=/);
});

test('หน้า login ใช้งานบนมือถือและ keyboard ได้', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/login');
  await page.getByLabel('ชื่อผู้ใช้').focus();
  await page.keyboard.type('e2e-staff'); await page.keyboard.press('Tab');
  await expect(page.getByLabel('รหัสผ่าน', { exact: true })).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('reset fragment ถูกล้างก่อนส่ง request และ token ใช้ครั้งเดียว', async ({ page }) => {
  const raw = randomBytes(32); const token = raw.toString('base64url');
  database(`INSERT INTO password_reset_requests(id,user_id,token_hash,created_at_utc,expires_at_utc)
    SELECT '${randomUUID()}',id,decode('${createHash('sha256').update(raw).digest('hex')}','hex'),now(),now()+interval '15 minutes'
    FROM users WHERE username='e2e-other'`);
  const urls: string[] = [];
  page.on('request', request => urls.push(request.url()));
  await page.goto('/auth/reset#token=' + token);
  await expect.poll(() => page.evaluate(() => location.hash === '' && location.search === '')).toBe(true);
  await page.getByLabel('รหัสผ่านใหม่', { exact: true }).fill('reset-replacement-password-123');
  await page.getByRole('button', { name: 'ตั้งรหัสผ่านใหม่', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('สำเร็จ');
  expect(urls.every(url => !url.includes(token))).toBe(true);
  await page.goto('/auth/reset#token=' + token);
  await page.getByLabel('รหัสผ่านใหม่', { exact: true }).fill('reset-replacement-password-456');
  await page.getByRole('button', { name: 'ตั้งรหัสผ่านใหม่', exact: true }).click();
  await expect(page.locator('form').getByRole('alert')).toContainText('ไม่สำเร็จ');
});

test('ผู้ใช้ทั่วไปตั้ง MFA และออกจากระบบทุกอุปกรณ์ของตนเองได้', async ({ browser }) => {
  const first = await browser.newContext(); const second = await browser.newContext();
  try {
    const a = await first.newPage(); const b = await second.newPage();
    await login(a, 'e2e-optional'); await expect(a).toHaveURL(/\/portal$/);
    await login(b, 'e2e-optional'); await expect(b).toHaveURL(/\/portal$/);
    await a.goto('/portal/account');
    await a.getByRole('button', { name: 'ออกจากระบบทุกอุปกรณ์', exact: true }).click();
    await expect(a).toHaveURL(/\/login$/);
    await b.reload(); await expect(b).toHaveURL(/\/login\?returnTo=/);
    await login(a, 'e2e-optional'); await expect(a).toHaveURL(/\/portal$/);
    await a.goto('/portal/account');
    await a.getByRole('link', { name: 'ตั้งค่า MFA เพิ่มความปลอดภัย' }).click();
    await a.getByRole('button', { name: 'เริ่มตั้งค่า MFA', exact: true }).click();
    const secret = await a.getByLabel('คีย์สำหรับกรอกเอง').inputValue();
    await a.getByLabel('รหัสยืนยัน 6 หลัก').fill(totp(secret));
    await a.getByRole('button', { name: 'ยืนยัน MFA', exact: true }).click();
    await expect(a.getByTestId('recovery-code')).toHaveCount(10);
    await a.getByRole('button', { name: 'บันทึกรหัสกู้คืนแล้ว ไปต่อ' }).click();
    await expect(a).toHaveURL(/\/portal\/account$/);
    await expect(a.getByText('เปิดใช้งาน MFA แล้ว')).toBeVisible();
  } finally { await first.close(); await second.close(); }
});

test('กดส่งซ้ำระหว่างรอส่ง login จริงเพียงครั้งเดียว', async ({ page }) => {
  let count = 0;
  let release!: () => void;
  const gate = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/auth/login', async route => { count++; await gate; await route.continue(); });
  try {
    await login(page);
    await expect.poll(() => count).toBe(1);
    await expect(page.locator('form').getByRole('button')).toBeDisabled();
    await page.locator('form').evaluate(form => (form as HTMLFormElement).requestSubmit());
    release();
    await expect(page).toHaveURL(/\/portal$/);
    expect(count).toBe(1);
  } finally { release(); }
});

test('API ล่มไม่ถือว่า session ใช้ได้และไม่แกล้ง redirect เป็น logout', async ({ page }) => {
  const api = process.env.TPR10_E2E_API!;
  expect(/^[a-f0-9]{64}$/.test(api)).toBe(true);
  await login(page); await expect(page).toHaveURL(/\/portal$/);
  expect(spawnSync('docker', ['stop', api]).status).toBe(0);
  try {
    await page.reload();
    await expect(page.getByRole('heading', { name: 'บริการเข้าสู่ระบบไม่พร้อมใช้งาน' })).toBeVisible();
    await expect(page).toHaveURL(/\/portal$/);
    await expect(page.getByTestId('current-user')).toHaveCount(0);
  } finally { expect(spawnSync('docker', ['start', api]).status).toBe(0); }
});

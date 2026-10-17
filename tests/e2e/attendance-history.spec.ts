import { test, expect, type Page, type BrowserContext } from '@playwright/test';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

// Synthetic history/device data; real HTTPS, Next and session validation.
const id = '55555555-5555-4555-8555-555555555555';
const site = { workspaceId: '11111111-1111-4111-8111-111111111111', projectId: '22222222-2222-4222-8222-222222222222', siteId: '33333333-3333-4333-8333-333333333333' };
const otherSite = '44444444-4444-4444-8444-444444444444';
const event = { id, occurredAtUtc: '2026-09-25T16:55:00+00:00', site, gps: { latitude: 13.7563, longitude: 100.5018, accuracyMeters: 15, sampledAtUtc: '2026-09-25T16:54:55+00:00' }, photo: { evidenceId: id } };
const row = { id, employeeId: id, workDate: '2026-09-25', version: 2, in: event, out: { ...event, id: otherSite, site: { ...site, siteId: otherSite }, occurredAtUtc: '2026-09-26T01:05:00+00:00' }, readBasis: 1, effectiveInUtc: event.occurredAtUtc, effectiveOutUtc: '2026-09-26T01:05:00+00:00' };
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64');
function sql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{12,64}$/.test(database)) throw new Error('isolated fixture required');
  return execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1'], { input, encoding: 'utf8' }).trim();
}
async function session(context: BrowserContext, username = 'e2e-scope-staff') {
  const raw = randomBytes(32), hash = createHash('sha256').update(raw).digest('hex');
  sql(`INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,security_version,mfa_verified_at_utc) SELECT '${randomUUID()}',id,decode('${hash}','hex'),'Active',now(),now(),now()+interval '8 hours',security_version,now() FROM users WHERE username='${username}';`);
  await context.addCookies([{ name: '__Host-tpr10_session', value: raw.toString('base64url'), domain: 'localhost', path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function setup(page: Page, context: BrowserContext, audience = 'mine') {
  await session(context);
  await page.route('**/api/v1/attendance/current', r => r.fulfill({ json: { openDay: null, todayCompleted: false, serverNowUtc: new Date().toISOString() } }));
  await page.route('**/api/v1/attendance/access', r => r.fulfill({ json: { canRecord: true, canReadTeam: true, canReadHr: true, canApproveSupervisor: false, canApproveHr: false, canManageDirectory: false } }));
  await page.route('**/api/v1/attendance/evidence/*/thumbnail', r => r.fulfill({ contentType: 'image/png', body: png, headers: { 'Cache-Control': 'no-store' } }));
  await page.route(`**/api/v1/attendance/${audience}?*`, r => r.fulfill({ json: { items: [{ ...row, readBasis: audience === 'team' ? 2 : audience === 'hr' ? 3 : 1 }], nextCursor: null } }));
  await page.goto('/portal/attendance' + (audience === 'mine' ? '' : '/' + audience));
  await expect(page.getByRole('heading', { name: 'ประวัติการลงเวลา', exact: true })).toBeVisible();
}
test('ของตนเองแสดงรูป GPS วันไทย คู่ข้ามวัน และสถานที่เข้าออกแยกกัน', async ({ page, context }) => {
  await setup(page, context);
  const history = page.getByRole('region', { name: 'ประวัติการลงเวลา', exact: true });
  await expect(history.getByText(/25 กันยายน 2569/)).toBeVisible();
  await expect(history.getByText('ทำงานข้ามวัน')).toBeVisible();
  await expect(history.getByText(/13.7563, 100.5018/)).toHaveCount(2);
  await expect(history.getByText(site.siteId, { exact: true })).toBeVisible();
  await expect(history.getByText(otherSite, { exact: true })).toBeVisible();
  await expect(history.getByRole('img')).toHaveCount(2);
  await expect(history.getByText('สิทธิ์: ข้อมูลของตนเอง')).toBeVisible();
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);
});
test('หน้าหัวหน้าไม่สร้างรูปหรือร้องขอรูปแม้ response ผิดสัญญามี photo', async ({ page, context }) => {
  const photos: string[] = []; page.on('request', r => { if (r.url().includes('/evidence/')) photos.push(r.url()); });
  await setup(page, context, 'team');
  await expect(page.getByText('สิทธิ์: สายบังคับบัญชา')).toBeVisible();
  await expect(page.getByText(/13.7563, 100.5018/)).toHaveCount(2);
  await expect(page.locator('#history img')).toHaveCount(0);
  await expect(page.locator('a[href*="/evidence/"]')).toHaveCount(0);
  expect(photos).toEqual([]);
});
test('HR แสดงเฉพาะรายการที่ API อนุญาตและล้างรูปทันทีเมื่อกลับแท็บก่อนตรวจสิทธิ์เสร็จ', async ({ page, context }) => {
  await setup(page, context, 'hr');
  await expect(page.locator('#history img')).toHaveCount(2);
  await expect(page.getByText('สิทธิ์: HR ที่ได้รับมอบหมาย')).toBeVisible();
  let release!: () => void; const held = new Promise<void>(r => { release = r; });
  await page.route('**/api/v1/attendance/hr?*', async r => { await held; await r.fulfill({ status: 403, json: {} }).catch(() => {}); });
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  try { await expect(page.locator('#history img')).toHaveCount(0); await expect(page.getByText(/13.7563, 100.5018/)).toHaveCount(0); }
  finally { release(); }
  await expect(page.getByRole('alert').filter({ hasText: 'ไม่มีสิทธิ์' })).toBeVisible();
});
test('ผู้ดูแลระบบไม่มีสิทธิ์อ่าน HR หรือรูปโดยอัตโนมัติผ่าน API จริง', async ({ page, context }) => {
  await session(context, 'e2e-admin');
  await page.goto('/portal/attendance/hr');
  await expect(page.getByRole('alert').filter({ hasText: 'ไม่มีสิทธิ์' })).toBeVisible();
  await expect(page.locator('#history img')).toHaveCount(0);
  const status = await page.evaluate(async id => (await fetch('/api/v1/attendance/evidence/' + id, { cache: 'no-store' })).status, id);
  expect([403, 404]).toContain(status);
});
test('เปลี่ยนบัญชีระหว่าง fetch ไม่แสดงผลบัญชีเดิม', async ({ page, context }) => {
  await setup(page, context);
  let release!: () => void; const held = new Promise<void>(r => { release = r; });
  let waiting = false;
  await page.route('**/api/v1/attendance/mine?*', async r => { waiting = true; await held; await r.fulfill({ json: { items: [row], nextCursor: null } }).catch(() => {}); });
  await page.getByRole('button', { name: 'โหลดประวัติใหม่', exact: true }).click();
  await expect.poll(() => waiting).toBe(true);
  await page.route('**/api/v1/attendance/mine?*', r => r.fulfill({ json: { items: [], nextCursor: null } }));
  await session(context, 'e2e-other');
  await page.evaluate(() => { const channel = new BroadcastChannel('tpr10-auth-change'); channel.postMessage('changed'); channel.close(); });
  release();
  await expect(page.getByText('ไม่พบประวัติในช่วงวันที่เลือก')).toBeVisible();
  await expect(page.locator('#history img')).toHaveCount(0);
  await expect(page.getByText(/13.7563, 100.5018/)).toHaveCount(0);
});
test('แบ่งหน้าด้วย cursor และเปลี่ยนช่วงวันกลับหน้าแรก', async ({ page, context }) => {
  await setup(page, context);
  const urls: string[] = [];
  await page.route('**/api/v1/attendance/mine?*', r => { urls.push(r.request().url()); return r.fulfill({ json: { items: [row], nextCursor: new URL(r.request().url()).searchParams.has('cursor') ? null : 'next-page' } }); });
  await page.getByRole('button', { name: 'โหลดประวัติใหม่', exact: true }).click();
  await page.getByRole('button', { name: 'ประวัติหน้าถัดไป', exact: true }).click();
  await expect.poll(() => urls.some(x => new URL(x).searchParams.get('cursor') === 'next-page')).toBe(true);
  await page.getByLabel('วันที่เริ่มต้น').fill('2026-09-01'); await page.getByLabel('วันที่สิ้นสุด').fill('2026-09-30');
  await page.getByRole('button', { name: 'ค้นหาประวัติ', exact: true }).click();
  await expect.poll(() => new URL(urls.at(-1)!).searchParams.get('from')).toBe('2026-09-01');
  expect(new URL(urls.at(-1)!).searchParams.has('cursor')).toBe(false);
});
test('บริการขัดข้องล้างข้อมูลและลองใหม่ได้โดยไม่ logout', async ({ page, context }) => {
  await setup(page, context);
  await page.route('**/api/v1/attendance/mine?*', r => r.fulfill({ status: 503, json: {} }));
  await page.getByRole('button', { name: 'โหลดประวัติใหม่', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'ไม่พร้อมใช้งาน' })).toBeVisible();
  await expect(page.locator('#history img')).toHaveCount(0);
  await expect(page).toHaveURL(/\/portal\/attendance$/);
});
test('มือถือแนวตั้งแนวนอนและแป้นพิมพ์ใช้ค้นหาประวัติได้', async ({ page, context }) => {
  await setup(page, context, 'hr');
  for (const viewport of [{ width: 390, height: 844 }, { width: 844, height: 390 }]) {
    await page.setViewportSize(viewport);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
  await page.getByRole('button', { name: 'ค้นหาประวัติ', exact: true }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByText('สิทธิ์: HR ที่ได้รับมอบหมาย')).toBeVisible();
});

test('ซ่อนแท็บล้าง DOM และกลับแท็บโหลดขอบเขต HR ใหม่ที่ว่าง', async ({ page, context }) => {
  await setup(page, context, 'hr');
  await expect(page.locator('#history img')).toHaveCount(2);
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(page.locator('#history img')).toHaveCount(0);
  await expect(page.getByText(/13.7563, 100.5018/)).toHaveCount(0);
  await page.route('**/api/v1/attendance/hr?*', r => r.fulfill({ json: { items: [], nextCursor: null } }));
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(page.getByText('ไม่พบประวัติในช่วงวันที่เลือก')).toBeVisible();
  await expect(page.locator('#history img')).toHaveCount(0);
});

test('session ตรวจไม่ได้ต้องล้างข้อมูลจาก DOM แม้ history API ยังตอบสำเร็จ', async ({ page, context }) => {
  await setup(page, context, 'hr');
  await expect(page.locator('#history img')).toHaveCount(2);
  await page.route('**/api/v1/auth/session', r => r.fulfill({ status: 503, json: {} }));
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await expect(page.locator('#history img')).toHaveCount(0);
  await expect(page.getByText(/13.7563, 100.5018/)).toHaveCount(0);
  await expect(page).toHaveURL(/\/portal\/attendance\/hr$/);
});

test('เปลี่ยนบัญชีระหว่าง fetch แม้ไม่มี auth notification ต้องไม่แสดงข้อมูลเดิม', async ({ page, context }) => {
  await setup(page, context);
  let release!: () => void; const held = new Promise<void>(r => { release = r; });
  let waiting = false;
  await page.route('**/api/v1/attendance/mine?*', async r => { waiting = true; await held; await r.fulfill({ json: { items: [row], nextCursor: null } }).catch(() => {}); });
  await page.getByRole('button', { name: 'โหลดประวัติใหม่', exact: true }).click();
  await expect.poll(() => waiting).toBe(true);
  await page.route('**/api/v1/attendance/mine?*', r => r.fulfill({ json: { items: [], nextCursor: null } }));
  await session(context, 'e2e-other'); release();
  await expect(page.getByText('ไม่พบประวัติในช่วงวันที่เลือก')).toBeVisible();
  await expect(page.locator('#history img')).toHaveCount(0);
  await expect(page.getByText(/13.7563, 100.5018/)).toHaveCount(0);
});

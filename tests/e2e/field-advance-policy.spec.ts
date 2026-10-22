import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
const root = '/portal/field-advances/settings';
const api = '/api/v1/field-advances/policies';
const screenshot = (name: string) => resolve('.superpowers/sdd/2026-10-19-module-7a-policy-permissions', name);
const workspace = (n: number) => `88100000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const project = (n: number) => `88200000-0000-4000-8000-${String(n).padStart(12, '0')}`;
function fixtureSql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{64}$/.test(database)) throw new Error('ต้องใช้ disposable HTTPS fixture');
  execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input, stdio: ['pipe', 'ignore', 'pipe'] });
}
async function session(context: BrowserContext, actor: number, company = 1) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{64}$/.test(database)) throw new Error('ต้องใช้ disposable HTTPS fixture');
  const user = `88000000-0000-4000-8000-${String(company * 100 + actor).padStart(12, '0')}`;
  const token = randomBytes(32); const hash = createHash('sha256').update(token).digest('hex');
  execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input: `
    INSERT INTO mfa_factors(id,user_id,protected_secret,confirmed_at_utc) SELECT gen_random_uuid(),'${user}','fixture-only-unused',now() WHERE NOT EXISTS (SELECT 1 FROM mfa_factors WHERE user_id='${user}');
    INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc) VALUES ('${randomUUID()}','${user}',decode('${hash}','hex'),'Active',now(),now(),now()+interval '1 hour',now());`, stdio: ['pipe', 'ignore', 'pipe'] });
  await context.addCookies([{ name: '__Host-tpr10_session', value: token.toString('base64url'), domain: 'localhost', path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function send(context: BrowserContext, path: string, body: unknown, method = 'POST') {
  const csrf = await context.request.get('/api/v1/auth/csrf'); const { token } = await csrf.json();
  return context.request.fetch(api + path, { method, headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': token }, data: body });
}
type Row = { id: string; version: number; contentHash: string; mode: string; sourcePolicyRevisionId: string | null; baseCompanyVersionId: string | null; expectedActiveRevisionId: string | null; expectedActiveVersion: number; content: { clearingDays: number; bands: { order: number; levelCode: string; upperInclusive: string }[]; categories: { id: string; name: string; active: boolean; unit: string | null; standardRate: string | null; evidenceTypes: string[] }[] } };
function commandMeta(v: Row) { return { commandId: randomUUID(), expectedVersion: v.version, reason: 'จำลอง', policy: { mode: v.mode, sourcePolicyRevisionId: v.sourcePolicyRevisionId, baseCompanyVersionId: v.baseCompanyVersionId, expectedActiveRevisionId: v.expectedActiveRevisionId, expectedActiveVersion: v.expectedActiveVersion, expectedContentHash: v.contentHash } }; }
async function seed(context: BrowserContext, reviewer: BrowserContext, n: number, source?: Row, days = 7) {
  const content = { clearingDays: days, bands: [{ order: 1, levelCode: 'director', upperInclusive: '1000.00' }], categories: [{ id: '88300000-0000-4000-8000-000000000001', name: 'หมวดจำลอง', active: true, unit: 'วัน', standardRate: '0.01', evidenceTypes: ['ใบเสร็จ'] }] };
  const meta = { commandId: randomUUID(), expectedVersion: 1, reason: 'จำลอง', policy: { mode: 'Company', sourcePolicyRevisionId: source?.id ?? null, baseCompanyVersionId: null, expectedActiveRevisionId: source?.id ?? null, expectedActiveVersion: source ? source.expectedActiveVersion + 1 : 0, expectedContentHash: null } };
  const created = await send(context, '', { scope: { workspaceId: workspace(n), projectId: null }, baseCompanyVersionId: null, content, meta }); expect(created.status()).toBe(201);
  let row: Row = await created.json();
  const submitted = await send(context, `/${row.id}/submit`, commandMeta(row)); expect(submitted.status()).toBe(200); row = await submitted.json();
  const approved = await send(reviewer, `/${row.id}/review`, { decision: 'approve', meta: commandMeta(row) }); expect(approved.status()).toBe(200);
  return await approved.json() as Row;
}
async function fill(page: Page, days = '7') {
  await page.getByLabel('วันเคลียร์ (วันปฏิทิน)').fill(days);
  await page.getByRole('button', { name: 'เพิ่มระดับวงเงิน', exact: true }).click();
  await page.getByLabel('รหัสระดับ 1', { exact: true }).fill('director');
  await page.getByLabel('วงเงินไม่เกิน 1', { exact: true }).fill('9999999999999999.99');
  for (const [i, name] of ['เดินทาง', 'ที่พัก', 'อาหาร', 'เบี้ยเลี้ยง', 'น้ำมัน'].entries()) {
    await page.getByLabel(`หลักฐานหมวด ${i + 1}`, { exact: true }).fill('ใบเสร็จ');
    await expect(page.getByLabel(`ชื่อหมวด ${i + 1}`, { exact: true })).toHaveValue(name);
  }
  await page.getByLabel('เหตุผล', { exact: true }).fill('ข้อมูลจำลองเท่านั้น');
}
test('ร่าง ส่งตรวจ เทียบ อนุมัติ แยกบัญชี และหมวดปิดไม่ลบประวัติ', async ({ page, context, browser }) => {
  await session(context, 1);
  const response = await page.goto(`${root}/company?workspaceId=${workspace(1)}`);
  expect(response!.status()).toBe(200);
  await page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true }).click(); await fill(page);
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
  await page.getByLabel('เหตุผล', { exact: true }).fill('ส่งให้ผู้ตรวจ');
  await page.getByRole('button', { name: 'ส่งตรวจ', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('ส่งตรวจสำเร็จ');
  const rows = await (await context.request.get(`${api}?workspaceId=${workspace(1)}`)).json(); const pending = rows[0];
  expect(pending.content.bands[0].upperInclusive).toBe('9999999999999999.99');
  const meta = { commandId: randomUUID(), expectedVersion: pending.version, reason: 'พยายามตรวจเอง', policy: { mode: pending.mode, sourcePolicyRevisionId: pending.sourcePolicyRevisionId, baseCompanyVersionId: pending.baseCompanyVersionId, expectedActiveRevisionId: pending.expectedActiveRevisionId, expectedActiveVersion: pending.expectedActiveVersion, expectedContentHash: pending.contentHash } };
  expect((await send(context, `/${pending.id}/review`, { decision: 'approve', meta })).status()).toBe(403);
  await expect(page.getByRole('button', { name: 'อนุมัติและเปิดใช้', exact: true })).toHaveCount(0);
  const reviewer = await browser.newContext(); await session(reviewer, 2); const review = await reviewer.newPage();
  try {
    await review.goto(`${root}/reviews?workspaceId=${workspace(1)}`);
    await review.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await expect(review.getByRole('heading', { name: 'เปรียบเทียบก่อน–หลัง', exact: true })).toBeVisible();
    await review.getByLabel('เหตุผล', { exact: true }).fill('ตรวจข้อมูลจำลองแล้ว');
    await review.getByRole('button', { name: 'อนุมัติและเปิดใช้', exact: true }).click();
    await expect(review.getByRole('status')).toContainText('อนุมัติสำเร็จ');
    await expect(review.getByText(`กติกาที่มีผล · ฐานบริษัท ${pending.id}`, { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
    await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true }).click();
    await page.getByLabel('เปิดใช้หมวด 1', { exact: true }).uncheck();
    await page.getByLabel('เหตุผล', { exact: true }).fill('ปิดหมวดในฉบับใหม่');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    const prior = await (await context.request.get(`${api}/${pending.id}?workspaceId=${workspace(1)}`)).json();
    expect(prior.content.categories[0].active).toBe(true);
    await page.getByLabel('ยอดตัวอย่าง (บาท)').fill('9999999999999999.99');
    await page.getByRole('button', { name: 'ประเมินวงเงิน', exact: true }).click();
    await expect(page.getByText('ระดับที่ต้องผ่าน: supervisor → project-manager → director', { exact: true })).toBeVisible();
    await page.screenshot({ path: screenshot('task-8-policy-wide.png'), fullPage: true });
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByLabel('เหตุผล', { exact: true }).focus(); await page.keyboard.type(' แป้นพิมพ์');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: screenshot('task-8-policy-narrow.png'), fullPage: true });
    expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);

    // I2: the same total has different approval levels after activating this revision.
    await page.getByLabel('รหัสระดับ 1', { exact: true }).fill('executive');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await page.getByLabel('เหตุผล', { exact: true }).fill('ส่งระดับใหม่ตรวจ');
    await page.getByRole('button', { name: 'ส่งตรวจ', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('ส่งตรวจสำเร็จ');
    await review.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
    await review.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await review.getByLabel('ยอดตัวอย่าง (บาท)').fill('100.00');
    await review.getByRole('button', { name: 'ประเมินวงเงิน', exact: true }).click();
    await expect(review.getByText('ระดับที่ต้องผ่าน: supervisor → project-manager → director', { exact: true })).toBeVisible();
    await review.getByLabel('เหตุผล', { exact: true }).fill('อนุมัติระดับใหม่');
    await review.getByRole('button', { name: 'อนุมัติและเปิดใช้', exact: true }).click();
    await expect(review.getByRole('status')).toContainText('อนุมัติสำเร็จ');
    await expect(review.getByText(/^ระดับที่ต้องผ่าน:/)).toHaveCount(0);
    await expect(review.getByRole('status')).toContainText('ประเมินวงเงินใหม่');
    await expect(review.getByLabel('ยอดตัวอย่าง (บาท)')).toHaveValue('100.00');
    await review.getByRole('button', { name: 'ประเมินวงเงิน', exact: true }).click();
    await expect(review.getByText('ระดับที่ต้องผ่าน: supervisor → project-manager → executive', { exact: true })).toBeVisible();
  } finally { await reviewer.close(); }
});

test('โครงการคงฐานเก่า แจ้งทบทวน และรับบริษัทล่าสุดผ่านอนุมัติ', async ({ page, context, browser }) => {
  await session(context, 1, 2); const reviewer = await browser.newContext(); await session(reviewer, 2, 2);
  try {
    const old = await seed(context, reviewer, 2);
    await page.goto(`${root}/projects/${project(2)}?workspaceId=${workspace(2)}`);
    await page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true }).click();
    await page.getByLabel('วันเคลียร์ (วันปฏิทิน)').fill('3'); await page.getByLabel('เหตุผล', { exact: true }).fill('เฉพาะโครงการ');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click(); await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await page.getByLabel('เหตุผล', { exact: true }).fill('ส่งตรวจ'); await page.getByRole('button', { name: 'ส่งตรวจ', exact: true }).click(); await expect(page.getByRole('status')).toContainText('ส่งตรวจสำเร็จ');
    let rows = await (await reviewer.request.get(`${api}?workspaceId=${workspace(2)}&projectId=${project(2)}`)).json();
    expect((await send(reviewer, `/${rows[0].id}/review`, { decision: 'approve', meta: commandMeta(rows[0]) })).status()).toBe(200);
    const latest = await seed(context, reviewer, 2, old, 20);
    await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
    await expect(page.getByText(/ต้องทบทวน · ยังคงใช้ฐานบริษัทเก่า/)).toContainText(old.id);
    await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await expect(page.getByLabel('วันเคลียร์ (วันปฏิทิน)')).toHaveValue('3');
    await page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true }).click();
    await expect(page.getByText(`ฉบับบริษัทฐาน: ${old.id}`, { exact: true })).toBeVisible();
    const basisDiff = page.getByRole('heading', { name: 'ความต่างจากบริษัทฐาน', exact: true }).locator('xpath=following-sibling::ul[1]');
    await expect(basisDiff).toContainText('วันเคลียร์: 7 → 3');
    await page.getByRole('button', { name: 'รับฐานบริษัทล่าสุดในร่าง', exact: true }).click();
    await expect(page.getByText(`ฉบับบริษัทฐาน: ${latest.id}`, { exact: true })).toBeVisible();
    await expect(basisDiff).toContainText('วันเคลียร์: 20 → 3');
    await expect(basisDiff).not.toContainText('วันเคลียร์: 7 → 3');
    await page.getByLabel('เหตุผล', { exact: true }).fill('รับฐานใหม่แต่คงข้อแตกต่าง');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await expect(basisDiff).toContainText('วันเคลียร์: 20 → 3');
    await expect(page.getByText(`ฐานของฉบับที่อ่าน ${latest.id}`, { exact: true })).toBeVisible();
    await expect(page.getByText(`กติกาที่มีผล · ฐานบริษัท ${old.id}`, { exact: true })).toBeVisible();
    await page.getByRole('combobox', { name: 'ที่มากติกา', exact: true }).selectOption('Inherit');
    await expect(page.getByLabel('วันเคลียร์ (วันปฏิทิน)')).toHaveValue('20'); await expect(page.getByLabel('วันเคลียร์ (วันปฏิทิน)')).toBeDisabled();
    await expect(basisDiff).toHaveText('');
    await page.getByLabel('เหตุผล', { exact: true }).fill('รับบริษัทล่าสุด'); await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click(); await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await expect(basisDiff).toHaveText('');
    await page.getByLabel('เหตุผล', { exact: true }).fill('ตรวจการรับบริษัท'); await page.getByRole('button', { name: 'ส่งตรวจ', exact: true }).click(); await expect(page.getByRole('status')).toContainText('ส่งตรวจสำเร็จ');
    await expect(basisDiff).toHaveText('');
    const review = await reviewer.newPage(); await review.goto(`${root}/reviews?workspaceId=${workspace(2)}&projectId=${project(2)}`);
    await review.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await expect(review.getByText('วันเคลียร์: 3 → 20', { exact: true })).toBeVisible();
    await review.screenshot({ path: screenshot('task-8-policy-review.png'), fullPage: true });
    await review.getByLabel('เหตุผล', { exact: true }).fill('อนุมัติฐานใหม่'); await review.getByRole('button', { name: 'อนุมัติและเปิดใช้', exact: true }).click(); await expect(review.getByRole('status')).toContainText('อนุมัติสำเร็จ');
    rows = await (await reviewer.request.get(`${api}/effective?workspaceId=${workspace(2)}&projectId=${project(2)}`)).json();
    expect(rows.companyVersionId).toBe(latest.id); expect(rows.content.clearingDays).toBe(20); expect(rows.requiresReview).toBe(false);
  } finally { await reviewer.close(); }
});
for (const [n, status] of [[3, 409], [4, 403], [5, 503]]) test(`${status} ควบคุมข้อมูลเก่าและไม่ส่ง mutation ซ้ำ`, async ({ page, context, browser }) => {
  await session(context, 1, n); const reviewer = await browser.newContext(); await session(reviewer, 2, n);
  try {
    await seed(context, reviewer, n);
    await page.goto(`${root}/company?workspaceId=${workspace(n)}`);
    await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click(); await page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true }).click();
    await page.getByLabel('เหตุผล', { exact: true }).fill('ข้อความส่วนตัว'); let calls = 0;
    await page.route(`**${api}`, async route => { calls++; await route.fulfill({ status, json: { code: 'controlled-test-failure' } }); });
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click(); await expect(page.getByRole('alert').filter({ hasText: status === 403 ? 'ไม่มีสิทธิ์' : status === 409 ? 'โหลดใหม่' : 'บริการไม่พร้อม' })).toBeVisible();
    expect(calls).toBe(1);
    if (status !== 503) { await expect(page.getByLabel('เหตุผล', { exact: true })).toHaveCount(0); await expect(page.getByText('หมวดจำลอง', { exact: true })).toHaveCount(0); }
    else { await expect(page).toHaveURL(/\/company/); await expect(page.getByRole('button', { name: 'บันทึกร่าง', exact: true })).toBeDisabled(); }
    await page.unroute(`**${api}`); await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click(); await expect(page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first()).toBeEnabled();
  } finally { await reviewer.close(); }
});
test('บัญชีเปลี่ยนระหว่าง response ล่าช้าไม่คืนข้อมูลเก่า', async ({ page, context, browser }) => {
  await session(context, 1, 6); const reviewer = await browser.newContext(); await session(reviewer, 2, 6);
  let release!: () => void; const gate = new Promise<void>(resolve => { release = resolve; }); let waiting = false;
  try {
    await seed(context, reviewer, 6);
    await page.goto(`${root}/company?workspaceId=${workspace(6)}`); await expect(page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first()).toBeVisible();
    await page.route(`**${api}?*`, async route => { const response = await route.fetch(); waiting = true; await gate; await route.fulfill({ response }).catch(() => {}); });
    await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click(); await expect.poll(() => waiting).toBe(true);
    await session(context, 5, 6); release();
    await expect(page.getByRole('alert').filter({ hasText: 'บัญชีเปลี่ยน' })).toBeVisible(); await expect(page.getByRole('button', { name: 'เปิดฉบับ', exact: true })).toHaveCount(0);
    await expect(page.getByText('หมวดจำลอง', { exact: true })).toHaveCount(0);
  } finally { release(); await reviewer.close(); }
});
test('ผู้ตรวจส่งกลับและปฏิเสธพร้อมเหตุผล เก็บเนื้อหาที่ตรวจในประวัติ', async ({ page, context, browser }) => {
  await session(context, 1, 7); const reviewer = await browser.newContext(); await session(reviewer, 2, 7);
  try {
    await seed(context, reviewer, 7);
    await page.goto(`${root}/company?workspaceId=${workspace(7)}`);
    await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click(); await page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true }).click();
    await page.getByLabel('วันเคลียร์ (วันปฏิทิน)').fill('9'); await page.getByLabel('เหตุผล', { exact: true }).fill('เสนอวันใหม่');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click(); await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await page.getByLabel('เหตุผล', { exact: true }).fill('ส่งตรวจ'); await page.getByRole('button', { name: 'ส่งตรวจ', exact: true }).click(); await expect(page.getByRole('status')).toContainText('ส่งตรวจสำเร็จ');
    const review = await reviewer.newPage(); await review.goto(`${root}/reviews?workspaceId=${workspace(7)}`); await review.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await expect(review.getByText('วันเคลียร์: 7 → 9', { exact: true })).toBeVisible(); await review.getByLabel('เหตุผล', { exact: true }).fill('ขอเหตุผลเพิ่มเติม');
    await review.getByRole('button', { name: 'ส่งกลับแก้ไข', exact: true }).click(); await expect(review.getByRole('status')).toContainText('ส่งกลับสำเร็จ');
    await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click(); await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await page.getByLabel('วันเคลียร์ (วันปฏิทิน)').fill('10'); await page.getByLabel('เหตุผล', { exact: true }).fill('ชี้แจงเพิ่มเติม');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click(); await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
    await page.getByLabel('เหตุผล', { exact: true }).fill('ส่งตรวจใหม่'); await page.getByRole('button', { name: 'ส่งตรวจ', exact: true }).click(); await expect(page.getByRole('status')).toContainText('ส่งตรวจสำเร็จ');
    await review.getByRole('button', { name: 'โหลดใหม่', exact: true }).click(); await review.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
    await review.getByLabel('เหตุผล', { exact: true }).fill('ยังไม่รับกติกาฉบับนี้'); await review.getByRole('button', { name: 'ปฏิเสธ', exact: true }).click(); await expect(review.getByRole('status')).toContainText('ปฏิเสธสำเร็จ');
    const history = review.getByRole('heading', { name: 'ประวัติการตรวจฉบับ', exact: true }).locator('..');
    await history.locator('summary').filter({ hasText: 'ส่งกลับ' }).click(); await expect(history.getByText('เหตุผล: ขอเหตุผลเพิ่มเติม', { exact: true })).toBeVisible();
    await history.locator('summary').filter({ hasText: 'ปฏิเสธ' }).click(); await expect(history.getByText('เหตุผล: ยังไม่รับกติกาฉบับนี้', { exact: true })).toBeVisible();
  } finally { await reviewer.close(); }
});
test('สิทธิ์โครงการค้นพบเฉพาะโครงการและอ่านบริษัทได้เพียง context ที่อนุญาต', async ({ page, context, browser }) => {
  await session(context, 1, 8); const reviewer = await browser.newContext(); await session(reviewer, 2, 8);
  try {
    const company = await seed(context, reviewer, 8); await session(context, 4, 8);
    await page.goto(root);
    await expect(page.getByRole('link', { name: 'บริษัทจำลอง 8 · โครงการจำลอง 8', exact: true })).toBeVisible();
    await expect(page.getByRole('link', { name: 'บริษัทจำลอง 8 · กติกาบริษัท', exact: true })).toHaveCount(0);
    const scopes = await (await context.request.get(`${api}/scopes`)).json(); expect(scopes).toHaveLength(1); expect(scopes[0].scope.projectId).toBe(project(8));
    await page.getByRole('link', { name: 'บริษัทจำลอง 8 · โครงการจำลอง 8', exact: true }).click();
    await expect(page.getByText('บริษัทฐานและฉบับล่าสุด', { exact: true })).toBeVisible();
    const scoped = `${api}/company-context?workspaceId=${workspace(8)}&projectId=${project(8)}`;
    const allowed = await (await context.request.get(scoped)).json(); expect(allowed.latest.id).toBe(company.id);
    expect((await context.request.get(`${scoped}&policyId=${company.id}`)).status()).toBe(403);
    expect((await context.request.get(`${api}?workspaceId=${workspace(8)}`)).status()).toBe(403);
    await page.goto(`${root}/company?workspaceId=${workspace(8)}`); await expect(page.getByRole('alert').filter({ hasText: 'ไม่มีสิทธิ์' })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'บริษัทจำลอง 8 · กติกาบริษัท', exact: true })).toHaveCount(0);
  } finally { await reviewer.close(); }
});

test('final I3 idle authorized policy keeps unsent draft after refresh', async ({ page, context }) => {
  await session(context, 1, 1);
  let checks = 0;
  page.on('response', response => { if (response.url().includes(`${api}/scopes`) && response.status() === 200) checks++; });
  await page.goto(`${root}/company?workspaceId=${workspace(1)}`);
  await page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true }).click();
  await page.getByLabel('วันเคลียร์ (วันปฏิทิน)').fill('17');
  await page.getByLabel('เหตุผล', { exact: true }).fill('ร่างยังไม่ส่ง');
  const before = checks;
  await expect.poll(() => checks, { timeout: 22000 }).toBeGreaterThan(before);
  await expect(page.getByLabel('วันเคลียร์ (วันปฏิทิน)')).toHaveValue('17');
  await expect(page.getByRole('textbox', { name: 'เหตุผล', exact: true })).toHaveValue('ร่างยังไม่ส่ง');
  await expect(page.getByRole('button', { name: 'บันทึกร่าง', exact: true })).toBeEnabled();
});

for (const [n, surface, removal] of [[9, 'root', 'revoke'], [10, 'details', 'revoke'], [11, 'root', 'expire'], [12, 'details', 'expire']] as const) {
  test(`final I3 idle policy ${surface} clears after ${removal}`, async ({ page, context, browser }) => {
    await session(context, 1, n);
    const reviewer = await browser.newContext(); await session(reviewer, 2, n);
    try {
      const company = await seed(context, reviewer, n);
      await page.goto(surface === 'root' ? root : `${root}/company?workspaceId=${workspace(n)}`);
      if (surface === 'root') await expect(page.getByRole('link', { name: `บริษัทจำลอง ${n} · กติกาบริษัท`, exact: true })).toBeVisible();
      else {
        await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).first().click();
        await expect(page.getByLabel('ชื่อหมวด 1', { exact: true })).toHaveValue('หมวดจำลอง');
        await expect(page.getByText(`ฉบับ ${company.id}`, { exact: true })).toBeVisible();
      }
      const user = `88000000-0000-4000-8000-${String(n * 100 + 1).padStart(12, '0')}`;
      // Independent backend change; no reload, focus change, auth broadcast or UI action.
      if (removal === 'revoke') fixtureSql(`UPDATE field_advance_grant_assignments a SET revoked_at_utc=now(),revoked_by='${user}',revocation_reason='ถอนจำลอง',version=a.version+1 FROM field_advance_grant_proposals p WHERE p.id=a.proposal_id AND p.recipient_id='${user}';`);
      else fixtureSql(`UPDATE field_advance_grant_proposals SET ends_at_utc=now()+interval '1 second' WHERE recipient_id='${user}';`);
      if (surface === 'root') {
        await expect(page.getByRole('link', { name: new RegExp(`บริษัทจำลอง ${n} ·`) })).toHaveCount(0, { timeout: 22000 });
      } else {
        await expect(page.getByRole('heading', { name: `บริษัทจำลอง ${n} · กติกาบริษัท`, exact: true })).toHaveCount(0, { timeout: 22000 });
        await expect(page.getByLabel('ชื่อหมวด 1', { exact: true })).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'สร้างฉบับใหม่', exact: true })).toHaveCount(0);
        await expect(page.getByText(`ฉบับ ${company.id}`, { exact: true })).toHaveCount(0);
      }
    } finally { await reviewer.close(); }
  });
}

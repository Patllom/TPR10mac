import { test, expect, type BrowserContext, type Page } from '@playwright/test';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
const root = '/portal/field-advances';
const api = '/api/v1/field-advances/requests';
test.setTimeout(120000);
const workspace = (n: number) => `88100000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const project = (n: number) => `88200000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const user = (n: number, actor: number) => `88000000-0000-4000-8000-${String(n * 100 + actor).padStart(12, '0')}`;
function sql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{64}$/.test(database)) throw new Error('ต้องใช้ disposable HTTPS fixture');
  execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input, stdio: ['pipe', 'ignore', 'pipe'] });
}
async function session(context: BrowserContext, n: number, actor: number) {
  const id = user(n, actor), token = randomBytes(32), hash = createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO mfa_factors(id,user_id,protected_secret,confirmed_at_utc) SELECT gen_random_uuid(),'${id}','fixture-unused',now() WHERE NOT EXISTS (SELECT 1 FROM mfa_factors WHERE user_id='${id}');
    INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc) VALUES ('${randomUUID()}','${id}',decode('${hash}','hex'),'Active',now(),now(),now()+interval '1 hour',now());`);
  await context.addCookies([{ name: '__Host-tpr10_session', value: token.toString('base64url'), domain: 'localhost', path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function policy(owner: BrowserContext, reviewer: BrowserContext, n: number) {
  const base = '/api/v1/field-advances/policies';
  async function send(c: BrowserContext, path: string, body: unknown) {
    const { token } = await (await c.request.get('/api/v1/auth/csrf')).json();
    const r = await c.request.post(base + path, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': token }, data: body });
    expect(r.ok()).toBe(true); return r.json();
  }
  const options = { mode: 'Company', sourcePolicyRevisionId: null, baseCompanyVersionId: null, expectedActiveRevisionId: null, expectedActiveVersion: 0, expectedContentHash: null as string | null };
  const content = { clearingDays: 7, bands: [{ order: 1, levelCode: 'director', upperInclusive: '100000.00' }], categories: [{ id: '88300000-0000-4000-8000-000000000001', name: 'ค่าเดินทางจำลอง', active: true, unit: 'เที่ยว', standardRate: '100.00', evidenceTypes: ['ใบเสร็จ'] }] };
  let row = await send(owner, '', { scope: { workspaceId: workspace(n), projectId: null }, baseCompanyVersionId: null, content, meta: { commandId: randomUUID(), expectedVersion: 1, reason: 'จำลอง', policy: options } });
  options.expectedContentHash = row.contentHash;
  row = await send(owner, `/${row.id}/submit`, { commandId: randomUUID(), expectedVersion: row.version, reason: 'จำลอง', policy: options });
  await send(reviewer, `/${row.id}/review`, { decision: 'approve', meta: { commandId: randomUUID(), expectedVersion: row.version, reason: 'จำลอง', policy: options } });
}
async function dwell(page: Page) {
  const current = await page.getByTestId('request-id').innerText();
  const polled = page.waitForResponse(r => r.url().endsWith(`${api}/${current}/screen`) && r.status() === 200);
  await page.clock.fastForward(16000);
  await polled;
  await expect(page.getByRole('main').getByRole('alert')).toHaveCount(0);
}
async function draft(page: Page, n: number, team = false) {
  await page.goto(`${root}/new?workspaceId=${workspace(n)}&projectId=${project(n)}`);
  await page.clock.install();
  await page.getByLabel('ผู้รับผิดชอบเงิน (ชื่อในระบบ)').selectOption({ label: `policy-fixture-${n}-0` });
  await page.getByLabel('รายละเอียดงาน').fill('สำรวจพื้นที่จำลอง');
  await page.getByLabel('วันเริ่มออกสนาม').fill('2026-10-24');
  await page.getByLabel('วันสิ้นสุดออกสนาม').fill('2026-10-25');
  await page.getByLabel('วันที่ต้องการเงิน').fill('2026-10-23');
  if (team) {
    await page.getByLabel('รูปแบบคำขอ').selectOption('Team');
    await page.getByLabel('ค้นหาสมาชิกทีม (ชื่อในระบบ)').fill(`policy-fixture-${n}-6`);
    await page.getByRole('button', { name: 'ค้นหาสมาชิกทีม', exact: true }).click();
    await page.getByLabel(`policy-fixture-${n}-6`, { exact: true }).check();
  }
  await page.getByLabel('จำนวน 1', { exact: true }).fill('1.2345');
  await page.getByLabel('รายละเอียดรายการ 1').fill('ออกสนาม');
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByText('ยอดจากเซิร์ฟเวอร์: 123.45 บาท', { exact: true })).toBeVisible();
  await dwell(page);
  return (await page.getByTestId('request-id').innerText()).trim();
}
async function confirm(page: Page) {
  await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
  await expect(page.getByRole('dialog')).toContainText('123.45');
  await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
  await expect(page.getByTestId('request-state')).toHaveText('รออนุมัติ');
  await dwell(page);
}
test('ยื่นแทน ทีมไม่มีสิทธิ์การเงิน เจ้าของยืนยัน และผ่านทุกขั้นก่อนรอจ่าย', async ({ page, context, browser }) => {
  const n = 13, owner = await browser.newContext(), supervisor = await browser.newContext();
  const manager = await browser.newContext(), director = await browser.newContext(), teammate = await browser.newContext();
  try {
    await session(owner, n, 1); await session(supervisor, n, 2); await policy(owner, supervisor, n);
    await session(context, n, 4); await session(manager, n, 3); await session(director, n, 5); await session(teammate, n, 7);
    const id = await draft(page, n, true);
    await expect(page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'ถอนคำขอ', exact: true })).toHaveCount(0);
    await page.getByRole('button', { name: 'ส่งให้ผู้รับผิดชอบ', exact: true }).click();
    await expect(page.getByTestId('request-state')).toHaveText('รอผู้รับผิดชอบยืนยัน'); await dwell(page);
    await page.goto(root);
    await expect(page.getByRole('combobox', { name: 'รายการ', exact: true })).toHaveValue('delegated');
    await expect(page.getByRole('link', { name: 'สำรวจพื้นที่จำลอง', exact: true })).toBeVisible();
    const own = await owner.newPage(); await own.goto(`${root}/${id}`); await own.clock.install(); await confirm(own);
    for (const c of [supervisor, manager, director]) {
      const review = await c.newPage(); await review.goto(`${root}/approvals`); await review.clock.install();
      await review.getByRole('link', { name: 'สำรวจพื้นที่จำลอง', exact: true }).click();
      await expect(review.getByRole('heading', { name: 'กติกาที่ตรึงไว้', exact: true })).toBeVisible();
      await review.getByRole('button', { name: 'อนุมัติขั้นนี้', exact: true }).click();
      // A completed assignee can lose read rights immediately; no stale receipt is rendered.
      await expect(review.getByRole('main').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
      await expect(review.getByTestId('request-id')).toHaveCount(0);
      await review.clock.fastForward(16000);
    }
    await own.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
    await expect(own.getByTestId('request-state')).toHaveText('อนุมัติแล้ว รอดำเนินการจ่าย'); await dwell(own);
    await expect(own.getByRole('button', { name: /จ่าย|บันทึกร่าง|ถอนคำขอ/ })).toHaveCount(0);
    const denied = await teammate.newPage(); await denied.goto(`${root}/${id}`);
    await expect(denied.getByRole('main').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
    await expect(denied.getByText('สำรวจพื้นที่จำลอง', { exact: true })).toHaveCount(0);
    await own.setViewportSize({ width: 390, height: 844 });
    expect(await own.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  } finally { for (const c of [owner, supervisor, manager, director, teammate]) await c.close(); }
});
test('ส่งกลับ แก้ฉบับ ยืนยันใหม่ และถอนด้วยเหตุผล', async ({ page, context, browser }) => {
  const n = 14, reviewer = await browser.newContext();
  try {
    await session(context, n, 1); await session(reviewer, n, 2); await policy(context, reviewer, n);
    const id = await draft(page, n); await confirm(page);
    const review = await reviewer.newPage(); await review.goto(`${root}/${id}`);
    await review.getByLabel('เหตุผลดำเนินการ').fill('เพิ่มรายละเอียดจุดสำรวจ');
    await review.getByRole('button', { name: 'ส่งกลับ', exact: true }).click();
    await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
    await expect(page.getByTestId('request-state')).toHaveText('ส่งกลับเพื่อแก้ไข');
    await page.getByRole('button', { name: 'สร้างฉบับแก้ไข', exact: true }).click();
    await expect(page.getByText('ฉบับที่ 2', { exact: true })).toBeVisible(); await dwell(page);
    await page.getByLabel('รายละเอียดงาน').fill('สำรวจพื้นที่จำลอง เพิ่มจุด');
    await expect(page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true })).toBeDisabled();
    await dwell(page);
    await expect(page.getByLabel('รายละเอียดงาน')).toHaveValue('สำรวจพื้นที่จำลอง เพิ่มจุด');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click(); await dwell(page);
    await expect(page.getByText('รายละเอียดงาน: สำรวจพื้นที่จำลอง → สำรวจพื้นที่จำลอง เพิ่มจุด', { exact: true })).toBeVisible();
    await confirm(page);
    await expect(page.getByText('ขั้นปัจจุบัน: supervisor', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'ถอนคำขอ', exact: true }).click();
    await page.getByRole('dialog').getByLabel('เหตุผลถอนคำขอ').fill('ยกเลิกการออกสนาม');
    await page.getByRole('button', { name: 'ยืนยันถอนคำขอ', exact: true }).click();
    await expect(page.getByTestId('request-state')).toHaveText('ถอนแล้ว'); await dwell(page);
    await expect(page.getByText('ยกเลิกการออกสนาม', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'สร้างฉบับแก้ไข', exact: true })).toHaveCount(0);
  } finally { await reviewer.close(); }
});
test('ทิ้งผลคำสั่งที่กลับมาหลังเปลี่ยนบัญชี', async ({ page, context, browser }) => {
  const n = 18, reviewer = await browser.newContext();
  try {
    await session(context, n, 1); await session(reviewer, n, 2); await policy(context, reviewer, n);
    const id = await draft(page, n);
    await page.route(`**${api}/${id}`, async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      const response = await route.fetch();
      await session(context, n, 7);
      await route.fulfill({ response });
    });
    await page.getByLabel('รายละเอียดงาน').fill('ผลที่ห้ามเปิดแก่บัญชีใหม่');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
    await expect(page.getByRole('main').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
    await expect(page.getByTestId('request-id')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'ตรวจผลด้วยคำสั่งเดิม', exact: true })).toHaveCount(0);
    await expect(page.getByLabel('รายละเอียดงาน')).toHaveCount(0);
  } finally { await reviewer.close(); }
});
test('ผลเครือข่ายไม่ทราบเก็บคำสั่งเดิมและไม่ส่งซ้ำอัตโนมัติ', async ({ page, context, browser }) => {
  const n = 15, reviewer = await browser.newContext();
  try {
    await session(context, n, 1); await session(reviewer, n, 2); await policy(context, reviewer, n);
    const sent: string[] = [];
    await page.route(`**${api}`, async route => {
      if (route.request().method() !== 'POST') return route.continue();
      sent.push(route.request().postData()!);
      if (sent.length === 1) { await route.fetch(); await route.abort('failed'); }
      else await route.continue();
    });
    await page.goto(`${root}/new?workspaceId=${workspace(n)}&projectId=${project(n)}`); await page.clock.install();
    await page.getByLabel('ผู้รับผิดชอบเงิน (ชื่อในระบบ)').selectOption({ label: `policy-fixture-${n}-0` });
    await page.getByLabel('รายละเอียดงาน').fill('ทดสอบผลไม่ทราบ');
    for (const label of ['วันเริ่มออกสนาม', 'วันสิ้นสุดออกสนาม', 'วันที่ต้องการเงิน']) await page.getByLabel(label).fill('2026-10-24');
    await page.getByLabel('จำนวน 1', { exact: true }).fill('1'); await page.getByLabel('รายละเอียดรายการ 1').fill('ออกสนาม');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
    await expect(page.getByRole('main').getByRole('alert')).toContainText('ไม่ทราบผล');
    await page.clock.fastForward(16000); expect(sent).toHaveLength(1);
    await expect(page.getByRole('button', { name: 'บันทึกร่าง', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'ตรวจผลด้วยคำสั่งเดิม', exact: true }).click();
    await expect(page.getByTestId('request-state')).toHaveText('ร่าง');
    expect(sent).toHaveLength(2); expect(sent[1]).toBe(sent[0]);
    const rows = await (await context.request.get(`${api}?workspaceId=${workspace(n)}&projectId=${project(n)}`)).json(); expect(rows).toHaveLength(1);
  } finally { await reviewer.close(); }
});
for (const [n, failure] of [[16, 'account'], [17, 'session']] as const) test(`หน้าเปิดค้างล้างข้อมูลเมื่อ ${failure}`, async ({ page, context, browser }) => {
  const reviewer = await browser.newContext();
  try {
    await session(context, n, 1); await session(reviewer, n, 2); await policy(context, reviewer, n);
    await draft(page, n);
    await page.getByLabel('รายละเอียดงาน').fill('ร่างลับยังไม่ส่ง');
    if (failure === 'account') await session(context, n, 7);
    else sql(`UPDATE sessions SET expires_at_utc=now()-interval '1 second' WHERE user_id='${user(n, 1)}';`);
    await page.clock.fastForward(16000);
    await expect(page.getByRole('main').getByRole('alert')).toContainText(/ไม่มีสิทธิ์|session หมดอายุ/);
    await expect(page.getByTestId('request-id')).toHaveCount(0);
    await expect(page.getByLabel('รายละเอียดงาน')).toHaveCount(0);
    expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);
  } finally { await reviewer.close(); }
});
test('ไม่มีผู้อนุมัติให้พักและแสดงเหตุผลจากเซิร์ฟเวอร์', async ({ page, context, browser }) => {
  const n = 19, reviewer = await browser.newContext();
  try {
    await session(context, n, 1); await session(reviewer, n, 2); await policy(context, reviewer, n);
    await draft(page, n);
    sql(`UPDATE reporting_lines SET valid_to_utc=now() WHERE employee_user_id='${user(n, 1)}';`);
    await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
    await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
    await expect(page.getByTestId('request-state')).toHaveText('พักสายอนุมัติ');
    await expect(page.getByText('เหตุผลพักสายอนุมัติ: ไม่มีผู้อนุมัติที่เข้าเงื่อนไข', { exact: true })).toBeVisible();
    await dwell(page);
    await expect(page.getByRole('button', { name: 'อนุมัติขั้นนี้', exact: true })).toHaveCount(0);
  } finally { await reviewer.close(); }
});

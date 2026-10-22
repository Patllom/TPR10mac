import { test, expect, type BrowserContext } from '@playwright/test';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';
const root = '/portal/field-advances/settings';
const api = '/api/v1/field-advances/grants';
const workspace = (n: number) => `88100000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const project = (n: number) => `88200000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const user = (n: number, actor: number) => `88000000-0000-4000-8000-${String(n * 100 + actor).padStart(12, '0')}`;
const query = (n: number) => `workspaceId=${workspace(n)}&projectId=${project(n)}`;
function sql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{64}$/.test(database)) throw new Error('ต้องใช้ disposable HTTPS fixture');
  execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input, stdio: ['pipe', 'ignore', 'pipe'] });
}
async function session(context: BrowserContext, actor: number, n: number) {
  const token = randomBytes(32); const hash = createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO mfa_factors(id,user_id,protected_secret,confirmed_at_utc) SELECT gen_random_uuid(),'${user(n, actor)}','fixture-only-unused',now() WHERE NOT EXISTS (SELECT 1 FROM mfa_factors WHERE user_id='${user(n, actor)}');
    INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc) VALUES ('${randomUUID()}','${user(n, actor)}',decode('${hash}','hex'),'Active',now(),now(),now()+interval '1 hour',now());`);
  await context.addCookies([{ name: '__Host-tpr10_session', value: token.toString('base64url'), domain: 'localhost', path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function send(context: BrowserContext, path: string, body: unknown) {
  const { token } = await (await context.request.get('/api/v1/auth/csrf')).json();
  return context.request.post(api + path, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': token }, data: body });
}
const meta = (version = 1) => ({ commandId: randomUUID(), expectedVersion: version, reason: 'ทดสอบวงจรสิทธิ์' });
const spec = (n: number) => ({ recipientId: user(n, 3), scope: { workspaceId: workspace(n), projectId: project(n) }, capabilities: ['policy.edit.project'], delegableCapabilities: [], approvalLimit: null, startsAt: new Date(Date.now() - 60000).toISOString(), endsAt: null });

test('สามคนเสนอ ตรวจ เริ่มใช้ ถอนแล้ว session เดิมหมดสิทธิ์ พร้อมประวัติและ diff', async ({ page, context, browser }) => {
  test.setTimeout(90000);
  const n = 9; await session(context, 1, n);
  const reviewer = await browser.newContext(), recipient = await browser.newContext();
  try {
    await session(reviewer, 2, n); await session(recipient, 3, n);
    await page.goto(`${root}/grants?${query(n)}`);
    await page.getByRole('button', { name: 'เสนอสิทธิ์ใหม่', exact: true }).click();
    await page.getByLabel('ค้นหาผู้รับ').fill('policy-fixture-9-2');
    await page.getByRole('button', { name: 'ค้นหาผู้รับ', exact: true }).click();
    await page.getByLabel('ผู้รับสิทธิ์').selectOption(user(n, 3));
    await page.getByLabel('ใช้ policy.edit.project', { exact: true }).check();
    await page.getByLabel('เหตุผล', { exact: true }).fill('ให้ทำกติกาโครงการ');
    await page.getByRole('button', { name: 'ส่งข้อเสนอ', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('เสนอสำเร็จ');
    const pending = (await (await reviewer.request.get(`${api}/review-queue?${query(n)}`)).json())[0];
    expect((await send(recipient, `/${pending.id}/review`, { decision: 'approve', meta: meta() })).status()).toBe(403);
    expect((await recipient.request.get(`/api/v1/field-advances/policies?${query(n)}`)).status()).toBe(403);
    const reviewPage = await reviewer.newPage();
    await reviewPage.goto(`${root}/reviews?domain=grants&${query(n)}`);
    await reviewPage.getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).first().click();
    await reviewPage.getByLabel('เหตุผล', { exact: true }).fill('ตรวจครบสามคน');
    await reviewPage.getByRole('button', { name: 'อนุมัติสิทธิ์', exact: true }).click();
    await expect(reviewPage.getByRole('status')).toContainText('อนุมัติสำเร็จ');
    expect((await recipient.request.get(`/api/v1/field-advances/policies?${query(n)}`)).status()).toBe(200);
    const recipientPage = await recipient.newPage(); await recipientPage.goto(`${root}/projects/${project(n)}?workspaceId=${workspace(n)}`);
    await expect(recipientPage.getByRole('heading', { name: `บริษัทจำลอง ${n} · โครงการจำลอง ${n}`, exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'โหลดสิทธิ์ใหม่', exact: true }).click();
    await page.getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).first().click();
    await expect(page.getByText('เริ่มใช้แล้ว', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'เตรียมข้อเสนอใหม่จากรายการนี้', exact: true }).click();
    await page.getByLabel('วงเงินอำนาจ (บาท)').fill('9999999999999999.99');
    await expect(page.getByText(/วงเงิน: ไม่มีอำนาจอนุมัติยอดเงิน → 9999999999999999.99/)).toBeVisible();
    await expect(page.getByText(/สิทธิ์เดิมยังคงอยู่/)).toBeVisible();
    await page.getByRole('button', { name: 'ยกเลิกการเตรียมข้อเสนอ', exact: true }).click();
    await page.getByLabel('เหตุผล', { exact: true }).fill('ถอนสิทธิ์ทดสอบ');
    await page.getByRole('button', { name: 'ถอนสิทธิ์', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('ถอนสำเร็จ');
    expect((await recipient.request.get(`/api/v1/field-advances/policies?${query(n)}`)).status()).toBe(403);
    await recipientPage.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
    await expect(recipientPage.getByRole('heading', { name: `บริษัทจำลอง ${n} · โครงการจำลอง ${n}`, exact: true })).toHaveCount(0);
    await page.goto(`${root}/history?${query(n)}`); await page.getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).first().click();
    await expect(page.getByRole('table', { name: 'ประวัติสิทธิ์' })).toContainText('ถอนสิทธิ์ทดสอบ');
    expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);
  } finally { await reviewer.close(); await recipient.close(); }
});

test('คิวแยกสิทธิ์ ผู้ตรวจไม่พอค้างรอ picker ไม่เผย alias หรือต่างบริษัท', async ({ page, context, browser }) => {
  const n = 10; await session(context, 1, n);
  const picker = await (await context.request.get(`${api}/recipients?${query(n)}`)).json();
  expect(picker.some((r: { id: string }) => r.id === user(n, 3))).toBe(true);
  expect(picker.some((r: { id: string }) => [user(n, 1), user(n, 6), user(9, 3)].includes(r.id))).toBe(false);
  expect((await context.request.get(`${api}/review-queue?${query(n)}`)).status()).toBe(403);
  const pending = await (await send(context, '', { spec: spec(n), meta: meta() })).json();
  sql(`UPDATE field_advance_grant_proposals SET capabilities=ARRAY['report.read'] WHERE recipient_id='${user(n, 2)}' AND capabilities @> ARRAY['grant.review'];`);
  const reviewer = await browser.newContext();
  try {
    await session(reviewer, 2, n);
    expect((await send(reviewer, `/${pending.id}/review`, { decision: 'approve', meta: meta() })).status()).toBe(403);
    await page.goto(`${root}/grants?${query(n)}`); await page.getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).first().click();
    await expect(page.getByText('รอตรวจ', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'อนุมัติสิทธิ์', exact: true })).toBeDisabled();
    const row = await (await context.request.get(`${api}/${pending.id}?${query(n)}`)).json(); expect(row.assignmentId).toBeNull();
  } finally { await reviewer.close(); }
});

test('ส่งกลับต้องเสนอใหม่ ต่ออายุผ่านตรวจใหม่และไม่แทนสิทธิ์เดิม', async ({ page, context, browser }) => {
  const n = 11; await session(context, 1, n); const reviewer = await browser.newContext();
  try {
    await session(reviewer, 2, n);
    const originalEnd = '2030-01-01T00:00:00Z';
    const first = await (await send(context, '', { spec: { ...spec(n), endsAt: originalEnd }, meta: meta() })).json();
    expect((await send(reviewer, `/${first.id}/review`, { decision: 'return', meta: meta() })).status()).toBe(200);
    await page.goto(`${root}/grants?${query(n)}`); await page.getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).first().click();
    await page.getByRole('button', { name: 'เตรียมข้อเสนอใหม่จากรายการนี้', exact: true }).click();
    await page.getByLabel('เหตุผล', { exact: true }).fill('เสนอใหม่หลังส่งกลับ');
    await page.getByRole('button', { name: 'ส่งข้อเสนอ', exact: true }).click(); await expect(page.getByRole('status')).toContainText('เสนอสำเร็จ');
    const pending = (await (await reviewer.request.get(`${api}/review-queue?${query(n)}`)).json())[0]; expect(pending.id).not.toBe(first.id);
    expect((await send(reviewer, `/${pending.id}/review`, { decision: 'approve', meta: meta() })).status()).toBe(200);
    const original = await (await context.request.get(`${api}/${pending.id}?${query(n)}`)).json();
    await page.getByRole('button', { name: 'โหลดสิทธิ์ใหม่', exact: true }).click();
    await page.locator('article').filter({ hasText: pending.id }).getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).click();
    await page.getByRole('button', { name: 'เตรียมข้อเสนอใหม่จากรายการนี้', exact: true }).click();
    await page.getByLabel('สิ้นสุด (Asia/Bangkok)').fill('2030-02-01T07:00');
    await expect(page.getByText(/สิ้นสุด:.*2030-01-01.*→.*2030-02-01/)).toBeVisible();
    await page.getByLabel('เหตุผล', { exact: true }).fill('ต่ออายุจริงอีกหนึ่งเดือน');
    await page.getByRole('button', { name: 'ส่งข้อเสนอ', exact: true }).click();
    await expect(page.getByRole('status')).toContainText('เสนอสำเร็จ');
    const another = (await (await reviewer.request.get(`${api}/review-queue?${query(n)}`)).json())[0];
    expect(Date.parse(another.spec.endsAt)).toBe(Date.parse('2030-02-01T00:00:00Z'));
    expect(another.assignmentId).toBeNull();
    expect((await send(reviewer, `/${another.id}/review`, { decision: 'approve', meta: meta() })).status()).toBe(200);
    const rows = await (await context.request.get(`${api}?${query(n)}`)).json();
    expect(rows.filter((r: { effectiveState: string; spec: { recipientId: string } }) => r.effectiveState === 'Active' && r.spec.recipientId === user(n, 3))).toHaveLength(2);
    expect(rows.find((r: { id: string }) => r.id === first.id).state).toBe('Draft');
    const retained = rows.find((r: { id: string }) => r.id === pending.id);
    expect(retained.assignmentId).toBe(original.assignmentId);
    expect(retained.assignmentVersion).toBe(original.assignmentVersion);
    expect(Date.parse(retained.spec.endsAt)).toBe(Date.parse(originalEnd));
    expect(rows.find((r: { id: string }) => r.id === another.id).assignmentId).not.toBe(original.assignmentId);
  } finally { await reviewer.close(); }
});

for (const mode of ['details', 'draft', 'pending'] as const) test(`M1 timer รับสถานะและปุ่มตามเวลารุ่นเดิม ${mode}`, async ({ page, context, browser }) => {
  test.setTimeout(65000);
  const n = 11;
  await session(context, mode === 'pending' ? 2 : 1, n);
  const other = await browser.newContext();
  try {
    await session(other, mode === 'pending' ? 1 : 2, n);
    const proposer = mode === 'pending' ? other : context;
    const reviewer = mode === 'pending' ? context : other;
    const proposed = await send(proposer, '', { spec: { ...spec(n), endsAt: new Date(Date.now() + 30000).toISOString() }, meta: meta() });
    expect(proposed.status()).toBe(201);
    const row = await proposed.json();
    if (mode !== 'pending') expect((await send(reviewer, `/${row.id}/review`, { decision: 'approve', meta: meta() })).status()).toBe(200);
    const before = await (await context.request.get(`${api}/${row.id}?${query(n)}`)).json();
    await page.goto(`${root}/grants?${query(n)}`);
    await page.locator('article').filter({ hasText: row.id }).getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).click();
    const details = page.locator('section').filter({ has: page.getByRole('heading', { name: 'รายละเอียดสิทธิ์', exact: true }) });
    if (mode === 'pending') await expect(page.getByRole('button', { name: 'อนุมัติสิทธิ์', exact: true })).toBeEnabled();
    else await expect(details.getByText('เริ่มใช้แล้ว', { exact: true })).toBeVisible();
    if (mode === 'draft') {
      await page.getByRole('button', { name: 'เตรียมข้อเสนอใหม่จากรายการนี้', exact: true }).click();
      await page.getByLabel('เหตุผล', { exact: true }).fill('ร่างยังไม่ส่งเมื่อสิทธิ์เดิมหมดอายุ');
      await page.getByLabel('สิ้นสุด (Asia/Bangkok)').fill('2030-02-01T07:00');
    }
    const refreshed = await page.waitForResponse(async response => {
      if (!response.url().includes(`${api}/${row.id}?`) || response.status() !== 200) return false;
      const data = await response.json();
      return mode === 'pending' ? !data.canApprove : data.effectiveState === 'Expired';
    }, { timeout: 48000 });
    const after = await refreshed.json();
    expect(after.version).toBe(before.version);
    expect(after.assignmentVersion).toBe(before.assignmentVersion);
    if (mode === 'draft') {
      await expect(page.getByLabel('เหตุผล', { exact: true })).toHaveValue('ร่างยังไม่ส่งเมื่อสิทธิ์เดิมหมดอายุ');
      await expect(page.getByLabel('สิ้นสุด (Asia/Bangkok)')).toHaveValue('2030-02-01T07:00');
      await page.getByRole('button', { name: 'ยกเลิกการเตรียมข้อเสนอ', exact: true }).click();
    }
    if (mode === 'pending') await expect(page.getByRole('button', { name: 'อนุมัติสิทธิ์', exact: true })).toBeDisabled();
    else await expect(details.getByText('หมดอายุ', { exact: true })).toBeVisible();
  } finally { await other.close(); }
});

test('เปลี่ยนบทบาทแล้วหน้าจอและ API ล้าง grant data ใน session เดิม', async ({ page, context }) => {
  const n = 12; await session(context, 1, n);
  const row = await (await send(context, '', { spec: spec(n), meta: meta() })).json();
  await page.goto(`${root}/grants?${query(n)}`); await page.getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).first().click();
  await expect(page.getByText('policy-fixture-12-2', { exact: true }).last()).toBeVisible();
  sql(`UPDATE field_advance_grant_proposals SET capabilities=ARRAY['report.read'] WHERE recipient_id='${user(n, 1)}' AND capabilities @> ARRAY['grant.propose'];`);
  await page.getByRole('button', { name: 'โหลดสิทธิ์ใหม่', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'ไม่มีสิทธิ์' })).toBeVisible();
  await expect(page.getByText('policy-fixture-12-2', { exact: true })).toHaveCount(0);
  expect((await context.request.get(`${api}/${row.id}?${query(n)}`)).status()).toBe(403);
  expect((await context.request.get(`${api}/${row.id}/history?${query(n)}`)).status()).toBe(403);
});

for (const [screen, n] of [['grants', 9], ['history', 11], ['reviews', 12]] as const) test(`I1 timer ล้างชื่อขอบเขต ${screen} root เมื่อถอนอำนาจโดยไม่ reload`, async ({ page, context }) => {
  test.setTimeout(40000);
  await session(context, 2, n);
  await page.goto(`${root}/${screen}${screen === 'reviews' ? '?domain=grants' : ''}`);
  const scopeLink = page.getByRole('link', { name: `บริษัทจำลอง ${n} · โครงการจำลอง ${n}`, exact: true });
  await expect(scopeLink).toBeVisible();
  let discoveryReads = 0;
  page.on('request', request => { if (request.url().includes(`${api}/scopes?`)) discoveryReads++; });
  // External authority change: leave the same document foreground, with no navigation or focus event.
  sql(`UPDATE field_advance_grant_proposals SET capabilities=ARRAY['report.read'] WHERE recipient_id='${user(n, 2)}' AND capabilities @> ARRAY['grant.review'];`);
  expect(await (await context.request.get(`${api}/scopes`)).json()).toEqual([]);
  await expect(scopeLink).toHaveCount(0, { timeout: 22000 });
  expect(discoveryReads).toBeGreaterThan(0);
  await expect(page.getByText('ยังไม่มีสิทธิ์จัดการการมอบหมาย', { exact: true })).toBeVisible();
});

test('QA 390px labels และ keyboard รักษาร่างระหว่าง timer', async ({ page, context }) => {
  test.setTimeout(45000);
  await session(context, 1, 11);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto(`${root}/grants?${query(11)}`);
  await page.getByRole('button', { name: 'เสนอสิทธิ์ใหม่', exact: true }).click();
  const search = page.getByLabel('ค้นหาผู้รับ', { exact: true });
  await search.focus(); await page.keyboard.type('policy-fixture-11-2');
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'ค้นหาผู้รับ', exact: true })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('option', { name: 'policy-fixture-11-2', exact: true })).toHaveCount(1);
  await page.getByRole('combobox', { name: 'ผู้รับสิทธิ์', exact: true }).selectOption(user(11, 3));
  const capability = page.getByLabel('ใช้ policy.edit.project', { exact: true });
  await capability.focus(); await page.keyboard.press('Space'); await expect(capability).toBeChecked();
  const reason = page.getByLabel('เหตุผล', { exact: true });
  await reason.focus(); await page.keyboard.type('keyboard draft fixture');
  const refreshed = page.waitForResponse(response => response.url().includes(`${api}/scopes?`) && response.status() === 200, { timeout: 22000 });
  await refreshed;
  await expect(reason).toHaveValue('keyboard draft fixture');
  await expect(capability).toBeChecked();
  expect(await page.locator('input, select').evaluateAll(elements => elements.every(element => !!(element as HTMLInputElement).labels?.length))).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: resolve('.superpowers/sdd/2026-10-19-module-7a-policy-permissions/task-10-390px-keyboard.png'), fullPage: true });
  await page.getByRole('button', { name: 'ยกเลิกการเตรียมข้อเสนอ', exact: true }).click();
  await page.getByRole('button', { name: 'เปิดสิทธิ์', exact: true }).first().click();
  await expect(page.getByRole('table', { name: 'ประวัติสิทธิ์' })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: resolve('.superpowers/sdd/2026-10-19-module-7a-policy-permissions/task-10-390px-history.png'), fullPage: true });
});

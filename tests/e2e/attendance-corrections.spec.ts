import { test, expect, type Page, type BrowserContext } from '@playwright/test';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

const id = '55555555-5555-4555-8555-555555555555';
const site = { workspaceId: '11111111-1111-4111-8111-111111111111', projectId: '22222222-2222-4222-8222-222222222222', siteId: '33333333-3333-4333-8333-333333333333' };
const proposal = { workDate: '2026-10-17', inUtc: '2026-10-17T01:00:00Z', outUtc: '2026-10-17T10:00:00Z', inSite: site, outSite: site, reason: 'ลืมลงเวลาทดสอบ' };
const initial = { id, employeeId: id, originalWorkDate: '2026-10-17', version: 1, state: 0, deadlineUtc: '2026-10-24T17:00:00Z', currentRevision: 1, proposal, baselineDayVersion: null, actions: ['save', 'submit'], readBasis: 'Own' };
async function session(context: BrowserContext, username = 'e2e-scope-staff') {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{12,64}$/.test(database)) throw new Error('isolated fixture required');
  const raw = randomBytes(32), hash = createHash('sha256').update(raw).digest('hex');
  execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1'], { input: `INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,security_version,mfa_verified_at_utc) SELECT '${randomUUID()}',id,decode('${hash}','hex'),'Active',now(),now(),now()+interval '8 hours',security_version,now() FROM users WHERE username='${username}';`, encoding: 'utf8' });
  await context.addCookies([{ name: '__Host-tpr10_session', value: raw.toString('base64url'), domain: 'localhost', path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function setup(page: Page, context: BrowserContext, state = 0) {
  await session(context);
  let value = { ...initial, state, actions: state === 5 ? [] : initial.actions };
  const calls: { method: string; path: string; body: Record<string, unknown> | null }[] = [];
  await page.route('**/api/v1/scopes?*', r => r.fulfill({ json: { items: [{ scope: site, capabilities: ['attendance:record'], workspaceName: 'หน่วยงาน', projectName: 'โครงการ', siteName: 'สำนักงาน' }], total: 1, pageNumber: 1, pageSize: 100 } }));
  await page.route('**/api/v1/attendance/corrections**', async r => {
    const path = new URL(r.request().url()).pathname, method = r.request().method();
    const body = method === 'POST' ? r.request().postDataJSON() : null;
    calls.push({ method, path, body });
    if (method === 'GET') {
      if (path.endsWith(id)) return r.fulfill({ json: { ...value, comparison: { dayId: null, current: null, original: null, baselineMatches: true }, history: { revisions: [], routes: [], approvals: [], reroutes: [] } } });
      return r.fulfill({ json: { items: [value], total: 1, pageNumber: 1, pageSize: 20 } });
    }
    const command = body.command ?? body;
    if (path.endsWith('/submit')) value = { ...value, state: 1, version: value.version + 1, actions: [] };
    if (path.endsWith('/return')) value = { ...value, state: 3, version: value.version + 1, actions: ['save', 'submit'] };
    if (path.endsWith('/revisions')) value = { ...value, proposal: body.input.proposal, currentRevision: value.currentRevision + 1, version: value.version + 1 };
    if (path.endsWith('/supervisor-approve')) value = { ...value, state: 2, version: value.version + 1, actions: ['hr-approve', 'return'], readBasis: 'WorkflowHr' };
    if (path.endsWith('/hr-approve')) value = { ...value, state: 4, version: value.version + 1, actions: [] };
    return r.fulfill({ status: path.endsWith('/corrections') ? 201 : 200, json: { id, commandId: command.commandId } });
  });
  await page.goto('/portal/attendance/corrections');
  await expect(page.getByRole('heading', { name: 'คำร้องแก้ไขเวลา', exact: true })).toBeVisible();
  return { calls, change: (next: Partial<typeof value>) => { value = { ...value, ...next }; } };
}
test('แก้เฉพาะเหตุผลไม่เปลี่ยนวินาทีและเศษวินาทีเดิม', async ({ page, context }) => {
  const fixture = await setup(page, context);
  fixture.change({ proposal: { ...proposal, inUtc: '2026-10-17T01:00:45.123456Z', outUtc: '2026-10-17T10:00:59.456789Z' } });
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByLabel('เหตุผลคำร้อง').fill('แก้เฉพาะเหตุผล');
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  const saved = fixture.calls.find(x => x.path.endsWith('/revisions'))!.body as { input: { proposal: typeof proposal } };
  expect(saved.input.proposal.inUtc).toBe('2026-10-17T01:00:45.123456Z');
  expect(saved.input.proposal.outUtc).toBe('2026-10-17T10:00:59.456789Z');
});
test('ผู้จัดเส้นทางเปิดคิวคำร้องค้างและส่งเริ่มหัวหน้าใหม่ได้', async ({ page, context }) => {
  const fixture = await setup(page, context);
  fixture.change({ state: 1, actions: ['reroute'], readBasis: 'WorkflowRouting' });
  await page.goto('/portal/attendance/approvals');
  await page.getByLabel('ขั้นพิจารณา').selectOption('routing');
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await expect(page.getByRole('button', { name: 'อนุมัติ', exact: true })).toHaveCount(0);
  await page.getByLabel('เหตุผลส่งกลับหรือจัดเส้นทาง').fill('เปลี่ยนหัวหน้า');
  await page.getByRole('button', { name: 'จัดเส้นทางใหม่', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  expect(fixture.calls.some(x => x.method === 'POST' && x.path.endsWith('/reroute'))).toBe(true);
});
test('คำร้องขาดทั้งคู่เลือกสถานที่เก่าตามเวลาที่เสนอได้', async ({ page, context }) => {
  const fixture = await setup(page, context);
  const oldSite = { ...site, siteId: '77777777-7777-4777-8777-777777777777' };
  await page.route('**/api/v1/attendance/corrections/sites?*', r => r.fulfill({ json: {
    items: [{ site: oldSite, label: 'สถานที่เก่าที่เคยได้รับมอบหมาย' }], total: 1, pageNumber: 1, pageSize: 100, atUtc: new URL(r.request().url()).searchParams.get('at')
  } }));
  await page.getByRole('button', { name: 'สร้างคำร้อง', exact: true }).click();
  await page.getByLabel('วันทำงานที่ขอแก้').fill('2026-10-17');
  await page.getByLabel('เวลาเข้า (ประเทศไทย)').fill('2026-10-17T08:00');
  await page.getByLabel('เวลาออก (ประเทศไทย)').fill('2026-10-17T17:00');
  await page.getByRole('button', { name: 'โหลดสถานที่ตามเวลาที่เสนอ', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'โหลดสถานที่แล้ว' })).toBeVisible();
  await page.getByLabel('สถานที่เข้า').selectOption(oldSite.siteId);
  await page.getByLabel('สถานที่ออก').selectOption(oldSite.siteId);
  await page.getByLabel('เหตุผลคำร้อง').fill('ย้ายสถานที่แล้ว แต่ขอแก้ของวันก่อน');
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  const sent = fixture.calls.find(x => x.method === 'POST')!.body as { input: { proposal: typeof proposal } };
  expect(sent.input.proposal.inSite).toEqual(oldSite);
  expect(sent.input.proposal.outSite).toEqual(oldSite);
});
test('สถานที่ย้อนหลังตอบช้าหลังเปลี่ยนบัญชีต้องไม่แสดงข้อมูลเดิม', async ({ page, context }) => {
  await setup(page, context); let release!: () => void, waiting = false;
  const held = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/attendance/corrections/sites?*', async r => {
    waiting = true; await held;
    await r.fulfill({ json: { items: [{ site, label: 'สถานที่ส่วนตัวบัญชีเดิม' }], total: 1, pageNumber: 1, pageSize: 100, atUtc: new URL(r.request().url()).searchParams.get('at') } }).catch(() => {});
  });
  await page.getByRole('button', { name: 'สร้างคำร้อง', exact: true }).click();
  await page.getByLabel('เวลาเข้า (ประเทศไทย)').fill('2026-10-17T08:00');
  await page.getByRole('button', { name: 'โหลดสถานที่ตามเวลาที่เสนอ', exact: true }).click();
  await expect.poll(() => waiting).toBe(true);
  await session(context, 'e2e-other');
  await page.route('**/api/v1/attendance/corrections?*', r => r.fulfill({ json: { items: [], total: 0, pageNumber: 1, pageSize: 20 } }));
  release();
  await expect(page.getByText('ไม่มีคำร้องในคิวนี้')).toBeVisible();
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
  await expect(page.getByText('สถานที่ส่วนตัวบัญชีเดิม')).toHaveCount(0);
});
test('สร้างร่างด้วยเวลาไทยและส่งคำร้องโดยไม่สร้างรูปย้อนหลัง', async ({ page, context }) => {
  const fixture = await setup(page, context);
  await page.getByRole('button', { name: 'สร้างคำร้อง', exact: true }).click();
  await page.getByLabel('วันทำงานที่ขอแก้').fill('2026-10-17');
  await page.getByLabel('เวลาเข้า (ประเทศไทย)').fill('2026-10-17T08:00');
  await page.getByLabel('เวลาออก (ประเทศไทย)').fill('2026-10-17T17:00');
  await page.getByLabel('สถานที่เข้า').selectOption(site.siteId);
  await page.getByLabel('สถานที่ออก').selectOption(site.siteId);
  await page.getByLabel('เหตุผลคำร้อง').fill('ลืมลงเวลาทดสอบ');
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  const create = fixture.calls.find(x => x.method === 'POST')!.body! as { input: { proposal: typeof proposal } };
  expect(create.input.proposal.inUtc).toBe('2026-10-17T01:00:00.000Z');
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await expect(page.getByText('ไม่มีหลักฐานถ่ายสดในรายการนี้')).toBeVisible();
  await expect(page.locator('img')).toHaveCount(0);
  await page.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  expect(fixture.calls.filter(x => x.method === 'POST')).toHaveLength(2);
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);
});
test('หมดอายุอ่านอย่างเดียวและมือถือใช้แป้นพิมพ์ได้ไม่ล้น', async ({ page, context }) => {
  await setup(page, context, 5);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByText('หมดอายุ', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true })).toHaveCount(0);
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});
test('ซ่อนแท็บล้างรายละเอียดและเหตุผลที่กำลังพิมพ์', async ({ page, context }) => {
  await setup(page, context);
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByLabel('เหตุผลคำร้อง').fill('ข้อความส่วนตัวที่ต้องล้าง');
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
  await expect(page.getByText('ลืมลงเวลาทดสอบ', { exact: true })).toHaveCount(0);
});
test('409 ล้างแบบแก้ไขและบังคับโหลดใหม่ไม่เขียนทับ', async ({ page, context }) => {
  const fixture = await setup(page, context);
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.route('**/api/v1/attendance/corrections/*/revisions', r => r.fulfill({ status: 409, json: {} }));
  await page.getByLabel('เหตุผลคำร้อง').fill('แก้เหตุผล');
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'โหลดใหม่' })).toBeVisible();
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
  expect(fixture.calls.filter(x => x.method === 'POST')).toHaveLength(0);
});
test('หัวหน้าส่งกลับบังคับเหตุผล พนักงานแก้ส่งใหม่แล้วอนุมัติสองขั้นโดยแก้แทนไม่ได้', async ({ page, context }) => {
  const fixture = await setup(page, context);
  fixture.change({ state: 1, actions: ['supervisor-approve', 'return'], readBasis: 'WorkflowSupervisor' });
  await page.goto('/portal/attendance/approvals');
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await expect(page.getByText(`รหัสพนักงาน: ${id}`, { exact: true })).toBeVisible();
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'ส่งกลับแก้ไข', exact: true })).toBeDisabled();
  await page.getByLabel('เหตุผลส่งกลับหรือจัดเส้นทาง').fill('ตรวจเวลาอีกครั้ง');
  await page.getByRole('button', { name: 'ส่งกลับแก้ไข', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  fixture.change({ readBasis: 'Own' });
  await page.goto('/portal/attendance/corrections');
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByLabel('เหตุผลคำร้อง').fill('แก้ไขตามหัวหน้า');
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  fixture.change({ actions: ['supervisor-approve', 'return'], readBasis: 'WorkflowSupervisor' });
  await page.goto('/portal/attendance/approvals');
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByRole('button', { name: 'หัวหน้าอนุมัติ', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  await page.getByLabel('ขั้นพิจารณา').selectOption('hr');
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByRole('button', { name: 'HR อนุมัติ', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  expect(fixture.calls.filter(x => x.method === 'POST').map(x => x.path.split('/').at(-1))).toEqual(['return', 'revisions', 'submit', 'supervisor-approve', 'hr-approve']);
});
test('response หายต้อง GET ก่อนลองคำสั่งเดิมและไม่ใช้ commandId ใหม่', async ({ page, context }) => {
  const fixture = await setup(page, context); let first: string | undefined; let attempts = 0;
  await page.route('**/api/v1/attendance/corrections/*/submit', r => {
    attempts++; const body = r.request().postDataJSON();
    if (attempts === 1) { first = body.commandId; return r.abort('failed'); }
    expect(body.commandId).toBe(first);
    return r.fulfill({ json: { id, commandId: first } });
  });
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true }).click();
  await expect(page.getByRole('button', { name: 'ตรวจสถานะและลองคำสั่งเดิม' })).toBeVisible();
  expect(attempts).toBe(1); const count = fixture.calls.length;
  await page.getByRole('button', { name: 'ตรวจสถานะและลองคำสั่งเดิม' }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  expect(fixture.calls[count]).toMatchObject({ method: 'GET', path: `/api/v1/attendance/corrections/${id}` });
  expect(attempts).toBe(2);
});
test('503 ไม่ logout และการถอนสิทธิ์ล้างรายละเอียด', async ({ page, context }) => {
  await setup(page, context); await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.route('**/api/v1/attendance/corrections?*', r => r.fulfill({ status: 503, json: {} }));
  await page.getByRole('button', { name: 'โหลดรายการใหม่' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'ระบบไม่พร้อม' })).toBeVisible();
  await expect(page).toHaveURL(/\/corrections$/);
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
  await page.route('**/api/v1/attendance/corrections?*', r => r.fulfill({ status: 403, json: {} }));
  await page.getByRole('button', { name: 'โหลดรายการใหม่' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'ไม่มีสิทธิ์' })).toBeVisible();
});
test('เปลี่ยนบัญชีระหว่างรายละเอียดตอบช้าจะไม่แสดงข้อมูลบัญชีเดิม', async ({ page, context }) => {
  await setup(page, context); let release!: () => void, waiting = false;
  const held = new Promise<void>(resolve => { release = resolve; });
  await page.route(`**/api/v1/attendance/corrections/${id}`, async r => { waiting = true; await held; await r.fulfill({ json: { ...initial, comparison: { dayId: null, baselineMatches: true, current: null, original: null }, history: { revisions: [], routes: [], approvals: [], reroutes: [] } } }).catch(() => {}); });
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click(); await expect.poll(() => waiting).toBe(true);
  await session(context, 'e2e-other');
  await page.route('**/api/v1/attendance/corrections?*', r => r.fulfill({ json: { items: [], total: 0, pageNumber: 1, pageSize: 20 } }));
  release();
  await expect(page.getByText('ไม่มีคำร้องในคิวนี้')).toBeVisible();
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
  await expect(page.getByText('ลืมลงเวลาทดสอบ', { exact: true })).toHaveCount(0);
});
test('มีทางเข้าคำร้องจากประวัติและ Portal', async ({ page, context }) => {
  await setup(page, context);
  await page.goto('/portal');
  await expect(page.getByRole('link', { name: 'คำร้องแก้ไขเวลา', exact: true })).toHaveAttribute('href', '/portal/attendance/corrections');
});
test('อ่าน body ช้าหลังเปลี่ยนบัญชีต้องทิ้งรายละเอียด แม้ headers มาถึงแล้ว', async ({ page, context }) => {
  await setup(page, context);
  await page.evaluate(() => {
    const original = window.fetch;
    window.fetch = async (...args) => {
      const response = await original(...args);
      if (String(args[0]).endsWith('/55555555-5555-4555-8555-555555555555')) {
        const read = response.json.bind(response);
        response.json = async () => { const data = await read(); document.documentElement.dataset.bodyWaiting = 'yes'; await new Promise<void>(resolve => window.addEventListener('release-test-body', () => resolve(), { once: true })); return data; };
      }
      return response;
    };
  });
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await expect(page.locator('html')).toHaveAttribute('data-body-waiting', 'yes');
  await session(context, 'e2e-other');
  await page.route('**/api/v1/attendance/corrections?*', r => r.fulfill({ json: { items: [], total: 0, pageNumber: 1, pageSize: 20 } }));
  await page.evaluate(() => window.dispatchEvent(new Event('release-test-body')));
  await expect(page.getByText('ไม่มีคำร้องในคิวนี้')).toBeVisible();
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
});
test('ผลอนุมัติตอบช้าหลังเปลี่ยนบัญชีไม่แสดงสำเร็จหรือข้อมูลเดิม', async ({ page, context }) => {
  const fixture = await setup(page, context);
  fixture.change({ state: 1, actions: ['supervisor-approve', 'return'], readBasis: 'WorkflowSupervisor' });
  await page.goto('/portal/attendance/approvals');
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  let release!: () => void, waiting = false; const held = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/attendance/corrections/*/supervisor-approve', async r => { waiting = true; await held; await r.fulfill({ json: { id, commandId: r.request().postDataJSON().commandId } }).catch(() => {}); });
  await page.getByRole('button', { name: 'หัวหน้าอนุมัติ', exact: true }).click(); await expect.poll(() => waiting).toBe(true);
  await session(context, 'e2e-other');
  await page.route('**/api/v1/attendance/corrections/approvals?*', r => r.fulfill({ json: { items: [], total: 0, pageNumber: 1, pageSize: 20 } }));
  release();
  await expect(page.getByText('ไม่มีคำร้องในคิวนี้')).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toHaveCount(0);
  await expect(page.getByText('ลืมลงเวลาทดสอบ', { exact: true })).toHaveCount(0);
});
test('สองแท็บส่ง revision เดิม อีกแท็บได้ conflict และไม่ลองใหม่เอง', async ({ page, context }) => {
  await setup(page, context); const other = await context.newPage(); await setup(other, context);
  let posts = 0;
  for (const tab of [page, other]) {
    await tab.route('**/api/v1/attendance/corrections/*/submit', r => {
      posts++; expect(r.request().postDataJSON().expectedVersion).toBe(1);
      return posts === 1 ? r.fulfill({ json: { id, commandId: r.request().postDataJSON().commandId } }) : r.fulfill({ status: 409, json: {} });
    });
    await tab.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  }
  await page.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  await other.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true }).click();
  await expect(other.getByRole('alert').filter({ hasText: 'โหลดใหม่' })).toBeVisible();
  expect(posts).toBe(2); await expect(other.getByLabel('เหตุผลคำร้อง')).toHaveCount(0); await other.close();
});
test('เริ่มจากวันในประวัติใช้ dayId และ version จริง ไม่สร้างวันใหม่', async ({ page, context }) => {
  const fixture = await setup(page, context);
  const userId = await page.evaluate(async () => (await (await fetch('/api/v1/auth/session', { cache: 'no-store' })).json()).userId as string);
  await page.route(`**/api/v1/attendance/days/${id}`, r => r.fulfill({ json: { id, employeeId: userId, workDate: proposal.workDate, version: 2, readBasis: 1, in: null, out: null,
    effectiveInUtc: proposal.inUtc, effectiveOutUtc: proposal.outUtc, effectiveInSite: site, effectiveOutSite: site,
    adjustments: [{ requestId: id, revision: 1, appliedAtUtc: proposal.outUtc, beforeInUtc: null, beforeOutUtc: null, afterInUtc: proposal.inUtc, afterOutUtc: proposal.outUtc }] } }));
  await page.goto(`/portal/attendance/corrections?day=${id}`);
  await page.getByRole('button', { name: 'สร้างคำร้อง', exact: true }).click();
  await expect(page.getByLabel('เวลาเข้า (ประเทศไทย)')).toHaveValue('2026-10-17T08:00');
  await page.getByLabel('เหตุผลคำร้อง').fill('แก้รายการที่เลือก');
  await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'สำเร็จ' })).toBeVisible();
  expect(fixture.calls.find(x => x.method === 'POST')?.body).toMatchObject({ input: { dayId: id, expectedDayVersion: 2, originalWorkDate: '2026-10-17' } });
});
test('เปลี่ยนบัญชีระหว่างรอ CSRF ต้องไม่มี POST', async ({ page, context }) => {
  const fixture = await setup(page, context); let release!: () => void, waiting = false;
  const held = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/auth/csrf', async r => { waiting = true; await held; await r.fulfill({ json: { token: 'synthetic-csrf-for-transport-test' } }).catch(() => {}); });
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true }).click();
  await expect.poll(() => waiting).toBe(true); await session(context, 'e2e-other'); release();
  await expect(page.getByRole('alert').filter({ hasText: 'ไม่มีสิทธิ์' })).toBeVisible();
  expect(fixture.calls.filter(x => x.method === 'POST')).toHaveLength(0);
  await expect(page.getByLabel('เหตุผลคำร้อง')).toHaveCount(0);
});
test('ผลสำเร็จหายแล้ว revision เปลี่ยน ต้องทบทวนไม่ส่งซ้ำอีก', async ({ page, context }) => {
  const fixture = await setup(page, context); let posts = 0;
  await page.route('**/api/v1/attendance/corrections/*/submit', r => { posts++; fixture.change({ version: 2, state: 1, actions: [] }); return r.abort('failed'); });
  await page.getByRole('button', { name: 'เปิดคำร้อง', exact: true }).click();
  await page.getByRole('button', { name: 'ส่งให้หัวหน้า', exact: true }).click();
  await page.getByRole('button', { name: 'ตรวจสถานะและลองคำสั่งเดิม' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'โหลดใหม่' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'ตรวจสถานะและลองคำสั่งเดิม' })).toHaveCount(0);
  expect(posts).toBe(1);
});

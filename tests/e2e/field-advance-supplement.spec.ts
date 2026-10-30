import { test, expect, type BrowserContext } from '@playwright/test';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
test.setTimeout(300000);
test.use({ actionTimeout: 15000, navigationTimeout: 30000 });
const workspace = (n: number) => `88100000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const project = (n: number) => `88200000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const root = '/api/v1/field-advances';
function sql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{64}$/.test(database)) throw new Error('ต้องใช้ disposable HTTPS fixture');
  return execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1'], { input, encoding: 'utf8' }).trim();
}
function apiContainer() {
  const database = process.env.TPR10_E2E_DATABASE!;
  if (!/^[a-f0-9]{64}$/.test(database)) throw new Error('Requires isolated database');
  const networks = JSON.parse(execFileSync('docker', ['inspect', '-f', '{{json .NetworkSettings.Networks}}', database], { encoding: 'utf8' }));
  const network = Object.keys(networks).find(n => /^tpr10-csrf-[a-z0-9]+$/.test(n));
  if (!network) throw new Error('Requires isolated HTTPS network');
  const members = JSON.parse(execFileSync('docker', ['network', 'inspect', '-f', '{{json .Containers}}', network], { encoding: 'utf8' }));
  const proxies = Object.keys(members).filter(id => execFileSync('docker', ['inspect', '-f', '{{.Config.Image}}', id], { encoding: 'utf8' }).trim() === 'nginx:1.28-alpine');
  expect(proxies).toHaveLength(1);
  const candidates = execFileSync('docker', ['ps', '-q', '--no-trunc', '--filter', 'ancestor=mcr.microsoft.com/dotnet/aspnet:10.0'], { encoding: 'utf8' }).trim().split('\n').filter(Boolean);
  const apis = candidates.filter(id => execFileSync('docker', ['inspect', '-f', '{{.HostConfig.NetworkMode}}', id], { encoding: 'utf8' }).trim() === `container:${proxies[0]}`);
  expect(apis).toHaveLength(1); return apis[0];
}
const user = (actor: number) => `88000000-0000-4000-8000-${String(1800 + actor).padStart(12, '0')}`;
async function session(context: BrowserContext, actor: number) {
  const id = user(actor), token = randomBytes(32), hash = createHash('sha256').update(token).digest('hex');
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
async function send(c: BrowserContext, url: string, body: unknown) {
  const { token } = await (await c.request.get('/api/v1/auth/csrf')).json();
  const response = await c.request.post(url, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': token }, data: body });
  expect(response.status(), await response.text()).toBe(url === root + "/requests" || url.endsWith("/supplements") ? 201 : 200); return response.json();
}

test('supplement shows 3000 payout against 13000 authority', async ({ page, context, browser }) => {
  const owner = context, checker = await browser.newContext(), payer = await browser.newContext();
  const approvers = await Promise.all([2, 3, 5].map(async actor => { const c = await browser.newContext(); await session(c, actor); return c; }));
  try {
    await session(owner, 1); await session(checker, 7); await session(payer, 4);
    await policy(owner, approvers[0], 18);
    const proposal = randomUUID(), review = randomUUID();
    sql(`UPDATE field_advance_grant_proposals SET capabilities=ARRAY['payment.record'] WHERE recipient_id='${user(4)}' AND capabilities=ARRAY['request.delegate'];
      INSERT INTO field_advance_grant_proposals(id,workspace_id,project_id,recipient_id,recipient_person_id,proposed_by,proposed_by_person_id,state,capabilities,delegable_capabilities,starts_at_utc,created_at_utc,reason)
      SELECT '${proposal}',p.workspace_id,p.project_id,'${user(7)}',l.person_id,p.proposed_by,p.proposed_by_person_id,'Approved',ARRAY['finance.check'],ARRAY[]::text[],p.starts_at_utc,p.created_at_utc,'สิทธิ์ตรวจจำลอง'
      FROM field_advance_grant_proposals p JOIN finance_account_links l ON l.user_id='${user(7)}' AND l.workspace_id=p.workspace_id WHERE p.recipient_id='${user(4)}' AND p.capabilities=ARRAY['payment.record'];
      INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,reviewer_person_id,outcome,created_at_utc,reason)
      SELECT '${review}',r.workspace_id,'${proposal}',r.reviewer_id,r.reviewer_person_id,r.outcome,r.created_at_utc,'ตรวจจำลอง' FROM field_advance_grant_reviews r JOIN field_advance_grant_proposals p ON p.id=r.proposal_id WHERE p.recipient_id='${user(4)}' AND p.capabilities=ARRAY['payment.record'];
      INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,review_id,created_by,created_at_utc,reason)
      SELECT gen_random_uuid(),p.workspace_id,p.project_id,p.id,'${review}',p.proposed_by,now(),'จำลอง' FROM field_advance_grant_proposals p WHERE p.id='${proposal}';`);
    const person = sql(`SELECT person_id FROM finance_account_links WHERE user_id='${user(1)}';`);
    const content = { kind: 'Individual', purpose: 'จ่ายเงินทดรองจำลอง', fieldStart: '2026-10-30', fieldEnd: '2026-10-31', neededOn: '2026-10-30', teamPersonIds: [], lines: [{ id: randomUUID(), categoryId: '88300000-0000-4000-8000-000000000001', description: 'เงินทดสอบ', mode: 'QuantityRate', quantity: '100', unitRate: '100.00', lumpSum: null }] };
    let request = await send(owner, root + '/requests', { input: { scope: { workspaceId: workspace(18), projectId: project(18) }, ownerPersonId: person, content }, meta: { commandId: randomUUID(), expectedVersion: 0, reason: 'จำลอง' } });
    const quote = await (await owner.request.get(`${root}/requests/${request.id}/quote`)).json();
    request = await send(owner, `${root}/requests/${request.id}/confirm`, { confirmationHash: quote.confirmationHash, meta: { commandId: randomUUID(), expectedVersion: request.version, reason: 'จำลอง' } });
    for (const c of approvers) {
      const current = await (await c.request.get(`${root}/requests/${request.id}`)).json();
      request = await send(c, `${root}/requests/${request.id}/review`, { decision: { outcome: 'Approve', stepId: current.currentStep.id }, meta: { commandId: randomUUID(), expectedVersion: current.version, reason: 'จำลอง' } });
    }

    const meta = (v: { familyVersion: number; requestVersion: number; installmentVersion: number | null }) => ({ commandId: randomUUID(), familyVersion: v.familyVersion, requestVersion: v.requestVersion, installmentVersion: v.installmentVersion, reason: 'สถานการณ์ส่วนเพิ่มจำลอง' });
    await test.step('ceiling activation requires both fresh approval and cleared original installments', async () => {
      let ceiling = await send(owner, root + '/requests', { input: { scope: { workspaceId: workspace(18), projectId: project(18) }, ownerPersonId: person, content: { ...content, purpose: 'ปรับวงเงินจำลอง' } }, meta: { commandId: randomUUID(), expectedVersion: 0, reason: 'จำลองปรับวงเงิน' } });
      const quote = await (await owner.request.get(`${root}/requests/${ceiling.id}/quote`)).json();
      ceiling = await send(owner, `${root}/requests/${ceiling.id}/confirm`, { confirmationHash: quote.confirmationHash, meta: { commandId: randomUUID(), expectedVersion: ceiling.version, reason: 'ยืนยันเดิม' } });
      await approve(ceiling.id);
      const before = await (await payer.request.get(`${root}/requests/${ceiling.id}/payments`)).json();
      let old = await send(payer, `${root}/requests/${ceiling.id}/installments`, { input: { amount: '500.00', destination: { channel: 'Cash', recipientPersonId: person, bankCode: null, accountNumber: null, accountHolder: null } }, meta: meta(before) });
      old = await send(payer, `${root}/installments/${old.installmentId}/submit`, meta(old));
      await page.clock.install();
      await page.goto(`/portal/field-advances/${ceiling.id}`);
      await page.getByRole('button', { name: 'ปรับวงเงินก่อนจ่าย', exact: true }).click();
      await page.getByLabel('จำนวนปรับวงเงิน 1', { exact: true }).fill('90');
      await page.getByLabel('เหตุผลปรับวงเงิน', { exact: true }).fill('ลดประมาณการโดยรอสะสาง');
      let committedAmendment = false;
      const amendmentIds: string[] = [], lookups: string[] = [];
      const originalVersion = (await (await owner.request.get(`${root}/requests/${ceiling.id}`)).json()).version;
      const amendmentRoute = `**${root}/requests/${ceiling.id}/ceiling-amendment`;
      const contextRoute = `**${root}/requests/${ceiling.id}/payment-context`;
      await page.route(contextRoute, route => committedAmendment
        ? route.fulfill({ status: 503, contentType: 'application/problem+json', body: '{}' }) : route.continue());
      await page.route(amendmentRoute, async route => {
        amendmentIds.push(route.request().postDataJSON().meta.commandId);
        const response = await route.fetch(); expect(response.ok()).toBe(true);
        committedAmendment = true; await route.fulfill({ response });
      });
      page.on('request', r => { if (r.method() === 'GET' && r.url().includes('/payment-commands/')) lookups.push(r.url()); });
      await page.getByRole('button', { name: 'บันทึกฉบับปรับวงเงินและพักจ่าย', exact: true }).click();
      const recovery = page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true });
      await expect(recovery).toBeVisible();
      expect(amendmentIds).toHaveLength(1);
      await expect(page.getByRole('alert').filter({ hasText: amendmentIds[0] })).toBeVisible();
      // Real parent timer reads the newly committed request version; child reads alone fail.
      const parentRead = page.waitForResponse(r => r.url().endsWith(`${root}/requests/${ceiling.id}`) && r.request().method() === 'GET');
      await page.clock.fastForward(16000);
      expect((await (await parentRead).json()).version).toBeGreaterThan(originalVersion);
      await expect(page.getByTestId('request-state')).toHaveText('ร่าง');
      await expect(recovery).toBeVisible();
      await expect(page.getByRole('alert').filter({ hasText: amendmentIds[0] })).toBeVisible();
      await expect(page.getByRole('button', { name: 'โหลดใหม่', exact: true })).toBeDisabled();
      await page.unroute(contextRoute);
      await recovery.click();
      await expect(recovery).toHaveCount(0);
      await expect(page.getByRole('button', { name: 'โหลดใหม่', exact: true })).toBeEnabled();
      expect(amendmentIds).toHaveLength(1);
      expect(lookups.some(url => url.endsWith(amendmentIds[0]))).toBe(true);
      await page.unroute(amendmentRoute);

      await expect(page.getByText('อนุมัติฉบับใหม่: ยังไม่ครบ · สะสางงวดเดิม: ยังไม่ครบ', { exact: true })).toBeVisible();
      await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
      await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('รออนุมัติ');
      await approve(ceiling.id);
      await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
      await expect(page.getByText('อนุมัติฉบับใหม่: ครบแล้ว · สะสางงวดเดิม: ยังไม่ครบ', { exact: true })).toBeVisible();
      await expect(page.getByRole('button', { name: 'เปิดใช้วงเงินฉบับใหม่', exact: true })).toHaveCount(0);
      let latest = (await (await payer.request.get(`${root}/installments/${old.installmentId}`)).json()).summary;
      latest = await send(payer, `${root}/installments/${old.installmentId}/cancel`, meta(latest));
      const catalog = await (await checker.request.get(`${root}/requests/${ceiling.id}/installments`)).json();
      const cancellation = catalog.proposals.find((p: { kind: string; state: string }) => p.kind === 'PaymentCancellation' && p.state === 'Pending');
      await send(checker, `${root}/payment-proposals/${cancellation.id}/confirm`, meta(latest));
      await page.getByRole('button', { name: 'โหลดสถานะวงเงินใหม่', exact: true }).click();
      await expect(page.getByText('อนุมัติฉบับใหม่: ครบแล้ว · สะสางงวดเดิม: ครบแล้ว', { exact: true })).toBeVisible();
      await page.getByLabel('เหตุผลเปิดใช้วงเงิน', { exact: true }).fill('ครบอนุมัติและสะสาง');
      await page.getByRole('button', { name: 'เปิดใช้วงเงินฉบับใหม่', exact: true }).click();
      await expect(page.getByText('วงเงินอนุมัติที่ใช้จ่ายอยู่ 9000.00 บาท', { exact: false })).toBeVisible();
      const activated = await (await owner.request.get(`${root}/requests/${ceiling.id}/payments`)).json();
      expect(activated.approved).toBe('9000.00'); expect(activated.reserved).toBe('0.00'); expect(activated.holdCodes).not.toContain('CeilingAmendment');
      const cancelled = (await (await payer.request.get(`${root}/installments/${old.installmentId}`)).json()).summary; expect(cancelled.state).toBe('Cancelled');
      await page.getByRole('button', { name: 'ปรับวงเงินก่อนจ่าย', exact: true }).click();
      await page.getByLabel('จำนวนปรับวงเงิน 1', { exact: true }).fill('80');
      await page.getByLabel('เหตุผลปรับวงเงิน', { exact: true }).fill('ฉบับปรับที่รอพิจารณา');
      await page.getByRole('button', { name: 'บันทึกฉบับปรับวงเงินและพักจ่าย', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('ร่าง');
      await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
      await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('รออนุมัติ');
      const toReject = await (await approvers[0].request.get(`${root}/requests/${ceiling.id}`)).json();
      await send(approvers[0], `${root}/requests/${ceiling.id}/review`, { decision: { outcome: 'Reject', stepId: toReject.currentStep.id }, meta: { commandId: randomUUID(), expectedVersion: toReject.version, reason: 'ปฏิเสธฉบับปรับเท่านั้น' } });
      sql(`INSERT INTO field_advance_payment_holds(id,workspace_id,project_id,family_id,request_id,code,reason,created_by,created_by_person_id,created_at_utc)
        SELECT gen_random_uuid(),r.workspace_id,r.project_id,r.family_id,NULL,'PaymentMismatch','เหตุผิดปกติจำลองที่ห้ามปลด','${user(7)}',l.person_id,now() FROM field_advance_requests r JOIN finance_account_links l ON l.user_id='${user(7)}' AND l.workspace_id=r.workspace_id WHERE r.id='${ceiling.id}';`);
      await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
      await expect(page.getByText('ผลฉบับปรับวงเงิน: Rejected ไม่ลบวงเงินอนุมัติเดิม', { exact: true })).toBeVisible();
      const checkPage = await checker.newPage(); await checkPage.goto(`/portal/field-advances/${ceiling.id}`);
      await checkPage.getByLabel('เหตุผลยุติยอดหรือกลับใช้เดิม', { exact: true }).fill('เสนอใช้ฉบับเดิมโดยคงเหตุอื่น');
      await checkPage.getByRole('button', { name: 'เสนอกลับใช้เงื่อนไขเดิม', exact: true }).click();
      await expect(checkPage.getByText('กลับใช้เงื่อนไขเดิม · รอยืนยัน', { exact: true })).toBeVisible();
      const final = await approvers[2].newPage(); await final.goto(`/portal/field-advances/approvals?disposition=${ceiling.id}`);
      await final.getByLabel('เหตุผลยุติยอดหรือกลับใช้เดิม', { exact: true }).fill('ยืนยันใช้เดิมโดยไม่ปลด mismatch');
      await final.getByRole('button', { name: 'ยืนยันการตัดสินใจนี้', exact: true }).click();
      await expect(final.getByRole('button', { name: 'ยืนยันการตัดสินใจนี้', exact: true })).toHaveCount(0);
      const resumed = await (await owner.request.get(`${root}/requests/${ceiling.id}/payments`)).json();
      expect(resumed.approved).toBe('9000.00'); expect(resumed.holdCodes).toContain('PaymentMismatch'); expect(resumed.holdCodes).not.toContain('CeilingAmendment');
      expect((await (await payer.request.get(`${root}/requests/${ceiling.id}/payment-context`)).json()).allowedActions).not.toContain('installments');
      await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('อนุมัติแล้ว รอดำเนินการจ่าย');
      await page.getByRole('button', { name: 'ดูฉบับที่ 3', exact: true }).click();
      await expect(page.getByRole('region', { name: 'หลักฐานฉบับคำขอ' })).toContainText('ปฏิเสธฉบับปรับเท่านั้น');
      await page.getByRole('button', { name: 'กลับฉบับปัจจุบัน', exact: true }).click();
      await page.getByRole('button', { name: 'ปรับวงเงินก่อนจ่าย', exact: true }).click();
      await page.getByLabel('จำนวนปรับวงเงิน 1', { exact: true }).fill('70');
      await page.getByLabel('เหตุผลปรับวงเงิน', { exact: true }).fill('ฉบับที่จะถอน');
      await page.getByRole('button', { name: 'บันทึกฉบับปรับวงเงินและพักจ่าย', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('ร่าง');
      await page.getByRole('button', { name: 'ถอนคำขอ', exact: true }).click();
      await page.getByLabel('เหตุผลถอนคำขอ', { exact: true }).fill('ถอนเฉพาะฉบับปรับ');
      await page.getByRole('button', { name: 'ยืนยันถอนคำขอ', exact: true }).click();
      await expect(page.getByText('ผลฉบับปรับวงเงิน: Withdrawn ไม่ลบวงเงินอนุมัติเดิม', { exact: true })).toBeVisible();
      const withdrawn = await (await owner.request.get(`${root}/requests/${ceiling.id}/payments`)).json();
      expect(withdrawn.approved).toBe('9000.00'); expect(withdrawn.holdCodes).toContain('CeilingAmendment'); expect(withdrawn.holdCodes).toContain('PaymentMismatch');
      await checkPage.close(); await final.close();

    });
    let summary = await (await payer.request.get(`${root}/requests/${request.id}/payments`)).json();
    summary = await send(payer, `${root}/requests/${request.id}/installments`, { input: { amount: '2000.00', destination: { channel: 'Cash', recipientPersonId: person, bankCode: null, accountNumber: null, accountHolder: null } }, meta: meta(summary) });
    summary = await send(payer, `${root}/installments/${summary.installmentId}/submit`, meta(summary));
    summary = await send(checker, `${root}/installments/${summary.installmentId}/review`, { outcome: 'Approve', meta: meta(summary) });
    summary = await send(payer, `${root}/installments/${summary.installmentId}/start`, meta(summary));
    await page.goto(`/portal/field-advances/${request.id}`);
    await expect(page.getByText('พักการปรับวงเงิน: ยังมีผลจ่ายไม่ชัดเจน ต้องสะสางก่อน', { exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'ปรับวงเงินก่อนจ่าย', exact: true })).toHaveCount(0);
    const csrf = await (await payer.request.get('/api/v1/auth/csrf')).json();
    const evidence = await payer.request.post(`${root}/requests/${request.id}/payment-evidence`, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': csrf.token, 'Content-Type': 'image/png' }, data: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgaPgPAAIDAYAkYfWXAAAAAElFTkSuQmCC', 'base64') });
    expect(evidence.status()).toBe(201);
    summary = await send(payer, `${root}/installments/${summary.installmentId}/record`, { input: { amount: '2000.00', recipientPersonId: person, paidOn: '2026-10-30', reference: null, evidenceIds: [(await evidence.json()).id] }, meta: meta(summary) });
    expect(summary.paid).toBe('2000.00');
    await page.goto(`/portal/field-advances/${request.id}`);
    await page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true }).click();
    await expect(page.getByText(`โครงการเดิม ${project(18)} · ผู้รับผิดชอบเดิม ${person} (ล็อกจากเซิร์ฟเวอร์)`, { exact: true })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'ผู้รับผิดชอบเงิน (ชื่อในระบบ)', exact: true })).toHaveCount(0);
    await page.getByLabel('รายละเอียดงานส่วนเพิ่ม', { exact: true }).fill('ส่วนเพิ่มจำลอง');
    await page.getByLabel('จำนวนส่วนเพิ่ม 1', { exact: true }).fill('30');
    await page.getByLabel('เหตุผลส่วนเพิ่ม', { exact: true }).fill('ขอเพิ่มเฉพาะงานใหม่');
    let committed = false, commandId = '', supplementId = ''; const commands: string[] = [];
    const createPattern = `**${root}/requests/${request.id}/supplements`;
    const readPattern = `**${root}/requests/*`;
    await page.route(readPattern, route => committed && route.request().method() === 'GET' && route.request().url().endsWith(supplementId)
      ? route.fulfill({ status: 409, contentType: 'application/problem+json', body: '{}' }) : route.continue());
    await page.route(createPattern, async route => {
      commandId = route.request().postDataJSON().meta.commandId; commands.push(commandId);
      const response = await route.fetch(); expect(response.status()).toBe(201);
      supplementId = (await response.json()).id; committed = true; await route.fulfill({ response });
    });
    await page.getByRole('button', { name: 'บันทึกร่างส่วนเพิ่ม', exact: true }).click();
    await expect(page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true })).toHaveCount(0);
    await page.unroute(readPattern);
    await page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true }).click();
    await expect(page).toHaveURL(new RegExp(`/field-advances/${supplementId}$`));
    expect(commands).toEqual([commandId, commandId]); await page.unroute(createPattern);
    const lockedSupplement = await (await owner.request.get(`${root}/requests/${supplementId}`)).json();
    expect(lockedSupplement.ownerPersonId).toBe(person); expect(lockedSupplement.scope).toEqual({ workspaceId: workspace(18), projectId: project(18) });
    await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
    await expect(page.getByText('ยอดส่วนเพิ่ม 3,000.00 บาท', { exact: true }).last()).toBeVisible();
    await expect(page.getByText('ฐานอำนาจอนุมัติ 13,000.00 บาท', { exact: true }).last()).toBeVisible();

    await test.step('stale quote requires fresh owner confirmation', async () => {
      const draft = await (await owner.request.get(`${root}/requests/${supplementId}`)).json();
      const csrf = await (await owner.request.get('/api/v1/auth/csrf')).json();
      const changed = await owner.request.put(`${root}/requests/${supplementId}`, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': csrf.token }, data: { content: { ...draft.content, purpose: 'ส่วนเพิ่มฉบับล่าสุด' }, meta: { commandId: randomUUID(), expectedVersion: draft.version, reason: 'แก้ก่อนยืนยัน' } } });
      expect(changed.status()).toBe(200);
      const stale = page.waitForResponse(r => r.url().endsWith(`/${supplementId}/confirm`) && r.request().method() === 'POST');
      await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
      expect((await stale).status()).toBe(409);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
      await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
      await expect(page.getByRole('dialog')).toContainText('ส่วนเพิ่มฉบับล่าสุด');
      await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('รออนุมัติ');
    });
    const reviewPage = await approvers[0].newPage();
    await test.step('assigned supplement basis is not source-read permission', async () => {
      await reviewPage.goto(`/portal/field-advances/${supplementId}`);
      await expect(reviewPage.getByText('ยอดส่วนเพิ่ม 3,000.00 บาท', { exact: true })).toBeVisible();
      await expect(reviewPage.getByText('ฐานอำนาจอนุมัติ 13,000.00 บาท', { exact: true })).toBeVisible();
      expect((await approvers[0].request.get(`${root}/requests/${request.id}`)).status()).toBe(403);
      expect((await approvers[0].request.get(`${root}/payment-evidence/${(await evidence.json()).id}`)).status()).toBe(403);
      const independentSource = await approvers[0].newPage();
      await independentSource.goto(`/portal/field-advances/${request.id}`);
      await expect(independentSource.getByRole('main').getByRole('alert').first()).toContainText('ไม่มีสิทธิ์');
      await expect(independentSource.getByText('จ่ายเงินทดรองจำลอง', { exact: true })).toHaveCount(0); await independentSource.close();
      await reviewPage.getByLabel('เหตุผลดำเนินการ', { exact: true }).fill('ส่งกลับส่วนเพิ่มเท่านั้น');
      const returned = reviewPage.waitForResponse(r => r.url().endsWith(`/${supplementId}/review`) && r.request().method() === 'POST');
      await reviewPage.getByRole('button', { name: 'ส่งกลับ', exact: true }).click();
      expect([200, 403]).toContain((await returned).status());
      expect((await (await owner.request.get(`${root}/requests/${supplementId}`)).json()).state).toBe('Returned');
      await expect(reviewPage.getByText('ฐานอำนาจอนุมัติ 13,000.00 บาท', { exact: true })).toHaveCount(0);
      await page.goto(`/portal/field-advances/${request.id}`);
      await expect(page.getByText('มีส่วนเพิ่มค้าง (Returned)', { exact: false })).toBeVisible();
      await expect(page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true })).toHaveCount(0);
      await page.goto(`/portal/field-advances/${supplementId}`);
      await page.getByRole('button', { name: 'สร้างฉบับแก้ไข', exact: true }).click();
      await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
      await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('รออนุมัติ');
    });
    await test.step('pending source remains payable but supplement has no spendable authority', async () => {
      const source = await (await payer.request.get(`${root}/requests/${request.id}/payments`)).json();
      let installment = await send(payer, `${root}/requests/${request.id}/installments`, { input: { amount: '100.00', destination: { channel: 'Cash', recipientPersonId: person, bankCode: null, accountNumber: null, accountHolder: null } }, meta: meta(source) });
      installment = await send(payer, `${root}/installments/${installment.installmentId}/submit`, meta(installment));
      installment = await send(checker, `${root}/installments/${installment.installmentId}/review`, { outcome: 'Approve', meta: meta(installment) });
      installment = await send(payer, `${root}/installments/${installment.installmentId}/start`, meta(installment));
      installment = await send(payer, `${root}/installments/${installment.installmentId}/record`, { input: { amount: '100.00', recipientPersonId: person, paidOn: '2026-10-30', reference: null, evidenceIds: [(await evidence.json()).id] }, meta: meta(installment) });
      expect(installment.paid).toBe('2100.00');
      const pending = await (await payer.request.get(`${root}/requests/${supplementId}/payments`)).json();
      expect(pending.spendable).toBe('0.00');
      const csrf = await (await payer.request.get('/api/v1/auth/csrf')).json();
      const denied = await payer.request.post(`${root}/requests/${supplementId}/installments`, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': csrf.token }, data: { input: { amount: '100.00', destination: { channel: 'Cash', recipientPersonId: person, bankCode: null, accountNumber: null, accountHolder: null } }, meta: meta(pending) } });
      expect(denied.status()).toBe(409);
      const after = await (await payer.request.get(`${root}/requests/${supplementId}/payments`)).json(); expect(after.reserved).toBe('0.00');
    });
    async function approve(id: string) {
      for (const c of approvers) {
        const current = await (await c.request.get(`${root}/requests/${id}`)).json();
        await send(c, `${root}/requests/${id}/review`, { decision: { outcome: 'Approve', stepId: current.currentStep.id }, meta: { commandId: randomUUID(), expectedVersion: current.version, reason: 'อนุมัติส่วนเพิ่มจำลอง' } });
      }
    }
    await approve(supplementId);
    await test.step('terminate original via limited disposition projection and retain cumulative basis', async () => {
      let unsettled = await (await payer.request.get(`${root}/requests/${request.id}/payments`)).json();
      unsettled = await send(payer, `${root}/requests/${request.id}/installments`, { input: { amount: '100.00', destination: { channel: 'Cash', recipientPersonId: person, bankCode: null, accountNumber: null, accountHolder: null } }, meta: meta(unsettled) });
      unsettled = await send(payer, `${root}/installments/${unsettled.installmentId}/submit`, meta(unsettled));
      const checkerPage = await checker.newPage();
      await checkerPage.goto(`/portal/field-advances/${request.id}`);
      await checkerPage.getByLabel('เหตุผลยุติยอดหรือกลับใช้เดิม', { exact: true }).fill('ยุติเฉพาะยอดที่เหลือ');
      await checkerPage.getByRole('button', { name: 'เสนอยุติยอดที่ยังไม่จ่าย', exact: true }).click();
      await expect(checkerPage.getByText('ยุติยอดที่ยังไม่จ่าย · รอยืนยัน', { exact: true })).toBeVisible();
      const final = await approvers[2].newPage();
      const accesses: string[] = []; final.on('request', r => { if (r.url().includes(root)) accesses.push(r.url()); });
      await final.goto('/portal/field-advances/approvals');
      await final.getByRole('link', { name: `พิจารณายุติยอด ${request.id}`, exact: true }).click();
      await expect(final.getByText('ยุติเฉพาะยอดที่ยังไม่จ่าย ยังต้องเคลียร์เงินที่จ่ายแล้ว', { exact: true })).toBeVisible();
      await final.getByLabel('เหตุผลยุติยอดหรือกลับใช้เดิม', { exact: true }).fill('ยืนยันตามอำนาจปัจจุบัน');
      const confirmed = final.waitForResponse(r => /payment-proposals\/[^/]+\/confirm$/.test(r.url()) && r.request().method() === 'POST', { timeout: 15000 });
      await final.getByRole('button', { name: 'ยืนยันการตัดสินใจนี้', exact: true }).click();
      const confirmedResponse = await confirmed; expect(confirmedResponse.status(), await confirmedResponse.text()).toBe(200);
      const currentContext = await approvers[2].request.get(`${root}/requests/${request.id}/payment-context`);
      expect(currentContext.status()).toBe(200); const liveContext = await currentContext.json();
      expect(liveContext.variant).toBe('disposition'); expect(liveContext.proposals[0].allowedActions).not.toContain('finalize');
      await expect(final.getByLabel('เหตุผลยุติยอดหรือกลับใช้เดิม', { exact: true })).toBeEnabled();
      await expect(final.getByRole('button', { name: 'ทำให้การยุติยอดมีผล', exact: true })).toHaveCount(0);
      const waiting = await (await owner.request.get(`${root}/requests/${request.id}/payments`)).json();
      expect(waiting.reserved).toBe('100.00'); expect(waiting.spendable).toBe('10000.00'); expect(waiting.paid).toBe('2100.00');
      await expect(checkerPage.getByRole('link', { name: 'สะสางและยกเลิกงวดตามสิทธิ์', exact: true })).toHaveAttribute('href', `/portal/field-advances/payments?requestId=${request.id}`);
      unsettled = (await (await payer.request.get(`${root}/installments/${unsettled.installmentId}`)).json()).summary;
      unsettled = await send(payer, `${root}/installments/${unsettled.installmentId}/cancel`, meta(unsettled));
      const catalog = await (await checker.request.get(`${root}/requests/${request.id}/installments`)).json();
      const cancel = catalog.proposals.find((p: { kind: string; state: string }) => p.kind === 'PaymentCancellation' && p.state === 'Pending');
      await send(checker, `${root}/payment-proposals/${cancel.id}/confirm`, meta(unsettled));
      const readyContext = await (await approvers[2].request.get(`${root}/requests/${request.id}/payment-context`)).json();
      expect(readyContext.proposals.find((p: { proposalId: string }) => p.proposalId === liveContext.proposals[0].proposalId).allowedActions).toContain('finalize');
      await final.getByRole('button', { name: 'โหลดสถานะวงเงินใหม่', exact: true }).click();
      await expect(final.getByRole('button', { name: 'ทำให้การยุติยอดมีผล', exact: true })).toBeVisible();
      await final.getByLabel('เหตุผลยุติยอดหรือกลับใช้เดิม', { exact: true }).fill('สะสางครบจึงยุติยอด');
      await final.getByRole('button', { name: 'ทำให้การยุติยอดมีผล', exact: true }).click();
      await expect(final.getByRole('button', { name: 'ทำให้การยุติยอดมีผล', exact: true })).toHaveCount(0);
      expect(accesses.some(url => url.endsWith(`/requests/${request.id}/payments`) || url.endsWith(`/requests/${request.id}`))).toBe(false);
      const terminated = await (await owner.request.get(`${root}/requests/${request.id}/payments`)).json();
      expect(terminated.approved).toBe('10000.00'); expect(terminated.paid).toBe('2100.00'); expect(terminated.spendable).toBe('2100.00'); expect(terminated.holdCodes).toContain('PaymentTerminated');
      await checkerPage.close(); await final.close();
    });
    await test.step('multiple supplements use each approved amount once after termination', async () => {
      let source = supplementId;
      for (const [quantity, total, basis] of [['20', '2,000.00', '15,000.00'], ['10', '1,000.00', '16,000.00']]) {
        await page.goto(`/portal/field-advances/${source}`);
        await page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true }).click();
        await page.getByLabel('รายละเอียดงานส่วนเพิ่ม', { exact: true }).fill('ส่วนเพิ่มหลายทอด');
        await page.getByLabel('จำนวนส่วนเพิ่ม 1', { exact: true }).fill(quantity);
        await page.getByLabel('เหตุผลส่วนเพิ่ม', { exact: true }).fill('งานเพิ่มเติม');
        await page.getByRole('button', { name: 'บันทึกร่างส่วนเพิ่ม', exact: true }).click();
        await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
        await expect(page.getByRole('dialog').getByText(`ยอดส่วนเพิ่ม ${total} บาท`, { exact: true })).toBeVisible();
        await expect(page.getByRole('dialog').getByText(`ฐานอำนาจอนุมัติ ${basis} บาท`, { exact: true })).toBeVisible();
        source = await page.getByTestId('request-id').innerText();
        await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
        await expect(page.getByTestId('request-state')).toHaveText('รออนุมัติ'); await approve(source);
      }
    });
    await test.step('historical role and a second owner account cannot become financial checker', async () => {
      const grant = randomUUID(), review = randomUUID();
      sql(`INSERT INTO field_advance_grant_proposals(id,workspace_id,project_id,recipient_id,recipient_person_id,proposed_by,proposed_by_person_id,state,capabilities,delegable_capabilities,starts_at_utc,created_at_utc,reason)
        SELECT '${grant}',workspace_id,project_id,'${user(6)}',recipient_person_id,proposed_by,proposed_by_person_id,'Approved',ARRAY['request.create','finance.check'],ARRAY[]::text[],starts_at_utc,created_at_utc,'บัญชีสองจำลอง'
        FROM field_advance_grant_proposals WHERE recipient_id='${user(1)}' AND capabilities=ARRAY['request.create'];
        INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,reviewer_person_id,outcome,created_at_utc,reason)
        SELECT '${review}',r.workspace_id,'${grant}',r.reviewer_id,r.reviewer_person_id,r.outcome,r.created_at_utc,'บัญชีสองจำลอง' FROM field_advance_grant_reviews r JOIN field_advance_grant_proposals p ON p.id=r.proposal_id WHERE p.recipient_id='${user(1)}' AND p.capabilities=ARRAY['request.create'];
        INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,review_id,created_by,created_at_utc,reason)
        SELECT gen_random_uuid(),workspace_id,project_id,id,'${review}',proposed_by,now(),'บัญชีสองจำลอง' FROM field_advance_grant_proposals WHERE id='${grant}';
        UPDATE field_advance_grant_proposals SET capabilities=ARRAY['request.approve','finance.check','report.read'] WHERE recipient_id='${user(2)}' AND capabilities=ARRAY['request.approve'];`);
      const alias = await browser.newContext(); await session(alias, 6);
      for (const actor of [alias, approvers[0]]) {
        const ui = await actor.newPage(); await ui.goto(`/portal/field-advances/${supplementId}`);
        await expect(ui.getByTestId('request-id')).toHaveText(supplementId);
        await expect(ui.getByRole('button', { name: 'เสนอยุติยอดที่ยังไม่จ่าย', exact: true })).toHaveCount(0);
        const ctx = await (await actor.request.get(`${root}/requests/${supplementId}/payment-context`)).json();
        expect(ctx.allowedActions).not.toContain('terminate-remaining');
        const csrf = await (await actor.request.get('/api/v1/auth/csrf')).json();
        const denied = await actor.request.post(`${root}/requests/${supplementId}/disposition`, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': csrf.token }, data: { outcome: 'TerminateRemaining', meta: { ...meta(ctx), installmentVersion: null } } });
        expect(denied.status()).toBe(403); await ui.close();
      }
      await alias.close();
      sql(`UPDATE field_advance_grant_proposals SET capabilities=ARRAY['request.approve'] WHERE recipient_id='${user(2)}' AND capabilities @> ARRAY['report.read'];`);
    });
    await test.step('committed supplement keeps original command for non-access reads and clears all data on 401/403', async () => {
      for (const status of [400, 503, 401, 403, 'identity', 'new-request'] as const) {
        await page.goto(`/portal/field-advances/${request.id}`);
        await page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true }).click();
        await page.getByLabel('รายละเอียดงานส่วนเพิ่ม', { exact: true }).fill(`ทดสอบผลอ่าน ${status}`);
        await page.getByLabel('จำนวนส่วนเพิ่ม 1', { exact: true }).fill('1');
        await page.getByLabel('เหตุผลส่วนเพิ่ม', { exact: true }).fill('ทดสอบ response หลัง commit');
        let created = '', committed = false; const ids: string[] = [];
        const mutation = `**${root}/requests/${request.id}/supplements`, reads = `**${root}/requests/*`;
        await page.route(reads, route => committed && route.request().method() === 'GET' && route.request().url().endsWith(created)
          ? route.fulfill({ status: typeof status === 'number' ? status : 503, contentType: 'application/problem+json', body: '{}' }) : route.continue());
        await page.route(mutation, async route => {
          ids.push(route.request().postDataJSON().meta.commandId);
          const response = await route.fetch(); expect(response.status()).toBe(201); created = (await response.json()).id; committed = true; await route.fulfill({ response });
        });
        await page.getByRole('button', { name: 'บันทึกร่างส่วนเพิ่ม', exact: true }).click();
        if (status === 'identity' || status === 'new-request') {
          await expect(page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true })).toBeVisible();
          if (status === 'identity') {
            await page.evaluate(() => { const channel = new BroadcastChannel('tpr10-auth-change'); channel.postMessage('changed'); channel.close(); });
            await expect(page.getByTestId('request-id')).toHaveCount(0);
          } else {
            await page.goto(`/portal/field-advances/${supplementId}`);
            await expect(page.getByTestId('request-id')).toHaveText(supplementId);
            await expect(page.getByRole('button', { name: 'โหลดใหม่', exact: true })).toBeEnabled();
          }
          await expect(page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true })).toHaveCount(0);
          await expect(page.getByRole('alert').filter({ hasText: ids[0] })).toHaveCount(0);
          expect(ids).toHaveLength(1);
        } else if (status === 401 || status === 403) {
          await expect(page.getByTestId('request-id')).toHaveCount(0);
          await expect(page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true })).toHaveCount(0);
        } else {
          await expect(page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true })).toBeVisible();
          await expect(page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true })).toHaveCount(0);
          await page.unroute(reads);
          await page.getByRole('button', { name: 'ตรวจผลคำสั่งวงเงินเดิม', exact: true }).click();
          await expect(page).toHaveURL(new RegExp(`/field-advances/${created}$`)); expect(ids).toHaveLength(2); expect(ids[1]).toBe(ids[0]);
        }
        await page.unroute(reads); await page.unroute(mutation);
      }
    });
    await test.step('Held supplement retains the pending slot without granting another submission', async () => {
      sql(`UPDATE field_advance_grant_proposals SET approval_limit=1 WHERE recipient_id='${user(2)}' AND capabilities=ARRAY['request.approve'];`);
      await page.goto(`/portal/field-advances/${request.id}`);
      await page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true }).click();
      await page.getByLabel('รายละเอียดงานส่วนเพิ่ม', { exact: true }).fill('รายการรอผู้อนุมัติที่เข้าเงื่อนไข');
      await page.getByLabel('จำนวนส่วนเพิ่ม 1', { exact: true }).fill('1');
      await page.getByLabel('เหตุผลส่วนเพิ่ม', { exact: true }).fill('ทดสอบพักสายอนุมัติ');
      await page.getByRole('button', { name: 'บันทึกร่างส่วนเพิ่ม', exact: true }).click();
      await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
      await page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true }).click();
      await expect(page.getByTestId('request-state')).toHaveText('พักสายอนุมัติ');
      await page.goto(`/portal/field-advances/${request.id}`);
      await expect(page.getByText('มีส่วนเพิ่มค้าง (Held)', { exact: false })).toBeVisible();
      await expect(page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true })).toHaveCount(0);
    });
  } finally { await Promise.all([checker, payer, ...approvers].map(c => c.close())); }
});

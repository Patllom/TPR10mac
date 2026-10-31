import { test, expect, type BrowserContext, type Page, type Locator } from '@playwright/test';
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
  expect(response.status(), await response.text()).toBe(url === root + "/requests" ? 201 : 200); return response.json();
}
const receipt = { name: 'receipt.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgaPgPAAIDAYAkYfWXAAAAAElFTkSuQmCC', 'base64') };
async function upload(page: Page) {
  const response = page.waitForResponse(r => r.url().endsWith('/payment-evidence') && r.request().method() === 'POST');
  await page.getByLabel('เพิ่มหลักฐาน', { exact: true }).setInputFiles(receipt);
  const result = await response; expect(result.status(), await result.text()).toBe(201);
  await expect(page.getByText('หลักฐานพร้อมใช้ 1 ไฟล์', { exact: true })).toBeVisible();
  return result.json();
}
async function tabTo(page: Page, target: Locator) {
  await expect(target).toBeVisible();
  for (let count = 0; count < 60 && !await target.evaluate(element => element === document.activeElement); count++)
    await page.keyboard.press('Tab');
  await expect(target).toBeFocused();
}
async function confirmProposal(page: Page) {
  const response = page.waitForResponse(r => /\/payment-proposals\/[^/]+\/confirm$/.test(r.url()) && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'ยืนยันข้อเสนอนี้', exact: true }).click();
  const result = await response; expect(result.status(), await result.text()).toBe(200);
  await expect(page.getByRole('button', { name: 'ยืนยันข้อเสนอนี้', exact: true })).toHaveCount(0);
}
test('payment review start record', async ({ page, context, browser }) => {
  const owner = await browser.newContext(), checker = await browser.newContext(), replacement = await browser.newContext();
  const approvers = await Promise.all([2, 3, 5].map(async actor => { const c = await browser.newContext(); await session(c, actor); return c; }));
  try {
    await test.step('ตรวจที่เก็บหลักฐานจริงใน disposable API container', async () => {
      const api = apiContainer();
      const probe = execFileSync('docker', ['exec', api, 'dotnet', '/fixture/fixture/TPR10.E2E.Fixture.dll', '--payment-storage-probe'], { encoding: 'utf8' });
      expect(probe).toContain('storage roundtrip bytes=3');
    });
    await session(context, 4); await session(owner, 1); await session(checker, 7);
    expect((await context.request.get('/api/v1/auth/session')).status()).toBe(200);
    await page.goto('/portal/field-advances/payments');
    await expect(page.getByRole('heading', { name: 'งานจ่ายเงินทดรอง', exact: true })).toBeVisible();
    await policy(owner, approvers[0], 18);
    // Financial actors are distinct from the owner and all request approvers.
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
    const reviewPage = await checker.newPage();
    await test.step('ยกเลิกงวดโดยผู้เสนอและผู้ตรวจต่างคน', async () => {
      await page.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await test.step('ใช้แป้นพิมพ์จัดงวดและสะสาง พร้อมข้อผิดพลาดภาษาไทยที่ผูกกับช่องกรอก', async () => {
        const amount = page.getByLabel('ยอดงวด (บาท)', { exact: true });
        await tabTo(page, amount); await page.keyboard.insertText('50.001');
        await page.keyboard.press('Tab');
        await expect(page.getByRole('combobox', { name: 'ช่องทาง', exact: true })).toBeFocused();
        const message = 'กรอกยอดงวดมากกว่าศูนย์และทศนิยมไม่เกิน 2 ตำแหน่ง';
        await expect(page.getByRole('main').getByRole('alert')).toHaveText(message);
        await expect(amount).toHaveAttribute('aria-invalid', 'true');
        await expect(amount).toHaveAccessibleDescription(message);
        await page.keyboard.press('Shift+Tab'); await expect(amount).toBeFocused();
        await page.keyboard.press('ControlOrMeta+A'); await page.keyboard.insertText('50.00');
        await page.keyboard.press('Tab');
        await expect(amount).toHaveAttribute('aria-invalid', 'false');
        await expect(page.getByText(message, { exact: true })).toHaveCount(0);
        await page.keyboard.press('Tab');
        await expect(page.getByLabel('เหตุผลจัดงวด', { exact: true })).toBeFocused();
        await page.keyboard.insertText('งวดจำลองเพื่อยกเลิก'); await page.keyboard.press('Tab');
        await expect(page.getByRole('button', { name: 'สร้างงวด', exact: true })).toBeFocused();
        await page.keyboard.press('Enter');
        await expect(page.getByTestId('installment-state')).toHaveText('ร่างงวด');
        await tabTo(page, page.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }));
        await page.keyboard.insertText('ยืนยันยังไม่ได้จ่าย');
        await tabTo(page, page.getByRole('button', { name: 'เสนอยกเลิกงวดที่ยังไม่จ่าย', exact: true }));
        await page.keyboard.press('Enter');
      });
      await expect(page.getByTestId('installment-state')).toHaveText('รอยืนยันยกเลิก');
      await reviewPage.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await reviewPage.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('ตรวจสอบว่าไม่มีการจ่ายจริง');
      await confirmProposal(reviewPage);
      await expect(reviewPage.getByTestId('installment-state')).toHaveText('ยกเลิกแล้ว');
      await expect(reviewPage.getByTestId('payment-reserved')).toHaveText('0.00');
    });
    for (const [index, channel] of ['Transfer', 'Cash'].entries()) {
      await page.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await page.getByLabel('ยอดงวด (บาท)', { exact: true }).fill('100.00');
      await test.step('เลือกช่องทาง ' + channel, () => page.getByRole('combobox', { name: 'ช่องทาง', exact: true }).selectOption(channel));
      if (channel === 'Transfer') {
        await page.getByLabel('ธนาคาร', { exact: true }).fill('TEST');
        await page.getByLabel('เลขบัญชี', { exact: true }).fill('0001234567');
        await page.getByLabel('ชื่อบัญชี', { exact: true }).fill('บัญชีจำลองผู้รับผิดชอบ');
      }
      await page.getByLabel('เหตุผลจัดงวด', { exact: true }).fill('เงินทดสอบ');
      await page.getByRole('button', { name: 'สร้างงวด', exact: true }).click();
      await page.getByRole('button', { name: 'ส่งตรวจงวด', exact: true }).click();
      await expect(page.getByTestId('payment-reserved')).toHaveText('100.00');
      const installment = await page.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).inputValue();
      await reviewPage.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await reviewPage.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(installment);
      await expect(reviewPage.getByRole('button', { name: 'สร้างงวด', exact: true })).toHaveCount(0);
      await expect(reviewPage.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      await reviewPage.getByRole('button', { name: 'ตรวจผ่านงวด', exact: true }).click();
      await expect(reviewPage.getByTestId('payment-paid')).toHaveText(index === 0 ? '0.00' : '100.00');
      await page.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
      await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toBeVisible();
      if (channel === 'Transfer') await expect(page.getByText('0001234567', { exact: true })).toBeVisible();
      let starts = 0, breakContext = false;
      await page.route("**/payment-context", route => breakContext ? route.abort("failed") : route.continue());
      await page.route('**/installments/*/start', async route => { starts++; const response = await route.fetch(); expect(response.status()).toBe(200); if (channel === 'Cash') await route.abort('failed'); else { breakContext = true; await route.fulfill({ response }); } });
      await page.getByRole('button', { name: 'เริ่มจ่าย', exact: true }).dblclick();
      await expect(page.getByText('กำลังตรวจสอบผล ห้ามจ่ายซ้ำ')).toBeVisible();
      expect(starts).toBe(1);
      await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      breakContext = false;
      await page.getByRole('button', { name: 'ตรวจผลคำสั่งเดิม', exact: true }).click();
      await expect(page.getByRole('button', { name: 'บันทึกจ่ายตรงงวด', exact: true })).toBeVisible();
      await page.unroute('**/installments/*/start'); await page.unroute('**/payment-context');
      await page.getByLabel('วันที่จ่ายจริง', { exact: true }).fill('2026-10-30');
      await page.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('จ่ายจำลองตรงงวด');
      await expect(page.getByRole('button', { name: 'บันทึกจ่ายตรงงวด', exact: true })).toBeDisabled();
      const uploaded = page.waitForResponse(r => r.url().endsWith('/payment-evidence') && r.request().method() === 'POST');
      await page.getByLabel('เพิ่มหลักฐาน', { exact: true }).setInputFiles({ name: 'receipt.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgaPgPAAIDAYAkYfWXAAAAAElFTkSuQmCC', 'base64') });
      const evidenceResponse = await uploaded; expect(evidenceResponse.status(), await evidenceResponse.text()).toBe(201);
      await expect(page.getByText('หลักฐานพร้อมใช้ 1 ไฟล์', { exact: true })).toBeVisible();
      await page.getByRole('button', { name: 'บันทึกจ่ายตรงงวด', exact: true }).click();
      await expect(page.getByTestId('payment-paid')).toHaveText(index === 0 ? '100.00' : '200.00');
      await expect(page.getByTestId('payment-reserved')).toHaveText('0.00');
      await expect(page.getByTestId('payment-available')).toHaveText(index === 0 ? '9900.00' : '9800.00');
      await expect(page.getByTestId('payment-approved')).toHaveText('10000.00');
      if (index === 0) {
        // Remove only the just-created synthetic object in this fixture's isolated API container.
        const ticket = await evidenceResponse.json();
        const location = sql(`SELECT storage_key FROM field_advance_evidence_locations WHERE evidence_id='${ticket.id}' AND is_current;`);
        expect(location).toMatch(/^v1:88400000000040008000000000000001:[a-f0-9]{32}$/);
        execFileSync('docker', ['exec', apiContainer(), 'rm', '--', '/tmp/' + location.slice(36)]);
        const read = page.waitForResponse(r => r.url().endsWith(`/payment-evidence/${ticket.id}`));
        await page.getByRole('button', { name: `ดาวน์โหลดหลักฐานคำขอ ${ticket.id}`, exact: true }).click();
        expect((await read).status()).toBe(409);
        await expect(page.getByTestId('payment-paid')).toHaveText('100.00');
        await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      }

    }
    await test.step('แก้ metadata โดยผู้ตรวจอีกคน ไม่เปลี่ยนยอดจ่าย', async () => {
      const id = await page.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).inputValue();
      for (const [index, reference] of (['REF-CORRECTED', null] as const).entries()) {
        await page.goto(`/portal/field-advances/payments?requestId=${request.id}`);
        await page.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(id);
        await page.getByLabel('วันที่จ่ายจริง', { exact: true }).fill('2026-10-29');
        await page.getByLabel('เลขอ้างอิง', { exact: true }).fill(reference ?? '');
        await page.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('แก้วันที่และเลขอ้างอิงในหลักฐานจำลอง');
        const correctedEvidence = await upload(page);
        // Authorization loss after a committed mutation clears protected UI, never a pending receipt.
        let committed = false;
        const detailsPattern = `**${root}/requests/${request.id}`;
        const metadataPattern = `**${root}/installments/${id}/metadata`;
        await page.route(detailsPattern, route => committed ? route.fulfill({ status: index === 0 ? 401 : 403, contentType: 'application/problem+json', body: '{}' }) : route.continue());
        await page.route(metadataPattern, async route => {
          const response = await route.fetch(); expect(response.status()).toBe(200);
          committed = true; await route.fulfill({ response });
        });
        const metadataResponse = page.waitForResponse(r => r.url().endsWith('/metadata') && r.request().method() === 'POST');
        await page.getByRole('button', { name: 'เสนอแก้วันจ่ายและหลักฐาน', exact: true }).click();
        const metadataResult = await metadataResponse; expect(metadataResult.status(), await metadataResult.text()).toBe(200);
        await expect(page.getByRole('main').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
        await expect(page.getByTestId('payment-approved')).toHaveCount(0);
        await expect(page.getByText('กำลังตรวจสอบผล ห้ามจ่ายซ้ำ', { exact: true })).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
        await page.unroute(detailsPattern); await page.unroute(metadataPattern);
        await reviewPage.goto(`/portal/field-advances/payments?requestId=${request.id}`);
        await reviewPage.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(id);
        await expect(reviewPage.getByText(`เลขอ้างอิงเดิม: ${index === 0 ? 'ไม่ระบุ' : 'REF-CORRECTED'}`, { exact: true })).toBeVisible();
        await expect(reviewPage.getByText(`เลขอ้างอิงใหม่: ${reference ?? 'ไม่ระบุ'}`, { exact: true })).toBeVisible();
        await reviewPage.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('ตรวจวันจ่าย เลขอ้างอิง และหลักฐานแล้ว');
        await confirmProposal(reviewPage);
        await expect(reviewPage.getByTestId('payment-paid')).toHaveText('200.00');
        // Fresh authorized read, not the submitted payload or a retained command receipt.
        const persistedResponse = await checker.request.get(`${root}/installments/${id}`);
        expect(persistedResponse.status()).toBe(200);
        const persisted = await persistedResponse.json();
        const corrections = persisted.proposals.filter((proposal: { kind: string; state: string }) => proposal.kind === 'PaymentMetadata' && proposal.state === 'Approved');
        expect(corrections).toHaveLength(index + 1);
        const correction = corrections.find((proposal: { after: { metadata: { evidenceIds: string[] } } }) => proposal.after.metadata.evidenceIds.includes(correctedEvidence.id));
        expect(correction.before.metadata.paidOn).toBe(index === 0 ? '2026-10-30' : '2026-10-29');
        expect(correction.after.metadata.paidOn).toBe('2026-10-29');
        expect(correction.before.metadata.reference).toBe(index === 0 ? null : 'REF-CORRECTED');
        expect(correction.after.metadata.reference).toBe(reference);
        expect(correction.after.metadata.evidenceIds).toEqual([correctedEvidence.id]);
        expect(correction.before.metadata.evidenceIds).not.toContain(correctedEvidence.id);
        expect(correction.evidence).toContainEqual(expect.objectContaining({ id: correctedEvidence.id, sha256: correctedEvidence.sha256, state: 'Ready' }));
        expect(persisted.summary.state).toBe('Paid');
        expect(persisted.summary.paid).toBe('200.00');
      }
    });
    const command = (v: { familyVersion: number; requestVersion: number; installmentVersion: number | null }) => ({ commandId: randomUUID(), familyVersion: v.familyVersion, requestVersion: v.requestVersion, installmentVersion: v.installmentVersion, reason: 'จัดฉากงวดจำลอง' });
    async function reviewed() {
      const summary = await (await context.request.get(`${root}/requests/${request.id}/payments`)).json();
      let row = await send(context, `${root}/requests/${request.id}/installments`, { input: { amount: '100.00', destination: { channel: 'Cash', recipientPersonId: person, bankCode: null, accountNumber: null, accountHolder: null } }, meta: command(summary) });
      row = await send(context, `${root}/installments/${row.installmentId}/submit`, command(row));
      return send(checker, `${root}/installments/${row.installmentId}/review`, { outcome: 'Approve', meta: command(row) });
    }
    const waiting = await reviewed();
    const inFlight = await reviewed();
    await test.step('start สำเร็จและ payment refresh สำเร็จ แต่ request details ขาด ต้องตรวจคำสั่งเดิม', async () => {
      await page.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await page.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(inFlight.installmentId);
      await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toBeEnabled();
      let committed = false, starts = 0, commandId = '';
      const requestDetails = `**${root}/requests/${request.id}`;
      await page.route(requestDetails, route => committed ? route.abort('failed') : route.continue());
      await page.route(`**${root}/installments/${inFlight.installmentId}/start`, async route => {
        starts++; commandId = route.request().postDataJSON().commandId;
        const response = await route.fetch(); expect(response.status()).toBe(200);
        committed = true; await route.fulfill({ response });
      });
      const refreshed = page.waitForResponse(r => committed && r.url().endsWith(`/installments/${inFlight.installmentId}`) && r.request().method() === 'GET');
      const contextRead = page.waitForResponse(r => committed && r.url().endsWith('/payment-context'));
      await page.getByRole('button', { name: 'เริ่มจ่าย', exact: true }).click();
      expect((await refreshed).status()).toBe(200); expect((await contextRead).status()).toBe(200);
      await expect(page.getByText('กำลังตรวจสอบผล ห้ามจ่ายซ้ำ', { exact: true })).toBeVisible();
      await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      expect(starts).toBe(1);
      await page.unroute(requestDetails);
      const lookup = page.waitForRequest(r => r.url().endsWith(`/payment-commands/${commandId}`) && r.method() === 'GET');
      await page.getByRole('button', { name: 'ตรวจผลคำสั่งเดิม', exact: true }).click();
      await lookup;
      await expect(page.getByRole('button', { name: 'บันทึกจ่ายตรงงวด', exact: true })).toBeVisible();
      await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      expect(starts).toBe(1);
      await page.unroute(`**${root}/installments/${inFlight.installmentId}/start`);
    });
    await test.step('รับช่วงและสะสางไม่ได้จ่ายจริง โดยไม่มีการเปิดจ่ายซ้ำอัตโนมัติ', async () => {
      const payerPerson = randomUUID(), grant = randomUUID(), approval = randomUUID();
      sql(`INSERT INTO users(id,username,normalized_username) VALUES ('${user(8)}','payment-replacement','PAYMENT-REPLACEMENT');
        INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason) SELECT gen_random_uuid(),'${user(8)}',workspace_id,department_id,now()-interval '1 day','${user(1)}','ผู้รับช่วงจำลอง' FROM employee_memberships WHERE user_id='${user(4)}';
        INSERT INTO finance_persons(id,workspace_id,created_by,reason) VALUES ('${payerPerson}','${workspace(18)}','${user(1)}','ผู้รับช่วงจำลอง');
        INSERT INTO finance_account_links(id,workspace_id,user_id,person_id,created_by,created_at_utc,reason) VALUES (gen_random_uuid(),'${workspace(18)}','${user(8)}','${payerPerson}','${user(1)}',now()-interval '1 hour','จำลอง');
        INSERT INTO field_advance_grant_proposals(id,workspace_id,project_id,recipient_id,recipient_person_id,proposed_by,proposed_by_person_id,state,capabilities,delegable_capabilities,starts_at_utc,created_at_utc,reason)
        SELECT '${grant}',workspace_id,project_id,'${user(8)}','${payerPerson}',proposed_by,proposed_by_person_id,'Approved',ARRAY['payment.record'],ARRAY[]::text[],starts_at_utc,created_at_utc,'รับช่วงจำลอง' FROM field_advance_grant_proposals WHERE recipient_id='${user(4)}' AND capabilities=ARRAY['payment.record'];
        INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,reviewer_person_id,outcome,created_at_utc,reason)
        SELECT '${approval}',r.workspace_id,'${grant}',r.reviewer_id,r.reviewer_person_id,r.outcome,r.created_at_utc,'จำลอง' FROM field_advance_grant_reviews r JOIN field_advance_grant_proposals p ON p.id=r.proposal_id WHERE p.recipient_id='${user(4)}' AND p.capabilities=ARRAY['payment.record'];
        INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,review_id,created_by,created_at_utc,reason)
        SELECT gen_random_uuid(),workspace_id,project_id,id,'${approval}',proposed_by,now(),'จำลอง' FROM field_advance_grant_proposals WHERE id='${grant}';`);
      await session(replacement, 8); const next = await replacement.newPage();
      await next.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await next.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(inFlight.installmentId);
      await expect(next.getByRole('button', { name: 'บันทึกจ่ายตรงงวด', exact: true })).toHaveCount(0);
      await next.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('รับช่วงเพื่อสอบทานเท่านั้น');
      await upload(next);
      const takeoverResponse = next.waitForResponse(r => r.url().endsWith('/takeover') && r.request().method() === 'POST');
      await next.getByRole('button', { name: 'เสนอรับช่วงสะสาง', exact: true }).click();
      const takeoverResult = await takeoverResponse; expect(takeoverResult.status(), await takeoverResult.text()).toBe(200);
      await reviewPage.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await reviewPage.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(inFlight.installmentId);
      await reviewPage.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('ตรวจการรับช่วงแล้ว');
      await confirmProposal(reviewPage);
      await expect(reviewPage.getByRole('button', { name: 'ยืนยันข้อเสนอนี้', exact: true })).toHaveCount(0);
      await next.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
      await expect(next.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      await expect(next.getByRole('button', { name: 'บันทึกจ่ายตรงงวด', exact: true })).toHaveCount(0);
      await next.getByRole('combobox', { name: 'ผลที่เสนอ', exact: true }).selectOption('Unpaid');
      await next.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('ตรวจแล้วไม่มีการจ่ายจริง');
      await upload(next);
      const outcomeResponse = next.waitForResponse(r => r.url().endsWith('/outcome') && r.request().method() === 'POST');
      await next.getByRole('button', { name: 'เสนอผลเพื่อให้ผู้ตรวจยืนยัน', exact: true }).click();
      const outcomeResult = await outcomeResponse; expect(outcomeResult.status(), await outcomeResult.text()).toBe(200);
      await reviewPage.getByRole('button', { name: 'โหลดใหม่', exact: true }).click();
      await reviewPage.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('ยืนยันไม่ได้จ่ายจริงจากหลักฐาน');
      await confirmProposal(reviewPage);
      await expect(reviewPage.getByTestId('installment-state')).toHaveText('รอตรวจ');
      await expect(reviewPage.getByTestId('payment-paid')).toHaveText('200.00');
      await expect(reviewPage.getByTestId('payment-reserved')).toHaveText('200.00');
    });
    await test.step('รายงานจ่ายผิดระงับทั้งชุดและแท็บเก่าเริ่มไม่ได้', async () => {
      const stale = await context.newPage();
      await stale.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      await stale.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(waiting.installmentId);
      await expect(stale.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toBeEnabled();
      await stale.clock.install(); await stale.clock.pauseAt(new Date());
      await page.goto(`/portal/field-advances/payments?requestId=${request.id}`);
      const paidOption = page.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).getByRole('option').filter({ hasText: /^จ่ายแล้ว/ }).first();
      const paidId = await paidOption.getAttribute('value');
      await page.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(paidId!);
      await page.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('พบยอดจริงผิดจากงวดจำลอง');
      await page.getByLabel('วันที่จ่ายจริง', { exact: true }).fill('2026-10-30');
      await page.getByLabel('ยอดที่พบจริง', { exact: true }).fill('101.00');
      await page.getByLabel('รหัสบุคคลผู้รับที่พบจริง', { exact: true }).fill(person);
      await upload(page);
      await page.getByRole('button', { name: 'รายงานเหตุจ่ายไม่ตรง', exact: true }).click();
      await expect(page.getByText('เหตุระงับ: PaymentMismatch', { exact: true })).toBeVisible();
      const staleCommand = stale.waitForResponse(r => r.url().endsWith(`/installments/${waiting.installmentId}/start`) && r.request().method() === 'POST');
      await stale.getByRole('button', { name: 'เริ่มจ่าย', exact: true }).click();
      expect((await staleCommand).status()).toBe(409);
      await expect(stale.getByText('เหตุระงับ: PaymentMismatch', { exact: true })).toBeVisible();
      await expect(stale.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      await stale.close();
      await page.getByRole('combobox', { name: 'งวดจ่าย', exact: true }).selectOption(waiting.installmentId);
      await expect(page.getByTestId('installment-state')).toHaveText('ตรวจผ่าน ยังไม่จ่าย');
      await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
      await expect(page.getByTestId('payment-paid')).toHaveText('200.00');
    });
    await page.setViewportSize({ width: 390, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.clock.install();
    sql(`UPDATE finance_account_links SET ended_at_utc=now(),version=version+1,ended_by='${user(1)}',end_reason='ทดสอบถอนตัวตน' WHERE user_id='${user(4)}';`);
    await page.clock.fastForward(16000);
    await expect(page.getByRole('main').getByRole('alert')).toContainText('ไม่มีสิทธิ์');
    await expect(page.getByTestId('payment-approved')).toHaveCount(0);
    await expect(page.getByLabel('เลขบัญชี', { exact: true })).toHaveCount(0);
  } finally { await Promise.all([owner, checker, replacement, ...approvers].map(c => c.close().catch(() => {}))); }
});

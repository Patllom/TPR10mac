import { test, expect, type BrowserContext } from '@playwright/test';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';

test.setTimeout(180000);
test.use({ actionTimeout: 15000, navigationTimeout: 30000 });

const user = (actor: number) => `88000000-0000-4000-8000-${String(1800 + actor).padStart(12, '0')}`;
const workspace = (value: number) => `88100000-0000-4000-8000-${String(value).padStart(12, '0')}`;
const project = (value: number) => `88200000-0000-4000-8000-${String(value).padStart(12, '0')}`;
const root = '/api/v1/field-advances';

function sql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{64}$/.test(database)) throw new Error('ต้องใช้ disposable HTTPS fixture');
  return execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1'], {
    input,
    encoding: 'utf8'
  }).trim();
}

async function session(context: BrowserContext, actor: number) {
  const id = user(actor);
  const token = randomBytes(32);
  const hash = createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO mfa_factors(id,user_id,protected_secret,confirmed_at_utc)
       SELECT gen_random_uuid(),'${id}','fixture-unused',now()
       WHERE NOT EXISTS (SELECT 1 FROM mfa_factors WHERE user_id='${id}');
       INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc)
       VALUES ('${randomUUID()}','${id}',decode('${hash}','hex'),'Active',now(),now(),now()+interval '1 hour',now());`);
  await context.addCookies([{
    name: '__Host-tpr10_session',
    value: token.toString('base64url'),
    domain: 'localhost',
    path: '/',
    secure: true,
    httpOnly: true,
    sameSite: 'Lax'
  }]);
  return hash;
}

async function command(context: BrowserContext, path: string, body: unknown) {
  const csrf = await (await context.request.get('/api/v1/auth/csrf')).json();
  const response = await context.request.post(path, {
    headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': csrf.token },
    data: body
  });
  const result = await response.text();
  expect([200, 201], result).toContain(response.status());
  return JSON.parse(result);
}

async function approvedPolicy(owner: BrowserContext, reviewer: BrowserContext, scopeNumber: number) {
  if (sql(`SELECT count(*) FROM field_advance_policy_revisions
    WHERE workspace_id='${workspace(scopeNumber)}' AND project_id IS NULL;`) !== '0') return;
  const content = {
    clearingDays: 7,
    bands: [{ order: 1, levelCode: 'director', upperInclusive: '100000.00' }],
    categories: [{
      id: '88300000-0000-4000-8000-000000000001',
      name: 'ค่าเดินทางจำลอง', active: true, unit: 'เที่ยว', standardRate: '100.00',
      evidenceTypes: ['ใบเสร็จ']
    }]
  };
  const policy = { mode: 'Company', sourcePolicyRevisionId: null, baseCompanyVersionId: null,
    expectedActiveRevisionId: null, expectedActiveVersion: 0, expectedContentHash: null as string | null };
  let row = await command(owner, `${root}/policies`, {
    scope: { workspaceId: workspace(scopeNumber), projectId: null },
    baseCompanyVersionId: null, content,
    meta: { commandId: randomUUID(), expectedVersion: 1, reason: 'นโยบายทดสอบ', policy }
  });
  policy.expectedContentHash = row.contentHash;
  row = await command(owner, `${root}/policies/${row.id}/submit`, {
    commandId: randomUUID(), expectedVersion: row.version, reason: 'ส่งนโยบายทดสอบ', policy
  });
  await command(reviewer, `${root}/policies/${row.id}/review`, {
    decision: 'approve',
    meta: { commandId: randomUUID(), expectedVersion: row.version, reason: 'อนุมัตินโยบายทดสอบ', policy }
  });
}

async function approvedRequest(owner: BrowserContext, approvers: BrowserContext[], scopeNumber: number) {
  await approvedPolicy(owner, approvers[0], scopeNumber);
  const ownerPerson = sql(`SELECT person_id FROM finance_account_links WHERE user_id='${user(1)}';`);
  const content = {
    kind: 'Individual', purpose: 'คำขอเดิมที่มีส่วนเพิ่ม', fieldStart: '2026-10-30',
    fieldEnd: '2026-10-31', neededOn: '2026-10-30', teamPersonIds: [],
    lines: [{ id: randomUUID(), categoryId: '88300000-0000-4000-8000-000000000001',
      description: 'งานเดิม', mode: 'QuantityRate', quantity: '100', unitRate: '100.00',
      lumpSum: null }]
  };
  let source = await command(owner, `${root}/requests`, {
    input: { scope: { workspaceId: workspace(scopeNumber), projectId: project(scopeNumber) },
      ownerPersonId: ownerPerson, content },
    meta: { commandId: randomUUID(), expectedVersion: 0, reason: 'สร้างคำขอเดิม' }
  });
  const quote = await (await owner.request.get(`${root}/requests/${source.id}/quote`)).json();
  source = await command(owner, `${root}/requests/${source.id}/confirm`, {
    confirmationHash: quote.confirmationHash,
    meta: { commandId: randomUUID(), expectedVersion: source.version, reason: 'ยืนยันคำขอเดิม' }
  });
  for (const approver of approvers) {
    const current = await (await approver.request.get(`${root}/requests/${source.id}`)).json();
    source = await command(approver, `${root}/requests/${source.id}/review`, {
      decision: { outcome: 'Approve', stepId: current.currentStep.id },
      meta: { commandId: randomUUID(), expectedVersion: current.version, reason: 'อนุมัติคำขอเดิม' }
    });
  }
  return { source, content, ownerPerson };
}

function grantFinanceChecker(scopeNumber: number, actor: number) {
  const proposal = randomUUID();
  const review = randomUUID();
  sql(`INSERT INTO field_advance_grant_proposals(id,workspace_id,project_id,recipient_id,
       recipient_person_id,proposed_by,proposed_by_person_id,state,capabilities,
       delegable_capabilities,starts_at_utc,created_at_utc,reason)
       SELECT '${proposal}','${workspace(scopeNumber)}','${project(scopeNumber)}','${user(actor)}',
         recipient.person_id,'${user(1)}',proposer.person_id,'Approved',ARRAY['finance.check'],
         ARRAY[]::text[],now()-interval '1 hour',now()-interval '1 hour','สิทธิ์ตรวจ fixture'
       FROM finance_account_links recipient CROSS JOIN finance_account_links proposer
       WHERE recipient.user_id='${user(actor)}' AND proposer.user_id='${user(1)}'
         AND recipient.workspace_id='${workspace(scopeNumber)}'
         AND proposer.workspace_id=recipient.workspace_id;
       INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,
         reviewer_person_id,outcome,created_at_utc,reason)
       SELECT '${review}','${workspace(scopeNumber)}','${proposal}','${user(2)}',person_id,
         'Approved',now()-interval '1 hour','ตรวจสิทธิ์ fixture'
       FROM finance_account_links WHERE user_id='${user(2)}' AND workspace_id='${workspace(scopeNumber)}';
       INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,
         review_id,created_by,created_at_utc,reason)
       VALUES (gen_random_uuid(),'${workspace(scopeNumber)}','${project(scopeNumber)}',
         '${proposal}','${review}','${user(2)}',now()-interval '1 hour','มอบหมาย fixture');`);
}

async function completionCommand(context: BrowserContext, requestId: string, path: string, actualEnd: string) {
  const [requestVersion, familyVersion] = sql(`SELECT r.version || ',' || f.version
    FROM field_advance_requests r JOIN field_advance_families f ON f.id=r.family_id
    WHERE r.id='${requestId}';`).split(',').map(Number);
  return command(context, `${root}/requests/${requestId}/settlement/completion/${path}`, {
    input: { actualEnd },
    meta: { commandId: randomUUID(), requestVersion, familyVersion, entityVersion: null,
      reason: 'วันจบงาน fixture' }
  });
}

test('review_queue_loads_without_automatic_financial_post', async ({ page, context }) => {
  await session(context, 1);
  const posts: string[] = [];
  page.on('request', request => {
    if (request.method() === 'POST' && request.url().includes('/settlement')) posts.push(request.url());
  });
  await page.goto('/portal/field-advances/settlements');
  await expect(page.getByRole('heading', { name: 'ตรวจเคลียร์ค่าใช้จ่ายและสะสาง', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'โหลดงานล่าสุด', exact: true }).click();
  await page.getByLabel('ประเภทงาน').selectOption('incidents');
  await expect(page.getByRole('button', { name: /สั่งโอน|จ่ายผ่านธนาคาร/ })).toHaveCount(0);
  expect(posts).toEqual([]);
});

test('review_queue_clears_protected_state_when_session_expires', async ({ page, context }) => {
  const hash = await session(context, 1);
  await page.goto('/portal/field-advances/settlements');
  await expect(page.getByRole('heading', { name: 'ตรวจเคลียร์ค่าใช้จ่ายและสะสาง' })).toBeVisible();
  sql(`UPDATE sessions SET created_at_utc=now()-interval '2 hours',
         last_seen_at_utc=now()-interval '2 hours',
         mfa_verified_at_utc=now()-interval '2 hours',
         expires_at_utc=now()-interval '1 hour'
       WHERE token_hash=decode('${hash}','hex');`);
  await page.getByRole('button', { name: 'โหลดงานล่าสุด' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'กรุณาเข้าสู่ระบบใหม่' })).toBeVisible();
  await expect(page.getByRole('button', { name: /^เปิดงาน/ })).toHaveCount(0);
});

test('review_queue_refuses_account_change_without_reusing_old_work', async ({ page, context }) => {
  await session(context, 1);
  await page.goto('/portal/field-advances/settlements');
  await expect(page.getByRole('heading', { name: 'ตรวจเคลียร์ค่าใช้จ่ายและสะสาง' })).toBeVisible();
  await context.clearCookies();
  await session(context, 2);
  await page.getByRole('button', { name: 'โหลดงานล่าสุด' }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'บัญชีเปลี่ยน' })).toBeVisible();
  await expect(page.getByRole('button', { name: /^เปิดงาน/ })).toHaveCount(0);
});

test('checker_reviews_current_completion_and_stale_tab_cannot_replay', async ({ page, context, browser }) => {
  test.setTimeout(240000);
  const scopeNumber = 18;
  const owner = await browser.newContext();
  const ownerAlias = await browser.newContext();
  const approvers = await Promise.all([2, 3, 5].map(async actor => {
    const account = await browser.newContext();
    await session(account, actor);
    return account;
  }));
  const stalePage = await context.newPage();
  try {
    await session(owner, 1);
    await session(ownerAlias, 6);
    const checkerSession = await session(context, 7);
    const { source } = await approvedRequest(owner, approvers, scopeNumber);
    grantFinanceChecker(scopeNumber, 7);
    grantFinanceChecker(scopeNumber, 6);
    await completionCommand(approvers[1], source.id, 'confirm', '2026-10-20');
    await completionCommand(approvers[1], source.id, 'correct', '2026-10-30');
    const scopesResponse = await context.request.get(`${root}/payment-scopes?page=1&pageSize=100`);
    expect(scopesResponse.status(), await scopesResponse.text()).toBe(200);
    expect(await scopesResponse.text()).toContain(project(scopeNumber));
    const queueResponse = await context.request.get(`${root}/settlement-work?workspaceId=${workspace(scopeNumber)}&projectId=${project(scopeNumber)}&lane=completion&take=25`);
    expect(queueResponse.status(), await queueResponse.text()).toBe(200);
    expect(await queueResponse.text()).toContain(source.id);
    const aliasQueue = await ownerAlias.request.get(`${root}/settlement-work?workspaceId=${workspace(scopeNumber)}&projectId=${project(scopeNumber)}&lane=completion&take=25`);
    expect(aliasQueue.status(), await aliasQueue.text()).toBe(200);
    expect((await aliasQueue.json()).items).toEqual([]);

    const route = '/portal/field-advances/settlements';
    await page.goto(route);
    await page.getByLabel('ประเภทงาน').selectOption('completion');
    await page.getByRole('button', { name: /^เปิดงาน/ }).click();
    await expect(page.getByText('วันจบเดิม 2026-10-20 → วันจบใหม่ 2026-10-30')).toBeVisible();
    await stalePage.goto(route);
    await stalePage.getByLabel('ประเภทงาน').selectOption('completion');
    await stalePage.getByRole('button', { name: /^เปิดงาน/ }).click();
    await page.getByLabel('เหตุผลการตรวจ').fill('ตรวจวันจบงานและฐานเดิมแล้ว');
    const firstResponse = page.waitForResponse(response => response.request().method() === 'POST'
      && response.url().includes('/settlement/completion/') && response.url().endsWith('/confirm'));
    await page.getByRole('button', { name: 'ยืนยันข้อเท็จจริงของรายการนี้' }).click();
    const acceptedResponse = await firstResponse;
    expect(acceptedResponse.status(), await acceptedResponse.text()).toBe(200);
    expect(sql(`SELECT state FROM field_advance_work_completions WHERE request_id='${source.id}'
      AND supersedes_id IS NOT NULL ORDER BY created_at_utc DESC LIMIT 1;`)).toBe('Accepted');
    await stalePage.getByLabel('เหตุผลการตรวจ').fill('ข้อมูลเดิมจากอีกแท็บ');
    const staleResponse = stalePage.waitForResponse(response => response.request().method() === 'POST'
      && response.url().includes('/settlement/completion/') && response.url().endsWith('/confirm'));
    await stalePage.getByRole('button', { name: 'ยืนยันข้อเท็จจริงของรายการนี้' }).click();
    expect((await staleResponse).status()).toBe(409);
    await expect(stalePage.getByRole('alert').filter({ hasText: 'ข้อมูลเปลี่ยน' })).toBeVisible();
    await completionCommand(approvers[1], source.id, 'correct', '2026-10-31');
    await page.getByRole('button', { name: 'โหลดงานล่าสุด' }).click();
    await page.getByRole('button', { name: /^เปิดงาน/ }).click();
    await expect(page.getByText('วันจบเดิม 2026-10-30 → วันจบใหม่ 2026-10-31')).toBeVisible();
    await expect(page.getByRole('button', { name: /สั่งโอน|จ่ายผ่านธนาคาร/ })).toHaveCount(0);
    sql(`UPDATE sessions SET created_at_utc=now()-interval '2 hours',
      last_seen_at_utc=now()-interval '2 hours',
      mfa_verified_at_utc=now()-interval '2 hours',
      expires_at_utc=now()-interval '1 hour'
      WHERE token_hash=decode('${checkerSession}','hex');`);
    await page.getByLabel('เหตุผลการตรวจ').fill('ตรวจหลัง session หมดอายุ');
    const pendingReceipt = page.getByRole('button', { name: 'ตรวจผลจากรหัสคำสั่ง' });
    if (await pendingReceipt.count() > 0) await pendingReceipt.click();
    else await page.getByRole('button', { name: 'ยืนยันข้อเท็จจริงของรายการนี้' }).click();
    await expect(page.getByRole('heading', { name: 'รายละเอียดงานตรวจ' })).toHaveCount(0);
    await expect(page.getByText('วันจบเดิม 2026-10-30 → วันจบใหม่ 2026-10-31')).toHaveCount(0);
    await expect(page.getByLabel('โครงการ').getByRole('option')).toHaveCount(0);
  } finally {
    await stalePage.close();
    await owner.close();
    await ownerAlias.close();
    await Promise.all(approvers.map(account => account.close()));
  }
});

test('committed_review_with_lost_response_recovers_after_reload_outside_current_queue', async ({ page, context, browser }) => {
  test.setTimeout(240000);
  const approvers = await Promise.all([2, 3, 5].map(async actor => {
    const account = await browser.newContext();
    await session(account, actor);
    return account;
  }));
  const owner = await browser.newContext();
  try {
    await session(owner, 1);
    await session(context, 7);
    const { source } = await approvedRequest(owner, approvers, 18);
    grantFinanceChecker(18, 7);
    await completionCommand(approvers[1], source.id, 'confirm', '2026-10-20');
    await completionCommand(approvers[1], source.id, 'correct', '2026-10-30');

    await page.goto('/portal/field-advances/settlements');
    await page.getByLabel('ประเภทงาน').selectOption('completion');
    await page.getByRole('button', { name: `เปิดงาน ${source.id}` }).click();
    await page.getByLabel('เหตุผลการตรวจ').fill('ตรวจผลแต่การตอบกลับขาดหาย');
    let committed = false;
    await page.route('**/settlement/completion/*/confirm', async route => {
      const response = await route.fetch();
      expect(response.status(), await response.text()).toBe(200);
      committed = true;
      await route.abort('failed');
    });
    await page.getByRole('button', { name: 'ยืนยันข้อเท็จจริงของรายการนี้' }).click();
    await expect.poll(() => committed).toBe(true);
    await expect(page.getByText('รอตรวจสอบผลรายการ')).toBeVisible();
    expect(sql(`SELECT state FROM field_advance_work_completions
      WHERE request_id='${source.id}' AND supersedes_id IS NOT NULL
      ORDER BY created_at_utc DESC LIMIT 1;`)).toBe('Accepted');

    let posts = 0;
    page.on('request', request => {
      if (request.method() === 'POST' && request.url().includes('/settlement/')) posts++;
    });
    await page.reload();
    await page.getByLabel('ประเภทงาน').selectOption('completion');
    await expect(page.getByRole('button', { name: `เปิดงาน ${source.id}` })).toHaveCount(0);
    await expect(page.getByText('คำสั่งที่ยังไม่ทราบผล')).toBeVisible();
    await page.getByRole('button', { name: 'ตรวจผลจากเซิร์ฟเวอร์' }).click();
    await expect(page.getByText('คำสั่งที่ยังไม่ทราบผล')).toHaveCount(0);
    expect(posts).toBe(0);
  } finally {
    await owner.close();
    await Promise.all(approvers.map(account => account.close()));
  }
});

test('incident_review_releases_only_its_hold_and_preserves_other_hold_and_capacity', async ({ page, context, browser }) => {
  test.setTimeout(300000);
  const owner = await browser.newContext();
  const checker = await browser.newContext();
  const approvers = await Promise.all([2, 3, 5].map(async actor => {
    const account = await browser.newContext();
    await session(account, actor);
    return account;
  }));
  try {
    await session(owner, 1);
    await session(context, 4);
    await session(checker, 7);
    const { source, ownerPerson } = await approvedRequest(owner, approvers, 18);
    sql(`UPDATE field_advance_grant_proposals SET capabilities=ARRAY['payment.record']
      WHERE recipient_id='${user(4)}' AND capabilities=ARRAY['request.delegate'];`);
    grantFinanceChecker(18, 7);

    const paymentMeta = (value: { familyVersion: number; requestVersion: number;
      installmentVersion: number | null }) => ({
      commandId: randomUUID(), familyVersion: value.familyVersion,
      requestVersion: value.requestVersion, installmentVersion: value.installmentVersion,
      reason: 'งวดทดสอบเหตุจ่ายผิด'
    });
    const summary = await (await context.request.get(`${root}/requests/${source.id}/payments`)).json();
    let installment = await command(context, `${root}/requests/${source.id}/installments`, {
      input: { amount: '100.00', destination: { channel: 'Cash', recipientPersonId: ownerPerson,
        bankCode: null, accountNumber: null, accountHolder: null } },
      meta: paymentMeta(summary)
    });
    installment = await command(context, `${root}/installments/${installment.installmentId}/submit`,
      paymentMeta(installment));
    installment = await command(checker, `${root}/installments/${installment.installmentId}/review`, {
      outcome: 'Approve', meta: paymentMeta(installment)
    });
    await command(context, `${root}/installments/${installment.installmentId}/start`, paymentMeta(installment));

    await page.goto(`/portal/field-advances/payments?requestId=${source.id}`);
    await page.getByRole('combobox', { name: 'งวดจ่าย', exact: true })
      .selectOption(installment.installmentId);
    await page.getByLabel('เหตุผลและหลักฐานประกอบ', { exact: true }).fill('พบจ่ายจริงต่ำกว่างวด');
    await page.getByLabel('วันที่จ่ายจริง', { exact: true }).fill('2026-10-30');
    await page.getByLabel('ยอดที่พบจริง', { exact: true }).fill('99.00');
    await page.getByLabel('รหัสบุคคลผู้รับที่พบจริง', { exact: true }).fill(ownerPerson);
    const evidenceResponse = page.waitForResponse(response => response.url().endsWith('/payment-evidence')
      && response.request().method() === 'POST');
    await page.getByLabel('เพิ่มหลักฐาน', { exact: true }).setInputFiles({
      name: 'receipt.png', mimeType: 'image/png',
      buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgaPgPAAIDAYAkYfWXAAAAAElFTkSuQmCC', 'base64')
    });
    const proof = await evidenceResponse;
    expect(proof.status(), await proof.text()).toBe(201);
    const evidence = await proof.json();
    const mismatch = page.waitForResponse(response => response.url().endsWith('/mismatch')
      && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'รายงานเหตุจ่ายไม่ตรง', exact: true }).click();
    expect((await mismatch).status()).toBe(200);

    const otherHold = randomUUID();
    sql(`INSERT INTO field_advance_payment_holds(id,workspace_id,project_id,family_id,
      code,reason,created_by,created_by_person_id,created_at_utc)
      SELECT '${otherHold}',r.workspace_id,r.project_id,r.family_id,'OtherHold',
        'เหตุแยกจากรายการจ่ายผิด','${user(4)}',l.person_id,now()
      FROM field_advance_requests r JOIN finance_account_links l
        ON l.user_id='${user(4)}' AND l.workspace_id=r.workspace_id
      WHERE r.id='${source.id}';`);
    const [requestVersion, familyVersion] = sql(`SELECT r.version || ',' || f.version
      FROM field_advance_requests r JOIN field_advance_families f ON f.id=r.family_id
      WHERE r.id='${source.id}';`).split(',').map(Number);
    const incident = await command(context, `${root}/requests/${source.id}/settlement/incidents/create`, {
      input: { attemptId: sql(`SELECT id FROM field_advance_payment_attempts
        WHERE request_id='${source.id}' ORDER BY created_at_utc DESC LIMIT 1;`),
      ownerReceived: '99.00', wrongRecipientReceived: '0.00', evidenceIds: [evidence.id] },
      meta: { commandId: randomUUID(), requestVersion, familyVersion, entityVersion: null,
        reason: 'เสนอข้อเท็จจริงจ่ายผิด' }
    });
    const before = sql(`SELECT spendable || ',' || reserved || ',' || incident_reservation_usage
      FROM field_advance_payment_accounts WHERE request_id='${source.id}';`);
    const reviewPage = await checker.newPage();
    await reviewPage.goto('/portal/field-advances/settlements');
    await reviewPage.getByLabel('ประเภทงาน').selectOption('incidents');
    await reviewPage.getByRole('button', { name: `เปิดงาน ${source.id}` }).click();
    await reviewPage.getByLabel('เหตุผลการตรวจ').fill('ตรวจผู้รับจริงและหลักฐานแล้ว');
    const confirm = reviewPage.waitForResponse(response => response.request().method() === 'POST'
      && response.url().endsWith(`/settlement/incidents/${incident.entityId}/confirm`));
    await reviewPage.getByRole('button', { name: 'ยืนยันข้อเท็จจริงของรายการนี้' }).click();
    expect((await confirm).status()).toBe(200);
    await expect.poll(() => sql(`SELECT state FROM field_advance_payment_incident_resolutions
      WHERE id='${incident.entityId}';`)).toBe('Accepted');
    await reviewPage.bringToFront();
    await reviewPage.getByRole('button', { name: 'โหลดงานล่าสุด' }).click();
    const releaseQueuePath = `${root}/settlement-work?workspaceId=${workspace(18)}`
      + `&projectId=${project(18)}&lane=incidents`;
    const releaseQueue = await checker.request.get(releaseQueuePath);
    expect(releaseQueue.status(), await releaseQueue.text()).toBe(200);
    expect(await releaseQueue.text()).toContain('incident-release');
    await expect(reviewPage.getByRole('button', { name: 'โหลดงานล่าสุด' })).toBeEnabled();
    await reviewPage.getByRole('button', { name: `เปิดงาน ${source.id}` }).click();
    await reviewPage.getByLabel('เหตุผลการตรวจ').fill('ปลดเฉพาะเหตุจ่ายผิดที่สะสางแล้ว');
    const release = reviewPage.waitForResponse(response => response.request().method() === 'POST'
      && response.url().endsWith(`/settlement/incidents/${incident.entityId}/release`));
    await reviewPage.getByRole('button', { name: 'ยืนยันปลดเหตุระงับนี้' }).click();
    expect((await release).status()).toBe(200);
    await expect(reviewPage.getByRole('button', { name: /^เปิดงาน/ })).toHaveCount(0);
    expect(sql(`SELECT released_at_utc IS NOT NULL FROM field_advance_payment_holds
      WHERE id='${otherHold}';`)).toBe('f');
    expect(sql(`SELECT count(*) FROM field_advance_payment_holds h
      JOIN field_advance_payment_incident_resolutions i ON i.hold_id=h.id
      WHERE i.id='${incident.entityId}' AND h.code='PaymentMismatch'
        AND h.released_at_utc IS NOT NULL;`)).toBe('1');
    const after = sql(`SELECT spendable || ',' || reserved || ',' || incident_reservation_usage
      FROM field_advance_payment_accounts WHERE request_id='${source.id}';`);
    const [beforeSpendable, beforeReserved, beforeIncident] = before.split(',').map(Number);
    const [afterSpendable, afterReserved, afterIncident] = after.split(',').map(Number);
    expect(afterSpendable - afterReserved - afterIncident)
      .toBe(beforeSpendable - beforeReserved - beforeIncident);
    const afterPayments = await context.request.get(`${root}/requests/${source.id}/payments`);
    expect(afterPayments.status()).toBe(200);
    expect(await afterPayments.text()).toContain('OtherHold');
    const [currentRequestVersion, currentFamilyVersion] = sql(`SELECT r.version || ',' || f.version
      FROM field_advance_requests r JOIN field_advance_families f ON f.id=r.family_id
      WHERE r.id='${source.id}';`).split(',').map(Number);
    const csrf = await (await context.request.get('/api/v1/auth/csrf')).json();
    const shortfall = await context.request.post(
      `${root}/requests/${source.id}/settlement/incidents/${incident.entityId}/shortfall`, {
        headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': csrf.token },
        data: { input: { amount: '1.00' }, meta: {
          commandId: randomUUID(), requestVersion: currentRequestVersion,
          familyVersion: currentFamilyVersion, entityVersion: null,
          reason: 'ต้องถูกเหตุระงับอื่นปฏิเสธ'
        } }
      });
    expect([403, 409]).toContain(shortfall.status());
    await page.goto(`/portal/field-advances/payments?requestId=${source.id}`);
    await expect(page.getByRole('button', { name: 'เริ่มจ่าย', exact: true })).toHaveCount(0);
    await reviewPage.close();
  } finally {
    await owner.close();
    await checker.close();
    await Promise.all(approvers.map(account => account.close()));
  }
});

test('closing_source_does_not_strand_existing_supplement_or_hide_adjustment', async ({ page, context, browser }) => {
  test.setTimeout(240000);
  const scopeNumber = 18;
  const approvers = await Promise.all([2, 3, 5].map(async actor => {
    const account = await browser.newContext();
    await session(account, actor);
    return account;
  }));
  try {
    await session(context, 1);
    const { source, content, ownerPerson } = await approvedRequest(context, approvers, scopeNumber);

    // The HTTPS fixture has no evidence storage. Seed only the already-paid history flag;
    // the test exercises the real supplement API, closure ordering, and browser UI.
    sql(`UPDATE field_advance_payment_accounts SET ever_paid=true
         WHERE request_id='${source.id}' AND approved_revision_id IS NOT NULL;`);

    const supplement = await command(context, `${root}/requests/${source.id}/supplements`, {
      content: { ...content, purpose: 'ส่วนเพิ่มที่สร้างก่อนปิด', lines: [{ ...content.lines[0],
        id: randomUUID(), description: 'งานส่วนเพิ่ม', quantity: '30' }] },
      meta: { commandId: randomUUID(), expectedVersion: 0, reason: 'ส่วนเพิ่มก่อนปิด' }
    });
    const supplementId = supplement.id as string;
    expect(supplementId).toBeTruthy();

    const completionId = randomUUID();
    const declarationId = randomUUID();
    const closureId = randomUUID();
    const caseId = randomUUID();
    sql(`BEGIN;
         SELECT pg_advisory_xact_lock(7241002);
         INSERT INTO field_advance_work_completions(id,workspace_id,project_id,family_id,
           request_id,command_id,proposed_by,proposed_by_person_id,reviewed_by,
           reviewed_by_person_id,reviewed_at_utc,accepted_at_utc,state,reason,snapshot_json,
           completed_on,clearing_due_on,refund_due_on,clearing_days)
         SELECT '${completionId}',r.workspace_id,r.project_id,r.family_id,r.id,c.command_id,
           '${user(1)}','${ownerPerson}','${user(3)}',pm.person_id,now(),now(),
           'Accepted','จบงานใน fixture','{}'::jsonb,'2026-10-25','2026-11-01','2026-11-01',7
         FROM field_advance_requests r
         JOIN finance_account_links pm ON pm.user_id='${user(3)}' AND pm.workspace_id=r.workspace_id
         CROSS JOIN LATERAL (SELECT command_id FROM field_advance_command_receipts
           WHERE workspace_id=r.workspace_id ORDER BY created_at_utc LIMIT 1) c
         WHERE r.id='${source.id}';
         INSERT INTO field_advance_submission_declarations(id,workspace_id,project_id,family_id,
           request_id,command_id,proposed_by,proposed_by_person_id,reviewed_by,
           reviewed_by_person_id,reviewed_at_utc,accepted_at_utc,state,reason,snapshot_json,
           work_completion_id,is_withdrawal)
         SELECT '${declarationId}',r.workspace_id,r.project_id,r.family_id,r.id,c.command_id,
           '${user(1)}','${ownerPerson}','${user(1)}','${ownerPerson}',now(),now(),
           'Accepted','ส่งครบใน fixture','{}'::jsonb,'${completionId}',false
         FROM field_advance_requests r
         CROSS JOIN LATERAL (SELECT command_id FROM field_advance_command_receipts
           WHERE workspace_id=r.workspace_id ORDER BY created_at_utc LIMIT 1 OFFSET 1) c
         WHERE r.id='${source.id}';
         UPDATE field_advance_families SET version=version+1
         WHERE id=(SELECT family_id FROM field_advance_requests WHERE id='${source.id}');
         UPDATE field_advance_requests SET state='Closed',version=version+1
         WHERE id='${source.id}';
         INSERT INTO field_advance_closure_records(id,workspace_id,project_id,family_id,
           request_id,actor_kind,accepted_at_utc,state,reason,snapshot_json,request_version,
           family_version,work_completion_id,declaration_id)
         SELECT '${closureId}',r.workspace_id,r.project_id,r.family_id,r.id,'System',now(),
           'Accepted','ปิดคำขอเดิมใน fixture','{}'::jsonb,r.version,f.version,
           '${completionId}','${declarationId}'
         FROM field_advance_requests r JOIN field_advance_families f ON f.id=r.family_id
         WHERE r.id='${source.id}';
         INSERT INTO field_advance_settlement_adjustment_cases(id,workspace_id,project_id,
           family_id,request_id,command_id,proposed_by,proposed_by_person_id,state,reason,
           snapshot_json,closure_record_id,kind,external_paid_amount)
         SELECT '${caseId}',r.workspace_id,r.project_id,r.family_id,r.id,c.command_id,
           '${user(1)}','${ownerPerson}','Pending','รายการปรับปรุงใน fixture','{}'::jsonb,
           '${closureId}','ExpenseAdjustment',0
         FROM field_advance_requests r
         CROSS JOIN LATERAL (SELECT command_id FROM field_advance_command_receipts
           WHERE workspace_id=r.workspace_id ORDER BY created_at_utc LIMIT 1 OFFSET 2) c
         WHERE r.id='${source.id}';
         COMMIT;`);

    await page.goto(`/portal/field-advances/${source.id}`);
    await expect(page.getByText('ปิดแล้ว มีรายการปรับปรุง')).toBeVisible();
    await expect(page.getByRole('button', { name: 'สร้างคำขอส่วนเพิ่ม' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'ขอส่วนเพิ่ม', exact: true })).toHaveCount(0);
    await page.goto(`/portal/field-advances/${supplementId}`);
    await page.getByRole('button', { name: 'ทบทวนและยืนยัน', exact: true }).click();
    const existingSupplementSubmit = page.getByRole('button', { name: 'ยืนยันและส่ง', exact: true });
    await expect(existingSupplementSubmit).toBeEnabled();
    await existingSupplementSubmit.click();
    await expect(page.getByTestId('request-state')).toHaveText('รออนุมัติ');
  } finally {
    await Promise.all(approvers.map(account => account.close()));
  }
});

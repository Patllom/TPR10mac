import { test, expect } from '@playwright/test';
import { browserComponent } from '../helpers/browser-component.mjs';

const actor = '11111111-1111-4111-8111-111111111111', recipient = '22222222-2222-4222-8222-222222222222';
const scope = { workspaceId: actor, projectId: recipient };
const labels = ['ระดับที่อนุมัติได้', 'ระดับที่มอบอำนาจต่อได้', 'ระดับที่แต่งตั้งเป็นผู้ดำรงบทบาทหลัก', 'ระดับที่แต่งตั้งผู้ดำรงบทบาทหลักต่อได้'];
const fields = ['approvalLevels', 'delegableApprovalLevels', 'primaryApprovalLevels', 'delegablePrimaryApprovalLevels'];
const source = { id: recipient, state: 'Pending', version: 1, spec: { recipientId: recipient, scope,
  capabilities: ['request.approve'], delegableCapabilities: ['request.approve'], approvalLimit: '123.45',
  startsAt: '2026-10-01T00:00:00Z', endsAt: null, approvalLevels: ['supervisor'], delegableApprovalLevels: ['supervisor'],
  primaryApprovalLevels: ['supervisor'], delegablePrimaryApprovalLevels: ['supervisor'] },
  assignmentId: null, assignmentVersion: null, effectiveState: 'Pending', proposedBy: actor,
  recipientName: 'Fixture recipient', canReview: false, canApprove: false, canRevoke: false };

test('raw level typing/backspace, strict payload validation, and draft lifecycle', async ({ page }) => {
  const posts: { spec: Record<string, unknown> }[] = [];
  await page.route('https://localhost:4443/**', async route => {
    const url = new URL(route.request().url());
    let body: unknown;
    if (url.pathname === '/') return route.fulfill({ contentType: 'text/html', body: '<div id="root"></div>' });
    if (url.pathname.endsWith('/session')) body = { userId: actor, stage: 'Active', permissions: [], mfaVerifiedAtUtc: null };
    else if (url.pathname.endsWith('/csrf')) body = { token: 'fixture' };
    else if (url.pathname.endsWith('/scopes')) body = [{ scope, workspaceName: 'Fixture', projectName: 'Project', capabilities: ['grant.propose'] }];
    else if (url.pathname.endsWith('/history')) body = [];
    else if (route.request().method() === 'POST') { posts.push(route.request().postDataJSON()); body = source; }
    else if (url.pathname.endsWith('/' + recipient)) body = source;
    else body = [source];
    await route.fulfill({ json: body });
  });
  await page.goto('/');
  await page.addScriptTag({ content: browserComponent('app/portal/field-advances/settings/GrantEditor.tsx') });
  await page.evaluate(props => (window as unknown as { renderEditor: (props: unknown) => void }).renderEditor(props), { userId: actor, ...scope });
  const button = (name: string) => page.getByRole('button', { name, exact: true });
  const input = (label: string) => page.getByLabel(label, { exact: true });
  async function fromSource() {
    await button('เปิดสิทธิ์').click();
    await button('เตรียมข้อเสนอใหม่จากรายการนี้').click();
  }
  await fromSource();
  for (const label of labels) {
    await input(label).fill('supervisor, ');
    await input(label).press('Backspace');
    await expect(input(label)).toHaveValue('supervisor,');
    await input(label).press('Backspace');
    await expect(input(label)).toHaveValue('supervisor');
    await input(label).pressSequentially(', director');
    await expect(input(label)).toHaveValue('supervisor, director');
  }
  await input('เหตุผล').fill('Fixture proposal');
  for (const label of labels) {
    for (const malformed of ['supervisor,', ',supervisor', 'supervisor,,director']) {
      await input(label).fill(malformed);
      await button('ส่งข้อเสนอ').click();
      await expect(page.getByRole('alert')).toBeVisible();
      expect(posts).toHaveLength(0);
      await expect(input(label)).toHaveValue(malformed);
    }
    await input(label).fill('supervisor, director');
  }
  // Full-spec validation still rejects primary levels outside their corresponding authority.
  await input(labels[2]).fill('executive');
  await button('ส่งข้อเสนอ').click();
  await expect(page.getByRole('alert')).toBeVisible(); expect(posts).toHaveLength(0);
  await input(labels[2]).fill('supervisor, director');
  // Programmatic submit deliberately does not blur the focused field.
  await button('ส่งข้อเสนอ').evaluate(element => (element as HTMLButtonElement).click());
  await expect(page.getByRole('status')).toContainText('เสนอสำเร็จ');
  expect(posts).toHaveLength(1);
  for (const field of fields) expect(posts[0].spec[field]).toEqual(['supervisor', 'director']);
  expect(posts[0].spec.approvalLimit).toBe('123.45');
  await button('เตรียมข้อเสนอใหม่จากรายการนี้').click();
  for (const label of labels) await expect(input(label)).toHaveValue('supervisor');
  await input(labels[0]).fill('unsent,');
  await button('ยกเลิกการเตรียมข้อเสนอ').click();
  await button('เสนอสิทธิ์ใหม่').click();
  for (const label of labels) await expect(input(label)).toHaveValue('');
  await input(labels[0]).fill('unsent,');
  await button('โหลดสิทธิ์ใหม่').click();
  await fromSource();
  for (const label of labels) await expect(input(label)).toHaveValue('supervisor');
  await input(labels[0]).fill('replaced-draft,');
  await fromSource();
  for (const label of labels) await expect(input(label)).toHaveValue('supervisor');
  await input(labels[0]).fill('private-draft,');
  await page.evaluate(() => { const c = new BroadcastChannel('tpr10-auth-change'); c.postMessage('changed'); c.close(); });
  await expect(input(labels[0])).toHaveCount(0);
  await button('โหลดสิทธิ์ใหม่').click();
  await button('เสนอสิทธิ์ใหม่').click();
  for (const label of labels) await expect(input(label)).toHaveValue('');
});

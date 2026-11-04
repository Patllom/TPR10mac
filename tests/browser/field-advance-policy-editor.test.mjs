import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { createRequire } from 'node:module';
import { firefox, expect } from '@playwright/test';
const require = createRequire(import.meta.url);
const webpack = require('next/dist/compiled/webpack/webpack'); webpack.init();

test('actual PolicyEditor renders legacy unset, saves positive days, clears them, and locks pending review', async () => {
  const temporary = await mkdtemp(join(tmpdir(), 'tpr10-policy-editor-'));
  let browser;
  try {
    const workspaceId = '11111111-1111-4111-8111-111111111111';
    const id = '22222222-2222-4222-8222-222222222222';
    const content = { clearingDays: 7, bands: [{ order: 1, levelCode: 'director', upperInclusive: '1000.00' }], categories: [{ id, name: 'เดินทาง', active: true, unit: null, standardRate: null, evidenceTypes: ['receipt'] }] };
    let row = { id, scope: { workspaceId, projectId: null }, state: 'Draft', version: 1, baseCompanyVersionId: null, content, contentHash: 'a'.repeat(64), mode: 'Company', sourcePolicyRevisionId: null, expectedActiveRevisionId: null, expectedActiveVersion: 0, activeRevisionId: null, activeVersion: 0 };
    const saved = [];
    // Only the HTTP boundary is simulated; React, the editor and policy client are real.
    await writeFile(join(temporary, 'entry.tsx'), `import React from 'react'; import {createRoot} from 'react-dom/client'; import Editor from ${JSON.stringify(resolve('app/portal/field-advances/settings/PolicyEditor.tsx'))}; createRoot(document.getElementById('root')!).render(<Editor userId="${workspaceId}" workspaceId="${workspaceId}" projectId={null} screen="company"/>);`);
    await writeFile(join(temporary, 'loader.cjs'), `const ts=require(${JSON.stringify(require.resolve('typescript'))});module.exports=function(source){return ts.transpileModule(source,{compilerOptions:{module:ts.ModuleKind.ESNext,target:ts.ScriptTarget.ES2022,jsx:ts.JsxEmit.ReactJSX}}).outputText;};`);
    await new Promise((accept, reject) => {
      const compiler = webpack.webpack({ mode: 'development', devtool: false, entry: join(temporary, 'entry.tsx'), output: { path: temporary, filename: 'editor.js' }, resolve: { extensions: ['.tsx', '.ts', '.js'], alias: { '@': process.cwd() }, modules: [resolve('node_modules')] }, module: { rules: [{ test: /\.tsx?$/, use: join(temporary, 'loader.cjs') }] } });
      compiler.run((error, stats) => compiler.close(closeError => error || closeError || stats.hasErrors() ? reject(error ?? closeError ?? new Error(stats.toString())) : accept()));
    });
    const script = await readFile(join(temporary, 'editor.js'), 'utf8');
    browser = await firefox.launch({ headless: true });
    const page = await browser.newPage();
    await page.route('http://localhost:4000/**', async route => {
      const request = route.request(), path = new URL(request.url()).pathname;
      const json = body => route.fulfill({ json: body });
      if (path === '/') return route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<meta charset="utf-8"><div id="root"></div><script src="/editor.js"></script>' });
      if (path === '/editor.js') return route.fulfill({ contentType: 'text/javascript; charset=utf-8', body: script });
      if (path === '/api/v1/auth/session') return json({ userId: workspaceId, stage: 'Active', permissions: [], mfaVerifiedAtUtc: '2026-10-31T00:00:00Z' });
      if (path === '/api/v1/auth/csrf') return json({ token: 'fixture-only' });
      if (path.endsWith('/scopes')) return json([{ scope: row.scope, workspaceName: 'บริษัทจำลอง', projectName: null, capabilities: ['policy.edit.company', 'policy.review'] }]);
      if (path.endsWith('/effective')) return json({ companyVersionId: id, projectVersionId: null, content: row.content, contentHash: row.contentHash, scope: row.scope, requiresReview: false });
      if (path.endsWith('/history')) return json([]);
      if (request.method() === 'PUT') {
        const body = request.postDataJSON(); saved.push(body.content);
        row = { ...row, content: body.content, version: row.version + 1, contentHash: 'b'.repeat(64) };
        return json(row);
      }
      if (path.endsWith('/' + id)) return json(row);
      if (path === '/api/v1/field-advances/policies') return json([row]);
      return route.abort();
    });
    await page.goto('http://localhost:4000/');
    await page.getByText('กติกาที่มีผล · ฐานบริษัท', { exact: false }).click();
    await expect(page.getByText('วันแก้ไข: ยังไม่ตั้งค่า', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).click();
    const days = page.getByLabel('วันแก้ไข (วันปฏิทิน)', { exact: true });
    await expect(days).toHaveValue('');
    await days.fill('5');
    await page.getByLabel('เหตุผล', { exact: true }).fill('ตั้งวันแก้ไข');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
    await expect(page.getByRole('status')).toHaveText('บันทึกสำเร็จ');
    assert.equal(saved[0].correctionDays, 5);
    await expect(days).toHaveValue('5');
    await days.fill('');
    await page.getByLabel('เหตุผล', { exact: true }).fill('ยกเลิกค่าร่าง');
    await page.getByRole('button', { name: 'บันทึกร่าง', exact: true }).click();
    await expect(page.getByRole('status')).toHaveText('บันทึกสำเร็จ');
    assert.equal(saved[1].correctionDays, null);
    row = { ...row, state: 'Pending', content: { ...row.content, correctionDays: 7 } };
    await page.reload();
    await page.getByRole('button', { name: 'เปิดฉบับ', exact: true }).click();
    await expect(days).toHaveValue('7');
    await expect(days).toBeDisabled();
  } finally {
    await browser?.close();
    await rm(temporary, { recursive: true, force: true });
  }
});

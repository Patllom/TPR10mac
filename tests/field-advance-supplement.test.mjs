import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import ts from 'typescript';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
const require = createRequire(import.meta.url), module = { exports: {} };
const source = readFileSync('app/portal/field-advances/SupplementEditor.tsx', 'utf8');
const output = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX } }).outputText;
new Function('require', 'module', 'exports', output)(require, module, module.exports);
const { default: Editor, money } = module.exports;
test('Supplement screen formats API decimals without converting to floating point', () => {
  assert.equal(money('9999999999999999.99'), '9,999,999,999,999,999.99');
  assert.equal(money('3000.00'), '3,000.00');
});
test('Supplement editor with no active category explains why saving is unavailable instead of crashing', () => {
  const html = renderToStaticMarkup(createElement(Editor, {
    source: { ownerPersonId: 'owner', scope: { projectId: 'project' }, content: { kind: 'Individual', purpose: '', fieldStart: '2026-10-30', fieldEnd: '2026-10-31', neededOn: '2026-10-30', teamPersonIds: [], lines: [] } },
    policy: { content: { categories: [{ id: 'closed', name: 'ปิดใช้', active: false, standardRate: null }] } },
    disabled: false, onSave() { assert.fail('render must not submit'); }, onCancel() {}
  }));
  assert.match(html, /ไม่มีหมวดค่าใช้จ่ายที่เปิดใช้/);
  assert.match(html, /<button[^>]*disabled=""[^>]*>บันทึกร่างส่วนเพิ่ม/);
});

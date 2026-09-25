import assert from 'node:assert/strict';
import { runHttpsSmoke } from './https-smoke-fixture.mjs';
const index = process.argv.indexOf('--spec');
const spec = index < 0 ? 'tests/e2e/identity.spec.ts' : process.argv[index + 1];
assert.ok(['tests/e2e/identity.spec.ts', 'tests/e2e/scopes.spec.ts', 'tests/e2e/scopes-boundary.spec.ts'].includes(spec), 'spec ต้องอยู่ใน allowlist');
await runHttpsSmoke({ port: process.argv[2] ?? '4001', spec, e2e: process.argv.includes('--e2e') });

import assert from 'node:assert/strict';
import { runHttpsSmoke } from './https-smoke-fixture.mjs';
assert.ok(process.argv.length <= 3, 'รับเฉพาะพอร์ต ไม่รับ --spec');
await runHttpsSmoke({ port: process.argv[2] ?? '4001', spec: 'tests/e2e/attendance-directory.spec.ts', e2e: true });

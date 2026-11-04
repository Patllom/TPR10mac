import { runHttpsSmoke } from './https-smoke-fixture.mjs';

await runHttpsSmoke({
  port: process.argv[2] ?? '4001',
  spec: 'tests/e2e/field-advance-settlement-review.spec.ts',
  e2e: true
});

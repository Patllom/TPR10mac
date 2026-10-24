import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { isDeepStrictEqual } from 'node:util';
import { httpsChildEnv } from '../infra/nginx/https-child-env.mjs';

const require = createRequire(import.meta.url);
const colorWarning = /NO_COLOR.*ignored.*FORCE_COLOR/;
const sentinel = 'HTTPS child unrelated warning';

function callerEnv(colors) {
  const env = { ...process.env };
  delete env.NO_COLOR;
  delete env.FORCE_COLOR;
  return Object.freeze({ ...env, ...colors, TPR10_ENV_SENTINEL: 'retained' });
}

function run(args, env) {
  const result = spawnSync(process.execPath, args, {
    env, encoding: 'utf8', timeout: 30000,
  });
  assert.equal(result.error, undefined);
  assert.equal(result.status, 0, result.stdout + result.stderr);
  return result;
}

// Break caught: copying conflicting flags into Node, or silencing warnings globally.
for (const [name, colors, forceColor] of [
  ['conflicting flags', { NO_COLOR: '1', FORCE_COLOR: '1' }, '1'],
  ['explicit color disable', { NO_COLOR: '1', FORCE_COLOR: '0' }, '0'],
  ['no color alone', { NO_COLOR: '1' }, '0'],
  ['empty no color', { NO_COLOR: '' }, '0'],
  ['forced color alone', { FORCE_COLOR: '2' }, '2'],
  ['empty force color', { NO_COLOR: '1', FORCE_COLOR: '' }, ''],
  ['no color preference', {}, undefined],
]) test(`HTTPS child environment: ${name}`, () => {
  const caller = callerEnv(colors);
  const before = { ...caller };
  const globalBefore = { ...process.env };
  const child = httpsChildEnv(caller);
  const result = run(['-e', `
    console.log(123);
    process.stdout.write('ENV:' + JSON.stringify({
      noColor: process.env.NO_COLOR,
      forceColor: process.env.FORCE_COLOR,
      retained: process.env.TPR10_ENV_SENTINEL,
    }) + '\\n');
    process.emitWarning(${JSON.stringify(sentinel)});
  `], child);
  assert.doesNotMatch(result.stderr, colorWarning);
  assert.ok(result.stderr.includes(sentinel), 'unrelated warnings must remain visible');
  const observed = JSON.parse(result.stdout.split('\n').find(line => line.startsWith('ENV:')).slice(4));
  assert.deepEqual(observed, { ...(forceColor === undefined ? {} : { forceColor }), retained: 'retained' });
  if (forceColor === '0') assert.doesNotMatch(result.stdout, /\x1b\[/);
  assert.notEqual(child, caller);
  assert.ok(isDeepStrictEqual(caller, before), 'caller environment must not change');
  assert.ok(isDeepStrictEqual({ ...process.env }, globalBefore), 'global environment must not change');
  const { NO_COLOR, FORCE_COLOR, ...otherCallerValues } = caller;
  const { NO_COLOR: childNoColor, FORCE_COLOR: childForceColor, ...otherChildValues } = child;
  assert.ok(isDeepStrictEqual(otherChildValues, otherCallerValues), 'unrelated environment values must be retained');
});

// Break caught: removing FORCE_COLOR leaves NO_COLOR for Playwright to collide with
// when WorkerHost unconditionally sets FORCE_COLOR=1. No browser/server is started.
for (const colors of [{ NO_COLOR: '1', FORCE_COLOR: '1' }, { NO_COLOR: '1' }]) {
  test(`Playwright worker keeps warnings visible without a color conflict: ${JSON.stringify(colors)}`, t => {
    const dir = mkdtempSync(join(tmpdir(), 'tpr10-color-worker-'));
    t.after(() => rmSync(dir, { recursive: true, force: true }));
    const config = join(dir, 'playwright.config.cjs');
    writeFileSync(config, `module.exports = {
      testDir: ${JSON.stringify(dir)}, testMatch: 'color.spec.cjs',
      outputDir: ${JSON.stringify(join(dir, 'results'))},
      workers: 1, retries: 0, reporter: 'line',
    };`);
    writeFileSync(join(dir, 'color.spec.cjs'), `
      const { test, expect } = require(${JSON.stringify(require.resolve('@playwright/test'))});
      test('child color environment', async () => {
        console.log(123);
        process.emitWarning(${JSON.stringify(sentinel)});
        expect(process.env.TPR10_ENV_SENTINEL).toBe('retained');
        console.log('HTTPS_COLOR_WORKER_RAN');
      });
    `);
    const caller = callerEnv(colors);
    const before = { ...caller };
    const result = run([require.resolve('@playwright/test/cli'), 'test', '--config', config], httpsChildEnv(caller));
    const output = result.stdout + result.stderr;
    assert.match(output, /HTTPS_COLOR_WORKER_RAN/);
    assert.doesNotMatch(output, colorWarning);
    assert.ok(output.includes(sentinel), 'worker warnings must remain visible');
    assert.ok(isDeepStrictEqual(caller, before), 'caller environment must not change');
  });
}

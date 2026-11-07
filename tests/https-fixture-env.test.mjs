import test from 'node:test';
import assert from 'node:assert/strict';
import childProcess from 'node:child_process';
import net from 'node:net';
import timers from 'node:timers/promises';
import { syncBuiltinESMExports } from 'node:module';
import { EventEmitter } from 'node:events';
import { writeFileSync } from 'node:fs';
import { runHttpsSmoke } from '../infra/nginx/https-smoke-fixture.mjs';

// Exercise the real orchestration, replacing only external process/network waits.
// Breaks caught: bypassing normalization in run(), Next or Playwright; dropping
// overrides; mutating process.env; adding warning suppression. Native direct
// calls intentionally inherit the parent environment (no normalization contract).
const cases = [
  ['conflict', { NO_COLOR: '1', FORCE_COLOR: '1' }, '1'],
  ['zero', { NO_COLOR: '1', FORCE_COLOR: '0' }, '0'],
  ['empty force', { NO_COLOR: '1', FORCE_COLOR: '' }, ''],
  ['no color', { NO_COLOR: '1' }, '0'],
  ['empty no color', { NO_COLOR: '' }, '0'],
  ['force alone', { FORCE_COLOR: '2' }, '2'],
  ['neither', {}, undefined],
];

for (const [name, colors, force] of cases) {
  test(`fixture callsite environments: ${name}`, async t => {
    const original = { ...process.env };
    t.after(() => {
      for (const key of Object.keys(process.env)) if (!(key in original)) delete process.env[key];
      Object.assign(process.env, original);
      t.mock.restoreAll();
      syncBuiltinESMExports();
    });
    delete process.env.NO_COLOR;
    delete process.env.FORCE_COLOR;
    Object.assign(process.env, colors, {
      TPR10_SMOKE_DIAGNOSTICS: '1', TPR10_E2E_GREP: 'callsite-sentinel',
      TPR10_ENV_SENTINEL: 'retained', TPR10_API_ORIGIN: 'parent-origin',
      TPR10_WEB_UPSTREAM: 'parent-upstream', NODE_EXTRA_CA_CERTS: 'parent-ca',
      PLAYWRIGHT_FIREFOX_POLICIES_JSON: 'parent-policy',
      TPR10_E2E_DATABASE: 'parent-db', TPR10_E2E_API: 'parent-api',
      ASPNETCORE_ENVIRONMENT: 'parent-environment', TPR10_SCOPE_TEST_UI: 'parent-ui',
    });
    const calls = [];
    let container = 0;
    let probe = 0;
    const result = (stdout = '', status = 0) => ({ stdout, stderr: '', status, signal: null });
    const record = (kind, command, args, options) => {
      calls.push({ kind, command, args, options });
    };
    t.mock.method(childProcess, 'spawnSync', (command, args, options) => {
      record('sync', command, args, options);
      if (command === 'openssl') return result();
      if (command === 'dotnet') {
        if (args[0] === 'ef') writeFileSync(args.at(-1), '-- schema');
        return result(args[0].endsWith('.dll') ? (args.length === 1 ? '$argon2id$fixture' : '-- seed') : '');
      }
      if (command === process.execPath) {
        if (args[0] === 'infra/nginx/render-config.mjs') writeFileSync(args[1], 'server_name localhost;');
        else assert.equal(args[0], 'node_modules/@playwright/test/cli.js');
        return result();
      }
      if (command === 'docker') {
        if (args[0] === 'run') return result(`container-${++container}`);
        if (args[0] === 'network' && args[1] === 'create') return result('network-id');
        assert.ok(['exec', 'logs', 'rm', 'network'].includes(args[0]));
        return result();
      }
      assert.equal(command, 'curl', 'unexpected subprocess must not escape interception');
      if (!args.includes('--cacert')) return result('', 60);
      if (args.includes('Host: evil.example')) return result('', 52);
      const url = args.at(-1);
      if (url.endsWith('/api/health/ready')) return result('ready');
      if (url.endsWith('/api/v1/auth/csrf')) {
        if (args.includes('-D')) writeFileSync(args[args.indexOf('-D') + 1],
          'cache-control: no-store\nset-cookie: __Host-tpr10_preauth=x; secure; samesite=lax; httponly');
        return result('{"token":"fixture"}');
      }
      if (url.endsWith('/api/v1/system/technical-probes')) {
        if (args.includes('--data')) writeFileSync(args[args.indexOf('-o') + 1], 'denied');
        return result(['403', '401', '201'][probe++]);
      }
      assert.equal(url, 'https://localhost:4443/');
      return result('<!doctype html>');
    });
    t.mock.method(childProcess, 'spawn', (command, args, options) => {
      record('async', command, args, options);
      const child = new EventEmitter();
      child.exitCode = null;
      child.kill = () => { child.exitCode = 0; queueMicrotask(() => child.emit('exit', 0)); };
      return child;
    });
    t.mock.method(net, 'createServer', () => {
      const server = new EventEmitter();
      server.listen = (_port, _host, done) => { done(); return server; };
      server.close = done => done();
      return server;
    });
    t.mock.method(timers, 'setTimeout', async () => {});
    t.mock.method(globalThis, 'fetch', async () => ({ ok: true }));
    syncBuiltinESMExports();

    // Covers both Next modes, scope UI on/off, E2E off/on, storage and each SQL
    // selector. Every run completes acceptance and finally cleanup.
    for (const [port, spec, e2e, diagnostics = true] of [
      ['4001', 'identity', false, false], ['4001', 'identity', true, false],
      ['4001', 'identity', false], ['4000', 'identity', true],
      ['4001', 'scopes', true], ['4001', 'attendance-storage', true],
      ['4001', 'attendance-directory', true], ['4001', 'field-advance-request', true],
    ]) {
      process.env.TPR10_SMOKE_DIAGNOSTICS = diagnostics ? '1' : '0';
      process.env.TPR10_E2E_GREP = diagnostics ? 'callsite-sentinel' : '';
      const parent = { ...process.env };
      calls.length = 0; container = 0; probe = 0;
      await runHttpsSmoke({ port, spec: `tests/e2e/${spec}.spec.ts`, e2e });
      assert.deepEqual({ ...process.env }, parent, 'fixture must leave parent untouched');
      const normalized = calls.filter(c => c.options?.env);
      const native = calls.filter(c => !c.options?.env);
      assert.equal(normalized.length, 22 + (e2e ? 9 : 0) + (e2e && spec === 'attendance-storage' ? 1 : 0) + (e2e && !['identity', 'attendance-storage'].includes(spec) ? 2 : 0));
      for (const { command, args, options } of normalized) {
        const env = options.env;
        const label = `${command} ${args.slice(0, 2).join(' ')}`;
        assert.notEqual(env, process.env, label);
        assert.equal(Object.hasOwn(env, 'NO_COLOR'), false, label);
        assert.equal(env.FORCE_COLOR, force, label);
        assert.equal(env.TPR10_ENV_SENTINEL, 'retained', label);
        for (const key of ['NODE_OPTIONS', 'NODE_NO_WARNINGS', 'NODE_DISABLE_COLORS']) {
          assert.equal(env[key], parent[key], `${label}: must not suppress warnings`);
        }
      }
      const envFor = predicate => {
        const matches = normalized.filter(predicate);
        assert.equal(matches.length, 1);
        return matches[0].options.env;
      };
      const migration = envFor(c => c.args[0] === 'ef');
      assert.equal(migration.ASPNETCORE_ENVIRONMENT, 'Development');
      const render = envFor(c => c.args[0] === 'infra/nginx/render-config.mjs');
      assert.equal(render.TPR10_WEB_UPSTREAM, `host.docker.internal:${port}`);
      assert.equal(render.TPR10_API_UPSTREAM, '127.0.0.1:5080');
      assert.equal(render.TPR10_TLS_CERT, '/tls/server.pem');
      assert.equal(render.TPR10_TLS_KEY, '/tls/server.key');
      const webCall = calls.find(c => c.kind === 'async');
      assert.deepEqual(webCall.args, ['node_modules/next/dist/bin/next', port === '4000' ? 'dev' : 'start', '-p', port, '--hostname', '127.0.0.1']);
      if (diagnostics) {
        assert.equal(webCall.options.stdio[0], 'ignore');
        assert.equal(typeof webCall.options.stdio[1], 'number');
        assert.equal(webCall.options.stdio[1], webCall.options.stdio[2]);
      } else assert.equal(webCall.options.stdio, 'ignore');
      const web = envFor(c => c.kind === 'async');
      assert.equal(web.TPR10_API_ORIGIN, 'https://localhost:4443');
      assert.match(web.NODE_EXTRA_CA_CERTS, /\/tpr10-tls-smoke-[^/]+\/ca.pem$/);
      assert.equal(web.TPR10_SCOPE_TEST_UI, spec === 'scopes' ? 'true' : '');
      if (e2e) {
        assert.equal(calls.find(c => c.args[0] === 'node_modules/@playwright/test/cli.js').options.stdio, 'inherit', 'browser warnings must remain visible');
        const browser = envFor(c => c.args[0] === 'node_modules/@playwright/test/cli.js');
        assert.equal(browser.NODE_EXTRA_CA_CERTS, web.NODE_EXTRA_CA_CERTS);
        assert.equal(browser.PLAYWRIGHT_FIREFOX_POLICIES_JSON, web.NODE_EXTRA_CA_CERTS.replace('ca.pem', 'firefox-policies.json'));
        assert.equal(browser.TPR10_E2E_DATABASE, 'container-1');
        assert.equal(browser.TPR10_E2E_API, 'container-3');
        assert.deepEqual(calls.find(c => c.args[0] === 'node_modules/@playwright/test/cli.js').args,
          ['node_modules/@playwright/test/cli.js', 'test', `tests/e2e/${spec}.spec.ts`, ...(diagnostics ? ['--grep', 'callsite-sentinel'] : [])]);
      }
      // All six direct native callsites, including three container removals.
      assert.deepEqual(native.map(c => c.command === 'docker' ? c.args.slice(0, c.args[0] === 'network' ? 2 : 1).join(' ') : c.args.includes('--cacert') ? 'curl hostile' : 'curl untrusted'),
        ['exec', ...(e2e && diagnostics ? ['logs'] : []), 'curl untrusted', 'curl hostile', 'rm', 'rm', 'rm', 'network rm']);
      for (const call of native) assert.equal(Object.hasOwn(call.options ?? {}, 'env'), false);
    }
  });
}

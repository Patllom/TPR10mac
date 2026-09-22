import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
const render = 'infra/nginx/render-config.sh';
test('render preserves nginx variables and both upstream paths', () => {
  const path = join(mkdtempSync(join(tmpdir(), 'tpr10-proxy-')), 'default.conf');
  const result = spawnSync('sh', [render, path], { encoding: 'utf8', env: {
    ...process.env, TPR10_WEB_UPSTREAM: '127.0.0.1:4001', TPR10_API_UPSTREAM: '127.0.0.1:5080',
  } });
  assert.equal(result.status, 0, result.stderr);
  const output = readFileSync(path, 'utf8');
  assert.match(output, /proxy_pass http:\/\/127\.0\.0\.1:5080;/);
  assert.match(output, /proxy_pass http:\/\/127\.0\.0\.1:4001;/);
  assert.match(output, /\$host/);
  assert.match(output, /\$proxy_add_x_forwarded_for/);
});
test('render rejects directive injection and invalid ports', () => {
  for (const upstream of ['host:80;bad', 'host/base:80', 'host:0', 'host:65536']) {
    const result = spawnSync('sh', [render, '/private/tmp/tpr10-should-not-render'], { encoding: 'utf8',
      env: { ...process.env, TPR10_WEB_UPSTREAM: upstream, TPR10_API_UPSTREAM: '127.0.0.1:5080' } });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /TPR10_.*UPSTREAM/);
  }
});
test('migration validation allows only the dedicated local database', () => {
  const result = spawnSync(process.execPath, ['ops/migrations/validate-local.mjs'], { encoding: 'utf8',
    env: { ...process.env, TPR10_CONNECTION_STRING: 'Host=127.0.0.1;Port=54329;Database=tpr10;Username=tpr10_app;Password=test-only' } });
  assert.equal(result.status, 0, result.stderr);
});
for (const connection of ['', 'Host=production;Port=54329;Database=tpr10;Username=tpr10_app',
  'Host=localhost;Port=5432;Database=tpr10;Username=tpr10_app',
  'Host=localhost;Port=54329;Database=other;Username=tpr10_app',
  'Host=localhost;Host=production;Port=54329;Database=tpr10;Username=tpr10_app']) {
  test('migration rejects non-local or ambiguous destination: ' + connection, () => {
    const result = spawnSync(process.execPath, ['ops/migrations/validate-local.mjs'], { encoding: 'utf8',
      env: { ...process.env, TPR10_CONNECTION_STRING: connection } });
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /ปลายทางฐานข้อมูล local ไม่ถูกต้อง/);
    assert.doesNotMatch(result.stdout, /Password=/);
  });
}

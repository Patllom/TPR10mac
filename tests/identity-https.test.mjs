import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

function render(overrides = {}) {
  const directory = mkdtempSync(join(tmpdir(), 'tpr10-https-render-'));
  const target = join(directory, 'https.conf');
  const result = spawnSync(process.execPath, ['infra/nginx/render-config.mjs', target, '--https'], {
    encoding: 'utf8', env: { ...process.env, TPR10_WEB_UPSTREAM: '127.0.0.1:4000',
      TPR10_API_UPSTREAM: '127.0.0.1:5080', TPR10_TLS_CERT: '/private/tls/localhost.pem',
      TPR10_TLS_KEY: '/private/tls/localhost-key.pem', ...overrides }
  });
  return { result, target, cleanup: () => rmSync(directory, { recursive: true, force: true }) };
}

for (const port of [4000, 4001]) test(`HTTPS proxy รักษา authority และใช้ web port ${port}`, () => {
  const run = render({ TPR10_WEB_UPSTREAM: `127.0.0.1:${port}` });
  try {
    assert.equal(run.result.status, 0, run.result.stderr);
    const text = readFileSync(run.target, 'utf8');
    assert.match(text, /listen 4443 ssl/);
    assert.match(text, /ssl_certificate \/private\/tls\/localhost.pem;/);
    assert.match(text, /X-Forwarded-Host \$http_host/);
    assert.match(text, /X-Forwarded-For \$remote_addr/);
    assert.doesNotMatch(text, /\$proxy_add_x_forwarded_for/);
    assert.match(text, /return 444/);
    assert.match(text, new RegExp(`proxy_pass http://127.0.0.1:${port}`));
  } finally { run.cleanup(); }
});

for (const invalid of [
  { TPR10_TLS_CERT: '/tmp/cert; injected' },
  { TPR10_TLS_KEY: 'relative-key.pem' },
  { TPR10_WEB_UPSTREAM: '127.0.0.1:3000' },
  { TPR10_API_UPSTREAM: 'untrusted.example:5080' }
]) test(`HTTPS renderer ปฏิเสธค่าที่ไม่ปลอดภัย ${Object.keys(invalid)[0]}=${Object.values(invalid)[0]}`, () => {
  const run = render(invalid);
  try { assert.notEqual(run.result.status, 0); }
  finally { run.cleanup(); }
});

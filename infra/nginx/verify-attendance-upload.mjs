// ทดสอบ nginx จริงบนพอร์ตสุ่ม ไม่แตะ container/พอร์ต Preview ที่เปิดอยู่
import assert from 'node:assert/strict';
import http from 'node:http';
import https from 'node:https';
import { mkdtemp, writeFile, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { execFileSync } from 'node:child_process';
import { once } from 'node:events';

const run = (command, args) => execFileSync(command, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
const scratch = await mkdtemp(join(tmpdir(), 'tpr10-capture-proxy-'));
const observed = [];
const upstream = http.createServer(async (req, res) => {
  let bytes = 0; for await (const chunk of req) bytes += chunk.length;
  observed.push({ path: req.url, bytes, host: req.headers.host, origin: req.headers.origin, proto: req.headers['x-forwarded-proto'], csrf: req.headers['x-csrf-token'] });
  res.writeHead(204); res.end();
});
let container;
try {
  upstream.listen(0, '0.0.0.0'); await once(upstream, 'listening');
  const upstreamPort = upstream.address().port;
  run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', join(scratch, 'key.pem'), '-out', join(scratch, 'cert.pem'), '-days', '1', '-subj', '/CN=localhost']);
  const template = process.argv.includes('--baseline')
    ? run('git', ['show', '3151bb3:infra/nginx/identity-local-https.conf.template'])
    : await readFile(new URL('./identity-local-https.conf.template', import.meta.url), 'utf8');
  const config = template.replaceAll('${TPR10_API_UPSTREAM}', `host.docker.internal:${upstreamPort}`)
    .replaceAll('${TPR10_WEB_UPSTREAM}', `host.docker.internal:${upstreamPort}`)
    .replaceAll('${TPR10_TLS_CERT}', '/etc/test/cert.pem').replaceAll('${TPR10_TLS_KEY}', '/etc/test/key.pem');
  await writeFile(join(scratch, 'default.conf'), config);
  container = run('docker', ['run', '--rm', '-d', '-p', '127.0.0.1::4443', '-v', `${scratch}:/etc/test:ro`, '-v', `${join(scratch, 'default.conf')}:/etc/nginx/conf.d/default.conf:ro`, 'nginx:1.28-alpine']);
  const port = Number(run('docker', ['port', container, '4443/tcp']).split(':').at(-1));
  function request(path, size, host = 'localhost:4443') {
    return new Promise((resolve, reject) => {
      const req = https.request({ hostname: '127.0.0.1', port, path, method: 'POST', rejectUnauthorized: false, servername: 'localhost',
        headers: { Host: host, Origin: 'https://localhost:4443', 'X-CSRF-Token': 'synthetic-proxy-test', 'Content-Length': size } }, res => {
        res.resume(); res.on('end', () => resolve(res.statusCode));
      });
      req.on('error', reject); req.setTimeout(5000, () => req.destroy(Error('proxy timeout'))); req.end(Buffer.alloc(size));
    });
  }
  let ready = false;
  for (let attempt = 0; attempt < 40; attempt++) {
    try { await request('/api/health/live', 0); ready = true; break; } catch { await new Promise(resolve => setTimeout(resolve, 100)); }
  }
  assert.ok(ready, 'nginx starts on isolated random port'); observed.length = 0;
  for (const path of ['/api/v1/attendance/check-ins', '/api/v1/attendance/check-outs']) {
    assert.equal(await request(path, 10 * 1024 * 1024 + 16384), 204, 'image plus metadata passes dedicated route');
    assert.equal(await request(path, 11 * 1024 * 1024 + 1), 413, '11MiB+1 rejected');
  }
  for (const path of ['/api/v1/auth/login', '/api/v1/attendance/check-ins/extra', '/api/v1/attendance/capture-challenges'])
    assert.equal(await request(path, 1024 * 1024 + 1), 413, 'other routes retain 1MiB');
  assert.equal(observed.length, 2);
  for (const entry of observed) { assert.equal(entry.host, 'localhost:4443'); assert.equal(entry.origin, 'https://localhost:4443'); assert.equal(entry.proto, 'https'); assert.equal(entry.csrf, 'synthetic-proxy-test'); }
  await assert.rejects(request('/api/v1/attendance/check-ins', 0, 'evil.example'));
  console.log('ผ่าน HTTPS nginx จริง: upload 2 routes, จำกัด11MiB, routeอื่น1MiB, authority/CSRF headers และปฏิเสธHostอื่น');
} finally {
  if (container) run('docker', ['stop', container]);
  upstream.closeAllConnections(); await new Promise(resolve => upstream.close(resolve));
  await rm(scratch, { recursive: true, force: true });
}

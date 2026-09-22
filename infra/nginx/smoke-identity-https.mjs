// Acceptance แบบ opt-in: Docker + .NET 10 + OpenSSL + Next build ที่สร้างแล้ว
// ไม่เพิ่ม CA ใน trust store, ไม่พิมพ์ token/cookie/key และล้างเฉพาะทรัพยากรที่สร้างเอง
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { createServer } from 'node:net';

const root = mkdtempSync(join(tmpdir(), 'tpr10-tls-smoke-'));
const suffix = root.split('-').at(-1).toLowerCase();
const containers = [];
let network;
let web;
const port = process.argv[2] ?? '4001';
assert.ok(['4000', '4001'].includes(port), 'ใช้ port 4000 หรือ 4001 เท่านั้น');
const run = (command, args, options = {}) => {
  const result = spawnSync(command, args, { encoding: 'utf8', timeout: 180000, ...options });
  if (result.status !== 0) throw new Error(`${command} ${args.slice(0, 2).join(' ')} ล้มเหลว: ${result.error?.message ?? result.stderr}`);
  return result.stdout.trim();
};
const docker = (...args) => run('docker', args);
const startContainer = (...args) => {
  const id = docker('run', '-d', ...args);
  containers.push(id);
  return id;
};
async function waitFor(check, label) {
  for (let attempt = 0; attempt < 60; attempt++) {
    try { if (await check()) return; } catch { /* รอเฉพาะ readiness */ }
    await delay(500);
  }
  throw new Error(`ไม่พร้อมภายใน 30 วินาที: ${label}`);
}
async function ensurePortFree(number) {
  const server = createServer();
  await new Promise((accept, reject) => server.once('error', reject).listen(number, '127.0.0.1', accept));
  await new Promise(accept => server.close(accept));
}
const tls = (path, args = []) => run('curl', ['--silent', '--show-error', '--max-time', '10', '--cacert', join(root, 'ca.pem'), ...args, `https://localhost:4443${path}`]);

try {
  await ensurePortFree(Number(port));
  await ensurePortFree(4443);
  console.log('เตรียม CA ชั่วคราวและ publish API');
  run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=TPR10 isolated smoke CA', '-keyout', join(root, 'ca.key'), '-out', join(root, 'ca.pem')]);
  run('openssl', ['req', '-newkey', 'rsa:2048', '-nodes', '-subj', '/CN=localhost', '-keyout', join(root, 'server.key'), '-out', join(root, 'server.csr')]);
  writeFileSync(join(root, 'server.ext'), 'subjectAltName=DNS:localhost\nbasicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\n', { mode: 0o600 });
  run('openssl', ['x509', '-req', '-in', join(root, 'server.csr'), '-CA', join(root, 'ca.pem'), '-CAkey', join(root, 'ca.key'), '-CAcreateserial', '-days', '1', '-extfile', join(root, 'server.ext'), '-out', join(root, 'server.pem')]);
  run('dotnet', ['publish', 'backend/src/TPR10.Api', '-c', 'Release', '--no-self-contained', '-o', join(root, 'api')]);
  run('dotnet', ['ef', 'migrations', 'script', '--project', 'backend/src/TPR10.Api', '--output', join(root, 'schema.sql')], { env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' } });
  run(process.execPath, ['infra/nginx/render-config.mjs', join(root, 'https.conf'), '--https'], {
    env: { ...process.env, TPR10_WEB_UPSTREAM: `host.docker.internal:${port}`, TPR10_API_UPSTREAM: '127.0.0.1:5080', TPR10_TLS_CERT: '/tls/server.pem', TPR10_TLS_KEY: '/tls/server.key' }
  });
  console.log('เริ่ม PostgreSQL, nginx และ API ในเครือข่ายทดสอบแยก');
  network = docker('network', 'create', `tpr10-csrf-${suffix}`);
  const database = startContainer('--network', network, '--network-alias', 'smoke-db', '-e', 'POSTGRES_HOST_AUTH_METHOD=trust', 'postgres:17.11-alpine3.24');
  await waitFor(() => spawnSync('docker', ['exec', database, 'pg_isready', '-h', '127.0.0.1', '-U', 'postgres'], { stdio: 'ignore' }).status === 0, 'PostgreSQL TCP');
  run('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input: readFileSync(join(root, 'schema.sql'), 'utf8') });
  const proxy = startContainer('--network', network, '-p', '127.0.0.1:4443:4443',
    '-v', `${root}:/tls:ro`, '-v', `${join(root, 'https.conf')}:/etc/nginx/conf.d/default.conf:ro`, 'nginx:1.28-alpine');
  // API อยู่ namespace เดียวกับ proxy จึงเห็น trusted hop เป็น loopback; ไม่มี publish API port
  const api = startContainer('--network', `container:${proxy}`, '-v', `${join(root, 'api')}:/app:ro`, '-w', '/app',
    '-e', 'ASPNETCORE_ENVIRONMENT=Testing', '-e', 'ASPNETCORE_URLS=http://127.0.0.1:5080',
    '-e', 'TPR10_CONNECTION_STRING=Host=smoke-db;Database=postgres;Username=postgres',
    'mcr.microsoft.com/dotnet/aspnet:10.0', 'dotnet', 'TPR10.Api.dll');
  await waitFor(() => tls('/api/health/ready', ['--fail']).includes('ready'), 'API ผ่าน HTTPS');
  web = spawn(process.execPath, ['node_modules/next/dist/bin/next', port === '4000' ? 'dev' : 'start', '-p', port, '--hostname', '127.0.0.1'], { stdio: 'ignore' });
  await waitFor(async () => (await fetch(`http://127.0.0.1:${port}`)).ok, 'Next');
  assert.match(tls('/', ['--fail']), /<!doctype html>/i);
  console.log('TLS และหน้าเว็บผ่าน; ตรวจ cookie/CSRF ผ่าน proxy จริง');
  const untrusted = spawnSync('curl', ['--silent', '--max-time', '10', 'https://localhost:4443/api/health/live']);
  assert.equal(untrusted.status, 60, 'CA ที่ยังไม่เชื่อถือต้องถูกปฏิเสธ');
  const issued = JSON.parse(tls('/api/v1/auth/csrf', ['--fail', '-D', join(root, 'headers'), '-c', join(root, 'cookies')]));
  const headers = readFileSync(join(root, 'headers'), 'utf8');
  assert.match(headers, /cache-control: no-store/i);
  assert.match(headers, /set-cookie: __Host-tpr10_preauth=.*secure.*samesite=lax.*httponly/i);
  assert.doesNotMatch(headers, /domain=/i);
  const missingStatus = tls('/api/v1/system/technical-probes', ['-H', 'Content-Type: application/json', '--data', '{}', '-o', join(root, 'denied-body'), '-w', '%{http_code}']);
  assert.equal(missingStatus, '403', readFileSync(join(root, 'denied-body'), 'utf8'));
  const requestConfig = join(root, 'request.conf');
  writeFileSync(requestConfig, `header = "Origin: https://localhost:4443"\nheader = "X-CSRF-Token: ${issued.token}"\nheader = "Content-Type: application/json"\ndata = "{\\"note\\":\\"tls-smoke\\"}"\n`, { mode: 0o600 });
  assert.equal(tls('/api/v1/system/technical-probes', ['-b', join(root, 'cookies'), '--config', requestConfig, '-o', '/dev/null', '-w', '%{http_code}']), '201');
  const forged = spawnSync('curl', ['--silent', '--max-time', '10', '--cacert', join(root, 'ca.pem'), '-H', 'Host: evil.example', 'https://localhost:4443/api/v1/auth/csrf']);
  assert.equal(forged.status, 52, 'Host ที่ไม่อนุญาตต้องถูกปิด connection');
  assert.equal(docker('exec', proxy, 'nginx', '-t').includes('failed'), false);
  assert.ok(api);
  console.log(`ผ่าน HTTPS acceptance: web${port}, trust CA, cookie flags, 403/201, hostile Host; API ไม่เปิดพอร์ตสาธารณะ`);
} finally {
  if (web) {
    web.kill('SIGTERM');
    await Promise.race([new Promise(done => web.once('exit', done)), delay(3000)]);
    if (web.exitCode === null) web.kill('SIGKILL');
  }
  for (const id of containers.reverse()) spawnSync('docker', ['rm', '-f', id], { stdio: 'ignore', timeout: 15000 });
  if (network) spawnSync('docker', ['network', 'rm', network], { stdio: 'ignore', timeout: 15000 });
  rmSync(root, { recursive: true, force: true });
}

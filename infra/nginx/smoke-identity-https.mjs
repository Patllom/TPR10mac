// Acceptance แบบ opt-in: Docker + .NET 10 + OpenSSL + Next build ที่สร้างแล้ว
// ไม่เพิ่ม CA ใน trust store, ไม่พิมพ์ token/cookie/key และล้างเฉพาะทรัพยากรที่สร้างเอง
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawn, spawnSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { createServer } from 'node:net';
import { createHash, randomBytes, randomUUID } from 'node:crypto';

const root = mkdtempSync(join(tmpdir(), 'tpr10-tls-smoke-'));
const suffix = root.split('-').at(-1).toLowerCase();
const containers = [];
let network;
let web;
const port = process.argv[2] ?? '4001';
assert.ok(['4000', '4001'].includes(port), 'ใช้ port 4000 หรือ 4001 เท่านั้น');
const specIndex = process.argv.indexOf('--spec');
const spec = specIndex < 0 ? 'tests/e2e/identity.spec.ts' : process.argv[specIndex + 1];
assert.ok(['tests/e2e/identity.spec.ts', 'tests/e2e/scopes.spec.ts', 'tests/e2e/scopes-boundary.spec.ts'].includes(spec), 'spec ต้องอยู่ใน allowlist');
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
  run('openssl', ['pkcs12', '-export', '-inkey', join(root, 'server.key'), '-in', join(root, 'server.pem'), '-out', join(root, 'protection.pfx'), '-passout', 'pass:']);
  mkdirSync(join(root, 'keys'), { mode: 0o700 });
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
  if (process.argv.includes('--e2e')) {
    run('dotnet', ['build', 'backend/tests/TPR10.E2E.Fixture', '-o', join(root, 'fixture'), '--verbosity', 'quiet']);
    const passwordHash = run('dotnet', [join(root, 'fixture', 'TPR10.E2E.Fixture.dll')]);
    assert.match(passwordHash, /^\$argon2id\$/);
    for (const [username, roleClass, forced] of [
      ['e2e-staff', 'staff', false], ['e2e-other', 'staff', false], ['e2e-optional', 'staff', false],
      ['e2e-forced', 'staff', true], ['e2e-admin', 'system-administration', false]
    ]) {
      const user = randomUUID(); const role = randomUUID();
      run('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input: `
        INSERT INTO users(id,username,normalized_username) VALUES ('${user}','${username}','${username.toUpperCase()}');
        INSERT INTO local_credentials(user_id,password_hash,must_change_password,password_changed_at_utc)
          VALUES ('${user}','${passwordHash}',${forced},now());
        INSERT INTO roles(id,name,role_class) VALUES ('${role}','${username}','${roleClass}');
        INSERT INTO user_roles(user_id,role_id) VALUES ('${user}','${role}');
      ` });
    }
    if (spec !== 'tests/e2e/identity.spec.ts') {
      const sql = run('dotnet', [join(root, 'fixture', 'TPR10.E2E.Fixture.dll'), '--scope-sql']);
      run('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input: sql });
    }
  }
  const proxy = startContainer('--network', network, '-p', '127.0.0.1:4443:4443',
    '-v', `${root}:/tls:ro`, '-v', `${join(root, 'https.conf')}:/etc/nginx/conf.d/default.conf:ro`, 'nginx:1.28-alpine');
  // API อยู่ namespace เดียวกับ proxy จึงเห็น trusted hop เป็น loopback; ไม่มี publish API port
  const api = startContainer('--network', `container:${proxy}`, '-v', `${join(root, 'api')}:/app:ro`, '-w', '/app',
    '-v', `${root}:/fixture:ro`, '-v', `${join(root, 'keys')}:/keys`,
    '-e', 'ASPNETCORE_ENVIRONMENT=Testing', '-e', 'ASPNETCORE_URLS=http://127.0.0.1:5080',
    '-e', 'Identity__Csrf__KeyRingPath=/keys', '-e', 'Identity__Csrf__CertificatePath=/fixture/protection.pfx',
    '-e', `Identity__Csrf__PerIpPermitLimit=${process.argv.includes('--e2e') ? '1000' : '20'}`,
    '-e', `Identity__Csrf__GlobalPermitLimit=${process.argv.includes('--e2e') ? '10000' : '600'}`,
    '-e', 'TPR10_CONNECTION_STRING=Host=smoke-db;Database=postgres;Username=postgres',
    'mcr.microsoft.com/dotnet/aspnet:10.0', 'dotnet', 'TPR10.Api.dll');
  await waitFor(() => tls('/api/health/ready', ['--fail']).includes('ready'), 'API ผ่าน HTTPS');
  web = spawn(process.execPath, ['node_modules/next/dist/bin/next', port === '4000' ? 'dev' : 'start', '-p', port, '--hostname', '127.0.0.1'], {
    stdio: 'ignore',
    env: { ...process.env, TPR10_API_ORIGIN: 'https://localhost:4443', NODE_EXTRA_CA_CERTS: join(root, 'ca.pem'), TPR10_SCOPE_TEST_UI: spec === 'tests/e2e/scopes.spec.ts' ? 'true' : '' }
  });
  await waitFor(async () => (await fetch(`http://127.0.0.1:${port}`)).ok, 'Next');
  assert.match(tls('/', ['--fail']), /<!doctype html>/i);
  if (process.argv.includes('--e2e')) {
    // Policy applies only to the disposable Playwright Firefox profile, never macOS trust.
    const policies = join(root, 'firefox-policies.json');
    writeFileSync(policies, JSON.stringify({ policies: { Certificates: { Install: [join(root, 'ca.pem')] } } }), { mode: 0o600 });
    const browserTests = spawnSync(process.execPath, ['node_modules/@playwright/test/cli.js', 'test', spec,
      ...(process.env.TPR10_E2E_GREP ? ['--grep', process.env.TPR10_E2E_GREP] : [])], {
      stdio: 'inherit', timeout: 600000,
      env: { ...process.env, NODE_EXTRA_CA_CERTS: join(root, 'ca.pem'), PLAYWRIGHT_FIREFOX_POLICIES_JSON: policies, TPR10_E2E_DATABASE: database, TPR10_E2E_API: api }
    });
    if (browserTests.status !== 0) throw new Error('Browser E2E ไม่ผ่าน');
    console.log('Browser E2E ผ่าน HTTPS ที่เชื่อถือ CA เฉพาะ profile ทดสอบ');
    // The outage test restarts Docker, which returns before the API is ready.
    // Wait only on readiness; never retry an uncertain mutation.
    await waitFor(() => tls('/api/health/ready', ['--fail']).includes('ready'), 'API หลัง browser outage test');
  }
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
  assert.equal(tls('/api/v1/system/technical-probes', ['-b', join(root, 'cookies'), '--config', requestConfig, '-o', '/dev/null', '-w', '%{http_code}']), '401');
  // Test-only DB fixture: validates transport/authorization, not a real login/TOTP flow.
  // The real password + enrollment + TOTP flow is exercised by the API integration tests.
  const actorId = randomUUID(); const roleId = randomUUID(); const permissionId = randomUUID();
  const sessionRaw = randomBytes(32);
  const hash = createHash('sha256').update(sessionRaw).digest('hex');
  run('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1'], { input: `
    INSERT INTO users(id,username,normalized_username) VALUES ('${actorId}','tls-fixture','TLS-FIXTURE');
    INSERT INTO roles(id,name,role_class) VALUES ('${roleId}','tls-fixture','staff');
    INSERT INTO permissions(id,capability) VALUES ('${permissionId}','system:probe');
    INSERT INTO user_roles(user_id,role_id) VALUES ('${actorId}','${roleId}');
    INSERT INTO role_permissions(role_id,permission_id) VALUES ('${roleId}','${permissionId}');
    INSERT INTO mfa_factors(id,user_id,protected_secret,confirmed_at_utc) VALUES ('${randomUUID()}','${actorId}','transport-test-only',now());
    INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc)
      VALUES ('${randomUUID()}','${actorId}',decode('${hash}','hex'),'Active',now(),now(),now()+interval '8 hours',now());
  ` });
  writeFileSync(join(root, 'cookies'), `# Netscape HTTP Cookie File\nlocalhost\tFALSE\t/\tTRUE\t0\t__Host-tpr10_session\t${sessionRaw.toString('base64url')}\n`, { mode: 0o600 });
  const sessionCsrf = JSON.parse(tls('/api/v1/auth/csrf', ['--fail', '-b', join(root, 'cookies')]));
  writeFileSync(requestConfig, `header = "Origin: https://localhost:4443"\nheader = "X-CSRF-Token: ${sessionCsrf.token}"\nheader = "Content-Type: application/json"\ndata = "{\\"note\\":\\"tls-smoke\\"}"\n`, { mode: 0o600 });
  assert.equal(tls('/api/v1/system/technical-probes', ['-b', join(root, 'cookies'), '--config', requestConfig, '-o', '/dev/null', '-w', '%{http_code}']), '201');
  const forged = spawnSync('curl', ['--silent', '--max-time', '10', '--cacert', join(root, 'ca.pem'), '-H', 'Host: evil.example', 'https://localhost:4443/api/v1/auth/csrf']);
  assert.equal(forged.status, 52, 'Host ที่ไม่อนุญาตต้องถูกปิด connection');
  assert.equal(docker('exec', proxy, 'nginx', '-t').includes('failed'), false);
  assert.ok(api);
  console.log(`ผ่าน HTTPS acceptance: web${port}, trust CA, cookie flags, 403/401/201, hostile Host; API ไม่เปิดพอร์ตสาธารณะ`);
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

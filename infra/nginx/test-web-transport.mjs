// Opt-in integration: node --test infra/nginx/test-web-transport.mjs (Docker + OpenSSL).
// Real rendered nginx/TLS, synthetic upstream; no application credentials or trust-store edits.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { Agent, request } from 'node:https';
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { setTimeout as delay } from 'node:timers/promises';
import { createRequire } from 'node:module';
// Reuse the WebSocket implementation already shipped with the pinned Next runtime.
const { WebSocket, WebSocketServer } = createRequire(import.meta.url)('next/dist/compiled/ws');

test('HTTPS web proxy ใช้ upstream connection ซ้ำ และ upgrade เฉพาะ WebSocket', async () => {
  const root = mkdtempSync(join(tmpdir(), 'tpr10-web-transport-'));
  let container;
  const server = createServer((req, res) => {
    res.setHeader('Content-Type', 'application/json');
    res.end(JSON.stringify({ port: req.socket.remotePort, connection: req.headers.connection ?? '', upgrade: req.headers.upgrade ?? '' }));
  });
  const sockets = new WebSocketServer({ noServer: true });
  server.on('upgrade', (req, socket, head) => {
    if (req.headers.upgrade !== 'websocket' || req.headers.connection?.toLowerCase() !== 'upgrade') return socket.destroy();
    sockets.handleUpgrade(req, socket, head, client => client.on('message', data => client.send(data)));
  });
  const run = (command, args, options = {}) => {
    const result = spawnSync(command, args, { encoding: 'utf8', timeout: 30000, ...options });
    assert.equal(result.status, 0, result.error?.message ?? result.stderr);
    return result.stdout.trim();
  };
  let agent;
  try {
    await new Promise((resolve, reject) => server.once('error', reject).listen(4000, '127.0.0.1', resolve));
    run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1', '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost', '-keyout', join(root, 'key.pem'), '-out', join(root, 'cert.pem')]);
    run(process.execPath, ['infra/nginx/render-config.mjs', join(root, 'https.conf'), '--https'], {
      env: { ...process.env, TPR10_WEB_UPSTREAM: 'host.docker.internal:4000', TPR10_API_UPSTREAM: '127.0.0.1:5080', TPR10_TLS_CERT: '/fixture/cert.pem', TPR10_TLS_KEY: '/fixture/key.pem' },
    });
    // One worker makes upstream reuse observable independently of worker scheduling.
    writeFileSync(join(root, 'nginx.conf'), 'events {}\nhttp { include /fixture/https.conf; }\n');
    container = run('docker', ['run', '-d', '-p', '127.0.0.1::4443', '-v', `${root}:/fixture:ro`, 'nginx:1.28-alpine', 'nginx', '-c', '/fixture/nginx.conf', '-g', 'daemon off;']);
    const port = Number(run('docker', ['port', container, '4443/tcp']).split(':').at(-1));
    agent = new Agent({ keepAlive: true, maxSockets: 1, ca: readFileSync(join(root, 'cert.pem')) });
    const get = () => new Promise((resolve, reject) => {
      const req = request({ hostname: 'localhost', port, path: '/transport-fixture', agent,
        headers: { Host: 'localhost:4443' } }, res => {
        let body = '';
        res.on('data', chunk => { body += chunk; });
        res.on('end', () => { try { assert.equal(res.statusCode, 200); resolve(JSON.parse(body)); } catch (error) { reject(error); } });
      });
      req.once('error', reject);
      req.setTimeout(5000, () => req.destroy(new Error('transport timeout')));
      req.end();
    });
    let first;
    for (let attempt = 0; attempt < 30; attempt++) {
      try { first = await get(); break; } catch (error) {
        // Retry only connection refusal while nginx starts, never status/body/TLS assertions.
        if (error.code !== 'ECONNREFUSED' || attempt === 29) throw error;
        await delay(100);
      }
    }
    // Readiness retry above only; assertions and measured requests are never retried.
    assert.notEqual(first.connection.toLowerCase(), 'upgrade', 'HTTP ปกติต้องไม่ขอ protocol upgrade');
    assert.equal(first.upgrade, '');
    for (let index = 0; index < 5; index++) assert.equal((await get()).port, first.port, 'ต้องใช้ upstream socket เดิม');
    const echoed = await new Promise((resolve, reject) => {
      const client = new WebSocket(`wss://localhost:${port}/transport-fixture`, {
        ca: readFileSync(join(root, 'cert.pem')), headers: { Host: 'localhost:4443' }, handshakeTimeout: 5000,
      });
      const timer = setTimeout(() => { client.terminate(); reject(new Error('WebSocket echo timeout')); }, 5000);
      client.once('open', () => client.send('transport-echo'));
      client.once('message', data => { clearTimeout(timer); client.terminate(); resolve(data.toString()); });
      client.once('error', error => { clearTimeout(timer); client.terminate(); reject(error); });
    });
    assert.equal(echoed, 'transport-echo', 'WebSocket ต้องรับส่ง frame ผ่าน TLS/proxy ได้จริง');
  } finally {
    agent?.destroy();
    for (const client of sockets.clients) client.terminate();
    sockets.close();
    if (container) spawnSync('docker', ['rm', '-f', container], { stdio: 'ignore', timeout: 15000 });
    server.closeAllConnections();
    await new Promise(resolve => server.close(resolve));
    rmSync(root, { recursive: true, force: true });
  }
});

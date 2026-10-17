import { test, expect, type Page, type BrowserContext } from '@playwright/test';
import { randomBytes, randomUUID, createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';

// Disposable database/session and synthetic device inputs only; never use a real employee camera.
const site = { workspaceId: '11111111-1111-4111-8111-111111111111', projectId: '22222222-2222-4222-8222-222222222222', siteId: '33333333-3333-4333-8333-333333333333' };
const id = '55555555-5555-4555-8555-555555555555';
function sql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{12,64}$/.test(database)) throw new Error('isolated fixture required');
  return execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1'], { input, encoding: 'utf8' }).trim();
}
test.beforeAll(() => {
  sql("INSERT INTO role_permissions(role_id,permission_id) SELECT a.role_id,p.id FROM user_scope_assignments a JOIN users u ON u.id=a.user_id CROSS JOIN permissions p WHERE u.username='e2e-scope-staff' AND p.capability='attendance:record' ON CONFLICT DO NOTHING;");
});
async function session(context: BrowserContext) {
  const raw = randomBytes(32); const hash = createHash('sha256').update(raw).digest('hex');
  sql(`INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,security_version) SELECT '${randomUUID()}',id,decode('${hash}','hex'),'Active',now(),now(),now()+interval '8 hours',security_version FROM users WHERE username='e2e-scope-staff';`);
  await context.addCookies([{ name: '__Host-tpr10_session', value: raw.toString('base64url'), domain: 'localhost', path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
type DeviceState = { mode: string; samples: number; stopped: number; revoked: number; tracks: MediaStreamTrack[] };
async function devices(page: Page, mode = 'ok') {
  await page.addInitScript(mode => {
    const state: DeviceState = { mode, samples: 0, stopped: 0, revoked: 0, tracks: [] };
    Object.assign(window, { captureDevice: state });
    Object.defineProperty(navigator.mediaDevices, 'getUserMedia', { configurable: true, value: async () => {
      if (state.mode === 'camera-denied') throw new DOMException('denied', 'NotAllowedError');
      const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 480;
      const draw = () => { const ctx = canvas.getContext('2d')!; ctx.fillStyle = '#246'; ctx.fillRect(0, 0, 640, 480); };
      draw(); const timer = setInterval(draw, 50); const stream = canvas.captureStream(10);
      for (const track of stream.getTracks()) { state.tracks.push(track); const stop = track.stop.bind(track); track.stop = () => { state.stopped++; clearInterval(timer); stop(); }; }
      return stream;
    } });
    Object.defineProperty(navigator.geolocation, 'getCurrentPosition', { value: (ok: PositionCallback, fail: PositionErrorCallback) => {
      state.samples++;
      if (state.mode === 'gps-timeout') { fail({ code: 3, message: 'timeout', PERMISSION_DENIED: 1, POSITION_UNAVAILABLE: 2, TIMEOUT: 3 }); return; }
      ok({ coords: { latitude: 13, longitude: 100, accuracy: 150, altitude: null, altitudeAccuracy: null, heading: null, speed: null }, timestamp: Date.now() - (state.mode === 'gps-stale' ? 61000 : 0) } as GeolocationPosition);
    } });
    const revoke = URL.revokeObjectURL.bind(URL); URL.revokeObjectURL = url => { state.revoked++; revoke(url); };
  }, mode);
}
const now = () => new Date().toISOString();
function receipt() { return { operationId: id, dayId: id, eventId: id, evidenceId: id, dayVersion: 1, workDate: '2026-09-26', occurredAtUtc: now() }; }
async function setup(page: Page, context: BrowserContext, mode = 'ok', overnight = false) {
  await session(context); await devices(page, mode);
  const event = { id, occurredAtUtc: '2020-01-01T01:00:00Z', site: { ...site, isValid: true }, gps: { latitude: 13, longitude: 100, accuracyMeters: 150, sampledAtUtc: '2020-01-01T01:00:00Z' }, photo: { evidenceId: id } };
  await page.route('**/api/v1/attendance/current', route => route.fulfill({ json: { openDay: overnight ? { id, employeeId: id, workDate: '2020-01-01', version: 1, in: event, out: null, readBasis: 1, effectiveInUtc: event.occurredAtUtc, effectiveOutUtc: null } : null, todayCompleted: false, serverNowUtc: now() } }));
  await page.route('**/api/v1/attendance/capture-challenges', route => route.fulfill({ status: 201, json: { id, serverNowUtc: now(), expiresAtUtc: new Date(Date.now() + (mode === 'expired' ? -1000 : 120000)).toISOString() } }));
  await page.goto('/portal/attendance');
  await expect(page.getByLabel('สถานที่ลงเวลา')).toBeVisible();
  await page.getByLabel('สถานที่ลงเวลา').selectOption(site.siteId);
}
async function capture(page: Page) {
  await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
  await page.getByRole('button', { name: 'ถ่ายภาพ', exact: true }).click();
  await expect(page.getByAltText('ภาพรอยืนยัน ยังไม่ประทับเวลา')).toBeVisible();
}

for (const resultKind of ['upload', 'operation']) {
  test(`review: silent account switch discards delayed ${resultKind}`, async ({ page, context }) => {
    await setup(page, context); await capture(page);
    let release!: () => void; let waiting = false;
    const held = new Promise<void>(resolve => { release = resolve; });
    await page.route('**/api/v1/attendance/check-ins', async route => {
      if (resultKind === 'operation') return route.abort('failed');
      waiting = true; await held; await route.fulfill({ status: 201, json: receipt() });
    });
    await page.route('**/api/v1/attendance/operations/*?action=CheckIn', async route => {
      waiting = true; await held; await route.fulfill({ json: { state: 'Committed', receipt: receipt() } });
    });
    await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click();
    if (resultKind === 'operation') await page.getByRole('button', { name: 'ตรวจผลลงเวลา', exact: true }).click();
    await expect.poll(() => waiting).toBe(true);
    await page.route('**/api/v1/auth/session', route => route.fulfill({ json: { userId: 'different-account', stage: 'Active', permissions: [], mfaVerifiedAtUtc: null } }));
    release();
    await expect(page.locator('section').getByRole('alert')).toContainText('บัญชี');
    await expect(page.getByText(/ลงเวลาสำเร็จ:/)).toHaveCount(0);
    await expect(page.getByAltText('ภาพรอยืนยัน ยังไม่ประทับเวลา')).toHaveCount(0);
  });
}
test('review: silent account switch before challenge must not open camera', async ({ page, context }) => {
  await setup(page, context);
  await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeEnabled();
  await page.route('**/api/v1/auth/session', route => route.fulfill({ json: { userId: 'different-account', stage: 'Active', permissions: [], mfaVerifiedAtUtc: null } }));
  await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
  await expect(page.locator('section').getByRole('alert')).toContainText('บัญชี');
  expect(await page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks.length)).toBe(0);
});
test('review: interrupted camera cannot capture retained frame', async ({ page, context }) => {
  await setup(page, context);
  await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
  await expect(page.getByRole('button', { name: 'ถ่ายภาพ', exact: true })).toBeVisible();
  await page.evaluate(() => {
    const track = (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks[0];
    Object.defineProperty(track, 'muted', { get: () => true }); track.dispatchEvent(new Event('mute'));
  });
  await expect(page.locator('section').getByRole('alert')).toContainText('กล้อง');
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
});
test('review: live track without a fresh delivered frame cannot submit', async ({ page, context }) => {
  await setup(page, context);
  await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
  await expect(page.getByRole('button', { name: 'ถ่ายภาพ', exact: true })).toBeVisible();
  await page.getByLabel('ภาพจากกล้องสด').evaluate(video => {
    Object.defineProperty(video, 'requestVideoFrameCallback', { value: () => 0 });
  });
  await page.getByRole('button', { name: 'ถ่ายภาพ', exact: true }).click();
  await expect(page.locator('section').getByRole('alert')).toContainText('กล้องไม่ส่งภาพใหม่');
  await expect(page.getByAltText('ภาพรอยืนยัน ยังไม่ประทับเวลา')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
});
test('review: midnight after challenge requires continuous confirmation', async ({ page, context }) => {
  await setup(page, context);
  await page.unroute('**/api/v1/attendance/current');
  const event = { id, occurredAtUtc: '2026-09-26T08:00:00Z', site: { ...site, isValid: true }, gps: { latitude: 13, longitude: 100, accuracyMeters: 10, sampledAtUtc: '2026-09-26T08:00:00Z' }, photo: { evidenceId: id } };
  await page.route('**/api/v1/attendance/current', route => route.fulfill({ json: { openDay: { id, employeeId: id, workDate: '2026-09-26', version: 1, in: event, out: null, readBasis: 1, effectiveInUtc: event.occurredAtUtc, effectiveOutUtc: null }, todayCompleted: false, serverNowUtc: '2026-09-26T16:59:59Z' } }));
  await page.route('**/api/v1/attendance/capture-challenges', route => route.fulfill({ status: 201, json: { id, serverNowUtc: '2026-09-26T16:59:59Z', expiresAtUtc: '2026-09-26T17:01:59Z' } }));
  await page.reload(); await page.getByLabel('สถานที่ลงเวลา').selectOption(site.siteId);
  await capture(page);
  await expect(page.getByLabel('ยืนยันว่าทำงานต่อเนื่องจากรอบเดิม')).toBeVisible();
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
});
for (const code of ['invalid-capture-time', 'invalid-gps']) {
  test(`review: actionable device clock error ${code}`, async ({ page, context }) => {
    await setup(page, context); await capture(page);
    await page.route('**/api/v1/attendance/check-ins', route => route.fulfill({ status: 400, json: { type: `urn:tpr10:attendance:${code}` } }));
    await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click();
    await expect(page.locator('section').getByRole('alert')).toContainText('เวลาอัตโนมัติ');
  });
}

for (const [mode, message] of [['camera-denied', 'กล้อง'], ['gps-timeout', 'GPS'], ['gps-stale', 'GPS'], ['expired', 'หมดอายุ']]) {
  test(`ป้องกันส่งเมื่อ ${mode}`, async ({ page, context }) => {
    await setup(page, context, mode);
    let uploads = 0; page.on('request', r => { if (/\/check-(ins|outs)$/.test(r.url())) uploads++; });
    await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
    await expect(page.locator('section').getByRole('alert')).toContainText(message);
    await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
    expect(uploads).toBe(0);
  });
}
test('ภาพสดเท่านั้น GPS ใหม่ ความแม่นยำต่ำไม่บล็อก และส่งครั้งเดียว', async ({ page, context }) => {
  await setup(page, context);
  await expect(page.locator('input[type=file]')).toHaveCount(0);
  await page.getByLabel('ภาพจากกล้องสด').evaluate(el => el.parentElement!.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: new DataTransfer() })));
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
  await capture(page);
  await expect(page.getByRole('status')).toContainText('100 เมตร');
  let uploads = 0; let payload = ''; let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/attendance/check-ins', async route => { uploads++; payload = route.request().postDataBuffer()!.toString(); await held; await route.fulfill({ status: 201, json: receipt() }); });
  await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click();
  await expect.poll(() => uploads).toBe(1);
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
  release();
  await expect(page.getByRole('status').filter({ hasText: 'ลงเวลาสำเร็จ' })).toBeVisible();
  expect(payload).toContain('name="photo"'); expect(payload).toContain('"accuracyMeters":150');
  expect(await page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.samples)).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => [localStorage.length, sessionStorage.length])).toEqual([0, 0]);
});
test('เน็ตหลุดหลังส่งต้องตรวจ operation ก่อนเริ่มใหม่ ไม่ส่งซ้ำเอง', async ({ page, context }) => {
  await setup(page, context); await capture(page);
  let uploads = 0; let key = '';
  await page.route('**/api/v1/attendance/check-ins', route => { uploads++; key = /"idempotencyKey":"([^"]+)"/.exec(route.request().postDataBuffer()!.toString())![1]; return route.abort('failed'); });
  await page.route('**/api/v1/attendance/operations/*?action=CheckIn', route => {
    expect(route.request().url()).toContain(key); return route.fulfill({ json: { state: 'Committed', receipt: receipt() } });
  });
  await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click();
  await expect(page.getByRole('button', { name: 'ตรวจผลลงเวลา', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'ตรวจผลลงเวลา', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('ลงเวลาสำเร็จ'); expect(uploads).toBe(1);
});
test('offline ไม่เก็บคิว และออกข้ามวันต้องยืนยันต่อเนื่อง', async ({ page, context }) => {
  await setup(page, context, 'ok', true);
  await expect(page.getByText(/Module 6D/)).toBeVisible(); await capture(page);
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
  await page.getByLabel('ยืนยันว่าทำงานต่อเนื่องจากรอบเดิม').check();
  await page.evaluate(() => { Object.defineProperty(navigator, 'onLine', { configurable: true, get: () => false }); window.dispatchEvent(new Event('offline')); });
  await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click();
  await expect(page.locator('section').getByRole('alert')).toContainText('อินเทอร์เน็ต');
});
test('ซ่อนแท็บหยุดกล้อง ล้างภาพ และโหลด current ใหม่เมื่อกลับมา', async ({ page, context }) => {
  await setup(page, context); await capture(page);
  let reads = 0; page.on('request', r => { if (r.url().endsWith('/attendance/current')) reads++; });
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(page.getByAltText('ภาพรอยืนยัน ยังไม่ประทับเวลา')).toHaveCount(0);
  expect(await page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks.every(t => t.readyState === 'ended'))).toBe(true);
  expect(await page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.revoked)).toBeGreaterThan(0);
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect.poll(() => reads).toBeGreaterThan(0);
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
});
test('มือถือแนวตั้งแนวนอน label keyboard และเมนูตาม exact assignment', async ({ page, context }) => {
  await setup(page, context);
  for (const viewport of [{ width: 390, height: 844 }, { width: 844, height: 390 }]) {
    await page.setViewportSize(viewport);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.getByLabel('สถานที่ลงเวลา').focus(); await page.keyboard.press('Tab');
    await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeFocused();
  }
  await page.goto('/portal'); await expect(page.getByRole('link', { name: 'ลงเวลาเข้า–ออก', exact: true })).toBeVisible();
});
test('GPS ที่เก่าเฉพาะตอนยืนยันต้องไม่ส่งภาพ', async ({ page, context }) => {
  await setup(page, context); await capture(page);
  let uploads = 0; page.on('request', r => { if (/\/check-(ins|outs)$/.test(r.url())) uploads++; });
  await page.evaluate(() => { (window as unknown as { captureDevice: DeviceState }).captureDevice.mode = 'gps-stale'; });
  await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click();
  await expect(page.locator('section').getByRole('alert')).toContainText('GPS'); expect(uploads).toBe(0);
});
test('ภาพหมดอายุระหว่างรอยืนยันต้องถ่ายใหม่', async ({ page, context }) => {
  await setup(page, context);
  await page.route('**/api/v1/attendance/capture-challenges', route => route.fulfill({ status: 201, json: { id, serverNowUtc: now(), expiresAtUtc: new Date(Date.now() + 3500).toISOString() } }));
  await capture(page);
  await expect(page.locator('section').getByRole('alert')).toContainText('หมดอายุ', { timeout: 7000 });
  await expect(page.getByAltText('ภาพรอยืนยัน ยังไม่ประทับเวลา')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true })).toBeDisabled();
});
test('ผล upload ที่ตอบหลังซ่อนแท็บไม่แสดงสำเร็จจนอ่าน operation', async ({ page, context }) => {
  await setup(page, context); await capture(page);
  let entered!: () => void; const reached = new Promise<void>(resolve => { entered = resolve; });
  let release!: () => void; const held = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/attendance/check-ins', async route => { entered(); await held; await route.fulfill({ status: 201, json: receipt() }).catch(() => {}); });
  await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click(); await reached;
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); });
  release();
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => false }); document.dispatchEvent(new Event('visibilitychange')); });
  await expect(page.getByRole('button', { name: 'ตรวจผลลงเวลา', exact: true })).toBeEnabled();
  await expect(page.getByText(/ลงเวลาสำเร็จ:/)).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeDisabled();
});
test('auth change ล้างภาพและไม่แสดงผลจากบัญชีเดิม', async ({ page, context }) => {
  await setup(page, context); await capture(page);
  await page.route('**/api/v1/auth/session', route => route.fulfill({ status: 401, json: {} }));
  await page.evaluate(() => { const channel = new BroadcastChannel('tpr10-auth-change'); channel.postMessage('changed'); channel.close(); });
  await expect(page.getByAltText('ภาพรอยืนยัน ยังไม่ประทับเวลา')).toHaveCount(0);
  await expect(page.getByText(/ลงเวลาสำเร็จ:/)).toHaveCount(0);
});
test('503 จาก current ไม่ logout หรือเปิดกล้องด้วยข้อมูลค้าง', async ({ page, context }) => {
  await setup(page, context);
  await page.route('**/api/v1/attendance/current', route => route.fulfill({ status: 503, json: {} }));
  await page.reload();
  await expect(page.locator('section').getByRole('alert')).toContainText('ไม่พร้อมใช้งาน');
  await expect(page).toHaveURL(/\/portal\/attendance$/);
  await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeDisabled();
});
test('คำขอไม่เคยถึง server ต้องรอ challenge หมดอายุและตรวจ 404 ก่อนเริ่มใหม่', async ({ page, context }) => {
  await setup(page, context);
  await page.route('**/api/v1/attendance/capture-challenges', route => route.fulfill({ status: 201, json: { id, serverNowUtc: now(), expiresAtUtc: new Date(Date.now() + 4000).toISOString() } }));
  await page.route('**/api/v1/attendance/check-ins', route => route.abort('failed'));
  await page.route('**/api/v1/attendance/operations/*?action=CheckIn', route => route.fulfill({ status: 404, json: {} }));
  await capture(page); await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click();
  await page.getByRole('button', { name: 'ตรวจผลลงเวลา', exact: true }).click();
  await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeDisabled();
  await expect(async () => {
    await page.getByRole('button', { name: 'ตรวจผลลงเวลา', exact: true }).click();
    await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeEnabled();
  }).toPass({ timeout: 12000, intervals: [4500] });
});
test('กล้องที่ยัง live ต้องหยุดทุก track เมื่อซ่อนแท็บ', async ({ page, context }) => {
  await setup(page, context); await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
  await expect(page.getByRole('button', { name: 'ถ่ายภาพ', exact: true })).toBeVisible();
  expect(await page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks.some(t => t.readyState === 'live'))).toBe(true);
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange')); });
  expect(await page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks.every(t => t.readyState === 'ended'))).toBe(true);
});
test('อนุญาตกล้องตอบช้าหลังออกจากแท็บต้องไม่เปิด track ค้าง', async ({ page, context }) => {
  await setup(page, context);
  await page.evaluate(() => {
    const original = navigator.mediaDevices.getUserMedia;
    Object.defineProperty(navigator.mediaDevices, 'getUserMedia', { value: async () => {
      const stream = await original.call(navigator.mediaDevices, { video: true });
      await new Promise<void>(resolve => Object.assign(window, { releaseCamera: resolve })); return stream;
    } });
  });
  await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
  await expect.poll(() => page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks.length)).toBe(1);
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true }); document.dispatchEvent(new Event('visibilitychange'));
    (window as unknown as { releaseCamera: () => void }).releaseCamera();
  });
  await expect.poll(() => page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks.every(t => t.readyState === 'ended'))).toBe(true);
});
test('เปลี่ยนบัญชีระหว่าง upload แล้วผลเก่าต้องไม่ปรากฏ', async ({ page, context }) => {
  await setup(page, context); await capture(page);
  let entered!: () => void; const reached = new Promise<void>(resolve => { entered = resolve; });
  let release!: () => void; const held = new Promise<void>(resolve => { release = resolve; });
  await page.route('**/api/v1/attendance/check-ins', async route => { entered(); await held; await route.fulfill({ status: 201, json: receipt() }).catch(() => {}); });
  await page.getByRole('button', { name: 'ยืนยันลงเวลา', exact: true }).click(); await reached;
  await context.clearCookies();
  const navigated = page.waitForURL(/\/login/);
  await page.evaluate(() => { const channel = new BroadcastChannel('tpr10-auth-change'); channel.postMessage('changed'); channel.close(); });
  release(); await navigated;
  await expect(page.getByText(/ลงเวลาสำเร็จ:/)).toHaveCount(0);
  await expect(page.getByAltText('ภาพรอยืนยัน ยังไม่ประทับเวลา')).toHaveCount(0);
});
test('กล้องได้ stream แต่ไม่ส่งเฟรมต้องหมดเวลารอและหยุด track', async ({ page, context }) => {
  await setup(page, context);
  await page.evaluate(() => { HTMLMediaElement.prototype.play = () => new Promise<void>(() => {}); });
  await page.getByRole('button', { name: 'เปิดกล้อง', exact: true }).click();
  await expect(page.locator('section').getByRole('alert')).toContainText('กล้อง', { timeout: 13000 });
  expect(await page.evaluate(() => (window as unknown as { captureDevice: DeviceState }).captureDevice.tracks.every(t => t.readyState === 'ended'))).toBe(true);
  await expect(page.getByRole('button', { name: 'เปิดกล้อง', exact: true })).toBeEnabled();
});

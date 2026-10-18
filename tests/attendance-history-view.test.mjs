import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';

const load = () => loadTs('lib/attendance/history-view.ts');
const id = '11111111-1111-4111-8111-111111111111';
const event = { id, occurredAtUtc: '2026-09-25T16:55:00Z', site: { workspaceId: id, projectId: id, siteId: id }, gps: { latitude: 13, longitude: 100, accuracyMeters: 10, sampledAtUtc: '2026-09-25T16:55:00Z' } };
const day = { id, employeeId: id, workDate: '2026-09-25', version: 1, in: { ...event, photo: { evidenceId: id } }, out: { ...event, id: '22222222-2222-4222-8222-222222222222', occurredAtUtc: '2026-09-26T01:05:00Z' }, readBasis: 2, effectiveInUtc: event.occurredAtUtc, effectiveOutUtc: '2026-09-26T01:05:00Z' };

test('ประวัติรับรองข้อมูลข้ามวันและให้รูปเฉพาะ Mine/HR ไม่ใช่หัวหน้า', () => {
  const { parseAttendancePage } = load();
  const page = { items: [day], nextCursor: null };
  assert.equal(parseAttendancePage(page, 'Mine').items[0].in.photo.evidenceId, id);
  assert.equal(parseAttendancePage(page, 'Hr').items[0].in.photo.evidenceId, id);
  assert.equal('photo' in parseAttendancePage(page, 'Team').items[0].in, false);
  assert.equal(parseAttendancePage(page, 'Mine').items[0].out.occurredAtUtc, day.out.occurredAtUtc);
});

test('ประวัติปฏิเสธวันเวลา, GPS, Site, basis, pagination และ photo ที่ผิดรูปแบบ', () => {
  const { parseAttendancePage } = load();
  const invalidRows = [
    { ...day, workDate: '2026-02-30' },
    { ...day, readBasis: 4 },
    { ...day, in: { ...event, gps: { ...event.gps, latitude: 91 } } },
    { ...day, in: { ...event, site: { workspaceId: id, projectId: null, siteId: id } } },
    { ...day, in: { ...event, photo: { evidenceId: 'nope' } } },
    { ...day, out: { ...event, occurredAtUtc: 'invalid' } },
  ];
  for (const row of invalidRows) assert.throws(() => parseAttendancePage({ items: [row], nextCursor: null }, 'Mine'));
  for (const nextCursor of ['', 1, 'x'.repeat(4097)]) assert.throws(() => parseAttendancePage({ items: [], nextCursor }, 'Mine'));
});

test('Thai date labels use work date while retaining separate entry/exit sites', () => {
  const { attendanceDayLabel } = load();
  assert.match(attendanceDayLabel(day.workDate), /25/);
  assert.match(attendanceDayLabel(day.workDate), /กันยายน/);
  assert.throws(() => attendanceDayLabel('not-a-date'));
});

test('ประวัติจากคำร้องไม่มีหลักฐานปลอมและคงเวลาเดิมแยกจากเวลาที่แก้', () => {
  const { parseAttendancePage } = load();
  const adjustment = { requestId: id, revision: 1, appliedAtUtc: '2026-09-26T02:00:00Z', beforeInUtc: null, beforeOutUtc: null, afterInUtc: day.effectiveInUtc, afterOutUtc: day.effectiveOutUtc };
  const corrected = { ...day, in: null, out: null, effectiveInSite: event.site, effectiveOutSite: event.site, adjustments: [adjustment] };
  const row = parseAttendancePage({ items: [corrected], nextCursor: null }, 'Mine').items[0];
  assert.equal(row.in, null); assert.equal(row.out, null); assert.equal(row.adjustments[0].requestId, id);
  assert.equal(JSON.stringify(row).includes('latitude'), false);
  const live = parseAttendancePage({ items: [{ ...corrected, in: day.in }], nextCursor: null }, 'Mine').items[0];
  assert.equal(live.in.occurredAtUtc, event.occurredAtUtc);
  for (const invalid of [{ ...corrected, adjustments: [] }, { ...corrected, effectiveInSite: null }, { ...corrected, effectiveOutUtc: '2026-09-24T01:00:00Z' }, { ...corrected, adjustments: [{ ...adjustment, requestId: 'invalid' }] }])
    assert.throws(() => parseAttendancePage({ items: [invalid], nextCursor: null }, 'Mine'));
});

test('accepts backend DateTimeOffset serialization and all nonempty Guid forms', () => {
  const { parseAttendancePage } = load();
  const backendId = '00000000-0000-0000-0000-000000000031';
  const backendEvent = { ...event, id: backendId, occurredAtUtc: '2026-09-25T16:55:00+00:00', gps: { ...event.gps, sampledAtUtc: '2026-09-25T16:55:00+00:00' } };
  const result = parseAttendancePage({ items: [{ ...day, employeeId: backendId, in: backendEvent, out: null, effectiveOutUtc: null }], nextCursor: null }, 'Mine');
  assert.equal(result.items[0].employeeId, backendId);
});

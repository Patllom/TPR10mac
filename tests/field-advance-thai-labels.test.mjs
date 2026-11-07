import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';
import { readFileSync } from 'node:fs';

const labels = loadTs('lib/field-advances/ui-labels.ts');

test('payment holds and recovery outcomes have Thai display labels', () => {
  assert.equal(labels.holdLabel('PaymentMismatch'), 'พบการจ่ายไม่ตรง');
  assert.equal(labels.holdLabel('CeilingAmendment'), 'กำลังแก้ไขวงเงิน');
  assert.equal(labels.holdLabel('PaymentTerminated'), 'ยุติการจ่าย');
  assert.equal(labels.paymentOutcomeLabel('Unpaid'), 'ไม่ได้จ่ายเงินจริง');
  assert.equal(labels.paymentOutcomeLabel('Matched'), 'จ่ายสำเร็จตรงงวด');
});

test('review status and source codes have Thai display labels', () => {
  assert.equal(labels.reviewStateLabel('Pending'), 'รอตรวจ');
  assert.equal(labels.reviewStateLabel('Accepted'), 'รับรองแล้ว');
  assert.equal(labels.reviewStateLabel('Returned'), 'ส่งกลับ');
  assert.equal(labels.recoverySourceLabel('WrongRecipient'), 'ผู้รับเงินผิดคน');
  assert.equal(labels.recoverySourceLabel('Owner'), 'ผู้รับผิดชอบเงิน');
});

test('unrecognized and inherited object keys always produce Thai fallback strings', () => {
  for (const code of ['toString', '__proto__', 'not-a-known-code']) {
    assert.equal(labels.holdLabel(code), 'เหตุระงับที่ต้องตรวจสอบ');
    assert.equal(labels.paymentOutcomeLabel(code), 'ผลที่ต้องตรวจสอบ');
    assert.equal(labels.reviewStateLabel(code), 'สถานะที่ต้องตรวจสอบ');
    assert.equal(labels.recoverySourceLabel(code), 'แหล่งเงินที่ต้องตรวจสอบ');
  }
});

test('pending supplement and amendment status are translated at the UI boundary', () => {
  const ui = readFileSync('app/portal/field-advances/CeilingAmendmentPanel.tsx', 'utf8');
  assert.match(ui, /reviewStateLabel\(workflow\.pendingSupplementState\)/);
  assert.match(ui, /reviewStateLabel\(workflow\.amendmentState\)/);
});

test('reviewer recovery detail translates source while the command keeps the source code', () => {
  const ui = readFileSync('app/portal/field-advances/SettlementAdjustmentPanel.tsx', 'utf8');
  assert.match(ui, /recoverySourceLabel\(detail\.recovery\.returnKind\)/);
  const reviewer = readFileSync('app/portal/field-advances/SettlementReviewDetails.tsx', 'utf8');
  assert.match(reviewer, /\{ entityId: item\.entityId, amount, source, evidenceIds \}/);
});

test('every accepted payment proposal kind has a Thai label and unknown kinds have a safe fallback', () => {
  const expected = {
    PaymentCancellation: 'ยกเลิกงวด', PaymentMetadata: 'แก้วันจ่ายและหลักฐาน',
    PaymentOutcome: 'สะสางผล', PaymentTakeover: 'รับช่วง',
    InstallmentReview: 'ตรวจงวดจ่าย', PaymentMismatch: 'รายงานจ่ายไม่ตรง',
    PaymentDisposition: 'กำหนดแนวทางจ่ายคงเหลือ'
  };
  for (const [kind, label] of Object.entries(expected)) {
    assert.equal(labels.paymentProposalKindLabel(kind), label);
  }
  for (const kind of ['toString', '__proto__', 'UnexpectedProposal']) {
    assert.equal(labels.paymentProposalKindLabel(kind), 'ประเภทข้อเสนอที่ต้องตรวจสอบ');
  }
});

test('recovery panel displays the proposal kind label without changing proposal identity', () => {
  const ui = readFileSync('app/portal/field-advances/PaymentRecoveryPanel.tsx', 'utf8');
  assert.match(ui, /paymentProposalKindLabel\(p\.kind\)/);
  assert.doesNotMatch(ui, /\?\? p\.kind/);
  assert.match(ui, /send\('confirm', null, reason, p\)/);
});

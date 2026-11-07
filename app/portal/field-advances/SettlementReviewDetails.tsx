'use client';
import { reviewStateLabel } from '@/lib/field-advances/ui-labels';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { currentAccount, PolicyError } from '@/lib/field-advances/policy-client';
import { createSettlementClient, SettlementError } from '@/lib/field-advances/settlement-client';
import { pendingSettlements, type PendingSettlement } from '@/lib/field-advances/settlement-view';
import type { SettlementCommand, SettlementRound } from '@/lib/field-advances/settlement-types';
import { createSettlementWorkClient, type SettlementDuplicateComparison, type SettlementWorkItem }
  from '@/lib/field-advances/settlement-work-client';
import SettlementAdjustmentPanel from './SettlementAdjustmentPanel';
import IncidentSettlementPanel from './IncidentSettlementPanel';

type Props = {
  userId: string;
  item: SettlementWorkItem;
  onBack: () => void;
  onDone: () => void;
  onAccessDenied: () => void;
};

const operations: Record<string, string> = {
  'round-approve': 'rounds/review',
  'round-check': 'rounds/review',
  'refund-review': 'refunds/review',
  'incident-review': 'incidents/confirm',
  'recovery-propose': 'incidents/recover',
  'incident-release': 'incidents/release',
  'shortfall-prepare': 'incidents/shortfall',
  'recovery-review': 'recoveries/confirm',
  'correction-review': 'corrections/confirm',
  'case-payment-review': 'case-payments/confirm',
  'excess-payment-review': 'excess-payments/confirm',
  'completion-review': 'completion/correction-confirm',
  'duplicate-review': 'duplicates/review',
  'excess-disposition': 'excess/disposition'
};

function evidenceLane(item: SettlementWorkItem): string | null {
  const actions = item.allowedActions;
  if (actions.includes('refund-review')) return 'refunds';
  if (actions.some(action => ['incident-review', 'recovery-propose',
    'incident-release', 'shortfall-prepare'].includes(action))) return 'incidents';
  if (actions.includes('recovery-review')) return 'recoveries';
  if (actions.includes('correction-review')) return 'corrections';
  if (actions.includes('case-payment-review')) return 'case-payments';
  if (actions.includes('excess-payment-review')) return 'excess-payments';
  return null;
}

export default function SettlementReviewDetails({ userId, item, onBack, onDone, onAccessDenied }: Props) {
  const [round, setRound] = useState<SettlementRound | null>(null);
  const [comparison, setComparison] = useState<SettlementDuplicateComparison | null>(null);
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);
  const [pending, setPending] = useState<PendingSettlement | null>(null);
  const [excessEvidenceIds, setExcessEvidenceIds] = useState<string[]>([]);
  const [excessDisposition, setExcessDisposition] = useState<
    'AwaitingExternalReview' | 'NoAdditionalPaymentRequired' | 'FinancialSettlementRecorded'
  >('AwaitingExternalReview');
  const lock = useRef(false);
  const client = useMemo(() => createSettlementClient(userId), [userId]);

  const handleError = useCallback((cause: unknown) => {
    const error = cause instanceof PolicyError ? new SettlementError(cause.status) : cause;
    if (error instanceof SettlementError && error.clearProtectedData) {
      setRound(null);
      setComparison(null);
      pendingSettlements.clear();
      setPending(null);
      onAccessDenied();
    }
    setError(error instanceof Error ? error.message : 'ยังยืนยันผลไม่ได้');
  }, [onAccessDenied]);

  useEffect(() => {
    let active = true;
    setPending(pendingSettlements.current(userId, item.requestId));
    if (item.roundId && item.revisionId) {
      void client.loadSettlementRound(item.requestId, item.roundId, item.revisionId)
        .then(value => {
          if (!active) return;
          if (value.currentStepId !== item.entityId || value.currentStepVersion !== item.entityVersion
            || value.requestVersion !== item.requestVersion || value.familyVersion !== item.familyVersion) {
            throw new SettlementError(409);
          }
          setRound(value);
        })
        .catch(cause => {
          if (active) handleError(cause);
        });
    }
    if (item.allowedActions.includes('duplicate-review')) {
      void createSettlementWorkClient(userId).duplicateComparison(item.requestId, item.entityId)
        .then(value => {
          if (!active) return;
          if (value.caseVersion !== item.entityVersion) throw new SettlementError(409);
          setComparison(value);
        })
        .catch(cause => {
          if (active) handleError(cause);
        });
    }
    return () => { active = false; };
  }, [client, item, userId, handleError]);

  async function dispatch(command: PendingSettlement, retry = false) {
    if (lock.current || !('requestVersion' in command.command)) return;
    lock.current = true;
    setBusy(true);
    setError('');
    try {
      if (await currentAccount() !== command.account) throw new SettlementError(403);
      await client.sendSettlementCommand(command.requestId, command.action, command.body, command.command);
      pendingSettlements.resolve(userId, item.requestId, command.command.commandId);
      setPending(null);
      setNotice('เซิร์ฟเวอร์ยืนยันผลคำสั่งแล้ว');
      onDone();
    } catch (cause) {
      if (!retry && cause instanceof SettlementError && !cause.unknownResult) {
        pendingSettlements.resolve(userId, item.requestId, command.command.commandId);
        setPending(null);
      } else {
        setPending(command);
      }
      handleError(cause);
    } finally {
      lock.current = false;
      setBusy(false);
    }
  }

  async function checkReceipt(command: PendingSettlement) {
    if (lock.current) return;
    lock.current = true;
    setBusy(true);
    setError('');
    try {
      if (await currentAccount() !== command.account) throw new SettlementError(403);
      await client.checkSettlementReceipt(command.requestId, command.action, command.entityId,
        command.command.commandId);
      pendingSettlements.resolve(userId, item.requestId, command.command.commandId);
      setPending(null);
      setNotice('เซิร์ฟเวอร์ยืนยันผลคำสั่งแล้ว');
      onDone();
    } catch (cause) {
      handleError(cause);
    } finally {
      lock.current = false;
      setBusy(false);
    }
  }

  async function send(action: string, body: Record<string, unknown>, why = reason) {
    if (lock.current || pendingSettlements.current(userId, item.requestId)) return;
    const reviewVariant = /^(corrections|case-payments|excess-payments)\/(return|reject)$/.test(action)
      && item.allowedActions.some(value => operations[value] === action.replace(/\/(return|reject)$/, '/confirm'));
    if ((!reviewVariant && !item.allowedActions.some(value => operations[value] === action)) || !why.trim()) {
      setError('กรุณาระบุเหตุผลและโหลดสิทธิ์ล่าสุด');
      return;
    }
    const metadata: SettlementCommand = {
      commandId: crypto.randomUUID(),
      requestVersion: item.requestVersion,
      familyVersion: item.familyVersion,
      entityVersion: action === 'incidents/recover' || action === 'incidents/shortfall'
        ? null : item.entityVersion,
      reason: why.trim()
    };
    try {
      if (await currentAccount() !== userId) throw new SettlementError(403);
      const frozen = pendingSettlements.begin(userId, item.requestId, action, body, metadata);
      setPending(frozen);
      await dispatch(frozen);
    } catch (cause) {
      handleError(cause);
    }
  }

  async function downloadRoundEvidence(evidenceId: string) {
    if (!item.roundId || !item.revisionId || busy) return;
    setBusy(true);
    setError('');
    try {
      if (await currentAccount() !== userId) throw new SettlementError(403);
      const blob = await client.downloadRoundEvidence(item.requestId, item.roundId, item.revisionId, evidenceId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'หลักฐานรอบค่าใช้จ่าย';
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (cause) {
      handleError(cause);
    } finally {
      setBusy(false);
    }
  }

  async function downloadWorkEvidence(evidenceId: string) {
    if (busy) return;
    const lane = evidenceLane(item);
    if (!lane) return;
    setBusy(true);
    setError('');
    try {
      const blob = await createSettlementWorkClient(userId).downloadEvidence(lane,
        item.requestId, item.entityId, evidenceId);
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'หลักฐานรายการตรวจ';
      link.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (cause) {
      handleError(cause);
    } finally {
      setBusy(false);
    }
  }

  async function uploadIncidentEvidence(file: File): Promise<string> {
    if (!item.allowedActions.includes('recovery-propose') || lock.current || pending) {
      throw new SettlementError(403);
    }
    lock.current = true;
    setBusy(true);
    setError('');
    try {
      if (await currentAccount() !== userId) throw new SettlementError(403);
      const result = await client.uploadAdjustmentEvidence(item.requestId, 'incidents', item.entityId, file);
      if (!result || typeof result !== 'object' || !('id' in result) || typeof result.id !== 'string') {
        throw new SettlementError(503);
      }
      return result.id;
    } catch (cause) {
      handleError(cause);
      throw cause;
    } finally {
      lock.current = false;
      setBusy(false);
    }
  }

  async function uploadExcessEvidence(file: File) {
    if (!item.allowedActions.includes('excess-disposition') || lock.current || pending) return;
    lock.current = true;
    setBusy(true);
    setError('');
    try {
      if (await currentAccount() !== userId) throw new SettlementError(403);
      const result = await client.uploadAdjustmentEvidence(item.requestId, 'excess', item.entityId, file);
      if (!result || typeof result !== 'object' || !('id' in result) || typeof result.id !== 'string') {
        throw new SettlementError(503);
      }
      setExcessEvidenceIds(previous => [...previous, result.id as string]);
    } catch (cause) {
      handleError(cause);
    } finally {
      lock.current = false;
      setBusy(false);
    }
  }

  const action = item.allowedActions[0];
  const disabled = busy || !!pending || !!error || !reason.trim();
  return <article className="space-y-4 rounded border p-4">
    <button className="underline" onClick={onBack}>กลับคิวงาน</button>
    <h2 className="text-xl font-semibold">รายละเอียดงานตรวจ</h2>
    <p className="break-all">คำขอ {item.requestId}</p>
    <p>สถานะ {reviewStateLabel(item.state)} · เวอร์ชัน {item.entityVersion}</p>
    {error && <p role="alert" className="rounded border border-red-500 p-3">{error}</p>}
    {notice && <p role="status">{notice}</p>}
    {pending && <section className="space-y-2 rounded border border-amber-600 p-3">
      <h3 className="font-semibold">รอตรวจสอบผลรายการ</h3>
      <p>ยังไม่ทราบผล ห้ามสร้างคำสั่งใหม่หรือส่งซ้ำอัตโนมัติ</p>
      <p className="break-all">รหัสคำสั่ง {pending.command.commandId}</p>
      <button className="underline" disabled={busy} onClick={() => void checkReceipt(pending)}>
        ตรวจผลจากรหัสคำสั่ง
      </button>
      {'requestVersion' in pending.command && <button className="ml-3 underline" disabled={busy}
        onClick={() => void dispatch(pending, true)}>ส่งคำสั่งเดิมอีกครั้ง</button>}
    </section>}
    {action === 'round-approve' || action === 'round-check' ? <section className="space-y-3">
      {!round && <p>กำลังโหลดฉบับและเอกสารที่ต้องตรวจ</p>}
      {round && <>
        <p>ยอดฉบับ {round.total} บาท · เอกสาร {round.documents.length} ใบ</p>
        <ul className="list-disc pl-6">{round.lines.map(line => <li key={line.id}>
          {line.description} · {line.amount} บาท
        </li>)}</ul>
        {round.documents.map(document => <div key={document.id} className="border-t pt-2">
          <p>{document.issuer} · {document.amount} บาท · {document.issuedOn}</p>
          {document.evidenceIds.map((id, index) => <button key={id} className="mr-3 underline"
            disabled={busy} onClick={() => void downloadRoundEvidence(id)}>
            ดาวน์โหลดหลักฐาน {index + 1}
          </button>)}
        </div>)}
      </>}
    </section> : <SettlementAdjustmentPanel item={item} disabled={busy}
      onDownload={evidenceId => void downloadWorkEvidence(evidenceId)} />}
    {item.detail?.incident && <IncidentSettlementPanel item={item} disabled={busy || !!pending || !!error}
      reasonReady={!!reason.trim()}
      onUpload={uploadIncidentEvidence}
      onRecover={(amount, source, evidenceIds) => void send('incidents/recover',
        { entityId: item.entityId, amount, source, evidenceIds })}
      onRelease={() => void send('incidents/release', { entityId: item.entityId })}
      onShortfall={amount => void send('incidents/shortfall', { entityId: item.entityId, amount })} />}
    {comparison && <section className="space-y-2 rounded border p-3">
      <h3 className="font-semibold">เทียบเอกสารสองคำขอที่ได้รับสิทธิ์ตรวจ</h3>
      {[comparison.current, comparison.other].map((document, index) => <div key={document.documentId}
        className="border-t pt-2">
        <p>{index === 0 ? 'คำขอนี้' : 'อีกคำขอ'} · {document.requestId}</p>
        <p>ผู้ออก {document.issuer} · เลข {document.hasNoNumber ? 'ไม่มีเลขจริง' : document.documentNumber}</p>
        <p>วันที่ {document.issuedOn} · ยอด {document.amount} บาท · เวอร์ชัน {document.version}</p>
      </div>)}
    </section>}
    {item.allowedActions.includes('excess-disposition') && <section className="space-y-2 rounded border p-3">
      <h3 className="font-semibold">บันทึกแนวทางยอดส่วนเกิน</h3>
      <p>การบันทึกนี้ไม่สั่งจ่ายธนาคาร และเซิร์ฟเวอร์จะตรวจยอดกับรายการจ่ายที่ยืนยันแล้วอีกครั้ง</p>
      <select className="block w-full rounded border p-2" value={excessDisposition}
        onChange={event => setExcessDisposition(event.target.value as typeof excessDisposition)}>
        <option value="AwaitingExternalReview">รอตรวจภายนอก</option>
        {item.detail?.excess?.acceptedPaymentIds.length === 0 &&
          <option value="NoAdditionalPaymentRequired">ไม่ต้องจ่ายเพิ่มเติม</option>}
        {!!item.detail?.excess?.acceptedPaymentIds.length &&
          <option value="FinancialSettlementRecorded">บันทึกผลจ่ายภายนอกที่ตรวจแล้ว</option>}
      </select>
      <label className="block">หลักฐานประกอบ
        <input type="file" accept="image/png,image/jpeg,application/pdf" disabled={busy || !!pending}
          onChange={event => {
            const file = event.target.files?.[0];
            event.target.value = '';
            if (file) void uploadExcessEvidence(file);
          }} />
      </label>
      <p role="status">หลักฐานพร้อมใช้ {excessEvidenceIds.length} ไฟล์</p>
      <button className="underline" disabled={busy || !!pending || !!error || !reason.trim() || excessEvidenceIds.length === 0}
        onClick={() => void send('excess/disposition', {
          entityId: item.entityId, disposition: excessDisposition, evidenceIds: excessEvidenceIds,
          paymentIds: excessDisposition === 'FinancialSettlementRecorded'
            ? item.detail?.excess?.acceptedPaymentIds ?? [] : []
        })}>บันทึกแนวทางพร้อมเหตุผล</button>
    </section>}
    {item.detail?.reason && <p>เหตุผลรายการ: {item.detail.reason}</p>}
    <label className="block">เหตุผลการตรวจ
      <textarea className="block w-full rounded border p-2" maxLength={2000} value={reason}
        onChange={event => setReason(event.target.value)} />
    </label>
    {action === 'round-approve' || action === 'round-check' || action === 'refund-review' ? <div className="flex gap-4">
      <button className="underline" disabled={disabled || action.startsWith('round-') && !round}
        onClick={() => void send(action === 'refund-review' ? 'refunds/review' : 'rounds/review',
          { entityId: item.roundId ?? item.entityId, decision: 'Approve', reason })}>อนุมัติรายการนี้</button>
      <button className="underline" disabled={disabled || action.startsWith('round-') && !round}
        onClick={() => void send(action === 'refund-review' ? 'refunds/review' : 'rounds/review',
          { entityId: item.roundId ?? item.entityId, decision: 'Return', reason })}>ส่งกลับพร้อมเหตุผล</button>
    </div> : null}
    {['incident-review', 'recovery-review', 'correction-review', 'case-payment-review',
      'excess-payment-review', 'completion-review'].includes(action) && <button className="underline"
      disabled={disabled} onClick={() => void send(operations[action], { entityId: item.entityId })}>
      ยืนยันข้อเท็จจริงของรายการนี้
    </button>}
    {['correction-review', 'case-payment-review', 'excess-payment-review'].includes(action) &&
      <div className="flex gap-4">
        <button className="underline" disabled={disabled}
          onClick={() => void send(operations[action].replace('/confirm', '/return'), { entityId: item.entityId })}>
          ส่งกลับพร้อมเหตุผล
        </button>
        <button className="underline" disabled={disabled}
          onClick={() => void send(operations[action].replace('/confirm', '/reject'), { entityId: item.entityId })}>
          ปฏิเสธพร้อมเหตุผล
        </button>
      </div>}
    {action === 'duplicate-review' && <div className="flex gap-4">
      <button className="underline" disabled={disabled || !comparison}
        onClick={() => void send('duplicates/review', { entityId: item.entityId, sameDocument: true })}>
        ยืนยันว่าเป็นเอกสารเดียวกัน
      </button>
      <button className="underline" disabled={disabled || !comparison}
        onClick={() => void send('duplicates/review', { entityId: item.entityId, sameDocument: false })}>
        ยืนยันว่าคนละเอกสาร
      </button>
    </div>}
  </article>;
}

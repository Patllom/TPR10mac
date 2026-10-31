 'use client';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPaymentClient, PaymentError } from '@/lib/field-advances/payment-client';
import { createRequestClient } from '@/lib/field-advances/request-client';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import type { PaymentSummary, PaymentUiContext, PaymentCatalog, InstallmentDetail, PaymentAction, PaymentProposal, PaymentCommand } from '@/lib/field-advances/payment-types';
import InstallmentEditor from './InstallmentEditor';
import PaymentRecoveryPanel from './PaymentRecoveryPanel';
type Screen = { summary: PaymentSummary; context: PaymentUiContext; catalog: PaymentCatalog; detail: InstallmentDetail | null; owner: string };
const states: Record<string, string> = { Draft: 'ร่างงวด', PendingReview: 'รอตรวจ', Returned: 'ส่งกลับแก้ไข', Reviewed: 'ตรวจผ่าน ยังไม่จ่าย', Paying: 'กำลังดำเนินการจ่าย', OutcomePending: 'รอตรวจสอบผล ห้ามจ่ายซ้ำ', CancelPending: 'รอยืนยันยกเลิก', Cancelled: 'ยกเลิกแล้ว', Paid: 'จ่ายแล้ว' };
export default function PaymentDetails({ userId, requestId }: { userId: string; requestId: string }) {
  const client = useMemo(() => createPaymentClient(userId), [userId]);
  const [screen, setScreen] = useState<Screen | null>(null), [error, setError] = useState(''), [busy, setBusy] = useState(false), [pending, setPending] = useState<string | null>(null);
  const abort = useRef(new AbortController()), active = useRef(true), locked = useRef(false), pendingId = useRef<string | null>(null), selected = useRef<string | null>(null), generation = useRef(0);
  const clear = useCallback(() => { active.current = false; generation.current++; abort.current.abort(); setScreen(null); setPending(null); pendingId.current = null; setError('ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่'); }, []);
  const fail = useCallback((e: unknown) => { if (!active.current) return; if (e && typeof e === 'object' && 'clearProtectedData' in e && e.clearProtectedData) { clear(); return; } setError(e instanceof Error ? e.message : 'โหลดไม่สำเร็จ'); }, [clear]);
  const load = useCallback(async () => {
    const ticket = ++generation.current, signal = abort.current.signal;
    const context = await client.getPaymentContext(requestId, signal);
    const summary = await client.getPaymentSummary(requestId, signal), catalog = await client.installments(requestId, signal);
    const owner = (await createRequestClient(userId).details(requestId, signal)).ownerPersonId;
    const id = selected.current ?? catalog.installments[0]?.installmentId;
    const detail = id ? await client.installment(requestId, id, signal) : null;
    if (summary.familyVersion !== context.familyVersion || summary.requestVersion !== context.requestVersion
      || detail && (detail.summary.familyVersion !== context.familyVersion || detail.summary.requestVersion !== context.requestVersion || !context.installments.some(x => x.installmentId === id && x.installmentVersion === detail.summary.installmentVersion))) throw new PaymentError(409);
    if (active.current && ticket === generation.current) { setScreen({ context, summary, catalog, detail, owner }); setError(''); }
  }, [client, requestId, userId]);
  useEffect(() => {
    active.current = true; abort.current = new AbortController(); const lifetime = generation; let disposed = false;
    const refresh = async () => { if (disposed || !active.current || locked.current) return; locked.current = true; try { await load(); } catch (e) { if (!disposed && active.current) { setScreen(null); fail(e); } } finally { if (!disposed) locked.current = false; } };
    void refresh(); const timer = setInterval(() => void refresh(), 15000), unsubscribe = subscribeAuthChanges(clear);
    const focus = () => { if (!locked.current && !pendingId.current) { setScreen(null); void refresh(); } }; window.addEventListener('focus', focus);
    return () => { disposed = true; active.current = false; locked.current = false; abort.current.abort(); lifetime.current++; clearInterval(timer); unsubscribe(); window.removeEventListener('focus', focus); };
  }, [load, clear, fail]);
  async function reload() { if (locked.current || !active.current) return; locked.current = true; setBusy(true); try { await load(); } catch (e) { setScreen(null); fail(e); } finally { locked.current = false; setBusy(false); } }
  async function send(action: PaymentAction, input: unknown, reason: string, proposal?: PaymentProposal) {
    if (!screen || locked.current || pendingId.current || !active.current) return;
    const id = screen.detail?.summary.installmentId;
    const actions = proposal ? screen.context.proposals.find(x => x.proposalId === proposal.id)?.allowedActions : action === 'installments' ? screen.context.allowedActions : screen.context.installments.find(x => x.installmentId === id)?.allowedActions;
    if (!actions?.includes(action)) return;
    locked.current = true; setBusy(true); setError('');
    const meta: PaymentCommand = { commandId: crypto.randomUUID(), familyVersion: screen.context.familyVersion, requestVersion: screen.context.requestVersion, installmentVersion: action === 'installments' ? null : screen.detail?.summary.installmentVersion ?? null, reason };
    const target = action === 'installments' ? {} : proposal ? { proposalId: proposal.id } : { installmentId: id };
    const payload = ['submit', 'start', 'cancel', 'confirm'].includes(action) ? { ...target, ...meta } : { ...target, meta, ...(action === 'review' ? { outcome: input } : action === 'takeover' ? { evidenceIds: input } : { input }) };
    let committed = false;
    try {
      const result = await client.sendPaymentCommand(requestId, action, payload, abort.current.signal);
      committed = true;
      if (result.installmentId) selected.current = result.installmentId;
      await load();
    } catch (e) {
      if (!active.current) return;
      if (e && typeof e === 'object' && 'clearProtectedData' in e && e.clearProtectedData) { clear(); return; }
      // Once committed, a refresh failure from either client must never expose old payment actions.
      if (committed || e instanceof PaymentError && (e.unknownResult || e.commandId && e.status >= 500)) { pendingId.current = meta.commandId; setPending(meta.commandId); }
      fail(e);
      if (e instanceof PaymentError && e.status === 409) { setScreen(null); try { await load(); } catch (next) { fail(next); } }
    } finally { locked.current = false; setBusy(false); }
  }
  async function lookup() {
    if (!pendingId.current || locked.current || !active.current) return; locked.current = true; setBusy(true);
    try { const result = await client.lookupCommand(pendingId.current, abort.current.signal); if (result.requestId !== requestId) throw new PaymentError(403); if (result.installmentId) selected.current = result.installmentId; await load(); pendingId.current = null; setPending(null); } catch (e) { fail(e); } finally { locked.current = false; setBusy(false); }
  }
  async function download(id: string) {
    try {
      const blob = await client.downloadEvidence(id, abort.current.signal);
      if (!active.current) return;
      const url = URL.createObjectURL(blob), link = document.createElement('a');
      link.href = url; link.download = 'หลักฐาน'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 0);
    } catch (e) { fail(e); }
  }
  const detail = screen?.detail, revision = detail?.revisions.find(x => x.isCurrent);
  const actions = pending ? [] : screen?.context.installments.find(x => x.installmentId === detail?.summary.installmentId)?.allowedActions ?? [];
  return <div className="space-y-4 [&_button]:rounded [&_button]:border [&_button]:px-3 [&_button]:py-2 [&_button:disabled]:opacity-50">
    {error && <p role="alert">{error}</p>}
    {pending && <div role="status"><p>กำลังตรวจสอบผล ห้ามจ่ายซ้ำ</p><button disabled={busy} onClick={() => void lookup()}>ตรวจผลคำสั่งเดิม</button></div>}
    <button disabled={busy || !!pending} onClick={() => void reload()}>โหลดใหม่</button>
    {screen && <><p className="break-all">คำขอ {requestId}</p><dl className="grid grid-cols-2 gap-3 rounded border p-4">{(['approved', 'reserved', 'paid', 'available'] as const).map((key, i) => <div key={key}><dt>{['วงเงินอนุมัติ', 'ยอดกันไว้', 'จ่ายแล้ว', 'พร้อมจัดงวด'][i]}</dt><dd data-testid={`payment-${key}`}>{screen.summary[key]}</dd></div>)}</dl><p>บาท · การตรวจผ่านและการกันวงเงินไม่เท่ากับจ่ายเงินจริง</p>
      {screen.summary.holdCodes.length > 0 && <p role="status">เหตุระงับ: {screen.summary.holdCodes.join(', ')}</p>}
      {screen.catalog.evidence.length > 0 && <section aria-label="หลักฐานระดับคำขอ"><h2>หลักฐานของคำขอนี้</h2><p>รายการนี้รวมไฟล์ที่เตรียมไว้ การอัปโหลดไม่ได้ยืนยันว่าจ่ายแล้ว</p>{screen.catalog.evidence.map(e => <button className="block break-all text-left" key={e.id} disabled={busy} onClick={() => void download(e.id)}>ดาวน์โหลดหลักฐานคำขอ {e.id}</button>)}</section>}
      <label className="block">งวดจ่าย<select className="block w-full border p-2" value={detail?.summary.installmentId ?? ''} disabled={busy || !!pending} onChange={e => { selected.current = e.target.value; setScreen(null); void reload(); }}>{screen.catalog.installments.map(x => <option key={x.installmentId} value={x.installmentId!}>{states[x.state] ?? x.state} · {x.installmentId}</option>)}</select></label>
      {!pending && screen.context.allowedActions.includes('installments') && <InstallmentEditor key={`new-${screen.context.requestVersion}`} ownerPersonId={screen.owner} busy={busy} save={(input, reason) => void send('installments', input, reason)} />}
      {detail && revision && <section className="space-y-4"><h2 className="font-semibold" data-testid="installment-state">{states[detail.summary.state] ?? detail.summary.state}</h2><section aria-label="ทบทวนก่อนจ่าย" className="space-y-2 rounded border p-4"><h3>ฉบับงวด {revision.revisionNumber} {revision.reviewedAtUtc ? 'ตรวจผ่าน' : 'ยังไม่ตรวจผ่าน'}</h3><p className="break-all">ผู้รับ {revision.destination.recipientPersonId}</p><p>ยอด {revision.amount} บาท · {revision.destination.channel === 'Cash' ? 'เงินสด' : 'โอนเงิน'}</p>{revision.destination.channel === 'Transfer' && <><p>{revision.destination.bankCode}</p><p>{revision.destination.accountNumber}</p><p>{revision.destination.accountHolder}</p></>}<p>{revision.reason}</p></section>
        {actions.includes('update') && <details><summary>แก้ไขงวด (ต้องมีเหตุผลและตรวจใหม่)</summary><InstallmentEditor key={revision.id} ownerPersonId={screen.owner} initial={revision} busy={busy} save={(input, reason) => void send('update', input, reason)} /></details>}
        {actions.includes('submit') && <button disabled={busy} onClick={() => void send('submit', null, revision.reason)}>ส่งตรวจงวด</button>}
        {actions.includes('review') && <Review busy={busy} send={(outcome, reason) => void send('review', outcome, reason)} />}
        {actions.includes('start') && <><p>ทบทวนผู้รับ ยอด ช่องทางและบัญชีฉบับตรวจผ่านก่อนเริ่ม จากนั้นจ่ายภายนอกเพียงครั้งเดียว</p><button disabled={busy} onClick={() => void send('start', null, 'ทบทวนฉบับตรวจผ่านก่อนจ่าย')}>เริ่มจ่าย</button></>}
        {!pending && <PaymentRecoveryPanel key={`${detail.summary.installmentId}-${detail.summary.installmentVersion}`} detail={detail} actions={actions} context={screen.context} busy={busy} send={(a, input, reason, p) => void send(a, input, reason, p)} upload={async file => {
          if (locked.current || !active.current) return;
          locked.current = true; setBusy(true);
          try {
            const ticket = await client.uploadEvidence(requestId, file, abort.current.signal);
            // First participation can advance the family version. Refresh before enabling commands.
            await load();
            if (active.current) return ticket;
          } catch (e) { setScreen(null); fail(e); }
          finally { locked.current = false; setBusy(false); }
        }} download={id => void download(id)} />}
      </section>}
    </>}
  </div>;
}
function Review({ busy, send }: { busy: boolean; send: (outcome: string, reason: string) => void }) {
  const [reason, setReason] = useState('ตรวจผู้รับ ยอด ช่องทางและหลักฐานแล้ว');
  return <div className="space-y-2"><label className="block">เหตุผลตรวจงวด<textarea className="block w-full border p-2" value={reason} onChange={e => setReason(e.target.value)} /></label><button disabled={busy || !reason.trim()} onClick={() => send('Approve', reason)}>ตรวจผ่านงวด</button><button disabled={busy || !reason.trim()} onClick={() => send('Return', reason)}>ส่งกลับแก้ไขงวด</button></div>;
}

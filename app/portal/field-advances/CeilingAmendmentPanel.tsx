'use client';
import { useEffect, useRef, useState } from 'react';
import { createPaymentClient, PaymentError } from '@/lib/field-advances/payment-client';
import { createRequestClient, RequestError } from '@/lib/field-advances/request-client';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import type { PaymentAction, PaymentWorkflowContext, PaymentProposal, PaymentSummary } from '@/lib/field-advances/payment-types';
import type { RequestView, RequestContent } from '@/lib/field-advances/request-types';
import type { PolicySnapshot } from '@/lib/field-advances/policy-types';
import SupplementEditor from './SupplementEditor';
import PaymentDispositionPanel from './PaymentDispositionPanel';
type Pending = { id: string; committed: boolean; action: PaymentAction; payload: unknown; resultId?: string };
export default function CeilingAmendmentPanel({ userId, requestId, source, onChanged, onBusy, onAccessLost }: {
  userId: string; requestId: string; source?: RequestView; onChanged?: () => void; onBusy?: (value: boolean) => void; onAccessLost?: () => void;
}) {
  const [context, setContext] = useState<PaymentWorkflowContext | null>(null), [proposals, setProposals] = useState<PaymentProposal[]>([]);
  const [summary, setSummary] = useState<PaymentSummary | null>(null), [policy, setPolicy] = useState<PolicySnapshot | null>(null);
  const [editing, setEditing] = useState<'supplement' | 'amendment' | null>(null), [reason, setReason] = useState('');
  const [error, setError] = useState(''), [busy, setBusy] = useState(false), [uncertain, setUncertain] = useState(false), [refresh, setRefresh] = useState(0);
  const live = useRef<AbortController | null>(null), polling = useRef<AbortController | null>(null), working = useRef(false), pending = useRef<Pending | null>(null);
  const current = useRef<PaymentWorkflowContext | null>(null);
  function hide() { current.current = null; setContext(null); setProposals([]); setSummary(null); setPolicy(null); setEditing(null); }
  function clear() { live.current?.abort(); polling.current?.abort(); pending.current = null; hide(); setReason(''); setUncertain(false); onBusy?.(false); }
  function failure(e: unknown) {
    if ((e instanceof RequestError || e instanceof PaymentError) && e.clearProtectedData) { clear(); onAccessLost?.(); }
    setError(e instanceof Error ? e.message : 'โหลดไม่สำเร็จ');
  }
  async function load(signal: AbortSignal) {
    const client = createPaymentClient(userId), next = await client.getWorkflowContext(requestId, signal);
    const catalog = await client.installments(requestId, signal);
    const account = next.variant === 'request' ? await client.getPaymentSummary(requestId, signal) : null;
    signal.throwIfAborted();
    if (account && (next.familyVersion !== account.familyVersion || next.requestVersion !== account.requestVersion)) throw new PaymentError(409);
    const latest = await client.getWorkflowContext(requestId, signal); signal.throwIfAborted();
    if (JSON.stringify(latest) !== JSON.stringify(next)) throw new PaymentError(409);
    if (current.current && JSON.stringify(current.current) !== JSON.stringify(next)) { setEditing(null); setPolicy(null); }
    current.current = next; setContext(next); setProposals(catalog.proposals); setSummary(account);
  }
  useEffect(() => {
    const abort = new AbortController(); live.current = abort;
    const update = async () => {
      if (abort.signal.aborted || working.current || polling.current || pending.current) return;
      const poll = new AbortController(); polling.current = poll;
      const signal = AbortSignal.any([abort.signal, poll.signal]);
      try { await load(signal); } catch (e) { if (!signal.aborted) { hide(); failure(e); } }
      finally { if (polling.current === poll) polling.current = null; }
    };
    void update(); const timer = setInterval(() => void update(), 15000), unsub = subscribeAuthChanges(() => { clear(); setError('ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่'); });
    return () => { abort.abort(); polling.current?.abort(); polling.current = null; clearInterval(timer); unsub(); };
    // Pending commands survive parent version refreshes; only identity/request changes remount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId, requestId, refresh]);
  const sourceVersion = source?.version;
  const observedSourceVersion = useRef(sourceVersion);
  useEffect(() => {
    if (observedSourceVersion.current === sourceVersion) return;
    observedSourceVersion.current = sourceVersion;
    // Reconciliation reloads its own context. Never abort it for a parent refresh.
    if (!pending.current) setRefresh(x => x + 1);
  }, [sourceVersion]);
  async function execute(command: Pending, recover = false) {
    if (working.current || !live.current || live.current.signal.aborted) return;
    working.current = true; pending.current = command; setBusy(true); onBusy?.(true); polling.current?.abort(); polling.current = null; setError('');
    const signal = live.current.signal;
    // Observe a successful mutation before the request client's post-command reads.
    const transport: typeof fetch = async (...args) => {
      const response = await fetch(...args);
      if (args[1]?.method === 'POST' && response.ok && String(args[0]).includes('/field-advances/')) command.committed = true;
      return response;
    };
    try {
      if (command.action === 'supplements') {
        if (!command.resultId) {
          const payload = command.payload as { content: RequestContent; reason: string };
          const result = await createRequestClient(userId, transport).supplement(requestId, payload.content, { commandId: command.id, expectedVersion: 0, reason: payload.reason }, signal);
          command.resultId = result.current.id;
        }
        await createRequestClient(userId).details(command.resultId, signal); signal.throwIfAborted();
        window.location.assign(`/portal/field-advances/${command.resultId}`); return;
      }
      const client = createPaymentClient(userId, transport);
      if (recover) {
        const result = await client.lookupCommand(command.id, signal);
        if (result.requestId !== requestId) throw new PaymentError(403);
      } else await client.sendPaymentCommand(requestId, command.action, command.payload, signal);
      command.committed = true;
      await load(signal); signal.throwIfAborted(); pending.current = null; setUncertain(false); setEditing(null); setPolicy(null); setReason(''); onBusy?.(false); onChanged?.();
    } catch (e) {
      if (signal.aborted) return;
      if ((e instanceof RequestError || e instanceof PaymentError) && e.clearProtectedData) failure(e);
      else if (command.committed || e instanceof RequestError && e.unknownResult || e instanceof PaymentError && (e.unknownResult || recover)) {
        hide(); setUncertain(true); setError(`ยังยืนยันข้อมูลปัจจุบันไม่ได้ ตรวจผลด้วยคำสั่งเดิม (${command.id})`);
      } else { pending.current = null; hide(); setUncertain(false); onBusy?.(false); failure(e); setRefresh(x => x + 1); }
    } finally { working.current = false; setBusy(false); }
  }
  function send(action: PaymentAction, why: string, target?: string, outcome?: string, content?: RequestContent) {
    const ctx = current.current; if (!ctx || pending.current || working.current || !why.trim()) return;
    const id = crypto.randomUUID(), meta = { commandId: id, familyVersion: ctx.familyVersion, requestVersion: ctx.requestVersion, installmentVersion: null, reason: why };
    const payload = action === 'supplements' ? { content, reason: why } : action === 'ceiling-amendment' ? { content, meta } : action === 'disposition' ? { outcome, meta } : target ? { ...meta, proposalId: target } : meta;
    void execute({ id, action, payload, committed: false });
  }
  async function edit(mode: 'supplement' | 'amendment') {
    if (!source || !live.current || working.current || pending.current) return;
    const signal = live.current.signal; working.current = true; setBusy(true); polling.current?.abort(); polling.current = null;
    try {
      const next = await createPaymentClient(userId).getWorkflowContext(requestId, signal);
      if (next.variant !== 'request' || !next.allowedActions.includes(mode === 'supplement' ? 'supplements' : 'ceiling-amendment')) throw new PaymentError(403);
      if (next.workflow.ownerPersonId !== source.ownerPersonId || next.workflow.scope.projectId !== source.scope.projectId || next.workflow.scope.workspaceId !== source.scope.workspaceId) throw new PaymentError(403);
      const config = await createRequestClient(userId).configuration(next.workflow.scope, signal); signal.throwIfAborted();
      current.current = next; setContext(next); setPolicy(config); setEditing(mode);
    } catch (e) { if (!signal.aborted) failure(e); }
    finally { working.current = false; setBusy(false); }
  }
  const workflow = context?.variant === 'request' ? context.workflow : null;
  return <section aria-label="ปรับวงเงินและส่วนเพิ่ม" className="space-y-4 rounded border p-4">
    {error && <p role="alert">{error}</p>}
    <button disabled={busy} onClick={() => { hide(); setRefresh(x => x + 1); }}>โหลดสถานะวงเงินใหม่</button>
    {uncertain && <button disabled={busy} onClick={() => { if (pending.current) void execute(pending.current, true); }}>ตรวจผลคำสั่งวงเงินเดิม</button>}
    {!uncertain && context && <>
      {workflow && <>
        {workflow.everPaid ? <p>เคยจ่ายเงินจริงแล้ว ขอใหม่เฉพาะส่วนเพิ่ม ไม่ปรับวงเงินเดิม</p> : <p>ก่อนจ่ายเงินจริง สามารถเสนอปรับวงเงินเมื่อเข้าเงื่อนไข</p>}
        {workflow.outcomeUnclear && <p role="status">พักการปรับวงเงิน: ยังมีผลจ่ายไม่ชัดเจน ต้องสะสางก่อน</p>}
        {workflow.pendingSupplementState && <p>มีส่วนเพิ่มค้าง ({workflow.pendingSupplementState}) รวมส่งกลับ/พัก ต้องพิจารณาให้เสร็จก่อนส่งรายการถัดไป</p>}
        {workflow.amendmentState && <><p>ผลฉบับปรับวงเงิน: {workflow.amendmentState} ไม่ลบวงเงินอนุมัติเดิม</p><p>อนุมัติฉบับใหม่: {workflow.amendmentApproved ? 'ครบแล้ว' : 'ยังไม่ครบ'} · สะสางงวดเดิม: {workflow.installmentsCleared ? 'ครบแล้ว' : 'ยังไม่ครบ'}</p><p>เปิดใช้วงเงินใหม่เมื่อครบสองเงื่อนไขเท่านั้น และคงเหตุระงับอื่น</p></>}
        {summary && <p>วงเงินอนุมัติที่ใช้จ่ายอยู่ {summary.approved} บาท · เหตุระงับ {summary.holdCodes.join(', ') || 'ไม่มี'}</p>}
        {context.variant === 'request' && !editing && <div className="flex flex-wrap gap-3">
          {source && context.allowedActions.includes('supplements') && <button disabled={busy} onClick={() => void edit('supplement')}>ขอส่วนเพิ่ม</button>}
          {source && context.allowedActions.includes('ceiling-amendment') && <button disabled={busy} onClick={() => void edit('amendment')}>ปรับวงเงินก่อนจ่าย</button>}
          {context.allowedActions.includes('ceiling-activate') && <><label>เหตุผลเปิดใช้วงเงิน<input value={reason} onChange={e => setReason(e.target.value)} /></label><button disabled={busy || !reason.trim()} onClick={() => send('ceiling-activate', reason)}>เปิดใช้วงเงินฉบับใหม่</button></>}
        </div>}
        {editing && policy && source && <SupplementEditor key={editing} source={source} policy={policy} amendment={editing === 'amendment'} disabled={busy} onCancel={() => setEditing(null)} onSave={(content, why) => send(editing === 'amendment' ? 'ceiling-amendment' : 'supplements', why, undefined, undefined, content)} />}
      </>}
      <PaymentDispositionPanel context={context} proposals={proposals} disabled={busy} onCommand={send} />
    </>}
  </section>;
}

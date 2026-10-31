'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Clear account-bound state on navigation. */
import { useState, useEffect, useId } from 'react';
import { createRequestClient } from '@/lib/field-advances/request-client';
import { createPaymentClient } from '@/lib/field-advances/payment-client';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import type { PaymentWorkflowContext, PaymentProposal } from '@/lib/field-advances/payment-types';
import type { RequestScopeChoice } from '@/lib/field-advances/request-types';
export default function PaymentDispositionPanel({ context, proposals, disabled, onCommand }: {
  context: PaymentWorkflowContext; proposals: PaymentProposal[]; disabled: boolean;
  onCommand: (action: 'disposition' | 'confirm' | 'finalize', reason: string, target?: string, outcome?: string) => void;
}) {
  const [reason, setReason] = useState('');
  const reasonId = useId();
  const can = (name: 'resume-original' | 'terminate-remaining') => context.variant === 'request' && context.allowedActions.includes(name);
  return <section aria-label="ยุติยอดหรือกลับใช้เงื่อนไขเดิม" className="space-y-3 rounded border p-4">
    <h2 className="font-semibold">ยุติยอดหรือกลับใช้เงื่อนไขเดิม</h2>
    <p>ยุติเฉพาะยอดที่ยังไม่จ่าย ยังต้องเคลียร์เงินที่จ่ายแล้ว</p>
    <p>คงวงเงินอนุมัติเดิมสำหรับฐานสะสม ไม่ปลดเหตุจ่ายผิดยอดหรือผิดคน</p>
    <p>ต้องมีผู้ตรวจเสนอและผู้ดำรงบทบาทอนุมัติขั้นสุดท้ายปัจจุบันยืนยันต่างคน ไม่มีผู้เข้าเงื่อนไขให้คงพัก</p>
    <fieldset disabled={disabled} className="min-w-0 space-y-3"><legend>เหตุผลและการตัดสินใจ</legend>
      <label className="block" htmlFor={reasonId}>เหตุผลยุติยอดหรือกลับใช้เดิม</label><textarea id={reasonId} className="block w-full rounded border p-2" maxLength={2000} value={reason} onChange={e => setReason(e.target.value)} />
      {can('resume-original') && <button disabled={!reason.trim()} onClick={() => onCommand('disposition', reason, undefined, 'ResumeOriginal')}>เสนอกลับใช้เงื่อนไขเดิม</button>}
      {can('terminate-remaining') && <button disabled={!reason.trim()} onClick={() => onCommand('disposition', reason, undefined, 'TerminateRemaining')}>เสนอยุติยอดที่ยังไม่จ่าย</button>}
      {proposals.filter(p => p.kind === 'PaymentDisposition').map(p => {
        const actions = context.proposals.find(x => x.proposalId === p.id)?.allowedActions ?? [];
        return <article key={p.id} className="space-y-2 border p-3"><p>{p.after.outcome === 'ResumeOriginal' ? 'กลับใช้เงื่อนไขเดิม' : 'ยุติยอดที่ยังไม่จ่าย'} · {p.state === 'Pending' ? 'รอยืนยัน' : 'ยืนยันแล้ว'}</p><p>{p.reason}</p>
          {p.before.approved !== null && <p>วงเงินอนุมัติเดิม {p.before.approved} บาท · ฐานอำนาจ {p.before.basis} บาท</p>}
          {actions.includes('confirm') && <button disabled={!reason.trim()} onClick={() => onCommand('confirm', reason, p.id)}>ยืนยันการตัดสินใจนี้</button>}
          {actions.includes('finalize') && <button disabled={!reason.trim()} onClick={() => onCommand('finalize', reason, p.id)}>ทำให้การยุติยอดมีผล</button>}
        </article>;
      })}
    </fieldset>
    <p>ก่อนยุติมีผล ต้องสะสางงวดกำลังจ่าย/ไม่ทราบผล และยืนยันยกเลิกงวดที่ยังไม่จ่ายครบ ไม่คืนยอดกันไว้เอง</p>
    {context.variant === 'request' && <a className="underline" href={`/portal/field-advances/payments?requestId=${context.requestId}`}>สะสางและยกเลิกงวดตามสิทธิ์</a>}
  </section>;
}
export function DispositionQueue({ userId }: { userId: string }) {
  const [scopes, setScopes] = useState<RequestScopeChoice[]>([]), [selected, setSelected] = useState('');
  const [scopePage, setScopePage] = useState(1), [page, setPage] = useState(1), [rows, setRows] = useState<string[]>([]), [error, setError] = useState('');
  const [more, setMore] = useState(false), [moreScopes, setMoreScopes] = useState(false), [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const abort = new AbortController(); let busy = false;
    const clear = () => { abort.abort(); setRows([]); setScopes([]); setError('ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่'); };
    const load = async () => {
      if (busy || abort.signal.aborted) return; busy = true;
      try {
        const raw = await createRequestClient(userId).scopes(scopePage, 20, abort.signal);
        const choices = raw.filter(s => s.capabilities.includes('request.approve'));
        const chosen = choices.find(s => s.scope.projectId === selected) ?? (!selected ? choices[0] : undefined);
        const client = createPaymentClient(userId);
        const candidates = chosen ? await client.queue(chosen.scope, (page - 1) * 20, 20, abort.signal) : [];
        const ids: string[] = [];
        for (const candidate of candidates) {
          const c = await client.getWorkflowContext(candidate.requestId, abort.signal);
          if (c.variant === 'disposition' || c.proposals.length) ids.push(c.requestId);
        }
        abort.signal.throwIfAborted(); setScopes(choices); setMoreScopes(raw.length === 20); setRows(ids); setMore(candidates.length === 20); setError('');
      } catch (e) { if (!abort.signal.aborted) { setRows([]); setScopes([]); setError(e instanceof Error ? e.message : 'โหลดไม่สำเร็จ'); } }
      finally { busy = false; }
    };
    void load(); const timer = setInterval(() => void load(), 15000), unsub = subscribeAuthChanges(clear);
    return () => { abort.abort(); clearInterval(timer); unsub(); };
  }, [userId, selected, scopePage, page, refresh]);
  return <section aria-label="งานยืนยันยุติยอด" className="space-y-3"><h2>งานยืนยันยุติยอด</h2>{error && <p role="alert">{error}</p>}
    <button onClick={() => setRefresh(x => x + 1)}>โหลดงานยุติยอดใหม่</button>
    <label>โครงการงานยุติยอด<select className="block w-full border p-2" value={selected || scopes[0]?.scope.projectId || ''} onChange={e => { setSelected(e.target.value); setRows([]); setPage(1); }}>{scopes.map(s => <option key={s.scope.projectId} value={s.scope.projectId!}>{s.workspaceName} · {s.projectName}</option>)}</select></label>
    <button disabled={scopePage === 1} onClick={() => { setScopePage(x => x - 1); setSelected(''); }}>โครงการยุติยอดหน้าก่อน</button><button disabled={!moreScopes} onClick={() => { setScopePage(x => x + 1); setSelected(''); }}>โครงการยุติยอดหน้าถัดไป</button>
    <ul>{rows.map(id => <li key={id}><a className="break-all underline" href={`/portal/field-advances/approvals?disposition=${id}`}>พิจารณายุติยอด {id}</a></li>)}</ul>
    <button disabled={page === 1} onClick={() => { setRows([]); setPage(x => x - 1); }}>งานยุติยอดหน้าก่อน</button><button disabled={!more} onClick={() => { setRows([]); setPage(x => x + 1); }}>งานยุติยอดหน้าถัดไป</button>
  </section>;
}

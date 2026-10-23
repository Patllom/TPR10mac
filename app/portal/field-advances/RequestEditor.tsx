'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation clears account-bound command payloads. */
import { useEffect, useRef, useState } from 'react';
import { createRequestClient, RequestError } from '@/lib/field-advances/request-client';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { diffRequestContent } from '@/lib/field-advances/request-view';
import type { PolicySnapshot } from '@/lib/field-advances/policy-types';
import type { FinanceScope, RequestView, RequestContent, RequestScreen, RequestHistory, RequestPersonChoice, ConfirmationQuote, RequestCommandResult, RequestCommand } from '@/lib/field-advances/request-types';
import RequestDetails from './RequestDetails';

const inputClass = 'block w-full min-w-0 rounded-lg border border-slate-400 bg-transparent p-2';
const buttonClass = 'rounded-lg border border-orange-700 px-4 py-2 disabled:opacity-50';
const emptyContent = (): RequestContent => ({ kind: 'Individual', purpose: '', fieldStart: '', fieldEnd: '', neededOn: '', teamPersonIds: [], lines: [] });
type Pending = { id: string; committed?: boolean; run: (signal: AbortSignal) => Promise<RequestCommandResult> };

export default function RequestEditor({ userId, requestId, scope }: { userId: string; requestId?: string; scope?: FinanceScope }) {
  const [row, setRow] = useState<RequestView | null>(null), [screen, setScreen] = useState<RequestScreen | null>(null);
  const [content, setContent] = useState<RequestContent>(emptyContent), [policy, setPolicy] = useState<PolicySnapshot | null>(null);
  const [owners, setOwners] = useState<RequestPersonChoice[]>([]), [owner, setOwner] = useState(''), [ownerPage, setOwnerPage] = useState(1);
  const [team, setTeam] = useState<RequestPersonChoice[]>([]), [teamNames, setTeamNames] = useState<Record<string, string>>({}), [prefix, setPrefix] = useState(''), [teamPage, setTeamPage] = useState(1);
  const [history, setHistory] = useState<RequestHistory[]>([]), [historyPage, setHistoryPage] = useState(1);
  const [quote, setQuote] = useState<ConfirmationQuote | null>(null), [dialog, setDialog] = useState<'confirm' | 'withdraw' | null>(null);
  const [reason, setReason] = useState(''), [error, setError] = useState(''), [status, setStatus] = useState(''), [busy, setBusy] = useState(false);
  const [ready, setReady] = useState(false), [uncertain, setUncertain] = useState(false), [refresh, setRefresh] = useState(0);
  const live = useRef<AbortController | null>(null), rowRef = useRef<RequestView | null>(null), pending = useRef<Pending | null>(null);
  const working = useRef(false), poll = useRef<AbortController | null>(null), dialogRef = useRef<HTMLDialogElement>(null);
  const client = useRef(createRequestClient(userId));
  const workspaceId = scope?.workspaceId, projectId = scope?.projectId;

  function clearProtected() {
    live.current?.abort(); poll.current?.abort(); pending.current = null; rowRef.current = null;
    setRow(null); setScreen(null); setContent(emptyContent()); setPolicy(null); setOwners([]); setOwner(''); setTeam([]); setTeamNames({}); setPrefix(''); setHistory([]); setQuote(null); setDialog(null); setReason(''); setStatus(''); setUncertain(false); setReady(false);
  }
  function failure(e: unknown) {
    if (e instanceof RequestError && e.clearProtectedData) clearProtected();
    setError(pending.current?.committed ? `บันทึกแล้ว แต่โหลดข้อมูลประกอบไม่สำเร็จ กรุณาตรวจผลด้วยคำสั่งเดิม (${pending.current.id})` : e instanceof Error ? e.message : 'บริการไม่พร้อม กรุณาลองใหม่');
  }
  async function load(signal: AbortSignal, force = false, current?: RequestView) {
    const id = current?.id ?? rowRef.current?.id ?? requestId;
    if (id) {
      const next = current ?? await client.current.details(id, signal);
      const context = await client.current.screen(next, signal);
      const events = await client.current.history(id, historyPage, 20, signal);
      const config = context.actions.includes('edit') ? await client.current.configuration(next.scope, signal) : null;
      signal.throwIfAborted();
      const changed = rowRef.current?.version !== next.version || rowRef.current?.revisionId !== next.revisionId;
      if (force || changed) { setContent(structuredClone(next.content)); setQuote(null); setDialog(null); }
      rowRef.current = next; setRow(next); setScreen(context); setHistory(events); setPolicy(config); setReady(true);
    } else if (workspaceId && projectId) {
      const selected = { workspaceId, projectId };
      const config = await client.current.configuration(selected, signal);
      const choices = await client.current.people(selected, '', ownerPage, 20, signal);
      signal.throwIfAborted(); setPolicy(config); setOwners(choices); setReady(true);
      setContent(old => old.lines.length ? old : { ...old, lines: [{ id: crypto.randomUUID(), categoryId: config.content.categories.find(c => c.active)?.id ?? '', description: '', mode: 'QuantityRate', quantity: '', unitRate: null, lumpSum: null }] });
    } else throw new RequestError(400);
  }
  useEffect(() => {
    const controller = new AbortController(); live.current = controller;
    const update = async () => {
      if (controller.signal.aborted || working.current || poll.current) return;
      const check = new AbortController(); poll.current = check;
      const signal = AbortSignal.any([controller.signal, check.signal]);
      try { await load(signal); }
      catch (e) { if (!signal.aborted) failure(e); }
      finally { if (poll.current === check) poll.current = null; }
    };
    void update(); const timer = setInterval(() => void update(), 15000);
    const unsubscribe = subscribeAuthChanges(() => { clearProtected(); setError('ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่'); });
    return () => { controller.abort(); poll.current?.abort(); poll.current = null; clearInterval(timer); unsubscribe(); };
    // State used by the timer is intentionally read through rowRef; drafts survive an unchanged poll.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [userId, requestId, workspaceId, projectId, refresh, historyPage, ownerPage]);
  useEffect(() => {
    if (dialog && !dialogRef.current?.open) dialogRef.current?.showModal();
  }, [dialog]);

  async function readAction(run: (signal: AbortSignal) => Promise<void>) {
    if (working.current || !live.current || live.current.signal.aborted) return;
    working.current = true; setBusy(true); poll.current?.abort(); poll.current = null; setError('');
    try { await run(live.current.signal); }
    catch (e) { if (!live.current.signal.aborted) failure(e); }
    finally { working.current = false; setBusy(false); }
  }
  async function execute(command: Pending) {
    if (working.current || !live.current || live.current.signal.aborted) return;
    pending.current = command; working.current = true; setBusy(true); poll.current?.abort(); poll.current = null; setError(''); setStatus('');
    try {
      const signal = live.current.signal;
      const result = await command.run(signal);
      signal.throwIfAborted();
      // The mutation and current read succeeded. Keep its identity even when a
      // subsequent projection fails; reconciliation must never become a new create.
      command.committed = true; rowRef.current = result.current;
      await load(signal, true, result.current);
      pending.current = null; setUncertain(false); setStatus('บันทึกสำเร็จ'); setReason('');
    } catch (e) {
      if (live.current.signal.aborted) return;
      if (e instanceof RequestError && e.clearProtectedData) failure(e);
      else if (command.committed) {
        setUncertain(true); setQuote(null); setDialog(null); failure(e);
      }
      else if (e instanceof RequestError && (e.unknownResult || e.refreshRequired && e.status !== 409)) {
        setUncertain(true); setDialog(null); setError(`ไม่ทราบผลปัจจุบัน กรุณาตรวจผลด้วยคำสั่งเดิม (${command.id})`);
      } else { pending.current = null; setUncertain(false); setQuote(null); setDialog(null); failure(e); }
    } finally { working.current = false; setBusy(false); }
  }
  function command(run: (meta: RequestCommand, signal: AbortSignal) => Promise<RequestCommandResult>) {
    if (pending.current || working.current) return;
    const meta = { commandId: crypto.randomUUID(), expectedVersion: rowRef.current?.version ?? 0, reason };
    void execute({ id: meta.commandId, run: signal => run(meta, signal) });
  }
  const editable = ready && (!row || screen?.actions.includes('edit'));
  const dirty = !!row && diffRequestContent(row.content, content).length > 0;
  const disabled = busy || uncertain;
  const can = (action: string) => screen?.actions.includes(action) ?? false;
  function save() {
    const payload = structuredClone(content), current = rowRef.current;
    command((meta, signal) => current ? client.current.update(current.id, payload, meta, signal)
      : client.current.create({ scope: { workspaceId: workspaceId!, projectId: projectId! }, ownerPersonId: owner, content: payload }, meta, signal));
  }
  function simple(action: 'sendToOwner' | 'returnToAuthor' | 'revise' | 'withdraw' | 'copy') {
    const current = rowRef.current; if (!current) return;
    command((meta, signal) => client.current[action](current.id, meta, signal));
  }
  function review(outcome: 'Approve' | 'Return' | 'Reject') {
    const current = rowRef.current; if (!current?.currentStep) return;
    const decision = { outcome, stepId: current.currentStep.id };
    command((meta, signal) => client.current.review(current.id, decision, meta, signal));
  }
  function searchTeam(page: number) {
    const selected = row?.scope ?? scope; if (!selected) return;
    void readAction(async signal => { const choices = await client.current.teamPeople(selected, prefix, page, 20, signal); signal.throwIfAborted(); setTeam(choices); setTeamPage(page); });
  }
  const updateLine = (index: number, patch: Partial<RequestContent['lines'][number]>) => setContent(c => ({ ...c, lines: c.lines.map((line, i) => i === index ? { ...line, ...patch } : line) }));
  return <div className="space-y-5">
    <nav className="flex flex-wrap gap-4 underline"><a href="/portal/field-advances">คำขอของฉัน / ที่ยื่นแทน</a><a href="/portal/field-advances/approvals">งานรออนุมัติ</a></nav>
    {error && <p role="alert" className="rounded border border-red-700 p-3">{error}</p>}{status && <p role="status">{status}</p>}
    <button className={buttonClass} disabled={busy} onClick={() => { setError(''); setRefresh(x => x + 1); }}>โหลดใหม่</button>
    {uncertain && <button className={buttonClass} disabled={busy} onClick={() => { if (pending.current) void execute(pending.current); }}>ตรวจผลด้วยคำสั่งเดิม</button>}
    {row && screen && <><RequestDetails row={row} screen={screen} history={history} quote={dirty ? null : quote} /><div className="flex gap-3"><button disabled={historyPage === 1 || busy} onClick={() => setHistoryPage(x => x - 1)}>ประวัติหน้าก่อน</button><button disabled={history.length < 20 || busy} onClick={() => setHistoryPage(x => x + 1)}>ประวัติหน้าถัดไป</button></div></>}
    {editable && dirty && <p>มีข้อมูลที่ยังไม่บันทึก กรุณาบันทึกร่างก่อนทบทวนหรือส่งให้ผู้รับผิดชอบ</p>}
    {editable && policy && <form onSubmit={e => { e.preventDefault(); save(); }} className="space-y-5">
      <fieldset disabled={disabled} className="min-w-0 space-y-4"><legend className="font-semibold">จัดทำคำขอเงินทดรอง</legend>
        {!row && <><label className="block">ผู้รับผิดชอบเงิน (ชื่อในระบบ)<select required className={inputClass} value={owner} onChange={e => setOwner(e.target.value)}><option value="">เลือกผู้รับผิดชอบ</option>{owners.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}</select></label><div className="flex gap-3"><button type="button" disabled={ownerPage === 1} onClick={() => setOwnerPage(p => p - 1)}>ผู้รับผิดชอบหน้าก่อน</button><button type="button" disabled={owners.length < 20} onClick={() => setOwnerPage(p => p + 1)}>ผู้รับผิดชอบหน้าถัดไป</button></div></>}
        <p>ชื่อในระบบ (ชื่อผู้ใช้) · ไม่ใช่ชื่อเต็มตามกฎหมาย</p>
        <label className="block">รูปแบบคำขอ<select className={inputClass} value={content.kind} onChange={e => setContent(c => ({ ...c, kind: e.target.value as RequestContent['kind'], teamPersonIds: e.target.value === 'Individual' ? [] : c.teamPersonIds }))}><option value="Individual">รายบุคคล</option><option value="Team">ทีม</option></select></label>
        <label className="block">รายละเอียดงาน<textarea required className={inputClass} value={content.purpose} onChange={e => setContent(c => ({ ...c, purpose: e.target.value }))} /></label>
        <div className="grid gap-4 sm:grid-cols-3">{([['fieldStart', 'วันเริ่มออกสนาม'], ['fieldEnd', 'วันสิ้นสุดออกสนาม'], ['neededOn', 'วันที่ต้องการเงิน']] as const).map(([key, label]) => <label key={key}>{label}<input required type="date" className={inputClass} value={content[key]} onChange={e => setContent(c => ({ ...c, [key]: e.target.value }))} /></label>)}</div>
        {content.kind === 'Team' && <section className="space-y-3"><label className="block">ค้นหาสมาชิกทีม (ชื่อในระบบ)<input className={inputClass} value={prefix} maxLength={100} onChange={e => setPrefix(e.target.value)} /></label><button type="button" className={buttonClass} onClick={() => searchTeam(1)}>ค้นหาสมาชิกทีม</button>
          <p>เฉพาะพนักงานปัจจุบันในบริษัทเดียวกัน การเลือกไม่เพิ่มสิทธิ์การเงิน</p>
          {team.map(p => <label key={p.id} className="block"><input type="checkbox" checked={content.teamPersonIds.includes(p.id)} onChange={e => { setTeamNames(names => ({ ...names, [p.id]: p.name })); setContent(c => ({ ...c, teamPersonIds: e.target.checked ? [...c.teamPersonIds, p.id] : c.teamPersonIds.filter(id => id !== p.id) })); }} /> {p.name}</label>)}
          <div className="flex gap-3"><button type="button" disabled={teamPage === 1} onClick={() => searchTeam(teamPage - 1)}>สมาชิกหน้าก่อน</button><button type="button" disabled={team.length < 20} onClick={() => searchTeam(teamPage + 1)}>สมาชิกหน้าถัดไป</button></div>
          <ul>{content.teamPersonIds.map(id => <li key={id}>{teamNames[id] ?? `สมาชิกที่เลือก ${id}`} <button type="button" onClick={() => setContent(c => ({ ...c, teamPersonIds: c.teamPersonIds.filter(x => x !== id) }))}>นำออก</button></li>)}</ul>
        </section>}
        <p>จำนวนไม่เกิน 4 ตำแหน่ง เงินไม่เกิน 2 ตำแหน่ง · ยอดตัดสินจากเซิร์ฟเวอร์เมื่อบันทึก</p>
        {content.lines.map((line, i) => {
          const category = policy.content.categories.find(c => c.id === line.categoryId), standard = category?.standardRate != null;
          return <fieldset key={line.id} className="min-w-0 space-y-3 rounded-xl border p-4"><legend>รายการ {i + 1}</legend>
            <label className="block">หมวด {i + 1}<select className={inputClass} value={line.categoryId} onChange={e => { const c = policy.content.categories.find(x => x.id === e.target.value); updateLine(i, { categoryId: e.target.value, mode: 'QuantityRate', quantity: '', unitRate: c?.standardRate != null ? null : '', lumpSum: null }); }}>{policy.content.categories.filter(c => c.active || c.id === line.categoryId).map(c => <option key={c.id} value={c.id} disabled={!c.active}>{c.name}{!c.active ? ' (ปิดใช้)' : ''}</option>)}</select></label>
            <label className="block">รายละเอียดรายการ {i + 1}<input required className={inputClass} value={line.description} onChange={e => updateLine(i, { description: e.target.value })} /></label>
            {standard ? <p>อัตรามาตรฐาน {category?.standardRate} บาท / {category?.unit} (แก้ไขไม่ได้)</p> : <label className="block">วิธีคำนวณ {i + 1}<select className={inputClass} value={line.mode} onChange={e => updateLine(i, e.target.value === 'LumpSum' ? { mode: 'LumpSum', quantity: null, unitRate: null, lumpSum: '' } : { mode: 'QuantityRate', quantity: '', unitRate: '', lumpSum: null })}><option value="QuantityRate">จำนวน × อัตรา</option><option value="LumpSum">เหมารวม</option></select></label>}
            {line.mode === 'QuantityRate' ? <div className="grid gap-3 sm:grid-cols-2"><label>จำนวน {i + 1}<input required inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,4})?" className={inputClass} value={line.quantity ?? ''} onChange={e => updateLine(i, { quantity: e.target.value })} /></label>{!standard && <label>อัตรา {i + 1}<input required inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,2})?" className={inputClass} value={line.unitRate ?? ''} onChange={e => updateLine(i, { unitRate: e.target.value })} /></label>}</div> : <label className="block">ยอดเหมารวม {i + 1}<input required inputMode="decimal" pattern="[0-9]+(\.[0-9]{1,2})?" className={inputClass} value={line.lumpSum ?? ''} onChange={e => updateLine(i, { lumpSum: e.target.value })} /></label>}
            <button type="button" className={buttonClass} onClick={() => setContent(c => ({ ...c, lines: c.lines.filter(x => x.id !== line.id) }))}>ลบรายการ {i + 1}</button>
          </fieldset>;
        })}
        <button type="button" className={buttonClass} onClick={() => { const category = policy.content.categories.find(c => c.active); if (category) setContent(c => ({ ...c, lines: [...c.lines, { id: crypto.randomUUID(), categoryId: category.id, description: '', mode: 'QuantityRate', quantity: '', unitRate: category.standardRate === null ? '' : null, lumpSum: null }] })); }}>เพิ่มรายการ</button>
        <button type="submit" className={`${buttonClass} ml-3 bg-orange-700 text-white`}>บันทึกร่าง</button>
      </fieldset>
    </form>}
    {row && screen && screen.actions.length > 0 && <fieldset disabled={disabled} className="min-w-0 space-y-4"><legend className="font-semibold">ดำเนินการ</legend>
      <label className="block">เหตุผลดำเนินการ<textarea className={inputClass} maxLength={2000} value={reason} onChange={e => setReason(e.target.value)} /></label>
      <div className="flex flex-wrap gap-3">
        {can('send-to-owner') && <button className={buttonClass} disabled={dirty} onClick={() => simple('sendToOwner')}>ส่งให้ผู้รับผิดชอบ</button>}
        {can('confirm') && <button className={buttonClass} disabled={dirty} onClick={() => void readAction(async signal => { const result = await client.current.quote(row.id, signal); await load(signal, true, result.request); signal.throwIfAborted(); setQuote(result.quote); setDialog('confirm'); })}>ทบทวนและยืนยัน</button>}
        {can('return-to-author') && <button className={buttonClass} onClick={() => simple('returnToAuthor')}>ส่งกลับผู้จัดทำ</button>}
        {can('review') && <><button className={buttonClass} onClick={() => review('Approve')}>อนุมัติขั้นนี้</button><button className={buttonClass} onClick={() => review('Return')}>ส่งกลับ</button><button className={buttonClass} onClick={() => review('Reject')}>ปฏิเสธ</button></>}
        {can('revise') && <button className={buttonClass} onClick={() => simple('revise')}>สร้างฉบับแก้ไข</button>}
        {can('withdraw') && <button className={buttonClass} onClick={() => setDialog('withdraw')}>ถอนคำขอ</button>}
        {can('copy') && <button className={buttonClass} onClick={() => simple('copy')}>คัดลอกเป็นคำขอใหม่</button>}
      </div>
    </fieldset>}
    {dialog && row && <dialog ref={dialogRef} aria-labelledby="request-dialog-title" onCancel={() => { if (!busy) setDialog(null); }} className="w-[min(92vw,40rem)] space-y-4 rounded-2xl border p-6 backdrop:bg-black/40">
      <h2 id="request-dialog-title" className="text-xl font-semibold">{dialog === 'confirm' ? 'ทบทวนก่อนยืนยันและส่ง' : 'ทบทวนก่อนถอนคำขอ'}</h2>
      <p>{row.content.purpose} · {row.content.fieldStart} ถึง {row.content.fieldEnd}</p>
      <p>วันที่ต้องการเงิน {row.content.neededOn}</p>
      <p className="break-all">โครงการ {row.scope.projectId} · ผู้รับผิดชอบเงิน {row.ownerPersonId}</p>
      {row.content.teamPersonIds.length > 0 && <p className="break-all">รหัสบุคคลสมาชิกทีม: {row.content.teamPersonIds.join(', ')}</p>}
      {dialog === 'confirm' && quote ? <><p>ยอดยืนยันจากเซิร์ฟเวอร์ {quote.estimate.total} บาท</p><p>ลำดับ: {quote.levels.join(' → ')}</p><p>วันเคลียร์ {quote.policy.content.clearingDays} วัน</p><ul>{quote.estimate.lines.map(l => <li key={l.id}>{l.description}: {l.amount} บาท</li>)}</ul><button className={buttonClass} disabled={disabled} onClick={() => { const id = row.id, hash = quote.confirmationHash; command((meta, signal) => client.current.confirm(id, hash, meta, signal)); }}>ยืนยันและส่ง</button></> : <form onSubmit={e => { e.preventDefault(); simple('withdraw'); }} className="space-y-4"><label className="block">เหตุผลถอนคำขอ<textarea required maxLength={2000} className={inputClass} value={reason} onChange={e => setReason(e.target.value)} /></label><button className={buttonClass} disabled={disabled}>ยืนยันถอนคำขอ</button></form>}
      <button className={buttonClass} disabled={busy} onClick={() => setDialog(null)}>ยกเลิก</button>
    </dialog>}
  </div>;
}

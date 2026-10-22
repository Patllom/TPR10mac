'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Navigation discards scoped state. */
import { useCallback, useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { createGrantClient, discoverGrants, GrantError, grantCapabilities, grantLevelFields, grantDifferences, grantStatus, type GrantSpec, type GrantRecord, type GrantHistory, type Recipient } from '@/lib/field-advances/grant-client';
import type { ScopeChoice } from '@/lib/field-advances/policy-types';

const root = '/portal/field-advances/settings';
const input = 'mt-1 block w-full min-w-0 rounded-lg border border-slate-400 bg-transparent p-2';
const button = 'rounded-lg border border-orange-600 px-4 py-2 text-sm font-semibold disabled:opacity-40';
type State = { choices: ScopeChoice[]; choice: ScopeChoice | null; rows: GrantRecord[]; selected: GrantRecord | null; source: GrantRecord | null; spec: GrantSpec | null; history: GrantHistory[]; recipients: Recipient[]; prefix: string; recipientPage: number; editing: boolean; reason: string; blocked: boolean; busy: boolean; error: string; message: string };
const empty = (): State => ({ choices: [], choice: null, rows: [], selected: null, source: null, spec: null, history: [], recipients: [], prefix: '', recipientPage: 1, editing: false, reason: '', blocked: true, busy: false, error: '', message: '' });
const dateLabel = (value: string) => new Date(value).toLocaleString('th-TH', { timeZone: 'Asia/Bangkok' }) + ' (Asia/Bangkok)';
const localDate = (value: string | null) => value ? new Date(Date.parse(value) + 7 * 3600000).toISOString().slice(0, 16) : '';
const utcDate = (value: string) => value && Number.isFinite(Date.parse(value + '+07:00')) ? new Date(value + '+07:00').toISOString() : '';

export default function GrantEditor({ userId, workspaceId, projectId, screen = 'grants' }: { userId: string; workspaceId: string | null; projectId: string | null; screen?: string }) {
  const [state, set] = useState<State>(empty);
  const generation = useRef(0), pending = useRef<AbortController | null>(null), busy = useRef(false);
  const latest = useRef(state); latest.current = state;
  const cancel = useCallback(() => { generation.current++; pending.current?.abort(); busy.current = false; }, []);
  const invalidate = useCallback(() => { cancel(); set(empty()); }, [cancel]);
  const failure = useCallback((e: unknown) => {
    const error = e instanceof GrantError ? e : new GrantError(503);
    // Unknown outcomes are blocked too. Never retain protected data behind an error banner.
    set(s => error.status === 400 ? { ...s, busy: false, error: error.message, message: '' } : { ...empty(), error: error.message });
  }, []);
  const load = useCallback(async () => {
    cancel(); const turn = generation.current, controller = new AbortController(); pending.current = controller;
    busy.current = true; set({ ...empty(), busy: true });
    try {
      const choices = await discoverGrants(userId, controller.signal);
      const choice = workspaceId ? choices.find(c => c.scope.workspaceId === workspaceId && c.scope.projectId === projectId) : null;
      if (workspaceId && (!choice || screen === 'reviews' && !choice.capabilities.includes('grant.review'))) throw new GrantError(403);
      const rows = choice ? await createGrantClient(choice.scope, userId, controller.signal).list(screen === 'reviews') : [];
      if (turn === generation.current) set({ ...empty(), choices, choice: choice ?? null, rows, blocked: false });
    } catch (e) { if (turn === generation.current) failure(e); }
    finally { if (turn === generation.current) busy.current = false; }
  }, [cancel, failure, projectId, screen, userId, workspaceId]);
  useEffect(() => {
    void load();
    const unsubscribe = subscribeAuthChanges(invalidate);
    const visibility = () => { if (document.hidden) invalidate(); else void load(); };
    const focus = () => { if (!document.hidden) void load(); };
    // Recheck authority while an unchanged page remains open; retain unsent input only when still authorized.
    const timer = window.setInterval(async () => {
      const s = latest.current, turn = generation.current;
      if (document.hidden || busy.current || s.blocked) return;
      try {
        const choices = await discoverGrants(userId, pending.current?.signal);
        if (!s.choice) {
          if (turn === generation.current && !busy.current) set(current => current.choice ? current : { ...current, choices });
          return;
        }
        const choice = choices.find(c => c.scope.workspaceId === s.choice!.scope.workspaceId && c.scope.projectId === s.choice!.scope.projectId);
        if (!choice || JSON.stringify(choice.capabilities) !== JSON.stringify(s.choice.capabilities)) throw new GrantError(403);
        const client = createGrantClient(s.choice.scope, userId, pending.current?.signal);
        const rows = await client.list(screen === 'reviews');
        const target = s.source ?? s.selected;
        const seen = target ? await client.details(target.id) : null;
        if (seen && target && (seen.version !== target.version || seen.assignmentVersion !== target.assignmentVersion)) throw new GrantError(409);
        // Time can change status/actions without changing a stored version. Keep the unsent spec/reason,
        // and only refresh the same record still displayed after the asynchronous reads complete.
        const refresh = (record: GrantRecord | null) => seen && record?.id === seen.id
          && record.version === seen.version && record.assignmentVersion === seen.assignmentVersion ? seen : record;
        if (turn === generation.current && !busy.current) set(current => turn === generation.current && !busy.current
          ? { ...current, rows, selected: refresh(current.selected), source: refresh(current.source) } : current);
      } catch (e) { if (turn === generation.current && !busy.current) { cancel(); failure(e); } }
    }, 15000);
    document.addEventListener('visibilitychange', visibility); window.addEventListener('focus', focus);
    return () => { unsubscribe(); clearInterval(timer); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('focus', focus); cancel(); };
  }, [cancel, failure, invalidate, load, screen, userId]);
  async function operate(action: (client: ReturnType<typeof createGrantClient>) => Promise<Partial<State>>) {
    if (!state.choice || state.blocked || busy.current) return;
    const turn = generation.current; busy.current = true; set(s => ({ ...s, busy: true, message: '', error: '' }));
    try {
      const changes = await action(createGrantClient(state.choice.scope, userId, pending.current?.signal));
      if (turn === generation.current) set(s => ({ ...s, ...changes, busy: false }));
    } catch (e) { if (turn === generation.current) failure(e); }
    finally { if (turn === generation.current) busy.current = false; }
  }
  const query = workspaceId ? `?workspaceId=${workspaceId}${projectId ? `&projectId=${projectId}` : ''}` : '';
  const canPropose = state.choice?.capabilities.includes('grant.propose');
  const disabled = state.blocked || state.busy;
  function start(source: GrantRecord | null) {
    if (!state.choice || !canPropose || disabled) return;
    set(s => ({ ...s, source, spec: source ? structuredClone(source.spec) : { recipientId: '', scope: s.choice!.scope, capabilities: [], delegableCapabilities: [], approvalLimit: null, startsAt: new Date().toISOString(), endsAt: null },
      editing: true, recipients: [], prefix: '', recipientPage: 1, reason: '', error: '', message: '' }));
  }
  function update(spec: GrantSpec) { set(s => ({ ...s, spec, message: '' })); }
  async function open(row: GrantRecord) {
    await operate(async client => ({ selected: await client.details(row.id), history: await client.history(row.id), source: null, spec: null, editing: false, reason: '', recipients: [] }));
  }
  function command(action: 'propose' | 'approve' | 'return' | 'reject' | 'revoke') {
    const meta = { commandId: crypto.randomUUID(), expectedVersion: action === 'propose' ? 1 : action === 'revoke' ? state.selected!.assignmentVersion! : state.selected!.version, reason: state.reason };
    void operate(async client => {
      // Revalidate the comparison source before creating an additive proposal from it.
      if (action === 'propose' && state.source) { const seen = await client.details(state.source.id); if (seen.version !== state.source.version || seen.assignmentVersion !== state.source.assignmentVersion) throw new GrantError(409); }
      const result = action === 'propose' ? await client.proposeGrant(state.spec!, meta) : action === 'revoke' ? await client.revokeGrant(state.selected!.assignmentId!, meta, state.selected!.id) : await client.reviewGrant(state.selected!.id, action, meta);
      const id = action === 'revoke' ? state.selected!.id : result.id;
      return { selected: await client.details(id), history: await client.history(id), rows: await client.list(screen === 'reviews'), source: null, spec: null, editing: false, reason: '', recipients: [], message: ({ propose: 'เสนอสำเร็จ — รอผู้ตรวจคนละบุคคล', approve: 'อนุมัติสำเร็จ — เริ่มใช้ตามเวลา', return: 'ส่งกลับสำเร็จ — ต้องเสนอใหม่', reject: 'ปฏิเสธสำเร็จ', revoke: 'ถอนสำเร็จ' })[action] };
    });
  }
  const current = state.selected;
  return <div className="min-w-0 space-y-5">
    <nav className="flex flex-wrap gap-4 underline" aria-label="เมนูสิทธิ์"><a href={root}>กติกาการเงิน</a><a href={`${root}/grants${query}`}>มอบหมายสิทธิ์</a><a href={`${root}/reviews${query}${query ? '&' : '?'}domain=grants`}>งานรอตรวจสิทธิ์</a><a href={`${root}/history${query}`}>ประวัติสิทธิ์</a></nav>
    <p>ผู้เสนอ ผู้ตรวจ และผู้รับต้องเป็นคนละบุคคล · การมอบหมายต้องผ่านการตรวจทุกครั้ง</p>
    <button className={button} onClick={() => void load()}>โหลดสิทธิ์ใหม่</button>
    {state.busy && <p aria-live="polite">กำลังโหลดหรือดำเนินการ…</p>}
    {state.error && <p role="alert" className="rounded border border-red-400 p-3">{state.error}</p>}
    {state.message && <p role="status" className="rounded border border-green-600 p-3">{state.message}</p>}
    {!state.choice && !state.error && !state.busy && <section><h2 className="text-xl font-semibold">ขอบเขตสิทธิ์ที่จัดการได้</h2>{!state.choices.length && <p>ยังไม่มีสิทธิ์จัดการการมอบหมาย</p>}<ul>{state.choices.filter(c => screen !== 'reviews' || c.capabilities.includes('grant.review')).map(c => <li key={`${c.scope.workspaceId}/${c.scope.projectId}`}><a className="underline" href={`${root}/${screen}?workspaceId=${c.scope.workspaceId}${c.scope.projectId ? `&projectId=${c.scope.projectId}` : ''}${screen === 'reviews' ? '&domain=grants' : ''}`}>{c.workspaceName} · {c.projectName ?? 'ระดับบริษัท'}</a></li>)}</ul></section>}
    {state.choice && <>
      <h2 className="text-xl font-semibold">{state.choice.workspaceName} · {state.choice.projectName ?? 'ระดับบริษัท'}</h2>
      {screen === 'reviews' && <p>หากไม่มีผู้ตรวจที่เข้าเงื่อนไข รายการจะค้างรอตรวจ ไม่มีการอนุมัติอัตโนมัติ</p>}
      {canPropose && screen === 'grants' && <button className={button} disabled={disabled} onClick={() => start(null)}>เสนอสิทธิ์ใหม่</button>}
      <section className="space-y-3"><h3 className="font-semibold">{screen === 'reviews' ? 'สิทธิ์รอตรวจ' : screen === 'history' ? 'รายการประวัติสิทธิ์' : 'รายการสิทธิ์'}</h3>
        {!state.rows.length && <p>ยังไม่มีรายการที่อ่านได้</p>}
        {state.rows.map(r => <article key={r.id} className="flex flex-wrap items-center gap-3 rounded border p-3"><div className="min-w-0 flex-1"><p>{r.recipientName} · {grantStatus(r.effectiveState)}</p><p className="break-all text-xs">{r.id}</p></div><button className={button} disabled={disabled} onClick={() => void open(r)}>เปิดสิทธิ์</button></article>)}
      </section>
      {current && !state.editing && <section className="space-y-3 rounded border p-4">
        <h3 className="font-semibold">รายละเอียดสิทธิ์</h3><p>{current.recipientName}</p><p>{grantStatus(current.effectiveState)}</p>
        <p className="break-all">ผู้เสนอ: {current.proposedBy} · ผู้รับ: {current.spec.recipientId}</p>
        <p>ความสามารถ: {current.spec.capabilities.join(', ')}</p><p>มอบต่อได้: {current.spec.delegableCapabilities.join(', ') || 'ไม่มี'}</p>
        {grantLevelFields.map(([key, label]) => <p key={key}>{label}: {(current.spec[key] ?? []).join(', ') || 'ไม่มี'}</p>)}
        <p>วงเงิน: {current.spec.approvalLimit ?? 'ไม่มีอำนาจอนุมัติยอดเงิน'}</p><p>เริ่ม: {dateLabel(current.spec.startsAt)}</p><p>สิ้นสุด: {current.spec.endsAt ? dateLabel(current.spec.endsAt) : 'ไม่กำหนด'}</p>
        <p>รุ่นข้อเสนอ {current.version}{current.assignmentVersion ? ` · รุ่นสิทธิ์ ${current.assignmentVersion}` : ''}</p>
        {canPropose && screen !== 'history' && <button className={button} disabled={disabled} onClick={() => start(current)}>เตรียมข้อเสนอใหม่จากรายการนี้</button>}
        {screen !== 'history' && <><label className="block">เหตุผล<input className={input} value={state.reason} maxLength={1000} disabled={disabled} onChange={e => set(s => ({ ...s, reason: e.target.value }))} /></label>
          {current.state === 'Pending' && <><p>ตรวจผู้เกี่ยวข้อง ขอบเขต วงเงิน และช่วงเวลาก่อนอนุมัติ หากไม่มีอำนาจครบหรือเป็นบุคคลเดียวกันจะตรวจไม่ได้</p><div className="flex flex-wrap gap-3">
            <button className={button} disabled={disabled || !current.canApprove} onClick={() => command('approve')}>อนุมัติสิทธิ์</button>
            <button className={button} disabled={disabled || !current.canReview} onClick={() => command('return')}>ส่งกลับข้อเสนอ</button>
            <button className={button} disabled={disabled || !current.canReview} onClick={() => command('reject')}>ปฏิเสธข้อเสนอ</button></div></>}
          {current.assignmentId && <button className={button} disabled={disabled || !current.canRevoke} onClick={() => command('revoke')}>ถอนสิทธิ์</button>}</>}
      </section>}
      {state.editing && state.spec && <section className="space-y-4 rounded border p-4">
        <h3 className="font-semibold">ข้อเสนอสิทธิ์ใหม่</h3><p>สิทธิ์เดิมยังคงอยู่จนถอนแยกหรือหมดอายุ · ไม่รวมวงเงินหลายสิทธิ์</p>
        <fieldset disabled={disabled} className="min-w-0 space-y-4">
          <div className="flex flex-wrap items-end gap-3"><label className="min-w-0 flex-1">ค้นหาผู้รับ<input className={input} value={state.prefix} maxLength={100} onChange={e => set(s => ({ ...s, prefix: e.target.value, recipients: [], recipientPage: 1 }))} /></label>
            <button className={button} onClick={() => void operate(async c => ({ recipients: await c.recipients(state.prefix), recipientPage: 1 }))}>ค้นหาผู้รับ</button></div>
          <label className="block">ผู้รับสิทธิ์<select className={input} value={state.spec.recipientId} onChange={e => update({ ...state.spec!, recipientId: e.target.value })}><option value="">เลือกผู้รับ</option>
            {state.source && !state.recipients.some(r => r.id === state.source!.spec.recipientId) && <option value={state.source.spec.recipientId}>{state.source.recipientName}</option>}
            {state.recipients.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}</select></label>
          <div className="flex gap-3"><button className={button} disabled={state.recipientPage === 1} onClick={() => void operate(async c => ({ recipients: await c.recipients(state.prefix, state.recipientPage - 1), recipientPage: state.recipientPage - 1 }))}>ผู้รับหน้าก่อน</button><button className={button} disabled={state.recipients.length < 20} onClick={() => void operate(async c => ({ recipients: await c.recipients(state.prefix, state.recipientPage + 1), recipientPage: state.recipientPage + 1 }))}>ผู้รับหน้าถัดไป</button></div>
          <p>ขอบเขต: {state.choice.workspaceName} · {state.choice.projectName ?? 'ระดับบริษัท'}</p>
          <div className="grid gap-4 sm:grid-cols-2">{(['capabilities', 'delegableCapabilities'] as const).map(key => <fieldset key={key} className="min-w-0 rounded border p-3"><legend>{key === 'capabilities' ? 'ความสามารถที่ใช้' : 'ความสามารถที่มอบต่อได้'}</legend>{grantCapabilities.map(c => <label key={c} className="block break-all py-1"><input type="checkbox" checked={state.spec![key].includes(c)} onChange={e => update({ ...state.spec!, [key]: e.target.checked ? [...state.spec![key], c] : state.spec![key].filter(x => x !== c) })} /> {key === 'capabilities' ? 'ใช้' : 'มอบต่อ'} {c}</label>)}</fieldset>)}</div>
          <label className="block">วงเงินอำนาจ (บาท)<input className={input} inputMode="decimal" value={state.spec.approvalLimit ?? ''} onChange={e => update({ ...state.spec!, approvalLimit: e.target.value || null })} /></label><p className="text-sm">เว้นว่าง = ไม่มีอำนาจอนุมัติยอดเงิน</p>
          <p className="text-sm">ระบุรหัสระดับคั่นด้วยจุลภาค: supervisor, project-manager หรือรหัสระดับตามกติกาโครงการ การแต่งตั้งหลักต้องอยู่ในระดับอำนาจที่ตรงกัน และต้องมีอำนาจแต่งตั้งต่อจากต้นทาง</p>
          <div className="grid gap-4 sm:grid-cols-2">{grantLevelFields.map(([key, label]) => <label key={key}>{label}<input className={input} value={(state.spec![key] ?? []).join(', ')} onChange={e => update({ ...state.spec!, [key]: e.target.value ? e.target.value.split(',').map(v => v.trim()) : [] })} /></label>)}</div>
          <div className="grid gap-4 sm:grid-cols-2"><label>เริ่มใช้ (Asia/Bangkok)<input className={input} type="datetime-local" value={localDate(state.spec.startsAt)} onChange={e => update({ ...state.spec!, startsAt: utcDate(e.target.value) })} /></label>
            <label>สิ้นสุด (Asia/Bangkok)<input className={input} type="datetime-local" value={localDate(state.spec.endsAt)} onChange={e => update({ ...state.spec!, endsAt: e.target.value ? utcDate(e.target.value) : null })} /></label></div>
          <label className="block">เหตุผล<input className={input} value={state.reason} maxLength={1000} onChange={e => set(s => ({ ...s, reason: e.target.value }))} /></label>
        </fieldset>
        {state.source && <section><h4 className="font-semibold">ความต่างก่อนเสนอใหม่</h4><ul className="break-words">{grantDifferences(state.source.spec, state.spec).map(d => <li key={d}>{d}</li>)}</ul></section>}
        <div className="flex flex-wrap gap-3"><button className={button} disabled={disabled} onClick={() => command('propose')}>ส่งข้อเสนอ</button><button className={button} disabled={disabled} onClick={() => set(s => ({ ...s, editing: false, source: null, spec: null, reason: '', recipients: [] }))}>ยกเลิกการเตรียมข้อเสนอ</button></div>
      </section>}
      {!!state.history.length && !state.editing && <div className="overflow-x-auto"><table aria-label="ประวัติสิทธิ์" className="w-full text-left text-sm"><caption className="py-2 text-left font-semibold">ประวัติสิทธิ์</caption><thead><tr>{['เหตุการณ์', 'เวลา (Asia/Bangkok)', 'ผู้ดำเนินการ', 'เหตุผล'].map(h => <th className="p-2" key={h}>{h}</th>)}</tr></thead><tbody>{state.history.map((h, i) => <tr className="border-t" key={i}><td className="p-2">{grantStatus(h.outcome)}</td><td className="p-2">{dateLabel(h.at)}</td><td className="break-all p-2">{h.actorId}</td><td className="break-words p-2">{h.reason}</td></tr>)}</tbody></table></div>}
    </>}
  </div>;
}

'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation discards account/scope state. */
import { useCallback, useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { createPolicyClient, discoverPolicies, PolicyError } from '@/lib/field-advances/policy-client';
import { differences, failureState, sourceLabel, statusLabel } from '@/lib/field-advances/policy-view';
import type { CommandMeta, CompanyContext, PolicyContent, PolicyHistory, PolicyMode, PolicySnapshot, PolicyView, ScopeChoice } from '@/lib/field-advances/policy-types';

const root = '/portal/field-advances/settings';
const input = 'mt-1 block w-full min-w-0 rounded-lg border border-slate-400 bg-transparent p-2 disabled:opacity-80';
const button = 'rounded-lg border border-orange-600 px-4 py-2 text-sm font-semibold disabled:opacity-40';
type State = { choices: ScopeChoice[]; choice: ScopeChoice | null; policies: PolicyView[]; selected: PolicyView | null; source: PolicyView | null; content: PolicyContent | null; context: CompanyContext | null; effective: PolicySnapshot | null; history: PolicyHistory[]; reason: string; mode: PolicyMode; basis: string | null; editing: boolean; blocked: boolean; busy: boolean; error: string; message: string; total: string; levels: string[] };
const empty = (): State => ({ choices: [], choice: null, policies: [], selected: null, source: null, content: null, context: null, effective: null, history: [], reason: '', mode: 'Company', basis: null, editing: false, blocked: true, busy: false, error: '', message: '', total: '', levels: [] });
function blank(): PolicyContent { return { clearingDays: 0, bands: [], categories: ['เดินทาง', 'ที่พัก', 'อาหาร', 'เบี้ยเลี้ยง', 'น้ำมัน'].map(name => ({ id: crypto.randomUUID(), name, active: true, unit: null, standardRate: null, evidenceTypes: [] })) }; }
function ContentSummary({ content }: { content: PolicyContent }) {
  return <div className="space-y-2 text-sm"><p>เคลียร์ภายใน {content.clearingDays} วันปฏิทิน นับวันถัดจากวันจบงานเป็นวันที่ 1</p>
    <ul className="space-y-1">{content.bands.map(b => <li key={b.order}>ระดับ {b.levelCode}: ไม่เกิน {b.upperInclusive} บาท (รวมยอดเท่าขอบ)</li>)}</ul>
    <ul className="space-y-1">{content.categories.map(c => <li key={c.id}>{c.name} · {c.active ? 'เปิดใช้' : 'ปิดใช้'} · {c.standardRate === null ? 'ไม่มีอัตรามาตรฐาน' : `${c.standardRate} บาท/${c.unit}`} · หลักฐาน: {c.evidenceTypes.join(', ')}</li>)}</ul>
  </div>;
}
export default function PolicyEditor({ userId, workspaceId, projectId, screen }: { userId: string; workspaceId: string | null; projectId: string | null; screen: string }) {
  const [state, set] = useState<State>(empty);
  const generation = useRef(0); const pending = useRef<AbortController | null>(null); const busy = useRef(false);
  const latest = useRef(state); latest.current = state;
  const cancel = useCallback(() => { generation.current++; pending.current?.abort(); busy.current = false; }, []);
  const invalidate = useCallback(() => { cancel(); set(empty()); }, [cancel]);
  const failure = useCallback((error: unknown) => {
    if (error instanceof DOMException && error.name === 'AbortError') return;
    const status = error instanceof PolicyError ? error.status : 503;
    const message = error instanceof PolicyError ? error.message : new PolicyError(503).message;
    set(s => status === 401 || status === 403 || status === 409 ? { ...failureState(s, status), ...empty(), error: message }
      : { ...s, busy: false, blocked: status !== 400, error: message, message: '' });
  }, []);
  const load = useCallback(async () => {
    pending.current?.abort(); const controller = new AbortController(); pending.current = controller;
    const turn = ++generation.current; busy.current = true; set({ ...empty(), busy: true });
    try {
      const choices = await discoverPolicies(userId, controller.signal);
      const choice = workspaceId ? choices.find(c => c.scope.workspaceId === workspaceId && c.scope.projectId === projectId) : null;
      if (workspaceId && !choice) throw new PolicyError(403);
      if (choice && screen === 'reviews' && !choice.capabilities.includes('policy.review')) throw new PolicyError(403);
      let policies: PolicyView[] = [], effective: PolicySnapshot | null = null, context: CompanyContext | null = null;
      if (choice) {
        const client = createPolicyClient(choice.scope, userId, controller.signal);
        policies = await client.loadPolicies(choice.scope, screen === 'reviews');
        try { effective = await client.effective(); } catch (e) { if (!(e instanceof PolicyError) || e.status !== 409) throw e; }
        if (choice.scope.projectId && effective) context = await client.companyContext();
      }
      if (generation.current === turn) set({ ...empty(), choices, choice: choice ?? null, policies, effective, context, blocked: false });
    } catch (error) { if (generation.current === turn) failure(error); }
    finally { if (generation.current === turn) busy.current = false; }
  }, [failure, projectId, screen, userId, workspaceId]);
  useEffect(() => {
    void load();
    const unsubscribe = subscribeAuthChanges(invalidate);
    const visibility = () => { if (document.hidden) invalidate(); else void load(); };
    const focus = () => { if (!document.hidden) void load(); };
    let checking = false;
    // Match grant management's bounded authority refresh without discarding an authorized draft.
    const timer = window.setInterval(async () => {
      const current = latest.current, turn = generation.current;
      if (document.hidden || busy.current || current.blocked || checking) return;
      checking = true;
      try {
        const choices = await discoverPolicies(userId, pending.current?.signal);
        if (current.choice) {
          const choice = choices.find(c => c.scope.workspaceId === current.choice!.scope.workspaceId && c.scope.projectId === current.choice!.scope.projectId);
          if (!choice || JSON.stringify(choice.capabilities) !== JSON.stringify(current.choice.capabilities)) throw new PolicyError(403);
          const client = createPolicyClient(choice.scope, userId, pending.current?.signal);
          for (const target of [current.selected, current.source]) {
            if (!target) continue;
            const seen = await client.details(target.id);
            if (seen.version !== target.version || seen.contentHash !== target.contentHash) throw new PolicyError(409);
          }
        }
        if (turn === generation.current && !busy.current) set(s => turn === generation.current && !busy.current ? { ...s, choices } : s);
      } catch (error) {
        if (turn === generation.current && !busy.current) { invalidate(); failure(error); }
      } finally { checking = false; }
    }, 15000);
    document.addEventListener('visibilitychange', visibility); window.addEventListener('focus', focus);
    return () => { unsubscribe(); clearInterval(timer); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('focus', focus); cancel(); };
  }, [cancel, failure, invalidate, load, userId]);
  async function operate(action: (client: ReturnType<typeof createPolicyClient>) => Promise<Partial<State>>) {
    if (!state.choice || state.blocked || busy.current) return;
    const turn = ++generation.current; busy.current = true; set(s => ({ ...s, busy: true, error: '', message: '' }));
    try {
      const changes = await action(createPolicyClient(state.choice.scope, userId, pending.current?.signal));
      if (generation.current === turn) set(s => ({ ...s, ...changes, busy: false }));
    } catch (error) { if (generation.current === turn) failure(error); }
    finally { if (generation.current === turn) busy.current = false; }
  }
  async function open(row: PolicyView) {
    await operate(async client => {
      const selected = await client.details(row.id); const history = await client.history(row.id);
      const context = state.choice!.scope.projectId ? await client.companyContext(row.id) : null;
      const source = selected.sourcePolicyRevisionId ? await client.details(selected.sourcePolicyRevisionId) : null;
      return { selected, source, content: structuredClone(selected.content), history, context, mode: selected.mode, basis: selected.baseCompanyVersionId, editing: ['Draft', 'Returned'].includes(selected.state), reason: '', message: '' };
    });
  }
  const canEdit = !!state.choice?.capabilities.includes(projectId ? 'policy.edit.project' : 'policy.edit.company');
  const canReview = !!state.choice?.capabilities.includes('policy.review');
  const editable = state.editing && canEdit && !state.blocked && !state.busy && state.mode !== 'Inherit';
  function start() {
    const source = state.selected ?? state.policies[0] ?? null;
    const mode = source?.mode ?? (projectId ? 'Override' : 'Company');
    const basis = projectId ? (mode === 'Inherit' ? state.context?.latest.id : source?.baseCompanyVersionId ?? state.context?.latest.id) ?? null : null;
    set(s => ({ ...s, source, selected: null, content: structuredClone(mode === 'Inherit' && s.context ? s.context.latest.content : source?.content ?? s.effective?.content ?? blank()), history: [], mode, basis, editing: true, reason: '', message: '', error: '' }));
  }
  function meta(): CommandMeta {
    const selected = state.selected;
    return { commandId: crypto.randomUUID(), expectedVersion: selected?.version ?? 1, reason: state.reason,
      policy: { mode: state.mode, sourcePolicyRevisionId: selected?.sourcePolicyRevisionId ?? state.source?.id ?? null, baseCompanyVersionId: state.basis,
        expectedActiveRevisionId: state.editing ? (selected?.activeRevisionId ?? state.source?.activeRevisionId ?? state.policies[0]?.activeRevisionId ?? null) : selected?.expectedActiveRevisionId ?? null,
        expectedActiveVersion: state.editing ? (selected?.activeVersion ?? state.source?.activeVersion ?? state.policies[0]?.activeVersion ?? 0) : selected?.expectedActiveVersion ?? 0,
        expectedContentHash: selected?.contentHash ?? null } };
  }
  function command(action: 'save' | 'submit' | 'approve' | 'return' | 'reject') {
    const commandMeta = meta();
    // Submit/review must bind exactly the seen draft, not substitute a newer active pointer.
    if (action !== 'save' && state.selected) commandMeta.policy = { ...commandMeta.policy, expectedActiveRevisionId: state.selected.expectedActiveRevisionId, expectedActiveVersion: state.selected.expectedActiveVersion };
    void operate(async client => {
      const result = action === 'save' ? await client.savePolicy(state.selected?.id ?? null, state.choice!.scope, state.content!, commandMeta)
        : action === 'submit' ? await client.submitPolicy(state.selected!.id, commandMeta) : await client.reviewPolicy(state.selected!.id, action, commandMeta);
      const selected = await client.details(result.id);
      const policies = await client.loadPolicies(state.choice!.scope, screen === 'reviews');
      const history = await client.history(result.id);
      const effective = action === 'approve' ? await client.effective() : state.effective;
      const context = projectId ? await client.companyContext(result.id) : state.context;
      // Replacement approval retires/increments the source; poll against its refreshed projection.
      const source = selected.sourcePolicyRevisionId ? await client.details(selected.sourcePolicyRevisionId) : null;
      return { selected, source, content: structuredClone(selected.content), policies, history, effective, context, basis: selected.baseCompanyVersionId, mode: selected.mode, editing: ['Draft', 'Returned'].includes(selected.state), reason: '', levels: action === 'approve' ? [] : state.levels,
        message: ({ save: 'บันทึกสำเร็จ', submit: 'ส่งตรวจสำเร็จ', approve: 'อนุมัติสำเร็จ กรุณาประเมินวงเงินใหม่', return: 'ส่งกลับสำเร็จ', reject: 'ปฏิเสธสำเร็จ' })[action] };
    });
  }
  function update(content: PolicyContent) { set(s => ({ ...s, content, message: '' })); }
  const query = workspaceId ? `?workspaceId=${workspaceId}${projectId ? `&projectId=${projectId}` : ''}` : '';
  const comparison = state.source?.content ?? state.policies.find(p => p.id === state.selected?.sourcePolicyRevisionId)?.content ?? null;
  const draftBasis = state.context && [state.context.basis, state.context.latest].find(revision => revision.id === state.basis);
  const dirty = !!state.selected && !!state.content && (JSON.stringify(state.selected.content) !== JSON.stringify(state.content) || state.mode !== state.selected.mode || state.basis !== state.selected.baseCompanyVersionId);
  return <div className="min-w-0 space-y-6">
    <p className="text-sm">จัดการกติกาเท่านั้น · การเปลี่ยนแปลงต้องผ่านผู้ตรวจคนละคน · ยังไม่ใช่การขอหรือจ่ายเงินจริง</p>
    <div className="flex flex-wrap gap-3"><a className="underline" href={root}>เลือกขอบเขตการเงิน</a><a className="underline" href={`${root}/grants`}>มอบหมายสิทธิ์</a><a className="underline" href={`${root}/reviews?domain=grants`}>งานรอตรวจสิทธิ์</a><button className={button} onClick={() => void load()}>โหลดใหม่</button></div>
    {state.busy && <p aria-live="polite">กำลังโหลดหรือดำเนินการ…</p>}
    {state.error && <p role="alert" className="rounded-lg border border-red-400 p-3">{state.error}</p>}
    {state.message && <p role="status" className="rounded-lg border border-green-600 p-3">{state.message}</p>}
    {!state.choice && !state.busy && !state.error && <><h2 className="text-xl font-semibold">ขอบเขตที่ได้รับสิทธิ์</h2>{!state.choices.length && <p>ยังไม่มีสิทธิ์จัดการกติกาในขอบเขตใด</p>}<ul className="space-y-3">{state.choices.map(c => <li key={`${c.scope.workspaceId}/${c.scope.projectId}`}><a className="underline" href={`${root}/${c.scope.projectId ? `projects/${c.scope.projectId}` : 'company'}?workspaceId=${c.scope.workspaceId}`}>{c.workspaceName} · {c.projectName ?? 'กติกาบริษัท'}</a> · {c.capabilities.includes('policy.review') ? 'ตรวจฉบับได้' : 'แก้กติกาได้'}</li>)}</ul></>}
    {state.choice && <>
      <h2 className="text-xl font-semibold">{state.choice.workspaceName} · {state.choice.projectName ?? 'กติกาบริษัท'}</h2>
      <nav aria-label="เมนูกติกา" className="flex flex-wrap gap-4 underline"><a href={`${root}/${projectId ? `projects/${projectId}` : 'company'}${query}`}>ฉบับกติกา</a><a href={`${root}/categories${query}`}>หมวดและอัตรา</a>{canReview && <a href={`${root}/reviews${query}`}>งานรอตรวจ</a>}</nav>
      {state.effective ? <details><summary>กติกาที่มีผล · ฐานบริษัท {state.effective.companyVersionId}</summary><ContentSummary content={state.effective.content} /></details> : <p>ยังไม่มีฉบับเปิดใช้ที่ยืนยันได้ กรุณาจัดทำและอนุมัติกติกาก่อนใช้งาน</p>}
      {state.effective?.requiresReview && <p className="rounded-lg border border-amber-500 p-3">ต้องทบทวน · ยังคงใช้ฐานบริษัทเก่า {state.effective.companyVersionId} จนกว่าฉบับโครงการใหม่ผ่านอนุมัติ</p>}
      {state.context && <section className="space-y-3 rounded-xl border p-4"><h3 className="font-semibold">บริษัทฐานและฉบับล่าสุด</h3><details><summary>ฐานของฉบับที่อ่าน {state.context.basis.id}</summary><ContentSummary content={state.context.basis.content} /></details><details><summary>บริษัทล่าสุด {state.context.latest.id}</summary><ContentSummary content={state.context.latest.content} /></details><ul>{differences(state.context.basis.content, state.context.latest.content).map((d, i) => <li key={i}>{d}</li>)}</ul></section>}
      {canEdit && screen !== 'reviews' && <button className={button} disabled={state.busy || state.blocked || !!projectId && !state.context} onClick={start}>สร้างฉบับใหม่</button>}
      <section className="space-y-3"><h3 className="font-semibold">{screen === 'reviews' ? 'ฉบับรอตรวจ' : 'รายการฉบับ'}</h3>{!state.policies.length && <p>ยังไม่มีฉบับในรายการนี้</p>}
        {state.policies.map(row => <article className="flex flex-wrap items-center gap-3 rounded-lg border p-3" key={row.id}><div className="min-w-0 flex-1 break-all"><p>{statusLabel(row.state)} · รุ่นข้อมูล {row.version} · {sourceLabel(row.mode)}</p><p className="text-xs">ฉบับ {row.id}</p></div><button className={button} disabled={state.busy || state.blocked} onClick={() => void open(row)}>เปิดฉบับ</button></article>)}
      </section>
      {state.content && <section className="space-y-5 border-t pt-5">
        <h3 className="text-lg font-semibold">{state.selected ? `${statusLabel(state.selected.state)} · รุ่นข้อมูล ${state.selected.version}` : 'ฉบับใหม่'}</h3>
        <p className="break-all text-xs">{state.selected ? `ฉบับ ${state.selected.id} · hash ${state.selected.contentHash}` : 'ยังไม่บันทึก'} · {sourceLabel(state.mode)}</p>
        {projectId && <><label className="block">ที่มากติกา<select className={input} value={state.mode} disabled={!state.editing || !canEdit || state.busy || state.blocked} onChange={e => set(s => ({ ...s, mode: e.target.value as PolicyMode, ...(e.target.value === 'Inherit' && s.context ? { content: structuredClone(s.context.latest.content), basis: s.context.latest.id } : {}) }))}><option value="Override">กำหนดเฉพาะโครงการ</option><option value="Inherit">ใช้กติกาบริษัทล่าสุด</option></select></label><p className="break-all">ฉบับบริษัทฐาน: {state.basis}</p>{state.editing && canEdit && state.context && <button className={button} disabled={state.busy || state.blocked} onClick={() => set(s => ({ ...s, basis: s.context!.latest.id, content: s.mode === 'Inherit' ? structuredClone(s.context!.latest.content) : s.content }))}>รับฐานบริษัทล่าสุดในร่าง</button>}</>}
        <label className="block">วันเคลียร์ (วันปฏิทิน)<input className={input} type="number" min="1" step="1" value={state.content.clearingDays || ''} disabled={!editable} onChange={e => update({ ...state.content!, clearingDays: Number(e.target.value) })} /></label><p className="text-sm">เริ่มนับวันถัดจากวันจบงานเป็นวันที่ 1</p>
        <fieldset className="space-y-3"><legend className="font-semibold">ระดับวงเงิน (บาทและสตางค์)</legend>{state.content.bands.map((b, i) => <div key={i} className="grid min-w-0 gap-3 sm:grid-cols-2"><label>รหัสระดับ {i + 1}<input className={input} value={b.levelCode} disabled={!editable} maxLength={64} onChange={e => update({ ...state.content!, bands: state.content!.bands.map((v, n) => n === i ? { ...v, levelCode: e.target.value } : v) })} /></label><label>วงเงินไม่เกิน {i + 1}<input className={input} inputMode="decimal" value={b.upperInclusive} disabled={!editable} onChange={e => update({ ...state.content!, bands: state.content!.bands.map((v, n) => n === i ? { ...v, upperInclusive: e.target.value } : v) })} /></label></div>)}{editable && <div className="flex flex-wrap gap-3"><button className={button} onClick={() => update({ ...state.content!, bands: [...state.content!.bands, { order: state.content!.bands.length + 1, levelCode: '', upperInclusive: '' }] })}>เพิ่มระดับวงเงิน</button>{!!state.content.bands.length && <button className={button} onClick={() => update({ ...state.content!, bands: state.content!.bands.slice(0, -1) })}>นำระดับสุดท้ายออก</button>}</div>}</fieldset>
        <fieldset className="space-y-4"><legend className="font-semibold">หมวด อัตรา และหลักฐาน</legend><p className="text-sm">ปิดใช้แทนการลบหมวด ประวัติฉบับเดิมคงอยู่ อัตราที่แก้ในกติกาต้องผ่านอนุมัติ</p>{state.content.categories.map((c, i) => {
          const change = (patch: Partial<typeof c>) => update({ ...state.content!, categories: state.content!.categories.map((v, n) => n === i ? { ...v, ...patch } : v) });
          return <div key={c.id} className="grid min-w-0 gap-3 rounded-lg border p-3 sm:grid-cols-2"><label>ชื่อหมวด {i + 1}<input className={input} value={c.name} disabled={!editable} maxLength={200} onChange={e => change({ name: e.target.value })} /></label><label className="flex items-center gap-2"><input type="checkbox" checked={c.active} disabled={!editable} onChange={e => change({ active: e.target.checked })} />เปิดใช้หมวด {i + 1}</label><label>หน่วยหมวด {i + 1}<input className={input} value={c.unit ?? ''} disabled={!editable} maxLength={100} onChange={e => change({ unit: e.target.value || null })} /></label><label>อัตรามาตรฐานหมวด {i + 1}<input className={input} inputMode="decimal" value={c.standardRate ?? ''} disabled={!editable} placeholder="เว้นว่างหากไม่มีอัตรามาตรฐาน" onChange={e => change({ standardRate: e.target.value || null })} /></label><label className="sm:col-span-2">หลักฐานหมวด {i + 1}<input className={input} value={c.evidenceTypes.join(', ')} disabled={!editable} placeholder="คั่นประเภทหลักฐานด้วย ," onChange={e => change({ evidenceTypes: e.target.value.split(',').map(v => v.trim()) })} /></label></div>;
        })}{editable && <button className={button} onClick={() => update({ ...state.content!, categories: [...state.content!.categories, { id: crypto.randomUUID(), name: '', active: true, unit: null, standardRate: null, evidenceTypes: [] }] })}>เพิ่มหมวด</button>}</fieldset>
        <section className="space-y-2"><h3 className="font-semibold">เปรียบเทียบก่อน–หลัง</h3>{state.selected?.sourcePolicyRevisionId && <p className="break-all text-xs">ต้นทาง {state.selected.sourcePolicyRevisionId}</p>}<ul className="space-y-1">{differences(comparison, state.content).map((line, i) => <li key={i}>{line}</li>)}</ul>{comparison && !differences(comparison, state.content).length && <p>เนื้อหาไม่เปลี่ยนจากฉบับต้นทาง</p>}{state.context && <><h4>ความต่างจากบริษัทฐาน</h4>{draftBasis ? <ul>{differences(draftBasis.content, state.content).map((line, i) => <li key={i}>{line}</li>)}</ul> : <p>ไม่พบเนื้อหาฐานที่เลือก กรุณาโหลดใหม่ก่อนเปรียบเทียบ</p>}</>}</section>
        {(canEdit && state.editing || canReview && state.selected?.state === 'Pending') && <><label className="block">เหตุผล<textarea className={input} maxLength={1000} value={state.reason} disabled={state.busy || state.blocked} onChange={e => set(s => ({ ...s, reason: e.target.value }))} /></label><div className="flex flex-wrap gap-3">
          {canEdit && state.editing && <><button className={button} disabled={state.busy || state.blocked || !state.reason.trim()} onClick={() => command('save')}>บันทึกร่าง</button>{state.selected && <button className={button} disabled={state.busy || state.blocked || dirty || !state.reason.trim()} onClick={() => command('submit')}>ส่งตรวจ</button>}{dirty && <p>บันทึกการแก้ไขก่อนส่งตรวจ</p>}</>}
          {canReview && state.selected?.state === 'Pending' && <>{(['approve', 'return', 'reject'] as const).map((action, i) => <button key={action} className={button} disabled={state.busy || state.blocked || !state.reason.trim()} onClick={() => command(action)}>{['อนุมัติและเปิดใช้', 'ส่งกลับแก้ไข', 'ปฏิเสธ'][i]}</button>)}<p className="text-sm">ผู้ร่วมแก้ฉบับนี้ตรวจไม่ได้ แม้มีสิทธิ์ตรวจ ระบบตรวจซ้ำก่อนบันทึก</p></>}
        </div></>}
        <section><h3 className="font-semibold">ประวัติการตรวจฉบับ</h3>{!state.history.length && <p>ยังไม่มีผลตรวจฉบับนี้</p>}{state.history.map(h => <details key={h.id}><summary>{statusLabel(h.outcome)} · {new Intl.DateTimeFormat('th-TH', { dateStyle: 'medium', timeStyle: 'short', timeZone: 'Asia/Bangkok' }).format(new Date(h.reviewedAt))} (ประเทศไทย) · รุ่น {h.reviewedVersion ?? 'ไม่ทราบ'}</summary><p>เหตุผล: {h.reason}</p>{h.content ? <ContentSummary content={h.content} /> : <p>ไม่มีหลักฐานเนื้อหาที่ตรวจ</p>}</details>)}</section>
      </section>}
      <section className="space-y-3 border-t pt-5"><h3 className="font-semibold">ตัวอย่างประเมินวงเงินจากกติกาที่มีผล</h3><p className="text-sm">ไม่สร้างคำขอ และไม่รับรองว่ามีบุคคลพร้อมอนุมัติจริง</p><label className="block">ยอดตัวอย่าง (บาท)<input className={input} inputMode="decimal" value={state.total} onChange={e => set(s => ({ ...s, total: e.target.value, levels: [] }))} disabled={state.busy || state.blocked} /></label><button className={button} disabled={state.busy || state.blocked || !state.effective} onClick={() => void operate(async client => ({ levels: await client.preview(state.total) }))}>ประเมินวงเงิน</button>{!!state.levels.length && <p>ระดับที่ต้องผ่าน: {state.levels.join(' → ')}</p>}</section>
    </>}
  </div>;
}

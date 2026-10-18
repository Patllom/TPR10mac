'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation discards private request state. */
import { useCallback, useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { isSessionView } from '@/lib/auth/session-view';
import { bangkokDate, parseSitePage, thaiTime, type CaptureChoice } from '@/lib/attendance/capture-view';
import { parseAttendancePage } from '@/lib/attendance/history-view';
import { getCorrection, listCorrections, sendCorrection, retryCorrection, retryCreateCorrection, CorrectionConflictError } from '@/lib/attendance/correction-client';
import { correctionError, correctionId, parseCorrection, parseCorrectionAck, parseCorrectionPage, validateCorrectionBody } from '@/lib/attendance/correction-view';
import type { CorrectionAction, CorrectionAudience, CorrectionCommand, CorrectionPage, CorrectionView, CreateCorrection, ReviseCorrection, SaveCorrection } from '@/lib/attendance/correction-types';
import CorrectionForm from './CorrectionForm';
import { loadCorrectionSites } from '@/lib/attendance/correction-site-client';
const states = ['ร่าง', 'รอหัวหน้า', 'รอ HR', 'ส่งกลับแก้ไข', 'อนุมัติแล้ว', 'หมดอายุ'];
const basis = { Own: 'ของตนเอง', Supervisor: 'สายบังคับบัญชา', Hr: 'HR ที่ได้รับมอบหมาย', WorkflowSupervisor: 'ผู้อนุมัติขั้นหัวหน้าของคำร้องนี้', WorkflowHr: 'ผู้อนุมัติขั้น HR ของคำร้องนี้', WorkflowRouting: 'ผู้จัดเส้นทางคำร้องในหน่วยงานที่ได้รับมอบหมาย' };
const time = (v: string | null | undefined) => v ? thaiTime(v) : 'ไม่มีเวลา';
type Pending = { action: 'create' | CorrectionAction; id: string | null; body: CreateCorrection | ReviseCorrection | CorrectionCommand; actor: string };
type State = { loading: boolean; page?: CorrectionPage; detail?: CorrectionView; form?: SaveCorrection; choices?: CaptureChoice[]; message?: string; error?: string; uncertain?: boolean; generation: number };
class HttpFailure extends Error { constructor(readonly status: number) { super(correctionError(status)); } }
const options = { cache: 'no-store', credentials: 'same-origin', redirect: 'error' } as const;
async function json(response: Response): Promise<unknown> { if (!response.ok) throw new HttpFailure(response.status); if (!response.headers.get('content-type')?.includes('application/json')) throw new HttpFailure(503); return response.json(); }
async function account(signal: AbortSignal) { const value = await json(await fetch('/api/v1/auth/session', { ...options, signal })); if (!isSessionView(value) || value.stage !== 'Active') throw new HttpFailure(403); return value.userId; }
export default function CorrectionPortal({ audience = 'mine' }: { audience?: CorrectionAudience }) {
  const [state, setState] = useState<State>({ loading: true, generation: 0 });
  const [page, setPage] = useState(1);
  const epoch = useRef(0), controller = useRef<AbortController | null>(null), pending = useRef<Pending | null>(null), actor = useRef<string | null>(null), busy = useRef(false);
  const clear = useCallback(() => { epoch.current++; controller.current?.abort(); pending.current = null; busy.current = false; actor.current = null; setState({ loading: false, generation: epoch.current }); }, []);
  const run = useCallback(async (work: (signal: AbortSignal, user: string) => Promise<Partial<State>>, keepPending = false) => {
    if (document.hidden) return;
    const generation = ++epoch.current; controller.current?.abort(); controller.current = new AbortController();
    const signal = AbortSignal.any([controller.current.signal, AbortSignal.timeout(15000)]);
    setState({ loading: true, generation });
    try {
      const user = await account(signal);
      if (generation !== epoch.current || signal.aborted || document.hidden) return;
      if (actor.current && actor.current !== user) { clear(); window.location.reload(); return; }
      actor.current = user;
      const result = await work(signal, user);
      const after = await account(signal);
      if (generation !== epoch.current || signal.aborted || document.hidden) return;
      if (after !== user) { clear(); window.location.reload(); return; }
      setState({ loading: false, generation, ...result });
    } catch (error) {
      if (generation !== epoch.current || document.hidden) return;
      const status = error instanceof HttpFailure || error instanceof CorrectionConflictError ? error.status : 503;
      // Errors never restore a stale form or infer rollback. The same command remains only in memory.
      if (!keepPending || status === 400 || status === 409) pending.current = null;
      setState({ loading: false, generation, error: error instanceof HttpFailure ? error.message : correctionError(status), uncertain: !!pending.current });
      if (status === 401) window.location.replace('/login');
    } finally { if (generation === epoch.current) busy.current = false; }
  }, [clear]);
  const load = useCallback(() => { pending.current = null; void run(async signal => ({ page: parseCorrectionPage(await json(await listCorrections(audience, page, 20, signal))) })); }, [run, audience, page]);
  useEffect(() => {
    const hidden = () => { if (document.hidden) clear(); else load(); };
    const changed = () => { clear(); window.location.reload(); };
    const unsubscribe = subscribeAuthChanges(changed);
    window.addEventListener('focus', load); window.addEventListener('pagehide', clear); document.addEventListener('visibilitychange', hidden);
    load();
    return () => { clear(); unsubscribe(); window.removeEventListener('focus', load); window.removeEventListener('pagehide', clear); document.removeEventListener('visibilitychange', hidden); };
  }, [clear, load]);
  async function choices(signal: AbortSignal) {
    const all: CaptureChoice[] = [];
    for (let p = 1; ; p++) { const result = parseSitePage(await json(await fetch(`/api/v1/scopes?page=${p}&pageSize=100`, { ...options, signal })), p); all.push(...result.items); if (!result.more) return all; }
  }
  function open(id: string) { pending.current = null; void run(async signal => {
    const detail = parseCorrection(await json(await getCorrection(id, signal)));
    if (detail.id !== id || !detail.comparison || !detail.history) throw new HttpFailure(503);
    const editable = audience === 'mine' && detail.actions.includes('save');
    return { detail, choices: editable ? await choices(signal) : [], form: editable ? { dayId: detail.comparison.dayId, originalWorkDate: detail.originalWorkDate, expectedDayVersion: detail.comparison.current?.version ?? null, proposal: detail.proposal } : undefined };
  }); }
  async function historicalChoices(inUtc: string, outUtc: string | null) {
    const generation = epoch.current, user = actor.current;
    if (!user || !controller.current || document.hidden) throw new HttpFailure(403);
    const signal = AbortSignal.any([controller.current.signal, AbortSignal.timeout(15000)]);
    const before = await account(signal);
    if (before !== user) { clear(); window.location.reload(); throw new HttpFailure(403); }
    const result = await loadCorrectionSites(inUtc, outUtc, signal);
    const after = await account(signal);
    if (generation !== epoch.current || signal.aborted || document.hidden) throw new HttpFailure(403);
    if (after !== user) { clear(); window.location.reload(); throw new HttpFailure(403); }
    return result;
  }
  function create() { pending.current = null; void run(async (signal, user) => {
    const selectedDay = new URLSearchParams(window.location.search).get('day');
    let form: SaveCorrection = { dayId: null, originalWorkDate: '', expectedDayVersion: null, proposal: { workDate: bangkokDate(new Date().toISOString()), inUtc: '', outUtc: null, inSite: { workspaceId: '', projectId: '', siteId: '' }, outSite: null, reason: '' } };
    if (selectedDay) {
      if (!correctionId(selectedDay)) throw new HttpFailure(400);
      const raw = await json(await fetch(`/api/v1/attendance/days/${selectedDay}`, { ...options, signal }));
      const day = parseAttendancePage({ items: [raw], nextCursor: null }, 'Mine').items[0];
      if (day.id !== selectedDay || day.employeeId !== user) throw new HttpFailure(404);
      const input = day.effectiveInSite ?? day.in?.site, output = day.effectiveOutSite ?? day.out?.site;
      if (!input?.projectId || !input.siteId) throw new HttpFailure(503);
      form = { dayId: day.id, originalWorkDate: day.workDate, expectedDayVersion: day.version, proposal: { workDate: day.workDate, inUtc: day.effectiveInUtc, outUtc: day.effectiveOutUtc, inSite: { workspaceId: input.workspaceId, projectId: input.projectId, siteId: input.siteId }, outSite: output?.projectId && output.siteId ? { workspaceId: output.workspaceId, projectId: output.projectId, siteId: output.siteId } : null, reason: '' } };
    }
    return { form, choices: await choices(signal) };
  }); }
  function mutate(action: Pending['action'], input?: SaveCorrection, reason: string | null = null) {
    if (busy.current || state.loading || !actor.current) return;
    const detail = state.detail;
    if (action !== 'create' && (!detail || !detail.actions.includes(action))) return;
    const command = { commandId: crypto.randomUUID(), expectedVersion: detail?.version ?? 1, revision: detail?.currentRevision ?? 1, reason };
    const body = action === 'create' ? { commandId: command.commandId, input: input! } : action === 'save' ? { input: input!, command } : command;
    validateCorrectionBody(action, body);
    pending.current = { action, id: detail?.id ?? null, body, actor: actor.current };
    dispatch(false);
  }
  function dispatch(retry: boolean) {
    const value = pending.current; if (!value || busy.current) return; busy.current = true;
    void run(async (signal, user) => {
      if (user !== value.actor) { pending.current = null; throw new HttpFailure(403); }
      const fence = async () => { if (await account(signal) !== value.actor) { pending.current = null; throw new HttpFailure(403); } signal.throwIfAborted(); };
      const response = retry ? value.action === 'create' ? await retryCreateCorrection(value.body as CreateCorrection, signal, fence)
        : await retryCorrection(value.action, value.id!, value.body as CorrectionCommand | ReviseCorrection, signal, fence)
        : await sendCorrection(value.action, value.id, value.body, signal, fence);
      const ack = parseCorrectionAck(await json(response));
      if (ack.commandId !== validateCorrectionBody(value.action, value.body) || value.id && ack.id !== value.id) throw new HttpFailure(503);
      pending.current = null;
      return { message: 'ทำรายการสำเร็จ กลับรายการคำร้องแล้ว', page: parseCorrectionPage(await json(await listCorrections(audience, page, 20, signal))) };
    }, true);
  }
  return <div className="min-w-0 space-y-5">
    <nav aria-label="เมนูคำร้อง" className="flex flex-wrap gap-4 underline"><a href="/portal/attendance/corrections">คำร้องของฉัน</a><a href="/portal/attendance/approvals">คิวหัวหน้า / HR</a><a href="/portal/attendance#history">ประวัติลงเวลา</a></nav>
    <p>เวลาประเทศไทย · แก้ไขโดยคำร้องเท่านั้น ไม่สร้างรูปหรือ GPS ย้อนหลัง</p>
    <button type="button" className="underline" disabled={state.loading} onClick={load}>โหลดรายการใหม่</button>
    {audience === 'mine' && !state.loading && !state.form && !state.uncertain && <button className="auth-button" onClick={create}>สร้างคำร้อง</button>}
    {state.loading && <p role="status">กำลังตรวจสอบบัญชีและข้อมูล…</p>}
    {state.error && <p role="alert">{state.error}</p>}
    {state.message && <p role="status">{state.message}</p>}
    {state.uncertain && <div><p>ยังยืนยันผลไม่ได้ ตรวจรายการก่อนเริ่มเรื่องใหม่ การส่งซ้ำจะตรวจ GET ก่อนและใช้รหัสคำสั่งเดิม</p><button className="auth-button" onClick={() => dispatch(true)}>ตรวจสถานะและลองคำสั่งเดิม</button></div>}
    {state.page && <><ol className="space-y-3">{state.page.items.map(v => <li key={v.id} className="rounded-xl border p-4"><p>วันต้นเรื่อง {v.originalWorkDate} · {states[v.state]} · ฉบับ {v.currentRevision}</p>{audience !== 'mine' && <p className="break-all">รหัสพนักงาน: {v.employeeId}</p>}<button className="underline" onClick={() => open(v.id)}>เปิดคำร้อง</button></li>)}</ol>{state.page.total === 0 && <p>ไม่มีคำร้องในคิวนี้</p>}
      <div className="flex flex-wrap gap-4"><button disabled={page <= 1} onClick={() => setPage(x => x - 1)}>หน้าก่อนหน้า</button><span>หน้า {page}</span><button disabled={page * 20 >= state.page.total} onClick={() => setPage(x => x + 1)}>หน้าถัดไป</button></div></>}
    {state.detail && <CorrectionDetail key={`${state.generation}-${state.detail.id}`} detail={state.detail} act={mutate} />}
    {state.form && <CorrectionForm key={state.generation} initial={state.form} choices={state.choices ?? []} busy={state.loading} loadChoices={historicalChoices} save={input => mutate(state.detail ? 'save' : 'create', input)} />}
  </div>;
}
function CorrectionDetail({ detail: d, act }: { detail: CorrectionView; act: (action: CorrectionAction, input?: SaveCorrection, reason?: string | null) => void }) {
  const [reason, setReason] = useState(''); const c = d.comparison!;
  return <section aria-label="รายละเอียดคำร้อง" className="min-w-0 space-y-4 break-words">
    <h2 className="text-xl font-bold">รายละเอียดคำร้อง · ฉบับ {d.currentRevision}</h2><p>{states[d.state]}</p><p className="break-all">รหัสพนักงาน: {d.employeeId}</p><p className="break-all">รหัสคำร้อง: {d.id}</p><p>สิทธิ์: {basis[d.readBasis]}</p>
    <p>กำหนดยื่น/ส่งใหม่ก่อน {time(d.deadlineUtc)} (รออนุมัติไม่หมดอายุตามกำหนดยื่น)</p>
    <div className="grid min-w-0 gap-4 md:grid-cols-2">
      <div><h3 className="font-bold">หลักฐานเวลาเดิม</h3>{c.original ? <p>เข้า {time(c.original.inUtc)} · ออก {time(c.original.outUtc)}</p> : <p>ไม่มีหลักฐานถ่ายสดในรายการนี้</p>}</div>
      <div><h3 className="font-bold">เวลาที่มีผลปัจจุบัน</h3>{c.current ? <><p>เข้า {time(c.current.inUtc)} · ออก {time(c.current.outUtc)}</p><p>รุ่นข้อมูล {c.current.version} · วัน {c.current.workDate}</p><p className="break-all">สถานที่เข้า {c.current.inSite?.siteId ?? 'ไม่มี'} · ออก {c.current.outSite?.siteId ?? 'ไม่มี'}</p></> : <p>ยังไม่มีคู่เวลาลงงาน</p>}</div>
    </div>
    {!c.baselineMatches && d.state !== 4 && <p role="alert">ข้อมูลลงเวลาเปลี่ยนจากรุ่นที่ยื่นแล้ว ต้องทบทวนและส่งกลับ ไม่ใช้เวลาปัจจุบันแทนค่าก่อนยื่น</p>}
    <div><h3 className="font-bold">เวลาที่เสนอ</h3><p>เข้า {time(d.proposal.inUtc)} · ออก {time(d.proposal.outUtc)}</p><p className="break-all">สถานที่เข้า {d.proposal.inSite.siteId} · ออก {d.proposal.outSite?.siteId ?? 'ไม่มี'}</p><p>{d.proposal.reason}</p></div>
    {d.state === 4 && <p>เวลาที่อนุมัติแก้ไข · ดูเวลาที่มีผลล่าสุดในประวัติลงเวลา</p>}
    <div className="flex flex-wrap gap-4">
      {d.actions.includes('submit') && <button className="auth-button" onClick={() => act('submit')}>ส่งให้หัวหน้า</button>}
      {d.actions.includes('supervisor-approve') && <button className="auth-button" disabled={!c.baselineMatches} onClick={() => act('supervisor-approve')}>หัวหน้าอนุมัติ</button>}
      {d.actions.includes('hr-approve') && <button className="auth-button" disabled={!c.baselineMatches} onClick={() => act('hr-approve')}>HR อนุมัติ</button>}
    </div>
    {(d.actions.includes('return') || d.actions.includes('reroute')) && <div className="space-y-3"><label className="block">เหตุผลส่งกลับหรือจัดเส้นทาง<textarea maxLength={2000} className="auth-input w-full" value={reason} onChange={e => setReason(e.target.value)} /></label>{d.actions.includes('return') && <button className="auth-button" disabled={!reason.trim()} onClick={() => act('return', undefined, reason.trim())}>ส่งกลับแก้ไข</button>}{d.actions.includes('reroute') && <button className="auth-button" disabled={!reason.trim()} onClick={() => act('reroute', undefined, reason.trim())}>จัดเส้นทางใหม่</button>}</div>}
    <details><summary>ประวัติฉบับและการพิจารณา</summary><ul className="space-y-3">{d.history?.revisions.map(r => <li key={r.revision}>ฉบับ {r.revision} · {time(r.createdAtUtc)} · เข้า {time(r.proposal.inUtc)} ออก {time(r.proposal.outUtc)} · {r.proposal.reason}</li>)}{d.history?.approvals.map((a, i) => <li key={`a${i}`}>{a.step === 'Supervisor' ? 'หัวหน้า' : 'HR'} {a.outcome === 'Approved' ? 'อนุมัติ' : 'ส่งกลับ'} · ฉบับ {a.revision} · {time(a.occurredAtUtc)} · {a.reason}</li>)}{d.history?.reroutes.map((r, i) => <li key={`r${i}`}>จัดเส้นทางฉบับ {r.revision} · {time(r.occurredAtUtc)} · {r.reason}</li>)}</ul></details>
  </section>;
}

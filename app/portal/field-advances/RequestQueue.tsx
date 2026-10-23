'use client';
/* eslint-disable @next/next/no-html-link-for-pages -- Full navigation clears account-bound request state. */
import { useEffect, useState } from 'react';
import { createRequestClient, RequestError } from '@/lib/field-advances/request-client';
import { requestQueueMode, requestStateLabel } from '@/lib/field-advances/request-view';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import type { RequestMode, RequestScopeChoice, RequestView } from '@/lib/field-advances/request-types';

export default function RequestQueue({ userId, review = false }: { userId: string; review?: boolean }) {
  const [scopes, setScopes] = useState<RequestScopeChoice[]>([]), [selected, setSelected] = useState('');
  const [scopePage, setScopePage] = useState(1), [page, setPage] = useState(1), [mode, setMode] = useState<RequestMode>(review ? 'review' : 'mine');
  const [hasNextScopePage, setHasNextScopePage] = useState(false);
  const [rows, setRows] = useState<RequestView[]>([]), [error, setError] = useState(''), [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const abort = new AbortController(), client = createRequestClient(userId); let active = true, loading = false;
    const clear = () => { active = false; abort.abort(); setScopes([]); setRows([]); setSelected(''); setHasNextScopePage(false); setError('ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่'); };
    const load = async () => {
      if (!active || loading) return; loading = true;
      try {
        const rawChoices = await client.scopes(scopePage, 20, abort.signal);
        const choices = rawChoices.filter(choice => requestQueueMode(choice.capabilities, review, mode) !== null);
        const chosen = choices.find(x => x.scope.projectId === selected) ?? (!selected ? choices[0] : undefined);
        if (selected && !chosen) throw new RequestError(403);
        const selectedMode = chosen ? requestQueueMode(chosen.capabilities, review, mode) : null;
        const items = chosen && selectedMode ? await client.list(chosen.scope, selectedMode, page, 20, abort.signal) : [];
        if (!active) return; setScopes(choices); setRows(items); setHasNextScopePage(rawChoices.length === 20); setError('');
      } catch (e) {
        if (!active) return; setRows([]); setScopes([]); setHasNextScopePage(false); setError(e instanceof Error ? e.message : 'โหลดไม่สำเร็จ');
        if (e instanceof RequestError && e.clearProtectedData) { active = false; abort.abort(); }
      } finally { loading = false; }
    };
    void load(); const timer = setInterval(() => void load(), 15000), unsubscribe = subscribeAuthChanges(clear);
    return () => { active = false; abort.abort(); clearInterval(timer); unsubscribe(); };
  }, [userId, review, selected, scopePage, page, mode, refresh]);
  const scope = scopes.find(x => x.scope.projectId === selected) ?? scopes[0];
  const displayedMode = scope ? requestQueueMode(scope.capabilities, review, mode) : null;
  return <div className="space-y-5">
    <nav className="flex flex-wrap gap-4 underline"><a href="/portal/field-advances">คำขอของฉัน / ที่ยื่นแทน</a><a href="/portal/field-advances/approvals">งานรออนุมัติ</a></nav>
    {error && <p role="alert">{error}</p>}<button className="rounded border px-4 py-2" onClick={() => setRefresh(x => x + 1)}>โหลดใหม่</button>
    {scopes.length > 0 && <><label className="block">โครงการ<select className="block w-full rounded border p-2" value={scope?.scope.projectId ?? ''} onChange={e => { setSelected(e.target.value); setRows([]); setPage(1); }}>{scopes.map(s => <option key={s.scope.projectId} value={s.scope.projectId!}>{s.workspaceName} · {s.projectName}</option>)}</select></label>
      {!review && <label className="block">รายการ<select className="block rounded border p-2" value={displayedMode ?? ''} onChange={e => { setMode(e.target.value as RequestMode); setRows([]); setPage(1); }}><option value="mine" disabled={!scope?.capabilities.includes('request.create')}>คำขอของฉัน</option><option value="delegated" disabled={!scope?.capabilities.includes('request.delegate')}>ที่ยื่นแทน</option>{scope?.capabilities.includes('report.read') && <option value="report">รายการตามสิทธิ์รายงาน</option>}</select></label>}
      {scope?.capabilities.some(c => c === 'request.create' || c === 'request.delegate') && <a className="inline-block rounded bg-orange-700 px-4 py-2 text-white" href={`/portal/field-advances/new?workspaceId=${scope.scope.workspaceId}&projectId=${scope.scope.projectId}`}>สร้างคำขอ</a>}
    </>}
    <div className="flex gap-3"><button disabled={scopePage === 1} onClick={() => { setScopePage(x => x - 1); setSelected(''); setRows([]); setPage(1); }}>โครงการหน้าก่อน</button><button disabled={!hasNextScopePage} onClick={() => { setScopePage(x => x + 1); setSelected(''); setRows([]); setPage(1); }}>โครงการหน้าถัดไป</button></div>
    <ul className="space-y-4">{rows.map(row => <li key={row.id} className="rounded-xl border p-4"><a className="underline" href={`/portal/field-advances/${row.id}`}>{row.content.purpose}</a><p>{requestStateLabel(row.state)} · {row.estimate?.total ?? 'รอคำนวณ'} บาท</p><p>ต้องการเงิน {row.content.neededOn}</p>{row.currentStep && <p>ขั้นปัจจุบัน: {row.currentStep.levelCode}</p>}</li>)}</ul>
    {!rows.length && !error && <p>ไม่มีรายการในหน้านี้</p>}
    <div className="flex gap-4"><button disabled={page === 1} onClick={() => { setRows([]); setPage(x => x - 1); }}>หน้าก่อน</button><span>หน้า {page}</span><button disabled={rows.length < 20} onClick={() => { setRows([]); setPage(x => x + 1); }}>หน้าถัดไป</button></div>
  </div>;
}

 'use client';
import { useEffect, useState } from 'react';
import { createPaymentClient, PaymentError } from '@/lib/field-advances/payment-client';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import type { PaymentScopeChoice, PaymentSummary } from '@/lib/field-advances/payment-types';
import PaymentDetails from './PaymentDetails';

export default function PaymentQueue({ userId }: { userId: string }) {
  const [requestId, setRequestId] = useState<string | null>(null);
  const [scopes, setScopes] = useState<PaymentScopeChoice[]>([]), [rows, setRows] = useState<PaymentSummary[]>([]);
  const [selected, setSelected] = useState(''), [scopePage, setScopePage] = useState(1), [page, setPage] = useState(1);
  const [error, setError] = useState(''), [refresh, setRefresh] = useState(0);
  useEffect(() => { setRequestId(new URLSearchParams(window.location.search).get('requestId')); }, []);
  useEffect(() => {
    if (requestId) return;
    const abort = new AbortController(), client = createPaymentClient(userId); let active = true, loading = false;
    const clear = () => { active = false; abort.abort(); setScopes([]); setRows([]); setError('ไม่มีสิทธิ์ หรือบัญชีเปลี่ยน กรุณาโหลดใหม่'); };
    const load = async () => {
      if (!active || loading) return; loading = true;
      try {
        const choices = await client.paymentScopes(scopePage, 25, abort.signal);
        const chosen = choices.find(x => x.scope.projectId === selected) ?? (!selected ? choices[0] : undefined);
        if (selected && !chosen) throw new PaymentError(403);
        const items = chosen ? await client.queue(chosen.scope, (page - 1) * 25, 25, abort.signal) : [];
        if (active) { setScopes(choices); setRows(items); setError(''); }
      } catch (e) { if (active) { setRows([]); setScopes([]); setError(e instanceof Error ? e.message : 'โหลดไม่สำเร็จ'); if (e instanceof PaymentError && e.clearProtectedData) clear(); } }
      finally { loading = false; }
    };
    void load(); const timer = setInterval(() => void load(), 15000), unsubscribe = subscribeAuthChanges(clear);
    const focus = () => { setRows([]); setScopes([]); void load(); }; window.addEventListener('focus', focus);
    return () => { active = false; abort.abort(); clearInterval(timer); unsubscribe(); window.removeEventListener('focus', focus); };
  }, [userId, selected, scopePage, page, refresh, requestId]);
  if (requestId) return <PaymentDetails key={requestId} userId={userId} requestId={requestId} />;
  return <div className="space-y-4">{error && <p role="alert">{error}</p>}
    <button onClick={() => setRefresh(x => x + 1)}>โหลดใหม่</button>
    <label className="block">โครงการ<select className="block w-full border p-2" value={selected || scopes[0]?.scope.projectId || ''} onChange={e => { setSelected(e.target.value); setRows([]); setPage(1); }}>{scopes.map(s => <option key={s.scope.projectId} value={s.scope.projectId!}>{s.workspaceName} · {s.projectName}</option>)}</select></label>
    <div className="flex gap-4"><button disabled={scopePage === 1} onClick={() => { setScopePage(x => x - 1); setSelected(''); setRows([]); }}>โครงการหน้าก่อน</button><button disabled={scopes.length < 25} onClick={() => { setScopePage(x => x + 1); setSelected(''); setRows([]); }}>โครงการหน้าถัดไป</button></div>
    <ul className="space-y-3">{rows.map(r => <li key={r.requestId} className="rounded border p-3"><button className="break-all text-left underline" onClick={() => setRequestId(r.requestId)}>เปิดงานจ่าย {r.requestId}</button><p>อนุมัติ {r.approved} · กันไว้ {r.reserved} · จ่ายแล้ว {r.paid} · พร้อมจัดงวด {r.available} บาท</p>{r.holdCodes.length > 0 && <p>เหตุระงับ: {r.holdCodes.join(', ')}</p>}</li>)}</ul>
    {!rows.length && !error && <p>ไม่มีงานจ่ายในหน้านี้</p>}
    <div className="flex gap-4"><button disabled={page === 1} onClick={() => { setRows([]); setPage(x => x - 1); }}>หน้าก่อน</button><button disabled={rows.length < 25} onClick={() => { setRows([]); setPage(x => x + 1); }}>หน้าถัดไป</button></div>
  </div>;
}

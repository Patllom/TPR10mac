'use client';
import { reviewStateLabel } from '@/lib/field-advances/ui-labels';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { createPaymentClient } from '@/lib/field-advances/payment-client';
import { createRequestClient } from '@/lib/field-advances/request-client';
import { createSettlementWorkClient, type SettlementWorkItem, type SettlementWorkScope } from '@/lib/field-advances/settlement-work-client';
import { currentAccount } from '@/lib/field-advances/policy-client';
import { createSettlementClient, SettlementError } from '@/lib/field-advances/settlement-client';
import { pendingSettlements, type PendingSettlement } from '@/lib/field-advances/settlement-view';
import { discoverSettlementScopes } from '@/lib/field-advances/settlement-scope-discovery';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import SettlementReviewDetails from './SettlementReviewDetails';

type ScopeChoice = { scope: SettlementWorkScope; label: string };
function accessLost(cause: unknown): cause is Error & { status: 401 | 403 } {
  if (!(cause instanceof Error) || !('status' in cause)) return false;
  return cause.status === 401 || cause.status === 403;
}
const laneLabels: Record<string, string> = {
  rounds: 'รอบค่าใช้จ่ายที่มอบหมาย',
  refunds: 'ตรวจเงินคืน',
  incidents: 'ตรวจข้อเท็จจริงจ่ายผิด',
  recoveries: 'ตรวจเงินเรียกคืน',
  corrections: 'ตรวจแก้ยอดคืน',
  'case-payments': 'ตรวจผลเคสหลังปิด',
  'excess-payments': 'ตรวจผลส่วนเกิน',
  completion: 'ตรวจแก้วันจบงาน',
  duplicates: 'ตรวจเอกสารซ้ำ',
  excess: 'พิจารณาส่วนเกินใบเดียว'
};

export default function SettlementReviewQueue({ userId }: { userId: string }) {
  const client = useMemo(() => createSettlementWorkClient(userId), [userId]);
  const receipts = useMemo(() => createSettlementClient(userId), [userId]);
  const [scopes, setScopes] = useState<ScopeChoice[]>([]);
  const [scopePages, setScopePages] = useState(1);
  const [moreScopes, setMoreScopes] = useState(false);
  const [pending, setPending] = useState<PendingSettlement[]>([]);
  const [selectedScope, setSelectedScope] = useState('');
  const [lane, setLane] = useState('rounds');
  const [rows, setRows] = useState<SettlementWorkItem[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [selected, setSelected] = useState<SettlementWorkItem | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const generation = useRef(0);
  const invalidate = useCallback(() => { generation.current += 1; }, []);

  const clear = useCallback((message = 'ไม่มีสิทธิ์หรือบัญชีเปลี่ยน กรุณาโหลดใหม่') => {
    generation.current++;
    setRows([]);
    setSelected(null);
    setScopes([]);
    setSelectedScope('');
    setMoreScopes(false);
    setPending([]);
    pendingSettlements.clear();
    setCursor(null);
    setBusy(false);
    setError(message);
  }, []);

  const loadScopes = useCallback(async (signal?: AbortSignal) => {
    const requestClient = createRequestClient(userId);
    const paymentClient = createPaymentClient(userId);
    return discoverSettlementScopes(userId, scopePages,
      (page, size) => requestClient.scopes(page, size, signal),
      (page, size) => paymentClient.paymentScopes(page, size, signal),
      () => currentAccount(fetch, signal));
  }, [userId, scopePages]);

  const refresh = useCallback(async (nextCursor?: string) => {
    const ticket = ++generation.current;
    setBusy(true);
    setError('');
    try {
      const discovered = await loadScopes();
      const choices = discovered.choices;
      const choice = choices.find(value => `${value.scope.workspaceId}:${value.scope.projectId}` === selectedScope) ?? choices[0];
      const page = choice ? await client.queue(choice.scope, lane, 25, nextCursor) : { items: [], nextCursor: null };
      if (await currentAccount() !== userId) throw new SettlementError(403);
      const unresolved = pendingSettlements.list(userId);
      if (ticket !== generation.current) return;
      setScopes(choices);
      setMoreScopes(discovered.hasMore);
      setPending(unresolved);
      setSelectedScope(choice ? `${choice.scope.workspaceId}:${choice.scope.projectId}` : '');
      setRows(page.items);
      setCursor(page.nextCursor);
      setSelected(null);
    } catch (cause) {
      if (ticket !== generation.current) return;
      if (accessLost(cause)) {
        clear(cause.message);
        return;
      }
      setRows([]);
      setSelected(null);
      setScopes([]);
      setSelectedScope('');
      setPending([]);
      setCursor(null);
      setError(cause instanceof Error ? cause.message : 'โหลดงานไม่สำเร็จ');
    } finally {
      if (ticket === generation.current) setBusy(false);
    }
  }, [client, lane, loadScopes, selectedScope, userId, clear]);

  const checkPending = useCallback(async (entry: PendingSettlement) => {
    setBusy(true);
    setError('');
    try {
      if (await currentAccount() !== userId) throw new SettlementError(403);
      await receipts.checkSettlementReceipt(entry.requestId, entry.action, entry.entityId,
        entry.command.commandId);
      pendingSettlements.resolve(userId, entry.requestId, entry.command.commandId);
      setPending(pendingSettlements.list(userId));
      await refresh();
    } catch (cause) {
      if (accessLost(cause)) clear(cause.message);
      else setError(cause instanceof Error ? cause.message : 'ยังยืนยันผลไม่ได้');
    } finally {
      setBusy(false);
    }
  }, [userId, receipts, refresh, clear]);

  useEffect(() => {
    void refresh();
    const unsubscribe = subscribeAuthChanges(clear);
    const focus = () => { setRows([]); setSelected(null); void refresh(); };
    const visibility = () => {
      if (document.hidden) {
        generation.current++;
        setRows([]);
        setSelected(null);
        setCursor(null);
      } else {
        void refresh();
      }
    };
    window.addEventListener('focus', focus);
    document.addEventListener('visibilitychange', visibility);
    return () => {
      invalidate();
      unsubscribe();
      window.removeEventListener('focus', focus);
      document.removeEventListener('visibilitychange', visibility);
    };
  }, [refresh, clear, invalidate]);

  return <div className="space-y-5 min-w-0">
    <p>แสดงเฉพาะงานที่เซิร์ฟเวอร์ตรวจสิทธิ์ให้ในโครงการและรายการปัจจุบัน</p>
    {error && <p role="alert" className="rounded border border-red-500 p-3">{error}</p>}
    <button className="rounded border px-3 py-2" disabled={busy} onClick={() => void refresh()}>โหลดงานล่าสุด</button>
    {pending.length > 0 && <section className="space-y-2 rounded border border-amber-600 p-3">
      <h2 className="font-semibold">คำสั่งที่ยังไม่ทราบผล</h2>
      <p>ตรวจผลด้วยรหัสคำสั่งเดิมก่อนทำรายการใหม่ ระบบไม่ส่งคำสั่งซ้ำอัตโนมัติ</p>
      {pending.map(entry => <div key={`${entry.requestId}:${entry.command.commandId}`} className="break-all">
        <p>คำขอ {entry.requestId} · รหัสคำสั่ง {entry.command.commandId}</p>
        <button className="underline" disabled={busy} onClick={() => void checkPending(entry)}>
          ตรวจผลจากเซิร์ฟเวอร์
        </button>
      </div>)}
    </section>}
    <label className="block">โครงการ
      <select className="block w-full rounded border p-2" value={selectedScope} disabled={busy} onChange={event => {
        setSelectedScope(event.target.value);
        setRows([]);
        setCursor(null);
      }}>
        {scopes.map(value => <option key={`${value.scope.workspaceId}:${value.scope.projectId}`}
          value={`${value.scope.workspaceId}:${value.scope.projectId}`}>{value.label}</option>)}
      </select>
    </label>
    {moreScopes && <button className="rounded border px-3 py-2" disabled={busy}
      onClick={() => setScopePages(value => value + 1)}>โหลดโครงการเพิ่ม</button>}
    <label className="block">ประเภทงาน
      <select className="block w-full rounded border p-2" value={lane} disabled={busy} onChange={event => {
        setLane(event.target.value);
        setRows([]);
        setCursor(null);
      }}>
        {Object.entries(laneLabels).map(([key, label]) => <option key={key} value={key}>{label}</option>)}
      </select>
    </label>
    {selected ? <SettlementReviewDetails key={`${selected.requestId}:${selected.entityId}:${selected.entityVersion}`}
      userId={userId} item={selected} onBack={() => setSelected(null)} onDone={() => void refresh()}
      onAccessDenied={clear} /> : <>
      <ul className="space-y-3">{rows.map(row => <li key={row.entityId} className="rounded border p-4">
        <button className="break-all text-left underline" onClick={() => setSelected(row)}>เปิดงาน {row.requestId}</button>
        <p>{laneLabels[lane]} · {reviewStateLabel(row.state)}</p>
      </li>)}</ul>
      {rows.length === 0 && !error && <p>ไม่มีงานที่ได้รับมอบหมายในหน้านี้</p>}
      {cursor && <button className="rounded border px-3 py-2" disabled={busy} onClick={() => void refresh(cursor)}>หน้าถัดไป</button>}
    </>}
  </div>;
}

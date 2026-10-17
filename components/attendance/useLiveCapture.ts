'use client';
import { useCallback, useEffect, useRef, useState } from 'react';
import { subscribeAuthChanges } from '@/lib/auth/auth-change';
import { isSessionView } from '@/lib/auth/session-view';
import { issueCaptureChallenge, submitCapture, getCaptureOperation, recordingError, recordingResponseError } from '@/lib/attendance/recording-client';
import { bangkokDate, gpsSample, parseChallenge, parseCurrent, parseOperation, parseReceipt, parseSitePage, type CaptureChoice, type CaptureCurrent } from '@/lib/attendance/capture-view';
import type { CaptureAction, CaptureMetadata, CaptureReceipt, CaptureSite, GpsSample } from '@/lib/attendance/recording-types';

type Phase = 'idle' | 'permissions' | 'ready' | 'captured' | 'submitting' | 'success' | 'uncertain' | 'error';
type View = { phase: Phase; error: string; previewUrl: string | null; gps: GpsSample | null; current: CaptureCurrent | null; choices: CaptureChoice[]; loading: boolean; receipt: CaptureReceipt | null; serverTime: string | null };
type Draft = { action: CaptureAction; key: string; site: CaptureSite; challengeId: string; deadline: number; settledAfter: number; blob?: Blob; capturedAt?: string };
const initial: View = { phase: 'idle', error: '', previewUrl: null, gps: null, current: null, choices: [], loading: true, receipt: null, serverTime: null };
const options = { cache: 'no-store', credentials: 'same-origin', redirect: 'error' } as const;
const offline = () => { if (!navigator.onLine) throw new Error('ไม่มีอินเทอร์เน็ต ระบบไม่เก็บคิว กรุณาเชื่อมต่อแล้วถ่ายใหม่'); };

function position(signal: AbortSignal): Promise<GpsSample> {
  return new Promise((resolve, reject) => {
    if (!navigator.geolocation) { reject(new Error('อุปกรณ์ไม่รองรับ GPS')); return; }
    const finish = (error?: Error, value?: GpsSample) => { clearTimeout(timer); signal.removeEventListener('abort', abort); if (error) reject(error); else resolve(value!); };
    const abort = () => finish(new Error('ยกเลิกการหาพิกัด'));
    const timer = setTimeout(() => finish(new Error('GPS หมดเวลารอ กรุณาลองใหม่ในบริเวณที่รับสัญญาณได้')), 12000);
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) { abort(); return; }
    navigator.geolocation.getCurrentPosition(value => {
      try { signal.throwIfAborted(); finish(undefined, gpsSample(value, Date.now())); } catch { finish(new Error('GPS เก่าหรือไม่ถูกต้อง กรุณาเปิดเวลาอัตโนมัติและหาพิกัดใหม่')); }
    }, () => finish(new Error('ไม่สามารถรับ GPS ได้ กรุณาอนุญาตตำแหน่งและลองใหม่')), { enableHighAccuracy: true, maximumAge: 0, timeout: 10000 });
  });
}

export function useLiveCapture() {
  const [view, setView] = useState<View>(initial);
  const state = useRef(view);
  const video = useRef<HTMLVideoElement>(null);
  const stream = useRef<MediaStream | null>(null);
  const draft = useRef<Draft | null>(null);
  const pending = useRef<{ key: string; action: CaptureAction; settledAfter: number } | null>(null);
  const actor = useRef<string | null>(null);
  const epoch = useRef(0);
  const controller = useRef<AbortController | null>(null);
  const checking = useRef(false);
  const serverAnchor = useRef<{ utc: number; monotonic: number } | null>(null);
  const serverNow = () => serverAnchor.current ? new Date(serverAnchor.current.utc + performance.now() - serverAnchor.current.monotonic).toISOString() : state.current.serverTime!;
  const update = useCallback((value: Partial<View>) => { state.current = { ...state.current, ...value }; setView(state.current); }, []);
  const release = useCallback(() => {
    stream.current?.getTracks().forEach(track => { track.onmute = null; track.onended = null; track.stop(); }); stream.current = null;
    if (video.current) video.current.srcObject = null;
    if (state.current.previewUrl) URL.revokeObjectURL(state.current.previewUrl);
    draft.current = null;
  }, []);
  const cancel = useCallback(() => { epoch.current++; controller.current?.abort(); release(); }, [release]);
  const stop = useCallback(() => {
    cancel(); checking.current = false;
    update({ phase: pending.current ? 'uncertain' : 'idle', previewUrl: null, gps: null, receipt: null, error: '' });
  }, [cancel, update]);
  const failure = useCallback((error: unknown) => {
    release();
    update({ phase: pending.current ? 'uncertain' : 'error', previewUrl: null, gps: null, error: error instanceof Error ? error.message : 'ทำรายการไม่สำเร็จ กรุณาถ่ายใหม่' });
  }, [release, update]);
  const sameAccount = useCallback(async (generation: number, signal: AbortSignal) => {
    try {
      const response = await fetch('/api/v1/auth/session', { ...options, signal: AbortSignal.any([signal, AbortSignal.timeout(10000)]) });
      const session: unknown = response.ok ? await response.json() : null;
      if (generation !== epoch.current) return false;
      if (!isSessionView(session) || session.stage !== 'Active') throw new Error('ตรวจสอบบัญชีไม่ได้ กรุณาโหลดข้อมูลใหม่');
      if (session.userId !== actor.current) { pending.current = null; throw new Error('บัญชีเปลี่ยนแปลง กรุณาโหลดข้อมูลใหม่'); }
      return true;
    } catch (error) {
      if (generation === epoch.current) { cancel(); update({ ...initial, loading: false, phase: 'error', error: error instanceof Error ? error.message : 'ตรวจสอบบัญชีไม่ได้ กรุณาโหลดข้อมูลใหม่' }); }
      return false;
    }
  }, [cancel, update]);
  const load = useCallback(async () => {
    stop(); const generation = epoch.current; const c = new AbortController(); controller.current = c;
    update({ loading: true, current: null, choices: [], serverTime: null });
    try {
      offline();
      const get = async (path: string) => {
        const response = await fetch(path, { ...options, signal: AbortSignal.any([c.signal, AbortSignal.timeout(10000)]) });
        if (!response.ok) throw new Error(recordingError(response.status));
        return response.json() as Promise<unknown>;
      };
      const session = await get('/api/v1/auth/session');
      if (generation !== epoch.current) return;
      if (!isSessionView(session) || session.stage !== 'Active') throw new Error('กรุณาเข้าสู่ระบบและยืนยันตัวตนก่อนลงเวลา');
      if (actor.current && actor.current !== session.userId) pending.current = null;
      actor.current = session.userId;
      const current = parseCurrent(await get('/api/v1/attendance/current'));
      const choices: CaptureChoice[] = [];
      for (let page = 1; ; page++) {
        c.signal.throwIfAborted();
        const result = parseSitePage(await get(`/api/v1/scopes?page=${page}&pageSize=100`), page);
        choices.push(...result.items);
        if (!result.more) break;
      }
      if (generation !== epoch.current || document.hidden) return;
      if (!await sameAccount(generation, c.signal)) return;
      serverAnchor.current = { utc: Date.parse(current.serverNowUtc), monotonic: performance.now() };
      update({ current, choices, loading: false, serverTime: current.serverNowUtc, phase: pending.current ? 'uncertain' : 'idle' });
    } catch (error) { if (generation === epoch.current) { failure(error); update({ loading: false }); } }
  }, [failure, sameAccount, stop, update]);

  useEffect(() => {
    const hidden = () => { if (document.hidden) { stop(); update({ current: null, choices: [], serverTime: null, loading: true }); } else void load(); };
    const auth = () => { pending.current = null; actor.current = null; stop(); update({ ...initial, loading: false, error: 'บัญชีเปลี่ยนแปลง กรุณาโหลดหน้าใหม่' }); };
    const pagehide = () => { stop(); update({ current: null, choices: [], serverTime: null }); };
    const unsubscribe = subscribeAuthChanges(auth);
    document.addEventListener('visibilitychange', hidden); window.addEventListener('pagehide', pagehide);
    void load();
    const expiration = setInterval(() => {
      if (draft.current && ['ready', 'captured'].includes(state.current.phase) && performance.now() >= draft.current.deadline) failure(new Error('การถ่ายภาพหมดอายุ กรุณาถ่ายใหม่'));
      if (draft.current && serverAnchor.current) update({ serverTime: new Date(serverAnchor.current.utc + performance.now() - serverAnchor.current.monotonic).toISOString() });
    }, 500);
    return () => { cancel(); clearInterval(expiration); unsubscribe(); document.removeEventListener('visibilitychange', hidden); window.removeEventListener('pagehide', pagehide); };
  }, [cancel, failure, load, stop, update]);

  async function start(action: CaptureAction, site: CaptureSite) {
    if (pending.current || state.current.loading || !state.current.current || !['idle', 'error'].includes(state.current.phase)) return;
    if (!state.current.choices.some(x => x.site.siteId === site.siteId && x.site.projectId === site.projectId && x.site.workspaceId === site.workspaceId)) return;
    const current = state.current.current;
    if (action !== (current.openDay ? 'CheckOut' : 'CheckIn') || (!current.openDay && current.todayCompleted)) return;
    stop(); const generation = epoch.current; const c = new AbortController(); controller.current = c;
    update({ phase: 'permissions', error: '' });
    try {
      offline();
      if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) throw new Error('กล้องต้องใช้ HTTPS และเบราว์เซอร์ที่รองรับ');
      const key = crypto.randomUUID(); const requested = performance.now();
      const response = await issueCaptureChallenge({ action, site, idempotencyKey: key }, c.signal);
      if (!response.ok) throw new Error(await recordingResponseError(response));
      const challenge = parseChallenge(await response.json());
      if (generation !== epoch.current) return;
      if (!await sameAccount(generation, c.signal)) return;
      const lifetime = Date.parse(challenge.expiresAtUtc) - Date.parse(challenge.serverNowUtc);
      // Stop capture conservatively from request start; allow absent-operation recovery only
      // after the later response-receipt bound, never while the challenge could still commit.
      draft.current = { action, key, site, challengeId: challenge.id, deadline: requested + lifetime, settledAfter: performance.now() + lifetime + 1000 };
      serverAnchor.current = { utc: Date.parse(challenge.serverNowUtc), monotonic: requested };
      update({ serverTime: challenge.serverNowUtc });
      // getUserMedia itself is not abortable. A late permission result must stop its tracks.
      const camera = navigator.mediaDevices.getUserMedia({ audio: false, video: { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 720 } } });
      let timedOut = false;
      let timer: ReturnType<typeof setTimeout> | undefined;
      camera.then(value => { if (generation !== epoch.current || timedOut) value.getTracks().forEach(t => t.stop()); }, () => {});
      let live: MediaStream;
      try { live = await Promise.race([camera, new Promise<never>((_, reject) => { timer = setTimeout(() => { timedOut = true; reject(new Error('กล้องหมดเวลารอ กรุณาอนุญาตแล้วลองใหม่')); }, 15000); })]); }
      catch { throw new Error('เปิดกล้องไม่ได้ กรุณาอนุญาตกล้องและลองใหม่'); }
      finally { clearTimeout(timer); }
      if (generation !== epoch.current) { live.getTracks().forEach(t => t.stop()); return; }
      stream.current = live;
      for (const track of live.getVideoTracks()) {
        const interrupted = () => { if (generation === epoch.current) { cancel(); failure(new Error('กล้องถูกขัดจังหวะ กรุณาเปิดกล้องและถ่ายใหม่')); } };
        track.onmute = interrupted; track.onended = interrupted;
      }
      if (!video.current) throw new Error('กล้องไม่พร้อม กรุณาลองใหม่');
      video.current.srcObject = live;
      let playbackTimer: ReturnType<typeof setTimeout> | undefined;
      try {
        await Promise.race([video.current.play(), new Promise<never>((_, reject) => {
          playbackTimer = setTimeout(() => reject(new Error('กล้องไม่ส่งภาพภายในเวลาที่กำหนด กรุณาถ่ายใหม่')), 10000);
        })]);
      } finally { clearTimeout(playbackTimer); }
      const gps = await position(c.signal);
      if (generation !== epoch.current) return;
      if (performance.now() >= draft.current!.deadline) throw new Error('การถ่ายภาพหมดอายุ กรุณาถ่ายใหม่');
      update({ phase: 'ready', gps });
    } catch (error) { if (generation === epoch.current) failure(error); }
  }

  async function capture() {
    const d = draft.current; const v = video.current;
    if (state.current.phase !== 'ready' || !d || !v) return;
    const generation = epoch.current;
    // Synchronous guard prevents two canvas encodes from replacing each other.
    update({ phase: 'permissions' });
    try {
      offline();
      if (performance.now() >= d.deadline) throw new Error('การถ่ายภาพหมดอายุ กรุณาถ่ายใหม่');
      if (!v.videoWidth || !v.videoHeight || !stream.current?.getVideoTracks().some(t => t.readyState === 'live' && !t.muted && t.enabled)) throw new Error('กล้องไม่พร้อม กรุณาถ่ายใหม่');
      // Require a newly delivered frame, not the last pixels retained by an interrupted video.
      await new Promise<void>((resolve, reject) => {
        let handle = 0;
        const timer = setTimeout(() => { v.cancelVideoFrameCallback(handle); reject(new Error('กล้องไม่ส่งภาพใหม่ กรุณาเปิดกล้องและถ่ายใหม่')); }, 2000);
        if (!v.requestVideoFrameCallback) { clearTimeout(timer); reject(new Error('เบราว์เซอร์ไม่รองรับการตรวจภาพสด กรุณาใช้เบราว์เซอร์รุ่นใหม่')); return; }
        handle = v.requestVideoFrameCallback(() => { clearTimeout(timer); resolve(); });
      });
      if (generation !== epoch.current) return;
      if (!stream.current?.getVideoTracks().some(t => t.readyState === 'live' && !t.muted && t.enabled)) throw new Error('กล้องไม่พร้อม กรุณาถ่ายใหม่');
      const scale = Math.min(1, 1920 / Math.max(v.videoWidth, v.videoHeight));
      const canvas = document.createElement('canvas'); canvas.width = Math.round(v.videoWidth * scale); canvas.height = Math.round(v.videoHeight * scale);
      canvas.getContext('2d')!.drawImage(v, 0, 0, canvas.width, canvas.height);
      const capturedAt = new Date().toISOString();
      const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', 0.9));
      if (generation !== epoch.current) return;
      if (!blob || blob.size > 10 * 1024 * 1024) throw new Error('ภาพไม่พร้อมหรือใหญ่เกินกำหนด กรุณาถ่ายใหม่');
      if (performance.now() >= d.deadline) throw new Error('การถ่ายภาพหมดอายุ กรุณาถ่ายใหม่');
      d.blob = blob; d.capturedAt = capturedAt;
      stream.current?.getTracks().forEach(t => t.stop()); stream.current = null; v.srcObject = null;
      update({ phase: 'captured', previewUrl: URL.createObjectURL(blob) });
    } catch (error) { if (generation === epoch.current) failure(error); }
  }

  async function submit(continuous: boolean) {
    const d = draft.current; const current = state.current.current;
    if (state.current.phase !== 'captured' || !d?.blob || !d.capturedAt || !current || pending.current) return;
    if (d.action === 'CheckOut' && current.openDay && current.openDay.workDate !== bangkokDate(serverNow()) && !continuous) { update({ serverTime: serverNow() }); return; }
    const generation = epoch.current; const c = controller.current!;
    update({ phase: 'submitting', error: '' });
    try {
      offline(); const gps = await position(c.signal);
      if (generation !== epoch.current) return;
      if (performance.now() >= d.deadline) throw new Error('การถ่ายภาพหมดอายุ กรุณาถ่ายใหม่');
      if (d.action === 'CheckOut' && current.openDay && current.openDay.workDate !== bangkokDate(serverNow()) && !continuous) { update({ phase: 'captured', serverTime: serverNow(), error: 'ข้ามวันแล้ว กรุณายืนยันว่าทำงานต่อเนื่อง' }); return; }
      const metadata: CaptureMetadata = { challengeId: d.challengeId, idempotencyKey: d.key, site: d.site, gps, deviceCapturedAtUtc: d.capturedAt, confirmContinuousWork: continuous, expectedDayVersion: current.openDay?.version ?? null };
      pending.current = { key: d.key, action: d.action, settledAfter: d.settledAfter };
      const response = await submitCapture(d.action, metadata, d.blob, c.signal);
      if (generation !== epoch.current) return;
      if (!response.ok) {
        if (!await sameAccount(generation, c.signal)) return;
        // Conflict, rate-limit or server failure may follow an earlier successful attempt.
        if (![409, 429].includes(response.status) && response.status < 500) pending.current = null;
        throw new Error(await recordingResponseError(response));
      }
      const receipt = parseReceipt(await response.json());
      if (generation !== epoch.current) return;
      if (!await sameAccount(generation, c.signal)) return;
      pending.current = null; release(); update({ phase: 'success', receipt, gps: null, previewUrl: null });
    } catch (error) { if (generation === epoch.current) failure(error); }
  }

  async function checkOperation() {
    const p = pending.current;
    if (!p || checking.current || state.current.loading) return;
    checking.current = true; const generation = epoch.current;
    update({ error: '' });
    try {
      offline();
      const response = await getCaptureOperation(p.key, p.action, controller.current!.signal);
      if (generation !== epoch.current) return;
      if (response.status === 404 && performance.now() >= p.settledAfter) {
        pending.current = null; await load();
        update({ error: 'ไม่พบรายการหลังพ้นอายุคำขอ กรุณาตรวจสถานะรอบและถ่ายใหม่' }); return;
      }
      if (response.status === 404) throw new Error('ยังไม่พบผลคำขอ กรุณารอให้พ้นอายุการถ่ายภาพแล้วตรวจผลอีกครั้ง');
      if (!response.ok) throw new Error(recordingError(response.status));
      const operation = parseOperation(await response.json());
      if (generation !== epoch.current) return;
      if (!await sameAccount(generation, controller.current!.signal)) return;
      if (operation.state === 'Committed') { pending.current = null; release(); update({ phase: 'success', receipt: operation.receipt, previewUrl: null, gps: null }); }
      else if (operation.state === 'Processing') update({ error: 'ระบบกำลังประมวลผล กรุณารอสักครู่แล้วตรวจผลอีกครั้ง' });
      else { pending.current = null; await load(); update({ error: 'คำขอเดิมไม่สำเร็จหรือหมดอายุ กรุณาตรวจสถานะรอบและถ่ายใหม่' }); }
    } catch (error) { if (generation === epoch.current) failure(error); }
    finally { if (generation === epoch.current) checking.current = false; }
  }
  function reset() { if (!pending.current && !['submitting', 'permissions'].includes(state.current.phase)) stop(); }
  return { ...view, video, start, capture, submit, checkOperation, reset, stop, reload: load };
}

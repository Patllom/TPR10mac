'use client';
import { useState, type FormEvent } from 'react';
import { useAuthMutation } from '@/components/auth/useAuthMutation';
import LogoutButtons from '@/components/auth/LogoutButtons';
import { isSessionView, sessionDestination } from '@/lib/auth/session-view';

export default function MfaForm({ enrollment, returnTo }: { enrollment: boolean; returnTo: string }) {
  const { run, busy, error } = useAuthMutation();
  const [uri, setUri] = useState('');
  const [recovery, setRecovery] = useState(false);
  const [codes, setCodes] = useState<string[]>([]);
  const manualKey = uri ? new URL(uri).searchParams.get('secret') ?? '' : '';
  async function enroll() {
    await run('/api/v1/auth/mfa/enroll', {}, async response => {
      const data = await response.json(); const parsed = new URL(data.provisioningUri);
      if (parsed.protocol !== 'otpauth:' || !/^[A-Z2-7]+$/.test(parsed.searchParams.get('secret') ?? '')) throw new Error();
      setUri(parsed.toString());
    });
  }
  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); const form = event.currentTarget; const code = new FormData(form).get('code');
    const action = enrollment ? 'confirm' : recovery ? 'recover' : 'challenge';
    void run('/api/v1/auth/mfa/' + action, { code }, async response => {
      const data = await response.json();
      if (!isSessionView(data.session)) throw new Error();
      form.reset(); setUri('');
      if (enrollment) {
        if (!Array.isArray(data.recoveryCodes) || data.recoveryCodes.length !== 10 || !data.recoveryCodes.every((c: unknown) => typeof c === 'string')) throw new Error();
        setCodes(data.recoveryCodes);
      } else window.location.replace(sessionDestination(data.session, returnTo));
    });
  }
  return <div className="space-y-5">
    <p>ห้ามแชร์คีย์หรือรหัสกู้คืนกับผู้อื่น</p>
    {codes.length > 0 ? <>
      <p>เก็บรหัสกู้คืนในที่ปลอดภัย แต่ละรหัสใช้ได้ครั้งเดียว และจะแสดงเฉพาะครั้งนี้</p>
      <ul className="space-y-2 break-all font-mono">{codes.map(code => <li data-testid="recovery-code" key={code}>{code}</li>)}</ul>
      <button className="auth-button" onClick={() => { setCodes([]); window.location.replace(returnTo); }}>บันทึกรหัสกู้คืนแล้ว ไปต่อ</button>
    </> : <>
      {enrollment && !uri && <><p>เปิดแอป Authenticator แล้วเริ่มตั้งค่า หากเพิ่งทำรายการค้างไว้หรือ session หมดอายุ ให้เข้าสู่ระบบใหม่และรอไม่เกิน 10 นาที</p>
        <button disabled={busy} className="auth-button" onClick={enroll}>เริ่มตั้งค่า MFA</button></>}
      {uri && <><label className="block">คีย์สำหรับกรอกเอง<input className="auth-input font-mono" readOnly value={manualKey} autoComplete="off" /></label>
        <label className="block">URI สำหรับ Authenticator<textarea className="auth-input break-all" readOnly value={uri} /></label></>}
      {(!enrollment || uri) && <form onSubmit={submit} aria-busy={busy}><fieldset className="space-y-5" disabled={busy}>
        <label className="block">{recovery ? 'รหัสกู้คืน' : 'รหัสยืนยัน 6 หลัก'}<input key={String(recovery)} className="auth-input" name="code" required autoComplete={recovery ? 'off' : 'one-time-code'} inputMode={recovery ? 'text' : 'numeric'} pattern={recovery ? '[A-Za-z0-9_-]{22}' : '[0-9]{6}'} maxLength={recovery ? 22 : 6} /></label>
        <button disabled={busy} className="auth-button">{recovery ? 'ยืนยันรหัสกู้คืน' : 'ยืนยัน MFA'}</button>
      </fieldset></form>}
      {!enrollment && <button disabled={busy} className="underline" onClick={() => setRecovery(!recovery)}>{recovery ? 'ใช้แอป Authenticator' : 'ใช้รหัสกู้คืน'}</button>}
    </>}
    {error && <p role="alert">{error}</p>}
    <LogoutButtons />
  </div>;
}

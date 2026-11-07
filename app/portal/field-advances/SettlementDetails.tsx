'use client';
import { useCallback, useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { currentAccount } from '@/lib/field-advances/policy-client';
import { createRequestClient } from '@/lib/field-advances/request-client';
import { createSettlementClient, SettlementError } from '@/lib/field-advances/settlement-client';
import type { SettlementCommand, SettlementFamily, SettlementPage, SettlementPreview, SettlementRound, SettlementRow, SettlementSummary } from '@/lib/field-advances/settlement-types';
import type { CategoryRule } from '@/lib/field-advances/policy-types';
import { pendingSettlements, documentInputError, stateLabel, timeLabel, type PendingSettlement } from '@/lib/field-advances/settlement-view';
import SettlementRoundEditor from './SettlementRoundEditor';
import RefundEditor from './RefundEditor';
import SettlementStatus from './SettlementStatus';
type Screen={summary:SettlementSummary;rounds:SettlementRound[];roundPage:SettlementPage;refunds:SettlementPage;documents:SettlementPage;completion:SettlementPage;family:SettlementFamily;categories:CategoryRule[]};
export default function SettlementDetails({requestId}:{requestId:string}){
 const [account,setAccount]=useState('');const accountRef=useRef('');const [screen,setScreen]=useState<Screen|null>(null);const [error,setError]=useState('');const [notice,setNotice]=useState('');const [busy,setBusy]=useState(false);const [fresh,setFresh]=useState(false);const lock=useRef(false);const [pending,setPending]=useState<PendingSettlement|null>(null);const [round,setRound]=useState<SettlementRound>();const [refund,setRefund]=useState<SettlementRow>();const [preview,setPreview]=useState<SettlementPreview|null>(null);const [reason,setReason]=useState('');const [proofs,setProofs]=useState<string[]>([]);const [issuer,setIssuer]=useState('');const [number,setNumber]=useState('');const [noNumber,setNoNumber]=useState(false);const [issuedOn,setIssuedOn]=useState('');const [amount,setAmount]=useState('');const [documentReason,setDocumentReason]=useState('');const [editorEpoch,setEditorEpoch]=useState(0);
 const [evidenceType,setEvidenceType]=useState('');
 const [editingDocument,setEditingDocument]=useState<SettlementRow>();
 const generation=useRef(0);const abort=useRef<AbortController | undefined>(undefined);
 const clear=useCallback(()=>{setFresh(false);setScreen(null);setRound(undefined);setRefund(undefined);setPreview(null);setReason('');setProofs([]);setIssuer('');setNumber('');setNoNumber(false);setIssuedOn('');setAmount('');setDocumentReason('');setEvidenceType('');setEditingDocument(undefined);setEditorEpoch(n=>n+1);},[]);
 const invalidate=useCallback(()=>{generation.current++;abort.current?.abort();},[]);
 const handleError=useCallback((e:unknown)=>{const status=e&&typeof e==='object'&&'status'in e?e.status:null;if(status===401||status===403){clear();setPending(null);}setError(e instanceof Error?e.message:'ยังยืนยันผลไม่ได้ กรุณาโหลดใหม่');},[clear]);
 const load=useCallback(async(cursors:Partial<Record<'rounds'|'refunds'|'documents'|'completion'|'family',string>>={})=>{
  setFresh(false);const g=++generation.current;abort.current?.abort();const controller=new AbortController();abort.current=controller;setError('');
  try{
   const user=await currentAccount(fetch,controller.signal);
   if(accountRef.current&&accountRef.current!==user){pendingSettlements.clear();clear();setPending(null);accountRef.current='';setAccount('');throw new SettlementError(403);}
   accountRef.current=user;setAccount(user);const client=createSettlementClient(user), requests=createRequestClient(user);
   const summary=await client.loadSettlement(requestId,controller.signal);
   const [roundPage,refunds,documents,completion,family,request]=await Promise.all([client.listSettlement(requestId,'rounds',25,cursors.rounds,controller.signal),client.listSettlement(requestId,'refunds',25,cursors.refunds,controller.signal),client.listSettlement(requestId,'documents',25,cursors.documents,controller.signal),client.listSettlement(requestId,'completion',25,cursors.completion,controller.signal),client.loadSettlementFamily(requestId,25,cursors.family,controller.signal),requests.details(requestId,controller.signal)]);
   const [requestScreen,...rounds]=await Promise.all([requests.screen(request,controller.signal),...roundPage.items.filter(r=>r.round?.revisionId).map(r=>client.loadSettlementRound(requestId,r.id,r.round!.revisionId!,controller.signal))]);
   if(g!==generation.current)return;
   if([roundPage,refunds,documents,completion,...rounds].some(p=>p.requestVersion!==summary.requestVersion||p.familyVersion!==summary.familyVersion)||family.familyVersion!==summary.familyVersion)throw new SettlementError(409);
   setScreen({summary,roundPage,rounds,refunds,documents,completion,family,categories:requestScreen.frozenPolicy?.content.categories??[]});setPending(pendingSettlements.current(user,requestId));setPreview(null);setRound(old=>old?rounds.find(r=>r.roundId===old.roundId):undefined);setRefund(old=>old?refunds.items.find(r=>r.id===old.id):undefined);setFresh(true);return true;
  }catch(e){if(g===generation.current)handleError(e);return false;}
 },[requestId,clear,handleError]);
 useEffect(()=>{void load();const focus=()=>{clear();void load();};const hidden=()=>{if(document.hidden){invalidate();clear();}else void load();};window.addEventListener('focus',focus);document.addEventListener('visibilitychange',hidden);return()=>{invalidate();window.removeEventListener('focus',focus);document.removeEventListener('visibilitychange',hidden);};},[load,clear,invalidate]);
 useEffect(()=>{const warn=(e:BeforeUnloadEvent)=>{if(accountRef.current&&pendingSettlements.current(accountRef.current,requestId)){e.preventDefault();e.returnValue='';}};window.addEventListener('beforeunload',warn);return()=>window.removeEventListener('beforeunload',warn);},[requestId]);
 function resetSuccessfulDocument(action:string){
  if(action!=='documents/create'&&action!=='documents/update')return;
  setEditingDocument(undefined);setProofs([]);setEvidenceType('');setIssuer('');setNumber('');
  setNoNumber(false);setIssuedOn('');setAmount('');setDocumentReason('');
 }
 async function dispatch(p:PendingSettlement,retry=false){
  if(lock.current||!('requestVersion'in p.command))return;lock.current=true;setBusy(true);setError('');setNotice('');
  try{if(await currentAccount()!==p.account){pendingSettlements.clear();clear();setPending(null);accountRef.current='';setAccount('');throw new SettlementError(403);}
   await createSettlementClient(p.account).sendSettlementCommand(p.requestId,p.action,p.body,p.command);
   resetSuccessfulDocument(p.action);
   pendingSettlements.resolve(p.account,p.requestId,p.command.commandId);setPending(null);setNotice('เซิร์ฟเวอร์ยืนยันผลคำสั่งแล้ว');setRound(undefined);setRefund(undefined);setPreview(null);await load();
  }catch(e){if(!retry&&e instanceof SettlementError&&!e.unknownResult){pendingSettlements.resolve(p.account,p.requestId,p.command.commandId);setPending(null);}else setPending(p);handleError(e);}
  finally{lock.current=false;setBusy(false);}
 }
 async function checkReceipt(p:PendingSettlement){
  if(lock.current)return;lock.current=true;setBusy(true);setError('');
  try{
   await createSettlementClient(p.account).checkSettlementReceipt(p.requestId,p.action,p.entityId,p.command.commandId);
   resetSuccessfulDocument(p.action);
   pendingSettlements.resolve(p.account,p.requestId,p.command.commandId);setPending(null);setNotice('เซิร์ฟเวอร์ยืนยันผลคำสั่งแล้ว');await load();
  }catch(e){handleError(e);}
  finally{lock.current=false;setBusy(false);}
 }
 async function command(action:string,body:unknown,why:string,entityVersion:number|null=null,confirmation?:SettlementPreview){
  try{if(!screen||!fresh||lock.current||pendingSettlements.current(account,requestId))return;
  if(!why.trim()){setError('กรุณาระบุเหตุผล');return;}
  const basis=confirmation??screen.summary;const meta:SettlementCommand={commandId:crypto.randomUUID(),requestVersion:basis.requestVersion,familyVersion:basis.familyVersion,entityVersion,reason:why,...(confirmation?{confirmationHash:confirmation.confirmationHash}:{})};
  const p=pendingSettlements.begin(account,requestId,action,body,meta);setPending(p);await dispatch(p);}catch(e){handleError(e);}
 }
 async function upload(file:File){
  if(lock.current||pendingSettlements.current(account,requestId))throw new Error('รอตรวจสอบผลรายการ');
  lock.current=true;setBusy(true);
  try{const value=await createSettlementClient(account).uploadEvidence(requestId,file);if(!value||typeof value!=='object'||!('id'in value)||typeof value.id!=='string')throw new SettlementError(503);
   if(!await load())throw new SettlementError(503);
   return value.id;
  }catch(e){handleError(e);throw e;}finally{lock.current=false;setBusy(false);}
 }
 async function download(id:string){try{const blob=await createSettlementClient(account).downloadEvidence(requestId,id);const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='หลักฐาน';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}catch(e){handleError(e);}}
 async function saveDocument(){
  const input={issuer,number:noNumber?null:number,hasNoNumber:noNumber,issuedOn,amount,evidenceIds:proofs,evidenceType};
  const problem=documentInputError(input,documentReason);
  if(problem){setError(problem);return;}
  await command(editingDocument?'documents/update':'documents/create',
   editingDocument?{...input,entityId:editingDocument.id}:input,documentReason,editingDocument?.version??null);
 }
 function editDocument(row:SettlementRow){
  const doc=row.document;if(!doc)return;
  setEditingDocument(row);setIssuer(doc.issuer);setNumber(doc.documentNumber??'');setNoNumber(doc.hasNoNumber);
  setIssuedOn(doc.issuedOn);setAmount(doc.amount);setProofs(doc.evidenceIds);setEvidenceType(doc.evidenceType??'');setDocumentReason('');
 }
 const disabled=busy||!!pending||!fresh;
 return <div className="space-y-6 min-w-0"><Link className="underline" href={`/portal/field-advances/${requestId}`}>กลับรายละเอียดคำขอ</Link><button className="ml-4 underline" disabled={busy} onClick={()=>void load()}>โหลดข้อมูลล่าสุด</button>
 {error&&<p role="alert" className="rounded border border-red-400 p-3">{error}</p>}{notice&&<p role="status">{notice}</p>}
 {busy&&<p role="status">กำลังส่งหรือตรวจข้อมูล กรุณารอ</p>}{pending&&screen&&!busy&&<section className="rounded border border-amber-600 p-4 space-y-2"><h2 className="font-semibold">รอตรวจสอบผลรายการ</h2><p>ยังไม่ทราบว่าบันทึกสำเร็จหรือไม่ ไม่มีการส่งซ้ำอัตโนมัติ การโหลดข้อมูลใหม่ไม่ยืนยันผลคำสั่งนี้</p><p className="break-all">รหัสคำสั่ง {pending.command.commandId}</p><p>หากยังไม่พบผล คำสั่งยังคงรอตรวจสอบและไม่เปิดให้สร้างรายการแทน</p><button className="underline mr-4" disabled={busy} onClick={()=>void checkReceipt(pending)}>ตรวจผลจากรหัสคำสั่ง</button>{'requestVersion'in pending.command&&<button className="underline" disabled={busy} onClick={()=>void dispatch(pending,true)}>ตรวจผลโดยส่งคำสั่งเดิมอีกครั้ง</button>}</section>}
 {screen&&<>
 <SettlementStatus summary={screen.summary}/>
 <section><h2 className="font-semibold">กำหนดเวลา (Asia/Bangkok)</h2>{screen.completion.items.map(row=><p key={row.id}>จบงาน {row.completion?.completedOn} · เคลียร์ {row.completion?.clearingDueOn} · คืนเงิน {row.completion?.refundDueOn} · {stateLabel(row.state)}</p>)}{screen.completion.items.length===0&&<p>ยังไม่มีวันจบงานที่ยืนยัน</p>}</section>
 <section className="space-y-4"><h2 className="font-semibold">รอบค่าใช้จ่าย</h2>{screen.rounds.map(r=><article key={r.revisionId} className="rounded-xl border p-4 space-y-2"><p>{stateLabel(r.state)} · ยอดจากเซิร์ฟเวอร์ {r.total} บาท</p><p>{timeLabel(r.submissionOnTime)}{r.submittedAtUtc&&` · ส่งระบบ ${new Intl.DateTimeFormat('th-TH',{dateStyle:'medium',timeStyle:'short',timeZone:'Asia/Bangkok'}).format(new Date(r.submittedAtUtc))}`}</p>
 {r.returnReview&&<div role="note" className="border-l-4 border-amber-600 pl-3"><p>ส่งกลับทั้งรอบ: {r.returnReview.reason}</p><p>กำหนดแก้ {r.returnReview.correctionDueOn??'ยังไม่ทราบ'}</p><p>แก้ฉบับใหม่แล้วเจ้าของต้องยืนยัน และเริ่มอนุมัติใหม่ทั้งหมด</p></div>}
 <ul>{r.lines.map(l=><li key={l.id}><span>{l.description}</span> · {l.amount} บาท {r.returnReview?.affectedLineIds.includes(l.id)&&<span> — ต้องแก้ตามเหตุผลส่งกลับ: {r.returnReview.reason}</span>}</li>)}</ul>
 {r.allowedActions?.includes('round-draft')&&<button className="underline mr-4" disabled={disabled} onClick={()=>{setRound(r);setPreview(null);setEditorEpoch(n=>n+1);}}>แก้ไขรอบ</button>}
 {r.allowedActions?.includes('round-confirm')&&<button className="underline" disabled={disabled} onClick={async()=>{try{setPreview(await createSettlementClient(account).previewSettlementRound(requestId,r.roundId));}catch(e){handleError(e);}}}>ทบทวนเพื่อยืนยันรอบ</button>}
 </article>)}{screen.roundPage.nextCursor&&<button className="underline" onClick={()=>void load({rounds:screen.roundPage.nextCursor!})}>รอบหน้าถัดไป</button>}</section>
 {preview&&<section className="rounded-xl border-2 border-orange-500 p-4 space-y-2" aria-label="ตัวอย่างยืนยันฉบับ"><h2 className="font-semibold">ทบทวนฉบับก่อนยืนยัน</h2><p className="break-all">ฉบับ {preview.revisionId} · เวอร์ชัน {preview.revisionVersion}</p><p>ยอดยืนยันจากเซิร์ฟเวอร์: {preview.total} บาท</p><ul>{preview.lines.map(l=><li key={l.id}>{l.description} · {l.amount} บาท</li>)}</ul><p>เอกสาร {preview.documents.length} ใบ · หลักฐาน {preview.evidence.length} ไฟล์</p><label className="block">เหตุผลยืนยันรอบ<input className="block w-full border p-2" value={reason} onChange={e=>setReason(e.target.value)}/></label><button className="underline mr-4" disabled={disabled} onClick={()=>void command('rounds/submit',{entityId:preview.roundId},reason,preview.revisionVersion,preview)}>ยืนยันฉบับนี้และส่งตรวจ</button><button className="underline" onClick={()=>setPreview(null)}>ปิดตัวอย่าง</button></section>}
 <SettlementRoundEditor key={`round-${round?.revisionId??'new'}-${round?.roundVersion??0}-${editorEpoch}`} categories={screen.categories} documents={screen.documents.items.flatMap(r=>r.document?[r.document]:[])} round={round} adjustment={screen.roundPage.items.find(r=>r.id===round?.roundId)?.round?.kind==='Adjustment'} serverAllowedActions={round?.allowedActions??screen.summary.allowedActions} disabled={disabled} onCancel={round?()=>{setRound(undefined);setEditorEpoch(n=>n+1);}:undefined} onSave={(body,why)=>command(round?'rounds/update':'rounds/create',round?{...(body as object),entityId:round.roundId}:body,why,round?.roundVersion??null)}/>
 {screen.summary.allowedActions.includes('document')&&<section className="rounded-xl border p-4 space-y-3"><h2 className="font-semibold">เพิ่มเอกสารของคำขอนี้</h2><fieldset disabled={disabled} className="grid gap-3 sm:grid-cols-2"><label>แนบรูปหรือ PDF<input type="file" accept="image/png,image/jpeg,application/pdf" className="block w-full" onChange={async e=>{const file=e.target.files?.[0];e.target.value='';if(!file)return;setBusy(true);try{const id=await upload(file);setProofs(p=>[...p,id]);}catch{/* parent cleared protected state */}finally{setBusy(false);}}}/></label><p>หลักฐานพร้อมใช้ {proofs.length} ไฟล์</p><label>ประเภทหลักฐาน<select aria-label="ประเภทหลักฐาน" className="block w-full border p-2" value={evidenceType} onChange={e=>setEvidenceType(e.target.value)}><option value="">เลือกตามเอกสารจริง</option>{Array.from(new Set(screen.categories.flatMap(c=>c.evidenceTypes))).map(kind=><option key={kind} value={kind}>{kind}</option>)}</select></label><label>ผู้ออกเอกสาร<input className="block w-full border p-2" value={issuer} onChange={e=>setIssuer(e.target.value)}/></label><label>เลขเอกสาร<input className="block w-full border p-2" disabled={noNumber} value={number} onChange={e=>setNumber(e.target.value)}/></label><label><input type="checkbox" checked={noNumber} onChange={e=>setNoNumber(e.target.checked)}/> ไม่มีเลขเอกสารจริง</label><label>วันที่เอกสาร<input type="date" className="block w-full min-w-0 border p-2" value={issuedOn} onChange={e=>setIssuedOn(e.target.value)}/></label><label>ยอดเอกสาร<input inputMode="decimal" className="block w-full border p-2" value={amount} onChange={e=>setAmount(e.target.value)}/></label><label>เหตุผลเอกสาร<input className="block w-full border p-2" value={documentReason} onChange={e=>setDocumentReason(e.target.value)}/></label><button className="underline" onClick={()=>void saveDocument()}>{editingDocument?'บันทึกการแก้ไขเอกสาร':'บันทึกเอกสาร'}</button>{editingDocument&&<button onClick={()=>{setEditingDocument(undefined);setProofs([]);setEvidenceType('');}}>ยกเลิกแก้ไขเอกสาร</button>}</fieldset></section>}
 <section><h2 className="font-semibold">เอกสารและหลักฐาน</h2>{screen.documents.items.map(row=><div key={row.id} className="border-b py-2"><p><span>{row.document?.issuer}</span> · {row.document?.amount} บาท · {row.document?.hasNoNumber?'ไม่มีเลขจริง':row.document?.documentNumber}</p>{screen.summary.allowedActions.includes('document')&&<button className="underline mr-3" disabled={disabled} onClick={()=>editDocument(row)}>แก้ไขเอกสาร</button>}{row.document?.evidenceIds.map((id,i)=><button key={id} className="underline mr-3" onClick={()=>void download(id)}>ดาวน์โหลดหลักฐาน {i+1}</button>)}</div>)}{screen.documents.nextCursor&&<button className="underline" onClick={()=>void load({documents:screen.documents.nextCursor!})}>เอกสารหน้าถัดไป</button>}</section>
 <section className="space-y-3"><h2 className="font-semibold">รายการคืนเงิน</h2>{screen.refunds.items.map(row=><article key={row.id} className="rounded-xl border p-3"><p>{row.refund?.amount} บาท · {stateLabel(row.state)} · {row.refund?.returnedOn}</p><p>{timeLabel(row.submissionOnTime)} · {row.refund?.companyRecipient}</p><p>{row.reason}</p>{row.allowedActions?.includes('refund-update')&&<button className="underline mr-3" disabled={disabled} onClick={()=>{setRefund(row);setEditorEpoch(n=>n+1);}}>แก้ไขคืนเงิน</button>}{row.allowedActions?.includes('refund-confirm')&&<button className="underline mr-3" disabled={disabled} onClick={()=>void command('refunds/confirm',{entityId:row.id},reason,row.version)}>ยืนยันคืนเงินรายการนี้</button>}{row.allowedActions?.includes('refund-cancel')&&<button className="underline" disabled={disabled} onClick={()=>void command('refunds/cancel',{entityId:row.id},reason,row.version)}>ยกเลิกคืนเงินรายการนี้</button>}</article>)}{screen.refunds.nextCursor&&<button className="underline" onClick={()=>void load({refunds:screen.refunds.nextCursor!})}>เงินคืนหน้าถัดไป</button>}</section>
 <RefundEditor key={`refund-${refund?.id??'new'}-${refund?.version??0}-${editorEpoch}`} row={refund} serverAllowedActions={refund?.allowedActions??screen.summary.allowedActions} disabled={disabled} onUpload={upload} onCancel={refund?()=>{setRefund(undefined);setEditorEpoch(n=>n+1);}:undefined} onSave={(body,why)=>command(refund?'refunds/update':'refunds/create',refund?{...(body as object),entityId:refund.id}:body,why,refund?.version??null)}/>
 <section className="space-y-3 rounded-xl border p-4"><label>เหตุผลส่งครบ ถอน หรือยืนยันรายการคืน<input className="block w-full border p-2" maxLength={2000} value={reason} onChange={e=>setReason(e.target.value)} disabled={disabled}/></label>{screen.summary.allowedActions.includes('declare')&&<button className="underline" disabled={disabled} onClick={()=>void command('declaration',{complete:!screen.summary.declaration||screen.summary.declaration.isWithdrawal},reason)}>{screen.summary.declaration&&!screen.summary.declaration.isWithdrawal?'ถอนการยืนยันส่งครบ':'ยืนยันส่งครบ'}</button>}</section>
 <section className="space-y-2"><h2 className="font-semibold">ภาพรวมเฉพาะคำขอที่มองเห็นในหน้านี้</h2><p>ยอดคงเหลือเฉพาะหน้านี้ {screen.family.pageTotals.ownerOutstanding} บาท ไม่ใช่ยอดทั้งชุด</p>{screen.family.requests.map(r=><p key={r.requestId} className="break-all"><Link className="underline" href={`/portal/field-advances/${r.requestId}/settlement`}>{r.requestId}</Link> · {stateLabel(r.state)}</p>)}{screen.family.nextCursor&&<button className="underline" onClick={()=>void load({family:screen.family.nextCursor!})}>ภาพรวมหน้าถัดไป</button>}</section>
 </>}
 </div>;
}

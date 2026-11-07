'use client';
import { useState } from 'react';
import type { CategoryRule } from '@/lib/field-advances/policy-types';
import type { SettlementDocument, SettlementRound } from '@/lib/field-advances/settlement-types';
import { linePreview, editableLine, roundInputError } from '@/lib/field-advances/settlement-view';
type Line = { categoryId:string; description:string; incurredOn:string; quantity:string; rate:string; lumpSum:string; sourceLineId:string|null; allocations:{documentId:string;amount:string}[]; mode:'rate'|'lump' };
export default function SettlementRoundEditor({ categories, documents, round, serverAllowedActions, disabled, adjustment = false, onSave, onCancel }: { categories:CategoryRule[]; documents:SettlementDocument[]; round?:SettlementRound; serverAllowedActions:string[]; disabled:boolean; adjustment?:boolean; onSave:(body:unknown,reason:string)=>Promise<void>; onCancel?:()=>void }) {
 const blank=():Line=>({categoryId:categories[0]?.id??'',description:'',incurredOn:'',quantity:'',rate:'',lumpSum:'',sourceLineId:null,allocations:[{documentId:'',amount:''}],mode:'rate'});
 const [lines,setLines]=useState<Line[]>(()=>round?round.lines.map(editableLine):[blank()]);
 const [reason,setReason]=useState('');const [error,setError]=useState('');
 const edit=(i:number,patch:Partial<Line>)=>setLines(rows=>rows.map((r,n)=>n===i?{...r,...patch}:r));
 async function save(){
  const input=lines.map(l=>{const category=categories.find(c=>c.id===l.categoryId);const useRate=category?.standardRate!=null||l.mode==='rate';return {sourceLineId:l.sourceLineId,categoryId:l.categoryId,description:l.description,incurredOn:l.incurredOn,quantity:useRate?l.quantity:null,rate:useRate?category?.standardRate??l.rate:null,lumpSum:useRate?null:l.lumpSum,allocations:l.allocations.filter(a=>a.documentId)};});
  const problem=roundInputError(input,adjustment);
  if(!reason.trim()||problem){setError(problem??'กรุณาระบุเหตุผล');return;}
  setError('');await onSave({lines:input},reason);
 }
 if(!serverAllowedActions.includes('round-draft'))return null;
 return <section aria-label="แก้ไขรอบค่าใช้จ่าย" className="space-y-4 rounded-xl border p-4"><h2 className="font-semibold">{round?'แก้ไขทั้งรอบเป็นฉบับใหม่':'เพิ่มรอบค่าใช้จ่าย'}</h2>
 <fieldset disabled={disabled} className="space-y-4 disabled:opacity-60">{lines.map((line,i)=>{const c=categories.find(c=>c.id===line.categoryId),n=i+1;const rate=c?.standardRate??line.rate;const useRate=c?.standardRate!=null||line.mode==='rate';return <div key={i} className="grid gap-3 rounded-lg border p-3 sm:grid-cols-2">
 <label>หมวด {n}<select aria-label={`หมวด ${n}`} className="block w-full border p-2" value={line.categoryId} onChange={e=>edit(i,{categoryId:e.target.value})}>{categories.map(c=><option key={c.id} value={c.id}>{c.name}</option>)}</select></label>
 <label>รายละเอียดรายการ {n}<input className="block w-full border p-2" value={line.description} onChange={e=>edit(i,{description:e.target.value})}/></label>
 <label>วันที่รายการ {n}<input type="date" className="block w-full min-w-0 border p-2" value={line.incurredOn} onChange={e=>edit(i,{incurredOn:e.target.value})}/></label>
 {c?.standardRate==null&&<label>รูปแบบ {n}<select aria-label={`รูปแบบ ${n}`} className="block w-full border p-2" value={line.mode} onChange={e=>edit(i,{mode:e.target.value as Line['mode']})}><option value="rate">จำนวน × อัตรา</option><option value="lump">เหมารวม</option></select></label>}
 {useRate?<><label>จำนวน {n}<input inputMode="decimal" className="block w-full border p-2" value={line.quantity} onChange={e=>edit(i,{quantity:e.target.value})}/></label><label>อัตรา {n}<input inputMode="decimal" className="block w-full border p-2" readOnly={c?.standardRate!=null} value={rate} onChange={e=>edit(i,{rate:e.target.value})}/></label></>:<label>ยอดเหมารวม {n}<input inputMode="decimal" className="block w-full border p-2" value={line.lumpSum} onChange={e=>edit(i,{lumpSum:e.target.value})}/></label>}
 <p className="sm:col-span-2">ตัวอย่างจากกติกาฉบับอนุมัติ: {linePreview(useRate?line.quantity:null,useRate?rate:null,useRate?null:line.lumpSum,adjustment&&line.sourceLineId!==null)??'ตรวจรูปแบบจำนวน/เงิน'} บาท · ยอดยืนยันใช้ผลเซิร์ฟเวอร์</p>
 {line.allocations.map((allocation,j)=><div key={j} className="grid gap-3 sm:col-span-2 sm:grid-cols-2"><label>เอกสารรายการ {n}{j>0?` ใบที่ ${j+1}`:''}<select aria-label={`เอกสารรายการ ${n}${j>0?` ใบที่ ${j+1}`:''}`} className="block w-full border p-2" value={allocation.documentId} onChange={e=>edit(i,{allocations:line.allocations.map((a,k)=>k===j?{...a,documentId:e.target.value}:a)})}><option value="">เลือกเอกสารของคำขอนี้</option>{documents.map(d=><option key={d.id} value={d.id}>{d.issuer} · {d.amount}</option>)}</select></label>
 <label>ยอดใช้เอกสาร {n}{j>0?` ใบที่ ${j+1}`:''}<input inputMode="decimal" className="block w-full border p-2" value={allocation.amount} onChange={e=>edit(i,{allocations:line.allocations.map((a,k)=>k===j?{...a,amount:e.target.value}:a)})}/></label><button className="underline text-left" type="button" onClick={()=>edit(i,{allocations:line.allocations.filter((_,k)=>k!==j)})}>นำเอกสารใบที่ {j+1} ออกจากรายการ {n}</button></div>)}
 <button type="button" className="underline text-left" onClick={()=>edit(i,{allocations:[...line.allocations,{documentId:'',amount:''}]})}>เพิ่มเอกสารรายการ {n}</button>
 <button type="button" className="underline text-left" disabled={lines.length===1} onClick={()=>setLines(rows=>rows.filter((_,j)=>j!==i))}>ลบรายการ {n}</button>
 </div>;})}
 <button type="button" className="underline" onClick={()=>setLines(rows=>[...rows,blank()])}>เพิ่มรายการ</button>
 <label className="block">เหตุผลรอบ<textarea className="block w-full border p-2" value={reason} onChange={e=>setReason(e.target.value)} maxLength={2000}/></label>
 {error&&<p role="alert">{error}</p>}<button type="button" className="rounded bg-orange-700 px-4 py-2 text-white" onClick={()=>void save()}>บันทึกรอบ</button>{onCancel&&<button type="button" className="ml-3 underline" onClick={onCancel}>ยกเลิกการแก้ไข</button>}
 </fieldset></section>;
}

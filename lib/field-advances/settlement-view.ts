import type { SettlementCommand, SettlementLine } from './settlement-types';
type PendingIdentity = Readonly<{ account:string; requestId:string; action:string; entityId:string|null; command:{commandId:string} }>;
export type PendingSettlement = PendingIdentity & ({body:unknown;command:SettlementCommand}|{body?:never;command:{commandId:string}});
function freeze<T>(value:T):T {
 if(value&&typeof value==='object'){Object.freeze(value);Object.values(value).forEach(freeze);}return value;
}
type MarkerStorage = Pick<Storage,'getItem'|'setItem'|'removeItem'|'length'|'key'>;
// Only operation identity persists. Original financial input exists exclusively in this account's memory.
export function createPendingStore(storage?:()=>MarkerStorage) {
 const entries=new Map<string,PendingSettlement>();
 const key=(account:string,request:string)=>'tpr10.settlement.pending.v1:'+account+':'+request;
 function current(account:string,requestId:string):PendingSettlement|null {
  const k=key(account,requestId), raw=storage?.().getItem(k);
  if(raw){
   const marker=JSON.parse(raw) as PendingIdentity;
   if(marker.account!==account||marker.requestId!==requestId||typeof marker.action!=='string'||typeof marker.command?.commandId!=='string'||!(marker.entityId===null||typeof marker.entityId==='string'))throw new Error('อ่านรหัสคำสั่งที่รอตรวจผลไม่ได้');
   const original=entries.get(k);
   return original?.command.commandId===marker.command.commandId?original:freeze({account,requestId,action:marker.action,entityId:marker.entityId,command:{commandId:marker.command.commandId}});
  }
  return entries.get(k)??null;
 }
 return {
  list(account:string):PendingSettlement[] {
   const prefix='tpr10.settlement.pending.v1:'+account+':';
   const requests=new Set<string>();
   for(const entry of entries.values()) {
    if(entry.account===account) requests.add(entry.requestId);
   }
   if(storage){
    const target=storage();
    for(let index=0;index<target.length;index++) {
     const markerKey=target.key(index);
     if(markerKey?.startsWith(prefix)) requests.add(markerKey.slice(prefix.length));
    }
   }
   return [...requests].map(requestId=>current(account,requestId)).filter(
    (value):value is PendingSettlement=>value!==null);
  },
  begin(account:string,requestId:string,action:string,body:unknown,command:SettlementCommand) {
   const k=key(account,requestId);if(current(account,requestId))throw new Error('รอตรวจสอบผลรายการ');
   const entityId=body&&typeof body==='object'&&'entityId'in body&&typeof body.entityId==='string'?body.entityId:null;
   const identity={account,requestId,action,entityId,command:{commandId:command.commandId}};
   const pending=freeze(JSON.parse(JSON.stringify({...identity,body,command})) as PendingSettlement);
   if(storage){const target=storage(),encoded=JSON.stringify(identity);target.setItem(k,encoded);if(target.getItem(k)!==encoded)throw new Error('บันทึกรหัสคำสั่งไม่ได้ จึงยังไม่ส่งรายการ');}
   entries.set(k,pending);return pending;
  },
  current,
  resolve(account:string,requestId:string,commandId:string){
   const k=key(account,requestId);if(current(account,requestId)?.command.commandId!==commandId)return;
   if(storage){const target=storage();target.removeItem(k);if(target.getItem(k)!==null)throw new Error('ยังล้างรหัสคำสั่งไม่ได้');}
   entries.delete(k);
  },
  clear(){entries.clear();}
 };
}
export const pendingSettlements=createPendingStore(()=>window.localStorage);
const maximumMoney = BigInt('999999999999999999');
function units(value: string, digits: number): bigint | null {
 if (!new RegExp('^\\d+(?:\\.\\d{1,'+digits+'})?$').test(value)) return null;
 const [whole, fraction=''] = value.split('.');
 return BigInt(whole)*BigInt(10)**BigInt(digits)+BigInt(fraction.padEnd(digits,'0'));
}
export function linePreview(quantity:string|null,rate:string|null,lump:string|null,adjustmentZero=false):string|null {
 let amount:bigint;
 if(lump!==null){
  const value=units(lump,2);
  if(quantity!==null||rate!==null||value===null||value<BigInt(adjustmentZero?0:1))return null;
  amount=value;
 }else{
  const q=quantity===null?null:units(quantity,4), r=rate===null?null:units(rate,2);
  if(q===null||r===null||q<BigInt(adjustmentZero?0:1)||r<=BigInt(0)||q>BigInt('99999999999999999999')||r>maximumMoney)return null;
  amount=(q*r+BigInt(5000))/BigInt(10000);
 }
 return amount>maximumMoney?null:(amount/BigInt(100))+'.'+(amount%BigInt(100)).toString().padStart(2,'0');
}
type DraftLine = Pick<SettlementLine,'sourceLineId'|'description'|'incurredOn'|'quantity'|'rate'|'lumpSum'|'allocations'>;
export function roundInputError(lines:DraftLine[],adjustment=false):string|null {
 let total=BigInt(0);
 if(lines.length===0||lines.length>1000)return 'จำนวนรายการไม่ถูกต้อง';
 for(const line of lines){
  const amount=linePreview(line.quantity,line.rate,line.lumpSum,adjustment&&line.sourceLineId!==null);
  if(!line.description.trim()||line.description.length>2000||!/^\d{4}-\d{2}-\d{2}$/.test(line.incurredOn)||amount===null||line.allocations.length>100)return 'ตรวจจำนวน อัตรา ยอดเงิน วันที่ และรายละเอียด';
  const ids=new Set<string>();let allocated=BigInt(0);
  for(const allocation of line.allocations){
   const value=units(allocation.amount,2);
   if(!allocation.documentId||ids.has(allocation.documentId)||value===null||value<=BigInt(0)||value>maximumMoney)return 'ยอดใช้เอกสารต้องมากกว่าศูนย์และห้ามซ้ำใบ';
   ids.add(allocation.documentId);allocated+=value;
  }
  const value=units(amount,2)!;
  if(allocated>value)return 'ยอดใช้เอกสารรวมเกินยอดรายการ';
  total+=value;if(total>maximumMoney)return 'ยอดรวมเกินขอบเขตที่รองรับ';
 }
 return null;
}
export const stateLabel=(s:string)=>({Draft:'ร่าง',AwaitingOwner:'รอเจ้าของยืนยัน',Returned:'ส่งกลับทั้งรอบ',Submitted:'ส่งตรวจแล้ว',Pending:'รอตรวจ',Accepted:'รับรองแล้ว',Cancelled:'ยกเลิกแล้ว',Approved:'อนุมัติคำขอแล้ว',Closed:'ปิดแล้ว'}[s]??s);
export const timeLabel=(value:boolean|null|undefined)=>value===true?'ส่งทันกำหนด':value===false?'ส่งหลังกำหนด':'ยังไม่มีผลประเมินเวลา';

export function editableLine(line: Pick<SettlementLine,'sourceLineId'|'categoryId'|'description'|'incurredOn'|'quantity'|'rate'|'lumpSum'|'allocations'>) {
 return {sourceLineId:line.sourceLineId,categoryId:line.categoryId,description:line.description,incurredOn:line.incurredOn,quantity:line.quantity??'',rate:line.rate??'',lumpSum:line.lumpSum??'',mode:line.lumpSum===null?'rate' as const:'lump' as const,allocations:line.allocations.map(a=>({...a}))};
}

export function documentInputError(input:{issuer:string;number:string|null;hasNoNumber:boolean;issuedOn:string;amount:string;evidenceIds:string[];evidenceType?:string|null},reason:string):string|null {
 if(!input.evidenceType?.trim())return 'กรุณาเลือกประเภทหลักฐานตามเอกสารจริง';
 if(!input.issuer.trim()||!reason.trim()||(!input.hasNoNumber&&!input.number?.trim())||!/^\d{4}-\d{2}-\d{2}$/.test(input.issuedOn)||!input.evidenceIds.length||linePreview(null,null,input.amount)===null||/^0(?:\.0+)?$/.test(input.amount))return 'กรอกผู้ออก เลขเอกสารหรือเลือกไม่มีเลข วันที่ ยอดมากกว่าศูนย์ไม่เกิน 2 ตำแหน่ง หลักฐาน และเหตุผล';
 return null;
}

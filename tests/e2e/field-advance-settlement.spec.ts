import { test, expect, type BrowserContext } from '@playwright/test';
import { createHash, randomBytes, randomUUID } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import { deflateSync } from 'node:zlib';
import { loadTs } from '../helpers/load-ts.mjs';
test.setTimeout(180000);
test.use({actionTimeout:15000,navigationTimeout:30000});
const workspace = (n: number) => `88100000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const project = (n: number) => `88200000-0000-4000-8000-${String(n).padStart(12, '0')}`;
const root = '/api/v1/field-advances';
function sql(input: string) {
  const database = process.env.TPR10_E2E_DATABASE;
  if (!database || !/^[a-f0-9]{64}$/.test(database)) throw new Error('ต้องใช้ disposable HTTPS fixture');
  return execFileSync('docker', ['exec', '-i', database, 'psql', '-U', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1'], { input, encoding: 'utf8' }).trim();
}
const user = (actor: number) => `88000000-0000-4000-8000-${String(1800 + actor).padStart(12, '0')}`;
async function session(context: BrowserContext, actor: number) {
  const id = user(actor), token = randomBytes(32), hash = createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO mfa_factors(id,user_id,protected_secret,confirmed_at_utc) SELECT gen_random_uuid(),'${id}','fixture-unused',now() WHERE NOT EXISTS (SELECT 1 FROM mfa_factors WHERE user_id='${id}');
  INSERT INTO sessions(id,user_id,token_hash,stage,created_at_utc,last_seen_at_utc,expires_at_utc,mfa_verified_at_utc) VALUES ('${randomUUID()}','${id}',decode('${hash}','hex'),'Active',now(),now(),now()+interval '1 hour',now());`);
  await context.addCookies([{ name: '__Host-tpr10_session', value: token.toString('base64url'), domain: 'localhost', path: '/', secure: true, httpOnly: true, sameSite: 'Lax' }]);
}
async function policy(owner: BrowserContext, reviewer: BrowserContext, n: number) {
  const base = '/api/v1/field-advances/policies';
  async function send(c: BrowserContext, path: string, body: unknown) {
    const { token } = await (await c.request.get('/api/v1/auth/csrf')).json();
    const r = await c.request.post(base + path, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': token }, data: body });
    expect(r.ok(), await r.text()).toBe(true); return r.json();
  }
  const options = { mode: 'Company', sourcePolicyRevisionId: null, baseCompanyVersionId: null, expectedActiveRevisionId: null, expectedActiveVersion: 0, expectedContentHash: null as string | null };
  const content = { clearingDays: 7, bands: [{ order: 1, levelCode: 'director', upperInclusive: '100000.00' }], categories: [{ id: '88300000-0000-4000-8000-000000000001', name: 'ค่าเดินทางจำลอง', active: true, unit: 'เที่ยว', standardRate: '100.00', evidenceTypes: ['ใบเสร็จ'] }] };
  let row = await send(owner, '', { scope: { workspaceId: workspace(n), projectId: null }, baseCompanyVersionId: null, content, meta: { commandId: randomUUID(), expectedVersion: 1, reason: 'จำลอง', policy: options } });
  options.expectedContentHash = row.contentHash;
  row = await send(owner, `/${row.id}/submit`, { commandId: randomUUID(), expectedVersion: row.version, reason: 'จำลอง', policy: options });
  await send(reviewer, `/${row.id}/review`, { decision: 'approve', meta: { commandId: randomUUID(), expectedVersion: row.version, reason: 'จำลอง', policy: options } });
}
async function send(c: BrowserContext, url: string, body: unknown) {
  const { token } = await (await c.request.get('/api/v1/auth/csrf')).json();
  const response = await c.request.post(url, { headers: { Origin: 'https://localhost:4443', 'X-CSRF-Token': token }, data: body });
  expect(response.status(), await response.text()).toBe(url === root + "/requests" || url.endsWith("/documents/create") ? 201 : 200); return response.json();
}
const receipt = { name: 'receipt.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGNgaPgPAAIDAYAkYfWXAAAAAElFTkSuQmCC', 'base64') };


function secondReceipt(){
 const crc=(data:Buffer)=>{let c=0xffffffff;for(const b of data){c^=b;for(let i=0;i<8;i++)c=(c>>>1)^((c&1)?0xedb88320:0);}return(c^0xffffffff)>>>0;};
 const chunk=(type:string,data:Buffer)=>{const name=Buffer.from(type),size=Buffer.alloc(4),sum=Buffer.alloc(4);size.writeUInt32BE(data.length);sum.writeUInt32BE(crc(Buffer.concat([name,data])));return Buffer.concat([size,name,data,sum]);};
 const header=Buffer.alloc(13);header.writeUInt32BE(1,0);header.writeUInt32BE(1,4);header[8]=8;header[9]=2;
 return {name:'second.png',mimeType:'image/png',buffer:Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]),chunk('IHDR',header),chunk('IDAT',deflateSync(Buffer.from([0,255,0,0]))),chunk('IEND',Buffer.alloc(0))])};
}
// Same synthetic Paid-fact arrangement as FieldAdvanceRefundTests.SeedPaid, disposable fixture only.
function seedPaid(id:string){
 const command=randomUUID(),installment=randomUUID(),revision=randomUUID(),attempt=randomUUID();
 sql(`BEGIN;
 INSERT INTO field_advance_command_receipts(command_id,workspace_id,actor_id,action,payload_sha256,status,result_json,created_at_utc) SELECT '${command}',workspace_id,'${user(4)}','payment.test',repeat('d',64),200,'{}',now() FROM field_advance_requests WHERE id='${id}';
 INSERT INTO field_advance_installments(id,workspace_id,project_id,family_id,request_id,state,created_by) SELECT '${installment}',workspace_id,project_id,family_id,id,'Paid','${user(4)}' FROM field_advance_requests WHERE id='${id}';
 INSERT INTO field_advance_installment_revisions(id,workspace_id,project_id,family_id,request_id,installment_id,revision_number,is_current,amount,channel,recipient_person_id,content_hash,reason,created_by) SELECT '${revision}',workspace_id,project_id,family_id,id,'${installment}',1,true,1000,'Cash',owner_person_id,repeat('a',64),'ข้อมูลจำลอง','${user(4)}' FROM field_advance_requests WHERE id='${id}';
 INSERT INTO field_advance_payment_attempts(id,workspace_id,project_id,family_id,request_id,installment_id,revision_id,command_id,payer_person_id,payer_id,state) SELECT '${attempt}',workspace_id,project_id,family_id,id,'${installment}','${revision}','${command}',(SELECT person_id FROM finance_account_links WHERE user_id='${user(4)}' AND workspace_id=r.workspace_id),'${user(4)}','Matched' FROM field_advance_requests r WHERE id='${id}';
 INSERT INTO field_advance_payment_results(id,workspace_id,project_id,family_id,request_id,installment_id,attempt_id,command_id,amount,recipient_person_id,paid_on,evidence_ids_json,recorded_by,recorded_by_person_id) SELECT gen_random_uuid(),workspace_id,project_id,family_id,id,'${installment}','${attempt}','${command}',1000,owner_person_id,'2026-10-30','[]','${user(4)}',(SELECT person_id FROM finance_account_links WHERE user_id='${user(4)}' AND workspace_id=r.workspace_id) FROM field_advance_requests r WHERE id='${id}';
 COMMIT;`);
}

function grantFinanceChecker(scopeNumber: number, actor: number) {
  const proposal = randomUUID();
  const review = randomUUID();
  sql(`INSERT INTO field_advance_grant_proposals(id,workspace_id,project_id,recipient_id,
       recipient_person_id,proposed_by,proposed_by_person_id,state,capabilities,
       delegable_capabilities,starts_at_utc,created_at_utc,reason)
       SELECT '${proposal}','${workspace(scopeNumber)}','${project(scopeNumber)}','${user(actor)}',
         recipient.person_id,'${user(1)}',proposer.person_id,'Approved',ARRAY['finance.check'],
         ARRAY[]::text[],now()-interval '1 hour',now()-interval '1 hour','สิทธิ์ตรวจ fixture'
       FROM finance_account_links recipient CROSS JOIN finance_account_links proposer
       WHERE recipient.user_id='${user(actor)}' AND proposer.user_id='${user(1)}'
         AND recipient.workspace_id='${workspace(scopeNumber)}'
         AND proposer.workspace_id=recipient.workspace_id;
       INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,
         reviewer_person_id,outcome,created_at_utc,reason)
       SELECT '${review}','${workspace(scopeNumber)}','${proposal}','${user(2)}',person_id,
         'Approved',now()-interval '1 hour','ตรวจสิทธิ์ fixture'
       FROM finance_account_links WHERE user_id='${user(2)}' AND workspace_id='${workspace(scopeNumber)}';
       INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,
         review_id,created_by,created_at_utc,reason)
       VALUES (gen_random_uuid(),'${workspace(scopeNumber)}','${project(scopeNumber)}',
         '${proposal}','${review}','${user(2)}',now()-interval '1 hour','มอบหมาย fixture');`);
}

async function prepare(context: BrowserContext, browser: import('@playwright/test').Browser) {
 const approvers = await Promise.all([2,3,5].map(async actor=>{const c=await browser.newContext(); await session(c,actor);return c;}));
 await session(context,1);
 await policy(context,approvers[0],18);
 const person=sql(`SELECT person_id FROM finance_account_links WHERE user_id='${user(1)}';`);
 const content={kind:'Individual',purpose:'เคลียร์จำลอง Task16',fieldStart:'2026-10-30',fieldEnd:'2026-10-31',neededOn:'2026-10-30',teamPersonIds:[],lines:[{id:randomUUID(),categoryId:'88300000-0000-4000-8000-000000000001',description:'เงินจำลอง',mode:'QuantityRate',quantity:'100',unitRate:'100.00',lumpSum:null}]};
 let request=await send(context,root+'/requests',{input:{scope:{workspaceId:workspace(18),projectId:project(18)},ownerPersonId:person,content},meta:{commandId:randomUUID(),expectedVersion:0,reason:'จำลอง'}});
 const quote=await (await context.request.get(`${root}/requests/${request.id}/quote`)).json();
 request=await send(context,`${root}/requests/${request.id}/confirm`,{confirmationHash:quote.confirmationHash,meta:{commandId:randomUUID(),expectedVersion:request.version,reason:'จำลอง'}});
 for(const c of approvers){const current=await(await c.request.get(`${root}/requests/${request.id}`)).json();request=await send(c,`${root}/requests/${request.id}/review`,{decision:{outcome:'Approve',stepId:current.currentStep.id},meta:{commandId:randomUUID(),expectedVersion:current.version,reason:'จำลอง'}});await c.close();}
 return request.id as string;
}
test('response_lost_keeps_same_command_without_automatic_resend',async({page,context,browser})=>{
 let summaryReads=0;
 page.on('response',async response=>{if(/\/settlement$/.test(new URL(response.url()).pathname)&&response.status()===200)summaryReads++;});
 page.on('response',async response=>{const url=new URL(response.url());if(url.pathname.includes('/settlement'))console.log('settlement-response',url.pathname.replace(/requests\/[^/]+/,'requests/[id]'),response.status());});
 const id=await prepare(context,browser);seedPaid(id);grantFinanceChecker(18,7);
 const csrf=await(await context.request.get('/api/v1/auth/csrf')).json();
 const uploaded=await context.request.post(`${root}/requests/${id}/settlement/evidence`,{headers:{Origin:'https://localhost:4443','X-CSRF-Token':csrf.token,'Content-Type':'image/png'},data:receipt.buffer});
 expect(uploaded.status(),await uploaded.text()).toBe(201);const proof=await uploaded.json();
 const summary=await(await context.request.get(`${root}/requests/${id}/settlement`)).json();
 const doc=await send(context,`${root}/requests/${id}/settlement/documents/create`,{input:{issuer:'เอกสารตั้งต้นจำลอง',number:null,hasNoNumber:true,issuedOn:'2026-10-31',amount:'1000.00',evidenceIds:[proof.id]},meta:{commandId:randomUUID(),requestVersion:summary.requestVersion,familyVersion:summary.familyVersion,entityVersion:null,reason:'จำลอง'}});
 const transport:typeof fetch=async(url)=>{const res=await context.request.get(String(url));return new Response(await res.text(),{status:res.status()});};
 const sc=(loadTs('lib/field-advances/settlement-client.ts') as typeof import('../../lib/field-advances/settlement-client')).createSettlementClient(user(1),transport);
 for(const collection of ['rounds','refunds','documents','completion'])await test.step('typed read '+collection,()=>sc.listSettlement(id,collection));
 await test.step('typed family',()=>sc.loadSettlementFamily(id));
 const rc=(loadTs('lib/field-advances/request-client.ts') as typeof import('../../lib/field-advances/request-client')).createRequestClient(user(1),transport);
 await test.step('approved policy snapshot',async()=>rc.screen(await rc.details(id)));
 await page.goto(`/portal/field-advances/${id}/settlement`);
 await expect(page.getByRole('heading',{name:'เคลียร์ค่าใช้จ่ายและคืนเงิน',exact:true})).toBeVisible();
 await test.step('repair_saved_untyped_document_without_reupload',async()=>{
  await page.getByRole('button',{name:'แก้ไขเอกสาร',exact:true}).click();
  await expect(page.getByLabel('ประเภทหลักฐาน',{exact:true})).toHaveValue('');
  await page.getByLabel('ประเภทหลักฐาน',{exact:true}).selectOption('ใบเสร็จ');
  await page.getByLabel('เหตุผลเอกสาร',{exact:true}).fill('ระบุชนิดเอกสารเดิมตามจริง');
  await page.getByRole('button',{name:'บันทึกการแก้ไขเอกสาร',exact:true}).click();
  await expect(page.getByRole('button',{name:'บันทึกเอกสาร',exact:true})).toBeVisible();
  const repaired=(await sc.listSettlement(id,'documents')).items.find(r=>r.id===doc.entityId)!;
  expect(repaired.document?.evidenceType).toBe('ใบเสร็จ');
  expect(repaired.document?.evidenceIds).toEqual([proof.id]);
 });


 await test.step('document_update_lost_response_exact_receipt_then_current_version_edit',async()=>{
  const before=(await sc.listSettlement(id,'documents')).items.find(r=>r.id===doc.entityId)!;
  const updatePath=`**/requests/${id}/settlement/documents/${doc.entityId}/update`;
  const updates:{meta:{commandId:string;entityVersion:number;requestVersion:number;familyVersion:number}}[]=[];
  await page.route(updatePath,async route=>{
   updates.push(route.request().postDataJSON());
   if(updates.length===1){
    const committed=await route.fetch();expect(committed.status(),await committed.text()).toBe(200);
    await route.abort('failed');
   }else await route.continue();
  });
  await page.getByRole('button',{name:'แก้ไขเอกสาร',exact:true}).click();
  await page.getByLabel('ผู้ออกเอกสาร',{exact:true}).fill('เอกสารหลังผลตอบกลับหาย');
  await page.getByLabel('เหตุผลเอกสาร',{exact:true}).fill('แก้ครั้งแรกก่อนตรวจ receipt');
  await page.getByRole('button',{name:'บันทึกการแก้ไขเอกสาร',exact:true}).click();
  await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
  expect(updates).toHaveLength(1);expect(updates[0].meta.entityVersion).toBe(before.version);
  const committed=(await sc.listSettlement(id,'documents')).items.find(r=>r.id===doc.entityId)!;
  expect(committed.version).toBe(before.version+1);
  expect(committed.document?.issuer).toBe('เอกสารหลังผลตอบกลับหาย');
  await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
  await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'บันทึกการแก้ไขเอกสาร',exact:true})).toBeDisabled();
  expect(updates).toHaveLength(1);
  const recovered=page.waitForResponse(response=>new URL(response.url()).pathname.endsWith(`/commands/${updates[0].meta.commandId}/receipt`));
  await page.getByRole('button',{name:'ตรวจผลจากรหัสคำสั่ง',exact:true}).click();
  const response=await recovered;expect(response.status(),await response.text()).toBe(200);
  const query=new URL(response.url()).searchParams;
  expect(query.get('operation')).toBe('documents/update');expect(query.get('entityId')).toBe(doc.entityId);
  expect((await response.json()).commandId).toBe(updates[0].meta.commandId);
  await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toHaveCount(0);
  await expect(page.getByRole('button',{name:'บันทึกการแก้ไขเอกสาร',exact:true})).toHaveCount(0);
  await expect(page.getByRole('button',{name:'บันทึกเอกสาร',exact:true})).toBeEnabled();
  for(const label of ['ผู้ออกเอกสาร','เลขเอกสาร','วันที่เอกสาร','ยอดเอกสาร','เหตุผลเอกสาร','ประเภทหลักฐาน'])
   await expect(page.getByLabel(label,{exact:true})).toHaveValue('');
  await expect(page.getByLabel('ไม่มีเลขเอกสารจริง',{exact:true})).not.toBeChecked();
  await expect(page.getByText('หลักฐานพร้อมใช้ 0 ไฟล์',{exact:true})).toBeVisible();
  expect(updates).toHaveLength(1); // Receipt lookup, never a replay POST.
  const current=await sc.loadSettlement(id);
  await page.getByRole('button',{name:'แก้ไขเอกสาร',exact:true}).click();
  await expect(page.getByLabel('ผู้ออกเอกสาร',{exact:true})).toHaveValue('เอกสารหลังผลตอบกลับหาย');
  await page.getByLabel('ผู้ออกเอกสาร',{exact:true}).fill('เอกสารแก้ครั้งถัดไปสำเร็จ');
  await page.getByLabel('เหตุผลเอกสาร',{exact:true}).fill('แก้จากเวอร์ชันปัจจุบันหลัง receipt');
  const next=page.waitForResponse(r=>r.url().endsWith(`/documents/${doc.entityId}/update`)&&r.request().method()==='POST');
  await page.getByRole('button',{name:'บันทึกการแก้ไขเอกสาร',exact:true}).click();
  const accepted=await next;expect(accepted.status(),await accepted.text()).toBe(200);
  expect(updates).toHaveLength(2);
  expect(updates[1].meta.commandId).not.toBe(updates[0].meta.commandId);
  expect(updates[1].meta.entityVersion).toBe(committed.version);
  expect(updates[1].meta.requestVersion).toBe(current.requestVersion);
  expect(updates[1].meta.familyVersion).toBe(current.familyVersion);
  await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toHaveCount(0);
  await expect(page.getByRole('button',{name:'บันทึกเอกสาร',exact:true})).toBeEnabled();
  const after=(await sc.listSettlement(id,'documents')).items.find(r=>r.id===doc.entityId)!;
  expect(after.version).toBe(committed.version+1);
  expect(after.document?.issuer).toBe('เอกสารแก้ครั้งถัดไปสำเร็จ');
  expect(after.document?.evidenceIds).toEqual([proof.id]);
  expect(after.document?.evidenceType).toBe('ใบเสร็จ');
  await page.unroute(updatePath);
 });

 const uniqueCommandIds=new Set<string>(); const payloads:string[]=[]; let automaticRetryPosts=0, manual=false;
 await page.route(`**/requests/${id}/settlement/rounds/create`,async route=>{
  const body=route.request().postData()!;payloads.push(body);uniqueCommandIds.add(JSON.parse(body).meta.commandId);
  if(payloads.length>1&&!manual) automaticRetryPosts++;
  if(payloads.length===1||payloads.length===4){const response=await route.fetch();expect(response.status()).toBe(201);await route.abort('failed');}
  else if(payloads.length===2)await route.fulfill({status:403,contentType:'application/json',body:'{}'});
  else await route.continue();
 });
 await page.getByLabel('รายละเอียดรายการ 1',{exact:true}).fill('ค่าเดินทางรอบแรก');
 await page.getByLabel('วันที่รายการ 1',{exact:true}).fill('2026-10-31');
 await page.getByLabel('จำนวน 1',{exact:true}).fill('1.0001');
 await page.getByLabel('เอกสารรายการ 1',{exact:true}).selectOption(doc.entityId);
 await page.getByLabel('ยอดใช้เอกสาร 1',{exact:true}).fill('100.01');
 await page.getByLabel('เหตุผลรอบ',{exact:true}).fill('บันทึกค่าเดินทางจำลอง');
 await test.step('invalid_round_inputs_remain_editable_without_post_or_pending',async()=>{
  for(const [quantity,allocation] of [['0','0'],['00.00','00.00'],['10000000000000000','1'],['1','100.01'],['1','00.00']]){
   await page.getByLabel('จำนวน 1',{exact:true}).fill(quantity);await page.getByLabel('ยอดใช้เอกสาร 1',{exact:true}).fill(allocation);
   await page.getByRole('button',{name:'บันทึกรอบ',exact:true}).click();
   await expect(page.locator('p[role=alert]')).toBeVisible();await expect(page.getByLabel('จำนวน 1',{exact:true})).toBeEditable();
   expect(payloads).toHaveLength(0);await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toHaveCount(0);
   expect(await page.evaluate(()=>Object.keys(localStorage).filter(k=>k.startsWith('tpr10.settlement.pending.')))).toEqual([]);
  }
  await page.getByLabel('จำนวน 1',{exact:true}).fill('1.0001');await page.getByLabel('ยอดใช้เอกสาร 1',{exact:true}).fill('100.01');
 });
 await test.step('storage_failure_does_not_dispatch',async()=>{
  await page.evaluate(()=>{const original=Storage.prototype.setItem;Object.defineProperty(window,'t16RestoreStorage',{value:()=>{Storage.prototype.setItem=original;},configurable:true});Storage.prototype.setItem=function(k,v){if(k.startsWith('tpr10.settlement.pending.'))throw new Error('storage unavailable');original.call(this,k,v);};});
  await page.getByRole('button',{name:'บันทึกรอบ',exact:true}).click();
  await expect(page.locator('p[role=alert]')).toContainText('storage unavailable');expect(payloads).toHaveLength(0);
  await expect(page.getByLabel('จำนวน 1',{exact:true})).toBeEditable();
  await page.evaluate(()=>{(window as unknown as {t16RestoreStorage:()=>void}).t16RestoreStorage();});
 });
 await page.getByRole('button',{name:'บันทึกรอบ',exact:true}).click();
 await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
 // Refresh reads must neither replay nor treat a changed summary as the receipt.
 await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
 await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
 expect(uniqueCommandIds.size).toBe(1);expect(automaticRetryPosts).toBe(0);expect(payloads).toHaveLength(1);
 await expect(page.getByRole('button',{name:'บันทึกรอบ',exact:true})).toBeDisabled();
 // A failed preflight on an explicit retry says nothing about the original committed/lost POST.
 manual=true;
 await page.route('**/api/v1/auth/csrf',route=>route.fulfill({status:403,contentType:'application/json',body:'{}'}));
 await page.getByRole('button',{name:'ตรวจผลโดยส่งคำสั่งเดิมอีกครั้ง',exact:true}).click();
 await expect(page.getByRole('region',{name:'สถานะเวลาและยอดเงิน'})).toHaveCount(0);
 expect(payloads).toHaveLength(1);
 await page.unroute('**/api/v1/auth/csrf');
 await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
 await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
 await page.getByRole('button',{name:'ตรวจผลโดยส่งคำสั่งเดิมอีกครั้ง',exact:true}).click();
 await expect(page.getByRole('region',{name:'สถานะเวลาและยอดเงิน'})).toHaveCount(0);
 await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
 await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
 expect(payloads).toHaveLength(2);
 const replayResponse=page.waitForResponse(r=>r.url().endsWith('/rounds/create')&&r.status()===201);
 await page.getByRole('button',{name:'ตรวจผลโดยส่งคำสั่งเดิมอีกครั้ง',exact:true}).click();
 await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toHaveCount(0);
 await replayResponse;
 expect(payloads).toHaveLength(3);expect(payloads[2]).toBe(payloads[0]);expect(payloads[1]).toBe(payloads[0]);expect(uniqueCommandIds.size).toBe(1);
 await expect(page.getByText('ค่าเดินทางรอบแรก',{exact:true})).toBeVisible();
 await page.getByRole('button',{name:'ทบทวนเพื่อยืนยันรอบ',exact:true}).click();
 await expect(page.getByText('ยอดยืนยันจากเซิร์ฟเวอร์: 100.01 บาท',{exact:true})).toBeVisible();
 await page.getByRole('button',{name:'ปิดตัวอย่าง',exact:true}).click();
 // Second independent round; the first remains visible.
 await page.getByLabel('รายละเอียดรายการ 1',{exact:true}).fill('ค่าเดินทางรอบสอง');
 await page.getByLabel('วันที่รายการ 1',{exact:true}).fill('2026-10-31');
 await page.getByLabel('เอกสารรายการ 1',{exact:true}).selectOption(doc.entityId);
 await page.getByLabel('เหตุผลรอบ',{exact:true}).fill('รอบใหม่หลังตรวจผลแล้ว');
 await page.getByLabel('จำนวน 1',{exact:true}).fill('2');
 await page.getByLabel('ยอดใช้เอกสาร 1',{exact:true}).fill('200');
 await page.getByRole('button',{name:'บันทึกรอบ',exact:true}).click();
 await test.step('committed_lost_response_full_reload_uses_only_explicit_receipt_get',async()=>{
  await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
  const original=JSON.parse(payloads[3]).meta.commandId;
  const markers=await page.evaluate(()=>Object.keys(localStorage).filter(k=>k.startsWith('tpr10.settlement.pending.')).map(k=>JSON.parse(localStorage.getItem(k)!)));
  expect(markers).toHaveLength(1);expect(markers[0]).toEqual({account:user(1),requestId:id,action:'rounds/create',entityId:null,command:{commandId:original}});
  page.once('dialog',dialog=>dialog.accept());await page.reload();
  await expect(page.getByText(`รหัสคำสั่ง ${original}`,{exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'บันทึกรอบ',exact:true})).toBeDisabled();
  await expect(page.getByRole('button',{name:'ตรวจผลโดยส่งคำสั่งเดิมอีกครั้ง',exact:true})).toHaveCount(0);
  expect(payloads).toHaveLength(4);
  const lookup=`**/requests/${id}/settlement/commands/${original}/receipt?*`;
  await page.route(lookup,route=>route.fulfill({status:404,contentType:'application/json',body:'{}'}));
  await page.getByRole('button',{name:'ตรวจผลจากรหัสคำสั่ง',exact:true}).click();
  await expect(page.locator('p[role=alert]')).toBeVisible();await expect(page.getByText(`รหัสคำสั่ง ${original}`,{exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'บันทึกรอบ',exact:true})).toBeDisabled();expect(payloads).toHaveLength(4);
  await page.unroute(lookup);
  await page.getByRole('button',{name:'ตรวจผลจากรหัสคำสั่ง',exact:true}).click();
  await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toHaveCount(0);
  await expect(page.getByRole('button',{name:'บันทึกรอบ',exact:true})).toBeEnabled();expect(payloads).toHaveLength(4);
 });
 await expect(page.getByText('ค่าเดินทางรอบสอง',{exact:true})).toBeVisible();
 await expect(page.getByText('ค่าเดินทางรอบแรก',{exact:true})).toBeVisible();
 // Real private evidence upload from file, no live-camera requirement.
 const readsBeforeUpload=summaryReads;
 await page.getByLabel('แนบรูปหรือ PDF',{exact:true}).setInputFiles(secondReceipt());
 await expect(page.getByText('หลักฐานพร้อมใช้ 1 ไฟล์',{exact:true})).toBeVisible();
 expect(summaryReads).toBeGreaterThan(readsBeforeUpload);
 expect(await page.getByLabel('แนบรูปหรือ PDF',{exact:true}).getAttribute('capture')).toBeNull();
 await expect(page.getByLabel('แนบรูปหรือ PDF',{exact:true})).toHaveAttribute('accept','image/png,image/jpeg,application/pdf');
 await page.getByLabel('ประเภทหลักฐาน',{exact:true}).selectOption('ใบเสร็จ');
 await page.getByLabel('ผู้ออกเอกสาร',{exact:true}).fill('ร้านจำลอง');
 await page.getByLabel('ไม่มีเลขเอกสารจริง',{exact:true}).check();
 await page.getByLabel('วันที่เอกสาร',{exact:true}).fill('2026-10-31');
 await page.getByLabel('ยอดเอกสาร',{exact:true}).fill('300');
 await page.getByLabel('เหตุผลเอกสาร',{exact:true}).fill('หลักฐานจำลอง');
 await page.getByRole('button',{name:'บันทึกเอกสาร',exact:true}).click();
 await expect(page.getByText('ร้านจำลอง',{exact:true})).toBeVisible();
 await test.step('employee_uploaded_document_reaches_positive_owner_submission',async()=>{
  const docs=await sc.listSettlement(id,'documents');
  const uploadedDoc=docs.items.find(r=>r.document?.issuer==='ร้านจำลอง')!;
  expect(uploadedDoc.document?.evidenceType).toBe('ใบเสร็จ');
  await page.getByLabel('รายละเอียดรายการ 1',{exact:true}).fill('รอบหลักฐานที่เลือกชนิดแล้ว');
  await page.getByLabel('วันที่รายการ 1',{exact:true}).fill('2026-10-31');
  await page.getByLabel('จำนวน 1',{exact:true}).fill('1');
  await page.getByLabel('เอกสารรายการ 1',{exact:true}).selectOption(uploadedDoc.id);
  await page.getByLabel('ยอดใช้เอกสาร 1',{exact:true}).fill('100');
  await page.getByLabel('เหตุผลรอบ',{exact:true}).fill('ทดสอบเส้นทางเอกสารจริง');
  await page.getByRole('button',{name:'บันทึกรอบ',exact:true}).click();
  const article=page.locator('article').filter({hasText:'รอบหลักฐานที่เลือกชนิดแล้ว'});
  await article.getByRole('button',{name:'ทบทวนเพื่อยืนยันรอบ',exact:true}).click();
  await page.getByLabel('เหตุผลยืนยันรอบ',{exact:true}).fill('เจ้าของตรวจหลักฐานแล้ว');
  const submitted=page.waitForResponse(r=>r.url().endsWith('/submit')&&r.request().method()==='POST');
  await page.getByRole('button',{name:'ยืนยันฉบับนี้และส่งตรวจ',exact:true}).click();
  const submitResponse=await submitted;expect(submitResponse.status(),await submitResponse.text()).toBe(200);
  await expect(article.getByRole('button',{name:'ทบทวนเพื่อยืนยันรอบ',exact:true})).toHaveCount(0);
 });

 await test.step('returned_round_projection_shows_shared_reason_on_affected_lines',async()=>{
  const list=await sc.listSettlement(id,'rounds'),row=list.items[0];const revision=row.round!.revisionId!;
  const path=`**/requests/${id}/settlement/rounds/${row.id}/revisions/${revision}`;
  await page.route(path,async route=>{const res=await route.fetch(),wire=await res.json();await route.fulfill({response:res,json:{...wire,state:'Returned',allowedActions:['round-draft'],returnReview:{revisionId:revision,stepId:randomUUID(),stepVersion:1,reason:'ตรวจวันที่และหลักฐานทั้งรอบ',affectedLineIds:wire.lines.map((l:{id:string})=>l.id),returnedAtUtc:'2026-11-01T00:00:00Z',correctionDueOn:'2026-11-04'}}});});
  await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
  await expect(page.getByText('ส่งกลับทั้งรอบ: ตรวจวันที่และหลักฐานทั้งรอบ',{exact:true})).toBeVisible();
  await expect(page.getByText('— ต้องแก้ตามเหตุผลส่งกลับ: ตรวจวันที่และหลักฐานทั้งรอบ',{exact:true})).toBeVisible();
  await page.unroute(path);await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
  await expect(page.getByText('ส่งกลับทั้งรอบ: ตรวจวันที่และหลักฐานทั้งรอบ',{exact:true})).toHaveCount(0);
 });
 await test.step('refund_pending_is_not_confirmed_and_money_is_strict',async()=>{
  await page.getByLabel('ยอดคืน (บาท)',{exact:true}).fill('100.001');
  await page.getByLabel('วันที่คืนเงิน',{exact:true}).fill('2026-10-31');
  await page.getByLabel('ปลายทางหรือผู้รับของบริษัท',{exact:true}).fill('บัญชีบริษัทจำลอง');
  await page.getByLabel('เหตุผลคืนเงิน',{exact:true}).fill('คืนเงินจำลอง');
  await page.getByLabel('หลักฐานคืนเงิน รูปหรือ PDF',{exact:true}).setInputFiles(receipt);
  await expect(page.getByText('หลักฐานคืนเงิน 1 ไฟล์',{exact:true})).toBeVisible();
  await page.getByRole('button',{name:'บันทึกคืนเงิน',exact:true}).click();
  await expect(page.getByText('กรอกยอดคืนมากกว่าศูนย์ไม่เกิน 2 ตำแหน่ง วันที่ ผู้รับบริษัท เหตุผล และหลักฐาน',{exact:true})).toBeVisible();
  for(const amount of ['00.00','10000000000000000']){
   await page.getByLabel('ยอดคืน (บาท)',{exact:true}).fill(amount);await page.getByRole('button',{name:'บันทึกคืนเงิน',exact:true}).click();
   await expect(page.getByLabel('ยอดคืน (บาท)',{exact:true})).toBeEditable();await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toHaveCount(0);
  }
  await page.getByLabel('ยอดคืน (บาท)',{exact:true}).fill('100.00');
  await page.getByRole('button',{name:'บันทึกคืนเงิน',exact:true}).click();
  await expect(page.getByRole('button',{name:'ยืนยันคืนเงินรายการนี้',exact:true})).toBeVisible();
  await page.getByLabel('เหตุผลส่งครบ ถอน หรือยืนยันรายการคืน',{exact:true}).fill('เจ้าของยืนยันหลักฐานคืนเงิน');
  await page.getByRole('button',{name:'ยืนยันคืนเงินรายการนี้',exact:true}).click();
  await expect(page.getByRole('button',{name:'ยืนยันคืนเงินรายการนี้',exact:true})).toHaveCount(0);
  const status=page.getByRole('region',{name:'สถานะเวลาและยอดเงิน'});
  await expect(status.locator('dl > div').filter({hasText:'คืนเงินยืนยันแล้ว'})).toContainText('0.00 บาท');
  await expect(status.locator('dl > div').filter({hasText:'คืนเงินรอตรวจ'})).toContainText('100.00 บาท');
  await expect(status.locator('dl > div').filter({hasText:'ยอดคงเหลือที่เจ้าของต้องสะสาง'})).toContainText('1000.00 บาท');
 });
 await test.step('completion_declaration_and_withdrawal_require_reason',async()=>{
  const manager=await browser.newContext();await session(manager,3);
  const current=await sc.loadSettlement(id);
  await send(manager,`${root}/requests/${id}/settlement/completion/confirm`,{input:{actualEnd:'2026-10-31'},meta:{commandId:randomUUID(),requestVersion:current.requestVersion,familyVersion:current.familyVersion,entityVersion:null,reason:'ผู้จัดการยืนยันจบงานจำลอง'}});await manager.close();
  await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
  await expect(page.getByRole('button',{name:'ยืนยันส่งครบ',exact:true})).toBeVisible();
  await page.getByLabel('เหตุผลส่งครบ ถอน หรือยืนยันรายการคืน',{exact:true}).fill('');
  await page.getByRole('button',{name:'ยืนยันส่งครบ',exact:true}).click();
  await expect(page.getByText('กรุณาระบุเหตุผล',{exact:true})).toBeVisible();
  await page.getByLabel('เหตุผลส่งครบ ถอน หรือยืนยันรายการคืน',{exact:true}).fill('ส่งเอกสารครบแล้ว');
  await page.getByRole('button',{name:'ยืนยันส่งครบ',exact:true}).click();
  await expect(page.getByText('ส่งครบแล้ว รอตรวจรับรอง',{exact:true})).toBeVisible();
  await expect(page.getByRole('button',{name:'บันทึกรอบ',exact:true})).toHaveCount(0);
  await page.getByLabel('เหตุผลส่งครบ ถอน หรือยืนยันรายการคืน',{exact:true}).fill('พบรายการต้องส่งเพิ่ม');
  await page.getByRole('button',{name:'ถอนการยืนยันส่งครบ',exact:true}).click();
  await expect(page.getByRole('button',{name:'บันทึกรอบ',exact:true})).toBeVisible();
 });
 const evidence='.superpowers/sdd/2026-10-31-module-7d-settlement-refunds/task-16-evidence';mkdirSync(evidence,{recursive:true});
 for(const width of [390,1280]){await page.setViewportSize({width,height:900});await expect.poll(()=>page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);await page.screenshot({path:`${evidence}/fix1-viewport-${process.env.TPR10_T16_PORT??'https'}-${width}.png`,fullPage:true});}
 expect(await page.evaluate(()=>Object.keys(localStorage).filter(k=>/settlement|refund/i.test(k)))).toEqual([]);
 await page.route(`**/requests/${id}/settlement`,route=>route.fulfill({status:403,contentType:'application/json',body:'{}'}));
 await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
 await expect(page.getByText('ค่าเดินทางรอบแรก',{exact:true})).toHaveCount(0);
 await expect(page.getByText('ร้านจำลอง',{exact:true})).toHaveCount(0);
 await page.unroute(`**/requests/${id}/settlement`);
 await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
 await expect(page.getByText('ค่าเดินทางรอบแรก',{exact:true})).toBeVisible();
 const previewResponse=page.waitForResponse(r=>r.url().endsWith('/preview'));
 await page.getByRole('button',{name:'ทบทวนเพื่อยืนยันรอบ',exact:true}).first().click();
 const confirmation=await(await previewResponse).json();
 let newUnknownPosts=0;
 await page.route(`**/requests/${id}/settlement/rounds/${confirmation.roundId}/submit`,async route=>{
  newUnknownPosts++;const sent=route.request().postDataJSON();
  expect(sent.meta.confirmationHash).toBe(confirmation.confirmationHash);
  expect(sent.meta.entityVersion).toBe(confirmation.revisionVersion);
  expect(sent.meta.requestVersion).toBe(confirmation.requestVersion);expect(sent.meta.familyVersion).toBe(confirmation.familyVersion);
  expect(sent.input).toEqual({});await route.abort('failed');
 });
 await page.getByLabel('เหตุผลยืนยันรอบ',{exact:true}).fill('เจ้าของยืนยันฉบับที่ทบทวน');
 await page.getByRole('button',{name:'ยืนยันฉบับนี้และส่งตรวจ',exact:true}).click();
 await expect(page.getByText('รอตรวจสอบผลรายการ',{exact:true})).toBeVisible();
 await expect.poll(()=>newUnknownPosts).toBe(1);
 await session(context,4);
 await page.getByRole('button',{name:'โหลดข้อมูลล่าสุด',exact:true}).click();
 await expect(page.getByText('ค่าเดินทางรอบแรก',{exact:true})).toHaveCount(0);
 expect(automaticRetryPosts).toBe(0);expect(newUnknownPosts).toBe(1);
 await expect(page.getByRole('button',{name:'ตรวจผลโดยส่งคำสั่งเดิมอีกครั้ง',exact:true})).toHaveCount(0);

});

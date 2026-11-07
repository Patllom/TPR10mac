import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const api = existsSync('lib/field-advances/settlement-client.ts') ? loadTs('lib/field-advances/settlement-client.ts') : {};
const user = '11111111-1111-4111-8111-111111111111', id = '22222222-2222-4222-8222-222222222222';
const meta = { commandId: user, requestVersion: 1, familyVersion: 1, entityVersion: null, reason: 'ทดสอบ' };
const row = { requestId: id, state: 'Approved', requestVersion: 1, familyVersion: 1, holds: [], allowedActions: ['read'], totals: { ownerReceived: '9999999999999999.99', approvedExpense: '0.00', confirmedRefund: '0.00', pendingRefund: '0.00', ownerOutstanding: '9999999999999999.99', wrongRecipientOutstanding: '0.00', hasOpenCase: false } };
function client(handler, account = () => user) {
 assert.equal(typeof api.createSettlementClient, 'function');
 return api.createSettlementClient(user, async (url, options) => url.endsWith('/session') ? Response.json({ userId: account(), stage: 'Active', permissions: [], mfaVerifiedAtUtc: null }) : url.endsWith('/csrf') ? Response.json({ token: 'csrf' }) : handler(url, options));
}
test('summary uses protected transport and exact money strings', async () => {
 const calls=[]; const c=client(async (url,o)=>{calls.push([url,o]);return Response.json(row);});
 assert.deepEqual(await c.loadSettlement(id),row); assert.equal(calls[0][0],`/api/v1/field-advances/requests/${id}/settlement`);
 assert.equal(calls[0][1].cache,'no-store');assert.equal(calls[0][1].redirect,'error');
});
test('unknown routes, internal close, actor and numeric money never send',async()=>{
 let calls=0; const c=client(async()=>{calls++;return Response.json(row);});
 for(const [action,body] of [['close',{}],['../refunds',{}],['rounds/create',{lines:[],actor:user}],['refunds/create',{amount:10}]]) await assert.rejects(()=>c.sendSettlementCommand(id,action,body,meta));
 assert.equal(calls,0);
});
test('post-dispatch account change and foreign response cannot escape',async()=>{
 let changed=false;const c=client(async()=>{changed=true;return Response.json(row);},()=>changed?id:user);
 await assert.rejects(()=>c.loadSettlement(id),e=>e.status===403&&e.clearProtectedData);
 await assert.rejects(()=>client(async()=>Response.json({...row,requestId:user})).loadSettlement(id),e=>e.status===403);
});
test('invalid wire money is rejected',async()=>{
 await assert.rejects(()=>client(async()=>Response.json({...row,totals:{...row.totals,ownerReceived:1}})).loadSettlement(id));
});
test('cancellation before send does not dispatch; unknown result never retries',async()=>{
 let calls=0;const c=client(async()=>{calls++;throw new Error('network');});
 const abort=new AbortController();abort.abort();await assert.rejects(()=>c.loadSettlement(id,abort.signal));assert.equal(calls,0);
 await assert.rejects(()=>c.sendSettlementCommand(id,'declaration',{complete:true},meta),e=>e.unknownResult&&e.commandId===user);assert.equal(calls,1);
});

test('malformed successful command response preserves unknown outcome and command id', async () => {
 const c=client(async()=>Response.json(null));
 await assert.rejects(()=>c.sendSettlementCommand(id,'declaration',{complete:true},meta), e=>e.unknownResult && e.commandId===user);
});
test('cancellation after dispatch remains unknown and does not retry', async () => {
 const abort=new AbortController();let calls=0;
 const c=client(async()=>{calls++;abort.abort();return Response.json({requestId:id,entityId:user,state:'Draft',requestVersion:2,familyVersion:2,entityVersion:1});});
 await assert.rejects(()=>c.sendSettlementCommand(id,'declaration',{complete:true},meta,abort.signal),e=>e.unknownResult&&e.commandId===user);
 assert.equal(calls,1);
});

const samples = {
 SettlementEmpty: {}, SettlementDate: {actualEnd:'2026-10-30'}, SettlementDeclaration: {complete:true}, SettlementDuplicate: {sameDocument:false}, SettlementTrack:{documentId:user}, SettlementAmount:{amount:'12.34'},
 SettlementRecovery:{amount:'12.34',source:'WrongRecipient',evidenceIds:[user]},
 RoundInput:{lines:[{sourceLineId:null,categoryId:user,description:'ค่าใช้จ่าย',incurredOn:'2026-10-30',quantity:null,rate:null,lumpSum:'12.34',allocations:[]}]},
 ReviewInput:{decision:'Approve',reason:'ตรวจแล้ว'}, DocumentInput:{issuer:'จำลอง',number:null,hasNoNumber:true,issuedOn:'2026-10-30',amount:'12.34',evidenceIds:[user]},
 RefundInput:{amount:'12.34',returnedOn:'2026-10-30',channel:'Transfer',companyRecipient:'บริษัทจำลอง',evidenceIds:[user],adjustmentCaseId:null},
 RefundCorrectionInput:{refundId:user,correctAmount:'12.34',evidenceIds:[user]}, CasePaymentInput:{caseId:user,amount:'12.34',paidOn:'2026-10-30',evidenceIds:[user]},
 ExcessPaymentInput:{excessId:user,recipientPersonId:user,amount:'12.34',paidOn:'2026-10-30',evidenceIds:[user]}, DispositionInput:{disposition:'AwaitingExternalReview',evidenceIds:[user],paymentIds:[]},
 IncidentInput:{attemptId:user,ownerReceived:'12.34',wrongRecipientReceived:'0.00',evidenceIds:[user]}
};
// Independent expected contracts, maintained here rather than imported from the production operation map.
const expectedOperations = [
 ['rounds/create', 'rounds/create', samples.RoundInput, false],
 ['rounds/update', 'rounds/{entityId}/update', samples.RoundInput, true],
 ['rounds/submit', 'rounds/{entityId}/submit', samples.SettlementEmpty, true],
 ['rounds/review', 'rounds/{entityId}/review', samples.ReviewInput, true],
 ['documents/create', 'documents/create', samples.DocumentInput, false],
 ['documents/update', 'documents/{entityId}/update', samples.DocumentInput, true],
 ['rounds/documents', 'rounds/{entityId}/documents', samples.DocumentInput, true],
 ['duplicates/review', 'duplicates/{entityId}/review', samples.SettlementDuplicate, true],
 ['completion/confirm', 'completion/confirm', samples.SettlementDate, false],
 ['completion/correct', 'completion/correct', samples.SettlementDate, false],
 ['completion/correction-confirm', 'completion/{entityId}/confirm', samples.SettlementEmpty, true],
 ['declaration', 'declaration', samples.SettlementDeclaration, false],
 ['refunds/create', 'refunds/create', samples.RefundInput, false],
 ['refunds/update', 'refunds/{entityId}/update', samples.RefundInput, true],
 ['refunds/confirm', 'refunds/{entityId}/confirm', samples.SettlementEmpty, true],
 ['refunds/cancel', 'refunds/{entityId}/cancel', samples.SettlementEmpty, true],
 ['refunds/review', 'refunds/{entityId}/review', samples.ReviewInput, true],
 ['corrections/create', 'corrections/create', samples.RefundCorrectionInput, false],
 ['case-payments/create', 'case-payments/create', samples.CasePaymentInput, false],
 ['excess-payments/create', 'excess-payments/create', samples.ExcessPaymentInput, false],
 ['adjustments/close', 'adjustments/{entityId}/close', samples.SettlementEmpty, true],
 ['rounds/adjust', 'rounds/{entityId}/adjust', samples.RoundInput, true],
 ['rounds/excess', 'rounds/{entityId}/excess', samples.SettlementTrack, true],
 ['excess/disposition', 'excess/{entityId}/disposition', samples.DispositionInput, true],
 ['incidents/create', 'incidents/create', samples.IncidentInput, false],
 ['incidents/confirm', 'incidents/{entityId}/confirm', samples.SettlementEmpty, true],
 ['incidents/recover', 'incidents/{entityId}/recover', samples.SettlementRecovery, true],
 ['recoveries/confirm', 'recoveries/{entityId}/confirm', samples.SettlementEmpty, true],
 ['incidents/release', 'incidents/{entityId}/release', samples.SettlementEmpty, true],
 ['incidents/shortfall', 'incidents/{entityId}/shortfall', samples.SettlementAmount, true],
 ['corrections/confirm', 'corrections/{entityId}/confirm', samples.SettlementEmpty, true],
 ['corrections/return', 'corrections/{entityId}/return', samples.SettlementEmpty, true],
 ['corrections/reject', 'corrections/{entityId}/reject', samples.SettlementEmpty, true],
 ['corrections/cancel', 'corrections/{entityId}/cancel', samples.SettlementEmpty, true],
 ['case-payments/confirm', 'case-payments/{entityId}/confirm', samples.SettlementEmpty, true],
 ['case-payments/return', 'case-payments/{entityId}/return', samples.SettlementEmpty, true],
 ['case-payments/reject', 'case-payments/{entityId}/reject', samples.SettlementEmpty, true],
 ['case-payments/cancel', 'case-payments/{entityId}/cancel', samples.SettlementEmpty, true],
 ['excess-payments/confirm', 'excess-payments/{entityId}/confirm', samples.SettlementEmpty, true],
 ['excess-payments/return', 'excess-payments/{entityId}/return', samples.SettlementEmpty, true],
 ['excess-payments/reject', 'excess-payments/{entityId}/reject', samples.SettlementEmpty, true],
 ['excess-payments/cancel', 'excess-payments/{entityId}/cancel', samples.SettlementEmpty, true],
 ];
assert.equal(expectedOperations.length,42);
assert.equal(new Set(expectedOperations.map(([action])=>action)).size,42);
for (const [action, expectedPath, input, hasEntity] of expectedOperations) {
 test(`typed operation ${action} selects the exact scoped POST contract`,async()=>{
  const body=hasEntity?{...input,entityId:user}:{...input}; let calls=0;
  const c=client(async(url,o)=>{
   calls++;
   assert.equal(url,`/api/v1/field-advances/requests/${id}/settlement/${expectedPath.replace('{entityId}',user)}`);
   assert.equal(o.method,'POST'); assert.equal(o.headers['X-CSRF-Token'],'csrf');
   assert.deepEqual(JSON.parse(o.body),{input,meta});
   return Response.json({requestId:id,entityId:user,state:'Accepted',requestVersion:2,familyVersion:2,entityVersion:1});
  });
  const result=await c.sendSettlementCommand(id,action,body,meta);
  assert.equal(result.entityId,user); assert.equal(calls,1);
 });
}

test('file download carries both request and evidence scope and cancellation',async()=>{
 let called=false;const abort=new AbortController();const c=client(async(url,o)=>{called=true;assert.equal(url,`/api/v1/field-advances/requests/${id}/settlement/evidence/${user}`);assert.ok(o.signal);return new Response(new Blob(['test'],{type:'image/png'}));});
 const result=await c.downloadEvidence(id,user,abort.signal);assert.equal(result.type,'image/png');assert.ok(called);
});

test('typed preview accepts exact string amounts and rejects numeric nested money', async()=>{
 const preview={requestId:id,roundId:user,revisionId:id,requestVersion:1,familyVersion:1,roundVersion:1,revisionVersion:1,confirmationHash:'a'.repeat(64),total:'12.34',approved:'1000.00',lines:[{id:user,sourceLineId:null,categoryId:user,description:'ค่าใช้จ่าย',incurredOn:'2026-10-30',quantity:null,rate:null,lumpSum:'12.34',amount:'12.34',allocations:[]}],documents:[],evidence:[]};
 assert.deepEqual(await client(async()=>Response.json(preview)).previewSettlementRound(id,user),preview);
 await assert.rejects(()=>client(async()=>Response.json({...preview,lines:[{...preview.lines[0],amount:12.34}]})).previewSettlementRound(id,user));
});
test('family and assigned revision use scoped read transport',async()=>{
 const family={requestId:id,requests:[{requestId:id,state:'Approved',totals:row.totals,holds:[],allowedActions:['read']}],pageTotals:row.totals,totalsScope:'visible-page',pageComplete:true,pageSettled:false,nextCursor:null,familyVersion:1};
 const c=client(async(url)=>{assert.equal(url,`/api/v1/field-advances/requests/${id}/settlement/family?take=25`);return Response.json(family);});
 assert.deepEqual(await c.loadSettlementFamily(id),family);
});

test('assigned round reads and downloads bind request round revision and proof',async()=>{
 const detail={requestId:id,roundId:user,revisionId:id,requestVersion:1,familyVersion:1,roundVersion:1,revisionVersion:1,state:'Submitted',currentStepId:user,currentStepVersion:1,total:'12.34',lines:[],documents:[],evidence:[]};
 const c=client(async(url)=>{assert.equal(url,`/api/v1/field-advances/requests/${id}/settlement/rounds/${user}/revisions/${id}`);return Response.json(detail);});
 assert.deepEqual(await c.loadSettlementRound(id,user,id),detail);
 const d=client(async(url)=>{assert.equal(url,`/api/v1/field-advances/requests/${id}/settlement/rounds/${user}/revisions/${id}/evidence/${user}`);return new Response(new Uint8Array([1]),{headers:{'Content-Type':'image/png'}});});
 assert.equal((await d.downloadRoundEvidence(id,user,id,user)).size,1);
 await assert.rejects(()=>client(async()=>Response.json(detail)).loadSettlementRound(id,user,user),e=>e.status===403);
});
test('detailed catalog rejects numeric refund money',async()=>{
 const page={items:[{id:user,requestId:id,version:1,state:'Draft',reason:'ทดสอบ',refund:{amount:12.34,returnedOn:'2026-10-30',channel:'Transfer',companyRecipient:'บริษัท',evidenceIds:[user],adjustmentCaseId:null}}],nextCursor:null,requestVersion:1,familyVersion:1};
 await assert.rejects(()=>client(async()=>Response.json(page)).listSettlement(id,'refunds'));
});

for (const status of [401, 403, 409]) test(`dispatched command ${status} retains unknown outcome and original command ID`, async()=>{
 let calls=0;
 const c=client(async()=>{calls++;return Response.json({code:status===409?'settlement-projection-changed':'finance-access-denied',secret:'must-not-escape'},{status});});
 await assert.rejects(()=>c.sendSettlementCommand(id,'declaration',{complete:true},meta),e=>{
  assert.equal(e.status,status);assert.equal(e.unknownResult,true);assert.equal(e.commandId,meta.commandId);
  assert.equal(e.clearProtectedData,status===401||status===403);assert.equal(JSON.stringify(e).includes('must-not-escape'),false);return true;
 });assert.equal(calls,1);
});
test('mismatched success after dispatch is unknown and clears protected data',async()=>{
 let calls=0;const c=client(async()=>{calls++;return Response.json({requestId:user,entityId:user,state:'Draft',requestVersion:2,familyVersion:2,entityVersion:1});});
 await assert.rejects(()=>c.sendSettlementCommand(id,'declaration',{complete:true},meta),e=>e.status===403&&e.clearProtectedData&&e.unknownResult&&e.commandId===meta.commandId);
 assert.equal(calls,1);
});
test('post-dispatch account change preserves original command and never resends in new account',async()=>{
 let changed=false,calls=0;const c=client(async()=>{calls++;changed=true;return Response.json({requestId:id,entityId:user,state:'Draft',requestVersion:2,familyVersion:2,entityVersion:1});},()=>changed?id:user);
 await assert.rejects(()=>c.sendSettlementCommand(id,'declaration',{complete:true},meta),e=>e.status===403&&e.unknownResult&&e.commandId===meta.commandId&&e.clearProtectedData);
 await assert.rejects(()=>c.sendSettlementCommand(id,'declaration',{complete:true},meta),e=>e.status===403&&!e.unknownResult);
 assert.equal(calls,1);
});

test('family continuation accepts sparse pages without root row and keeps opaque cursor',async()=>{
 const page={requestId:id,requests:[],pageTotals:{...row.totals,ownerReceived:'0.00',ownerOutstanding:'0.00'},totalsScope:'visible-page',pageComplete:false,pageSettled:false,nextCursor:'opaque+/=',familyVersion:1};
 let calls=0;const c=client(async(url,o)=>{calls++;assert.equal(url,`/api/v1/field-advances/requests/${id}/settlement/family?take=10&cursor=opaque%2B%2F%3D`);assert.equal(o.cache,'no-store');return Response.json(page);});
 assert.deepEqual(await c.loadSettlementFamily(id,10,'opaque+/='),page);assert.equal(calls,1);
 await assert.rejects(()=>c.loadSettlementFamily(id,101));assert.equal(calls,1);
 const abort=new AbortController();abort.abort();await assert.rejects(()=>c.loadSettlementFamily(id,10,undefined,abort.signal));assert.equal(calls,1);
 await assert.rejects(()=>client(async()=>Response.json({...page,requestId:user})).loadSettlementFamily(id),e=>e.status===403);
 await assert.rejects(()=>client(async()=>Response.json({...page,pageSettled:true})).loadSettlementFamily(id));
});

test('employee declaration rejects invented timing and authority values', async () => {
 for (const declaration of [{id:user,version:1,isWithdrawal:false,declaredAtUtc:'bad',workCompletionId:id,completionOnTime:true},{id:user,version:1,isWithdrawal:false,declaredAtUtc:'2026-10-31T00:00:00Z',workCompletionId:id,completionOnTime:'yes'}])
  await assert.rejects(()=>client(async()=>Response.json({...row,declaration})).loadSettlement(id));
});

test('PDF upload preserves MIME and bytes without requiring live camera',async()=>{
 let sent;const c=client(async(url,options)=>{sent=options;return Response.json({id:user});});
 const proof=new Blob(['%PDF-1.7\nfixture'],{type:'application/pdf'});
 await c.uploadEvidence(id,proof);assert.equal(sent.body,proof);assert.equal(sent.headers['Content-Type'],'application/pdf');
});

test('R2 receipt check is GET only, exact identity bound and missing receipt remains unresolved',async()=>{
 const result={requestId:id,entityId:user,state:'Draft',requestVersion:2,familyVersion:2,entityVersion:1};
 const c=client(async(url,o)=>{assert.equal(o.method,'GET');assert.ok(url.includes('/commands/'+meta.commandId+'/receipt?'));return Response.json({commandId:meta.commandId,operation:'rounds/create',operationEntityId:null,result});});
 assert.equal(typeof c.checkSettlementReceipt,'function');
 assert.deepEqual(await c.checkSettlementReceipt(id,'rounds/create',null,meta.commandId),result);
 await assert.rejects(()=>client(async()=>new Response('{}',{status:404})).checkSettlementReceipt(id,'rounds/create',null,meta.commandId));
 await assert.rejects(()=>client(async()=>Response.json({commandId:id,operation:'rounds/create',operationEntityId:null,result})).checkSettlementReceipt(id,'rounds/create',null,meta.commandId));
});

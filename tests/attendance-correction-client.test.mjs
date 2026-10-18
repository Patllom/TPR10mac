import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { loadTs } from './helpers/load-ts.mjs';
const id = '00000000-0000-0000-0000-000000000001';
const key = '00000000-0000-0000-0000-000000000002';
const command = { commandId: key, expectedVersion: 2, revision: 1, reason: null };
const proposal = { workDate:'2026-09-25', inUtc:'2026-09-25T01:00:00Z', outUtc:null,
  inSite:{workspaceId:id, projectId:id, siteId:id}, outSite:null, reason:'ลืมลงเวลา' };
const input = { dayId:null, originalWorkDate:'2026-09-25', expectedDayVersion:null, proposal };
const view = { id, employeeId:id, originalWorkDate:'2026-09-25', version:2, state:0,
  deadlineUtc:'2026-10-02T17:00:00Z', currentRevision:1, proposal, baselineDayVersion:null, actions:['save','submit'], readBasis:'Own' };
function client() { assert.ok(existsSync('lib/attendance/correction-client.ts'), 'correction client missing'); return loadTs('lib/attendance/correction-client.ts'); }
test('detail comparison accepts bounded time/site data but rejects evidence and list disclosure', () => {
  const p = loadTs('lib/attendance/correction-view.ts');
  const comparison = { dayId:id, baselineMatches:true, original:{inUtc:proposal.inUtc,outUtc:null}, current:{version:2,workDate:proposal.workDate,inUtc:proposal.inUtc,outUtc:null,inSite:proposal.inSite,outSite:null} };
  assert.deepEqual(p.parseCorrection({...view,comparison}).comparison,comparison);
  assert.throws(()=>p.parseCorrection({...view,comparison:{...comparison,photo:'/private'}}));
  assert.throws(()=>p.parseCorrection({...view,comparison:{...comparison,current:{...comparison.current,gps:{latitude:13}}}}));
  assert.throws(()=>p.parseCorrectionPage({items:[{...view,comparison}],total:1,pageNumber:1,pageSize:20}));
});
test('identity fence runs after CSRF and before POST including explicit retry', async () => {
  const c = client(), old = global.fetch; const calls = [];
  global.fetch = async url => { calls.push(url); return Response.json(url.endsWith('/csrf') ? {token:'csrf'} : view); };
  const fence = async () => { calls.push('identity-fence'); throw new Error('account changed'); };
  try {
    await assert.rejects(() => c.sendCorrection('submit', id, command, undefined, fence), /account changed/);
    assert.deepEqual(calls, ['/api/v1/auth/csrf', 'identity-fence']);
    calls.length = 0;
    await assert.rejects(() => c.retryCorrection('submit', id, command, undefined, fence), /account changed/);
    assert.deepEqual(calls, [`/api/v1/attendance/corrections/${id}`, '/api/v1/auth/csrf', 'identity-fence']);
  } finally { global.fetch = old; }
});
test('all correction commands use exact routes, one POST and bounded private transport', async () => {
  const c = client(); const old = global.fetch; const calls=[];
  global.fetch=async(url, options)=>{calls.push([url,options]); return Response.json(url.endsWith('/csrf') ? {token:'csrf'} : {id,commandId:key});};
  try {
    for(const [action,body] of [['create',{commandId:key,input}],['save',{input,command}],...['submit','supervisor-approve','hr-approve','return','reroute'].map(a=>[a,{...command,reason:'ตรวจแล้ว'}])]) {
      calls.length=0; const response=await c.sendCorrection(action, action==='create'?null:id, body);
      assert.equal(response.status,200); assert.equal(calls.length,2);
      assert.equal(calls[1][0],'/api/v1/attendance/corrections'+(action==='create'?'':`/${id}/${action==='save'?'revisions':action}`));
      const o=calls[1][1]; assert.equal(o.method,'POST'); assert.equal(o.credentials,'same-origin'); assert.equal(o.cache,'no-store'); assert.equal(o.redirect,'error'); assert.ok(o.signal); assert.equal(o.headers['X-CSRF-Token'],'csrf'); assert.deepEqual(JSON.parse(o.body),body);
    }
  } finally {global.fetch=old;}
});
test('forged actions, ids, fields and unknown states fail closed before fetch', async()=>{
  const c=client(); const old=global.fetch; let count=0; global.fetch=async()=>{count++;return Response.json({});};
  try {
    for(const args of [['delete',id,command],['submit','../users',command],['submit',id,{...command,actorId:id}],['create',null,{commandId:key,input:{...input,proposal:null}}]])
      await assert.rejects(()=>c.sendCorrection(...args));
    assert.equal(count,0);
    const p=loadTs('lib/attendance/correction-view.ts'); assert.deepEqual(p.parseCorrection(view),view);
    for(const bad of [{...view,state:99},{...view,actions:['delete']},{...view,readBasis:'Admin'},{...view,photo:'/secret'},{...view,history:{}}]) assert.throws(()=>p.parseCorrection(bad));
    assert.deepEqual(p.parseCorrectionAck({id,commandId:key}),{id,commandId:key});
    assert.throws(()=>p.parseCorrectionAck({id,commandId:key,proposal}));
  }finally{global.fetch=old;}
});
test('503 and uncertain transport never replay or log out',async()=>{
  const c=client(); const old=global.fetch; let posts=0;
  try {
    global.fetch=async(url)=> url.endsWith('/csrf')?Response.json({token:'csrf'}):(posts++,new Response(null,{status:503}));
    assert.equal((await c.sendCorrection('submit',id,command)).status,503); assert.equal(posts,1);
    global.fetch=async(url)=>{if(url.endsWith('/csrf'))return Response.json({token:'csrf'});posts++;throw new TypeError('network');};
    await assert.rejects(()=>c.sendCorrection('submit',id,command), e=>e.commandId===key); assert.equal(posts,2);
  }finally{global.fetch=old;}
});
test('explicit retry reads current revision first and blocks stale or inaccessible state',async()=>{
  const c=client(); const old=global.fetch; const calls=[]; let current=view;
  global.fetch=async(url,options)=>{calls.push([url,options]);return Response.json(url.endsWith('/csrf')?{token:'csrf'}:options?.method==='POST'?{id,commandId:key}:current);};
  try {
    await c.retryCorrection('submit',id,command); assert.deepEqual(calls.map(x=>x[0]),[`/api/v1/attendance/corrections/${id}`,'/api/v1/auth/csrf',`/api/v1/attendance/corrections/${id}/submit`]);
    calls.length=0; current={...view,version:3}; await assert.rejects(()=>c.retryCorrection('submit',id,command), e => e.status === 409); assert.equal(calls.length,1);
    calls.length=0; global.fetch=async(url)=>{calls.push(url);return new Response(null,{status:404});};
    assert.equal((await c.retryCorrection('submit',id,command)).status,404); assert.equal(calls.length,1);
  }finally{global.fetch=old;}
});
test('abort after CSRF does not submit and list accepts only bounded mine/supervisor/hr queries',async()=>{
  const c=client(); const old=global.fetch; const controller=new AbortController(); let count=0;
  try {
    global.fetch=async()=>{count++;controller.abort();return Response.json({token:'csrf'});};
    await assert.rejects(()=>c.sendCorrection('submit',id,command,controller.signal));assert.equal(count,1);
    const calls=[];global.fetch=async(url)=>{calls.push(url);return Response.json({items:[],total:0,pageNumber:1,pageSize:20});};
    await c.listCorrections('mine');await c.listCorrections('supervisor');await c.listCorrections('hr',2,25);
    assert.deepEqual(calls,['/api/v1/attendance/corrections?page=1&pageSize=20','/api/v1/attendance/corrections/approvals?stage=supervisor&page=1&pageSize=20','/api/v1/attendance/corrections/approvals?stage=hr&page=2&pageSize=25']);
    await assert.rejects(()=>c.listCorrections('admin'));await assert.rejects(()=>c.listCorrections('mine',1,101));assert.equal(calls.length,3);
  }finally{global.fetch=old;}
});
test('history and page validation reject nested private fields and preserve approved numeric state',()=>{
  client();const p=loadTs('lib/attendance/correction-view.ts');
  const history={revisions:[{revision:1,baselineDayVersion:null,proposal,createdAtUtc:'2026-09-25T01:00:00+00:00'}],
    routes:[{revision:1,supervisorId:id,hrCandidateIds:[key],createdAtUtc:'2026-09-25T01:00:00Z'}],
    approvals:[{revision:1,step:'Supervisor',actorId:id,outcome:'Approved',reason:null,occurredAtUtc:'2026-09-25T01:00:00Z'}],
    reroutes:[{revision:1,actorId:key,reason:'เปลี่ยนสายงาน',occurredAtUtc:'2026-09-25T01:00:00Z'}]};
  assert.deepEqual(p.parseCorrection({...view,state:4,actions:[],history}).history,history);
  for(const bad of [{...history,approvals:[{...history.approvals[0],outcome:'Rejected'}]},
    {...history,routes:[{...history.routes[0],photo:'/secret'}]}, {...history,revisions:[{...history.revisions[0],proposal:{...proposal,inSite:{...proposal.inSite,path:'/secret'}}}]}])
    assert.throws(()=>p.parseCorrection({...view,history:bad}));
  assert.deepEqual(p.parseCorrectionPage({items:[view],total:1,pageNumber:1,pageSize:20}).items,[view]);
  for(const bad of [{items:[{...view,history}],total:1,pageNumber:1,pageSize:20},{items:[view],total:0,pageNumber:1,pageSize:20},{items:[],total:0,pageNumber:1,pageSize:101}]) assert.throws(()=>p.parseCorrectionPage(bad));
  assert.doesNotMatch(p.correctionError(503),/เข้าสู่ระบบ/);
});
test('payload is frozen before CSRF await and CSRF failure never dispatches mutation',async()=>{
  const c=client();const old=global.fetch;const changed={...command};let posted;let calls=0;
  try {
    global.fetch=async(url,options)=>{calls++;if(url.endsWith('/csrf')){changed.actorId=id;return Response.json({token:'csrf'});}posted=JSON.parse(options.body);return Response.json({id,commandId:key});};
    await c.sendCorrection('submit',id,changed);assert.deepEqual(posted,command);assert.equal(calls,2);
    calls=0;global.fetch=async()=>{calls++;return new Response(null,{status:429});};
    assert.equal((await c.sendCorrection('submit',id,command)).status,429);assert.equal(calls,1);
    calls=0;await assert.rejects(()=>c.sendCorrection('create',null,{commandId:key,input:{...input,proposal:{...proposal,reason:'ก'.repeat(2001)}}}));assert.equal(calls,0);
  }finally{global.fetch=old;}
});
test('explicit create retry reads own requests and reuses exact command id without creating a new key',async()=>{
  const c=client();const old=global.fetch;const calls=[];const body={commandId:key,input};
  assert.equal(typeof c.retryCreateCorrection,'function','explicit create retry helper missing');
  global.fetch=async(url,options)=>{calls.push([url,options]);return Response.json(url.endsWith('/csrf')?{token:'csrf'}:options?.method==='POST'?{id,commandId:key}:{items:[view],total:1,pageNumber:1,pageSize:20});};
  try {
    await c.retryCreateCorrection(body);
    assert.deepEqual(calls.map(x=>x[0]),['/api/v1/attendance/corrections?page=1&pageSize=20','/api/v1/auth/csrf','/api/v1/attendance/corrections']);
    assert.deepEqual(JSON.parse(calls[2][1].body),body);
  }finally{global.fetch=old;}
});
test('history-granted supervisor uses the actual backend readBasis without widening workflow grants',()=>{
  client();const p=loadTs('lib/attendance/correction-view.ts');
  assert.equal(p.parseCorrection({...view,readBasis:'Supervisor'}).readBasis,'Supervisor');
  assert.throws(()=>p.parseCorrection({...view,readBasis:'Team'}));
});

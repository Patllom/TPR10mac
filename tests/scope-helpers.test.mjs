import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';
const w = '11111111-1111-4111-8111-111111111111';
const p = '22222222-2222-4222-8222-222222222222';
const s = '33333333-3333-4333-8333-333333333333';
test('scope URL และ route parser แยกระดับ exact และปฏิเสธ forged segments', () => {
  const { scopePath, parseScopePath, recordPath } = loadTs('lib/scopes/scope-path.ts');
  for (const [key, suffix] of [[{ workspaceId:w, projectId:null, siteId:null }, ''], [{ workspaceId:w, projectId:p, siteId:null }, `/projects/${p}`], [{ workspaceId:w, projectId:p, siteId:s }, `/projects/${p}/sites/${s}`]]) {
    assert.equal(scopePath(key), `/portal/scopes/${w}${suffix}`);
    assert.equal(recordPath(key), `/api/v1/workspaces/${w}${suffix}/scope-probe-records`);
    assert.deepEqual(parseScopePath(w, suffix.split('/').filter(Boolean)), key);
  }
  for (const parts of [['sites',s], ['projects',p,'extra'], ['projects',p,'sites',s,'extra'], ['projects','https://evil.example']]) assert.throws(() => parseScopePath(w, parts));
  for (const key of [{ workspaceId:w, projectId:null, siteId:s }, { workspaceId:'https://evil.example', projectId:null, siteId:null }, { workspaceId:'00000000-0000-0000-0000-000000000000', projectId:null, siteId:null }]) assert.throws(() => scopePath(key));
});
test('server discovery forward cookie เฉพาะ configured origin แบบ no-store และอ่านหน้าถัดไปได้', async () => {
  const { fetchScopes } = loadTs('lib/scopes/scope-fetch.ts');
  const page = { items:[], total:101, pageNumber:2, pageSize:100 };
  let calls = 0;
  const result = await fetchScopes('https://api.example', 'test-cookie', async (url, init) => {
    calls++; assert.equal(url, 'https://api.example/api/v1/scopes?page=2&pageSize=100');
    assert.equal(init.headers.Cookie, 'test-cookie'); assert.equal(init.cache, 'no-store'); assert.equal(init.redirect, 'error'); assert.ok(init.signal);
    return Response.json(page);
  }, 2);
  assert.deepEqual(result, page); assert.equal(calls, 1);
});
test('server discovery ปฏิเสธ origin/JSON ผิดและไม่แปลง outage เป็น logout', async () => {
  const { fetchScopes, ScopeHttpError } = loadTs('lib/scopes/scope-fetch.ts');
  for (const origin of [undefined, 'file:///tmp', 'https://api.example/evil', 'https://user:pass@api.example', 'https://api.example?url=evil']) {
    await assert.rejects(() => fetchScopes(origin, 'private', () => { throw Error('must not send'); }), error => error instanceof ScopeHttpError && error.status === 503);
  }
  for (const status of [401,403,404,409,429,503]) await assert.rejects(() => fetchScopes('https://api.example','',async () => new Response(null,{status})), error => error.status === status);
  await assert.rejects(() => fetchScopes('https://api.example','',async () => Response.json({ items:[{scope:{workspaceId:'evil'}}] })), error => error.status === 503);
});
test('ข้อความ scope errors แยก permission, unavailable, stale version และ outage', () => {
  const { scopeError } = loadTs('lib/scopes/scope-view.ts');
  for (const [status, word] of [[401,'เข้าสู่ระบบ'],[403,'สิทธิ์'],[404,'พื้นที่'],[409,'โหลด'],[503,'บริการ'],[429,'รอ']]) assert.match(scopeError(status),new RegExp(word));
});
test('authMutation อนุญาตคู่ POST/PATCH เฉพาะ routes ที่กำหนด และ auth เดิมยัง POST', async () => {
  const { authMutation } = loadTs('lib/auth/auth-client.ts');
  const original = globalThis.fetch;
  try {
    for (const [path, method] of [[`/api/v1/organization/workspaces/${w}`,'PATCH'],[`/api/v1/workspaces/${w}/projects/${p}/sites/${s}/scope-probe-records/${s}`,'PATCH'],['/api/v1/scope-assignments','POST'],[`/api/v1/scope-assignments/${s}/revoke`,'POST'],[`/api/v1/workspaces/${w}/scope-probe-records/export-simulation`,'POST'],['/api/v1/auth/logout',undefined]]) {
      const calls=[]; globalThis.fetch=async (url, init) => { calls.push([url,init]); return url.endsWith('/csrf') ? Response.json({token:'test'}) : new Response(null,{status:204}); };
      assert.equal((await authMutation(path,{},method)).status,204); assert.equal(calls.length,2); assert.equal(calls[1][1].method,method ?? 'POST'); assert.equal(calls[1][1].headers['X-CSRF-Token'],'test');
    }
    for (const [path,method] of [['https://evil.example/api/v1/auth/logout','POST'],['/api/v1/auth/logout','PATCH'],['/api/v1/organization/workspaces','PATCH'],[`/api/v1/organization/workspaces/${w}?evil=1`,'PATCH'],['/api/v1/users','POST'],[`/api/v1/workspaces/${w}/scope-probe-records`,'DELETE']]) {
      globalThis.fetch=async () => assert.fail('invalid request must not issue CSRF'); await assert.rejects(() => authMutation(path,{},method));
    }
    let calls=0; globalThis.fetch=async () => {calls++; return new Response(null,{status:503});};
    assert.equal((await authMutation('/api/v1/scope-assignments',{})).status,503); assert.equal(calls,1);
  } finally {globalThis.fetch=original;}
});

test('ยกเลิกบัญชีระหว่างรับ CSRF แล้วต้องไม่ส่ง mutation ด้วยบัญชีใหม่', async () => {
  const { authMutation } = loadTs('lib/auth/auth-client.ts');
  const original=globalThis.fetch;const controller=new AbortController();
  let release;const gate=new Promise(resolve=>{release=resolve;});let mutations=0;
  try {
    globalThis.fetch=async (_url,init)=>{
      if(init.method) {mutations++;return new Response(null,{status:204});}
      await gate;return Response.json({token:'fixture-only'});
    };
    const request=authMutation('/api/v1/scope-assignments',{},'POST',controller.signal);
    controller.abort();release();
    await assert.rejects(request);assert.equal(mutations,0);
  } finally {release();globalThis.fetch=original;}
});

test('auth change แจ้งช่องอื่นโดยไม่ยกเลิก login ของหน้าต่างผู้ส่งเอง', async () => {
  const original=globalThis.window;globalThis.window=new EventTarget();
  const {subscribeAuthChanges,publishAuthChange}=loadTs('lib/auth/auth-change.ts');
  let own=0;const unsubscribe=subscribeAuthChanges(()=>{own++;});
  const other=new BroadcastChannel('tpr10-auth-change');
  try {
    const received=new Promise(resolve=>{other.onmessage=event=>resolve(event.data);});
    publishAuthChange();assert.equal(await received,'changed');assert.equal(own,0);
  } finally {unsubscribe();other.close();if(original===undefined)delete globalThis.window;else globalThis.window=original;}
});

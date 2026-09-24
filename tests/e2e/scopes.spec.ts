import { test, expect, type Page } from '@playwright/test';
import { createHmac } from 'node:crypto';
const pendingResources=new WeakMap<Page,Set<string>>();
test.beforeEach(async ({page})=>{
  const pending=new Set<string>();pendingResources.set(page,pending);
  page.on('request',request=>pending.add(request.resourceType()+' '+new URL(request.url()).pathname));
  const ended=(request:import('@playwright/test').Request)=>pending.delete(request.resourceType()+' '+new URL(request.url()).pathname);
  page.on('requestfinished',ended);page.on('requestfailed',ended);
});
test.afterEach(async ({page},info)=>{
  if(info.status!==info.expectedStatus) {
    // Fixture diagnostics only: paths/types/state, never query/body/cookies or private DOM.
    console.log('scope pending resource paths/types:',[...pendingResources.get(page)??[]]);
    console.log('scope document readiness:',await page.evaluate(()=>({ready:document.readyState,path:location.pathname})).catch(()=>({ready:'unavailable'})));
  }
});
const w = '11111111-1111-4111-8111-111111111111', p = '22222222-2222-4222-8222-222222222222';
const a = '33333333-3333-4333-8333-333333333333', b = '44444444-4444-4444-8444-444444444444';
const path = (site: string) => `/portal/scopes/${w}/projects/${p}/sites/${site}`;
function code(secret: string) {
  const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bits=[...secret].map(c=>alphabet.indexOf(c).toString(2).padStart(5,'0')).join('');
  const key=Buffer.from(bits.match(/.{8}/g)!.map(x=>parseInt(x,2)));
  const counter=Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now()/30000)));
  const digest=createHmac('sha1',key).update(counter).digest(); const index=digest[19]&15;
  return ((digest.readUInt32BE(index)&0x7fffffff)%1000000).toString().padStart(6,'0');
}
async function login(page: Page, user: string, mfa=false) {
  await page.goto('/login'); await page.getByLabel('ชื่อผู้ใช้',{exact:true}).fill(user);
  await page.getByLabel('รหัสผ่าน',{exact:true}).fill('e2e-isolated-password-123');
  await page.getByRole('button',{name:'เข้าสู่ระบบ',exact:true}).click();
  if (mfa) {
    await page.getByRole('button',{name:'เริ่มตั้งค่า MFA',exact:true}).click();
    const secret=await page.getByLabel('คีย์สำหรับกรอกเอง').inputValue();
    await page.getByLabel('รหัสยืนยัน 6 หลัก').fill(code(secret));
    await page.getByRole('button',{name:'ยืนยัน MFA',exact:true}).click();
    await page.getByRole('button',{name:'บันทึกรหัสกู้คืนแล้ว ไปต่อ'}).click();
  }
  await expect(page).toHaveURL(/\/portal$/);
  // Finish the real login navigation before issuing the next full navigation.
  await expect(page.getByRole('heading',{name:'ระบบปฏิบัติการภายใน',exact:true})).toBeVisible();
  await page.waitForLoadState('load');
}
test('ไม่มี assignment ไม่เปิด catalog และ deep link ผิดไม่เปิดข้อมูล', async ({page})=>{
  await login(page,'e2e-staff'); await page.goto('/portal/scopes');
  await expect(page.getByText('ยังไม่ได้รับมอบหมายพื้นที่',{exact:true})).toBeVisible();
  await page.goto(path(a)); await expect(page.getByText('ข้อมูลทดสอบเฉพาะ A',{exact:true})).toHaveCount(0);
  await expect(page.locator('section').getByRole('alert')).toContainText('พื้นที่');
  const invalid=await page.goto(`/portal/scopes/${w}/sites/${a}`); expect(invalid!.status()).toBe(404);
});
test('พนักงานอ่านเฉพาะ A ไม่มี restricted field และ logout/back/เปลี่ยนบัญชีไม่แสดงข้อมูลเก่า', async ({page})=>{
  await login(page,'e2e-scope-staff'); await page.goto('/portal/scopes');
  await page.getByRole('link',{name:'ไซต์ทดสอบ A',exact:true}).click();
  await expect(page.getByText('ข้อมูลทดสอบเฉพาะ A',{exact:true})).toBeVisible();
  await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toHaveCount(0);
  await page.getByRole('button',{name:'ออกจากระบบ',exact:true}).click(); await expect(page).toHaveURL(/\/login$/);
  await page.goBack(); await expect(page.getByText('ข้อมูลทดสอบเฉพาะ A',{exact:true})).toHaveCount(0);
  await login(page,'e2e-scope-other'); await page.goto('/portal/scopes');
  await expect(page.getByRole('link',{name:'ไซต์ทดสอบ A',exact:true})).toHaveCount(0);
  await page.getByRole('link',{name:'ไซต์ทดสอบ B',exact:true}).click();
  await expect(page.getByText('ข้อมูลทดสอบเฉพาะ B',{exact:true})).toBeVisible();
  await expect(page.getByText('ข้อมูลทดสอบเฉพาะ A',{exact:true})).toHaveCount(0);
});
test('บทบาท A ไม่ใช้ใน B และ response A ที่มาช้าไม่กลับมาแสดงหลัง navigation', async ({page})=>{
  await login(page,'e2e-scope-approver',true);
  const discovery=await page.evaluate(async()=>{const response=await fetch('/api/v1/scopes',{cache:'no-store'});return response.headers.get('cache-control');});expect(discovery).toContain('no-store');
  const reading=page.waitForResponse(response=>response.url().includes(`/sites/${a}/scope-probe-records?`));
  const html=await page.goto(path(a));expect(html!.headers()['cache-control']).toContain('no-store');expect((await reading).headers()['cache-control']).toContain('no-store');
  await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toBeVisible();
  const exporting=page.waitForResponse(response=>response.url().endsWith('/export-simulation'));
  await page.getByRole('button',{name:'ส่งออกข้อมูลทดสอบ'}).click(); await expect(page.getByRole('status')).toContainText('ส่งออก 1 รายการ');
  expect((await exporting).headers()['cache-control']).toContain('no-store');await expect(page.getByLabel('ผลส่งออก')).toContainText('ข้อมูลจำกัด A');
  let release!:()=>void; const gate=new Promise<void>(r=>{release=r;}); let started!:()=>void; const arrived=new Promise<void>(r=>{started=r;});
  await page.route(`**/sites/${a}/scope-probe-records?*`,async route=>{const response=await route.fetch(); started(); await gate; await route.fulfill({response}).catch(()=>{});});
  try {
    await page.getByRole('button',{name:'โหลดข้อมูลใหม่',exact:true}).click(); await arrived;
    await page.goto(path(b)); release();
    await expect(page.getByText('ข้อมูลทดสอบเฉพาะ B',{exact:true})).toBeVisible();
    await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toHaveCount(0);
    await expect(page.getByText('ข้อมูลจำกัด B',{exact:true})).toHaveCount(0);
    await expect(page.getByRole('button',{name:'ส่งออกข้อมูลทดสอบ'})).toHaveCount(0);
  } finally {release();}
  await page.unroute(`**/sites/${a}/scope-probe-records?*`);
  await page.goto(path(a));await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toBeVisible();
  let releaseExport!:()=>void;const exportGate=new Promise<void>(r=>{releaseExport=r;});let exportStarted=false;
  await page.route('**/export-simulation',async route=>{const response=await route.fetch();exportStarted=true;await exportGate;await route.fulfill({response}).catch(()=>{});});
  try {
    await page.getByRole('button',{name:'ส่งออกข้อมูลทดสอบ'}).click();await expect.poll(()=>exportStarted).toBe(true);
    await page.goto(path(b));releaseExport();await expect(page.getByText('ข้อมูลทดสอบเฉพาะ B',{exact:true})).toBeVisible();
    await expect(page.getByLabel('ผลส่งออก')).toHaveCount(0);await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toHaveCount(0);
  } finally {releaseExport();await page.unroute('**/export-simulation');}
  await page.goto(path(a));await page.getByRole('button',{name:'ส่งออกข้อมูลทดสอบ'}).click();await expect(page.getByLabel('ผลส่งออก')).toContainText('ข้อมูลจำกัด A');
  await page.getByRole('button',{name:'ออกจากระบบ',exact:true}).click();await expect(page).toHaveURL(/\/login$/);await page.goBack();
  await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).not.toBeVisible();await expect(page.getByLabel('ผลส่งออก')).not.toBeVisible();
});

test('กลับเข้าแท็บหลังเปลี่ยนบัญชีโดยไม่มี cross-window message ยังล้าง restricted DOM/export',async ({page})=>{
  await login(page,'e2e-scope-privacy',true);await page.goto(path(a));
  await page.getByRole('button',{name:'ส่งออกข้อมูลทดสอบ'}).click();await expect(page.getByLabel('ผลส่งออก')).toContainText('ข้อมูลจำกัด A');
  await page.evaluate(()=>window.dispatchEvent(new PageTransitionEvent('pagehide')));
  await expect(page.getByLabel('ผลส่งออก')).not.toBeVisible();
  await page.reload();await page.getByRole('button',{name:'ส่งออกข้อมูลทดสอบ'}).click();await expect(page.getByLabel('ผลส่งออก')).toContainText('ข้อมูลจำกัด A');
  // Headless Firefox does not reliably change OS-tab visibility. Deliver the browser lifecycle
  // event deterministically; account/session changes still go through real UI and HTTPS API.
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).not.toBeVisible();await expect(page.getByLabel('ผลส่งออก')).not.toBeVisible();
  const other=await page.context().newPage();
  // Exercise session comparison independently of broadcasts (e.g. restricted browser storage).
  await other.addInitScript(()=>{
    Object.defineProperty(window,'BroadcastChannel',{value:undefined});
    Storage.prototype.setItem=()=>{throw new Error('storage blocked in this test window');};
  });
  try {
    await other.goto('/portal/account');await other.getByRole('button',{name:'ออกจากระบบ',exact:true}).click();await expect(other).toHaveURL(/\/login$/);
    await login(other,'e2e-scope-other');
    await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});document.dispatchEvent(new Event('visibilitychange'));});
    await expect(page.locator('section').getByRole('alert')).toContainText('พื้นที่');
    await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toHaveCount(0);await expect(page.getByLabel('ผลส่งออก')).toHaveCount(0);
  } finally {await other.close();}
});
test('selector แบ่งหน้าเกิน100 และใช้ keyboard/mobile ได้',async ({page})=>{
  await page.setViewportSize({width:390,height:844}); await login(page,'e2e-scope-many'); await page.goto('/portal/scopes');
  await expect(page.getByRole('link',{name:/พื้นที่แบ่งหน้า/})).toHaveCount(100);
  const next=page.getByRole('link',{name:'หน้าถัดไป',exact:true}); await next.focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('link',{name:/พื้นที่แบ่งหน้า/})).toHaveCount(1);
  expect(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
});

for (const operation of ['read','export']) test(`หน้าต่างยัง visible ล้างบัญชีเก่าและไม่รับ ${operation} ที่ตอบช้าหลัง logout อีกหน้าต่าง`,async ({page})=>{
  if(operation==='export') await page.context().addInitScript(()=>{Object.defineProperty(window,'BroadcastChannel',{value:undefined});});
  await login(page,`e2e-scope-window-${operation}`,true);await page.goto(path(a));
  await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toBeVisible();
  expect(await page.evaluate(()=>document.hidden)).toBe(false);
  const pattern=operation==='read'?`**/sites/${a}/scope-probe-records?*`:'**/export-simulation';
  let release!:()=>void;const gate=new Promise<void>(resolve=>{release=resolve;});let arrived=false;
  await page.route(pattern,async route=>{const response=await route.fetch();arrived=true;await gate;await route.fulfill({response}).catch(()=>{});});
  const other=await page.context().newPage();
  try {
    await page.getByRole('button',{name:operation==='read'?'โหลดข้อมูลใหม่':'ส่งออกข้อมูลทดสอบ',exact:true}).click();await expect.poll(()=>arrived).toBe(true);
    await other.goto('/portal/account');await other.getByRole('button',{name:'ออกจากระบบ',exact:true}).click();await expect(other).toHaveURL(/\/login$/);
    // No visibility/pagehide event is sent to the original page: both windows remain visible.
    await expect(page).toHaveURL(/\/login(?:\?|$)/);
    release();await login(other,'e2e-scope-other');
    await page.goto(path(b));await expect(page.getByText('ข้อมูลทดสอบเฉพาะ B',{exact:true})).toBeVisible();
    await expect(page.getByText('ข้อมูลจำกัด A',{exact:true})).toHaveCount(0);await expect(page.getByLabel('ผลส่งออก')).toHaveCount(0);
  } finally {release();await page.unroute(pattern);await other.close();}
});
test('ผู้ดูแลไม่มี business bypass และจัดการโครงสร้างผ่าน form ที่มี version',async ({page,browser})=>{
  await login(page,'e2e-scope-admin',true); await page.goto('/portal/scopes'); await expect(page.getByText('ยังไม่ได้รับมอบหมายพื้นที่',{exact:true})).toBeVisible();
  await page.goto(path(a)); await expect(page.locator('section').getByRole('alert')).toContainText('พื้นที่');
  await page.goto('/portal/admin/organization');
  await page.getByLabel('รหัส',{exact:true}).fill('UI_NEW'); await page.getByLabel('ชื่อ',{exact:true}).fill('พื้นที่สร้างผ่านหน้าเว็บ');
  await page.getByRole('button',{name:'สร้างโครงสร้าง',exact:true}).click(); await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
  const created=page.getByRole('listitem').filter({hasText:'UI_NEW · พื้นที่สร้างผ่านหน้าเว็บ'});
  while(await created.count()===0) {await page.getByRole('button',{name:'หน้าถัดไป',exact:true}).click(); await expect(page.getByText('กำลังโหลดโครงสร้าง…',{exact:true})).toHaveCount(0);}
  await created.getByRole('button',{name:'แก้ไขโครงสร้าง',exact:true}).click();
  await page.getByLabel('ชื่อ',{exact:true}).fill('พื้นที่แก้ไขผ่านหน้าเว็บ');
  await page.getByLabel('เปิดใช้งาน',{exact:true}).uncheck(); await page.getByLabel('เหตุผล',{exact:true}).fill('ปิดพื้นที่ทดสอบที่เพิ่งสร้าง');
  await page.getByRole('button',{name:'บันทึกโครงสร้าง',exact:true}).click();
  const updated=page.getByRole('listitem').filter({hasText:'UI_NEW · พื้นที่แก้ไขผ่านหน้าเว็บ'});
  await expect(updated).toContainText('ปิดใช้งาน · เวอร์ชัน 2');
  const noJs=await browser.newContext({javaScriptEnabled:false,storageState:await page.context().storageState()});
  try {const staticPage=await noJs.newPage();await staticPage.goto('/portal/admin/organization');await expect(staticPage.getByRole('button',{name:'สร้างโครงสร้าง',exact:true,includeHidden:true})).toBeDisabled();} finally {await noJs.close();}
});
test('assignment manager ใช้ options โดยไม่ต้อง users:manage และ self-grant ถูกปฏิเสธจริง',async ({page})=>{
  await login(page,'e2e-scope-manager',true); await page.goto('/portal/admin/assignments');
  await expect(page.getByRole('option',{name:'บัญชีของฉัน (ห้ามมอบหมายให้ตนเอง)'})).toBeDisabled();
  await page.getByLabel('ผู้ใช้',{exact:true}).selectOption({label:'e2e-scope-target'});
  await page.getByLabel('Workspace UUID').fill(w);
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:true});document.dispatchEvent(new Event('visibilitychange'));});
  await page.evaluate(()=>{Object.defineProperty(document,'hidden',{configurable:true,value:false});document.dispatchEvent(new Event('visibilitychange'));});
  await page.waitForLoadState('networkidle');
  await expect(page.getByLabel('Workspace UUID')).toHaveValue(w);
  await expect(page.getByLabel('ผู้ใช้',{exact:true}).locator('option:checked')).toHaveText('e2e-scope-target');
  await page.route('**/api/v1/auth/session',route=>route.fulfill({status:503,json:{}}));
  await page.evaluate(()=>{window.dispatchEvent(new Event('focus'));});
  await expect(page.getByRole('alert').filter({hasText:'ข้อมูลถูกซ่อนไว้'})).toBeVisible();
  await expect(page.getByLabel('Workspace UUID')).not.toBeVisible();await expect(page).toHaveURL(/\/portal\/admin\/assignments$/);
  await page.unroute('**/api/v1/auth/session');await page.getByRole('button',{name:'ตรวจสอบอีกครั้ง'}).click();
  await expect(page.getByLabel('Workspace UUID')).toBeVisible();await expect(page.getByLabel('Workspace UUID')).toHaveValue(w);
  await page.getByLabel('Project UUID').fill(p); await page.getByLabel('Site UUID').fill(a);
  await page.getByLabel('บทบาท',{exact:true}).selectOption({label:'บทบาททดสอบ พนักงาน'});
  await page.getByLabel('เหตุผล',{exact:true}).fill('ทดสอบผ่านหน้าจอ');
  await page.getByRole('button',{name:'มอบหมายสิทธิ์',exact:true}).click(); await expect(page.getByRole('status')).toContainText('session');
  const status=await page.evaluate(async ({w,p,a})=>{
    const session=await (await fetch('/api/v1/auth/session',{cache:'no-store'})).json(); const csrf=await (await fetch('/api/v1/auth/csrf',{cache:'no-store'})).json();
    const roles=await (await fetch('/api/v1/scope-assignments/options/roles',{cache:'no-store'})).json();
    return (await fetch('/api/v1/scope-assignments',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf.token},body:JSON.stringify({userId:session.userId,scope:{workspaceId:w,projectId:p,siteId:a},roleId:roles.items[0].id,reason:'self denied'})})).status;
  },{w,p,a}); expect(status).toBe(403);
  const target=page.getByRole('listitem').filter({hasText:'ผู้ใช้ e2e-scope-target'});
  while(await target.count()===0) {await page.getByRole('button',{name:'หน้าถัดไป',exact:true}).click();await expect(page.getByRole('button',{name:'หน้าถัดไป',exact:true})).toBeVisible();}
  await target.getByRole('button',{name:'เปลี่ยนการมอบหมาย',exact:true}).click();
  await page.getByLabel('เหตุผล',{exact:true}).fill('ไม่มีการเปลี่ยนแปลง');
  await expect.soft(page.getByRole('button',{name:'บันทึกการมอบหมายใหม่',exact:true})).toBeDisabled();
  await page.getByLabel('Site UUID').fill(b); await page.getByLabel('เหตุผล',{exact:true}).fill('เปลี่ยนพื้นที่ทดสอบ');
  await page.getByRole('button',{name:'บันทึกการมอบหมายใหม่',exact:true}).click();
  const replaced=target.filter({hasText:b});
  await expect(replaced).toContainText('เวอร์ชัน 1');await expect(target.filter({hasText:a})).toContainText('ถอนแล้ว');
  await page.getByLabel('เหตุผล',{exact:true}).fill('ถอนพื้นที่ทดสอบ'); await replaced.getByRole('button',{name:/^ถอนสิทธิ์/}).click();
  await expect(replaced).toContainText('ถอนแล้ว'); await expect(replaced).toContainText('เวอร์ชัน 2');
  await page.goto('/portal/admin/organization');await expect(page.locator('section').getByRole('alert')).toContainText('สิทธิ์');await expect(page.locator('form')).toHaveCount(0);
});

test('สองบทบาทในพื้นที่เดียวแยกถอนถูกบทบาท และ no-op ไม่รายงานถอน session',async ({page})=>{
  await login(page,'e2e-scope-reviewer',true);await page.goto('/portal/admin/assignments');
  for(const role of ['บทบาททดสอบ พนักงาน','บทบาททดสอบ อนุมัติ']) {
    await page.getByLabel('ผู้ใช้',{exact:true}).selectOption({label:'e2e-scope-target'});
    await page.getByLabel('Workspace UUID').fill(w);await page.getByLabel('Project UUID').fill(p);await page.getByLabel('Site UUID').fill(a);
    await page.getByLabel('บทบาท',{exact:true}).selectOption({label:role});await page.getByLabel('เหตุผล',{exact:true}).fill('สองบทบาทเพื่อทดสอบการถอน');
    await page.getByRole('button',{name:'มอบหมายสิทธิ์',exact:true}).click();await expect(page.getByRole('status')).toContainText('session');
  }
  const rows=page.getByRole('listitem').filter({hasText:'ผู้ใช้ e2e-scope-target'}).filter({hasText:'ใช้งานอยู่'});
  while(await rows.count()<2) {await page.getByRole('button',{name:'หน้าถัดไป',exact:true}).click();await expect(page.getByRole('button',{name:'หน้าถัดไป',exact:true})).toBeVisible();}
  const staff=rows.filter({hasText:'บทบาททดสอบ พนักงาน'}),approver=rows.filter({hasText:'บทบาททดสอบ อนุมัติ'});
  await expect(staff).toHaveCount(1);await expect(approver).toHaveCount(1);
  await expect(staff.getByText(/^รหัสบทบาท /)).toBeVisible();await expect(approver.getByText(/^รหัสบทบาท /)).toBeVisible();
  await approver.getByRole('button',{name:'เปลี่ยนการมอบหมาย',exact:true}).click();await page.getByLabel('เหตุผล',{exact:true}).fill('คงสิทธิ์เดิม');
  await page.route('**/scope-assignments/*/replace',()=>{throw new Error('no-op ต้องไม่ส่ง replacement request');});
  await page.locator('form').evaluate(form=>(form as HTMLFormElement).requestSubmit());
  await expect(page.getByRole('status')).toHaveCount(0);await expect(page.getByRole('button',{name:'บันทึกการมอบหมายใหม่',exact:true})).toBeDisabled();
  await approver.getByRole('button',{name:/^ถอนสิทธิ์บทบาท บทบาททดสอบ อนุมัติ/}).click();
  await expect(approver).toHaveCount(0);await expect(staff).toHaveCount(1);
  const history=page.getByRole('listitem').filter({hasText:'ผู้ใช้ e2e-scope-target'}).filter({hasText:'บทบาททดสอบ อนุมัติ'});await expect(history).toContainText('ถอนแล้ว');
});

test('record POST/PATCH ใช้ version จริง ป้องกันส่งซ้ำ และ 503 ไม่ออกจากระบบ',async ({page,browser})=>{
  await login(page,'e2e-scope-staff');await page.goto(path(a));
  const api=`/api/v1/workspaces/${w}/projects/${p}/sites/${a}/scope-probe-records`;
  const noJs=await browser.newContext({javaScriptEnabled:false,storageState:await page.context().storageState()});
  try {const staticPage=await noJs.newPage();await staticPage.goto(path(a));await expect(staticPage.getByRole('button',{name:'สร้างรายการ',exact:true,includeHidden:true})).toBeDisabled();} finally {await noJs.close();}
  let count=0;let release!:()=>void;const gate=new Promise<void>(r=>{release=r;});
  await page.route(`**${api}`,async route=>{count++;await gate;await route.continue();});
  try {
    await page.getByLabel('ข้อความ',{exact:true}).fill('สร้างทดสอบ UI');await page.getByRole('button',{name:'สร้างรายการ',exact:true}).click();
    await expect.poll(()=>count).toBe(1);await expect(page.getByRole('button',{name:'สร้างรายการ',exact:true})).toBeDisabled();
    await page.locator('form').evaluate(form=>(form as HTMLFormElement).requestSubmit());release();
    await expect(page.getByText('สร้างทดสอบ UI',{exact:true})).toBeVisible();expect(count).toBe(1);
  } finally {release();await page.unroute(`**${api}`);}
  const row=page.getByRole('listitem').filter({hasText:'สร้างทดสอบ UI'});
  await row.getByRole('button',{name:'แก้ไขรายการ'}).click();
  const changed=await page.evaluate(async api=>{
    const rows=await (await fetch(api)).json();const row=rows.items.find((x:{note:string})=>x.note==='สร้างทดสอบ UI');
    const csrf=await (await fetch('/api/v1/auth/csrf')).json();
    return (await fetch(api+'/'+row.id,{method:'PATCH',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf.token},body:JSON.stringify({note:'แก้จากอีกหน้าจอ',expectedVersion:row.version})})).status;
  },api);expect(changed).toBe(200);
  await page.getByLabel('ข้อความ',{exact:true}).fill('ค่าเก่าต้องไม่ทับ');await page.getByRole('button',{name:'บันทึกการแก้ไข',exact:true}).click();
  await expect(page.locator('section').getByRole('alert')).toContainText('ข้อมูลเปลี่ยนแปลง');
  await page.getByRole('button',{name:'โหลดข้อมูลใหม่',exact:true}).click();await expect(page.getByText('แก้จากอีกหน้าจอ',{exact:true})).toBeVisible();
  await page.getByRole('listitem').filter({hasText:'แก้จากอีกหน้าจอ'}).getByRole('button',{name:'แก้ไขรายการ'}).click();
  await page.getByLabel('ข้อความ',{exact:true}).fill('บันทึก UI สำเร็จ');await page.getByRole('button',{name:'บันทึกการแก้ไข',exact:true}).click();await expect(page.getByText('บันทึก UI สำเร็จ',{exact:true})).toBeVisible();
  await page.route(`**${api}?*`,route=>route.fulfill({status:503,json:{}}));
  await page.getByRole('button',{name:'โหลดข้อมูลใหม่',exact:true}).click();await expect(page.locator('section').getByRole('alert')).toContainText('บริการไม่พร้อม');
  await expect(page.getByText('บันทึก UI สำเร็จ',{exact:true})).toHaveCount(0);await expect(page).toHaveURL(path(a));
});

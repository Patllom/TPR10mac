import { test, expect } from '@playwright/test';
import { createHmac } from 'node:crypto';
test('production ไม่เปิด technical UI เมื่อไม่มี server-only flag แต่ selector ยังเปิด',async ({page})=>{
  await page.goto('/login'); await page.getByLabel('ชื่อผู้ใช้',{exact:true}).fill('e2e-scope-staff');
  await page.getByLabel('รหัสผ่าน',{exact:true}).fill('e2e-isolated-password-123'); await page.getByRole('button',{name:'เข้าสู่ระบบ',exact:true}).click();
  await expect(page).toHaveURL(/\/portal$/); await page.goto('/portal/scopes');
  await expect(page.getByText('ไซต์ทดสอบ A',{exact:true})).toBeVisible();
  const response=await page.goto('/portal/scopes/11111111-1111-4111-8111-111111111111/projects/22222222-2222-4222-8222-222222222222/sites/33333333-3333-4333-8333-333333333333');
  expect(response!.status()).toBe(404); await expect(page.getByText('ข้อมูลทดสอบเฉพาะ A')).toHaveCount(0);
});

test('production ปิด technical UI แต่ผู้มีสิทธิ์ยังจัดการ Organization และ Assignment ได้',async ({page})=>{
  await page.goto('/login');await page.getByLabel('ชื่อผู้ใช้',{exact:true}).fill('e2e-scope-admin');
  await page.getByLabel('รหัสผ่าน',{exact:true}).fill('e2e-isolated-password-123');await page.getByRole('button',{name:'เข้าสู่ระบบ',exact:true}).click();
  await page.getByRole('button',{name:'เริ่มตั้งค่า MFA',exact:true}).click();
  const secret=await page.getByLabel('คีย์สำหรับกรอกเอง').inputValue();
  const alphabet='ABCDEFGHIJKLMNOPQRSTUVWXYZ234567',bits=[...secret].map(c=>alphabet.indexOf(c).toString(2).padStart(5,'0')).join('');
  const counter=Buffer.alloc(8);counter.writeBigUInt64BE(BigInt(Math.floor(Date.now()/30000)));
  const digest=createHmac('sha1',Buffer.from(bits.match(/.{8}/g)!.map(x=>parseInt(x,2)))).update(counter).digest();
  await page.getByLabel('รหัสยืนยัน 6 หลัก').fill(((digest.readUInt32BE(digest[19]&15)&0x7fffffff)%1000000).toString().padStart(6,'0'));
  await page.getByRole('button',{name:'ยืนยัน MFA',exact:true}).click();await page.getByRole('button',{name:'บันทึกรหัสกู้คืนแล้ว ไปต่อ'}).click();await expect(page).toHaveURL(/\/portal$/);
  await page.goto('/portal/admin/organization');await expect(page.getByRole('button',{name:'สร้างโครงสร้าง',exact:true})).toBeEnabled();
  await page.getByLabel('รหัส',{exact:true}).fill('PROD_UI');await page.getByLabel('ชื่อ',{exact:true}).fill('ทดสอบขอบเขต production');
  await page.getByRole('button',{name:'สร้างโครงสร้าง',exact:true}).click();await expect(page.getByRole('status')).toContainText('บันทึกสำเร็จ');
  await page.goto('/portal/admin/assignments');await expect(page.getByLabel('ผู้ใช้',{exact:true})).toBeEnabled();await expect(page.getByRole('option',{name:'e2e-scope-target',exact:true})).toHaveCount(1);
});

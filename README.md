# TPR-10 เว็บและระบบภายใน

Module 1 เพิ่ม ASP.NET Core API และ PostgreSQL foundation พร้อม /portal และ API ผ่าน origin เดียวกับเว็บ

อ่าน [คู่มือ Module 1](docs/runbooks/module-1-foundation.md) และ [หลักฐานตรวจรับ](docs/architecture/module-1-exit-gate.md)

Development ใช้ port 4000; production process ใช้ port 4001; API ภายในใช้ port 5080

```bash
npm test
npm run lint
npm run build
dotnet tool restore
dotnet test backend/TPR10.sln -c Release
dotnet build backend/TPR10.sln -c Release
dotnet format backend/TPR10.sln --verify-no-changes
```

Landing Page และหน้า authentication ใช้ Next.js 15.5.26, React 19.3, TypeScript และ Tailwind CSS ใช้ Node.js 22.13 ขึ้นไปในสาย 22 หรือ Node 24 ขึ้นไป (`.nvmrc` ระบุรุ่นที่ทดสอบ)

## หน้าปัจจุบัน

หน้าหลักประกอบใน `app/page.tsx` จากส่วนต่อไปนี้:

- ภาพรวมและ telemetry
- บริการทรัพยากรน้ำและระบบโทรมาตร
- โซลูชันดิจิทัลและ AI
- โครงสร้างพื้นฐาน เครือข่าย และความปลอดภัย
- ปุ่มติดต่อและส่วนท้าย

ต้นแบบ AETHERIS เดิมอยู่ใน `archive/aetheris/` และไม่รวมใน TypeScript compilation หรือ runtime ปัจจุบัน

## คำสั่งเว็บ

```bash
npm ci
npm run dev
npm run lint
npm run build
npm run start
```

Development เปิดที่ `http://localhost:4000` ส่วน production process เปิดที่ `http://localhost:4001` หลังรัน `npm run build`

## ขอบเขตปัจจุบัน

- ค่า telemetry ในส่วน hero เป็นข้อมูลจำลองใน browser
- แบบฟอร์มติดต่อแสดงสถานะสำเร็จในเครื่อง ยังไม่เชื่อมอีเมลหรือ backend
- ตัวเลขการตลาดและข้อความรับรองต้องได้รับการยืนยันจากเจ้าของธุรกิจก่อนเผยแพร่
- `/login` เชื่อมบัญชีภายในกับ `/portal`; หน้า MFA/เปลี่ยนรหัสผ่าน/กู้คืน และ `/portal/account` ใช้ API เป็นผู้ตรวจสิทธิ์จริง อ่าน [คู่มือ Identity](docs/runbooks/module-2-identity.md)
- โมดูลธุรกิจและอีเมล reset production ยังไม่เปิดใช้งาน; การผ่าน Task 8 ไม่ใช่การอนุมัติ Security Exit Gate

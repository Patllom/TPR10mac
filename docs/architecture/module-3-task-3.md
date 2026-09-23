# Module 3 — ผลงาน Task 3: จัดการโครงสร้างองค์กร

สถานะ: ผ่านการตรวจรับขอบเขต Task3 แล้ว ไม่ใช่การรับรอง Module 3 ทั้งหมดพร้อมใช้งานจริง

## ขอบเขตที่พัฒนา

- API 12 เส้นทางสำหรับรายการ สร้าง และแก้ไข Workspace, Department, Project และ Site; ต้องมีสิทธิ์ระบบ `organization:manage` และ MFA ล่าสุด การเปลี่ยนข้อมูลต้องผ่าน CSRF
- ตรวจสิทธิ์ซ้ำใน service หลังล็อกธุรกรรม รวมถึงการอ่านรายการ เพื่อไม่ใช้สิทธิ์ที่ถูกถอนระหว่างทาง
- รหัสองค์กรเป็น ASCII ความยาว 1–64 ตัวอักษร ปรับเป็นตัวพิมพ์ใหญ่และไม่ซ้ำภายใน parent; ชื่อยาวไม่เกิน 200 และเหตุผลไม่เกิน 500 ตัวอักษร ไม่รับ control characters
- PATCH ต้องส่งชื่อ สถานะ เวอร์ชัน และเหตุผลครบ ไม่อนุญาตแก้รหัส ย้าย parent หรือส่ง field อื่น; เวอร์ชันเก่าตอบ 409 และ parent ไม่ตรงตอบ 404
- รายการเริ่มต้น 25 รายการ จำกัด 100 เรียงคงที่ และป้องกัน offset overflow; ยังอ่าน metadata ขององค์กรที่ปิดได้เพื่อบริหารการเปิดคืน
- การปิด Workspace/Project/Site ถอน assignments ใน subtree และ sessions ของผู้ได้รับผลกระทบแต่ละคนครั้งเดียวในธุรกรรมเดียว การปิด Department ไม่ถอนสิทธิ์ Site
- ไม่เปลี่ยนสถานะลูกทุกตัวตาม parent แต่สถานะ parent ที่ปิดทำให้ลูกใช้งานไม่ได้ การเปิดคืนไม่คืน assignments อัตโนมัติ
- บันทึก audit ทั้งผลสำเร็จและการปฏิเสธภายใน service โดยระบุ actor และ scope แบบมีโครงสร้าง หาก audit ล้มเหลวตอบ 503 และย้อนธุรกรรมทั้งหมด ไม่เก็บชื่อ/รหัสองค์กรหรือ request body ใน metadata
- OpenAPI เพิ่มสัญญา organization-control-plane โดยคงข้อยืนยันเส้นทาง identity เดิมครบ

## หลักฐาน TDD

เห็นการทดสอบ API ล้มเหลวจากเส้นทางที่ยังไม่มี และ lifecycle ล้มเหลวก่อนเขียน implementation จากนั้นผ่าน 34 กรณีแรก เพิ่ม OpenAPI แล้วเห็น 12 กรณีล้มเหลวจาก scope metadata เดิม ก่อนแก้ transformer

ชุดขยายตรวจขอบเขตค่า การไม่เปลี่ยนข้อมูลเมื่อ PATCH ค่าเดิม และการแข่งขัน create/update ผ่าน advisory lock ด้วยสอง DbContext จริง พบปัญหาชุดทดสอบขอ CSRF token ซ้ำเกิน rate limit จึงแก้ให้ใช้ token ที่ยัง valid ซ้ำ โดยไม่ลด limiter ของระบบ ทดสอบ matrix สิทธิ์ซ้ำผ่าน 5/5

## ผลตรวจรับ

ผลโค้ดสุดท้ายหลังแก้ review: backend 504/504 ไม่มี skipped, backend Build ไม่มี warning/error, format verification ผ่าน; Node tests 28/28, ESLint และ Next.js production build ผ่าน HTTPS browser E2E หลังแก้ review ผ่าน 13/13 พร้อมตรวจ cookie/CSRF/host ผ่าน proxy จริง

หลักฐานคำสั่ง:

```sh
dotnet test backend/TPR10.sln --no-restore
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm test
npm run lint
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
```

ใช้ SDK 10.0.401 ตาม `global.json` โดยไม่อัปเกรด dependency; logs ของรอบนี้อยู่ `/private/tmp/tpr10-m3t3-*.log` ซึ่งเป็นไฟล์ชั่วคราว ส่วนรายงานนี้เก็บผลสรุปถาวรใน Git

## การตัดสินใจและข้อจำกัด

ผู้ตรวจอิสระ Dewey ไม่พบ Critical และพบ Important 1 ข้อ: JSON/UUID/query binding ที่ตอบ 400 ก่อนเข้า service ยังไม่มี denial audit จึงเพิ่ม regression 10 กรณีและเห็น RED ครบก่อนเพิ่ม middleware สำหรับเส้นทางองค์กรโดยเฉพาะ ขอบเขตนี้ทำงานหลัง authorization/CSRF/session และไม่อ่านหรือบันทึก body; regression ผ่าน GREEN 10/10 และชุดรวม 504/504 แล้ว ไม่ส่ง review ซ้ำตามกระบวนการแก้หนึ่งรอบของ Superpowers

Minor ที่เลื่อนไปติดตาม: หาก session ถูกถอนหลัง middleware ตรวจแต่ก่อน service ได้ lock ระบบปฏิเสธด้วย 403 แทน 401 การเข้าถึงยังถูกปิดและมี audit แต่ response contract ยังควรปรับพร้อม regression เฉพาะกรณีนี้

- เพิ่มการแก้ OpenAPI transformer/tests จากรายการไฟล์ขั้นต่ำของ Task3 เพราะข้อกำหนดร่วมบังคับสัญญาของทุก endpoint ตั้งแต่เปิดใช้
- ตรวจอิสระเฉพาะ Task3 เพิ่มตามข้อกำหนดส่งมอบของผู้ใช้ ไม่แทน whole-branch review ใน Task9
- ผู้ตรวจไม่รันชุดทดสอบซ้ำ ผล runtime ในรายงานเป็นการตรวจโดยผู้พัฒนา ไม่ใช่หลักฐานสองชุดอิสระ
- Assignment/scoped discovery/data/export อยู่ Tasks4–7, Portal/cache อยู่ Task8 และการตรวจรวมทั้ง branch/OpenAPI/runbook อยู่ Task9; หากใช้งานก่อนครบจะยังไม่มี workflow ธุรกิจที่รับรองได้
- การถือ lock ระหว่างอ่านรายการเลือกความถูกต้องของสิทธิ์เป็นหลัก ยังไม่รับรอง throughput production
- ข้อความเหตุผลผ่านการจำกัดรูปแบบ แต่ไม่ใช่ระบบ DLP ผู้ใช้ยังต้องไม่กรอกความลับ
- ยังไม่มีหน้าจัดการองค์กร, Assignment API, scope data-plane หรือ export ในรอบนี้ งานเหล่านั้นอยู่ Tasks4–9
- ไม่เปลี่ยนพอร์ต: development 4000 และ production preview 4001
- ตรวจครบก่อน local commit บน `codex/module-3-organization-scope` ยังไม่ push หรือ merge และไม่ได้แก้งานค้างของ main
- ขั้นต่อไปคือ Task4: Assignment API ที่ไม่ให้ผู้ดูแลเพิ่มสิทธิ์ตัวเอง โดยรอผู้ใช้สั่งเริ่ม

# Module 6A — ผลตรวจ Task 1: โครงสร้างบุคลากรและรายการสิทธิ์

วันที่: 25 กันยายน 2026 (เวลาไทย)

สถานะ: Task1 ผ่านการตรวจรับแล้ว ชุด backend ทั้งหมดจบด้วย exit0 เมื่อ24กันยายน2026 เวลา19:17 UTC (25กันยายน เวลา02:17ไทย) ยังไม่ใช่การปิด6Aทั้งหมด

## ขอบเขตที่ทำ

- เพิ่ม EmployeeMembership, ReportingLine และ HrAssignment พร้อมประวัติช่วงเวลา UTC แบบรวมจุดเริ่มแต่ไม่รวมจุดสิ้นสุด และ version
- ใช้ foreign key แบบคู่ ป้องกันแผนกข้าม Workspace และสายบังคับบัญชาอ้างพนักงานผิด membership
- ป้องกันพนักงานมีต้นสังกัด/หัวหน้าทับช่วงเวลา และป้องกัน HR assignment ซ้ำในหน่วยเดียวกัน รวมกรณีสอง transaction เขียนพร้อมกัน
- เพิ่มสิทธิ์7รายการ รวมทั้งหมด19รายการ โดยคง ID/ชื่อ12รายการเดิม ไม่แจกสิทธิ์ใหม่ให้ Admin อัตโนมัติ
- สร้าง migration `20260924185530_AddAttendanceDirectory` ด้วย EF และตรวจการขึ้น–ลง–ขึ้นบนฐานทดสอบที่ทิ้งได้

## หลักฐาน TDD และการตรวจรับ

| การตรวจ | ผลที่ยืนยันแล้ว |
| --- | --- |
| RED ก่อน implementation | พบตารางหายและ catalog12แทน19ตามคาด |
| RED หลังสร้างตาราง ก่อนเพิ่ม trigger/guard | 21ผ่าน/15ไม่ผ่าน แสดงว่าข้อบังคับ overlap และ downgrade ยังขาด |
| Tests เฉพาะงานและ catalog regression | 39ผ่าน ไม่มีล้มเหลวหรือข้าม |
| Frontend tests | 35ผ่าน ไม่มีล้มเหลวหรือข้าม |
| Backend/fixture Build | ผ่าน ไม่มี warning/error |
| Backend/fixture format | ผ่าน |
| Frontend Build และ Lint | ผ่าน |
| Backend regression ทั้งหมดหลังแก้ | 865/865ผ่าน ไม่มีล้มเหลวหรือข้าม; exit0 |
| Code Review โดย reviewer แยก | ไม่พบ Critical/Important/Minor ที่ต้องแก้ |

ชุด tests ใหม่ใช้ PostgreSQL จริง ไม่ใช้ EF InMemory; ทดสอบ foreign keys, ช่วงเวลาติดกัน/ทับซ้อน, การเขียนพร้อมกัน, ประวัติข้าม Workspace, catalog และการย้อน migration

คำสั่งหลักที่ใช้ตรวจ (เรียกใน worktree แยกด้วย Node/.NET ของโครงการ):

```sh
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceCatalogTests|FullyQualifiedName~AttendanceDirectorySchemaTests|FullyQualifiedName~ScopeCatalogTests|FullyQualifiedName~AuthorizationMigrationTests|FullyQualifiedName~Role_lifecycle_catalog_and_validation'
dotnet test backend/TPR10.sln --logger 'console;verbosity=normal'
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
dotnet build backend/tests/TPR10.E2E.Fixture
dotnet format backend/tests/TPR10.E2E.Fixture --verify-no-changes --no-restore
npm test
npm run lint
npm run build
git diff --check
```

`npm ci` ใช้ lockfile เดิมและมี deprecation warnings ของ dependencies เดิม ไม่ได้อัปเกรด dependency ใน Task1 ไม่มีการรัน HTTPS E2E ของหน้าจอใหม่เพราะยังไม่มีหน้าจอในระยะนี้; เกณฑ์ E2E ทั้ง6Aยังอยู่ในTask6

## ข้อผิดพลาดระหว่างตรวจและการแก้

- Fixture ทดสอบ FK เดิมใช้ผู้ใช้เดียวกับหัวหน้า จึงถูก self-manager check ก่อน FK แก้เป็นผู้ใช้คนที่สาม ไม่ผ่อน constraint
- การเก็บกวาด race test พยายาม rollback transaction ที่ commit แล้ว แก้ให้ติดตามสถานะ commit โดยคง assertions การแข่งขันเดิม
- Baseline รอบแรกผ่าน827และล้มเหลว2 tests ของ Bootstrap เพราะทดสอบเก่าเรียก subprocess จาก binary ที่ถูก build ใหม่ระหว่างรัน ทำให้ schema ต่างรุ่น ตรวจสองกรณีด้วย source เดิมและ output แยกแล้วผ่าน2/2 จึงไม่แก้โค้ด Bootstrap ไม่ถือรอบแรกเป็นชุดทดสอบที่ผ่านครบ และไม่ build ทับขณะรัน verification อีก

## การตัดสินใจเพิ่มเติมและผลกระทบ

1. ทำเฉพาะ Task1 ตามคำสั่ง ไม่ทำทั้ง6Aต่ออัตโนมัติ — Tasks2–6ยังค้าง
2. นำแผนที่อนุมัติจาก main มายัง worktree โดยคงต้นฉบับ — เอกสารเดิมบน main ยังไม่commit ต้องตรวจความต่างก่อนรวม branch
3. Downgrade ปฏิเสธเมื่อมีประวัติบุคลากรหรือ grants ของสิทธิ์ใหม่ทั้ง7 — ไม่ลบข้อมูลเงียบ ๆ; ผู้ดูแลต้องเตรียมการย้าย/เก็บประวัติก่อนย้อนรุ่น
4. Trigger รองรับการเขียนแบบ READ COMMITTED เท่านั้น — ปฏิเสธ isolation แบบ snapshot เพื่อไม่ให้มองข้าม transaction ที่ commit ระหว่างรอ lock; หากภายหลังต้องใช้ isolation อื่นต้องออกแบบใหม่
5. ปรับจำนวน catalog ใน regression tests เดิมให้ตรง19 โดยคงการตรวจ grant เดิม — ไม่ลบ tests หรือผ่อนการตรวจสิทธิ์
6. Reviewer ไม่ตัดสิน graph cycles, effective-now/history immutability, self-grant/role-class rules, contextual read, API/UI และ revocation ของ6Aทั้งหมด เพราะอยู่ใน Tasks2–6ตามแผน — ไม่ได้ยกเลิกข้อกำหนด และยังห้ามถือ Task1 เป็นระบบที่พร้อมใช้งานจริง

ไม่มีประเด็นย่อยที่ reviewer ให้เลื่อนไปแก้ภายหลัง

## สิ่งที่ยังไม่ได้ทำ

ยังไม่มี API/หน้าจัดการบุคลากร การตัดสินสิทธิ์อ่านรูป/GPS เส้นทางอนุมัติ กล้อง ลงเวลา หรือคำร้อง ไม่ได้เปลี่ยน Preview ไม่ได้ push/merge/deploy และยังไม่ปิด Module6A ทั้งชุด

ขั้นต่อไปเมื่อผู้ใช้สั่ง: Task2 — กฎช่วงเวลาและการตรวจสายบังคับบัญชาวนวง

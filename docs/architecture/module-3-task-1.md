# รายงาน Module 3 — Task 1: ฐานข้อมูลและประเภทสิทธิ์

วันที่: 2026-09-24 (Asia/Bangkok)
สถานะ: Task1 ผ่านการตรวจรับด้านเทคนิคแล้ว พร้อมเริ่ม Task2 ตามคำสั่งผู้ใช้ ยังไม่ใช่การปิด Module3 หรืออนุมัติ production
Branch: `codex/module-3-organization-scope`
ฐานก่อนเริ่ม: `99523a868a4465ef9215b2054e4066a0ed84cd2e`

## ขอบเขตที่ทำ

- เพิ่มตาราง Workspace, Department, Project, Site, scoped assignment และ technical record
- ผูก composite foreign keys ให้ identifier ของ Project/Site อยู่ใน parent ที่ตรงกัน
- บังคับรูปแบบ nullable scope และข้อมูลการถอนสิทธิ์ด้วย CHECK constraints
- ป้องกัน active assignment ซ้ำด้วย partial unique indexes แยกสามระดับ โดยเก็บประวัติรายการที่ถอนแล้วได้
- เพิ่ม `Permission.Domain` แยก `system` กับ `scoped-business`; catalog รวม12รายการ ส่วน Administrator ได้8สิทธิ์ระบบ ไม่มีสิทธิ์ธุรกิจอัตโนมัติ
- เพิ่ม ScopeKey และชุดข้อมูลทดสอบสำหรับงานถัดไป โดยไม่สร้างข้อมูลโครงสร้างหรือ assignment ใน production
- เพิ่ม migration `20260923173517_AddOrganizationScopeFoundation` และทดสอบ apply/down/reapply บนฐานชั่วคราว

## หลักฐาน TDD และการตรวจ

| รายการ | ผลที่ตรวจแล้ว |
| --- | --- |
| Baseline Backend | 356/356 ผ่านก่อนแก้ implementation |
| Baseline Node | 28/28 ผ่าน |
| RED schema/catalog | 27 failures: ยังไม่มีตารางและ catalog มี6แทน12 |
| RED ScopeKey | 4 failures สำหรับ identifier ว่าง/ไม่มี parent; 3 valid cases ผ่าน |
| GREEN เฉพาะ Task1 | 39/39 ผ่าน รวม EF mapping และ negative FK แยก nullable levels |
| พิสูจน์ tests หลัง review | ถอด FK เฉพาะฐานชั่วคราวแล้วล้ม4/4; คืน schema จาก migration แล้วผ่าน |
| Node tests หลังแก้ | 28/28 ผ่าน |
| Frontend production Build และ ESLint | ผ่าน |
| Backend Build และ format check | ผ่าน; Build ไม่มี warning/error |
| Backend regression ทั้งชุด | รอบสุดท้ายหลังแก้ review ผ่าน395/395 ไม่มี skipped; ใช้เวลา4.67นาที |
| Browser E2E ผ่าน HTTPS พอร์ต4001 | ผ่าน13/13 รวม TLS/cookie/CSRF acceptance; production code ไม่เปลี่ยนหลังรอบนี้ |
| Code Review อิสระ | ตรวจแล้ว; แก้ช่องว่าง FK tests ตามหลักฐาน RED/GREEN โดยไม่เรียก review ซ้ำ เหลือ Minor1ข้อ |

ทดสอบฐานข้อมูลด้วย PostgreSQL17 จริง ไม่ใช้ EnsureCreated หรือฐาน in-memory ผลรอบนี้ไม่ใช่หลักฐานว่าการตรวจสิทธิ์ API ของ Module3 เสร็จแล้ว

## การตัดสินใจและข้อจำกัด

1. ทดสอบ UUID ว่างใน ScopeKey ก่อน เพราะ Task1 ยังไม่มี scoped API; ต้องทดสอบ HTTP400 ใน Task3/6 ต่อ มิฉะนั้นจะขาดหลักฐานที่ HTTP boundary
2. ทำใน native worktree และนำเฉพาะแผนที่อนุมัติมาด้วย ไม่รวมไฟล์ backup เดิมของ main และไม่แก้ runtime ของ main
3. เพิ่ม review อิสระเฉพาะ Task1 หนึ่งรอบตามขอบเขตส่งมอบของผู้ใช้ มีต้นทุน review เพิ่ม และไม่แทน review ทั้ง Module3 ใน Task9
4. ใช้ SDK10.0.401 ที่ติดตั้งไว้จริงโดยแก้ PATH เฉพาะคำสั่ง ไม่ลดเวอร์ชันใน global.json
5. Migration down ลบข้อมูลโครงสร้าง/assignment/technical record และ grants ใหม่ของ Module3 จึงห้าม downgrade ฐานจริงหลังมีข้อมูลโดยไม่มีแผนเก็บข้อมูลและสิทธิ์ การทดสอบ roundtrip ใช้ฐานชั่วคราวเท่านั้น

## สิ่งที่ยังไม่อยู่ใน Task1

### ผล Code Review และรายการค้าง

ผู้ตรวจอิสระอ่าน source/tests รวมไฟล์ใหม่ทั้งหมด ไม่พบ implementation defect ระดับ Critical/Important และเสนอเพิ่มหลักฐาน tests2ข้อ:

- ช่องว่าง negative FK tests ระดับ Workspace-only/Project-only: ผู้พัฒนายกระดับเป็น Important เพราะ FK ระดับ Site อาจทำให้ test ผ่านแม้ FK ของ parent หาย เพิ่ม4กรณีและพิสูจน์ด้วย mutation ในฐานชั่วคราวแล้ว หากละเลยจะจับ schema regression ที่เสี่ยงข้ามพื้นที่ไม่ได้
- Minor ที่ยังค้าง: roundtrip ตรวจจำนวน session/user-role และ audit immutability แต่ยังไม่ได้เทียบ session ID/hash/stage/expiry และ identity/audit metadata ทุกfieldก่อน–หลัง หาก migration ในอนาคตแก้ค่าโดยจำนวนแถวคงเดิม tests อาจไม่จับ รอบนี้ตรวจ SQL แล้วไม่มีคำสั่งเปลี่ยนค่าเหล่านั้น

สิ่งที่ผู้ตรวจไม่ได้ตัดสินคือ API/MFA/lifecycle/race/UI ของ Tasks2–9, HTTP400 ที่จะทดสอบใน Task3/6 และ downgrade ฐานจริง/production readiness ผู้พัฒนาคงขอบเขตเหล่านี้ตามแผน ไม่ถือว่า review Task1 อนุมัติให้เปิดระบบธุรกิจหรือ downgrade ข้อมูลจริง

### งานต่อไป

- การกรอง global permissions และ MFA/session lifecycle ของ scoped roles อยู่ Task2
- Organization/assignment API, scoped authorization และ field visibility อยู่ Tasks3–7
- หน้าจอและ browser acceptance ของ Module3 อยู่ Task8; E2E รอบนี้เป็น regression ของ identity เดิม
- OpenAPI, whole-branch review, policy/production sign-off ยังเป็นงานต่อไป

ไม่มีการ push, merge หรือ deploy จากการสั่งทำ Task1

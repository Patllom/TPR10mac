# รายงาน Module 7D Task 1 — โครงสร้างข้อมูลและการคำนวณเงิน

สถานะ: ผ่านการตรวจรับ Task 1 — Test, Build, Lint/Format และการตรวจอิสระครบ

## ขอบเขต

ทำเฉพาะ Task 1 ของ [แผน 7D](../superpowers/plans/2026-10-31-module-7d-settlement-refunds.md): entities/contracts/math, EF configuration, migration จาก 7C และเทสต์ฐานข้อมูลจริงจำลอง ไม่รวม workflow, API, หน้าจอ หรือ Task 2

ฐานก่อนเริ่ม `a91cce472ce89ca429be8f51a80de9b3542375cf` บน worktree เดิม สงวนงานค้าง 14 paths ของ HTTPS 504/รายงาน 7B ไม่รวมใน commit นี้

ผู้ลงมือ: Avicenna; ผู้ตรวจอิสระ: Herschel ตรวจข้อกำหนดและคุณภาพผ่านหลังแก้รอบ 1 ผู้ประสานงานเป็นผู้รันชุดตรวจร่วมและ commit หลังตรวจครบ

## ข้อกำหนดสำคัญ

- จำนวนไม่เกิน 4 ตำแหน่ง เงิน/อัตราไม่เกิน 2 ตำแหน่ง ปัดบรรทัดเป็นสตางค์แบบหลักที่สามตั้งแต่ 5 ปัดขึ้น
- ยอดคืนรอตรวจไม่ลดภาระที่รับรองแล้ว
- ยอดรายการเป็นจำนวนไม่ติดลบตามชนิดข้อมูล แต่ยอดคงค้างสุทธิอาจติดลบตามแบบ §10 ต้องแสดงภาระบริษัท ไม่ตัดเป็นศูนย์
- immutable accepted history, scope FK, unique command/application/closure และผลต่างที่ไม่ลงซ้ำ
- migration ไม่สร้างยอดเงิน สิทธิ์ หรือนโยบายวันแก้ไขสมมติ ไม่ใช้ฐานข้อมูลจริง

## หลักฐานก่อนเริ่ม

Frontend baseline `npm test` ผ่าน 175 กรณี ไม่มี failure โค้ดก่อนเริ่มตรง hash ของชุดตรวจ 7C ล่าสุด ผลเก่านั้นเป็นเพียง baseline ไม่ใช่การรับรอง 7D

## TDD และผลตรวจ

เทสต์คำนวณมี runtime RED จาก API ที่ยังไม่มี และ schema มี runtime RED จากตารางที่ยังไม่ถูกสร้าง หลังเพิ่มโครงสร้าง focused tests ผ่าน 17 กรณี (math 4, schema 13)

ผู้ตรวจพบ P2: document guard อาจเห็น revision ที่ยังไม่ commit แล้วอนุญาตแก้เอกสารขณะรับรอง เพิ่มเทสต์สอง PostgreSQL connections ทั้งการรับรอง revision และ allocation พบ RED จริงทั้งสองกรณี แก้ให้ล็อก referencing revisions ตามลำดับ GUID และ recheck หลังรอ เทสต์ใช้ `pg_blocking_pids` ยืนยันการรอ ไม่ใช่อาศัยเวลาหน่วง หลังแก้ schema ผ่าน 15 กรณี ผู้ตรวจยืนยัน ADDRESSED และไม่พบข้อสำคัญใหม่

Migration เพิ่ม 16 entities; เปรียบเทียบ snapshot พบ entity เดิม 76 blocks คงเดิม มี scoped alternate key บน payment holds เพื่ออ้างอิงเหตุเดิม ตารางใหม่มี check/FK/unique และ accepted-history guards รัน idempotent SQL ซ้ำสองครั้งผ่าน ไม่สร้าง money/grants หรือค่าธุรกิจสมมติ

ผลสุดท้าย: Backend ผ่าน 3,083 กรณีทั้งหมด (รวม Task 1 จำนวน 19 กรณี) ไม่มี failure; Frontend ผ่าน 175 กรณี Build ทั้งสองส่วนและ Lint/Format ผ่านครบ ผู้ตรวจอิสระผ่านทั้งข้อกำหนดและคุณภาพหลังแก้ P2

ชุด Backend ใช้เวลา 1.6028 ชั่วโมง จบ 2026-10-31 10:43:18 UTC (17:43:18 เวลาไทย) ผลตรวจ hash ยืนยัน source ไม่เปลี่ยนระหว่างรัน งานค้างเดิม 14 paths คงเดิมทั้งหมด

Frontend ตรวจบน source เดิมของ frontend; หลังผลนั้นเปลี่ยนเฉพาะ backend 4 paths จึงใช้ผลเดิมได้ ไม่ได้อ้างว่า rerun Frontend หลังแก้ race ตัวคำนวณเป็น static และ config อยู่ใน DbContext จึงไม่แก้ FieldAdvanceRegistration.cs โดยไม่จำเป็น

| คำสั่ง | ผล | เวลาตรวจเสร็จ UTC |
|---|---|---|
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน (exit 0) | 2026-10-31T09:04:27.056945+00:00 |
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | ผ่าน (exit 0) | 2026-10-31T09:07:07.851911+00:00 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger console;verbosity=normal` | ผ่าน (exit 0) | 2026-10-31T10:43:18.617834+00:00 |
| `npm test` | ผ่าน (exit 0) | 2026-10-31T08:57:11.953279+00:00 |
| `npm run build` | ผ่าน (exit 0) | 2026-10-31T08:57:24.516936+00:00 |
| `npm run lint` | ผ่าน (exit 0) | 2026-10-31T08:57:28.388232+00:00 |
| `git diff --check` | ผ่าน (exit 0) | 2026-10-31T08:57:28.456268+00:00 |

หลักฐาน local: `/tmp/tpr10-module7d-task1-final2-G-backend-summary.jsonl`, `/tmp/tpr10-module7d-task1-final2-G-test.log`, `/tmp/tpr10-module7d-task1-final-G-frontend-summary.jsonl` และ `/tmp/task1-fix1-format-verify.log` เวลา Format คำนวณจากเวลาเริ่มและ duration ใน command ledger ของผู้ลงมือ

ข้อความ `Failed executing DbCommand` ระหว่างชุดทดสอบเป็น log ของการทดสอบข้อผิดพลาด ไม่ใช่ผลเทสต์ FAIL; ใช้ผลสรุปของ test runner และ exit code เป็นข้อยืนยัน

ข้อจำกัดขอบเขต: Task นี้สร้างฐานข้อมูล สัญญาข้อมูล และคำนวณเงิน ยังไม่ใช่ workflow เคลียร์เงิน/คืนเงินที่ใช้งานผ่าน API หรือหน้าจอ และไม่รับรองกติกาข้ามตารางของ services ใน Task ถัดไป


ประวัติที่ไม่ถือเป็นผลผ่าน: Format ครั้งแรก exit2; fixture cleanup ของ race ครั้งแรกหลังแก้ยังผิดและถูกแก้ก่อน schema GREEN; รายการ compile/fixture/audit utility failure ในรายงานผู้ลงมือไม่ได้ใช้แทน behavioral RED

## รายการคงติดตาม

คง M2-R1/R2-C1, HTTPS 504, Minor log, OpenAPI 500, T13-M1 และ T14-M1 ไม่ปิดด้วยเทสต์ที่คล้ายกัน พักมือถือจริงและ NAS จริง ไม่มีการ push/merge/deploy หรือ migrate ฐานจริงใน Task นี้

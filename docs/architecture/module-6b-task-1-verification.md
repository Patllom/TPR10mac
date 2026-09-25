# Module 6B — ผลการตรวจ Task 1: แบบจำลองหลักฐานและที่เก็บ

วันที่ 25 กันยายน 2026

สถานะ: Task 1 ผ่านเกณฑ์ TDD, Code Review และ Test/Build/Lint แล้ว ตรวจผลรอบส่งมอบวันที่ 25 กันยายน 2026 เวลา 04:16 UTC

## ขอบเขต

ดำเนินการเฉพาะ Task 1 ตามคำสั่งผู้ใช้ บนสาขา `codex/module-6b-evidence-storage` จากฐาน `0cfde59` ไม่เปลี่ยน checkout หลัก ไม่หยุด Preview และไม่ใช้ฐานข้อมูลจริงทดสอบ migration

- เพิ่มตารางหลักฐาน สำเนาภาพ การผูกเหตุการณ์ ที่เก็บ ปลายทางเขียน งานย้าย และรายการย้าย รวม 7 ตาราง
- เพิ่มสัญญาข้อมูลสำหรับ Task ถัดไป ไม่มีการอ่านหรือเขียนไฟล์จริงใน Task นี้
- เก็บ checksum/ขนาด/มิติภาพเต็มและภาพย่อแยกกัน รหัสหลักฐานคงเดิมเมื่อย้ายสำเนา
- ผูก snapshot เจ้าของ–สมาชิก–หน่วยงานด้วย foreign key แบบหลายคอลัมน์
- ป้องกันแก้ไขตัวตน วันเวลา และข้อมูลภาพที่เตรียมแล้ว ป้องกันลบ/ล้างประวัติ
- ตรวจการเผยแพร่ตอนจบ transaction: ต้องมีการผูกเหตุการณ์และสำเนาที่ตรวจสอบแล้วทั้งภาพเต็มและภาพย่อ
- การ downgrade ต้องหยุดทั้ง transaction หากมีข้อมูลในตารางใหม่ ไม่ถอนกลไกรักษาประวัติของ Module 6A

## หลักฐาน TDD

1. ก่อนเพิ่ม schema: ชุดทดสอบพบตารางที่ยังไม่มี 41 กรณี อีก 1 กรณีทดสอบพฤติกรรมเดิมผ่าน ไม่อ้างว่าทั้ง 42 กรณีเป็น RED
2. หลังเพิ่ม schema: พบข้อมูลตั้งต้นของการทดสอบ SQL ขาดค่า `accept_writes` จากนั้นพบ `attempts` ขาดค่า แก้เฉพาะข้อมูลตั้งต้นให้ระบุค่าอย่างชัดเจน ไม่ลดข้อบังคับ NOT NULL
3. ชุดทดสอบเฉพาะ Task ผ่าน 42/42 กรณี ทดสอบบน PostgreSQL ชั่วคราว ไม่ใช้ mock แทนฐานข้อมูล
4. Code Review พบว่าข้อมูลภาพที่เตรียมแล้วเปลี่ยนได้เมื่อเป็น Orphan: เพิ่ม regression 6 กรณี เห็น RED ทั้ง 6 เพราะไม่มี exception ก่อนแก้ guard ให้รักษา metadata หลังออกจาก Reserved แล้วทั้ง 6 ผ่าน
5. ปรับ test ให้เพิ่ม version อย่างถูกต้องก่อนลองแก้ข้อมูลห้ามแก้ ตรวจข้อความ guard/ชื่อ constraint โดยตรง และตรวจ reservation ผิดรูปแบบขณะ INSERT ไม่ใช่หลังสร้างแล้ว นอกจากนี้แก้ตัวแทนข้อความของ field ไม่ให้กระทบ field ภาพย่อโดยบังเอิญ
6. พิสูจน์ความไวของ test ด้วย mutation ในฐานข้อมูลทดสอบชั่วคราว: แทน guard เฉพาะด้านด้วย guard เรื่อง version อย่างเดียว ชุด immutability ล้ม 9/9 ตามคาด จากนั้นถอน mutation ออกจาก fixture และตรวจว่าโค้ดส่งมอบไม่เหลือคำสั่งแทน guard
7. หลังแก้ review ชุดทดสอบเฉพาะ Task ผ่าน 49/49 รวมกรณีเปลี่ยนข้อมูลการทำงานที่อนุญาตด้วย version ใหม่
8. Regression ทั้งชุดพบ 3 กรณีล้มใน `AttendanceDirectorySchemaTests.Downgrade_with_new_grants_is_refused_without_erasing_grant_or_schema` สำหรับ `attendance:record`, `attendance:directory-manage` และ `attendance:hr-read` สาเหตุคือ test สมมติว่า migration ล่าสุดยังเป็น 6A แต่ 6B ที่ว่าง downgrade ได้ก่อน 6A ปฏิเสธตามปกติ จึงปรับ fixture ให้เริ่มจาก migration รุ่น 6A และเปรียบเทียบประวัติที่ใช้จริงก่อน/หลัง โดยคง assertion ว่าสิทธิ์ไม่ถูกลบไว้ ไม่แก้ production เพื่อบังคับพฤติกรรมผิดให้ผ่าน test
9. รอบที่พบปัญหาจบด้วย 1,043 ผ่าน / 3 ล้ม จาก 1,046 กรณี (23.13 นาที) ไม่อ้างเป็นผลสำเร็จ หลังแก้ fixture ชุด schema ของ 6A และ 6B ผ่านรวม 86/86 และเริ่ม regression ทั้งชุดใหม่ด้วย binaries ชุดส่งมอบ
10. รอบส่งมอบหลังแก้ fixture ผ่าน 1,046/1,046 ใช้เวลา 22.77 นาที ไม่มีการข้าม test ตรวจ checksum ของไฟล์โค้ดและ test หลังรันแล้วตรงกับชุดที่ใช้เริ่มรันทั้งหมด

## ผลตรวจระหว่างงาน

| รายการ | ผลล่าสุด |
| --- | --- |
| `dotnet test backend/TPR10.sln --filter FullyQualifiedName~EvidenceSchemaTests` | ผ่าน 49/49 หลังแก้ review (แยก artifacts ระหว่างตรวจ) |
| Schema regression ของ 6A และ 6B หลังปรับ fixture | ผ่าน 86/86 |
| `npm test` | ผ่าน 40/40 |
| `npm run lint` | ผ่าน |
| `npm run build` | ผ่าน |
| `dotnet build backend/TPR10.sln --no-restore` | ผ่าน ไม่มี warning/error |
| `dotnet format backend/TPR10.sln --verify-no-changes` | ผ่าน |
| Backend regression ทั้งชุด | ผ่าน 1,046/1,046 หลังแก้ review และ fixture |
| Code Review อิสระเฉพาะ Task 1 | พบ Important 2 เรื่อง แก้และพิสูจน์แล้ว ไม่มี Critical/Minor |

Backend regression รอบก่อนแก้ review ถูกยกเลิกอย่างชัดเจนหลังพบข้อบกพร่อง ไม่นับเป็นผลผ่าน รอบถัดไปใน `--artifacts-path /private/tmp/tpr10-module6b-task1-review` พบปัญหา fixture 3 กรณี ส่วนรอบส่งมอบที่ผ่านครบใช้ `--artifacts-path /private/tmp/tpr10-module6b-task1-final` แยก binaries ชัดเจน

คำสั่งรอบส่งมอบ backend: `dotnet test backend/TPR10.sln --artifacts-path /private/tmp/tpr10-module6b-task1-final --no-build --logger 'console;verbosity=normal'` หลัง build โค้ดชุดเดียวกันด้วย artifacts path เดียวกัน ผล exit code 0

หลักฐานสำคัญ: `task1-backend-delivery.log`, `task1-schema-regression-green.log`, `task1-backend-build-regression.log`, `task1-format-regression.log`, `task1-node-final.log`, `task1-lint-final.log`, `task1-frontend-build-final.log` และ `task1-delivery-source.sha256`

บันทึกการรันระหว่างงานอยู่ใน `.superpowers/sdd/2026-09-25-module-6b-evidence-storage/` ซึ่งเป็นข้อมูลในเครื่อง ไม่ commit log ที่ยาวทั้งหมดลง repository

## การตัดสินใจและข้อจำกัด

1. ทำเฉพาะ Task 1 แม้กระบวนการ Native รองรับทำต่อเนื่องทั้งแผน; Tasks 2–8 รอคำสั่งถัดไป
2. เพิ่ม metadata ของภาพย่อแยกจากภาพเต็ม เพื่อให้ตรวจ checksum แต่ละไฟล์ได้ถูกต้อง; Task 5 ต้องใช้ข้อมูลทั้งสองชุด
3. เพิ่ม alternate key ของสมาชิกจาก 6A เพื่อรองรับ foreign key หลายคอลัมน์ โดยไม่เปลี่ยนสิทธิ์; มีต้นทุน index เพิ่ม
4. `EventId` เป็นรหัสอ้างอิงห้ามซ้ำและห้ามแก้ไข แต่ยังไม่มี foreign key ไปเหตุการณ์ลงเวลา เพราะตารางนั้นอยู่ใน 6C; ต้องเติมก่อนเปิดการลงเวลาจริง
5. ตรวจ Code Review เฉพาะ Task นี้เพิ่มจาก whole-module review ใน Task 8 เพื่อทบทวนฐานข้อมูลก่อนนำไปต่อยอด
6. ใช้ transaction lock ร่วมกับ 6A และอนุญาตการเขียนที่ READ COMMITTED เท่านั้น ต้องตรวจ contention ก่อนใช้งานจริง; ไม่มี filesystem I/O ภายใต้ lock ใน Task นี้
7. ปฏิเสธ downgrade เมื่อมีข้อมูลในตารางใหม่ใด ๆ รวมข้อมูลตั้งค่าที่เก็บ เพื่อป้องกันข้อมูลสูญหาย; หากเริ่มตั้งค่าแล้ว ต้องวางแผน rollback อย่างชัดเจน
8. เก็บ SQL guard ใน partial migration ที่คงพฤติกรรมเดิม ไม่อ้าง runtime helper ที่เปลี่ยนได้; การปรับในอนาคตต้องออก migration ใหม่
9. ปรับ test เดิมของ 6A ให้ตรึงรุ่นเริ่มต้นของ 6A โดยเฉพาะ แทนสมมติว่าทุกรุ่นในโปรเจกต์ยังเป็น 6A; มีไฟล์ทดสอบเดิมเพิ่มในขอบเขตแก้ไขหนึ่งไฟล์และต้องตรวจ regression ใหม่ ไม่ลดข้อรับประกันการรักษาสิทธิ์หรือประวัติ

## ขอบเขตที่ผู้ตรวจเว้นและข้อสรุปของผู้ดำเนินงาน

- Event foreign key: ยืนยันเลื่อนไป 6C ตามแผน ต้องเติมก่อนเปิดลงเวลาจริง มิฉะนั้นอาจมีรหัสอ้างอิงไม่พบเหตุการณ์
- Filesystem, ความทนทานของไฟล์, checksum จาก bytes จริง, stamping, NAS, API, การตรวจสิทธิ์ก่อนส่ง bytes, worker และ UI: เป็น Tasks 2–8 ที่ยังไม่ได้รับคำสั่งให้ทำ การผ่าน schema ไม่พิสูจน์ความพร้อมของส่วนเหล่านี้
- Retry/fencing ของ worker และการเพิ่ม version ของ job/item: มี fields, constraints และ concurrency tokens ใน schema แต่การเปลี่ยนสถานะเป็นหน้าที่ application ใน Task 6 ต้องตรวจ stale lease และ race ก่อนเปิด worker
- การทำงานพร้อมกันและ contention: ผู้ตรวจอ่านกลไก serialize แต่ไม่ได้รัน race/load test; คงเป็น gate ของ Tasks 4/6/8 ไม่อ้างว่า Task 1 ผ่าน benchmark
- ผล Test/Build/Lint: ผู้ตรวจไม่ได้รันคำสั่งเอง ผู้ดำเนินงานจึงตรวจผลจริงและรัน regression หลังแก้ ไม่ใช้ความเห็นจาก review แทนผล verification

ไม่มีข้อเสนอ Minor ที่เลื่อนออกจาก Task นี้

ยังไม่มีการประทับภาพ กล้อง GPS การลงเวลา API/UI ที่เก็บ worker ย้ายไฟล์ หรือการทดสอบ NAS จริงใน Task 1 จึงไม่อ้างว่า Module 6B พร้อมใช้งานทั้งหมด

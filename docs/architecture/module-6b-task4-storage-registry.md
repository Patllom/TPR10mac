# Module 6B — Task 4: ทะเบียนที่เก็บและปลายทางหลักฐาน

สถานะ: Task 4 ผ่าน TDD, การแก้ข้อพบจาก Code Review และ Test/Build/Lint ครบ เมื่อ 25 กันยายน 2569 ยังไม่ push/merge และยังไม่เริ่ม Task 5

## ขอบเขต

เพิ่มทะเบียนที่เก็บจาก alias ใน protected deployment configuration, API 7 รายการ, การสลับ write target แบบตรวจรุ่น, internal reservation pin และการตรวจสุขภาพทุก 60 วินาที ไม่มีหน้า Portal ใหม่ใน Task นี้

ไม่เริ่ม Task 5–8 ไม่ mount NAS จริง ไม่แตะ Preview หรือ checkout หลัก ไม่ push/merge

## การกำหนดที่เก็บ

ตั้งค่า `AttendanceStorage:Locations` เป็นรายการที่มี `Alias`, `Kind` (`local-folder` หรือ `nas-mounted-folder`), `RootPath`, `ExpectedVolumeId`, `MarkerId` ตามข้อกำหนด adapter ในรายงาน Task 3 ตั้งค่าได้เฉพาะผู้ดูแล deployment ห้ามส่ง root/credentials ผ่าน HTTP

`AttendanceStorage:HealthWorkerEnabled` ค่าเริ่มต้น `true`; ปิดได้สำหรับ test host ที่เรียกตรวจด้วยตนเอง การปิดไม่ทำให้ readiness เดิมใช้ได้ตลอดไป

API คืนเฉพาะ alias/kind/ID/version/สถานะ ไม่มี root หรือข้อมูล NAS ส่วนตัว fingerprint ผูกกับการตั้งค่าทั้งชุด หากเปลี่ยน root ภายใต้ alias เดิม จะปฏิเสธการใช้งานเดิม ต้องเพิ่ม alias/registration ใหม่เพื่อไม่ให้รูปเก่าถูกชี้ผิดที่ ห้ามแก้ฐานข้อมูลเพื่อข้าม guard

## สิทธิ์และ transaction

ทุก storage API ต้อง `attendance:storage-manage` และ recent MFA; ผู้ดูแลระบบไม่ได้รับสิทธิ์อัตโนมัติ สิทธิ์นี้ไม่ให้ดูรูปหรือ GPS ใช้ระบบ session/permission/audit เดิม ไม่มีระบบสิทธิ์ใหม่

ตรวจสิทธิ์ก่อน bind/validate request และตรวจซ้ำหลัง filesystem I/O การ probe ไม่ถือ shared identity lock ขณะอ่านเขียนไฟล์ หลัง I/O ต้องกลับมาตรวจ actor/version/config และบันทึกผลพร้อม audit ใน transaction เดียว Audit ล้มเหลวต้องไม่เปลี่ยน target/reservation/version

## API ใน Task 4

ทุก path เริ่มด้วย `/api/v1/attendance/storage`

| วิธี | Path | ผลสำเร็จ |
|---|---|---|
| GET | `/options` | Page ของ alias ที่ตั้งค่าไว้ |
| GET | `/locations` | Page ของทะเบียนที่เก็บ |
| POST | `/locations` | 201 พร้อม StorageView; ไม่เลือก target อัตโนมัติ |
| POST | `/locations/{id}/probe` | StorageHealthView |
| GET | `/write-target` | StorageTargetView |
| POST | `/write-target` | StorageTargetView รุ่นใหม่ |
| GET | `/health` | Page ของ StorageHealthView |

Pagination ใช้ offset ตั้งแต่0 และ limit1–100 ค่าเริ่มต้น25 ค่าผิดตอบ400 ไม่ clamp

Mutation ต้อง reason และ expectedVersion ตาม DTO ยกเว้น registration ซึ่งยังไม่มีรุ่น Body ไม่รับฟิลด์อื่น สถานะ401/403/404/409/503 ไม่ให้ข้อมูล path; response ใช้ no-store

## การตรึงและเปลี่ยนปลายทาง

การ probe เพิ่ม `StorageLocation.Version` ตาม database guard จึงต้องโหลดรุ่นใหม่ การสลับใช้ `StorageWriteTarget.Version` ไม่ใช่รุ่น location คำขอพร้อมกันจาก target รุ่นเดียวกันสำเร็จเพียงหนึ่งรายการ

Readiness เก็บใน process ผูก storage ID/version/config และหมดอายุ60วินาที หลัง restart ต้อง probe ใหม่ ไม่ใช้ CheckedAt ในฐานข้อมูลแทนหลักฐานว่า mount ปัจจุบันยังพร้อม ก่อน switch มี native probe อีกครั้ง ไม่มี fallback ไปปลายทางอื่น

`PinAsync(operationId, subject, stamp)` เป็น internal service ไม่มี HTTP endpoint พนักงานใช้ได้เฉพาะตัวเองและ employment snapshot ที่ถูกต้อง ไม่ต้องได้ storage-manage โดย 6C ต้องตรวจ camera challenge และ exact Site ก่อนเรียก

Pin บันทึก owner/snapshot/เวลา/action/storage ID/version/object key ก่อน I/O การ retry operation เดิมต้อง metadata เดิมและคืน reservation เดิมแม้ target เปลี่ยนแล้ว การลงไฟล์จริงใน Task 5 ต้องตรวจ mount อีกครั้ง ไม่ถือ receipt เป็นสิทธิ์ข้าม adapter

Timestamp ต้องเป็นเวลาจาก server ที่ความละเอียด microsecond ของ PostgreSQL และ snapshot ตรงกับ stamp ห้ามส่งเวลาจาก client หรือเปลี่ยน stamp ระหว่าง retry

Pin ใหม่ปฏิเสธ storage ที่ `AcceptWrites=false`; retry operation เดิมคืน pin เดิมเพื่อ idempotency/late arrival ไม่ใช่สิทธิ์เขียนโดยข้าม lifecycle การซีล source พร้อมสร้าง migration job/audit และเงื่อนไข source ไม่ใช่ active target จะทำแบบ atomic ใน Task 6 ไม่เปิด endpoint ซีลแยกใน Task 4 ส่วนการ prepare/reuse pin ที่เป็น Orphan/Published ต้องตรวจ state ใน consumer Task 5

## การตรวจสุขภาพ

Worker แยกสองวงจร: native readiness probe ทุก30วินาทีเพื่อมีระยะเผื่อก่อน TTL60 หมด กับ integrity scan ทุก60วินาที ไม่รอ scan ที่ช้าก่อน refresh readiness ตรวจ checksum/length จาก manifest ไม่เกิน100 copies ต่อ storage และมีเวลางานอ่านรวม20วินาทีต่อ tick พร้อม keyset cursor/หมุนไป storage ถัดไป ไม่อ่าน bytes หรือ path ออก HTTP

ผล integrity แยกจาก receipt จึงไม่ถูกล้างเมื่อกด probe ระหว่างรอบใหม่ที่ยังไม่ครบจะคงอย่างน้อยจำนวนปัญหาจากรอบที่ตรวจครบล่าสุด จนตรวจจบรอบใหม่จึงลดจำนวนได้ จำนวน orphan อัปเดตจาก DB โดยไม่เปลี่ยนความพร้อม unknown ให้กลายเป็น ready

`missingObjects` หมายถึงจำนวน manifest copy ที่ตรวจยืนยันไม่ได้ในรอบสแกน รวมไฟล์หายหรือ checksum ไม่ตรง ไม่ใช่ข้อยืนยันว่าถูกลบ `orphanObjects` นับ EvidenceObject ที่มีสถานะ Orphan ในฐานข้อมูล ไม่สำรวจไฟล์นอก manifest และไม่เปลี่ยนสถานะ/ลบหลักฐานใด ๆ

`errorCode` เป็นรหัสปลอดภัย เช่น storage-unavailable, storage-config-changed, capacity-unknown, manifest-copy-unverified, orphan-evidence, manifest-scan-in-progress ไม่มี exception/path ส่วนตัว ข้อมูล scan/cursor อยู่ใน process; หลัง restart แสดง unknown จนตรวจใหม่ ไม่มี email

พื้นที่ว่างต่ำกว่า10GiBหรือ10%เป็น warning ตาม adapter Task 3 ข้อมูล capacity ไม่ทราบต้องคง unknown แม้มี orphan warning ห้ามแปลงเป็นพร้อมเขียน

## หลักฐานตรวจสอบ

| รายการ | ผลล่าสุด |
|---|---|
| TDD ของ registry/API | 13 RED → 13 GREEN |
| TDD ของ reservation pin | 8 RED → 8 GREEN; เพิ่ม sealed/no-target/idempotency assertions |
| TDD ของ worker/OpenAPI | worker2 RED และ OpenAPI2 RED → GREEN |
| Regression จาก Review | 3 RED สำหรับ readiness/integrity และ2 RED สำหรับ errorCode/orphan → GREEN |
| Focused รวม Task4 + schema + native adapter | 128 ผ่าน ไม่มี failed/skipped |
| Backend Build | ผ่าน ไม่มี warning/error |
| Backend format verifier | ผ่าน หลังจัด whitespace |
| Frontend tests | 40 ผ่าน |
| Frontend Build / ESLint | ผ่านทั้งคู่ |
| Backend ทั้งชุดหลัง Review | 1,174/1,174 ผ่าน ไม่มี failed/skipped, exit0, เวลา25.1795นาที |

บันทึกผลอยู่ใน workspace ledger ของแผน และตรึง SHA-256 ของไฟล์ backend ที่เปลี่ยน19ไฟล์ก่อนรันชุดเต็ม ตรวจซ้ำแล้วตรงครบ19ไฟล์ ณ `2026-09-25T10:38:23Z` จึงยืนยันโค้ดที่ส่งมอบตรงกับผลทดสอบ ไม่ถือว่าผลนี้รับรอง NAS จริงหรือ Module 6B พร้อม production

คำสั่งตรวจหลัก: `dotnet test backend/TPR10.sln --no-restore`, `dotnet build backend/TPR10.sln --no-restore`, `dotnet format backend/TPR10.sln --verify-no-changes --no-restore`, `npm test`, `npm run lint`, `npm run build` ผ่านทั้งหมดหลังแก้ Review

## ขอบเขตการรับรอง deployment

Task นี้ตรวจใน API process เดียว ยังไม่รับรองหลาย replicas: receipt อยู่ใน memory แต่ location version อยู่ใน DB ต้องออกแบบการประสาน worker/readiness ก่อน scale out การวัด throughput, จำนวน storage มาก, backup/restore, NAS จริง, disconnect/remount/permissions และ durability ข้าม crash ยังเป็น production gate ใน Task 8

Code Review อิสระพบ2 Important และ1 Minor: แยก readiness ออกจาก scan, รักษาคำเตือน integrity, และเติม safe errorCode/orphan count ให้ครบ แก้ทั้งหมดพร้อม regression tests ที่เห็น RED ก่อนแก้ และรัน backend ชุดเต็มหลังแก้ผ่านแล้ว ไม่มีข้อเสนอค้างจาก review นี้

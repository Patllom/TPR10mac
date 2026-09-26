# Module 6B — Task 5: เตรียม เผยแพร่ และอ่านหลักฐานภาพ

วันที่ 25 กันยายน 2026

สถานะ: Task 5 ผ่าน TDD, Code Review และ Test/Build/Lint แล้ว ไม่ใช่การรับรองว่า Module 6B ทั้งหมดหรือระบบ Production พร้อมใช้งาน

## ขอบเขต

ทำเฉพาะ Task 5 ต่อจาก `a85c664` บนสาขา `codex/module-6b-evidence-storage` ไม่เปลี่ยน checkout หลักหรือ Preview และไม่ mount NAS จริง

เพิ่มบริการภายในสำหรับเตรียมรูปและผูกกับรายการธุรกิจ พร้อม API อ่านรูปสามเส้นทาง ไม่มี API อัปโหลดหรือเผยแพร่รูปสำหรับ client การลงเวลาด้วยกล้อง/GPS, challenge, exact Site, คู่เข้า–ออก และ FK ของ event เป็นงาน Module 6C

## การเตรียมและเผยแพร่

1. ใช้ reservation ที่ Task 4 ตรึงเจ้าของ ต้นสังกัด เวลา operation และที่เก็บไว้แล้ว ตรวจค่าทั้งหมดให้ตรงกัน
2. อ่านภาพภายในขนาดกำหนดและตรึง SHA-256 ของอินพุตลงฐานข้อมูลก่อนทำไฟล์ ใช้ lease 60 วินาทีและ fencing version ป้องกันผู้ทำงานเก่าบันทึกผลหลังเสียสิทธิ์
3. ประทับภาพเต็มและสร้าง thumbnail จากภาพประทับแล้ว ใช้ adapter เดิมเขียน immutable ทั้งสองไฟล์ โดยไม่ถือ identity lock ขณะประมวลผลหรือเข้าถึงไฟล์
4. ตรวจ session เจ้าของ lease และ fence อีกครั้งใน transaction สั้น ก่อนบันทึก checksum/ขนาดและเปลี่ยนเป็น Prepared
5. `StagePublicationAsync` ต้องมี transaction ของผู้เรียก ตรวจ operation, event ID, เจ้าของ, snapshot และ stamp จากนั้นเพิ่ม binding เปลี่ยนสำเนาเป็น Active และเพิ่ม audit โดยไม่ SaveChanges/Commit แทนผู้เรียก
6. Module 6C ต้องตรวจ challenge, assignment และคู่ลงเวลาอีกครั้ง และบันทึก event พร้อมหลักฐานและ audit ใน transaction เดียวกัน

ส่ง operation เดิมกับอินพุตเดิมคืนผลเดิม ภาพคนละไฟล์ตอบ conflict แม้ decode เป็น pixels เดียวกัน ไม่เขียนทับไฟล์เดิม หากเขียนได้เฉพาะภาพเต็มแล้ว thumbnail ล้มเหลว รูปยังไม่เผยแพร่ การลองใหม่หลัง lease หมดใช้ไฟล์ immutable เดิมที่ checksum ตรงได้

เพิ่ม migration `PinEvidenceInputDigest` โดยไม่แก้ migration เดิม: digest เปลี่ยนไม่ได้เมื่อถูกตรึง และ downgrade ปฏิเสธหากทำให้ digest ที่มีอยู่สูญหาย ไม่เก็บภาพต้นฉบับเพิ่มในฐานข้อมูล

## การอ่านและสิทธิ์

| เส้นทาง | ผลลัพธ์เมื่ออนุญาต |
| --- | --- |
| `GET /api/v1/attendance/evidence/{id}` | JPEG ภาพเต็มแบบแสดงในหน้า |
| `GET /api/v1/attendance/evidence/{id}/thumbnail` | JPEG ภาพย่อ |
| `GET /api/v1/attendance/evidence/{id}/download` | JPEG ดาวน์โหลด ชื่อเป็นรหัสหลักฐาน |

ทั้งสามเส้นทางและ HEAD ใช้สิทธิ์เดียวกัน: เจ้าของดูของตนเองได้ รวมผู้เป็นหัวหน้า; หัวหน้าดูรูปลูกทีมไม่ได้; HR ต้อง capability, recent MFA และ current assignment ตรงหน่วยงานใน snapshot; Admin และ storage-manager ไม่ได้สิทธิ์อ่านรูปอัตโนมัติ

Reserved/Prepared/Orphan และข้อมูลนอกสิทธิ์ตอบ 404 การไม่มี session/ยืนยันตัวตนไม่ครบใช้ 401/403 ตาม policy เดิม ไม่เปิดพาธหรือ storage locator ให้ browser

ตรวจสิทธิ์รอบแรกใน transaction สั้น จากนั้นอ่าน bytes นอก identity lock ตรวจ checksum จาก Active copy ถ้าเสียจึงลองเฉพาะ Fallback ที่ metadata อนุญาตและ checksum ตรง ไม่ค้นไฟล์นอกทะเบียน ไม่อ่าน Quarantined copy

ก่อนส่ง ตรวจ session, `CanReadPhoto`, สถานะ Published และ metadata สำเนาอีกครั้ง บันทึก audit พร้อมฐานสิทธิ์ `Own` หรือ `Hr` และ workspace จาก snapshot รวมถึง audit การปฏิเสธในรอบแรก

## การถอนสิทธิ์ระหว่างส่งรูป

ใช้ dedicated connection ที่ปิด pooling และ connection-scoped shared advisory lock `7241002` ระหว่าง final authorization, audit commit และส่ง buffer เท่านั้น ไม่มี filesystem I/O ภายใต้ lock นี้

- ถอนสิทธิ์ commit ก่อน reader ได้ shared lock: ไม่ส่งรูป
- Reader ได้ shared lock ก่อน: ส่งในช่วงจำกัดก่อนธุรกรรมถอนสิทธิ์ commit ภาพที่ส่งหรือดาวน์โหลดไปแล้วเรียกคืนไม่ได้
- จำกัดภาพไม่เกิน 10 MiB และช่วง final delivery 5 วินาที รวมการตอบ error หาก client ค้าง
- เมื่อยกเลิก เกินเวลา หรือ network ล้มเหลวหลังเริ่มส่ง จะ abort ก่อนปล่อย lock ไม่ส่ง JSON ต่อท้ายรูป
- Audit ล้มเหลวก่อนเริ่มส่ง: 503 ไม่มีภาพ; ใช้ finally ปล่อย lock และ dispose physical connection ไม่คืน connection ที่ถือ lock เข้าพูล

ส่ง `Cache-Control: no-store` และ `X-Content-Type-Options: nosniff` ไม่ใช้ ETag/Last-Modified/304; Range ตอบ 416 หลังตรวจสิทธิ์ HEAD ตรวจสิทธิ์ก่อนคืน metadata และไม่มี body

## การรายงานรายการค้าง

`EvidenceReconciler.ReportAsync` รายงานจำนวน reservation ที่มีอายุอย่างน้อย 24 ชั่วโมง lease หมดแล้วหรือไม่มี lease และไม่มี binding ไม่เปลี่ยนสถานะ ไม่ส่ง locator และไม่ลบไฟล์ใด ๆ ยังไม่มีการตั้ง scheduler สำหรับบริการนี้ใน Task 5

## TDD และ Code Review

- เริ่มจาก Writer ที่ยังไม่มี: RED 1 กรณี ก่อนผ่านกรณีเตรียมซ้ำ/อินพุตต่าง/rollback/publish
- API รูป: RED ก่อนเพิ่ม routes และ reader; พบ HEAD คืน problem body จึงแก้ให้ HEAD คืนเฉพาะ status เมื่อถูกปฏิเสธ
- Guard digest และ reconciler: RED 2 กรณีก่อนเพิ่มการป้องกันและรายงาน
- เพิ่มการทดสอบสิทธิ์ทั้งสามรูปแบบ, ข้ามหน่วยงาน, HR หมด MFA, checksum ของ fallback, concurrent prepare, stale fence, partial filesystem failure และ audit rollback
- ทดสอบ barrier ก่อนโหลดไฟล์ หลังโหลดไฟล์ และก่อน shared lock สำหรับ revoke/disable/HR สิ้นสุดสิทธิ์ พร้อมกรณี slow client, cancellation และ network failure
- Reviewer แยกหนึ่งรายตรวจแบบ read-only พบ Important 2 ข้อ: error 503 อาจถือ lock เกินเวลา และ audit ขาดฐานสิทธิ์; เพิ่ม regression ให้ RED แล้วแก้เป็น GREEN ทั้งสองข้อ รวมข้อเสนอ audit การปฏิเสธ รวม 4 กรณี
- ไม่มี Critical หรือ Minor ที่ reviewer รายงาน ไม่มีการใช้ผล review แทนผลทดสอบจริง
- Regression พบ fixture downgrade ของ Task 1 จำนวน 2 กรณีที่สมมติว่า migration นั้นเป็นรุ่นล่าสุด จึงตรึง fixture ไปยังรุ่น Task 1 ก่อนเริ่มทดสอบ; ไม่ลดการตรวจรักษาข้อมูล/ประวัติเดิม ส่วน guard digest ใหม่มีทดสอบแยก ตรวจซ้ำรวม 3 กรณีผ่าน

## ผลตรวจขั้นสุดท้าย

| การตรวจหลังแก้ review | ผล |
| --- | --- |
| Backend ทั้งชุด `dotnet test backend/TPR10.sln --no-build --no-restore` | ผ่าน 1,230/1,230 ไม่มี failure/skip; 29.4484 นาที; exit 0 |
| Focused: Evidence, Storage, AttendanceAccess และ IdentityOpenApi | ผ่าน 321/321; exit 0 |
| Backend `dotnet build backend/TPR10.sln --no-restore` | ผ่าน ไม่มี warning/error |
| Backend `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน; exit 0 |
| Frontend `npm test` | ผ่าน 40/40 |
| Frontend `npm run lint` | ผ่าน; exit 0 |
| Frontend `npm run build` | ผ่าน; exit 0 |
| Source SHA-256 หลัง full suite | ตรงครบ 274 ไฟล์ |
| `git diff --check` | ผ่าน |

หลักฐานใน `.superpowers/sdd/2026-09-25-module-6b-evidence-storage/`: `task5-backend-delivery.log`, `task5-focused-delivery.log`, `task5-build-final.log`, `task5-format-final.log`, `task5-node-final.log`, `task5-lint-final.log`, `task5-frontend-build-final.log`, `task5-delivery-source.sha256` และ regression RED/GREEN ของ review

## ข้อจำกัดและรายการที่ไม่ขยายงาน

Tasks 1–4 ตรวจเฉพาะจุดเชื่อมต่อ ไม่ทำซ้ำ; Tasks 6–8 ยังไม่ได้ทำ ความสำเร็จของ Task 5 ไม่เท่ากับจบ Module 6B

การ mount NAS, durability ของ NAS จริง, หลาย replica, throughput/capacity และ backup/restore drill ยังต้องผ่าน gate ของ Task 8/Production ไม่มีการใช้โฟลเดอร์ทดสอบแทนหลักฐาน NAS จริง

ไม่มี production writer ก่อน Task 5 ที่ต้องย้าย Prepared rows เก่า; ไม่เพิ่ม backfill สมมติ ไม่เพิ่ม cleanup/scheduler อัตโนมัติ การเลือก fallback จำกัด 16 สำเนาและตรวจ metadata ซ้ำแบบปฏิเสธเมื่อไม่แน่ใจ ไม่รับประกัน availability เมื่อสำเนาถูกเปลี่ยนระหว่างอ่าน

การสร้างบริการ authorization/audit รอบสุดท้ายใช้ DbContext บน connection เดียวกันโดยตั้งใจ เพื่อไม่ให้เกิดการขอ exclusive lock จากอีก connection ขณะถือ shared lock

เอกสารไม่มี credentials รูปจริง พิกัดจริง หรือข้อมูล mount ส่วนตัว ยังไม่ได้ push หรือ merge

# Module 7C — Task 12: API และ client ตามขอบเขตสิทธิ์

สถานะ: Task 12 ผ่านแล้ว — แยกผู้ลงมือและผู้ตรวจ ตรวจแก้ครบ Test, Build, Format และ Lint ผ่านทั้งหมด ปิดผลวันที่ 28 ตุลาคม 2026 (เวลาไทย)

ฐาน `650a5a8ac980fd6bf4fcfbadd4424302c7ca8a66` บน `codex/module-7b-design` โค้ด commit `1bff164ceed6ab308c0b9ff0406881103ca22b4c` ยังไม่ push/merge

## ขอบเขต

เพิ่ม API ใต้ `/api/v1/field-advances` ตาม prefix เดิม สำหรับยอดจ่าย คิว งวดและฉบับงวด ข้อเสนอ การยืนยัน/ทำให้การยุติมีผล การสะสาง การปรับวงเงิน ส่วนเพิ่ม และหลักฐาน พร้อม client ที่ตรวจชนิดข้อมูลและคำสั่งขณะทำงาน ไม่มี UI Task 13 หรือ schema ใหม่

- ใช้บริการธุรกิจ Tasks 1–11 เดิม ข้อมูลตอบกลับเป็น DTO ที่กำหนดรายการฟิลด์ ไม่ส่ง EF entity หรือที่อยู่ไฟล์
- จำนวนเงินและ GUID เป็น string รุ่นข้อมูลตรวจ safe integer คำสั่งตรวจฟิลด์ เหตุผล สิทธิ์ปัจจุบัน session/MFA/CSRF
- ผลคำสั่งเดิมผูกผู้สั่งและสิทธิ์ปัจจุบัน ไม่ส่ง receipt JSON เก่ากลับโดยตรง
- การอ่านและดาวน์โหลดตรวจสิทธิ์ก่อนส่งข้อมูลจริง ไฟล์เป็น attachment พร้อม nosniff และ private/no-store
- client รองรับ abort และตรวจตัวตนก่อน/หลังตอบกลับ ไม่สร้างรหัสคำสั่งหรือส่งซ้ำอัตโนมัติ
- GET installments คืน catalog รวม installments/proposals/evidence เพื่อค้นหารายการที่มีสิทธิ์ ผู้ยืนยันที่มีเฉพาะสิทธิ์ตัดสิน disposition เห็นบริบทตัดสินตามขอบเขต ไม่ได้สิทธิ์อ่านต้นทางหรือไฟล์เพิ่ม
- แก้ Program.cs เฉพาะ callback header ของ endpoint ที่มี PaymentApiMetadata เพื่อคง private/no-store ส่วน route อื่นใช้ no-store เดิม

## หลักฐานผู้ลงมือ

Einstein ใช้ TDD ทั้ง HTTP และ client ก่อนเพิ่มโค้ด พร้อมเพิ่ม RED จากจุดที่ตรวจพบเรื่อง discovery, basis, การ refresh งวด, สิทธิ์ผู้ยืนยัน, binary schema และสิทธิ์หมดอายุระหว่าง serialize

- Baseline HTTP 12 และ frontend ที่เกี่ยวข้อง 71 ผ่าน
- ก่อน review: HTTP 12 ผ่าน และ frontend ที่เกี่ยวข้อง 83 ผ่าน (หลังแก้ review เป็น HTTP 17 ตามผลด้านล่าง)
- Regression 190 ผ่านระหว่างพัฒนา; หลังปรับการส่งข้อมูลรอบสุดท้ายรัน regression 62 ผ่าน จึงไม่อ้างว่า 190 กรณีรันบน snapshot สุดท้าย
- Build และ scoped Format/Lint ผ่าน; ผลชุดเต็มหลัง review อยู่ด้านล่าง
- ข้อผิดพลาด fixture เรื่อง grant, canonical temp path, CSRF limiter และสถานะ MFA แยกจาก RED ของพฤติกรรมจริง ไม่ลดตัวกรองความปลอดภัยเพื่อให้ผ่าน

OpenAPI ใหม่ทดสอบสร้างเอกสารจริง HTTP 200 โดยเลือกเฉพาะ route PaymentApiMetadata และตรวจ schema ของคำสั่งกับ error responses ยังไม่อ้างว่าแก้ OpenAPI 500 ของเอกสารทั้งระบบแล้ว

## ผู้ตรวจอิสระและชุดตรวจสุดท้าย

Einstein ลงมือ และ Gauss ตรวจอิสระแยกกัน ตรวจซ้ำหลังแก้ครบสองรอบ ไม่พบ Critical/Important คงค้างในขอบเขต Task นี้

1. รอบแรกพบช่องโหว่การส่ง catalog แบบเต็มเมื่อสิทธิ์อ่านถูกถอน/หมดอายุ แต่ยังมีสิทธิ์ยืนยัน disposition แก้ให้การตรวจสิทธิ์ก่อนส่งผูกกับชนิดข้อมูลที่เตรียมไว้ มี RED 2 กรณีก่อนแก้และ GREEN หลังแก้
2. เพิ่มเทสต์ปฏิเสธ proposal/receipt/evidence ที่มีอยู่จริงข้ามคำขอ และผู้ยืนยันที่ไม่มีสิทธิ์อ่านหลักฐาน พร้อมเทสต์ ProtectedResult จริงด้วย stream ควบคุมการค้าง ตรวจ abort และการคืน advisory lock ทั้ง timeout/ผู้ใช้ยกเลิก
3. HTTP หลังแก้ผ่าน 17 กรณี และ regression สิทธิ์/คำขอผ่าน 62 กรณี
4. ชุดเต็มรอบแรกพบเทสต์รายการ API เดิมยังไม่รวม 25 operations ใหม่ จึงหยุด backend รอบนั้นด้วย exit 130 ไม่ถือว่าผ่าน แก้เฉพาะรายการ literal ใน FieldAdvanceOpenApiTests คง strict equality เดิม มี RED 1 และ GREEN 81 กรณี ผู้ตรวจตรวจซ้ำผ่าน
5. รอบแก้ที่สองไม่มี product/client เปลี่ยน เพิ่มไฟล์เทสต์ที่แก้เป็นขอบเขตจำเป็นของ Task 12 รวมไฟล์โค้ด/เทสต์ 16 ไฟล์

Frontend ทั้งชุดผ่าน 167 กรณี, Build และ Lint ผ่าน Backend ทั้งชุดผ่าน 3,023 กรณี พร้อม Build และ Format รอบสุดท้ายผ่าน ทุกคำสั่ง exit 0 และ checksum ก่อน–หลังไม่เปลี่ยน Backend Test ใช้เวลา 86 นาที 26 วินาที Frontend รันก่อนแก้รายการคาดหวัง OpenAPI ซึ่งแก้เฉพาะเทสต์ backend จึงไม่รัน frontend ซ้ำโดยไม่มีเหตุ

เทสต์ stream ควบคุมยืนยันพฤติกรรม guard จริง แต่ไม่อ้างว่าทดสอบเครือข่ายจริง การส่งข้อมูลไปบางส่วน หรือ transport ที่ไม่ทำตาม cancellation แล้ว EF10102 ยังปรากฏใน log ที่เทสต์ผ่านและคงไว้ในรายการ Minor log

## ผลชุดตรวจสุดท้าย

| คำสั่ง | ผล | ตรวจเสร็จ UTC |
|---|---|---|
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | exit 0; source ไม่เปลี่ยน | 2026-10-27T16:50:53.784702+00:00 |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | exit 0; source ไม่เปลี่ยน | 2026-10-27T16:51:11.849309+00:00 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger console;verbosity=normal` | exit 0; source ไม่เปลี่ยน | 2026-10-27T18:17:37.529224+00:00 |
| `npm test` | exit 0; source ไม่เปลี่ยน | 2026-10-27T16:44:31.490395+00:00 |
| `npm run build` | exit 0; source ไม่เปลี่ยน | 2026-10-27T16:44:45.706718+00:00 |
| `npm run lint` | exit 0; source ไม่เปลี่ยน | 2026-10-27T16:44:48.827241+00:00 |
| `git diff --check` | exit 0; source ไม่เปลี่ยน | 2026-10-27T16:44:48.895541+00:00 |

ตรวจ checksum โค้ด/เทสต์ 16 ไฟล์ตรงกับชุดที่ review และไฟล์ค้างเดิม 14 ไฟล์ไม่เปลี่ยน ตรวจ `git diff --check` และ staged diff ซ้ำก่อน commit ผ่าน ไม่มีไฟล์นอกขอบเขตเข้า code commit

หลักฐาน log ในเครื่อง (SHA256):

- `/tmp/tpr10-task12-G-build.log`: `8a367c64020111456ca0bf193f288f5d354d953964f068ce904ae0ea085fba11`
- `/tmp/tpr10-task12-G-format.log`: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`
- `/tmp/tpr10-task12-G-test.log`: `1526b6ee6388337a7679ae10ceb9133ed037d544133b6efad5efc9fa51a30c7f`
- `/tmp/tpr10-task12-G-npm-test.log`: `ab1c6cceb8addae7264eec2aa65a5fc0017723f527c7929819fdfc9f286d29f6`
- `/tmp/tpr10-task12-G-npm-build.log`: `93f220ceb995bc7c221f2104f188b375dda339f36ce63c4605f26fe49489b62b`
- `/tmp/tpr10-task12-G-npm-lint.log`: `6a758d01b9a69c81fac6755e8dc5cbcbd9d91a817724cead5abd8c44d15c4f82`
- `/tmp/tpr10-task12-G-diff-check.log`: `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`

## ข้อจำกัดและรายการติดตาม

HTTP ครอบคลุมการขนส่งข้อมูลและสิทธิ์ รวม happy path ของงวดและผล start หาย ส่วนทุกชุดสถานะทางธุรกิจอาศัย service regression เดิมและชุดเต็ม ไม่อ้างว่าเทสต์ HTTP จำลองทุก transition combination

คง M2-R1/R2-C1, HTTPS 504, Minor log และ OpenAPI 500 พักมือถือจริง/NAS จริง ไม่มี Task 13, push, merge, deploy หรือ migration บน production ไฟล์ค้างเดิม 14 ไฟล์ไม่รวมในงานนี้

# Module 7C — Task 13: หน้าจอฝ่ายการเงิน

สถานะ: Task 13 เสร็จ ผ่านผู้ตรวจอิสระและ Test/Build/Format/Lint ครบ ตรวจเสร็จวันที่ 31 ตุลาคม 2026 (Asia/Bangkok)

ฐาน `e3aa9f30fed4f9bcf9d9863a7b9d71c7eb200bae` บน `codex/module-7b-design` ต่อจาก Task 12 ไม่รวม Task 14

## สิ่งที่พัฒนา

เพิ่มหน้าคิวงานจ่าย รายละเอียดงวด เตรียม/ตรวจ/เริ่มจ่าย/บันทึกผล และการสะสาง ยกเลิก แก้ข้อมูล หรือรับช่วงตามสิทธิ์ แสดงยอดอนุมัติ ยอดกัน ยอดจ่าย และยอดพร้อมจัดงวดแยกกัน หากผลตอบกลับหรือข้อมูลหลังเริ่มจ่ายหาย แสดง “กำลังตรวจสอบผล ห้ามจ่ายซ้ำ” และตรวจรหัสคำสั่งเดิม ผู้ตรวจเห็นวันจ่าย เลขอ้างอิง และหลักฐานก่อน–หลังที่จะอนุมัติ

เพิ่ม API อ่านอย่างเดียว payment-scopes/payment-context ให้ผู้มีเฉพาะสิทธิ์การเงินค้นหาโครงการและเห็นคำสั่งที่ทำได้ตามสิทธิ์ปัจจุบัน ตรวจข้อมูลและสิทธิ์ซ้ำก่อนส่งข้อมูล การมีสิทธิ์อ่านรายงานไม่ทำให้ได้สิทธิ์ทำรายการ ทุก mutation ยังตรวจใน transaction เดิม ไม่เพิ่ม grant หรือเปลี่ยนกติกาธุรกิจ

## ปัญหาที่พบระหว่างทดสอบ

- Configuration binder ไม่เติม dictionary ที่ใช้ GUID เป็น key ทำให้รายการโฟลเดอร์ว่าง แก้การอ่าน GUID อย่างชัดเจนและปฏิเสธค่าผิด/ซ้ำ คงข้อจำกัดไฟล์และโฟลเดอร์เดิม มีเทสต์ผ่าน DI จริง
- Native filesystem adapter ใช้ค่า Linux x64 บน ARM64 แก้ค่า O_DIRECTORY/O_NOFOLLOW ตามสถาปัตยกรรม ทดสอบอ่านเขียนและปฏิเสธ symlink/overwrite จริงใน Linux container อ้างอิง [Linux ARM64 UAPI](https://raw.githubusercontent.com/torvalds/linux/master/arch/arm64/include/uapi/asm/fcntl.h)
- ผู้ตรวจพบช่องผิดพลาดจาก request-client หลังเริ่มจ่ายและข้อมูลเลขอ้างอิงก่อน–หลังไม่ครบ แก้ทั้งสองจุดพร้อมเทสต์ RED→GREEN ผู้ตรวจคนเดิมตรวจซ้ำแล้วผ่าน

ปัญหา upload 503 ที่พบครั้งนี้ไม่ใช่หลักฐานปิด HTTPS 504 เดิม

## การตรวจรับ

ผู้ลงมือ Chandrasekhar แยกจากผู้ตรวจ Schrodinger ตรวจทั้งความตรงข้อกำหนดและคุณภาพโค้ด รอบแรกมี Important สองข้อ แก้รอบ 1 แล้วได้ PASS / Approved ไม่พบปัญหาใหม่ใน diff รอบแก้

- HTTPS E2E รอบ RED ล้มที่หน้าจอที่ยังไม่มีหลังเข้าสู่ระบบสำเร็จ ไม่ใช่ certificate/504
- HTTPS dev 4000 และ production 4001 ผ่านหลังแก้รอบสุดท้าย: เงินโอน/สด สอง session, double-click, response หาย, รายละเอียดคำขอโหลดไม่ได้หลังเริ่มจ่าย, หลักฐานหาย, family hold, stale version, revoked identity, keyboard/ข้อความผิดพลาดภาษาไทย และ viewport จำลอง
- ตรวจ metadata ที่ persist จริง รวมวันที่ เลขอ้างอิง และหลักฐานหลังผู้ตรวจยืนยัน
- Focused HTTP/OpenAPI/หลักฐาน 74 ผ่าน; Node 171 ผ่าน
- รอบสุดท้าย frontend Test/Build/Lint/diff check ผ่านทั้งหมด; backend Build/Format/Test ผ่านครบ 3,037 tests ไม่มีล้มเหลวหรือข้าม
- บันทึกผลสุดท้ายใน /tmp/tpr10-task13-final-G-*-summary.jsonl พร้อมคำสั่ง เวลา exit code และ hash ตรวจโค้ดไม่เปลี่ยน

การผ่าน E2E หนึ่ง test ที่มีหลายสถานการณ์ไม่หมายถึงทุก branch ผ่าน UI แล้ว ยังไม่ได้ทดสอบ UI outcome Unknown/Matched, edit/return/re-review ทุกแบบ, pagination หรือทุกลำดับการสลับบัญชีและ response เก่า ขอบเขตเหล่านี้เปิดเผยไว้ในการตรวจ ไม่อ้างแทนการทดสอบมือถือจริง/NAS จริง

## การเก็บงานและรายการติดตาม

งานค้างเดิม 13 ไฟล์ต้องคง hash เดิม Shared HTTPS fixture เพิ่มเฉพาะ allowlist ของ payment spec และจะ stage เฉพาะบรรทัดนี้ ไม่รวมงาน capture เดิม

คง M2-R1/R2-C1, HTTPS 504, Minor log และ OpenAPI 500 เพิ่ม T13-M1: บางสถานะ/เหตุระงับยังแสดงรหัสภาษาอังกฤษ ให้ตรวจในการ review รวมก่อน merge พักมือถือจริง/NAS จริงตามเดิม ไม่มี push/merge/deploy หรือ migration ฐานจริง

## ผลตรวจสุดท้ายและ commit

โค้ด commit `e973432` รวม 20 ไฟล์ โดย shared fixture รวมเฉพาะ payment-spec allowlist การทดสอบ backend ใช้เวลา 1.4661 ชั่วโมง (จบ 2026-10-30T17:36:32Z) ยืนยัน source hash ไม่เปลี่ยนระหว่างตรวจทั้งสองชุด

| คำสั่ง | ผล |
|---|---|
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | exit 0; source ไม่เปลี่ยน |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | exit 0; source ไม่เปลี่ยน |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger console;verbosity=normal` | exit 0; source ไม่เปลี่ยน |
| `npm test` | exit 0; source ไม่เปลี่ยน |
| `npm run build` | exit 0; source ไม่เปลี่ยน |
| `npm run lint` | exit 0; source ไม่เปลี่ยน |
| `git diff --check` | exit 0; source ไม่เปลี่ยน |

Backend 3,037/3,037 และ frontend 171/171 ผ่าน HTTPS รอบ fix1 ผ่านทั้ง4000/4001 ไม่มีการทดสอบมือถือจริงหรือ NAS จริง งานถัดไปตามแผนคือ Task 14 แต่ยังไม่ได้เริ่ม

# ตรวจ OpenAPI 500 — 7 พฤศจิกายน 2026

สถานะ: **ยังไม่ยืนยันสาเหตุและยังไม่ถือว่าแก้แล้ว**

ฐาน main d7507b4, branch codex/fix-openapi-500 ผู้ลงมือ Galileo ผู้ตรวจอิสระ Hypatia ผู้ควบคุมตรวจผลคำสั่งจริง

หลักฐานเดิมในรายงาน Task5 Module7C ระบุเกิด500หนึ่งครั้งระหว่างชุดรวม แต่ชุดเฉพาะและชุดรวมต่อมาผ่าน ไม่พบ log เหตุเดิมในตำแหน่งชั่วคราวที่อ้างไว้ จึงไม่มี exception/stack trace เพื่อผูกสาเหตุ

ตรวจ factory, worker lifetime, การลงทะเบียน OpenAPI และ transformers ไม่พบหลักฐานเพียงพอสำหรับแก้ production code การแยก worker ออกจาก metadata-only host มีอยู่ใน main แล้ว ไม่ใช่แพตช์ใหม่

เพิ่ม OpenApiConcurrencyTests: ขอเอกสาร16คำขอพร้อมกัน3รอบใน host เดียว รวม48คำขอ ทุกคำขอต้อง200 JSONถูกต้อง มีเส้นทางตัวอย่างและ schemas ไม่ว่าง ไม่มี retry หรือกลืนข้อผิดพลาด เป็น coverage เพื่อการวินิจฉัย ไม่ใช่ regression ที่เคย RED และไม่ตรวจครบทุก schema การทำงานพร้อมกันขึ้นกับ scheduler ไม่ใช่การรับรอง load production

## ผลตรวจ

- เทสต์ concurrent1/1 ผ่าน `/private/tmp/openapi-concurrency-real.log`
- `dotnet test backend/TPR10.sln -m:1 --no-restore --filter 'FullyQualifiedName~OpenApi'`:142/142 ผ่าน exit0 รวม host lifetime `/private/tmp/openapi-focused.log`
- Backend Build ผ่าน 0 warnings/errors `/private/tmp/openapi-backend-build.log`
- Format เฉพาะไฟล์ที่เพิ่มผ่าน `/private/tmp/openapi-format.log`
- Frontend Build และ Lint ผ่าน `/private/tmp/openapi-web-build.log`, `/private/tmp/openapi-web-lint.log`
- ผู้ตรวจอิสระไม่พบปัญหาสำคัญในขอบเขตเทสต์ที่เพิ่ม
- คำสั่งเริ่มแรกใน sandbox ถูก VSTest socket permission ปฏิเสธ ไม่ใช่ OpenAPI500; รันใหม่ด้วยสิทธิ์ที่เหมาะสมผ่าน

## สิ่งที่ยังต้องมีเพื่อแก้สาเหตุ

เมื่อเกิดอีก ให้เก็บ response/status, เวลา UTC, correlation ID, backend exception/stack trace, commit และสภาพแวดล้อม/เทสต์ที่กำลังรัน แล้วสร้างเทสต์ที่ทำให้ล้มเหลวด้วยสาเหตุเดียวกันก่อนแก้ ไม่ควรปิดประเด็นจากชุดผ่านนี้ และไม่เพิ่ม retry เพื่อซ่อน500

ไม่มีการแก้ production code ไม่มี commit/push คงรายการติดตามอื่นทั้งหมดและพักมือถือจริง/NASจริงตามเดิม

# รายงานตรวจรับ Module 7D — Task 13

ขอบเขต: ให้คำขอส่วนเพิ่มที่มีอยู่ก่อนคำขอหลักปิดดำเนินการต่อได้ โดยห้ามสร้างส่วนเพิ่มใหม่หลังหลักปิด

## การเปลี่ยนแปลง

- แยกเงื่อนไขสร้างใหม่กับดำเนินการต่อ เชื่อมการยืนยัน/ส่งแก้/ตรวจคำขอและ action บนหน้าจอ โดยยังตรวจสิทธิ์ปัจจุบัน วงเงินสะสม การแยกหน้าที่ และช่องส่วนเพิ่มรออนุมัติเพียงหนึ่งรายการ
- เพิ่มหลักฐานลำดับสร้างภายใต้ transaction และ family lock เดียวกับการปิด ไม่ใช้เวลาจากผู้ใช้ ไม่เปิดคำขอหลักกลับ และคงผลอนุมัติ/ประวัติบุคคลเดิม
- ข้อมูลก่อนอัปเกรดที่คำขอหลักยังเปิดและมีหลักฐานครบ จะได้ legacy baseline ที่แยกจากเลขลำดับสร้างจริง หากหลักปิดแล้วหรือพิสูจน์ข้อมูลเดิมไม่ได้ migration จะหยุดก่อนเปลี่ยน schema ไม่เดาประวัติย้อนหลัง
- เพิ่ม migration พร้อมข้อบังคับลำดับที่ commit จริง ประวัติแก้ไม่ได้ และ guard ป้องกัน rollback ที่ทำให้ provenance สูญหาย

## หลักฐานและข้อจำกัด

ผู้ลงมือ Hooke ใช้ behavioral RED จากส่วนเพิ่มเดิมส่งใหม่แล้วได้ 409 แทน 200 และเคส provenance/closure order จากนั้นชุดเฉพาะสุดท้ายผ่าน 19/19 เคส

รอบผลกระทบกว้างก่อนแก้ผ่าน 283/284 เคส; เคสล้มเกิดจาก fixture ใช้ model ใหม่บน schema 7C แก้ legacy branch ให้ใช้คอลัมน์เดิม จากนั้นพบ SQL ของ idempotent migration ที่ใช้ SELECT advisory lock ภายใน DO block แก้เป็น PERFORM และรันเคสที่ล้มให้ผ่าน ก่อนตรวจทั้งคลาส migration ผ่าน 15/15 ไม่เปลี่ยนผลรอบล้มเดิมให้เป็นผ่าน

ผู้ตรวจ Darwin ตรวจอิสระให้ Spec PASS / Quality Approved ไม่พบ Critical/Important คง T13-REVIEW-M1 เรื่อง EF Query[10102] ร่วมกับ Minor log เดิม ไม่อ้างว่า log ไม่มี warning ผู้ประสานงานตรวจ HEAD/index และ hash งานเดิม 14 paths แล้วตรงเดิม

ชุดรวมรอบแรกพบเคสแก้เงินคืนล้มหลัง rollback: Task13 Down ถอดคอลัมน์ก่อน guard ขั้นถัดไปปฏิเสธ จึงหยุดรอบนั้นและไม่นับเป็นผ่าน แม้ process ที่ถูกหยุดคืน exit 0 แก้ preflight ให้ปฏิเสธก่อน DDL พร้อม RED ของเคสเดิมและ atomicity test จากนั้น targeted 3/3 และ affected 68/68 ผ่าน ข้อจำกัดคือ downgrade ที่มี guarded history รวม Normal request จะถูกปฏิเสธแม้ย้อนเพียงขั้นเดียว เพื่อรักษา schema ให้ครบ

ผู้ตรวจ Darwin ตรวจซ้ำเฉพาะ fix ให้ T13-R1 = ADDRESSED ไม่มีปัญหาใหม่จากการแก้ คง Minor T13-REVIEW-M1 และข้อจำกัด downgrade ตามที่ระบุ

ชุดรวมรอบสองจบด้วยผล 3,420/3,461 ผ่าน และ 41 เคสล้มใน PaymentAccessTests จาก fixture ที่สร้างส่วนเพิ่มโดยไม่มี ordering proof และข้อมูลตั้งต้นตามเงื่อนไขใหม่ ปรับเฉพาะ fixture สองไฟล์ คง assertions สิทธิ์/การแยกหน้าที่เดิมและ production guard พร้อม valid control ของ schema tests จากนั้น targeted 20/20 และ affected 98/98 ผ่าน

ผู้ตรวจ Darwin ตรวจซ้ำให้ T13-R2 = ADDRESSED ไม่พบปัญหาใหม่จาก fix และยืนยันว่าข้อทดสอบ authorization/SoD เดิมไม่ถูกลด คง Minor T13-REVIEW-M1 ตามเดิม

การปิดอัตโนมัติและยอดสรุปทั้งชุดเป็น Task 14 ซึ่งยังไม่เริ่มในงานนี้ ไม่มีการ migrate ฐานจริง/push/merge คง M2-R1/R2-C1, HTTPS 504, Minor log, OpenAPI 500, T13-M1/T14-M1, T4-M1/T11-M1 สำหรับ Task 18 และ T12-M1 ภายใต้ Minor log พักมือถือจริง/NAS จริง

## ผลตรวจร่วม G


Backend ผ่าน 3462 เคส และ Frontend ผ่าน 178 เคส

| คำสั่ง | ผล | จบเวลา UTC |
|---|---|---|
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน exit 0 | 2026-11-02T07:58:05.492578+00:00 |
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | ผ่าน exit 0 | 2026-11-02T07:58:06.637687+00:00 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'` | ผ่าน exit 0 | 2026-11-02T09:56:51.169473+00:00 |
| `npm test` | ผ่าน exit 0 | 2026-11-02T07:57:49.768716+00:00 |
| `npm run build` | ผ่าน exit 0 | 2026-11-02T07:58:06.040102+00:00 |
| `npm run lint` | ผ่าน exit 0 | 2026-11-02T07:58:07.701698+00:00 |
| `git diff --check` | ผ่าน exit 0 | 2026-11-02T07:58:07.774061+00:00 |

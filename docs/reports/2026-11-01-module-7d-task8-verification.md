# ผลตรวจรับ Module 7D Task 8 — ทยอยคืนเงินและตรวจรับคืน

สถานะ: ผ่านผู้ตรวจอิสระและ Test, Build, Lint/Format ครบตามตารางหลักฐานด้านล่าง

- เจ้าของหรือผู้ยื่นแทนเตรียมรายการคืนเงิน เจ้าของต้องยืนยันก่อนการเงินตรวจรับ รองรับโอนและเงินสดพร้อมปลายทางบริษัทและหลักฐานทุกใบ
- Draft/Pending/Returned กันยอดเพื่อป้องกันเสนอซ้ำ แต่ยังไม่ลดหนี้จนการเงินรับรอง การส่งกลับคงยอดไว้ ลดหรือยกเลิกได้เฉพาะส่วนยังไม่รับรองพร้อมเหตุผลและประวัติ
- แก้เนื้อหาแล้วต้องให้เจ้าของยืนยันใหม่ ไม่แก้รายการที่รับรองแล้ว การแก้หลังรับรองและหลังปิดอยู่ใน Task 9
- ตรวจยอดร่วมกับการรับรองค่าใช้จ่ายภายใต้ transaction เดียวกัน ตรวจทั้งสองลำดับที่เกิดพร้อมกัน และแยก version conflict จากยอดไม่เพียงพอ
- คำนวณเงินจริงถึงเจ้าของโดยผลสะสางเหตุจ่ายผิดแทนฐานเดิม ไม่บวกซ้ำ เงินคืนจากผู้รับผิดคนไม่หักหนี้เจ้าของ
- เก็บวันคืนจริงและเวลาส่งหลักฐานครบจากระบบ แยกกำหนดเดิมกับกำหนดที่แก้แล้วตาม Asia/Bangkok การรอผู้ตรวจไม่เปลี่ยนเวลาส่งเดิม
- ไม่มี schema/migration หรือ UI/API ใหม่ การเงินรับรองปลายทางด้วยการตรวจจริง ไม่เชื่อมธนาคารอัตโนมัติ

ผู้ลงมือ Carson และผู้ตรวจอิสระ Plato แยกกัน ผู้ตรวจให้ผ่านทั้งข้อกำหนดและคุณภาพ ไม่พบ Critical/Important/Minor ใหม่ มีหลักฐาน RED จาก expected409/actual200 ก่อนแก้ และ focused สุดท้ายผ่าน39/39, regression84/84 พร้อม full-solution Format บน source เดียวกับที่ตรวจ

ข้อจำกัดการตรวจ: ไม่มีเทสต์ refund/refund race ใหม่โดยตรง ใช้หลักฐานเส้นทาง lock เดียวกันร่วมกับเทสต์ executor เดิมและ expense/refund race ทั้งสองลำดับ ไม่รับรอง workflow เหตุจ่ายผิดในอนาคตทั้งหมด ส่วน reader ปฏิเสธ accepted adjustments ที่ยังไม่รองรับเพื่อป้องกันยอดผิดจนงานถัดไปเชื่อมต่อ

คง M2-R1/R2-C1, HTTPS 504, Minor log, OpenAPI 500, T13-M1/T14-M1 และ T4-M1 สำหรับ Task18; T6-M1 ปิดแล้ว พักมือถือจริง/NAS จริง ไม่รวม push/merge/deploy/migrate ฐานจริง รักษาไฟล์ค้างเดิม14ไฟล์

หลักฐาน local: /tmp/7d-t8-final-backend-summary.jsonl, /tmp/7d-t8-final-frontend-summary.jsonl, /tmp/7d-t8-final-test.log, /tmp/7d-t8-implementer/ และ task-8-report.md/task-8-review-report.md ใน .superpowers/sdd/2026-10-31-module-7d-settlement-refunds/


Backend ผ่าน 3299 เคส และ Frontend ผ่าน 178 เคส

| คำสั่ง | ผล | จบเวลา UTC |
|---|---|---|
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน exit 0 | 2026-11-01T10:43:50.754991+00:00 |
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | ผ่าน exit 0 | 2026-11-01T10:52:03.663343+00:00 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'` | ผ่าน exit 0 | 2026-11-01T12:36:15.288380+00:00 |
| `npm test` | ผ่าน exit 0 | 2026-11-01T10:52:08.407991+00:00 |
| `npm run build` | ผ่าน exit 0 | 2026-11-01T10:52:19.471186+00:00 |
| `npm run lint` | ผ่าน exit 0 | 2026-11-01T10:52:21.139258+00:00 |
| `git diff --check` | ผ่าน exit 0 | 2026-11-01T10:52:21.214653+00:00 |

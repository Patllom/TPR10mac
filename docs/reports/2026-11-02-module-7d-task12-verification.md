# รายงานตรวจรับ Module 7D — Task 12

ขอบเขต: ปลดเฉพาะเหตุจ่ายผิดที่สะสางครบ และเตรียมงวดส่วนขาดให้ผ่านตรวจใหม่ โดยรักษาประวัติการจ่ายและป้องกันใช้สิทธิ์ซ้ำ

## การเปลี่ยนแปลง

- เพิ่ม IncidentPaymentBridge สำหรับยืนยันปลดเฉพาะเหตุที่สะสางครบ และสร้างงวดส่วนขาดสถานะ PendingReview พร้อมสิทธิ์และยอดกันในธุรกรรมเดียว
- เพิ่มประวัติปรับฐาน reservation และ claim ที่อ้าง incident/attempt/installment ชัดเจน คง historical Paid/Consume เดิม ไม่สร้างการจ่ายปลอม
- ใช้ projection คุมวงเงินที่มี provenance และข้อจำกัดฐานข้อมูล การ activate สิทธิ์ลด usage พร้อมเพิ่ม Reserved เท่ากัน จึงไม่เปิด available ให้งวดปกติ
- Return คงสิทธิ์ แก้ยอดเปลี่ยน claim/reserve พร้อมกัน ยกเลิกคืนเฉพาะสิทธิ์ที่ยังไม่ใช้ และ consumed claims ไม่เปิดกลับ
- เชื่อม read/executor/ledger/termination ให้ใช้ฐานคำนวณเดียวกัน พร้อม migration guards ป้องกันสูญประวัติและ downgrade ที่ทิ้ง schema ครึ่งหนึ่ง

ชุดเฉพาะสุดท้ายผ่าน 27 เคส และชุด migration dependencies ผ่าน 49 เคส รอบ broad regression เดิมผ่าน 544/546; สองเคสล้มจาก fixture ที่ materialize model ใหม่บน schema เก่าและ Down ที่ถูก lower guard ปฏิเสธหลังถอดคอลัมน์แล้ว แก้ fixture ให้ใช้คอลัมน์เดิมและเพิ่ม guard ก่อน DDL จากนั้นทดสอบส่วนที่แก้ผ่าน ไม่เปลี่ยนผล aggregate รอบเดิมเป็น PASS

## การทดสอบและตรวจอิสระ

ผู้ลงมือ Hume และผู้ตรวจ Banach แยกกัน รอบแรกพบ T12-R1: allowedActions ยังใช้ historical Paid/สถานะเก่า ทำให้ปุ่มยุติยอดไม่ปรากฏ แก้ PaymentUiService พร้อมเทสต์ HTTP/command จริง RED 2 เคส แล้ว GREEN 3/3 และ scoped regression 95/95 ผู้ตรวจซ้ำให้ ADDRESSED และ Approved ไม่มีข้อสำคัญค้าง

คง T12-M1 ร่วมกับ Minor log เดิม: EF Query[10102] Skip/Take ไม่มี OrderBy ยังพบใน test logs ไม่ถือว่าปิดและไม่อ้างว่า logs ไม่มี warning ส่วน current authorization ก่อน replay/commit ใช้ executor เดิมที่ตรวจแล้ว แต่ไม่ได้อ้างว่ามี dedicated test ครบทุกจังหวะ expiry ของ bridge

คงรายการติดตามเดิม M2-R1/R2-C1, HTTPS 504, Minor log, OpenAPI 500, T13-M1/T14-M1, T4-M1 และ T11-M1 สำหรับ Task 18 พักมือถือจริง/NAS จริง ไม่มีการ push/merge, migration ฐานจริง หรือเริ่ม Task 13

## ผลตรวจร่วม G


Backend ผ่าน 3440 เคส และ Frontend ผ่าน 178 เคส

| คำสั่ง | ผล | จบเวลา UTC |
|---|---|---|
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน exit 0 | 2026-11-02T01:49:55.948665+00:00 |
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | ผ่าน exit 0 | 2026-11-02T01:57:34.597052+00:00 |
| `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'` | ผ่าน exit 0 | 2026-11-02T03:53:52.067774+00:00 |
| `npm test` | ผ่าน exit 0 | 2026-11-02T01:57:35.762552+00:00 |
| `npm run build` | ผ่าน exit 0 | 2026-11-02T01:57:47.474591+00:00 |
| `npm run lint` | ผ่าน exit 0 | 2026-11-02T01:57:49.215887+00:00 |
| `git diff --check` | ผ่าน exit 0 | 2026-11-02T01:57:49.296724+00:00 |

# Module 6B — Task 6: ย้ายสำเนารูปโดยรักษาหลักฐานเดิม

สถานะ: ผ่านการตรวจรับ Task 6 — ใช้ TDD, Code Review และรัน Test/Build/Lint ครบ ไม่ได้หมายความว่า Module 6B หรือการทดสอบ NAS จริงเสร็จทั้งหมด

## ขอบเขต

เพิ่มงานย้ายรูปจากโฟลเดอร์หรือ NAS ที่ลงทะเบียนแล้ว ไปยังที่เก็บที่ตรวจความพร้อมแล้ว โดยไม่เปลี่ยนรหัสรูป รหัสเหตุการณ์ วันเวลา ชนิดเข้า/ออก checksum หรือ pixels ที่ประทับไว้ ไม่ใช้บริการประทับภาพซ้ำ

ไม่มีการลบสำเนาต้นทาง ไม่มีการถอดหรือ mount NAS จริง ไม่มีการเปลี่ยน Preview และไม่มีการเพิ่มกล้อง/GPS หรือหน้าลงเวลาใน Task นี้

## วิธีทำงาน

1. ผู้มี `attendance:storage-manage` และ MFA ล่าสุดส่งคำขอเริ่มย้าย พร้อม request ID, รุ่นต้นทาง/ปลายทาง และเหตุผล
2. ระบบตรวจว่าต้นทางไม่ใช่ active write target ไม่มีงานที่ขัดกัน และที่เก็บพร้อม จากนั้นปิดรับ reservation ใหม่ที่ต้นทาง สร้าง job/manifest/audit ใน transaction เดียว
3. รวมรูปสถานะ Prepared และ Published ทั้งภาพเต็มและภาพย่อ ไม่ย้าย raw upload; reservation ที่ตรึงต้นทางไว้ก่อนหน้ายังเตรียมรูปให้เสร็จได้
4. Worker จอง item ด้วย lease 60 วินาทีและ fencing version แล้วปล่อย database lock ก่อนทำ filesystem I/O
5. อ่านต้นทาง ตรวจ checksum คัดลอกแบบไม่เขียนทับ อ่านปลายทางกลับมาตรวจขนาดและ SHA-256 แยกตาม variant
6. Transaction รอบสุดท้ายตรวจ lease/fence รุ่นที่เก็บ สถานะงาน checksum และสิทธิ์ปัจจุบันของผู้สั่งอีกครั้ง แล้วสลับสำเนาใหม่เป็น Active และเก่าเป็น Fallback พร้อม audit
7. ก่อน Completed ต้องตรวจรูปที่เตรียมเสร็จภายหลัง ไม่มี reservation ที่ยังค้างและไม่มี Active copy ที่ต้นทางเหลืออยู่

Completed หมายถึงงานนี้ย้ายและตรวจสำเนาครบ ไม่ใช่อนุมัติให้ถอด NAS หรือลบต้นทาง การเลิกใช้ต้นทางยังต้องมีขั้นตอนตรวจ restore/read และอนุมัติแยก

## API

เส้นทางทั้งหมดอยู่ใต้ `/api/v1/attendance/storage` ใช้สิทธิ์จัดการ storage และ recent MFA เดิม ทุก POST ใช้ CSRF; ไม่เปิดพาธจริงหรือข้อมูลภาพ

| วิธี/เส้นทาง | ผลสำเร็จ |
| --- | --- |
| POST `/migrations` | 202 พร้อมสถานะงาน |
| GET `/migrations?offset=0&limit=25` | 200 รายการงาน; limit สูงสุด 100 |
| GET `/migrations/{id}` | 200 สถานะและจำนวนรายการ |
| POST `/migrations/{id}/resume` | 202 เมื่อรุ่นตรงและงาน Blocked ผ่านการตรวจใหม่ |

request ID เดิมและข้อมูลเดิมคืน job เดิมพร้อม audit การเรียกซ้ำ ข้อมูลต่างตอบ 409; start/resume ที่ audit ล้มเหลวไม่ commit การเปลี่ยนแปลง

## การกู้คืนและการหยุดงาน

- Process หยุดก่อน/หลังคัดลอก: lease หมดแล้ว worker ใหม่ใช้ manifest/key เดิม ตรวจสำเนาที่มีอยู่ก่อนรับเข้า ไม่เขียนทับ
- Audit/ฐานข้อมูลล้มเหลว: สำเนาไฟล์อาจอยู่ปลายทางแต่ยังไม่ Active; สำเนาต้นทางยังเปิดใช้ จน metadata/audit commit สำเร็จ
- Checksum ไม่ตรง: Blocked ทันที ไม่ retry อัตโนมัติ ไม่แก้หรือเขียนทับไฟล์ที่เสีย
- I/O ชั่วคราว: retry ห้าครั้งหลังครั้งแรก หน่วง 1/5/30/120/300 วินาที จากนั้น Blocked ต้อง resume ชัดเจน
- ผู้สั่งถูกปิดบัญชีหรือถอน capability: Blocked ไม่ cutover แม้คัดลอกสำเร็จ; ไม่ใช้ MFA/session ที่หมดอายุของหน้าเว็บตัดสิน worker เบื้องหลัง
- Resume ต้องมี recent MFA, expectedVersion ปัจจุบัน, readiness ใหม่ และผู้สั่งเดิมยังมีสิทธิ์ ไม่ย้ายความเป็นเจ้าของคำสั่งให้ผู้กด resume โดยเงียบ
- สำเนา Fallback อ่านได้เฉพาะผ่าน metadata และการตรวจสิทธิ์รูปเดิม ไม่ให้สิทธิ์ดูภาพแก่ storage operator โดยอัตโนมัติ

## ข้อตัดสินใจที่ใช้ใน Task นี้

1. Prepared ที่ย้ายแล้วมี Active metadata แต่ยังอ่านรูปไม่ได้จน Published พร้อม binding; ขั้นเผยแพร่เลือก Active ปัจจุบันต่อ variant ก่อนสำเนาที่ตรึงเดิม ป้องกันกลับไปเปิดต้นทางหลังย้าย
2. เก็บรุ่นต้นทาง/ปลายทางที่ผู้ใช้ส่งไว้สำหรับ idempotency และตรวจรุ่นปัจจุบันก่อน–หลัง I/O แต่ละรายการ เพราะ health refresh เพิ่มรุ่นได้ หากรุ่นเปลี่ยนจะ retry ไม่ cutover จากข้อมูลเก่า
3. คำว่า retry สูงสุดห้าครั้งตีความเป็นครั้งแรกบวกห้า retry รวมสูงสุดหก attempts เพื่อใช้ช่วงหน่วงทั้งห้าค่าตามแผน; ผลกระทบคือ I/O ได้เพิ่มอีกหนึ่งครั้งเมื่อเทียบกับการนับห้าครั้งรวมครั้งแรก
4. Reservation ที่ยังไม่เสร็จและไม่มี lease ที่ยังใช้ได้ทำให้ job Blocked เมื่ออายุงานครบ 60 วินาที ไม่ทำให้หลักฐานหมดอายุหรือถูกลบ; ผู้ดูแลอาจต้อง resume อีกครั้งเมื่อการเตรียมรูปเสร็จช้า
5. ตัวรันเบื้องหลังตรวจงานทุก 30 วินาที แบบวนคิว ครั้งละไม่เกิน 100 items โดยอ่านสถานะงานจากฐานข้อมูล มี protected configuration `AttendanceStorage:MigrationWorkerEnabled` (ปกติเปิด) ปิดแล้วงานยังคงอยู่และไม่ถูกรัน งานถัดไปอาจรอรอบประมาณ 30 วินาที
6. Manifest เริ่มต้นสร้างครบใน transaction เดียวด้วย `INSERT SELECT` ไม่โหลดทั้งคลังเข้า process; การเติมรายการภายหลังจำกัดครั้งละ 100 ที่ขอบรอบ batch ไม่สแกนก่อนคัดลอกทุก item คำสั่งสร้างครั้งแรกยังใช้เวลาตามขนาดคลังใน PostgreSQL จึงยังต้องวัดภาระจริงใน Task 8

## Code Review และข้อจำกัดที่ยังคงไว้

Reviewer แยกตรวจแบบ read-only พบ Important หนึ่งข้อเรื่อง manifest ไม่จำกัดงานภายใต้ lock ร่วม เพิ่ม regression ด้วย 61 objects/122 copies ซึ่งล้มเหลวที่ 122/120 รายการก่อนแก้เป็นการสร้างแบบชุดและเติมแบบจำกัดจำนวน หลังแก้ชุด Task 6 ผ่าน 33/33 กรณี ไม่มี Critical ที่ reviewer รายงาน

ข้อย่อยที่เลื่อนไว้ ไม่ใช่ข้อบกพร่องการเปิดเผยข้อมูลที่ตรวจพบ:

- เพิ่ม assertion โดยตรงว่า Prepared ที่มี Active copy หลังย้ายยังอ่าน full/thumbnail/download ไม่ได้ ก่อนเผยแพร่ ขณะนี้ reader ยังบังคับ Published และมีชุดทดสอบปฏิเสธ Prepared จาก Task 5
- เพิ่มการหยุด–เปิด application ใหม่จริง แล้ว probe/resume จนสำเร็จ ปัจจุบันทดสอบโหลด job จากฐานข้อมูลด้วย scope ใหม่, readiness หมดอายุ และ resume แล้ว แต่ไม่อ้างว่ารับรองการ restart application/restore ระบบครบถ้วน

ประเด็นที่ reviewer เว้นและข้อตัดสินใจที่คงไว้:

- NAS จริงและการรับรอง backup/restore/ภาระระบบ: อยู่ Task 8; การทดสอบ local ไม่รับรอง NAS หรือ production SLA
- การถอนสิทธิ์ขณะส่งภาพทั่วไป: คง Task 5/7 และรัน regression เดิม; Task นี้ตรวจสิทธิ์ผู้สั่งย้ายและการอ่านระหว่าง cutover ไม่เขียน reader ใหม่
- การแปลงภาพ/metadata/หลายเฟรม/ตัวอักษรไทย: คง Task 2 เพราะงานย้ายรักษา bytes เดิม ไม่ประทับใหม่
- Portal/event/challenge/pairing: อยู่ 6C จึงยังไม่ใช่หน้าลงเวลาที่ใช้งานได้จาก Task นี้
- ผล Test/Build/Lint และเอกสาร: ผู้ดำเนินงานหลักตรวจจริง ไม่ถือคำ review เป็นหลักฐานว่าทดสอบผ่าน

## หลักฐานตรวจรับ

- TDD: API ที่ยังไม่มีตอบ 404 ก่อนเพิ่ม; worker ที่ยังไม่มีทำให้ชุดทดสอบล้มเหลวก่อนเพิ่ม; scheduler ที่ยังไม่มีทำให้ทดสอบล้มเหลวก่อนเพิ่ม
- ชุด Task 6 หลังแก้ review: 33/33 ผ่าน รวม migration/race/API/manifest bounds
- ชุด migration/API/OpenAPI: 88/88 ผ่านก่อนเพิ่มการตรวจขอบเขตอีกชุด
- Regression evidence/storage/OpenAPI ก่อนแก้ review: 328/328 ผ่าน; ไม่ใช้แทนผลทั้งระบบหลังแก้
- Frontend tests 40/40 ผ่าน; `npm run build` และ `npm run lint` exit 0
- Backend build หลังแก้ review: exit 0 ไม่มี warning/error; format verification exit 0
- Backend ทั้งชุดหลังแก้ review: 1,263/1,263 ผ่าน ไม่มี fail/skip ใช้เวลา 31.7848 นาที ตรวจ hash source 349 ไฟล์ตรงกับตอนเริ่มรัน

คำสั่งตรวจรับหลัก:

```sh
dotnet test backend/TPR10.sln --no-restore --logger 'console;verbosity=normal'
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm test
npm run build
npm run lint
```

โค้ดหลังเริ่ม full suite ไม่มีการเปลี่ยนแปลง มีเฉพาะอัปเดตเอกสารผลตรวจ ไม่ push/merge และไม่เริ่ม Task 7 ในคำขอนี้

หลักฐานทั้งหมดใช้ภาพสังเคราะห์ โฟลเดอร์ทดสอบส่วนตัวและฐาน PostgreSQL ชั่วคราว ไม่ใช่หลักฐานผ่านการทดสอบ NAS จริง การตรวจ NAS จริงยังอยู่ใน Task 8

# Module 6C Task 6 — API ลงเวลา ประวัติ และตัวส่งคำขอ

## ขอบเขต

เชื่อมบริการ Tasks 3–5 กับ HTTP โดยใช้ session, CSRF, current grants และการจัดเก็บหลักฐานเดิม ไม่เพิ่ม dependency หรือ migration ไม่แก้ Landing Page และยังไม่สร้างหน้ากล้องของ Task 7

| เส้นทางใต้ `/api/v1/attendance` | หน้าที่ |
| --- | --- |
| POST `/capture-challenges` | ออก challenge ผูกบัญชี/session/action/Site/key |
| POST `/check-ins`, `/check-outs` | รับ metadata และภาพ ลงเวลาแบบ atomic; replay คืน receipt เดิม |
| GET `/operations/{idempotencyKey}?action=CheckIn\|CheckOut` | ตรวจผลคำขอเดิม เฉพาะผู้ใช้และ session ที่ผูกไว้ |
| GET `/current` | รอบเปิดและสถานะลงเวลาครบคู่วันนี้ |
| GET `/mine`, `/team`, `/hr` | ประวัติตามสิทธิ์พร้อมช่วงวันและ cursor |
| GET `/days/{dayId}` | รายละเอียดวันที่อ่านได้ตามสิทธิ์ปัจจุบัน |

## การรับภาพและความปลอดภัย

- อ่าน multipart แบบ streaming จำกัด request 11 MiB, metadata 16 KiB, ภาพ 10 MiB ตรวจภาพจริงไม่เกิน 20 ล้านพิกเซลด้วย pipeline 6B
- ต้องมี `metadata` ชนิด JSON และ `photo` ชนิด JPEG/PNG อย่างละหนึ่งส่วน ปฏิเสธส่วนซ้ำ/ไม่รู้จัก, base64, URL, JSON field ซ้ำ/เกินหรือขาดข้อมูลที่จำเป็น
- ไม่เก็บ raw upload ลงไฟล์ชั่วคราวใน HTTP parser ใช้ buffer ที่มีขอบเขต ก่อนผ่านบริการประทับและจัดเก็บเดิม
- Kestrel ยกขีดจำกัดเฉพาะ endpoint ที่มี metadata สำหรับอัปโหลด nginx ใช้ exact location เฉพาะสองเส้นทางนี้เป็น 11 MiB เส้นทางอื่นยังเป็น 1 MiB ตามเดิม
- ไม่ผ่อน session, CSRF, MFA หรือ TLS ทุก response ส่วนตัวใช้ no-store; 429 ส่ง Retry-After และ 503 ไม่ใช่คำสั่งให้ออกจากระบบ

## การตรวจสิทธิ์ขณะส่งข้อมูล

ประวัติ รายละเอียด ผล operation และ receipt หลังลงเวลา ใช้ result ที่เริ่มอ่านใหม่ในจังหวะส่ง HTTP ไม่ส่ง DTO เก่าที่ผ่านการตรวจไว้นานแล้ว

ใช้ PostgreSQL connection แยกแบบไม่คืน connection ลง pool และ session advisory lock `7241002` แบบ exclusive ตลอดการอ่าน การ commit audit การ serialize และส่ง response งบเวลาสูงสุด 5 วินาที JSON สูงสุด 1 MiB ไม่มี filesystem I/O ใต้ lock

เหตุผลที่ใช้ exclusive: บริการเดิมเปิด transaction และขอ exclusive identity lock บน connection เดียวกันได้โดยไม่เปลี่ยนสัญญาของบริการ หากใช้ shared แล้ว upgrade จะเสี่ยง deadlock ต้นทุนคือรายการอ่านถูกเรียงลำดับร่วมกับการเปลี่ยนสิทธิ์ ต้องวัดโหลดก่อน Production

ก่อนส่ง byte แรกตรวจ session และสิทธิ์รูป/ฐานสิทธิ์ซ้ำ เมื่อถอนสิทธิ์ชนะ lock จะไม่ส่งข้อมูล หากการส่งชนะจะจบภายใน budget ก่อนการถอนสิทธิ์ commit กรณีส่งค้างหรือหลุดจะ abort และปิด physical connection เพื่อปล่อย lock ไม่ต่อ JSON ลงท้าย response ที่เริ่มส่งแล้ว ไม่สามารถเรียกคืนข้อมูลที่ผู้ใช้ดาวน์โหลดไปแล้วได้

## Client transport

- แยกฟังก์ชัน exact route จาก `authMutation` เดิม ไม่ขยาย allowlist เป็นเส้นทางอิสระ
- ขอ CSRF ใหม่ด้วย timeout 10 วินาที ใช้ same-origin credentials, no-store และ redirect:error
- ส่ง FormData ให้ browser สร้าง boundary เอง ไม่ตั้ง Content-Type ของ upload เป็น JSON
- Upload timeout 75 วินาที เมื่อ network/timeout หลังส่งแล้ว คืน `CaptureUncertainError` พร้อม key เดิมเพื่อให้ UI ตรวจ operation ไม่ retry หรือสร้าง key ใหม่อัตโนมัติ
- ตรวจ AbortSignal หลังรับ CSRF และก่อนส่ง รองรับ Task 7 ยกเลิกเมื่อเปลี่ยนบัญชี ไม่มี localStorage หรือ offline queue สำหรับภาพ/GPS
- ประเภท TypeScript ครอบคลุม challenge, capture, receipt, operation และ history ข้อมูลที่รับยังต้องผ่าน parser ของหน้าจอตาม Task 8 ก่อนแสดงผล

## TDD และการตรวจโค้ด

- RED client 6 กรณีจาก transport ที่ยังไม่มี
- RED API/OpenAPI 20 กรณีจาก endpoint ที่ยังไม่มี; forged CSRF อีกหนึ่งกรณีผ่านอยู่แล้วด้วย middleware เดิม จึงไม่อ้างว่าเป็น RED ของฟีเจอร์ใหม่
- Review พบ OpenAPI action เป็น integer แต่ HTTP รับข้อความ จึงเพิ่ม RED สอง environment และแก้ schema ให้รับ CheckIn/CheckOut พร้อมบันทึก query parameters
- ปรับ inventory OpenAPI เดิมให้รวม 9 เส้นทางใหม่หลังทดสอบจับการเปลี่ยนแปลงได้
- ตรวจโดยผู้เขียนตามแนวทาง Native: ขอบเขต buffer, strict parsing, CSRF ก่อน mutation, การอ่านใหม่ตอนส่ง, reentrant lock, การปล่อย lock, timeout/cancellation, audit rollback และการไม่เปลี่ยนข้อมูลเก่า
- การตรวจทั้งสาขาโดยผู้ตรวจอิสระยังอยู่ Task 9 ไม่ถือว่า Task นี้พร้อม merge หรือ Production

Fixture ใช้ HTTP pipeline และ PostgreSQL จริง การฉีด `image-busy` แทนเฉพาะ boundary ประมวลผลภาพเพื่อยืนยันการแปลง 429/Retry-After ไม่แทนการตรวจ load จริง แก้ fixture ที่ต้องเพิ่ม storage version เมื่อปิดรับไฟล์ โดยไม่ผ่อน constraint ของระบบ

## ผลตรวจ

- ชุด API/OpenAPI/Identity/Delivery รอบแรกผ่าน 83/83
- ชุดร่วม API/ประวัติ/CSRF รอบขยายผ่าน 150/150 ไม่มีข้ามกรณี ใช้เวลา 1 นาที 31 วินาที; เพิ่มการตรวจขอบเขต Kestrel อีกสองกรณีหลังรอบนี้ให้ตรวจในชุดทั้งโครงการและหลัง commit
- `npm test` ผ่าน 49/49; `npm run lint`, `npm run build` ผ่าน
- `dotnet build` ผ่าน ไม่มี warning/error; `dotnet format --verify-no-changes` และ `git diff --check` ผ่าน
- nginx HTTPS จริงบนพอร์ตสุ่ม: config เดิมตอบ413ต่อภาพพร้อมmetadataที่ควรรับได้ จากนั้น config ใหม่ผ่านทั้งสอง upload routes, 11MiB+1 ตอบ413, routeอื่นยัง1MiB, Host/Origin/CSRF/Forwarded-Proto ส่งต่อถูกต้อง และปฏิเสธ Host อื่น
- สคริปต์ proxy ใช้ container และใบรับรองทดสอบชั่วคราว ล้างทรัพยากรของตนเมื่อจบ ไม่เปลี่ยน Preview ที่เปิดอยู่
- Backend ทั้งโครงการ: **ผ่าน 1,478/1,478** ไม่มี failed/skipped ใช้เวลา 37.3025 นาที หลักฐาน `task6-backend-full.log`

Fingerprint backend ก่อนและหลังรอบเต็มตรงกัน: `d2fc0351c20c63e5ca2c755737a827aec755710833609de814b08f365c27e11e` ไม่มีการแก้โค้ดระหว่างรอบยืนยัน

หลักฐานดิบอยู่ในพื้นที่ที่ Git ไม่ติดตามของแผน 6C: `task6-api-red.log`, `task6-client-red.log`, `task6-contract-review-red.log`, `task6-api-review.log`, `task6-focused-final.log`, `task6-proxy-red.log`, `task6-proxy-green.log` และ `progress.md`

## งานถัดไปและข้อจำกัด

Task 7 จะเพิ่มหน้ากล้อง/GPS และ state machine ของ UI ตามด้วยประวัติใน Task 8 และตรวจรับรวม Task 9 การผ่าน API ไม่เท่ากับทดสอบมือถือหรือ NAS จริง และยังต้องตรวจ Production gate แยก

ไม่เปลี่ยนพอร์ต dev 4000 / production-build 4001 / HTTPS Preview 4443 ไม่แตะ checkout หลัก ยังไม่ push หรือ merge งาน 6C

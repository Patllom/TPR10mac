# Module 6C — Task 3: Challenge และการติดตามคำขอลงเวลา

วันที่ตรวจ: 26 กันยายน 2026

สถานะ: ผ่านเกณฑ์เทคนิค Task 3 แล้ว ยังไม่ใช่การส่งมอบ Module 6C ทั้งระยะ และยังไม่ push/merge

## ขอบเขตที่เพิ่ม

- `CaptureChallengeService` ออก challenge อายุ 120 วินาที ผูกผู้ใช้จาก session ฝั่งเซิร์ฟเวอร์, session, เข้า/ออก, Site ที่ระบุครบทั้งสามระดับ และ idempotency key แบบ UUID ไม่มีช่องให้เลือกเจ้าของจาก client
- ตรวจ session/MFA ตามระบบเดิม การมอบหมาย Site แบบตรงระดับ capability `attendance:record` และสถานะการเป็นพนักงานปัจจุบัน ไม่มีสิทธิ์จาก Workspace/Project ครอบลงมาโดยอัตโนมัติ
- ขอซ้ำด้วยข้อมูลเดิมก่อนหมดอายุได้ challenge เดิมและวันหมดอายุเดิม ไม่ยืดเวลา ขอพร้อมกันสร้างเพียงหนึ่งรายการพร้อม audit หนึ่งครั้ง หาก audit ล้มเหลวไม่เหลือ challenge
- `CaptureValidation` ตรวจ GPS เป็นตัวเลข finite ในขอบเขต latitude/longitude และ accuracy มากกว่า 0 อายุ sample ตั้งแต่ 0 ถึง 60 วินาที รวมขอบ 60 วินาที ไม่มี geofence และไม่ปฏิเสธเพราะ accuracy มากกว่า 100 เมตร
- เวลาอุปกรณ์เป็น metadata ไม่แทนเวลาเกิดเหตุการณ์จากเซิร์ฟเวอร์ ไม่รับเวลาในอนาคต การส่งครั้งแรกต้องถ่ายหลังออก challenge
- `CaptureOperationStore` ตรึงรหัสวัน เวลา ต้นสังกัด version ที่คาดหวัง และ digest ของ metadata พร้อม SHA256 ของ bytes ภาพ ใช้ JSON ของข้อมูลชนิดแน่นอนและ normalize UTC ไม่ hash multipart boundary
- เปลี่ยนรูปหรือ metadata ที่ถูกต้องระหว่างใช้ key เดิมให้ conflict ส่วนข้อมูลรูปแบบไม่ถูกต้องให้ validation error
- Lease อายุ 60 วินาที คำขอซ้ำขณะที่งานเดิมยังถือ lease ได้ conflict พร้อม Retry-After 2 วินาที เมื่อ lease หมดอายุแต่ challenge ยังไม่หมดอายุ reclaim ได้ โดยเพิ่ม fencing/version และคงข้อมูลที่ตรึงไว้ทั้งหมด
- ตรวจสิทธิ์ session/Site และ challenge/fence/lease อีกครั้งก่อนผู้เรียกจะทำ business commit หาก challenge หมดอายุแม้เริ่มคำขอทันเวลาแล้วก็ไม่ผ่าน
- อ่านสถานะได้เฉพาะผู้ใช้และ session ที่ผูกไว้ ใช้ key คู่กับประเภทเข้า/ออก ไม่เปิดอ่าน operation ของคนอื่นหรือ session ใหม่ แม้เป็นผู้ใช้เดิม

## การส่งต่องานให้ Task 4

| จุดเรียก | ข้อตกลง |
| --- | --- |
| `IssueAsync(request, ct)` | เปิด transaction เอง ออก challenge ใหม่คืน 201; retry ตรงกันคืน 200; ไม่สร้าง attendance event |
| `ClaimAsync(action, metadata, imageSha256, subject, dayId, ct)` | ผู้เรียกต้องเปิด identity transaction และเป็นเจ้าของ SaveChanges/commit; `subject` และ `dayId` มาจากการตรวจ pairing ฝั่งเซิร์ฟเวอร์ ไม่รับเป็นค่าที่เชื่อถือจาก client |
| `ValidateForCommitAsync(operationId, fence, ct)` | ผู้เรียกถือ identity transaction; service ตรวจฐานข้อมูลล่าสุดหลังล็อกพนักงานและคืน snapshot แบบไม่ติดตามการเปลี่ยนแปลง ไม่บันทึกผลเอง |
| `GetAsync(key, action, ct)` | เปิด transaction เอง ตรวจ session ปัจจุบัน และคืน DTO สถานะ/receipt แบบจำกัด fields ไม่มี GPS หรือ image bytes |

ลำดับ lock ใช้ `ScopeOperation.BeginAsync` สำหรับ identity ก่อน `AttendanceEmployeeLock.AcquireAsync` เสมอ ไม่มีงานไฟล์หรือภาพอยู่ใต้ lock ในบริการนี้ การ claim ยังไม่สร้างวัน/เหตุการณ์ ไม่เผยแพร่รูป ไม่ consume challenge และไม่สร้าง receipt; ทั้งหมดเป็นหน้าที่ Task 4 ใน business transaction เดียวกัน

Task 4 ต้องใช้ค่าที่คืนจาก operation เดิมเมื่อ retry ไม่ใช้เวลา/วัน/ต้นสังกัดที่คำนวณใหม่ เตรียมภาพนอก transaction และตรวจ final fence อีกครั้งก่อน commit ความล้มเหลวของฐานข้อมูลในเมธอดที่ใช้ transaction ของผู้เรียกต้องถูก rollback และแปลงเป็น 503 โดยผู้เรียก

การอ่านสถานะไม่แก้ฐานข้อมูล: lease หมดแต่ challenge ยังใช้ได้แสดง `Processing`; challenge หมดขณะยัง Processing แสดง `Expired` โดยไม่เปลี่ยนแถว ส่วน `Committed` คืน receipt เดิมได้แม้ challenge หมดอายุแล้ว หาก receipt ผิดรูปแบบ/ไม่ตรง operation ให้ 503 ไม่แปลงเป็น logout

## หลักฐาน TDD และการตรวจโค้ด

1. RED 37 กรณีก่อนเพิ่มบริการจริง ล้มเหลวเพราะยังไม่มีชนิดบริการที่ต้องการ ไม่ใช่เพราะเตรียมฐานข้อมูลไม่สำเร็จ
2. GREEN 37 กรณี แล้วขยายเป็น 68 กรณี ครอบคลุม revocation ระหว่างรอ identity lock, audit failure, GPS bounds, rollback, status และ receipt
3. การตรวจโดยผู้เขียนพบกรณี EF คืน entity ที่ติดตามค้างจาก transaction เก่า หลังอีกคำขอ reclaim lease ไปแล้ว: เพิ่ม `RetainedContextCannotReclaimAReplacementWorkersLiveLease` ได้ RED คาด 409 แต่ได้ 201
4. แก้โดย reload operation หลังถือ lock และอ่าน challenge ล่าสุดแบบไม่ติดตามการเปลี่ยนแปลง จากนั้นชุด Task 3 รวม schema regression ผ่าน 102/102 ไม่มี failed/skipped

ข้อผิดพลาดในการเตรียมทดสอบที่แก้ก่อนยืนยัน RED: ใช้ชื่อตารางจริง `user_scope_assignments` และ property `AuditEvent.EventType` ไม่เปลี่ยนข้อบังคับ production เพื่อให้ test ผ่าน

## ผลยืนยัน

| รายการ | ผลล่าสุด |
| --- | --- |
| ชุด Task 3 + `AttendanceRecordingSchemaTests` | ผ่าน 102/102 ไม่มี skipped ใช้เวลา 29 วินาที |
| `dotnet test backend/TPR10.sln` | ผ่าน 1,392/1,392 ไม่มี failed/skipped ใช้เวลา 34.9621 นาที |
| `npm test` | ผ่าน 43/43 |
| `npm run lint` | ผ่าน |
| `npm run build` | ผ่าน |
| `dotnet build backend/TPR10.sln --no-restore` | ผ่าน 0 warning/error |
| `dotnet format backend/TPR10.sln --no-restore --verify-no-changes` | ผ่าน |
| `git diff --check` และ `git diff --cached --check` | ผ่าน รวมไฟล์ใหม่ที่เตรียม commit |

ผลดิบอยู่ในพื้นที่ทดสอบที่ Git ไม่ติดตามของแผน 6C: `task3-red.log`, `task3-stale-context-red.log`, `task3-focused.log`, `task3-backend-full.log` และ logs Test/Build/Lint ของ Task 3

Fingerprint ของไฟล์ backend ก่อนและหลัง regression ตรงกัน: `ecf400bb289d69893b5a746b5cb0946c8e01791f50b5e1dc0898970ed0baec59` ไม่มีการเปลี่ยนโค้ดระหว่างรอบยืนยัน

## การตัดสินใจในขอบเขตและสิ่งที่ยังไม่ส่งมอบ

- เพิ่ม DI registration และ fixture PostgreSQL จริงนอกเหนือจากรายชื่อไฟล์ใน brief เพื่อพิสูจน์ว่าบริการใช้งานผ่าน registration จริงได้ ไม่เพิ่ม route หรือ dependency ใหม่ หากเปลี่ยนแนวทางในอนาคตสามารถถอด registration/helper โดยไม่เปลี่ยน schema
- เพิ่ม `ClaimAsync` และ `ValidateForCommitAsync` เป็นจุดเชื่อมสำหรับ Task 4 เพื่อทดสอบ idempotency/fencing ใน Task 3 โดยไม่ทำ business commit ล่วงหน้า หากสัญญาผู้เรียกปรับใน Task 4 ต้องปรับ tests ของ seam ให้สอดคล้องโดยคงกฎเรื่องเวลาและสิทธิ์
- Retry ตรวจอายุ GPS กับเวลาที่ตรึงใน operation เดิม ส่วน challenge/lease ใช้นาฬิกาปัจจุบัน จึงไม่ปฏิเสธข้อมูลเดิมเพียงเพราะเวลาผ่านไป และไม่ยืดอายุ challenge
- ตรวจโดยผู้เขียนตามวิธี Native; การตรวจทั้งสาขาโดยผู้ตรวจอิสระยังอยู่ Task 9 ไม่ถือว่ารอบนี้ผ่าน independent review หรือพร้อม merge
- ยังไม่มี API ลงเวลา/หน้ากล้อง/ประวัติใหม่ ยังลงเวลาใช้งานจริงไม่ได้ ต้องทำ Tasks 4–9 ต่อ ไม่แตะ checkout หลักหรือ Preview ไม่เปลี่ยนพอร์ต และยังไม่ push/merge งาน 6C
- การทดสอบ NAS จริง มือถือจริง และ Production gate ยังไม่ผ่านจาก Task นี้

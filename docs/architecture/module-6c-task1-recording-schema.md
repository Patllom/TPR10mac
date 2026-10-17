# Module 6C Task 1 — โครงสร้างข้อมูลลงเวลาและ lock ต่อพนักงาน

วันที่: 26 กันยายน 2026

สถานะ: Task 1 ผ่านการตรวจรับแล้ว ชุดเฉพาะ Task 33/33 และ backend regression ทั้งชุด 1,303/1,303 ผ่าน ไม่มีล้มเหลวหรือข้าม ไม่ใช่การส่งมอบทั้ง Module 6C

## ขอบเขตที่ทำ

- เพิ่ม `AttendanceDay`, `AttendanceEvent`, `CaptureChallenge`, `AttendanceOperation` และ DTO ตามแผน 6C
- วันทำงานคำนวณตาม Asia/Bangkok; unique คน+วันไม่แยกตาม Workspace และมีรอบเปิดได้ไม่เกินหนึ่งต่อคน
- เหตุการณ์เข้า–ออกเป็นข้อมูลเดิมที่แก้/ลบไม่ได้ เวลาออกต้องหลังเข้าและมีเหตุการณ์เข้าก่อน; ภาพต่อเหตุการณ์ไม่ซ้ำ
- อ้าง Site ด้วย Workspace/Project/Site ครบชุด และอ้างเจ้าของวัน/ต้นสังกัดด้วย composite FK
- ป้องกัน GPS นอกช่วง, NaN/Infinity, เวลาไม่ finite, snapshot เปลี่ยน และการอัปเดตวันโดยไม่เพิ่ม version
- Challenge ผูก session/actor/action/Site/key; Operation ตรึง payload digest/เวลา/ต้นสังกัด/วันเป้าหมาย พร้อม version และ fencing สำหรับ service ใน Task 3–4
- เพิ่ม `AttendanceEmployeeLock.AcquireAsync` ที่ต้องอยู่ใน transaction และถือ identity lock ก่อน ใช้ lock namespace แยกต่อพนักงาน ปลดเมื่อ transaction จบ
- Migration `20260926074317_AddAttendanceRecording` อัปเกรด/rollback/อัปเกรดกลับบนฐานทดสอบได้ และปฏิเสธ rollback เมื่อมีข้อมูลลงเวลา ไม่ลบประวัติเพื่อให้ rollback ผ่าน

## หลักฐาน TDD และการตรวจ

| การตรวจ | ผล ณ รอบนี้ |
| --- | --- |
| RED ก่อนมี schema | 21 tests ล้มเหลวเพราะไม่มีตารางใหม่ |
| RED ก่อนเพิ่ม guards | ตรวจพบการแก้ข้อมูลเดิม/ลำดับเวลา/rollback ที่ยังไม่ป้องกัน |
| RED เพิ่มเติม | ออกงานทั้งที่ไม่มี original check-in ถูกยอมรับก่อนเพิ่ม guard |
| GREEN ชุดเฉพาะ Task | 33/33 ผ่าน ไม่มี skip |
| Node tests | 43/43 ผ่าน |
| Next.js build / ESLint | ผ่าน |
| .NET build | ผ่าน ไม่มี warning/error |
| .NET format / git diff --check | ผ่าน |
| Backend regression ทั้งชุด | ผ่าน 1,303/1,303; exit 0; ใช้เวลา 33.6524 นาที |

คำสั่งหลัก:

```sh
dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceRecordingSchemaTests
dotnet test backend/TPR10.sln --logger 'console;verbosity=normal'
npm test
npm run lint
npm run build
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
git diff --check
```

ผลละเอียดอยู่ในพื้นที่ทำงานเฉพาะแผน `.superpowers/sdd/2026-09-26-module-6c-online-attendance/` ซึ่งไม่ส่งเข้า Git และใช้เฉพาะข้อมูลสังเคราะห์

ฐานสาขา `8a965af` และตรวจ fingerprint ของไฟล์ backend หลังรันซ้ำแล้วตรงกับระหว่าง verification: `099fe63d51a6e5ca026cf3c7f884e9838eacab236576e0a0d71dd01605b65954` ไม่มีการเปลี่ยน backend ระหว่างรอผล

## การตรวจโค้ดและขอบเขตที่ยังไม่ทำ

ตรวจทานโดยผู้เขียนในรอบ Task 1 ตามวิธี Native; การตรวจอิสระทั้งสาขายังเป็น Task 9 ไม่อ้างว่าได้ตรวจอิสระแล้ว

ไม่มีการเปิด endpoint ลงเวลา ไม่มีการเพิ่มหน้ากล้อง/GPS ไม่มีการ migrate ฐาน Preview และไม่มีการ push/merge ในรอบนี้

- Task 2 จะเชื่อม EvidenceBinding → Event → Day และเปลี่ยน read gate ให้ใช้ snapshot ของวันทำงาน พร้อม publication integrity ที่ครบทั้ง event/ภาพ
- Task 3–4 จะตรวจสิทธิ์/challenge expiry/lease/ผล operation และ commit เวลา+รูป+audit จริง ข้อบังคับ schema อย่างเดียวไม่ใช่การรับรองธุรกรรมนี้
- Task 5–8 เป็นประวัติ API และหน้าจอ; Task 9 รวม regression/mobile/การตรวจอิสระ
- คำร้องแก้เวลาเป็น 6D; Task 1 ไม่มีทางให้พนักงาน/ผู้ดูแลแก้เวลาโดยตรง
- NAS จริงและ Production gate ของ 6B ยังรอตรวจ ไม่ถือว่าการมี schema ทำให้ผ่าน gate เหล่านี้

การปรับจากรายชื่อไฟล์ในแผน: ไม่แก้ `AttendanceRegistration.cs` เพราะ Task นี้มีเฉพาะ model/DTO/static lock ไม่มี service ใหม่ที่ต้องลงทะเบียน; หาก Task ถัดไปเพิ่ม service จึงเพิ่ม registration พร้อม consumer

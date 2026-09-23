# รายงาน Task 5 — MFA และการกู้คืนการยืนยันตัวตน

## ขอบเขต

พัฒนาเฉพาะ Task 5 จากฐาน `065d5ae` บน `codex/module-2-identity` ตามแผนที่อนุมัติ ยังไม่รวม Tasks 6–9 ไม่ merge/push และยังไม่ใช่การรับรอง production สถานะรายงาน: กำลังตรวจ regression และรอ Code Review

## สิ่งที่เพิ่ม

- API enroll/confirm/challenge/recover ใต้ `/api/v1/auth/mfa` ต้องมี session และ CSRF ตาม origin ที่อนุญาต
- Secret เข้ารหัสด้วย Data Protection purpose แยกผูก user/factor และต้องมี protected persistent key ring ก่อน enroll
- TOTP 6 หลัก/30 วินาที ยอมรับ ±1 step; conditional update ของ last-used-step ป้องกัน replay ภายใน transaction
- Confirmation และ challenge หมุน session/CSRF binding; assurance 15 นาทีแล้วกลับสู่ stage challenge โดยไม่ต่อ absolute expiry
- Recovery codes 10 ชุด สุ่มชุดละ 128-bit แสดงครั้งเดียว เก็บ SHA-256; conditional consume พร้อม revoke factor/codes/session เดิมและเพิ่ม security version จากนั้นออก restricted enrollment session
- Restricted-stage allowlist ใช้ endpoint metadata ก่อนต่อ idle; ไม่มี permission หรือ MFA boolean จาก client ที่ยกระดับสิทธิ์ได้
- Operator-assisted recovery use case ตรวจ `users:recover-mfa`, MFA ล่าสุดไม่เกิน 15 นาที, account/session ปัจจุบัน, ห้ามทำให้ตนเอง และบังคับเหตุผล/เลขอ้างอิงหลักฐาน; ยังไม่ map admin HTTP จน Task 6
- เพิ่ม migration `AddMfaEnrollmentBinding` และ `AddMfaAttemptStates` ไม่แก้ migration เดิม

## Dependency

Pin [Otp.NET 1.4.1](https://www.nuget.org/packages/Otp.NET/1.4.1) พร้อม lock files ตรวจ LICENSE.txt ใน package เป็น MIT และ repository commit `83b39e1f2a5bfc87b6d1af15b91bffc6f1638429` NuGet audit ของ solution ไม่พบ vulnerable packages จากแหล่งที่ใช้ ณ 23 กันยายน 2026

[เอกสารผู้พัฒนา Otp.NET](https://github.com/kspearrin/Otp.NET) ระบุว่า library ตรวจรหัสได้ แต่ผู้ใช้ library ต้องจัดการการใช้ timestep ซ้ำเอง จึงใช้เงื่อนไขฐานข้อมูล ไม่ถือการเรียก VerifyTotp เพียงอย่างเดียวเป็นการป้องกัน replay

## หลักฐาน TDD

- Baseline backend179 และ Node23 ผ่านก่อนเริ่ม
- MFA routes 5 กรณี RED404 → GREEN; stage sample เดิมใช้ string enum จึงปรับ test deserializer ไม่เปลี่ยน production behavior; MFA tests ชุดแรกผ่าน6
- Challenge/recovery/stage/expiry เป็น RED จาก NotImplemented/พฤติกรรมขาด แล้วผ่าน; technical probe ทดสอบ route จริงเห็น RED201 ก่อนเพิ่ม restricted-stage guard เป็น403
- Optional Staff confirmation พบเปิด factor ก่อน Rotate ทำให้ session เดิมไม่ผ่าน Validate; ย้าย rotation ก่อน activation ภายใน transaction เดียว แล้วผ่าน
- MFA account lockout แยกจาก IP limiter: test เดินเวลา 1 นาทีหลังผิด5ครั้ง เพื่อยืนยัน IP budget ใหม่ไม่ล้าง account lock15นาที ไม่ลด guard ใน production
- Pending enrollment ที่หมดอายุ RED409 → เริ่มใหม่ได้หลัง login ใหม่ โดย revoke pending เก่า
- Core MFA25 tests ผ่านก่อนเพิ่ม operator: concurrent code/recovery, drift, expiry, stage, persistent restart/key loss, CSRF และ audit failure rollback จริง
- Operator7 tests RED → GREEN ครอบคลุม permission, recent MFA, no-self และ evidence; เพิ่ม lifecycle/rollback ของ target จริงและ concurrency confirmation/±1-step ใน regression

## ข้อตัดสินใจและผลหากผิด

1. ทำ Task 5 ใน worktree เดิมและไม่ redo Tasks 1–4; review diff Task5 แต่รัน regression ทั้งหมด หากผิดอาจพลาด interaction จึงต้องเก็บ worktree/ledger จนจบแผน
2. Stage test ของแผนตรวจพฤติกรรมที่มีแล้ว จึงเพิ่ม HTTP enrollment/confirmation เพื่อพิสูจน์ RED ใหม่ หากผิดจะเป็น tests ที่ไม่ทดสอบ feature ใหม่
3. ห้าม persist MFA ด้วย ephemeral key ring แม้ development; login ยังทำได้แต่ enroll503 จนตั้ง keys หากผิดผู้ใช้จะเข้าไม่ได้หลัง restart
4. Flow enroll/restricted challenge อายุ10นาที, MFA assurance15นาที, account MFA lock5/15/15; เป็นค่าพัฒนาที่ต้องให้ Security owner อนุมัติ หากผิดต้องปรับนโยบายก่อน rollout
5. Operator recovery เป็น trusted internal use case ที่ยังไม่ map HTTP; Task6ต้อง bind actorSessionId จาก request และตรวจ policy ไม่รับ actor ID จาก JSON หากผิดเสี่ยงกู้บัญชีโดยไม่มีสิทธิ์
6. ใช้ advisory7241002 ร่วม account mutations แล้ว lock user/session ตามลำดับ; transaction ครอบคลุม proof consumption, revoke, rotation และ audit หากผิดอาจ replay หรือเกิด partial commit ต้อง benchmark global lock ก่อนรับโหลดจริง
7. เพิ่ม nullable enrollment-session binding และตาราง attempt แยก password; ไม่ใช้ password failure counter ร่วมกัน หากผิด MFA brute force อาจล้าง/ชน password lockout
8. Recovery ไม่ต่อ absolute login expiry และไม่ให้ MFA assurance; ต้องตั้ง factor ใหม่ก่อน privileged action หากผิดเสี่ยงใช้ recovery ข้าม MFA
9. Catalog เพิ่ม capability เฉพาะ MFA ใหม่สำหรับ bootstrap ใหม่; ไม่มีการเปลี่ยน role grants ของฐานข้อมูลเดิมอัตโนมัติ Task6ต้องจัดการ catalog/grants ที่มีอยู่ก่อนเปิด operator API มิฉะนั้น operator เดิมอาจยังไม่มีสิทธิ์ใหม่
10. ไม่เปิด self-service reset/หน้าเว็บ/production admin route ในรอบนี้; password resetอยู่Task7 หน้าเว็บTask8 และ authorizationเต็มTask6 หากผิดจะอ้าง readiness เกินจริง

## ผลตรวจสุดท้ายและ Review

ก่อน review: backend215/215 และ Node23/23 ผ่าน; Next lint/build และ .NET build/format ผ่าน (0 warnings/errors), git diff --check ผ่าน รอผล HTTPS และผู้ตรวจอิสระก่อนส่งมอบ

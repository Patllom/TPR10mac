# ผลตรวจ Task 7: รีเซ็ตรหัสผ่านและบังคับเปลี่ยนรหัสผ่าน

สถานะ: Test/Build/Lint และ HTTPS ผ่าน อยู่ระหว่าง Code Review ยังไม่ส่งมอบ และยังไม่ push

## ขอบเขต

ทำเฉพาะ Task 7 บน branch `codex/module-2-identity` จาก `ecbfeb7` ไม่แก้ main ไม่เริ่ม UI ของ Task 8 และไม่อ้างผ่าน Security Exit Gate ของ Module 2

เพิ่ม API ขอรีเซ็ต/ใช้ token/เปลี่ยนรหัสผ่าน และ admin reset ที่ต้องมี `users:manage` พร้อม MFA ปัจจุบัน ทุก mutation อยู่ภายใต้ CSRF/Origin และ response เป็น no-store

Token สุ่ม 32 bytes อายุ 15 นาที เก็บ SHA-256 ใน request table; outbox เก็บ payload ที่เข้ารหัสด้วย Data Protection และ purpose ผูก request ID การเปลี่ยน credential, เพิกถอน session/token และ audit อยู่ transaction เดียวกัน ไม่มี auto-login

Admin reset คืนรหัสชั่วคราวให้ผู้ดูแลเฉพาะ response ที่ได้รับอนุญาต อายุ 15 นาที ใช้ login สำเร็จได้ครั้งเดียว หลังจากนั้นใช้ได้เพียงยืนยัน current password ภายใน session ที่ถูกบังคับเปลี่ยนรหัส เมื่อเปลี่ยนเสร็จต้อง login ใหม่และทำ MFA ตาม role/factor เดิม ไม่ล้าง MFA โดยการ reset password

## หลักฐาน TDD

- Baseline backend 259/259 ผ่านก่อนเปลี่ยนโค้ด
- Core reset 10 tests แดงเนื่องจากยังไม่มี endpoint; forced-change แก้ path fixture แล้วแดงที่ endpoint ที่ยังไม่มี ก่อนทำให้ผ่านรวม 11 ข้อ
- Admin reset 4 tests แดงก่อนสร้าง endpoint/temporary credential lifecycle แล้วผ่าน
- Dispatcher retry แดงจากโครงยังไม่มี implementation; configuration gate และ DI sink อีก 3 ข้อแดงก่อนทำให้ผ่าน
- ชุด core/admin/delivery/safety รอบแรก 26/26 ผ่าน; เพิ่ม audit rollback/CSRF/expired delivery แล้วผ่าน 38/38
- Tests เพิ่มเติมที่ตรวจระบบป้องกันเดิมเป็น regression coverage ไม่อ้างว่าทุกข้อเป็นวงจร RED–GREEN ใหม่

## ข้อตัดสินใจและข้อจำกัด

1. ใช้ worktree เดิมและเก็บ ledger ไว้ต่อ Tasks 8–9 ตรวจเฉพาะ Task 7 และจุดเชื่อมกับงานก่อนหน้า พร้อม full regression; หากผิดอาจตกหล่น interaction นอกชุดทดสอบ
2. Email reset เปิดเฉพาะ Testing/Development ที่มี persistent encrypted key ring; production ปิด และ config ที่พยายามเปิดถูกปฏิเสธตอน startup จนกว่าจะพัฒนา adapter Module 5 หากเข้าใจผิดผู้ใช้จะรออีเมลที่ยังไม่ได้ส่ง
3. Logged-in account ใช้ reset token ของอีกบัญชีไม่ได้ ส่วน anonymous ให้ token เป็นตัวระบุบัญชี ไม่รับ userId จาก client หากผิดผู้ใช้ต้อง logout ก่อนกู้บัญชีอื่น
4. คง global identity transaction lock ร่วมกับ account/MFA และล็อก user ก่อน session/request เพื่อป้องกันการใช้ข้อมูลเก่า หากผิดจะกระทบ throughput ต้องตรวจ load/distributed deployment ก่อน production
5. จำกัดคำขอ reset ที่ยังใช้ได้หนึ่งรายการต่อบัญชี คำขอซ้ำไม่สร้างหรือทำลาย token เดิม หากผิดผู้ใช้ต้องรอหมดอายุหรือดำเนินการคำขอเดิม
6. Admin temporary credential แสดงครั้งเดียวใน authorized no-store response ไม่เก็บ plaintext ไม่ส่งอีเมล ผู้ดูแลต้องส่งต่อผ่านช่องทางยืนยันตัวบุคคลที่ได้รับอนุมัติ หาก response หายหรือล็อกอินแล้ว session หาย ต้องให้ผู้ดูแลออกใหม่ ไม่ replay คำสั่งแบบ idempotent
7. เพิ่ม migration ใหม่ `AddTemporaryCredentialLifecycle` ไม่แก้ migration เก่า ค่า null ทำให้ credential เดิมไม่กลายเป็น temporary; อย่า downgrade หลังเริ่มใช้ temporary credential เพราะจะสูญเสียหลักฐาน consume/expiry
8. ตัวรับข้อความพัฒนาเป็น DI-only inbox ในหน่วยความจำ จำกัด 1,000 รายการ และล้างรายการเก่า 15 นาทีเมื่อใช้งาน ไม่ใช่ public token endpoint ไม่มี background worker; dispatcher ใช้ request ID เดิม retry ได้สูงสุด 5 ครั้ง ห่าง 1 นาที หากผิดอาจส่งไม่สำเร็จ ต้องมี worker/retention/monitoring ของ Module 5
9. Network send กับ DB commit ไม่ใช่ exactly-once transaction; adapter จริงต้อง deduplicate ด้วย request ID และห้ามบันทึก token จาก exception/log การหมดอายุ/เพิกถอนยังตรวจที่ API เสมอ หากผิดอาจส่งข้อความซ้ำ แต่ไม่ควรใช้ token ซ้ำได้
10. คง lockout เดิม ไม่ปลด lockout ด้วยการเปลี่ยนรหัสผ่าน ผู้ใช้ที่ยังติด lockout อาจต้องรอให้ครบเวลา หากผิดจะกระทบขั้นตอนกู้บัญชีจริง ต้องให้ security owner อนุมัตินโยบายก่อน deploy

## งานที่ยังอยู่นอกการส่งมอบ

หน้าเว็บและ browser flow อยู่ Task 8; OpenAPI/acceptance/security-owner approval อยู่ Task 9; Gmail delivery/worker และข้อมูล retention อยู่ Module 5; scope assignment อยู่ Module 3 ข้อจำกัดและ Minor ของ Tasks 1–6 ยังเป็นไปตามรายงานเดิม

## Verification และ Code Review

ผลรัน 2026-09-23 บน worktree ของ Task 7:

| คำสั่ง | ผล |
| --- | --- |
| `dotnet test backend/TPR10.sln --verbosity minimal` | 301/301 ผ่าน ไม่ skip; 3 นาที 46 วินาที |
| `dotnet test backend/TPR10.sln --filter FullyQualifiedName~PasswordResetRaceTests --verbosity minimal` | 4/4 ผ่าน |
| `dotnet test backend/TPR10.sln --filter FullyQualifiedName~Admin_reset_revokes_immediately --verbosity minimal` | 1/1 ผ่าน รวม actor/role/target และไม่เก็บรหัสใน audit metadata |
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | ผ่าน 0 warning/error |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน |
| `npm test` | 23/23 ผ่าน |
| `npm run lint` และ `npm run build` | ผ่าน |
| `node infra/nginx/smoke-identity-https.mjs 4001` และ `4000` | ผ่านทั้งคู่ ไม่เปลี่ยน trust store |
| `git diff --check` | ผ่าน |

Full regression รอบก่อนหน้า 296/297 ล้มที่ test เดิมนับ account routes 3 เส้นทาง ตอนนี้เพิ่ม admin reset เป็น 4 เส้นทางแล้ว ปรับ expectation พร้อม DI และตรวจ anonymous 401 ของเส้นทางใหม่ทั้ง Testing/Production ไม่ลดการตรวจ policy ผล full รอบใหม่เป็น 301/301 ตามตาราง

HTTPS smoke ตรวจ transport ด้วย authorized fixture ในฐานข้อมูลแยก ไม่ใช่ browser login/TOTP end-to-end; flow จริงพิสูจน์ด้วย API integration tests หน้าเว็บอยู่ Task 8

11. ปรับ route-count contract 3→4 เพื่อครอบคลุม admin reset โดยยังตรวจ named policy ทุกเส้นทางและ anonymous rejection หากผิดอาจปล่อย route ไม่มี policy จึงใช้ทั้ง metadata และ HTTP test

ผลผู้ตรวจอิสระ: รอผล ยังไม่อ้างว่าจบ review

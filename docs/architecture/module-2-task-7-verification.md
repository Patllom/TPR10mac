# ผลตรวจ Task 7: รีเซ็ตรหัสผ่านและบังคับเปลี่ยนรหัสผ่าน

สถานะ: implementation, review อิสระ, การแก้ Important ด้วย TDD และ Test/Build/Lint หลังแก้ผ่านครบ มี Minor 1 ข้อเลื่อนไว้ ยังไม่ push และไม่ใช่การรับรอง production readiness

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

ผลรันก่อนแก้ review เมื่อ 2026-09-23 บน worktree ของ Task 7 (ผลหลังแก้อยู่หัวข้อถัดไป):

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

## ผล Code Review และการแก้

Carver ตรวจแบบ read-only ช่วง `ecbfeb7..097a8ef`: Critical 0, Important 1, Minor 1 ผู้ตรวจไม่ได้รัน suite ซ้ำ หลักฐาน Test/Build/Lint เป็นของผู้ทำหลัก ผู้ตรวจปิดแล้ว ไม่มี review รอบสอง

Important: `password/change` เดิมตรวจ current password โดยไม่มี account-bound budget จึงไม่ติด lockout ร่วมกับ login แก้ในหนึ่ง fix pass: ใช้ `login_attempt_windows` เดียวกันตาม normalized username, ผิด 5 ครั้งใน 15 นาทีพัก 15 นาที, ตรวจ lockout ก่อน hash, บันทึก `identity.password.change.failed`/`throttled` พร้อม outcome denied และ commit counter+audit เมื่อปฏิเสธ

`PasswordResetThrottleTests` 4 ข้อแดงก่อนแก้และผ่านหลังแก้ ครอบคลุมข้าม session, concurrent failures, change ทำให้ login ถูกพัก, login lockout ปิด change และสร้าง bucket กลับเมื่อถูก cleanup แล้ว Full regression หลังแก้ผ่าน 305/305 ไม่ skip

12. ลำดับล็อก password change ใหม่คือ admission lock สั้นแยก transaction → attempt bucket → identity lock → user → session เพื่อให้สอดคล้อง login ที่ล็อก bucket ก่อน user; bucket จำกัด 10,000 รายการ อายุ 30 นาที ใช้กติกาเดิม ไม่สร้าง counter แยกที่สลับ endpoint เพื่อเลี่ยงได้ หากผิดเสี่ยง deadlock/lockout bypass จึงตรวจ concurrency และ full regression

Minor ที่เลื่อน: enumeration test เปรียบเทียบ known/unknown ยังใช้บัญชีไม่มี email และไม่มี persistent keys จึงไม่ครอบคลุมการเทียบข้อความของ eligible-account branch แม้มี test ออก request/outbox ที่เข้าเกณฑ์แยกอยู่แล้ว คง severity Minor เพราะ production code ใช้ข้อความตอบเดียวกัน แต่ควรเพิ่ม coverage นี้ในงานทดสอบถัดไป

### ขอบเขตที่ผู้ตรวจเว้นและคำตัดสินของผู้ทำหลัก

- Task 8: Next cache/return URL/browser flow ไม่มีใน diff ให้ตรวจใน Task 8 ไม่อ้างพร้อม หากผิดเสี่ยง session รั่วหรือ redirect ไม่ปลอดภัย
- Task 9: OpenAPI/Security Exit Gate ยังต้องทำตามแผนและให้ security owner อนุมัติ หากผิดผู้ใช้ API เข้าใจ contract/readiness ผิด
- Module 5: Gmail/worker/monitoring/retention ยังไม่ทำ จึงคง production email disabled หากผิดผู้ใช้จะรออีเมลไม่ได้
- Exactly-once/network adapter จริงยังไม่รับรอง ต้อง deduplicate request ID หากผิดอาจส่งข้อความซ้ำ
- Load/distributed/global-lock capacity ยังไม่พิสูจน์ ต้องทดสอบก่อน production หากผิดกระทบ availability
- Downgrade หลังใช้ temporary credential ยังไม่รับรอง คงคำเตือนห้ามใน runbook หากผิดอาจคืนความสามารถใช้ credential ซ้ำ
- นโยบายคง lockout, response/session หายแล้วออกใหม่ และหนึ่ง active reset ต่อบัญชีคงตาม ruling พัฒนา ต้องอนุมัติก่อน deploy หากผิดกระทบการกู้บัญชีและ UX
- Tasks 1–6 ทั้งชุด/Minor เดิม/direct SQL/Module 3 scope ไม่ใช่ขอบเขต review นี้ ใช้ full regression และตรวจจุดเชื่อม ไม่อ้างรับรองส่วนที่ยังไม่ทำ หากผิดอาจพลาดปัญหานอก diff

เอกสารและ fix หลัง review ตรวจโดยผู้ทำหลัก ไม่อ้างว่าได้รับ independent re-review

## Verification หลังแก้ Review

| คำสั่ง | ผลเมื่อ 2026-09-23 |
| --- | --- |
| `dotnet test backend/TPR10.sln --verbosity minimal` | 305/305 ผ่าน ไม่ skip; 11 นาที 51 วินาที |
| `dotnet build backend/TPR10.sln --no-restore --verbosity minimal` | ผ่าน 0 warning/error |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | ผ่าน |
| `npm test` | 23/23 ผ่าน |
| `npm run lint` | ผ่าน ไม่มี warning/error |
| `npm run build` | ผ่าน |
| `node infra/nginx/smoke-identity-https.mjs 4001` และ `4000` | ผ่านทั้งคู่หลังแก้ |
| `git diff --check` | ผ่าน |

หลักฐานอยู่ใน ledger และ logs ของแผน (`task7-post-review-full.log`, `task7-post-review-web.log`) รอบ full หลังแก้ใช้เวลานานกว่าก่อนแก้ แต่ผ่านทั้งหมด ยังไม่สรุปสาเหตุด้านประสิทธิภาพจากระยะเวลาเพียงครั้งเดียว

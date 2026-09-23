# รายงานตรวจสอบ Module 2 — Task 4: บัญชีผู้ใช้และผู้ดูแลเริ่มต้น

## ขอบเขตและสถานะ

ส่งมอบเฉพาะ Task 4 บน branch `codex/module-2-identity` ฐาน `ca4b858` ผ่าน TDD, ผู้ตรวจอิสระ, การแก้ Important และ Test/Build/Lint หลังแก้ ไม่เริ่ม Tasks 5–9 และไม่ merge/push ยังมี Minor ที่เลื่อนตามรายการด้านล่าง

## หลักฐาน TDD และพฤติกรรม

- Bootstrap: สอง tests แดงก่อนมี CLI แล้วผ่านพร้อม registration404; ใช้ PTY และสอง process จริง ตรวจไม่ echo รหัสผ่าน สร้างผู้ดูแลได้ครั้งเดียว ไม่ overwrite และ Login ต้องตั้ง MFA
- Create/Update: 10 tests แดงก่อน implementation แล้วผ่าน ตรวจ must-change-password, normalized duplicate409, validation400, permission403, disable/version/revoke และ last-active-admin409
- Pagination/role guard: tests แดงก่อนเพิ่ม bounded list และ `roles:manage`; permission `users:manage` อย่างเดียวไม่ให้ระบุ role เพื่อยกระดับตนเอง
- Adapter: 2 tests แดงด้วย NotImplementedException แล้วผ่าน; actor จาก principal, default25, no-store, named policy metadata และยืนยันไม่เปิด route ใน Testing/Production
- Rollback/concurrency: 7 tests ผ่าน ตรวจ audit failure ด้วย PostgreSQL trigger จริงสำหรับ bootstrap/create/update, สอง admin ปิด/ถอดตนเองพร้อมกัน, normalized create แข่งขัน และ role change ยกเลิก session จริง
- Bootstrap validation: control character ทำให้ test แดง (คาดปฏิเสธแต่สร้างสำเร็จ) ก่อนเพิ่ม guard; ตรวจ input ไม่ถูกต้องไม่เหลือ partial seed และ CLI ปฏิเสธ redirected stdin

## ข้อจำกัดของหลักฐาน

รันทดสอบบัญชีรอบหนึ่งล้มเหลวทุกกรณีต่อเนื่องช่วงละประมาณ 15 วินาทีด้วย output แบบ quiet ซึ่งไม่มี stack trace; PostgreSQL container แสดง ready การทดสอบแยกและรันซ้ำแบบ minimal ผ่านโดยไม่แก้ production code จึงยังไม่ยืนยันสาเหตุและไม่อ้างว่าแก้บั๊กนี้แล้ว การตรวจสุดท้ายใช้ minimal เพื่อเก็บรายละเอียดหากเกิดซ้ำ

API จัดการบัญชียังไม่ map ทุก environment จน Task 6; tests use case ไม่ใช่ HTTP authorization/MFA acceptance ยังไม่รับรอง production readiness และคงรายการค้างของ Tasks 1–3

## ข้อตัดสินใจระหว่างทำ

1. ใช้ worktree เดิมของแผน ทำเฉพาะ Task 4 และไม่ลบ ledger เมื่อแผนยังไม่จบ; หากผิดอาจสูญเสียบริบท Tasks 5–9
2. ไม่เปิด admin HTTP แม้ Testing จน permission+MFA พร้อม; หากผิดจะต้องปรับ integration Task 6 ไม่ใช่ลด guard
3. Bootstrap รับ interactive TTY เท่านั้น ไม่มี password ผ่าน args/env/pipe และทดสอบกับฐานข้อมูลแยก; หากต้องใช้ automation ต้องออกแบบ secure input contract ต่างหาก
4. Account use case เป็นเจ้าของ transaction/audit/Save และใช้ lock ร่วม bootstrap; RevokeUser เพิ่ม version อยู่แล้วจึงไม่เพิ่มซ้ำ; หากผิดเสี่ยง race หรือ partial commit
5. Seed เฉพาะ 5 role classes และ named permissions ที่โมดูลนี้เป็นเจ้าของ ไม่ seed demo/business data; หากผิดต้องขยาย catalog ในโมดูลที่เกี่ยวข้อง
6. การระบุ roles ต้องมี `roles:manage` เพิ่มจาก `users:manage`; หากผิดอาจมีช่องยกระดับสิทธิ์ผ่าน provisioning
7. เพิ่ม optional actorId ให้ audit writer โดยคง caller เดิม; metadata ระบุ actor roles/target/outcome/scope ส่วน schema เต็มอยู่ Task 6; หากผิด auditor อาจตีความ lifecycle ไม่ครบ
8. no-store ใน adapter ยังไม่พิสูจน์ denial/exception pipeline เพราะ route ปิดอยู่; Task 6 ต้องทดสอบก่อน map มิฉะนั้น response ผิดพลาดอาจถูก cache

## ผลตรวจสุดท้ายและ Code Review

ก่อน review: backend tests ผ่าน 177/177, Node tests ผ่าน 23/23, .NET build ไม่มี warning/error, format verify ผ่าน, Next lint/build ผ่าน และ git diff --check ผ่าน (23 กันยายน 2026)

HTTPS smoke ผ่านทั้ง Next 4000 และ 4001: CA ที่เชื่อถือ/ไม่เชื่อถือ, cookie flags, no-store, CSRF403/201 และ hostile Host; ใช้ CA ชั่วคราว ไม่เปลี่ยน system trust และไม่เปิด API สู่เครือข่ายสาธารณะ

ผลตรวจหลังแก้ Important (23 กันยายน 2026):

- `dotnet test backend/TPR10.sln --verbosity minimal`: 179/179 ผ่าน ไม่มี skipped
- `dotnet build backend/TPR10.sln --no-restore --verbosity minimal`: ผ่าน 0 warnings / 0 errors
- `dotnet format backend/TPR10.sln --verify-no-changes --no-restore`: ผ่าน
- `npm test`: 23/23 ผ่าน; `npm run lint` และ `npm run build`: ผ่าน
- `node infra/nginx/smoke-identity-https.mjs 4001` และ `4000`: ผ่านซ้ำหลังแก้
- `git diff --check`: ผ่าน ไม่มีการ push/merge และไม่เปลี่ยน main

ผู้ตรวจอิสระ Schrodinger ตรวจ `ca4b858..ede7282` แบบ read-only พบ Critical 0, Important 1, Minor 1 และรัน Account tests จาก binary เดิมผ่าน 35/35 ไม่ใช่การ rebuild อิสระ

Important: การรับรหัสผ่านเพียงครั้งเดียวเสี่ยงพิมพ์ผิดจนผู้ดูแลคนแรกเข้าใช้ไม่ได้ จึงเพิ่ม confirmation แบบซ่อนก่อนเริ่ม transaction; PTY tests mismatch/cancel แดงทั้งสองกรณี (คาด exit1 แต่ได้ exit0) ก่อนแก้ จากนั้น AccountTests ผ่าน 10/10 รวมตรวจไม่ echo และไม่เหลือ partial seed ไม่ส่ง review รอบสองตามกระบวนการ

### Minor ที่เลื่อน

Audit `changed-fields` ของการเปลี่ยนเฉพาะ roles ยังระบุ `active,roles` แม้ active คงเดิม; รายการ actor/target/transaction ถูกต้อง แต่ผู้ตรวจต้องไม่ตีความ field นี้ว่ามีการเปลี่ยน active เสมอ เก็บปรับความแม่นยำแยกต่างหากตาม fix-pass policy ไม่ซ่อนข้อสังเกตนี้

ข้อเสนอเพิ่ม tests role-change rollback และ re-enable/old-session เป็นการเสริมหลักฐาน ไม่ใช่บั๊กที่พิสูจน์แล้ว เก็บพิจารณาใน Task 6 ไม่อ้างว่าทดสอบทั้งสองกรณีแล้ว

### การตัดสินรายการที่ผู้ตรวจแยกขอบเขต

1. HTTP permission/MFA/restricted-stage/freshness และ no-store denial/exception ต้องผ่าน Task 6 ก่อน map; คง route ปิด มิฉะนั้นเสี่ยงข้ามสิทธิ์/cache
2. Enrollment/challenge/recovery/password change/reset อยู่ Tasks 5/7; Task 4 ตรวจเฉพาะ stage ที่เชื่อมต่อ มิฉะนั้นอาจเข้าใจผิดว่ากู้บัญชีได้แล้ว
3. Browser/UI และ production readiness ยังไม่รับรอง; Task 8 และ production gate ต้องตรวจต่างหาก มิฉะนั้นอาจ deploy ก่อนพร้อม
4. Tasks 1–3 ไม่ review ใหม่ทั้งชุด แต่รัน regression รวมและตรวจ interaction; หากผิดอาจพลาดข้อบกพร่องเก่าที่อยู่นอก diff
5. HTTPS4000/4001 ผู้ทำหลักรันจริงผ่าน ไม่ใช่ผู้ตรวจซ้ำอิสระ; หากผิดจะประเมินความแข็งแรงของหลักฐานเกินจริง
6. Test/Build/Lint/TDD ผู้ทำหลักเป็นผู้รัน; ผู้ตรวจยืนยันเฉพาะ Account จาก binary เดิมและ diff ไม่อ้าง independent rebuild; หากผิดอาจปิดบังช่องว่าง verification
7. Capacity ของ global lock, OS/terminal นอก macOS/Linux PTY, process crash และ network ระหว่าง commit ยังไม่รับรอง; ต้องตรวจ operational gate ก่อน rollout มิฉะนั้นเสี่ยง throughput/ผล commit ไม่แน่นอน
8. Catalog ที่เปลี่ยน SQL นอกระบบไม่อยู่ใน contract; Task 6 role-definition mutations ต้องรักษา invariant/lock/revocation ร่วม มิฉะนั้น last-admin และสิทธิ์อาจผิด
9. Report ที่แก้ระหว่าง review อยู่นอก review HEAD; ผู้ทำหลักตรวจเอกสารตามผลจริง ไม่อ้างผู้ตรวจรับรองเนื้อหาใหม่ มิฉะนั้น provenance คลาดเคลื่อน
10. ผู้ตรวจไม่เขียน Second Brain เพราะ read-only; ผู้ทำหลักรับผิดชอบ checkpoint และไม่ crawl คลัง มิฉะนั้นบริบทงานอาจไม่ durable

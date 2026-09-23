# รายงานตรวจสอบ Module 2 — Task 4: บัญชีผู้ใช้และผู้ดูแลเริ่มต้น

## ขอบเขตและสถานะ

ทำเฉพาะ Task 4 บน branch `codex/module-2-identity` ฐาน `ca4b858` ไม่เริ่ม Tasks 5–9 และไม่ merge/push สถานะขณะจัดทำ: implementation อยู่ระหว่างตรวจ Test/Build/Lint และรอผู้ตรวจอิสระ ยังไม่ส่งมอบ

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

ก่อน review: backend tests ผ่าน 177/177, Node tests ผ่าน 23/23, .NET build ไม่มี warning/error, format verify ผ่าน, Next lint/build ผ่าน และ git diff --check ผ่าน (23 กันยายน 2026) รอ independent review และผล HTTPS smoke ก่อนส่งมอบ

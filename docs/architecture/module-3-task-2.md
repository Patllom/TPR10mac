# Module 3 — Task 2: Scoped Roles, MFA และการถอนสิทธิ์

## ขอบเขต

งานนี้เชื่อมบทบาทที่ผูกกับ Workspace/Project/Site เข้ากับระบบยืนยันตัวตนเดิม ยังไม่เปิด Organization API, Assignment API หรือหน้าจอใหม่ ซึ่งอยู่ใน Task 3 เป็นต้นไป

## พฤติกรรมที่เพิ่ม

- ใช้ `IEffectiveRolePolicy` ร่วมกันใน login และการตรวจ session: บัญชีต้อง active และมี global privileged role หรือ scoped role ชั้น `approval`, `accounting`, `finance-data-access` ที่ assignment และบรรพบุรุษยัง active
- Scoped role ไม่ถูกรวมเป็นสิทธิ์ส่วนกลาง การแสดง permissions ใน session และการตรวจสิทธิ์จัดการเดิมยอมรับเฉพาะ domain `system`
- เมื่อได้รับ scoped privileged role ใหม่ cookie แบบ Active ที่ยังไม่ผ่าน MFA จะใช้ต่อไม่ได้ แม้ตรวจผ่าน `SessionService.ValidateAsync` โดยตรง
- การเปลี่ยนรหัสผ่านบังคับให้ login ใหม่ผ่าน policy เดียวกัน การยืนยัน MFA มีอายุ 15 นาที และ recovery ไม่ถือเป็นหลักฐาน MFA ล่าสุด
- ปิดบัญชีแล้วถอน scoped assignments พร้อมเพิ่ม version, ระบุผู้ถอน/เวลา/เหตุผล และ audit ราย assignment ก่อนถอน sessions เปิดบัญชีกลับไม่คืน assignments เดิม
- เปลี่ยน permissions ของ role แล้วถอน sessions ของผู้มี role ทั้ง global และ scoped แบบรวมรายชื่อไม่ซ้ำ เรียงลำดับ และเพิ่ม security version คนละหนึ่งครั้ง

## สัญญาของ AssignmentLifecycle สำหรับ Task ถัดไป

ผู้เรียกต้องเปิด transaction และถือ advisory lock `7241002` ก่อนแตะ user/session/assignment ส่วน helper ไม่เปิดหรือ commit transaction ไม่ save และไม่ถอน sessions เอง

- `RevokeForUserAsync`: ถอนเฉพาะ assignments ที่ยังไม่ถูกถอนของผู้ใช้ คืนจำนวนที่เปลี่ยน
- `RevokeForScopeAsync`: ถอนระดับเป้าหมายและลูกหลาน คืน user IDs ที่ได้รับผลกระทบแบบไม่ซ้ำ เรียงลำดับ ผู้เรียกนำไปถอน sessions ใน transaction เดียวกัน
- Workspace cascade ครอบคลุม project/site ภายใน; Project cascade ครอบคลุม site ภายใน; Site ไม่แตะ parent หรือ sibling การ cascade นี้ใช้เพื่อปิดพื้นที่ ไม่ใช่การสืบทอดสิทธิ์อ่านข้อมูล
- ตรวจ UUID/shape ของ scope และ actor/reason ก่อนเปลี่ยนข้อมูล การเรียกซ้ำก่อนหรือหลัง save ไม่เพิ่ม version หรือ audit ซ้ำ
- การเปลี่ยน role grants รวมทุก assignment ที่ยังไม่ถูกถอน แม้บรรพบุรุษ inactive เพื่อไม่เหลือ session เก่า ส่วน MFA ใช้เฉพาะ assignment ที่มีบรรพบุรุษ active

## หลักฐานการพัฒนา

ใช้ Superpowers แบบ inline และ TDD กับ PostgreSQL จริงในฐานข้อมูลทดสอบแยก ไม่เพิ่ม dependency หรือแก้ migration ใน Task นี้

- Baseline: identity/scope 63 รายการผ่าน
- RED รอบแรก: MFA/session/domain 5 รายการไม่ผ่านตามช่องว่างเดิม; lifecycle 2 รายการไม่ผ่านเพราะยังไม่ถอน scoped assignments/scoped sessions
- เพิ่ม RED เฉพาะ cascade contract, domain guard แต่ละชั้น, operator recovery และกรณีเรียก lifecycle ซ้ำก่อน save
- Fault injection ทำให้ audit insert ล้มเหลว ตรวจว่าบัญชี, assignments, grants, security version และ session rollback รวมทั้ง cookie จริงยังใช้ได้หลัง rollback
- ตรวจ callers: password reset/change ถอน session และกลับเข้า login; MFA confirm/challenge ผ่าน rotation; recovery ออก enrollment session โดยไม่มี MFA assurance; operator recovery ถอน sessions และตรวจ domain ซ้ำใน use case

## ผลตรวจส่งมอบ

- ชุดทดสอบใหม่ Task 2: 29/29 ผ่าน
- Frontend: Test 28/28, Lint และ production Build ผ่าน
- Browser E2E: 13/13 ผ่าน พร้อม HTTPS acceptance, cookie, CSRF และ hostile Host บนพอร์ต 4001
- Backend regression ทั้งชุด: 424/424 ผ่าน ไม่มีรายการข้าม (รวม 29 รายการใหม่)
- Backend Build: ผ่าน ไม่มี warning/error; `dotnet format --verify-no-changes --no-restore` ผ่าน
- Code Review อิสระ: ไม่พบ Critical/Important; มี Minor ด้าน test coverage 1 ข้อที่บันทึกไว้ด้านล่าง
- ผลรันจาก source ชุดเดียวกับที่ส่งมอบ; log รอบนี้อยู่ใน `/private/tmp/tpr10-m3t2-*.log` ในเครื่องพัฒนา เป็นไฟล์ชั่วคราว ไม่ใช่ artifact ถาวร

## ข้อจำกัดและงานถัดไป

- ยังไม่ push หรือ merge และไม่เปลี่ยนงานค้างเดิมบน main
- Task 3–9 ยังไม่เสร็จ จึงยังไม่ถือว่า Module 3 พร้อมเปิดใช้งานจริง
- ข้อสังเกต Minor จาก Task 1 เรื่องเทียบค่าราย field ใน migration roundtrip ยังคงอยู่ ไม่ใช่ขอบเขต Task 2
- ขั้นถัดไปเมื่อได้รับคำสั่ง: Task 3 Organization management API ใช้ lifecycle และ lock/transaction contract ข้างต้น

## ผล Code Review อิสระ

ผู้ตรวจ Godel ตรวจ diff และไฟล์ใหม่เทียบกับ Task 2 และ spec โดยไม่แก้ไฟล์ ไม่พบ Critical หรือ Important และเห็นว่าพร้อมส่งมอบเมื่อ verification ผ่านครบ

ข้อสังเกต Minor ที่เลื่อนไปเก็บเพิ่ม: test ของ `AffectedUsersAsync` ยังไม่แยกผู้ใช้ที่มีเฉพาะ scoped role ใต้ ancestor ที่ inactive จึงยังไม่มี regression test เจาะจงป้องกันการเติม active-ancestor filter ผิดจุด โค้ดปัจจุบันไม่มี filter นั้นและถอน session แบบ conservative ตามที่กำหนด

### การตัดสินใจและขอบเขตที่รับไว้

- รวมทุก non-revoked assignment ใน affected users แม้ ancestor inactive เพื่อไม่เหลือ session เก่า ผลแลกเปลี่ยนคืออาจให้ผู้ใช้ login ใหม่มากกว่าที่จำเป็น
- ป้องกันการถอนซ้ำทั้งสถานะฐานข้อมูลและ tracked state เพื่อให้ caller เรียกหลายครั้งก่อน save ได้ หากละเลยจะเพิ่ม version/audit ซ้ำ
- เพิ่ม domain check ภายใน operator MFA recovery ให้สอดคล้องกับ use case อื่น หากไม่ตรวจซ้ำจะพึ่ง HTTP guard ชั้นเดียว
- ตรวจอิสระเพิ่มเฉพาะ Task 2 ตามขอบเขตส่งมอบ ไม่แทนการตรวจทั้ง branch ใน Task 9; ไม่เริ่ม Task 3 อัตโนมัติ
- Organization/Assignment API, การห้าม self-assignment และ UI เป็น Tasks 3 เป็นต้นไป หากเปิดระบบก่อนทำครบจะยังไม่มีการบังคับขอบเขตธุรกิจครบวงจร
- Record/export concurrency และ browser cache isolation เป็นงานภายหลัง ต้องตรวจใน Tasks 5–9 ก่อนเปิดใช้งาน
- Schema/migration คงจาก Task 1 โดยตรวจความเข้ากันได้เท่านั้น รวม Minor roundtrip ที่ยังค้าง หากเปลี่ยน migration ภายหลังต้องรันการทดสอบนั้นใหม่
- Lock ownership เป็นสัญญาของ caller ไม่ตรวจ ownership ภายใน helper; caller ปัจจุบันถือ lock ถูกต้อง หาก caller ใหม่ผิดสัญญาอาจเกิด race จึงต้องใช้ลำดับเดียวกันใน Task 3/4
- ไม่รองรับการคืนบริบทหลัง rollback หรือ grant ใหม่ที่ยังไม่ save แล้วเรียก revoke ใน DbContext เดียวกัน; ยังไม่มีเส้นทางนั้นใน Task 2 ผู้เรียกใหม่ต้อง save grant ก่อนและเลิกใช้บริบทที่ rollback เพื่อไม่พลาด assignment
- ไม่ออกแบบ Module 2 ใหม่หรือรับรอง production capacity; ต้องทดสอบโหลดและการใช้งานจริงแยกต่างหาก
- ผู้ตรวจอ่านเนื้อหาทดสอบแต่ไม่รัน Test/Build/Lint/E2E ซ้ำ ผลรันที่รายงานมาจากผู้พัฒนาในรอบนี้เท่านั้น ไม่ใช่การรับรอง runtime สองชุดอิสระ

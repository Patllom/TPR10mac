# Module 3 — Task 6: อ่านและเขียนข้อมูลตามพื้นที่

## ขอบเขต

เพิ่ม API ข้อมูลทดสอบ `scope-probe-records` สำหรับ Workspace, Project และ Site โดยแยกสิทธิ์แต่ละระดับอย่างเคร่งครัด ไม่ถือว่าสิทธิ์ของพื้นที่แม่ครอบคลุมพื้นที่ลูก และไม่มีทางลัดให้ผู้ดูแลระบบที่ไม่มี assignment

มี 12 operations: list, detail, create และ update อย่างละ 3 ระดับ เปิดเฉพาะ Development/Testing เท่านั้น ไม่เปิดใน Production และไม่ปรากฏใน OpenAPI ของ Production

ยังไม่รวม export-simulation (Task 7), หน้าจอเลือกพื้นที่และข้อมูล (Task 8) หรือการตรวจรวมทั้ง Module (Task 9)

## พฤติกรรมสำคัญ

- Repository บังคับ `(workspaceId, projectId, siteId)` ตรงกันทุกช่อง รวมค่า null ก่อนค้นหา นับจำนวน หรือแบ่งหน้า ไม่มี public query ที่ค้นด้วย record ID โดยไม่ตรวจพื้นที่
- Server กำหนด actor และ scope จาก session/route ที่ตรวจสิทธิ์แล้ว ไม่รับค่าปลอมจาก request body
- DTO ของ POST/PATCH ปฏิเสธ field ที่ไม่รู้จัก รวม `actorId` และ `workspaceId`
- ผู้มีสิทธิ์อ่านทั่วไปไม่เห็น property `restrictedNote` แม้ค่าที่จัดเก็บเป็น null
- อ่าน restricted field ต้องมี `scope-probe:restricted-read` ในพื้นที่เดียวกันและ recent MFA นอกเหนือจากสิทธิ์อ่าน
- การส่ง `restrictedNote` ใน POST/PATCH รวม null ต้องมี write, restricted-read และ recent MFA; PATCH ไม่ส่ง field จะคงค่าเดิม, null ล้างค่า, string แทนค่า และชนิดอื่นถูกปฏิเสธ
- ผู้มี write แต่ไม่มี read ได้เฉพาะ `{id, version}` พร้อม Location ไม่ได้อ่านข้อความกลับผ่าน mutation response
- Note ยาวได้ไม่เกิน 500 และไม่มี control characters; ข้อความว่างใช้ได้ตาม schema ส่วน null ใช้ไม่ได้
- List เริ่ม page 1, pageSize 25, สูงสุด 100 เรียงตามเวลาและ ID; count อยู่ในพื้นที่เดียวกัน และปฏิเสธ offset overflow
- PATCH ต้องส่ง expectedVersion; ค่าเก่าตอบ 409 ไม่เปลี่ยนข้อมูล ส่วนคำขอที่ไม่มี effective change คง version/เวลาเดิมและยังมี audit
- UUID ผิดรูปแบบและ binding errors ผ่าน denial audit; ปัญหาที่ service คืนมี correlation ID และ response ใช้ no-store

## Transaction และการแข่งขันระหว่างคำขอ

API ใช้ ScopeOperation จาก Task 5: เปิด transaction และรับ advisory lock `7241002` ก่อนอ่าน session/assignment/permission ใหม่ แล้ว materialize DTO และบันทึก audit ก่อน commit จึงส่ง response ได้

Audit ของ list/detail/create/update ระบุ actor, acting role, exact scope, target และจำนวนแถว ไม่เก็บเนื้อหา Note หรือ restricted field; audit ล้มเหลวตอบ 503 แบบไม่เปิดเผยรายละเอียดฐานข้อมูลและ rollback การเขียน/version

การทดสอบ race ใช้ DbCommandInterceptor เฉพาะ test factory และ TaskCompletionSource หยุด HTTP operation ก่อน/หลัง lock จริง ใช้ HTTP revoke assignment อีกคำขอหนึ่ง ไม่สร้าง test hook ใน production และไม่ใช้ Sleep เป็นหลักฐานลำดับ มี timeout 10 วินาทีเพื่อป้องกัน test ค้างเท่านั้น

เมื่อ revoke commit ก่อน operation ต้องไม่ส่งข้อมูลหรือเขียน record; เมื่อ operation ได้ lock ก่อน สามารถสำเร็จก่อน revoke และคำขอถัดไปถูกปฏิเสธ 401

## OpenAPI

ประกาศ exact-business scope และ business capability แยกจาก global permission policy ใช้ response variants สำหรับ public, restricted และ write-only พร้อม Location, CSRF, cookie security และ error statuses โดยคง contract tests ของ identity เดิม

## ข้อวินิจฉัยระหว่างทำ

1. ใช้ worktree และ dependency เดิม ไม่ทำ Tasks 1–5 ซ้ำ และไม่แก้ main — หากเลือก workspace ผิดจะทับงานเดิม
2. ข้อความแผนที่ระบุ 5 methods ต่อระดับตีความเป็น 4 ใน Task 6 เพราะ export อยู่ Task 7 — การเปิด placeholder จะทำให้สัญญา API เกินงานที่ทดสอบ
3. เพิ่ม binding audit, row-count allowlist และ OpenAPI พร้อม route ไม่รอ Task 9 — มิฉะนั้นจะมีช่องว่างด้าน audit/เอกสารตั้งแต่เปิดใช้
4. Write response ตรวจ read capability ซ้ำภายใน transaction เดียวกัน — หากใช้ write แทน read จะเปิดเผยข้อมูลที่ไม่ได้รับสิทธิ์
5. Note ว่างใช้ได้ตาม schema/spec; ไม่เพิ่ม minimum นอกแผน — client ต้องรองรับข้อความว่าง
6. เอา C# optional constructor default ของ JsonElement ออกจาก CreateScopeRecord เพราะ schema exporter เกิด JsonException/500; HTTP omission ยังเป็น Undefined เช่นเดิม — C# caller ต้องส่ง `default` เอง ไม่เปลี่ยน wire contract
7. PATCH no-op คง version/เวลาเดิมแต่ยัง audit — client ต้องใช้ version ที่ server ส่งกลับ
8. ตรวจโค้ดอิสระเฉพาะ Task 6 เพิ่มก่อนส่งมอบ ไม่แทนการตรวจรวม Task 9 — ยังไม่ใช่การอนุมัติพร้อมใช้ Production หรือ push/merge
9. Export และการส่งข้อมูลก่อน export commit อยู่ Task 7 — ไม่อ้างว่าการอ่านปกติแทนการทดสอบ export ได้
10. Race กับ role-grant change, deactivate parent และ disable account อยู่ matrix ต่อใน Task 7 — Task 6 ตรวจ assignment revoke ไม่รับรองทุก lifecycle mutation
11. การตรวจ Task 6 ไม่รับรองย้อนหลังว่า coverage ของ login/session/forced-change/recovery/affected-user union ใน Tasks 2/4 ครบทุกกรณี — ข้อ Minor เดิมยังคงอยู่
12. UI stale response/cache/back/logout และ scoped HTTPS browser อยู่ Task 8 — E2E identity เดิม 13 กรณีไม่แทน scope UI acceptance
13. Whole-branch acceptance, capacity และ production readiness ยังเป็น Task 9/production gates — ไม่เปิด technical probes ใน Production
14. ผู้ตรวจอิสระทำ static review เท่านั้น ส่วน runtime gates รันโดยผู้พัฒนารอบนี้ — ไม่อ้างว่ามีผล runtime จากผู้ตรวจสองชุด

## ผล Code Review และข้อค้าง

ผู้ตรวจอิสระ Zeno ตรวจทั้ง tracked และ untracked files แบบ read-only ไม่พบ Critical/Important มี Minor ที่บันทึกไว้ 2 ข้อ ไม่รวมแก้ในรอบนี้ตามกระบวนการ executing-plans:

1. กรณี operation-first ควรมี signal ว่า revoke เข้าถึง lock แล้วก่อนปล่อย operation เพื่อให้ยืนยัน contention จริงทุกครั้ง ปัจจุบันทดสอบ commit order แต่ฝั่ง revoke อาจเริ่มช้าจนทำงานต่อกันได้
2. Audit failure test ตรวจ record, version และ user SecurityVersion แล้ว แต่ยังไม่ตรวจ session rows โดยตรง จึงยังไม่รับรอง assertion ของ revoke/stage ครบทุก field; ต้องแยก LastSeenAtUtc ซึ่ง middleware ต่ออายุนอก transaction ของ operation

## หลักฐานการตรวจสอบ

ผลตรวจวันที่ 24 กันยายน 2026: Test, Build, Lint และ E2E ผ่านครบ; Code Review ไม่พบ Critical/Important มี Minor 2 ข้อตามข้างต้น

- Baseline ก่อนแก้: 61/61
- RED ก่อน implementation: records 10 กรณีล้มด้วย endpoint ที่ยังไม่มี; safety 25 กรณีล้ม และ 1 กรณี global-admin 404 ผ่านจากพฤติกรรมเดิม
- GREEN HTTP ชุดแรก: 36/36
- Race: 9/9
- OpenAPI: RED 12 → GREEN รวม identity 70/70
- Focused รวม boundary tests: 126/126 ไม่มี skipped
- Frontend tests: 28/28; ESLint และ Next build ผ่าน
- HTTPS/E2E: 13/13 และ TLS/cookie/CSRF/host acceptance ที่พอร์ต 4001 ผ่าน
- Backend ทั้งชุด: 706/706 ไม่มี skipped ใช้เวลา 13 นาที 31 วินาที
- Backend Build: 0 warnings, 0 errors; `dotnet format --verify-no-changes --no-restore` exit 0

Log ระหว่างงานอยู่ที่ `/private/tmp/tpr10-m3t6-*.log` ไม่รวมใน Git เพราะเป็นผลชั่วคราว

## งานถัดไป

เมื่อผู้ใช้สั่ง Task 7 จึงเพิ่ม export-simulation และขยาย race/acceptance ที่ระบุในแผน ไม่เริ่มอัตโนมัติ ไม่ push หรือ merge จากการทำ Task 6

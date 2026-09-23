# Module 3 — ผลงาน Task 4: มอบหมายบทบาทตามขอบเขต

สถานะ: ผ่านการตรวจรับขอบเขต Task4 แล้ว ยังไม่ใช่การรับรอง Module 3 พร้อม production

## สิ่งที่พัฒนา

- API 6 เส้นทาง: GET/POST `/api/v1/scope-assignments`, POST `/{id}/replace`, POST `/{id}/revoke`, GET `/options/users` และ `/options/roles`
- ทุกเส้นทางต้องมีสิทธิ์ระบบ `scope-assignments:manage` และ MFA ล่าสุด; mutation ต้องผ่าน CSRF/Origin/Host เดิม และ service ตรวจสิทธิ์ซ้ำหลังได้ advisory lock ภายใน transaction
- ห้าม grant/replace/revoke assignment ของตนเอง ใช้ actor จาก session; ไม่รับ actorId หรือการเปลี่ยน UserId ผ่าน body
- Grant ตรวจบัญชีและโครงสร้าง active, exact tuple, role class ที่อนุญาต และเหตุผล; ไม่แก้ global user_roles และไม่มอบสิทธิ์ parent/ลูกเพิ่ม
- Replace ถอนรายการเดิมและสร้างรายการใหม่แบบ atomic โดยเก็บประวัติเดิม; no-op คืนรายการเดิมโดยไม่เพิ่ม version หรือถอน session; stale version/duplicate ตอบ 409 โดยไม่เปลี่ยนข้อมูล
- Revoke ถอนเฉพาะ assignment ที่ระบุ ไม่ใช้ helper ที่ถอนทั้ง user/subtree และไม่คืน assignment อัตโนมัติ
- Grant/replace/revoke ที่มีผลจริง invalidate sessions ของ target และเพิ่ม SecurityVersion ครั้งเดียว พร้อม audit ใน transaction เดียว; audit ล้มตอบ 503 และ rollback ทั้งชุด
- Binding ที่ผิดรูปแบบก็มี denial audit หลัง authorization โดยไม่อ่านหรือเก็บ body; เพิ่มเฉพาะ metadata key `assignment-id` สำหรับเชื่อมประวัติ replace
- Options ไม่ต้องมี users:manage/roles:read และไม่ขยายสิทธิ์ Module2: users มีเฉพาะ Id/Username/IsActive ไม่คืน actor; roles มีเฉพาะ Id/Name/RoleClass/BusinessCapabilities ไม่รวม system-administration หรือ system permissions
- OpenAPI เพิ่ม 6 สัญญา assignment-control-plane โดยคง identity 26 และ organization 12 เส้นทางเดิม

## สัญญารายการ

ใช้ `page`/`pageSize` ค่าเริ่มต้น 1/25 จำกัด 100 และปฏิเสธ offset overflow; response ใช้ `items,total,pageNumber,pageSize` ตามสัญญาร่วม Page<T>

GET assignments กรองด้วย `userId`, `workspaceId`, `projectId`, `siteId`, `revoked` แบบ nullable tuple ตรงกันทั้งหมด ไม่มี scope filter คือรายการ control plane ทั้งหมด ไม่ใช่สิทธิ์ธุรกิจทุก scope; ไม่ระบุ revoked คือรวมประวัติ

Options ใช้ `prefix` ยาวไม่เกิน 100 ไม่รับ control characters ค้นแบบ prefix literal ไม่ใช้ wildcard; username ไม่แยกตัวพิมพ์ผ่าน normalized username ส่วน role name แยกตัวพิมพ์ รายการผู้ใช้ยังแสดงสถานะ inactive ได้ แต่ grant ให้บัญชี inactive ไม่ได้

## กระบวนการและผลทดสอบ

ดำเนินงานแบบ Superpowers Native/inline ตามแผนที่อนุมัติ โดย baseline lifecycle ผ่าน 26/26; RED API/atomicity 27 กรณี จากนั้น API ขยาย RED39 และ OpenAPI RED6 ก่อน implementation ที่เกี่ยวข้อง

รอบแรกผ่าน 41/44 อีก 3 กรณีเป็นสมมติฐาน fixture ผิด: SeedUserAsync สร้าง global role ว่างไว้เสมอ จึงเปลี่ยน assertion เป็นการยืนยันว่า global roles เดิมไม่ถูกแก้ และผ่านซ้ำ 3/3 โดยไม่เปลี่ยน production เพื่อหลบ test

ผลโค้ดสุดท้าย: backend ทั้งชุดผ่าน 576/576 ไม่มี skipped, backend Build ไม่มี warning/error และ format verification ผ่าน; focused 123/123; Node24 tests 28/28, Lint, frontend Build ผ่านแล้ว Code Review อิสระไม่มี Critical/Important ทุกผลเป็นการตรวจรอบ Task4 นี้

E2E รอบแรก browser ผ่าน 13/13 แต่ขั้น HTTPS acceptance ต่อท้ายล้มด้วย 502: test สุดท้าย restart API แล้ว Docker คืนค่าก่อนแอปพร้อม จึงเพิ่มการรอ health readiness หลัง browser แบบมีเพดาน 30 วินาที ใช้ GET เท่านั้น ไม่ retry mutation หลังแก้รันด้วย Node24 ผ่าน browser 13/13 และ HTTPS acceptance ครบ (TLS/cookie/CSRF/host) ที่พอร์ต4001

คำสั่งตรวจรับ:

```sh
dotnet test backend/TPR10.sln --no-restore
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm test
npm run lint
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
```

ใช้ .NET SDK10.0.401 ตาม global.json และ Node24.19.0 ตาม engines; ผลย่อเก็บในรายงานนี้ ส่วน log ชั่วคราวคือ `/private/tmp/tpr10-m3t4-*.log`

## การตัดสินใจและขอบเขตที่ยังไม่ทำ

Code Review อิสระโดย Herschel ไม่พบ Critical/Important มี Minor ที่เลื่อนไปติดตาม 2 ข้อ (โค้ดปัจจุบันถูกต้องจากการตรวจ):

1. เพิ่ม controlled barrier ที่ถอนสิทธิ์ขณะ request กำลังรอ lock เพื่อป้องกันการย้าย guard ไปก่อน lock ในอนาคต; ปัจจุบันทดสอบถอนสิทธิ์ก่อนเรียก service
2. เพิ่ม assertions รายการ exact Project/Site/sibling และ userId filter; ปัจจุบัน test รายการตรวจผล exact Workspace และ query เทียบครบ tuple

Minor เดิมของ Tasks1–3 ยังไม่ถูกปิดจากงานนี้ ได้แก่ migration full-field roundtrip, inactive-ancestor affected-user regression และ Organization 403/401 race ผล runtime เป็นการตรวจโดยผู้พัฒนา ไม่ใช่การรันทดสอบสองชุดอิสระ

- ใช้ worktree และ dependencies เดิม ไม่แตะงานค้างของ main และไม่อัปเกรดแพ็กเกจ
- ตรวจพบ PATH เดิมเป็น Node20 นอก engines ของโปรเจกต์ จึงใช้ Node24.19.0 ที่มีอยู่ใน bundled runtime และรัน frontend/E2E ใหม่ ไม่ติดตั้งหรือเปลี่ยนแพ็กเกจ
- แก้ลำดับ setup ในตัวอย่างแผนให้ bootstrap admin ก่อน ScopeFixture เพราะ bootstrap ต้องยังไม่มี user
- เพิ่ม policy registration, OpenAPI และ binding audit จากรายการไฟล์ขั้นต่ำ เพื่อให้ทุก endpoint ที่เปิดมีสัญญาและการป้องกันครบ
- สร้าง Scopes/ScopeContracts.cs สำหรับ Page<T>/ExpectedChange ตามสัญญาร่วม Tasks5+ ต้องใช้ชนิดเดิม ไม่ประกาศซ้ำ
- การอ่าน control plane ถือ lock เพื่อ recheck สิทธิ์และ materialize ก่อน commit; ยังไม่รับรอง throughput production
- ตรวจอิสระเฉพาะ Task4 เพิ่มสำหรับการส่งมอบรอบนี้ ไม่แทน whole-branch review ใน Task9
- ยังไม่ทำ ScopeContext/discovery/data-plane/export/UI; อยู่ Tasks5–8 และต้องผ่าน Task9 ก่อนรับรองทั้งโมดูล
- พอร์ต development 4000 / production 4001 คงเดิม ไม่ push หรือ merge อัตโนมัติ
- Local commit อยู่บน `codex/module-3-organization-scope` หลังตรวจครบ ไม่แก้งานค้างบน main; ขั้นต่อไปคือ Task5: ScopeContext, scoped authorization และ discovery โดยรอผู้ใช้สั่งเริ่ม

# คู่มือ Module 3 — โครงสร้างองค์กร การมอบหมาย และขอบเขตข้อมูล

## ขอบเขตและสิ่งที่ยังไม่อนุมัติ

คู่มือนี้ใช้กับ Organization/Assignment API และ Portal ของ Module 3 ต้องใช้ร่วมกับ [Module 2](module-2-identity.md) สำหรับ HTTPS, bootstrap, session, CSRF, MFA และ key ring และ [Exit Gate](../architecture/module-3-exit-gate.md) สำหรับผลตรวจ/ข้อจำกัด การผ่าน tests หรือ push Git ไม่ใช่การอนุมัติ Production

Workspace → Project → Site เป็นโครงสร้าง แต่สิทธิ์ **ไม่สืบทอด**: `(workspaceId,null,null)`, `(workspaceId,projectId,null)` และ `(workspaceId,projectId,siteId)` เป็นคนละขอบเขต Department เป็นข้อมูลประกอบ ไม่ใช่ security scope ไม่มี global Administrator bypass สำหรับข้อมูลธุรกิจ และการสร้างองค์กร/บัญชี/พื้นที่ใหม่ไม่สร้าง assignment ให้อัตโนมัติ

## เตรียมฐานข้อมูลและระบบ

1. ตรวจปลายทาง PostgreSQL, backup และ restore ที่ทดสอบแล้ว ห้ามใช้ฐานข้อมูลจริงรัน integration tests หรือ fixture
2. Local ที่ทิ้งได้ใช้ขั้นตอน [Module 1](module-1-foundation.md) และ `sh ops/migrations/apply-local.sh` ภายใต้ connection string ที่จัดการผ่านช่องทางลับ สคริปต์จำกัด loopback/port/database/user โดยตั้งใจ ไม่แก้ guard เพื่อชี้ Production
3. ตรวจ migration `20260923173517_AddOrganizationScopeFoundation` ต่อจาก Module 2: เพิ่ม hierarchy/assignments/records และ permission domain; composite FK/CHECK ปิด dangling/cross-parent scope, unique active assignment รักษาประวัติ revoked ไม่ใช้แก้ SQL ตรงเพื่อแจกสิทธิ์
4. ฐานใหม่ bootstrap ผู้ดูแลผ่าน interactive CLI ตาม Module 2 จากนั้นตั้ง MFA ฐานที่มีบัญชีแล้วห้าม bootstrap ซ้ำ Migration นี้เพิ่ม system grantsใหม่สองรายการให้ role Administrator IDคงที่ที่มีอยู่ด้วย จึงต้องให้เจ้าของระบบทบทวนผลต่อผู้ถือroleนั้นก่อน apply ไม่ใช่อ้างว่าmigrationไม่เปลี่ยนgrants
5. Catalog มี 12 capabilities: system เดิม6 + `organization:manage`, `scope-assignments:manage` และ business4 (`scope-probe:read`, `scope-probe:write`, `scope-probe:restricted-read`, `scope-probe:export`) bootstrap Administrator ได้ system8 เท่านั้น ส่วนroleอื่นต้องอนุมัติgrantsผ่านAPIเอง ไม่มีbusiness assignmentอัตโนมัติ ตรวจ grantsจริงก่อนใช้งานฐานเก่า
6. Dev Next ใช้ **4000**, production build ใช้ **4001**; เข้า auth ผ่าน HTTPS proxy ที่เชื่อถือใบรับรองเท่านั้น ตัวอย่างทดสอบใช้ `https://localhost:4443` → private API loopback5080 ไม่เปิด API สาธารณะ

API ไม่ migrate ตอน start และไม่เปลี่ยน schema ย้อนหลังแทนการตรวจ deployment จริง Operations ต้อง review SQL migration, แยก migration owner/runtime role และกำหนด maintenance window ก่อนใช้งานจริง

## ผู้ดูแลสองคนและ domain ของบทบาท

ผู้ดูแล A สร้างบัญชี B ผ่าน Users API ตาม Module 2 โดยผู้กำหนด global roles ต้องมี `roles:manage` และผู้สร้างบัญชีมี `users:manage` ผู้รับเปลี่ยนรหัสผ่านและตั้ง MFA ด้วยตนเอง ห้ามใช้บัญชีร่วม/ใส่รหัสผ่านในเอกสาร เมื่อ B ผ่านกระบวนการอนุมัติจึงให้ system capability สำหรับงานที่รับผิดชอบ

ผู้มี `scope-assignments:manage` + recent MFA มอบหมายได้เฉพาะ **ผู้อื่น**: grant/replace/revoke ของตัวเองถูกปฏิเสธทุกเส้นทาง จึงให้ B มอบหมาย business role แก่ A และกลับกันเมื่อมีเหตุผลที่อนุมัติ ไม่มีทางลัด self-grant และไม่มีระบบป้องกันผู้ดูแลสองคนสมคบกันแทน governance ขององค์กร

Role grants ใช้ Permissions/Role API เดิม (`GET /api/v1/permissions`, `PUT /api/v1/roles/{id}/permissions` เป็นการแทนรายการทั้งชุด) แยก system/business domain: global session ไม่รวม business capabilities; scoped role ไม่ทำให้ผ่าน control plane ห้ามนำ role class `system-administration` มาเป็น assignment ใช้ business role class ที่องค์กรอนุมัติ เช่น `staff` (Staff) หรือ `approval` (Approver) การมี scoped privileged role บังคับ MFA ใน login/session/forced-change/recovery แม้ไม่มี global privileged role

อย่าแจก `users:manage` หรือ `roles:read` เพิ่มเพียงเพื่อใช้หน้า assignment: options endpoints ให้ข้อมูลขั้นต่ำภายใต้ `scope-assignments:manage` อยู่แล้ว

## สร้างโครงสร้างเริ่มต้นผ่าน API

ทุก route ในตารางต้อง Active session, `organization:manage`, recent MFAไม่เกิน15นาที คำขอ POST/PATCH ต้อง cookie + Origin + CSRF ตาม Module 2 ห้ามนำ token/cookie ไปใส่ command history ตัวอย่าง body ด้านล่างไม่มีข้อมูลจริง

| ระดับ | Collection (GET/POST) |
| --- | --- |
| Workspace | `/api/v1/organization/workspaces` |
| Department | `/api/v1/organization/workspaces/{workspaceId}/departments` |
| Project | `/api/v1/organization/workspaces/{workspaceId}/projects` |
| Site | `/api/v1/organization/workspaces/{workspaceId}/projects/{projectId}/sites` |

POST ส่ง `{ "code": "DEMO", "name": "พื้นที่ตัวอย่าง" }` คืน201พร้อม ID/version; สร้าง Workspace ก่อน Project แล้ว Site ใช้ ID ของ parent ที่ได้รับจริง Department ไม่ใช่ parent ของ Project

PATCH collection ต่อ `/{id}` ส่ง `{ "name": "ชื่อใหม่", "isActive": true, "expectedVersion": 1, "reason": "เลขอ้างอิงคำขอที่ไม่มีข้อมูลลับ" }` ใช้ version ล่าสุดจาก GET ไม่เดาเลขหรือ retry อัตโนมัติ GET ใช้ pageเริ่ม1/pageSizeเริ่ม25 สูงสุด100 ตรวจหน้าถัดไปตาม total

สร้างผ่านหน้า `/portal/admin/organization` ได้ภายใต้เงื่อนไขเดียวกัน UI ไม่ใช่แหล่งอำนาจ API ตรวจซ้ำใน transaction หลังได้ lock

## มอบหมายและถอนสิทธิ์

| Endpoint | สัญญา |
| --- | --- |
| GET `/api/v1/scope-assignments` | filter `userId`, exact `workspaceId/projectId/siteId`, `revoked`, page/pageSize; null ไม่ใช่ wildcard เมื่อระบุ scope |
| GET `/api/v1/scope-assignments/options/users` หรือ `/options/roles` | ข้อมูลขั้นต่ำค้นด้วย prefix และแบ่งหน้า ไม่เปิด credentials |
| POST `/api/v1/scope-assignments` | `{userId,scope:{workspaceId,projectId,siteId},roleId,reason}` UUID จาก API; ใช้ null สำหรับระดับที่ไม่มี |
| POST `/api/v1/scope-assignments/{id}/replace` | `{scope,roleId,expectedVersion,reason}` เจ้าของเดิมเปลี่ยนไม่ได้ |
| POST `/api/v1/scope-assignments/{id}/revoke` | `{expectedVersion,reason}` |

ทุก route ต้อง system `scope-assignments:manage` และ recent MFA; POST ต้อง CSRF หน้า `/portal/admin/assignments` แสดงชื่อบทบาทและแยกปุ่มถอนเมื่อคนเดียวมีหลาย role ในพื้นที่เดียวกัน

เปลี่ยนจริงจะเพิ่ม security version/ถอน sessions ของผู้ได้รับผล พร้อม assignment history และ audit ใน transaction เดียว Replace revoke แถวเก่าและสร้างแถวใหม่ ไม่เขียนทับประวัติ; replaceที่ค่าเดิมเป็น no-op ไม่ถอน session ให้โหลดข้อมูลล่าสุดก่อนทำรายการ การ login ใหม่ไม่ทำให้ assignment ที่ revoked กลับมา

Deactivate Workspace/Project/Site ถอน assignments ใต้ระดับนั้นและ sessions ที่เกี่ยวข้อง Disable account ถอน assignments/sessions ด้วย Reactivate/enable ไม่คืน assignment ต้องอนุมัติ grant ใหม่ การเปลี่ยน role grants กระทบ union ของ global และ scoped users โดยไม่ทำซ้ำผู้ใช้เดิม

## Discovery, Portal และข้อมูลทดสอบ

`GET /api/v1/scopes` ต้อง Active session คืนเฉพาะ exact assignments ที่ยัง active พร้อม breadcrumb ขั้นต่ำและ business capabilities ต่อ tuple; ไม่คืน business records หรือเพิ่มสิทธิ์ parent/sibling หน้า `/portal` ใช้รายการนี้ ไม่มี assignmentแสดงหน้าว่าง ไม่แสดง catalog ทั้งองค์กร

Technical records อยู่ที่ `/api/v1/workspaces/{workspaceId}[/projects/{projectId}[/sites/{siteId}]]/scope-probe-records` มี list/detail/POST/PATCH และ POST `export-simulation` สำหรับ Development/Testing เท่านั้น ไม่ใช่ business module หรือระบบไฟล์/NAS

- อ่านต้อง `scope-probe:read`; เขียนต้อง `scope-probe:write` ใน exact tuple
- `restrictedNote` ปรากฏเฉพาะ `restricted-read` + recent MFA; ไม่มีสิทธิ์คือไม่มี property ไม่ใช่ null แทนข้อมูลลับ
- POST/PATCH ที่ส่ง `restrictedNote` แม้เป็น null ต้อง write + restricted-read + recent MFA; PATCHไม่ส่งคือคงค่าเดิม, nullล้าง, stringแทนค่า; write-onlyตอบ ID/version ไม่แถมข้อมูลอ่าน
- Export ต้อง export + recent MFA ไม่บังคับ read แต่ restricted visibility ยังแยก ส่ง JSONหลัง audit commit ไม่มีการสร้างไฟล์ จำกัด100 ถ้าเกินตอบ400ไม่ตัดเงียบ ให้ลดช่วง UTC `createdFrom` รวมขอบต้น / `createdTo` ไม่รวมขอบท้าย
- Production API ไม่ map probesและไม่ประกาศใน OpenAPI ต้องไม่ตั้ง server fixture flag `TPR10_SCOPE_TEST_UI` เป็น true ใน deployment จริง การทดสอบ Next production build กับ API Testing ไม่ใช่ API Production; มีการทดสอบ boundary ทั้งสองฝั่งแยกกัน

ทุก response ส่วน private ใช้ no-store ไม่ใส่ restricted payload ใน localStorage/log หน้าเปลี่ยน scopeใช้ full navigation ยกเลิก requestเก่าและล้างข้อมูลก่อนผลใหม่ การเปลี่ยนบัญชีที่สำเร็จผ่านUIแจ้งหน้าต่างอื่นด้วย BroadcastChannel หรือ storage event ซึ่งส่งเพียงสัญญาณ/nonce ไม่ส่งidentity/token/ข้อมูลธุรกิจ หน้าต่างรับซ่อนและถอดprivate DOM ยกเลิกquery/mutationเก่า แล้วโหลดจากserverใหม่; abortไม่ใช่การย้อนรายการที่servercommitไปแล้ว

เมื่อกลับเข้าแท็บ/หน้าต่าง จะซ่อนและปิดการโต้ตอบชั่วคราวแล้วตรวจ `/api/v1/auth/session` เทียบกับsessionที่SSRตรวจไว้ รักษาdraftเฉพาะในmemoryเมื่อactor/stage/permissions/MFAเดิมยังตรง ไม่reloadทิ้งทุกครั้ง หากเปลี่ยนหรือถูกถอนต้องโหลดใหม่/เข้าสู่ระบบใหม่ ถ้าบริการตรวจsessionล่มให้คงซ่อนข้อมูล ไม่แกล้งlogout และตรวจอีกครั้งเมื่อบริการกลับมา ไม่เก็บdraftลงpersistent storage

หากbrowserปิดทั้งBroadcastChannelและstorage จะตรวจการเปลี่ยนบัญชีเมื่อกลับเข้าแท็บ/focusหรือโหลดหน้าใหม่ ไม่รับรองการแจ้งทันทีในหน้าต่างที่ไม่เคยกลับมาfocus; ไม่ใช่ระบบserver-pushตรวจทุกremote revocation การย้อนhistoryยังตรวจใหม่ Browser testsใช้Firefoxจริงร่วมกับcontrolled visibility events ไม่รับรองทุกOS/browser/BFCache

## จัดการข้อผิดพลาดและ audit outage

| สถานะ | ผู้ใช้/ผู้ดูแลควรทำ |
| --- | --- |
| 400 | ตรวจ body, UUID, pagination, version และช่วง export; ไม่เดาว่า serverแก้ให้ |
| 401 | sessionหมดอายุ/ถูกถอน เข้าสู่ระบบใหม่; ห้ามใช้ผลเก่าแสดงต่อ |
| 403 | capability/MFA/CSRFไม่ผ่าน ตรวจ session/step-upก่อน; ไม่ retry mutation อัตโนมัติ |
| 404 | scopeไม่มี/ไม่active/ไม่มอบหมายหรือ recordผิดtuple ใช้ข้อความไม่เผยว่ามีข้อมูลจริงหรือไม่ |
| 409 | version/duplicate conflict โหลดใหม่และให้ผู้ใช้ยืนยัน ไม่ส่งซ้ำด้วย versionที่เดา |
| 429 | เคารพ Retry-After ไม่วนส่งคำขอ |
| 503 | dependency/auditไม่พร้อม ไม่แปลงเป็น logout และไม่แสดง success |
| 500/อื่น ๆ | อาจเป็น bodyว่างหรือไม่ใช่JSON ตรวจ Content-Type; ใช้ correlation ID ตรวจ incident ไม่สรุป rollback จาก statusอย่างเดียว |

Discovery dependency failure ใช้ `urn:tpr10:scope-unavailable-service` แต่ clientต้องรับข้อผิดพลาดอื่นด้วย Auditล้มใน transactionทำให้ข้อมูล/assignment/grants/securityversion/session rollback และไม่ส่ง read/export DTO; การตอบกลับหายหลัง commitยังเป็นผลไม่แน่นอน จึงตรวจ GET/history/auditที่ได้รับอนุญาตก่อนตัดสินใจส่งใหม่

เมื่อ outage ให้เก็บเฉพาะเวลา/operation/correlation ID ตรวจการเข้าถึงฐานข้อมูลและ audit insert/commit โดย Operations ฟื้น dependencyแล้วตรวจความสอดคล้อง ห้ามปิด audit/ยกเลิก transaction/ให้สิทธิ์ข้ามเพียงเพื่อให้คำขอสำเร็จ ห้ามแก้หรือลบ auditเก่า

Audit เก็บ actor/role/scope/action/outcome/correlation/row-count และ filters/destination-typeตาม allowlist ไม่เก็บ note/restrictedNote/password/token/factor/recovery codes แต่ allowlistไม่ใช่ DLP ที่จับ secretซึ่งผู้ใช้พิมพ์ใน reasonได้ทุกชนิด ห้ามเปิด body/header/cookie logging หรือ browser traceของผู้ใช้จริง

## Downgrade และการรับรองก่อน Production

การ rollback migration Module3 ทำลายโครงสร้าง/assignments/records ของ Module3 ไม่ใช่ปุ่มย้อน deploymentที่ปลอดภัยอัตโนมัติ และ auditอ้างอิงเดิมต้องคงอยู่ตามนโยบาย ห้ามรัน downgradeกับข้อมูลจริงโดยไม่มี backup/restoreที่พิสูจน์แล้ว, migration SQL review, reconciliation plan และผู้อนุมัติ ควรประเมิน forward-fixก่อน

ยังต้องให้ System/Business/Security owner ลงนาม scope matrix, role grants, field visibility, ผู้มอบหมาย และข้อจำกัด ส่วน Operations ต้องรับรอง capacityของ advisory lockร่วม, rate limiterหลายreplicas, TLS/firewall, key restore/rotation, monitoringและ recovery drill รวมข้อค้าง Module2 ไม่มี production sign-offเกิดขึ้นจาก Task9

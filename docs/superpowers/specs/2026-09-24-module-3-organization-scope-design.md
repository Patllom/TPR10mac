# Design Spec — Module 3: โครงสร้างองค์กรและสิทธิ์ตามขอบเขตข้อมูล

วันที่จัดทำ: 2026-09-24 (Asia/Bangkok)
สถานะ: ผู้ใช้อนุมัติเอกสารและ Implementation Plan แล้ว และสั่งเริ่ม Task 1 เมื่อ 2026-09-24; ไม่ใช่การอนุมัติเปิด production
ฐานโค้ดที่ตรวจ: `ab6f30efbfa5c4861d657db5914736592c6d8b64`
ภาษา: ไทย โดยคง identifier และชื่อเทคนิคที่จำเป็น

เอกสารอ้างอิง: [Architecture Baseline Module 0](2026-09-18-tpr10-module-0-architecture-baseline-design.md) ส่วน 6–8, 11, 17, 20 และ [Exit Gate Module 2](../../architecture/module-2-exit-gate.md)

## 1. เป้าหมายและความหมายของการอนุมัติ

สร้าง Organization and Scope ที่โมดูลธุรกิจนำไปใช้ได้ โดยผู้ใช้ที่เข้าสู่ระบบแล้วจะอ่าน เขียน หรือส่งออกข้อมูลได้เฉพาะพื้นที่และชนิดข้อมูลที่ได้รับมอบหมาย ไม่ใช่เพียงมี role ระดับบัญชีแล้วเข้าถึงทุกพื้นที่

ผู้ใช้ยืนยันแล้ว:

1. Project assignment ไม่ครอบคลุม Site อัตโนมัติ ต้องระบุ Site แยกทุกแห่ง รวม Site ที่สร้างใหม่
2. ผู้ใช้คนเดียวมีบทบาทธุรกิจต่างกันตาม Project/Site ได้ เช่น ผู้อนุมัติใน A แต่พนักงานใน B
3. ต่อเติม Module 2 โดยแยกสิทธิ์ดูแลระบบออกจากสิทธิ์ธุรกิจ ไม่เขียนระบบ identity ใหม่ทั้งชุด
4. ผู้ดูแลบัญชี/assignment ไม่ได้สิทธิ์อ่านข้อมูลธุรกิจทุกโครงการโดยอัตโนมัติ
5. ถอน assignment แล้ว revoke session พร้อม audit และทดสอบคำขอที่เกิดพร้อมกัน

รายละเอียดเชิงนโยบายในฉบับนี้ เช่น การห้ามมอบหมายสิทธิ์ให้ตนเองและขอบเขตหน้าจอ รวมอยู่ในฐานออกแบบที่ผู้ใช้ให้ดำเนินการต่อแล้ว การอนุมัตินี้ไม่แทน Security/Business owner sign-off สำหรับ production

ผลสำเร็จของ Module 3 คือหลักฐานการแยกข้อมูล ไม่ใช่ระบบ Check-in/เบิกจ่าย/ทรัพย์สินที่เสร็จแล้ว และไม่ใช่การอนุมัติเปิด production

## 2. ทางเลือกและแนวทางที่เลือก

| ทางเลือก | ประโยชน์ | ข้อแลกเปลี่ยน |
| --- | --- | --- |
| ต่อเติม scoped authorization โดยใช้บัญชีและ catalog เดิม — เลือกแนวทางนี้ | รักษา identity/session ที่ทดสอบแล้ว แยกขอบเขตการใช้ permission ได้ชัด | ต้องตรวจจุดเชื่อม MFA, role mutation และ session ทุกแห่งที่เดิมอ่านเฉพาะ user_roles |
| เปลี่ยนระบบสิทธิ์ทั้งหมดเป็น assignment แบบเดียว | มี abstraction กลางเพียงแบบเดียว | เปลี่ยนความหมายของสิทธิ์ดูแลระบบเดิมและเพิ่ม regression scope โดยยังไม่จำเป็นต่อ Module 3 |

ใช้ application authorization และ scoped repository ร่วมกับ composite database constraints เป็นหลักฐาน MVP ไม่เพิ่ม PostgreSQL row-level security หรือ external policy engine ในรอบนี้ และไม่อ้างว่าฐานข้อมูลป้องกัน direct SQL จากผู้มีสิทธิ์ฐานข้อมูลได้ทั้งหมด

## 3. ขอบเขตส่งมอบ

### อยู่ใน Module 3

- จัดการ Workspace, Project, Site และ Department แบบโครงสร้างพื้นฐาน
- มอบหมาย/ถอนบทบาทธุรกิจให้ผู้ใช้ใน scope ที่ระบุ พร้อมประวัติและเหตุผล
- สร้าง `ScopeContext` ฝั่ง API และประเมิน named permission จาก assignment ที่ตรง scope
- ใช้ข้อมูลปัจจุบันตรวจการเข้าถึงและเพิกถอน ไม่เชื่อ role/scope ที่ client ส่งมา
- เชื่อม scoped role เข้ากับนโยบาย MFA และ session revocation ของ Module 2
- Repository และ technical record สำหรับพิสูจน์ list/detail/create/update/export-simulation
- หน้าจอจัดการโครงสร้าง/assignment ที่จำเป็น และตัวเลือกพื้นที่ใน Portal
- OpenAPI, audit, security acceptance, migration และ runbook ภาษาไทย

### ไม่อยู่ใน Module 3

- Workflow อนุมัติ, maker-checker ของธุรกิจ และการอนุมัติข้อยกเว้น Check-in จริง
- NAS, attachment, การส่งอีเมล production และ report/export job จริง
- ข้อมูลธุรกิจของ Modules 6–8 หรือ dashboard รวมหลาย Workspace
- การย้าย Project/Site ข้าม parent, hard delete โครงสร้าง/ประวัติ, bulk import และ delegated scope administrators
- ตัวแก้ policy แบบภาษาทั่วไปหรือ UI กำหนด field policy โดยผู้ใช้
- การเก็บ Minor เดิมของ Module 2 ทั้งหมดโดยอัตโนมัติ; แก้เฉพาะสิ่งที่ขวาง Module 3 โดยระบุเหตุและ regression

## 4. แบบจำลอง Scope: ตรงระดับ ไม่สืบทอดสิทธิ์

Scope key เป็นหนึ่งในสามรูปแบบเท่านั้น:

| ระดับ | ค่าใน scope key | ข้อมูลที่เข้าถึงได้เมื่อ permission ครบ |
| --- | --- | --- |
| Workspace | workspace_id; project_id และ site_id เป็น null | record ระดับ Workspace เท่านั้น |
| Project | workspace_id + project_id; site_id เป็น null | record ระดับ Project ที่ไม่มี Site เท่านั้น |
| Site | workspace_id + project_id + site_id | record ของ Site นั้นเท่านั้น |

- ค่า null หมายถึงระดับของ record ไม่ใช่ wildcard และไม่ใช่ “ทุก Site”
- Site assignment ระบุ parent อยู่ใน tuple แต่ไม่สร้างสิทธิ์อ่าน record ระดับ Project หรือ Workspace
- ไม่บังคับให้มี business assignment ของ parent เพิ่มเพื่อใช้ Site ที่ถูกมอบหมาย; parent ใช้ตรวจ ancestry และแสดง breadcrumb เท่าที่จำเป็น
- ผู้มีสิทธิ์ Site A เห็นชื่อ/identifier ของ parent เพื่อเลือกพื้นที่ได้ แต่ไม่เห็นรายการ Site พี่น้อง จำนวน record หรือข้อมูลธุรกิจของ parent
- การขอข้อมูลระดับ Project ไม่รวบข้อมูล Site ลูก แม้ผู้ใช้มี assignment บาง Site ภายใต้ Project นั้น
- ไม่มี endpoint “ทุก scope” สำหรับ business records ใน Module 3; หนึ่งคำขอเลือก scope เดียวอย่างชัดเจน
- Department เป็น metadata ภายใน Workspace ไม่ได้ให้สิทธิ์เข้าถึงตามแผนกโดยอัตโนมัติ
- Workspace/Project/Site หรือ account ที่ inactive ทำให้ assignment ใต้โครงสร้างนั้นใช้ไม่ได้ การเปิดกลับไม่ยกเลิกการ revoke assignment เดิม

ตัวอย่าง: สมชายมี Approver ที่ Project A และ Staff ที่ Site A1 จะใช้ permission ของ Staff ที่ Site A1 เท่านั้น การมี Approver ที่ Project A ไม่เพิ่มสิทธิ์อนุมัติให้ A1 และไม่เปิด Site A2

## 5. แยก System Permission กับ Scoped Business Permission

### 5.1 Catalog ร่วม แต่แหล่งมอบสิทธิ์แยกกัน

- ใช้ `users`, `roles`, `permissions`, `role_permissions` เดิมร่วมกัน ไม่สร้างชื่อ role ซ้ำรายโครงการ
- `user_roles` เดิมเป็นแหล่งสิทธิ์ระบบ; scoped endpoint ห้ามนำ capability จาก global role มารวมกับ capability จาก assignment
- เพิ่มการจำแนก permission เป็น `system` หรือ `scoped-business` แบบ explicit ในข้อมูลที่ควบคุมด้วย migration ไม่เดาจาก prefix ของชื่อ
- สิทธิ์เดิมทั้งหกของ Module 2 คงอยู่ในกลุ่ม system เพื่อรักษา contract เดิม
- การผูก role ใน `user_scope_assignments` ให้ได้เฉพาะ business capabilities ของ role นั้น ไม่เปิด `users:manage`, `roles:manage` หรือ capability ระบบอื่น
- แม้ global role มี business capability ก็ไม่ทำให้เข้าถึง scoped endpoint ได้โดยไม่มี scoped assignment ที่ตรงกัน
- ไม่เปิดการเปลี่ยนชนิด permission ผ่าน generic role administration API
- Role class `system-administration` ไม่อนุญาตให้เลือกเป็น business assignment; Staff, Approver, Accounting, Finance Data Access และ custom role ใน class ที่อนุญาตใช้ได้
- คนเดียวมีหลาย role ใน scope เดียวได้; รวม business capabilities เฉพาะ assignment ที่ยังใช้ได้และตรง tuple เท่านั้น

### 5.2 Permission ของ Module 3

| ชื่อ | กลุ่ม | การบังคับใช้ |
| --- | --- | --- |
| `organization:manage` | system | จัดการและดู catalog โครงสร้างทั้งหมด ต้องมี recent MFA |
| `scope-assignments:manage` | system | ดู/มอบหมาย/ถอน assignment ของผู้อื่น ต้องมี recent MFA |
| `scope-probe:read` | scoped-business | list/detail ของ technical record ใน scope ที่ตรงกัน |
| `scope-probe:write` | scoped-business | create/update technical record ใน scope ที่ตรงกัน |
| `scope-probe:export` | scoped-business | export-simulation ใน scope ที่ตรงกัน ต้องมี recent MFA |
| `scope-probe:restricted-read` | scoped-business | อ่าน restricted field ของ technical record ต้องมี read/export ตาม operation ด้วยและ recent MFA |

Migration ให้ Administrator เดิมได้เฉพาะ management capabilities ใหม่ ไม่ auto-grant business capabilities หรือสร้าง assignment ให้บัญชีจริง Catalog ของ business permissions เริ่มโดยไม่มี grant อัตโนมัติ; ชุดทดสอบตั้ง role grants และ assignments ใน fixture ที่แยกจาก production

### 5.3 ผู้มอบหมายและการเพิ่มสิทธิ์ให้ตนเอง

ข้อเสนอที่ใช้ในเอกสารนี้: ผู้มี `scope-assignments:manage` และ MFA จัดการ assignment ของผู้ใช้อื่นได้ข้ามโครงสร้างในฐานะ control plane แต่ห้ามเพิ่ม เปลี่ยน หรือถอน assignment ของตนเองผ่าน API นี้ หากผู้ดูแลต้องเข้าถึงข้อมูลธุรกิจให้ผู้ดูแลอีกคนเป็นผู้มอบหมายและมี audit

การสร้างผู้ดูแลคนที่สองใช้ขั้นตอนจัดการบัญชี/role เดิมของ Module 2 ไม่เพิ่ม hidden bypass หรือ break-glass endpoint ผู้มีอำนาจจัดการสิทธิ์ยังเป็น trusted administrator ไม่อ้างว่าการแยก control plane ป้องกันผู้ดูแลสมคบกันได้

## 6. โมเดลข้อมูลและความสมบูรณ์ของฐานข้อมูล

| Entity | ข้อมูลและกฎหลัก |
| --- | --- |
| workspaces | UUID, code ไม่ซ้ำ, name, active, version และข้อมูลผู้สร้าง/แก้ไข |
| departments | UUID, workspace_id, code ไม่ซ้ำภายใน Workspace, name, active; ไม่เป็น security scope |
| projects | UUID, workspace_id, code ไม่ซ้ำภายใน Workspace, name, active, version |
| sites | UUID, workspace_id, project_id, code ไม่ซ้ำภายใน Project, name, active, version |
| user_scope_assignments | UUID, user_id, workspace_id, project_id nullable, site_id nullable, role_id, created_at/by, revoked_at/by, reason และ version |
| scope_probe_records | UUID, exact scope tuple, public note, restricted note, version, created/updated UTC และ actor |

กฎบังคับ:

1. Identifier เป็น UUID; เวลาในฐานข้อมูลเป็น UTC และหน้าเว็บแสดง Asia/Bangkok
2. CHECK ปฏิเสธ site_id ที่มีค่าแต่ project_id เป็น null และปฏิเสธ revoked metadata ที่ขัดกัน
3. Composite foreign key ของ project/site/assignment/record ต้องตรวจ workspace และ project ที่เป็น parent จริง ไม่ใช้เพียง FK ไป UUID ทีละตัว
4. Active assignment ของ user + exact scope + role ซ้ำไม่ได้ รวมกรณี nullable columns; ใช้ unique partial indexes แยกสามระดับเพื่อไม่ให้ null ทำให้เกิดรายการซ้ำ
5. Code รับตัวอักษร ASCII A–Z, ตัวเลข, `_`, `-` ยาว 1–64 ตัว หลัง trim/uppercase; name 1–200 ตัว ไม่รับ control characters
6. Assignment ผูก role/scope แบบไม่แก้ tuple ในที่เดิม; เปลี่ยน role หรือ scope โดย revoke เดิมและสร้างใหม่ใน transaction เดียวพร้อม version check เพื่อเก็บประวัติ
7. ไม่มี hard delete หรือย้าย ancestry; ใช้สถานะ active และการ revoke การ deactivate parent revoke assignments ใต้ parent และ sessions ของผู้ได้รับผลทั้งหมดใน transaction เดียว
8. ใช้ optimistic version สำหรับ rename/deactivate/revoke/update; version เก่าตอบ 409 และไม่เปลี่ยนข้อมูล
9. การ disable account ต้อง revoke assignment พร้อม sessions; เปิด account กลับไม่คืน assignments เดิม
10. การเปิด Workspace/Project/Site กลับให้ใช้งานได้ต้องมอบหมายใหม่เอง ไม่ฟื้น assignment ที่ถูก revoke

Migration ต้องรักษาบัญชี/role/session/audit เดิม ไม่สร้าง production organization แบบเดาสุ่ม และมี roundtrip บนฐานทดสอบ; runbook ห้าม downgrade หลังมีข้อมูล scope จริงหากไม่มีแผนรักษาข้อมูลและสิทธิ์

## 7. ขอบเขตของส่วนประกอบ

| ส่วนประกอบ | หน้าที่ | สิ่งที่ห้ามทำ |
| --- | --- | --- |
| Organization administration | จัดการโครงสร้างและ optimistic version | อ่าน business records เพื่อแสดงยอดรวม |
| Assignment administration | ตรวจผู้มอบหมาย/target/role/ancestry และทำ lifecycle/audit/revoke | เชื่อ actor จาก body หรือแก้ user_roles เพื่อจำลอง scope |
| Scoped authorization | resolve exact scope และ permission จาก session/DB ปัจจุบัน | ใช้ global business grants หรือ wildcard parent |
| ScopeContext | immutable ผลการอนุญาตของ request รวม actor, scope, capability, acting role และ assignment ที่ใช้ | ให้ client สร้างหรือใช้เป็น bearer credential ข้าม request |
| Scoped repository | query/update ที่ใส่ exact scope predicate ตั้งแต่ต้น | มี public overload ที่รับเพียง record ID โดยไม่มี ScopeContext |
| Identity effective-role/MFA policy | รวม role class ที่ยังมีผลจาก global role และ scoped assignment เพื่อบังคับ MFA | flatten scoped permissions ลง session permissions ที่ใช้ได้ทุกพื้นที่ |
| Portal | เลือกพื้นที่และแสดงผล API | เป็น authority ของสิทธิ์หรือเก็บ cookie/token ใน JavaScript |

ตำแหน่งโค้ดเป้าหมายคือ `backend/src/TPR10.Api/Organization/`, `backend/src/TPR10.Api/Scopes/` และ `app/portal/` โดยใช้ DbContext/audit/session เดิม ไม่แยก service deployment ใหม่ในรอบนี้

จุดเชื่อมที่ตรวจจากโค้ดจริง: `Identity/Sessions/LoginService.cs` และ `SessionService.cs` อ่าน MFA role class จาก `user_roles`; `PermissionHandler.cs` และ `PermissionMutationGuard.cs` ตรวจ global role grants; `AuditEventWriter.cs` มี metadata allowlist ซึ่งยังไม่มี filter/row-count/destination ของ export ต้องเพิ่มแบบจำกัด ไม่ปิด validation เดิม

## 8. ลำดับคำขอและขอบเขต API

### 8.1 Contract ของ scope

ใช้ route identifier เป็น requested scope ไม่รับ scope จาก header หรือ body เป็น authority เส้นทาง technical records แยก Workspace, Project และ Site อย่างชัดเจน ภายใต้ `/api/v1/workspaces/{workspaceId}` แล้วต่อ `/projects/{projectId}` และ `/sites/{siteId}` ตามระดับ ก่อน `/scope-probe-records`

ทุกระดับรองรับ GET list, GET detail, POST create, PATCH update และ POST `export-simulation` ซึ่งคืน JSON bounded dataset ไม่เขียนไฟล์และไม่เป็น report job Body ของ record ไม่มี user_id/workspace_id/project_id/site_id สำหรับเปลี่ยนเจ้าของหรือ scope

API control plane แยก `/api/v1/organization/...` สำหรับโครงสร้าง และ `/api/v1/scope-assignments/...` สำหรับ assignment ไม่ใช้สิทธิ์ control plane แทนสิทธิ์ของ technical records

`GET /api/v1/scopes` คืนเฉพาะ exact tuples ที่ผู้ใช้เข้าได้พร้อมชื่อ parent ขั้นต่ำและ capabilities ของแต่ละ tuple ไม่คืนรายชื่อผู้ใช้หรือ assignments ของผู้อื่น และไม่รวม capabilities ลง `SessionView.Permissions` แบบ global

### 8.2 การอ่าน/เขียนและข้อผิดพลาด

1. ตรวจ session/account/stage ผ่าน middleware เดิม; unsafe methods ต้องผ่าน CSRF และ Origin/Host
2. resolve hierarchy และ assignment จาก DB; ตรวจ capability จาก role ใน exact tuple และ MFA ตาม policy
3. สร้าง ScopeContext ที่ใช้เฉพาะ request และ query repository ด้วย exact tuple ตั้งแต่ SQL predicate
4. Write ต้องตรวจ session/permission/assignment ซ้ำภายใน transaction ก่อนแก้ record รวม version ของ record
5. DTO แยก public/restricted fields; ผู้ไม่มี restricted-read ไม่ได้รับ field นั้นใน JSON รวม list/detail/export ไม่ใช่แค่ซ่อนในหน้าเว็บ
6. POST/PATCH ที่ส่ง restricted field โดยไม่มี restricted-read พร้อม write ถูกปฏิเสธ ไม่ silently discard แล้วรายงานสำเร็จ
7. ไม่มี session/ถูก revoke ตอบ 401; stage/MFA ไม่ครบหรือไม่มี capability ใน scope ที่เข้าถึงได้ตอบ 403
8. Scope ไม่มีอยู่/ไม่ถูกมอบหมาย/ไม่ active หรือ record อยู่นอก scope ตอบ 404 รูปแบบเดียวกัน ไม่เปิดชื่อ owner, count หรือรายละเอียด existence
9. UUID/โครงสร้าง request ผิดตอบ 400; duplicate/version conflict ตอบ 409; audit หรือ dependency ใช้งานไม่ได้ตอบ 503 แบบ sanitized ไม่ส่ง partial result
10. List ใช้ page/pageSize มีลำดับคงที่ default25/max100 และ count ภายใน exact scope เท่านั้น; offset overflow ถูกปฏิเสธ 400
11. Export-simulation จำกัดสูงสุด100 records หากผลเกินเพดานตอบ400 ให้ลดช่วง ไม่ truncate เงียบ; filter จำกัด createdFrom/createdTo UTC ที่ตรวจช่วงเวลาแล้ว ไม่รับ arbitrary expression หรือค้นจากเนื้อหา note
12. ทุก sensitive response มี no-store/correlation ID; ไม่แคชข้อมูลตาม URL โดยละเลยตัวผู้ใช้

## 9. MFA, Revocation และคำขอพร้อมกัน

- Scoped role class ที่เป็น approval/accounting/finance-data-access ทำให้ MFA เป็นข้อบังคับเช่นเดียวกับ global privileged role ผู้มี privileged assignment อย่างน้อยหนึ่งรายการต้องผ่าน MFA ใน login flow ไม่รอให้เข้าพื้นที่นั้นก่อน
- ไม่ลด MFA ของผู้ที่ลงทะเบียนไว้แล้ว และคง recent assurance 15 นาทีสำหรับ privileged actions ตาม Module 2
- Grant/revoke/replace assignment และ role-grant mutation ต้อง invalidate sessions ของผู้ได้รับผล เพื่อไม่ให้ login stage หรือ navigation ใช้ข้อมูลเก่า การเปลี่ยน role grants ต้องหา affected users จากทั้ง user_roles และ active scoped assignments
- เพิ่ม effective-role/MFA boundary ที่ Identity เรียกได้ผ่าน interface ไม่ให้แต่ละจุดคัดลอก query global-only เดิม และตรวจทุก transition ที่เลือก session stage รวม forced password change/recovery
- Session permissions เดิมยังมีความหมายเป็น system permissions; business capability ต้องเรียก scope discovery/authorization แยก
- ใช้ transaction guard กับลำดับ lock ร่วมกับ Module 2; MVP ใช้ identity advisory lock เดิมก่อน user/session/assignment/record locks เพื่อกำหนดลำดับ revoke กับ sensitive operation ที่ทดสอบได้ ไม่สร้าง lock order สวนกัน
- Read/export ของ technical records resolve authorization และ materialize DTO ภายในขอบเขต transaction/lock ที่สอดคล้องกับ revoke ไม่คืน IQueryable ให้ materialize ภายหลัง
- หาก revoke commit ก่อน operation ตรวจสิทธิ์ในขอบเขตที่ serialize แล้ว operation ต้องไม่สำเร็จ; หาก operation ได้สิทธิ์และจบ transaction ก่อน revoke จะถือว่าเกิดก่อน revoke ไม่อ้างว่าสามารถดึง response ที่ส่งไปแล้วกลับคืนได้
- เมื่อ audit ล้ม mutation, version change และ session revoke ต้อง rollback ทั้งชุด สำหรับ denial/read/export ที่กำหนดให้ audit ต้องไม่ส่งข้อมูลก่อนบันทึกสำเร็จ
- แนวทาง lock นี้เน้น correctness ของ MVP ต้อง benchmark ก่อน production; ไม่รับรอง throughput หรือหลาย replica โดยไม่มีหลักฐาน

## 10. Audit และการไม่เปิดเผยข้อมูล

ใช้ `SecurityAuditRequest` เดิมให้ครบ actor, acting role, workspace/project/site, action, target, outcome และ correlation ไม่ใช้ชื่อ role เป็น security decision

เหตุการณ์ที่ต้องมี: organization create/update/deactivate/reactivate, assignment grant/revoke/replace, scope access denied, technical record create/update/read/list และ export-simulation

- Mutation และ success audit อยู่ transaction เดียวกัน; denied audit ไม่ commit business change
- Scope ที่ร้องขอผิดหรือไม่มีสิทธิ์ให้ระบุใน audit ว่าเป็น requested/unverified identifiers ไม่ปลอมว่าเป็น authorized context
- Export audit เก็บ normalized filters แบบ allowlist, row count, destination type=`response-json` และ scope ที่อนุญาตแล้ว
- ไม่เก็บ record body, restricted note, session cookie, CSRF token, password หรือ MFA secret ใน audit/log
- เพิ่ม metadata keys ที่จำเป็นแบบเจาะจง เช่น assignment-id, filter-from, filter-to, row-count, destination-type และ scope-validation; ไม่เปิด arbitrary client metadata
- เหตุผลการเปลี่ยนสิทธิ์เป็นข้อความจำกัดความยาว ไม่รับ control characters และเตือนห้ามใส่ข้อมูลลับ การตรวจนี้ไม่ใช่ DLP

## 11. หน้าเว็บและประสบการณ์ผู้ใช้

- คง Landing/Login/Portal และพอร์ต4000/4001 เดิม
- ผู้ไม่มี assignment เห็นสถานะ “ยังไม่ได้รับมอบหมายพื้นที่” ไม่แสดงรายชื่อโครงการทั้งหมด
- ตัวเลือกพื้นที่แสดงเฉพาะ exact tuples ที่ API คืนมา พร้อมระดับ Workspace/Project/Site ที่แยกชัดเจน
- เปลี่ยนพื้นที่เป็น navigation ที่มี identifier ใน route ไม่สร้าง session ใหม่หรือเปลี่ยน cookie เป็นแหล่งอำนาจของ scope
- หน้าจอ organization/assignment เปิดเฉพาะผู้มี management capability; API ตรวจซ้ำและต้อง MFA
- Assignment form เลือกพื้นที่ ผู้ใช้ บทบาท และเหตุผล; ไม่เลือกตนเอง ไม่ใส่ System Administrator เป็น business role และแสดงผลการ invalidate sessions ให้ทราบ
- ไม่มี role/permission editor ใหม่ซ้ำกับ identity API; fixture/runbook ใช้ API เดิมตั้ง role grants ตาม permission domain ที่กำหนด
- ข้อมูล sensitive fetch แบบ no-store; เมื่อเปลี่ยนบัญชี/พื้นที่/logout ต้องไม่แสดงข้อมูลเดิมจาก client cache, history หรือผล request เก่าที่ตอบช้า
- Production ไม่เปิด technical record endpoints หรือหน้าจอ technical data; production UI มี selector/administration ตามสิทธิ์ ส่วน fixture Development/Testing มีหน้า technical record สำหรับ browser acceptance
- หน้าจอและข้อความผิดพลาดภาษาไทย ใช้ keyboard/mobile ได้ ไม่ใช้สีอย่างเดียวสื่อระดับสิทธิ์

## 12. เกณฑ์ทดสอบและตรวจรับ

| กลุ่ม | หลักฐานที่ต้องมี |
| --- | --- |
| Hierarchy/database | composite FK ข้าม Workspace/Project ล้มจริง, null-level CHECK, active assignment uniquenessทั้งสามระดับ, migration apply/rollback/reapply โดย auditเดิมไม่หาย |
| Exact assignment | Project ไม่อ่าน Site, Site ไม่อ่าน sibling/parent records, Site ใหม่ไม่ inherit, revoked/inactive assignment ใช้ไม่ได้ |
| Scoped roles | คนเดียว Approver ที่ A/Staff ที่ B ไม่ใช้ capability ข้ามพื้นที่; global Administrator ไม่มี business bypass; หลาย role รวมเฉพาะ exact scope |
| Management boundary | ไม่มี management capability/MFA ถูกปฏิเสธ; scoped role ที่มี system permission ไม่เปิด control plane; self-assignment ถูกปฏิเสธ |
| MFA transitions | scoped privileged role บังคับ enrollment/challenge ทั้ง login/session/forced-change/recovery paths; เพิ่ม role ระหว่าง session ไม่ให้ใช้ stage เก่า |
| IDOR/data visibility | list/detail/write/export ข้ามscopeและrecordIDผิดtupleไม่รั่ว; count/filterอยู่scope; restricted fieldไม่ปรากฏเมื่อไม่มีสิทธิ์; bodyปลอมactor/scopeไม่ได้ |
| Concurrency | ใช้ PostgreSQLจริงกับ controlled barriers ไม่พึ่งsleep: revokeก่อน/หลังread/write/export, rolegrantchangeระหว่างrequest, concurrentduplicateassignment/versionconflict |
| Audit failures | auditล้ม rollback assignment+securityversion+session+record ทั้งชุด; denial/read/exportไม่ปล่อยข้อมูลเมื่อauditล้ม |
| Lifecycle | deactivateparent/disableaccount revokeผู้ได้รับผล; reactivateไม่คืนสิทธิ์เดิม; rolegrantchangeกระทบผู้ใช้scopedด้วย |
| Browser | สลับscope/บัญชี, unauthorizeddeep link, logout/back, responseเก่ามาช้า, ไม่มีassignment, mobile/keyboard ผ่าน HTTPSจริง |
| Regression/contracts | Module2 suiteเดิมไม่เสีย; OpenAPIทุกrouteระบุ CSRF/cookie/capability/MFA/scope/errorและpagination; technicalroutesไม่เปิดProduction |

ต้องทำ TDD, review อิสระ และรัน Test/Build/Lint รวม E2E ก่อนแจ้ง implementation เสร็จ ผลตรวจต้องเป็นของ commit/worktree ที่ส่งมอบ ไม่ใช้ผล Module 2 แทนผล Module 3

## 13. Gate ที่แยกจากกัน

1. **Design gate:** ผู้ใช้ตรวจและอนุมัติเอกสารฉบับนี้ ก่อนจัดทำ Implementation Plan
2. **Plan gate:** แผนต้องระบุไฟล์/interface/migration/test code/commands และลำดับงานที่ทำได้ทีละ Task; ใช้วิธี Native/inline ที่ผู้ใช้เลือกไว้เดิม เว้นแต่ผู้ใช้เปลี่ยน
3. **Technical gate:** ทุก acceptance ผ่าน ไม่มี Critical/Important ค้าง และบันทึก Minor ตามจริง
4. **Policy gate:** System/Business/Security owner อนุมัติ scope matrix, role grants, data-type/field visibility และการมอบหมายผู้ดูแลด้วยข้อมูลทดสอบ ไม่ใช่ credential จริง
5. **Production gate:** ยังต้องผ่านข้อค้าง Module 2, Operations, capacity และ deploymentจริง การอนุมัติ design หรือ push Git ไม่แทน gate นี้

## 14. ผลตรวจทานเอกสารด้วยตนเอง

- Exact scope และ role isolation เชื่อมกับ baseline6/8 และ acceptance20.2 โดยตรง ไม่เพิ่มสิทธิ์ผ่าน null/parent/globalrole
- ระบุ MFA และ role-grant revocation จุดเชื่อมModule2 รวมSessionService/LoginService ไม่จำกัดเฉพาะ endpointใหม่
- แยก control plane ที่เห็น organization metadata จาก data plane ที่ต้องassignment; ห้ามself-grantเป็นข้อเสนอที่ผู้ใช้ต้องตรวจในฉบับนี้
- ระบุ Departmentเป็นmetadata, scope discoveryไม่เปิดbusinessparent และproductionปิดtechnicalrecords เพื่อลดการตีความต่างกัน
- ScopeContextไม่ใช่หลักฐานสิทธิ์ถาวร มีtransactionrecheck/orderingและขอบเขตresponseที่ย้อนคืนไม่ได้
- Export-simulation/fieldvisibilityเป็นtechnicalacceptance ไม่กล่าวอ้างว่าระบบรายงานหรือbusinessworkflowเสร็จ
- ไม่มี production code, migration หรือ dependency ใหม่ในงานจัดทำเอกสารนี้ ยังไม่ได้รัน tests/build/lint ของ Module3 เพราะยังไม่มี implementation

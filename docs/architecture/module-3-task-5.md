# Module 3 — Task 5: สิทธิ์แบบ exact scope และรายการพื้นที่ของผู้ใช้

สถานะ: ผ่านการตรวจรับขอบเขต Task5 แล้ว ยังไม่ใช่การรับรองทั้ง Module3 พร้อม production

## สิ่งที่ส่งมอบ

- `ScopeContext` เป็น immutable context ที่ API สร้างเองจาก session และ assignment ปัจจุบัน ไม่รับ actor/role/scope จาก header เป็นหลักฐานสิทธิ์
- `ScopeAccess` ตรวจ shape → session/account/security version/expiry/idle → stage/forced password change/MFA → exact assignment และ active ancestry → business capability ภายใน transaction หลัง advisory lock 7241002
- Workspace, Project และ Site เป็น exact nullable tuple; null ไม่ใช่ wildcard, ไม่สืบทอดสิทธิ์ parent/sibling และ global Administrator ไม่มี business bypass
- หลาย role รวมได้เฉพาะ business capabilities ใน tuple เดียวกัน; เลือก acting role/assignment อย่างคงที่ ไม่ใช้สิทธิ์ระบบจาก scoped role
- Restricted visibility ต้องมี restricted-read ใน exact scope และ recent MFA; export ต้อง recent MFA เช่นกัน ผู้ที่ assurance หมดอายุยังถูกจำกัดตาม stage policy ของ Module2
- `ScopeOperation` เป็นเจ้าของ transaction: ตรวจสิทธิ์ → callback materialize → save/audit/commit → คืนผล; callback ตอบ error ต้อง rollback แม้ callback เคย SaveChanges แล้ว จากนั้น clear tracked changes และบันทึก denial ใน transaction ใหม่
- Audit/dependency failure ที่รู้จักตอบ 503 แบบ sanitized ไม่คืน partial data; programming exception ไม่ถูกเหมารวมเป็น dependency failure และ transaction ยังคง rollback
- `GET /api/v1/scopes` ต้องมี authenticated Active session ไม่ต้องมี management capability คืน `items,total,pageNumber,pageSize` และรายการ `scope,workspaceName,projectName,siteName,capabilities` เท่านั้น
- Discovery dedupe exact tuples ก่อน pagination, default25/max100, stable order, offset overflow400; ไม่มี assignment คืน200พร้อมรายการว่าง รวม Administrator; query capabilities ไม่ใช้ N+1 และอ่านเฉพาะหน้าที่ร้องขอ
- Discovery ถือ lock ตรวจ session ซ้ำและ commit audit ก่อนคืนรายการ; invalid pagination รวมข้อความที่แปลงเป็นเลขไม่ได้ผ่าน denial audit; มี no-store และ correlation ID ใน header/ProblemDetails ของ service
- OpenAPI เพิ่ม scope-discovery โดยรักษาสัญญา identity26, organization12 และ assignment6 เดิม

## หลักฐาน TDD และการตรวจรับ

- Baseline23/23 ผ่าน
- RED52กรณีของ authorization/discovery หลังแก้ fixture system catalog; มี scaffolding ที่คืน501เพื่อพิสูจน์ behavior ก่อน implementation
- GREENรอบแรก108/110 อีกสองข้อคือ OpenAPIยังidentity-only และ fixture expiryเท่ากับcreationขัด CHECK; แก้ contract และปรับ clock ของ fixture โดยไม่ลด constraint
- CorrelationId body RED1 ก่อนเพิ่ม HTTP boundary extension
- Focusedสุดท้าย119/119 ผ่าน รวม barrier/exception/forged identifiers เพิ่มเติม ไม่มี skipped
- Mutation check ที่เชื่อ RequestSession snapshot โดยไม่ revalidate DB ทำให้12/12 regression testsล้ม รวม expired session และ revokeระหว่างรอlock; คืนโค้ดแล้วก่อน verification
- Node24 tests28/28, Lint และ frontend Build ผ่าน
- E2E13/13 และ HTTPS acceptance (TLS/cookie/CSRF/host) ผ่านที่พอร์ต4001; เป็น identity regression ยังไม่ใช่ browser acceptance ของ scope UI ที่ยังไม่สร้าง
- Code Review อิสระโดย Lagrange ไม่พบ Critical/Important; Minor2ข้อบันทึกไว้ด้านล่าง
- Full backend638/638 ไม่มี skipped ใช้เวลา11นาที2วินาที; backend Build0warnings/0errors และ `dotnet format --verify-no-changes` ผ่าน exit0
- ตรวจรอบนี้เมื่อ 2026-09-23 UTC ใน worktree `module-3-organization-scope/TPR10` บน branch `codex/module-3-organization-scope` จากฐาน `e3ba105`; ไม่มี production code เปลี่ยนหลัง review/E2E/full suite

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

ใช้ .NET10.0.401 และ Node24.19.0; log รอบนี้ `/private/tmp/tpr10-m3t5-*.log`

## การตัดสินใจและขอบเขต

- ใช้ worktree/dependencies เดิม ไม่แก้ main ไม่ upgrade package และไม่สร้าง schema/migration ใหม่
- เพิ่ม OpenAPI contract ตั้งแต่เปิด route ตาม Global Constraints แทนการรอ Task9; หากไม่ทำ client จะเข้าใจ boundary ผิด
- Query page/pageSize รับข้อความและแปลงด้วย invariant integer ภายใน boundary เพื่อให้ invalid binding ถูก audit โดยไม่อ่าน body; ค่าผิดส่ง sentinel0เข้า validator
- MFA ของ Module2 ยังบังคับ stage ทั้ง session ไม่ผ่อนให้ ordinary read หลัง assuranceหมดอายุ; หากลดนโยบายจะขัด session contractเดิม
- Caller ของ ScopeAccess ต้องถือ transaction/lock; ScopeOperation/Discoveryทำให้เอง ส่วน callback ต้อง materialize DTO ไม่ stream/lazyquery และห้าม commit transaction เอง; Task6–7 ต้องรักษาสัญญานี้
- Denialจาก callbackไม่เก็บข้อความหรือpayloadของcallback ตอบ sanitized status และทำ auditใหม่หลังrollback เพื่อไม่ commitpartialbusinessstate
- Scope discovery auditไม่กำหนด actingRole/scopeเทียมเพราะเป็นหลายtupleไม่ใช่businessoperation; สำเร็จใช้ actorของsession
- ตรวจอิสระเฉพาะ Task5 ตามการส่งมอบ ไม่แทน whole-branch reviewTask9; testsรันโดยผู้พัฒนา ไม่อ้าง independentruntimeสองชุด
- ยังไม่ทำ repository/record/export API (Task6–7), selector/UI/cache (Task8), whole-branch acceptance/runbook/production benchmark (Task9และproductiongate)
- MinorเดิมTasks1–4ยังคงติดตามแยก งานbarrierTask5รับรอง scoped operation/discovery ไม่ใช่ AssignmentService จึงไม่ปิดMinorTask4แทน
- พอร์ต dev4000/prod4001คงเดิม ยังไม่ push/merge และไม่เริ่มTask6อัตโนมัติ
- Commit เฉพาะ Task5 ในเครื่องหลังตรวจครบ; ขั้นต่อไป Task6 คือ scoped repository และ technical record API โดยรอผู้ใช้สั่งเริ่ม

## Minor ที่เลื่อนติดตาม

1. OpenAPI ของ discovery ระบุสถานะ503แล้ว แต่รายการ `x-tpr10-problem-types` ยังขาด `urn:tpr10:scope-unavailable-service`; client ที่ใช้ HTTP status ยังทำงานได้ ต้องเพิ่ม metadata และ assertion ให้ครบ
2. Test export ข้าม sibling scope ยังขาด recent MFA จึงได้403ก่อนตรวจcapability ทำให้ assertionนี้ยังไม่แยกสาเหตุ; queryปัจจุบันเทียบexacttupleถูกต้อง ควรเพิ่มpositivecontrol MFA+exportในsibling แล้วตรวจtargetยัง403

ข้อสองเป็นช่องว่าง regression coverage ไม่ใช่การพบ authorization bypass; Minorทั้งสองยังไม่ถูกแก้ในรอบนี้ตามกระบวนการ executing-plans

## คำตัดสินต่อขอบเขตที่ผู้ตรวจยังไม่รับรอง

| ประเด็น | คำตัดสินและผลหากนำไปใช้เกินหลักฐาน |
| --- | --- |
| Record-ID forgery, HTTP scope binding, repository predicates, restricted output/write fields | เป็นTask6; Task5รับรองevaluatorเท่านั้น หากอ้างว่าจบdata-planeตอนนี้จะเกินสิ่งที่ทดสอบ |
| Read/write/export แข่งกับrevoke/deactivate/role mutation ทั้งสองcommit orders | เป็นTasks6–7; barrierรอบนี้รับรองเฉพาะsession revokeก่อนoperationตรวจDB หากละเลยขั้นต่อไปอาจพลาดraceของcallerจริง |
| Auditเฉพาะrecord/target/rowcount/filter/exportlimit/versionrollback | เป็นTasks6–7; sharedtransactionผ่านแต่ไม่แทนoperationเฉพาะ หากไม่ทำจะขาดหลักฐานaudit/exportจริง |
| Callback materialization และห้ามจัดการtransactionเอง | เป็นcaller contractที่Task6ต้องตรวจ; explicitstatusไม่พิสูจน์eagerDTOโดยอัตโนมัติ หากcallerละเมิดอาจอ่านหรือส่งข้อมูลนอกขอบเขตlock |
| ScopeAccessตรวจtransactionแต่ไม่ตรวจadvisorylock ownership | currentownersถือlockถูกต้อง; futurecallerต้องรักษาลำดับ หากเรียกตรงผิดสัญญาอาจเกิดrace |
| UI selector/cache/delay/back/accessibility | เป็นTask8 ยังไม่มีUIใหม่ หากอ้างbrowser scopeacceptanceจากidentityE2Eจะเกินหลักฐาน |
| รับรองTasks1–4ใหม่ทั้งหมด | นอกTask5 ตรวจเฉพาะintegration; Minorเดิมยังคงอยู่ ไม่ถือว่าปิดข้อค้างย้อนหลัง |
| Paginationหลายrequest | ไม่รับรองsnapshotคงที่ข้ามหน้าเมื่อassignmentเปลี่ยน แต่แต่ละrequestตรวจสิทธิ์ปัจจุบัน; clientควรrefreshรายการเมื่อมีการเปลี่ยนสิทธิ์ |
| Capabilitiesในdiscovery | หมายถึงgrantsไม่ใช่รับประกันว่าMFAพร้อมทุกoperation; APIตรวจซ้ำเสมอ clientต้องรองรับ403/MFA |
| Capacity/multireplica/deployment/recovery/directSQL/owner sign-off | เป็นproductiongate ไม่ใช่Task5; sharedlockเน้นcorrectness ยังไม่รับรองthroughputหรือproductionpolicy |
| Runtimeverificationของreviewer | reviewerตรวจstaticเท่านั้น ผู้พัฒนาเป็นผู้รันคำสั่งจริง ไม่อ้างหลักฐานruntimeสองรอบอิสระ |
| Whole-branch acceptance | ยังต้องTask9 ไม่รวมpush/mergeและไม่อนุมัติproduction |

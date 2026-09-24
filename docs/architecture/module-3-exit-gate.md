# Exit Gate Module 3 — Organization และ Exact Scope

## สถานะ ณ รอบ Task 9

กำลังตรวจรับ: **ยังไม่ประกาศ Technical gate ผ่าน** จนผล full verification และ whole-branch review ครบ Policy/Production gate ยังรอผู้รับผิดชอบอนุมัติ เอกสารนี้เป็นฉบับทำงานและจะเติมผลจริงก่อนส่งมอบ

งานอยู่ใน `codex/module-3-organization-scope` ที่ `/Users/theerapat_k/.codex/worktrees/module-3-organization-scope/TPR10` ฐาน Module3 คือ `99523a868a4465ef9215b2054e4066a0ed84cd2e`; เริ่ม Task9 จาก `3256b6f62d3e147b401e4af63643e72fa8626004` ยังไม่ push/merge/deploy และไม่แตะงานค้างใน main

อ้างอิง [Spec](../superpowers/specs/2026-09-24-module-3-organization-scope-design.md), [แผน](../superpowers/plans/2026-09-24-module-3-organization-scope.md), [คู่มือปฏิบัติงาน](../runbooks/module-3-organization-scope.md) และ [ข้อค้าง Module2](module-2-exit-gate.md)

## สิ่งที่ส่งมอบ

Tasks1–8 สร้าง hierarchy/composite constraints, exact assignments/history, system/business domain, scoped MFA+revocation, discovery, record/field/export boundaries, atomic audit, PostgreSQL controlled race tests และ Portal/administration/technical UI

Task9 เพิ่ม `ScopeEndpointMetadata(Mode,Capability,RequireMfa,Level)` และ transformerแยกด้านเอกสารซึ่ง Identity transformerเรียกโดยตรง ไม่แข่งลำดับ registration ไม่สร้าง authorityใหม่ และคง runtime `ScopeProbeBoundary` สำหรับ audit; `system-management`/`scope-discovery` level none ไม่ใช่ business wildcard ส่วน `exact-business` ระบุ workspace/project/site

OpenAPI enumerate34 Module3 operations ใน Development: organization12 + assignments6 + discovery1 + records15; Productionเหลือ19 operationsด้านบริหาร/discovery ไม่แสดงprobes และคง26 identity operationsเดิม ระบุ cookie/CSRFไม่ซ้ำ, capability/MFA, pagination, restricted omission/absent-null-string, write-only variant และ generic500/defaultที่อาจไม่มีJSON พร้อม discovery503 URNที่เคยขาด

TDD: contractใหม่45กรณี REDตาม metadata/schemaที่ขาด → focused118ผ่าน; เพิ่ม discovery503 contract RED1เพราะ URNหาย → focused119ผ่าน ไม่มีการเปลี่ยน authorization/transaction เพื่อทำให้เอกสารผ่าน

## แผนที่หลักฐานตรง Spec ข้อ12

ชื่อด้านล่างอยู่ใน `backend/tests/TPR10.Api.IntegrationTests` เว้นแต่ระบุ browser; ผล fullsuiteต้องอ่านจากหัวข้อผลตรวจ ไม่ถือเพียงมีชื่อtestว่าได้ผ่านแล้ว

| กลุ่มตรวจรับ | หลักฐานจริง |
| --- | --- |
| Hierarchy/database | `ScopeSchemaTests.Nullable_levels_reject_dangling_workspace_or_cross_workspace_project`, `Database_rejects_invalid_hierarchy_or_metadata`, `Active_assignment_is_unique_at_each_level_but_regrant_preserves_history`, `Upgrade_roundtrip_preserves_identity_session_and_immutable_audit` |
| Exact assignment | `ScopeRecordSafetyTests.Cross_scope_patch_and_new_site_never_inherit_parent_or_header_scope`, `ScopeExportTests.Export_filters_exact_nullable_tuple_before_cap_and_uses_inclusive_exclusive_utc` |
| Scoped roles | `ScopeAuthorizationTests.Global_roles_never_bypass_assignment`, `ScopeExportTests.Fresh_login_after_role_change_preserves_exact_scope_and_current_field_visibility`, `ScopedMfaTests.Global_session_never_flattens_business_permissions` |
| Management boundary | `AssignmentApiTests.All_routes_require_system_management_and_recent_mfa`, `Administrator_cannot_modify_own_assignments`, `ScopedMfaTests.Scoped_management_permission_never_opens_global_management_or_counts_as_last_admin` |
| MFA transitions | `ScopedMfaTests.Scoped_privilege_requires_enrollment_without_global_privilege`, `Forced_password_change_returns_to_scoped_mfa_policy_on_next_login`, `Scoped_mfa_expires_at_fifteen_minutes_and_recovery_never_supplies_assurance`, `Newly_granted_privilege_rejects_old_unverified_cookie` |
| IDOR/field visibility | `ScopeRecordTests.Detail_cannot_load_foreign_record_by_known_id`, `ScopeRecordSafetyTests.Invalid_or_forged_body_is_audited_without_inserting`, `Restricted_capability_without_recent_mfa_does_not_expose_field`, `ScopeExportTests.Export_permission_does_not_need_read_but_restricted_visibility_is_separate` |
| Concurrency | `ScopeRaceTests.Http_operation_and_assignment_revoke_follow_lock_commit_order`, `Read_write_export_serialize_with_lifecycle_and_invalidate_real_cookies`, `Concurrent_same_expected_version_has_exactly_one_winner`, `AssignmentAtomicityTests.Concurrent_duplicate_or_version_requests_have_one_atomic_winner` |
| Audit failures | `AssignmentAtomicityTests.Audit_failure_rolls_back_assignment_versions_and_target_session`, `ScopeAuditFailureTests.Failed_audit_never_sends_data_or_changes_records_and_session_security_state`, `Lifecycle_audit_failure_rolls_back_assignment_grants_parents_and_live_sessions`, `Export_commit_failure_never_serializes_prepared_records` |
| Lifecycle | `ScopedIdentityLifecycleTests.Disable_revokes_assignments_and_sessions_and_enable_never_restores_them`, `Role_grant_change_revokes_scoped_user_exactly_once_even_with_global_membership`, `Scope_lifecycle_cascades_only_below_target_and_leaves_commit_and_sessions_to_caller` |
| Browser | `tests/e2e/scopes.spec.ts`: ไม่มีassignment/deep-link, staffไม่มีrestricted+logout/back/account switch, A/B+late response, กลับแท็บล้างDOM/export, selectorเกิน100+keyboard/mobile, adminไม่มีbypass, manageroptions/self-grant, multiple roles/no-op, POST/PATCH/version/503; `scopes-boundary.spec.ts` ปิดtechnicalUI |
| Regression/contracts | fullsolutionรวม Module2; `ScopeOpenApiTests`, `ScopeRecordOpenApiTests`, `IdentityOpenApiTests`, `ScopeRecordSafetyTests.Production_does_not_map_or_document_record_probes`, `ScopeExportTests.Production_does_not_map_or_document_exports`; HTTPS `identity.spec.ts` |

## ผลตรวจคำสั่ง

รอบก่อนreview ตรวจเสร็จ `2026-09-24T15:38Z`: `dotnet restore`ผ่าน; fullsuite828/828 ไม่มีskipped ใช้16นาที52วินาที; Build0warnings/0errors, format verifyผ่าน; NuGetรวมtransitiveไม่พบvulnerablepackagesจากsourceที่ใช้ รอบนี้เริ่มก่อนเติมdiscoveryURNหนึ่งtest จึงตรวจdeltaด้วยfocused119/119และต้องรันfinalfull829ก่อนส่งมอบ ไม่อ้าง828เป็นผลfinal Runtimeทดสอบ Node24.19.0, SDK10.0.401, PostgreSQL containersทิ้งได้; ไม่มีอัปเกรดdependenciesเพื่อหลบผลตรวจ

Frontendรอบก่อนreview: `npm ci`, `npm test`33/33ไม่skip, `npm run lint`, `npm run build` สำเร็จ; HTTPS dev4000 identity13/scopes9 และ prod4001 identity13/scopes9/boundary2 ผ่าน; `npm audit` พบ0 vulnerabilities ณ เวลาตรวจ ไม่รับรองความปลอดภัยของ dependencyตลอดไป

`npm ci` แจ้ง deprecated packages6รายการ (inflight, config-array, rimraf, object-schema, glob, eslint) แม้ auditคืน0 จึงไม่อ้างว่าไม่มีwarningหรือทุกdependencyได้รับการสนับสนุน ยังไม่มีการเปลี่ยนpackage/lockfileในTask9

คำสั่งตรวจซ้ำจากรากworktree (ตั้งPATHให้ตรงNode/.NETที่ติดตั้งและเปิดDockerก่อน):

```sh
dotnet restore backend/TPR10.sln
dotnet test backend/TPR10.sln
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm ci
npm test
npm run lint
node infra/nginx/smoke-identity-https.mjs 4000 --e2e
node infra/nginx/smoke-identity-https.mjs 4000 --e2e --spec tests/e2e/scopes.spec.ts
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes.spec.ts
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes-boundary.spec.ts
dotnet list backend/TPR10.sln package --vulnerable --include-transitive
npm audit
git diff --check
```

Dev E2Eต้องจบก่อนbuild แล้วจึงprod E2E ห้ามมีหลายprocessเขียน `.next` พร้อมกัน Harnessใช้CAที่เชื่อถือเฉพาะprofileทดสอบ ไม่มีTLS bypassและปิดserver/containerของตนเมื่อจบ ไม่แตะฐานธุรกิจ

## คำตัดสินระหว่าง Task9

| คำตัดสิน | เหตุผลและผลหากผิด |
| --- | --- |
| ใช้spec/planและworktreeที่อนุมัติแล้ว ไม่เริ่มTasks1–8ใหม่ | รักษางานmain; หากฐานผิดอาจตรวจคนละโค้ด จึงเทียบbranch/SHA/ledgerก่อนทำ |
| Metadataใหม่เป็นเอกสาร เรียกScope transformerผ่านIdentityโดยตรง | ไม่เปลี่ยนอำนาจruntimeหรือพึ่งregistrationorder; หากmetadataไม่ตรงclientจะเข้าใจสิทธิ์ผิด ใช้34literalcontractsตรวจ |
| Control plane/discoveryใช้level none | ไม่สื่อว่าparentrouteเป็นbusinessassignmentหรือnullwildcard; runtimeยังตรวจparentและสิทธิ์จริง |
| เก็บScopeProbeBoundaryสำหรับbindingaudit | ไม่ปะปนmetadataเอกสารกับenforcement; ต้นทุนคือmetadataสองชนิดที่ต้องคงสอดคล้อง |
| ระบุrestrictedrequestschemaและ500/defaultเฉพาะModule3 | ปิดขอบเขตTask9 ไม่อ้างปิดMinorModule2ทั้งหมด; consumerที่ถือว่าทุกerrorJSON503ยังต้องแก้ |
| ปิดdiscovery503 URNเดิมด้วยTDD | เป็นerrorcontractที่Task9รับผิดชอบโดยตรง ไม่ขยายไปแก้runtimeMinorอื่น; fullsuiteรอบแรกยัง828จึงต้องตรวจfinal829 |
| คู่มือมนุษย์ตรวจเทียบโค้ด ไม่ทำtestgrepข้อความ | testsอยู่ที่HTTP/schemaจริง; ยังต้องownerทบทวนขั้นตอนdeploymentก่อนใช้งาน |

คำตัดสิน Tasks1–8 และขอบเขตreviewของแต่ละงานคงอยู่ในรายงาน [Task1](module-3-task-1.md), [Task2](module-3-task-2.md), [Task3](module-3-task-3.md), [Task4](module-3-task-4.md), [Task5](module-3-task-5.md), [Task6](module-3-task-6.md), [Task7](module-3-task-7.md), [Task8](module-3-task-8.md) ไม่ถือว่าreviewรายงานเดิมแทนwhole-branchreview

## ข้อค้างเดิมที่ไม่ถือว่าหายเพราะ suite ผ่าน

| ที่มา | สถานะ/ผลกระทบ |
| --- | --- |
| Task1 migration roundtrip | ยังไม่เทียบ session ID/hash/stage/expiry และ identity/auditทุกfieldก่อน–หลัง; จำนวนแถวเท่าเดิมอาจซ่อนการเปลี่ยนค่า |
| Task2 affected-users | ยังไม่มี regressionแยกผู้ใช้scoped-onlyใต้inactive ancestor; โค้ดปัจจุบันไม่มี active-ancestor filterที่ผิดจุด |
| Task3 Organization race | sessionถูกถอนระหว่างmiddlewareกับserviceอาจได้403แทน401; ปิดสิทธิ์และauditแล้ว แต่การแสดงผลต่างจากหมดsessionปกติ |
| Task4 Assignment | ยังไม่มี barrierถอนmanager permissionระหว่างรอlockเฉพาะAssignmentService และ list exactProject/Site/sibling+userfilter assertionsครบทุกมิติ; testTask7 data-planeไม่แทนmanagementนี้ |
| Task5 discovery503 URN | ปิดใน Task9ด้วย RED→GREENของ `Discovery_documents_its_dependency_failure_problem_type` |
| Task5 sibling export MFA | testเดิมยังมี403ที่MFAอาจบังสาเหตุ; Task7มี fresh-login/real-MFA A/B regressionเพิ่ม ดู `Fresh_login_after_role_change_preserves_exact_scope_and_current_field_visibility` ไม่อ้างว่าแก้testเก่า |
| Task6 race/session assertions | Task7เพิ่ม controlled overlapและ session/cookie assertions ตามรายงานTask7 |
| Task7 exportเกิน100 | backend400ยังข้อความทั่วไป ไม่แนะนำลดช่วงโดยตรง; OpenAPI/คู่มือ/UIบอกข้อจำกัด แต่ไม่ถือว่าปิดruntimeข้อความนี้ |
| Task8 UUIDdeep-link | uppercaseUUIDผ่านparserแต่เทียบcanonicalpathแบบcase-sensitiveอาจ404; selectorปกติสร้างlowercaseถูกต้อง |
| Module2 | owner registerและMinorsเดิมทั้งหมดคงตามรายงานModule2; generic500/defaultแก้เอกสารเฉพาะModule3 ไม่ได้ปิดทั้งModule2 |

## Whole-branch Code Review

รอผู้ตรวจอิสระ fresh contextหนึ่งคนตรวจทั้งModule3เทียบspec/planและ5ReviewFocus รวมจุดเชื่อมModule2 จะบันทึก findings, regrade, คำตัดสินต่อสิ่งที่ไม่ตรวจ และหลักฐานfixที่นี่ ไม่มีการอ้างว่าreviewรายTaskก่อนหน้าแทนwhole-branchreview

## Policy และ Production gate — ยังรออนุมัติ

| Owner | หลักฐานที่ยังต้องอนุมัติ |
| --- | --- |
| System/Business | scope matrixทั้งสามระดับ, role/capability grants, field visibility และการมอบหมายผู้ดูแลด้วยข้อมูลทดสอบ |
| Security | self-grant separation, trusted management/การสมคบของผู้ดูแล, MFA classes/recent assurance, recoveryและข้อจำกัดที่เลื่อนไว้ |
| Operations | hostname/TLS/firewall/private API, migration/backup/restore/rotation drill, auditincident/monitoring และ productionflagปิดtechnicalUI/API |
| Operations + Security | benchmark advisory lockร่วม7241002/Argon concurrencyบนเครื่องจริง, throughputและmulti-replica rate budget |
| เจ้าของ Module2 | sign-offรายการเดิมทั้งหมด รวม emailadapterที่ยังรอModule5; การผ่านModule3ไม่แทนการส่งอีเมลจริงหรือSSO |

ไม่มีข้อมูลจริง/credentialsในtestsหรือเอกสาร ไม่ทำmanual OS/BFCache/Chrome/WebKit certification ไม่ทดสอบcapacity/DRของdeploymentจริงในรอบนี้

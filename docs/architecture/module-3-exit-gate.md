# Exit Gate Module 3 — Organization และ Exact Scope

รายละเอียดคำตัดสินทุกข้อจาก ledger เก็บในภาคผนวกท้ายรายงาน เพื่อไม่สูญหายเมื่อเก็บ scratch ออกจาก workspace

## สถานะ ณ รอบ Task 9

**Technical gate ผ่าน — Tasks 1–9 ครบ** หลัง full verification และ whole-branch review พร้อมแก้ Important ด้วย TDD; Policy/Production gate ยังรอผู้รับผิดชอบอนุมัติ ไม่ใช่การอนุมัติ deploy

งานอยู่ใน `codex/module-3-organization-scope` ที่ `/Users/theerapat_k/.codex/worktrees/module-3-organization-scope/TPR10` ฐาน Module3 คือ `99523a868a4465ef9215b2054e4066a0ed84cd2e`; เริ่ม Task9 จาก `3256b6f62d3e147b401e4af63643e72fa8626004` ยังไม่ push/merge/deploy และไม่แตะงานค้างใน main

อ้างอิง [Spec](../superpowers/specs/2026-09-24-module-3-organization-scope-design.md), [แผน](../superpowers/plans/2026-09-24-module-3-organization-scope.md), [คู่มือปฏิบัติงาน](../runbooks/module-3-organization-scope.md) และ [ข้อค้าง Module2](module-2-exit-gate.md)

## สิ่งที่ส่งมอบ

Tasks1–8 สร้าง hierarchy/composite constraints, exact assignments/history, system/business domain, scoped MFA+revocation, discovery, record/field/export boundaries, atomic audit, PostgreSQL controlled race tests และ Portal/administration/technical UI

Task9 เพิ่ม `ScopeEndpointMetadata(Mode,Capability,RequireMfa,Level)` และ transformerแยกด้านเอกสารซึ่ง Identity transformerเรียกโดยตรง ไม่แข่งลำดับ registration ไม่สร้าง authorityใหม่ และคง runtime `ScopeProbeBoundary` สำหรับ audit; `system-management`/`scope-discovery` level none ไม่ใช่ business wildcard ส่วน `exact-business` ระบุ workspace/project/site

OpenAPI enumerate34 Module3 operations ใน Development: organization12 + assignments6 + discovery1 + records15; Productionเหลือ19 operationsด้านบริหาร/discovery ไม่แสดงprobes และคงcontractของ26 identity operationsในDevelopmentเดิม (technical routesของModule2ยังปิดในProductionเช่นเดิม) ระบุ cookie/CSRFไม่ซ้ำ, capability/MFA, pagination, restricted omission/absent-null-string, write-only variant และ generic500/defaultที่อาจไม่มีJSON พร้อม discovery503 URNที่เคยขาด

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

รอบหลังreviewfix ตรวจbackendเสร็จ `2026-09-24T16:19Z`: `dotnet test backend/TPR10.sln` **829/829 ไม่มีskipped** ใช้17นาที1วินาที; Build solutionและE2E fixture0warnings/0errors, format verifyทั้งสองผ่าน และNuGetรวมtransitiveไม่พบvulnerablepackages ทุกคำสั่งexit0 หลักฐาน `/private/tmp/tpr10-m3t9-postreview-{full,build,format,fixture-build,fixture-format,nuget}.log`

Frontendหลังfix: dev4000 identity13/13 (40.5วินาที) + scopes11/11 (46.9วินาที); prod4001 identity13/13 (28.1วินาที) + scopes11/11 (27.8วินาที) + boundary2/2 (5.1วินาที) รวม E2E50/50 ผ่าน ไม่มีskip Node35/35, Build, Lint และnpm audit0 ผ่าน ทุกคำสั่งexit0 รอบ identity สุดท้ายอยู่ใน /private/tmp/tpr10-m3t9-{dev-identity,prod-identity,web-build,node,lint}-verified.log ตรวจหลังbackendเสร็จวันที่2026-09-24 UTC

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
| ใช้commitสำหรับreviewที่ยังระบุgatepending | ผู้ตรวจเห็นช่วงSHAครบทั้งModule3 ไม่หลงไฟล์untracked; ยังไม่ใช่การรับรองผลส่งมอบ |
| คงI1/I2เป็นImportant และยกระดับURLคู่มือผิดเป็นImportant | ข้อมูลค้างข้ามบัญชี/ร่างฟอร์มหาย/ขั้นตอนพาไป404เป็นผลต่อผู้ใช้จริง ไม่ใช้คำว่าtechnical-onlyเป็นข้อยกเว้น |
| Auth changeส่งเฉพาะinvalidationและไม่echoผู้ส่ง | BroadcastChannelร่วมในหน้าต่างเดียวกัน หรือstorage nonce; หากเผลอส่งกลับจะขัดlogin/MFA จึงมีnative-channel RED→GREEN |
| Session revalidationเทียบbaselineจากSSRก่อนคืนdraft | actor/stage/permissions/MFAต้องตรง, errorคงซ่อน; เพิ่มGETเมื่อmount/focusแต่ไม่เก็บdraftลงstorage หากไม่เทียบอาจแสดงdraftผิดบัญชี |
| PrivateViewซ่อนทั้งcontainerด้วยdisplay:none | visibilityของลูกอาจoverrideparenthidden; browserpagehideREDพบผลexportยังvisibleก่อนแก้ ไม่ใช้CSSซ่อนที่ถูกลูกเปิดทับได้ |
| ตรวจfinalใหม่หลังfixโดยไม่reviewรอบสอง | TDDและfullverificationเป็นหลักฐานfix; ไม่อ้างผู้ตรวจรันruntimeหรือรับรองdiffหลังfixอิสระ |

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
| Dev navigationในTask9 | บางรอบค้างก่อนassertionsที่script `/_next/static/chunks/main-app.js` โดยdocumentเป็นinteractive; เพิ่มdiagnosticsไม่เก็บsecretและรอPortalheading/loadก่อนnavigationถัดไป รอบล่าสุดผ่านแต่ไม่อ้างroot causeของNext/FirefoxหรือModule2ถูกแก้ทั้งหมด ไม่มีretry/skipหรือเพิ่มtimeoutเพื่อกลบผล |

มีรอบ dev identity ที่ `returnTo ภายนอกและ malformed ไม่พาออกจาก Portal` timeout60วินาที (12ผ่าน/1ล้ม) พร้อมFirefox actor-destroyed/Next pagehide stack ก่อนรอบตรวจซ้ำ จึงเพิ่มการรอPortalheadingและloadก่อนปิดcontextในแต่ละกรณี ไม่ตัดmalformed inputs ไม่ขยายtimeout ไม่เปลี่ยนproductionredirectpolicy และไม่อ้างว่าเป็นroot causeเดียวกับอาการเก่าทั้งหมด

## Whole-branch Code Review

Boole ตรวจแบบfresh context/read-only `99523a8..fa9c1b1` ทั้ง9commitsหนึ่งรอบ ผล Critical0 / Important2 / Minorใหม่2 ผู้ตรวจตรวจdiff/model consistencyเอง แต่ไม่ได้รันbackend/E2Eซ้ำ ไม่ใช่ independent runtime certification; ReviewFocus1–4ไม่พบblockerใหม่ ส่วนFocus5พบI1 และต้องแก้โดยไม่คงI2 ไม่มีreviewรอบสอง

| ข้อ | ผลตรวจและคำตัดสิน |
| --- | --- |
| I1 Important | REDยืนยันdelayed GET/exportข้ามlogoutยังอยู่หน้าเก่า; แก้ด้วยinvalidationข้ามหน้าต่าง/ยกเลิกin-flight/ถอดprivate DOM และpagehideซ่อนทั้งcontainer รองรับBroadcastChannelและstorage fallback; ปิดด้วยfinal backend829/829, Node35/35, E2E50/50 |
| I2 Important | REDยืนยันWorkspace UUIDกลายเป็นค่าว่าง; แก้ด้วยrevalidationที่เทียบsessionจากSSR รักษาdraftในmemoryเมื่อactor/สิทธิ์เดิมตรง,503ซ่อน/retryไม่logout,บัญชีเปลี่ยนแม้ไม่มีbroadcastก็reloadหลังกลับแท็บ; ปิดด้วยfinal backend829/829, Node35/35, E2E50/50 |
| M1 Minor — เลื่อน | childแสดงactive flagของตัวเองโดยไม่บอกinactive parent; APIยังdenyถูกต้อง ผู้ดูแลอาจสับสน409 ควรแยกสถานะeffectiveในงานถัดไป |
| M2 ยกระดับ Important ด้านคู่มือ | URLหน้ามอบหมายผิดทำให้ขั้นตอนปฏิบัติงานไป404 ไม่ใช่เพียงถ้อยคำ จึงแก้เป็น `/portal/admin/assignments` ตามroute/menuและE2Eจริง; เอกสารมนุษย์ไม่เพิ่มtestgrepข้อความ |

### สิ่งที่ผู้ตรวจเว้นไว้ และคำตัดสินครบทุกข้อ

| เรื่อง | คำตัดสิน / ผลหากตีความว่าผ่านแล้ว |
| --- | --- |
| 1. scope matrix/role grants/field policy Production | รอownerลงนาม ไม่ใช้technicaltestsแทน; มิฉะนั้นอาจเปิดข้อมูลผิดนโยบาย |
| 2. ผู้ดูแลสมคบ/การพิสูจน์ตัวบุคคล | trusted-managementและgovernanceยังต้องใช้จริง; แอปไม่ป้องกันsocial engineeringครบ |
| 3. Lock/Argon/DoS capacity | ต้องbenchmarkเครื่องเป้าหมาย; อาจคอขวดหรือล็อกผู้ใช้จริง |
| 4. Multi-replica rate budget | limiterรายprocessไม่รับรองเพดานรวม; ต้องออกแบบก่อนscale |
| 5. TLS/firewall/proxy/private API/fixtureflag จริง | localhostfixtureไม่แทนdeploymentตรวจจริง; ผิดพลาดอาจเปิดAPI/technicalUI |
| 6. Backup/restore/key rotation/downgrade | ต้องOperationsdrillและอนุมัติ; roundtripไม่รับรองกู้ข้อมูลหรือkeysจริง |
| 7. Direct SQL/RLS | ขอบเขตแอปไม่กันผู้ถือสิทธิ์DB; ต้องleast privilegeและDBgovernance ไม่อ้างRLSที่ไม่มี |
| 8. Business workflow/NAS/jobs/email/SSO | ยังไม่ส่งมอบในModule3; โมดูลถัดไปต้องมีแผนและacceptanceของตน |
| 9. ทุกbrowser/OS/BFCache/WCAG | ยังไม่รับรองทั้งหมด แต่ไม่ใช้ข้อนี้เลื่อนI1/I2; หากข้ามอาจมีUIprivacy/accessibilityที่ไม่ตรวจ |
| 10. Callersในอนาคต | ต้องรักษาtransaction/lock/materializeก่อนcommitด้วยtests; ผลนี้ไม่รับรองโค้ดที่ยังไม่มี |
| 11. Minor Module2อื่น | คงทะเบียนเดิม ไม่ปิดเพราะsuiteผ่าน; ต้องประเมินก่อนProduction |
| 12. Independent runtime/final829 | ผู้ทำหลักรับผิดชอบรันและอ่านผลใหม่ ผู้ตรวจไม่ได้รันแทน; หากข้ามจะไม่มีหลักฐานโค้ดส่งมอบ |

## Policy และ Production gate — ยังรออนุมัติ

| Owner | หลักฐานที่ยังต้องอนุมัติ |
| --- | --- |
| System/Business | scope matrixทั้งสามระดับ, role/capability grants, field visibility และการมอบหมายผู้ดูแลด้วยข้อมูลทดสอบ |
| Security | self-grant separation, trusted management/การสมคบของผู้ดูแล, MFA classes/recent assurance, recoveryและข้อจำกัดที่เลื่อนไว้ |
| Operations | hostname/TLS/firewall/private API, migration/backup/restore/rotation drill, auditincident/monitoring และ productionflagปิดtechnicalUI/API |
| Operations + Security | benchmark advisory lockร่วม7241002/Argon concurrencyบนเครื่องจริง, throughputและmulti-replica rate budget |
| เจ้าของ Module2 | sign-offรายการเดิมทั้งหมด รวม emailadapterที่ยังรอModule5; การผ่านModule3ไม่แทนการส่งอีเมลจริงหรือSSO |

ไม่มีข้อมูลหรือcredentialsของผู้ใช้จริงในtestsหรือเอกสาร Fixturesใช้ข้อมูลสังเคราะห์ในฐานที่ทิ้งได้ ไม่ทำmanual OS/BFCache/Chrome/WebKit certification ไม่ทดสอบcapacity/DRของdeploymentจริงในรอบนี้

## ภาคผนวกคำตัดสินจาก ledger

- Ruling: local ESLint exceptionเฉพาะno-html-link-for-pagesในPortal/ScopeFrameเพราะfullnavigationตามspecป้องกันclientcache — ไม่ปิดruleอื่น — หากเปลี่ยนเป็นLinkต้องพิสูจน์cache/accountisolationใหม่
- Ruling: ทำตามdesign/planที่อนุมัติ ไม่brainstormใหม่ — จำกัดTask8และไม่pushmerge — ยังต้องTask9wholebranchgate
- Ruling: assignmentmanagerที่ไม่มีorganization:manageกรอกscopeUUIDที่ได้รับจากผู้ดูแล ไม่เรียกorganizationcatalogที่ไม่มีสิทธิ์; users/rolesใช้optionsAPI — หากต้องการcatalogใหม่ต้องออกแบบสิทธิ์เพิ่ม ไม่ยกระดับโดยUI
- Ruling: เพิ่มhelper/clientquery/serverboundaryเท่าที่จำเป็นตามcontracttask8 — no-store/abort/generation/fullnavigationเป็นข้อกำหนด — หากboundaryผิดอาจแสดงข้อมูลเก่า
- Ruling: Reviewer setasideการรับรองTasks1–6/Minorsเดิม — ตรวจเฉพาะจุดเชื่อมTask7ไม่recertifyทั้งระบบ — ข้อค้างก่อนหน้ายังต้องตามต่อ
- Ruling: Reviewer setasideUI stale/cache/back/logout — อยู่Task8 — E2Eidentity13ไม่ใช่scopedUIacceptance
- Ruling: Reviewer setasidewholebranch/throughput/multireplica/productionready — อยู่Task9และproductiongate — ไม่ใช้Task7เป็นอนุมัติmergeหรือเปิดprobesproduction
- Ruling: Reviewerไม่รันruntime — executorเป็นผู้รันfocused/full/TestBuildLintE2E — ห้ามอ้างindependentfullrerun
- Ruling: regression lifecycle ใช้implementationTasks2–6เดิม ไม่สร้างproductionfixโดยไม่มีRED; การเพิ่มmutation/commitfaultพิสูจน์ rollback ที่Task7เรียกร้อง — ถ้าทดสอบไม่ถูกจุดอาจพลาดtransaction boundary
- Ruling: ใช้ isolated worktree/dependencies เดิม; Tasks1–6 ไม่ทำซ้ำ — แผนอนุมัติแล้ว — main ต้องไม่เปลี่ยน
- Ruling: เพิ่ม repository ExportAsync ที่รับScopeContextและคืน bounded array101 แทน publicIQueryable — query exacttuple/filters/order ภายในTX — หากผิดจะเสี่ยงข้ามscopeหรือส่งก่อนaudit
- Ruling: เพิ่ม OpenAPI/metadataallowlist พร้อม3routes export และtest-onlyhooksสำหรับrace — เป็นGlobalConstraints/acceptanceของTask7 — ไม่รอTask9
- Ruling: ขยายraceให้มีsignalจากทั้งoperationและrevoker และตรวจsessionstateในfaulttests — ตรงTask7matrixและครอบคลุมMinorTask6ที่ทับซ้อน — ไม่เก็บMinorอื่นอัตโนมัติ
- Ruling: ReviewอิสระเฉพาะTask7ก่อนส่งมอบ ไม่แทนTask9 — ผู้ใช้สั่งทีละTask — ไม่pushmerge
- Final: Ruling: exportและresponseก่อนexportcommitเป็นTask7 — Task6ไม่มีexport — หากอ้างรวมจะเกินหลักฐาน
- Final: Ruling: role-grant/deactivate/disable race matrix เพิ่มเติมเป็นTask7 — Task6เจาะassignmentrevoke — ยังไม่รับรองทุกlifecyclemutation
- Final: Ruling: Tasks2/4login/session/forced-change/recovery/affected-userunionไม่recertifyด้วยTask6review — fullsuiteเป็นregressionเท่านั้น — inheritedMinorsยังอยู่
- Final: Ruling: UIstale/cache/back/logoutและscopedHTTPSbrowserเป็นTask8 — รอบนี้E2Eidentityเดิม13ผ่าน — ไม่ใช่scopeUIacceptance
- Final: Ruling: wholebranch/capacity/productionreadinessเป็นTask9/productiongate — Task6technicalprobesถูกปิดProduction — ไม่อ้างพร้อมdeploy
- Final: Ruling: reviewerstaticไม่รันTestBuildLintE2E — executorรับผิดชอบruntime — ไม่อ้างผลruntimeอิสระสองชุด
- Ruling: CreateScopeRecord ไม่ใส่ optional C# constructor default ของ JsonElement — .NET schema exporter serialize defaultแล้ว JsonException500; HTTP absentยังUndefinedเหมือนPATCHและมีregression — caller C# ต้องส่งdefaultเอง ไม่เปลี่ยนwirecontract
- Ruling: PATCH no-opคงversionและเวลาเดิม แต่ยังaudit — versionเปลี่ยนเฉพาะeffectivechangeตามแบบเดียวกับmanagement — clientต้องใช้versionที่serverคืน
- Ruling: ใช้ worktree/แผนเดิม ไม่ทำ Tasks1–5 ซ้ำ — งานต่อที่อนุมัติแล้ว — main และไฟล์เดิมต้องไม่เปลี่ยน
- Ruling: Task6 เปิด 4 operations ต่อระดับ รวม12 ไม่ใช่5ตามข้อความแผน — export เป็นTask7 — ห้ามเปิด placeholder export
- Ruling: เพิ่ม binding audit, OpenAPI และ row-count allowlist พร้อม API — เป็นข้อบังคับสัญญาและ audit ตั้งแต่เปิด route — หากไม่ทำจะมีช่องว่าง metadata/denial
- Ruling: write response ตรวจ read capability ซ้ำใน transaction เดียวกันก่อน projection; write-onlyคืน id/version+Location — ไม่ใช้สิทธิ์ write แทน read — หากผิดจะรั่วfield
- Ruling: Note ยอมรับข้อความว่างตาม schema/spec แต่ห้าม null/control/เกิน500; restricted absentคงเดิม nullล้าง — ไม่เพิ่มminimumนอกแผน — UIต้องรองรับข้อความว่าง
- Ruling: review เพิ่มเฉพาะTask6ก่อนส่งมอบ ไม่แทนTask9wholebranch — ผู้ใช้แบ่งสั่งทีละtask — ยังไม่pushmerge
- Final: Ruling: recordID/binding/repository/fieldserialization/writevalidationเป็นTask6 — Task5รับรองevaluator — หากอ้างครบจะเกินหลักฐาน
- Final: Ruling: actualread/write/exportvsrevoke/deactivate/rolegrantsทั้งcommitordersเป็นTasks6–7 — barrierTask5เฉพาะsessionrevokeก่อนDBcheck — ต้องทดสอบcallerจริงต่อ
- Final: Ruling: recordaudittarget/count/filter/exportlimits/versionrollbackเป็นTasks6–7 — sharedTXไม่แทนoperationเฉพาะ — มิฉะนั้นaudit/exportไม่มีหลักฐาน
- Final: Ruling: callbackeagerDTO/noowncommitเป็นcallercontract — explicitstatusไม่enforceทุกอย่าง — Task6callerผิดอาจอ่านหลังlock
- Final: Ruling: ScopeAccessตรวจTXไม่ใช่lockownership — currentownerslockถูก — callerใหม่ต้องรักษาลำดับไม่เช่นนั้นrace
- Final: Ruling: UIcache/delay/back/accessibilityTask8 — ยังไม่มีUI — identityE2Eไม่แทนscopebrowser
- Final: Ruling: Tasks1–4ไม่recertifyทั้งชุดและMinorsคงอยู่ — นอกTask5 — อย่าอ้างปิดย้อนหลัง
- Final: Ruling: discoverypagesไม่snapshotข้ามrequest — แต่ละrequestauthorizeสด — UIต้องrefreshเมื่อสิทธิ์เปลี่ยน
- Final: Ruling: discoverycapabilitiesเป็นgrantsไม่ใช่MFAguarantee — executionตรวจซ้ำ — UIต้องรองรับ403
- Final: Ruling: capacity/multireplica/deploy/recovery/directSQL/ownersignoffproductiongate — เน้นcorrectnessMVP — ยังไม่รับรองthroughput
- Final: Ruling: reviewerstaticไม่รันtests — executorรับผิดชอบruntime — ไม่อ้างindependentruntimeสองชุด
- Final: Ruling: wholebranchTask9ยังรอ — Task5reviewเฉพาะdeliverable — ไม่pushmergeหรือproductionapproval
- Ruling: discovery querystring เป็นstring parseInvariantในhandler เพื่อทุก malformedpaginationผ่านdenialaudit ไม่ต้อง middlewareพิเศษ — OpenAPIระบุเป็นข้อความมีคำอธิบายnumeric — หากclientส่งarray/overflowต้อง400
- Ruling: discoveryหลายtupleไม่มีactingrole/scopeปลอม; operationfailureไม่ใช้callbackbodyในresponse/audit — ป้องกันleak — callerต้องใช้statuscontract
- Ruling: ใช้ executing-plans inline และ worktree เดิม dependency เดิม; Task1–4 ไม่ทำซ้ำ — แผนอนุมัติแล้วไม่ออกแบบใหม่ — ไม่เปลี่ยน main
- Ruling: ขยาย OpenAPI enumeration/transformer พร้อม GET/scopes ตั้งแต่ task นี้ตาม Global Constraints; Task9 ยังต้องตรวจรวม — ถ้าเลื่อนไป API ใหม่จะอธิบายเป็น identity-only ผิด
- Ruling: ScopeOperation callback ต้อง materialize DTO และคืน IResult ที่มี status ก่อน commit; callback error ต้อง rollback/clear tracked changes แล้วทำ denial audit ใหม่ — ไม่ปล่อย partial state
- Ruling: MFA ยึด stage เดิม Module2: assurance ที่หมดอายุตอบ403แม้ ordinary read; staff ที่ไม่เคย enroll อ่าน public ได้ แต่ restricted-read/export ต้อง recent MFA — ไม่ลด assurance policy
- Ruling: PATHเดิมเป็นNode20นอกengines; ใช้bundledNode24.19.0ที่มีอยู่และรันfrontend/E2Eซ้ำ — ไม่แก้packageหรือinstall — ผลNode20ไม่ใช้เป็นfinalgate
- Ruling: เพิ่มreadinesswaitในHTTPSsmokeหลังbrowseroutagetest — พบREDbrowser13passแต่curlcsrf502เพราะdockerstartไม่รอASP.NETready — retryเฉพาะhealth GETไม่mutation; หากไม่แก้acceptanceจะflaky
- Ruling: reviewเพิ่มเฉพาะTask4ตามการส่งมอบของผู้ใช้ ไม่แทนTask9 — ต้องตรวจอิสระก่อนปิดtask — executorเป็นเจ้าของruntimeverification
- Ruling: ใช้worktreeเดิมที่สะอาดและdependencyเดิม ไม่ติดตั้งใหม่ — งานต่อจากTask3 — ไม่เสี่ยงlockfile/dependencyเปลี่ยนโดยไม่เกี่ยวงาน
- Ruling: แก้ลำดับfixtureในตัวอย่างแผนให้Adminก่อนScopeFixture — bootstrapไม่ทำงานหลังมีuser — มิฉะนั้นREDจะเป็นfixtureerrorไม่ใช่404
- Ruling: เพิ่มIdentityRegistration/OpenAPI/testsและbindingauditในขอบเขต — policyและcontractต้องมีตั้งแต่เปิดendpoint รวมบทเรียนTask3 — ถ้าไม่ทำจะเปิดrouteที่เอกสาร/denialauditไม่ครบ
- Ruling: สร้างScopes/ScopeContracts.cs สำหรับPage<T>/ExpectedChangeตามสัญญาร่วม — Task4ใช้ก่อนTask5 — tasksถัดไปต้องreuseไม่ประกาศซ้ำ
- Ruling: GETscopefilterใช้workspaceId/projectId/siteId query แบบexacttuple, revoked=nullหมายถึงประวัติทั้งหมด; prefixจำกัด100; usersoptionsไม่แสดงactorแต่แสดงinactiveเพื่อสถานะ — ไม่มีbusinesswildcardหรือselftarget — UIต้องใช้contractนี้
- Ruling: revoke singleassignmentแก้เฉพาะrowในservice ไม่เรียกcascadehelper — helperTask2มีเฉพาะuser/subtree — มิฉะนั้นถอนroleอื่นโดยไม่ตั้งใจ
- Final: Ruling: ScopeContext/discovery/data-plane/export/racesกับbusinessoperationอยู่Tasks5–7 — Task4รับเฉพาะassignmentcontrolplane — หากใช้ก่อนครบจะไม่มีbusinessauthorizationที่รับรอง
- Final: Ruling: Portal/selector/cacheอยู่Task8 — ไม่มีUIใหม่รอบนี้ — E2Eปัจจุบันรับรองidentityregressionไม่ใช่UIassignment
- Final: Ruling: wholebranchacceptance/runbook/throughput/multireplicaอยู่Task9และproductiongate — ไม่รับรองproductionจากTask4 — ต้องตรวจรวม/benchmarkต่อ
- Final: Ruling: runtimeverificationเป็นexecutor ไม่ใช่reviewer — ไม่มีการรับรองruntimeอิสระสองรอบ — ต้องอ่านผลTestBuildLintE2Eเอง
- Final: Ruling: MinorเดิมTasks1–3ยังคงอยู่ — นอกTask4และไม่ใช่blocker — อย่าเข้าใจว่าTask4ปิดmigration/affectedusers/Organization401raceแล้ว
- Ruling: เพิ่มIdentityOpenApiTransformer/Testsในขอบเขต — GlobalConstraintsบังคับcontractทุกrouteตั้งแต่Task3 — หากไม่เพิ่มจะอธิบายorganizationเป็นidentity-onlyผิด
- Ruling: PATCHต้องมีName/IsActive/ExpectedVersion/Reasonครบและrejectunknownfields — ไม่ให้missingboolกลายเป็นfalseและห้ามreparent/codechange — clientต้องส่งfullshape
- Ruling: listยังเห็นinactive metadataได้; create/active-updateใต้inactiveparent409 — เพื่อบริหารและreactivateได้แต่ไม่เปิดchildภายใต้parentปิด — UIภายหลังต้องแสดงparentstate
- Ruling: service listถือlockและguardซ้ำด้วย — ไม่ปล่อยcatalogหลังสิทธิ์ถูกถอนระหว่างmiddleware — แลกthroughputกับcorrectnessตามMVP
- Ruling: ไม่มีdescendant IsActive flag cascade; ถอนassignments/sessionตามsubtree — ancestorinactiveทำให้childใช้ไม่ได้ตามspec — callerต้องไม่แปลchildflagแยกจากancestor
- Ruling: auditเพิ่มเฉพาะscope-validation; fieldชื่อ/codeไม่ลงmetadata — keepallowlist — reasonยังไม่ใช่DLP
- Final: Ruling: เพิ่ม review เฉพาะTask3 — ผู้ใช้จำกัดส่งมอบรายtaskและขอreview — ไม่แทนwholebranchTask9
- Final: Ruling: bindingdenialเป็นImportant — ผู้ดูแลส่งPATCHผิดshapeต้องมีauditตามขอบเขตmanagementdenial — หากปล่อยไว้เหตุการณ์และauditoutageจะถูกข้าม
- Final: Ruling: Assignment/scoped discovery/data/export อยู่Tasks4–7 — ไม่เปิดdata planeก่อนครบguard — หากใช้ก่อนครบจะไม่มีbusinessworkflow
- Final: Ruling: Portal/cache/browserorganization อยู่Task8 — ไม่มีUIใหม่ในTask3 — E2Eตอนนี้รับรองidentityregressionเท่านั้น
- Final: Ruling: wholebranchacceptance/runbook/capacity อยู่Task9และproductiongate — ไม่รับรองproductionจากTask3 — ต้องตรวจรวมและbenchmarkภายหลัง
- Final: Ruling: reviewerไม่รันtests — executorรับผิดชอบfreshverification — ไม่อ้างหลักฐานruntimeอิสระสองรอบ
- Ruling: AffectedUsers รวม non-revoked assignments แม้ ancestor inactive เพื่อถอน session แบบ conservative; MFA ตรวจ ancestor active ตามแผน
- Ruling: lifecycle ตรวจ tracked revoked state ก่อนเปลี่ยนซ้ำ เพราะ caller อาจยังไม่ได้ SaveChanges; RED expected0 actual1 ยืนยันปัญหา
- Ruling: OperatorMfaRecovery ตรวจ Domain=system ภายใน use case เช่นเดียวกับ guard; RED403เทียบ204รองรับ ไม่ขยาย recovery workflow
- Ruling: review เพิ่มสำหรับ Task2 deliverable ไม่แทน whole-branch review ของ Task9; ผู้ใช้สั่งTask2จึงไม่เริ่มTask3เอง
- Final: Ruling: Organization/Assignment APIs, self-assignment และ UI เลื่อนไปTasks3+ — ไม่มีrouteใหม่รอบนี้ — หากเปิดก่อนครบสิทธิ์ธุรกิจจะยังไม่ถูกบังคับครบ
- Final: Ruling: record/export race ordering และbrowser cache isolation อยู่Tasks5–9 — Task2รับเฉพาะtransactioncontract — หากละเลยภายหลังอาจมีข้อมูลข้ามscope
- Final: Ruling: Task1migration/fullfieldroundtripไม่แก้ — เป็นฐานที่ตรวจเข้ากันได้แล้ว — หากmigrationเปลี่ยนต้องทดสอบใหม่
- Final: Ruling: lifecycleไม่ตรวจruntimeadvisoryownership — callerปัจจุบันถือ7241002ก่อนเสมอ — callerใหม่ผิดลำดับอาจrace
- Final: Ruling: unsavednewgrants/reusedrolledbackDbContextไม่อยู่callerปัจจุบัน — callerใหม่ต้องsavegrantและทิ้งcontextหลังrollback — มิฉะนั้นอาจข้ามassignment
- Final: Ruling: ไม่redesignModule2/benchmarkproduction — ไม่มีการเปลี่ยนplatform — ยังไม่รับรองcapacity
- Final: Ruling: reviewerไม่รันtestsซ้ำ — executorเป็นผู้รันTestBuildLintE2Eพร้อมหลักฐาน — ไม่อ้างว่าruntimeผ่านการรับรองสองชุดอิสระ
- Task 1: Ruling: emptyUUIDAPIshape ในแผนทดสอบด้วย ScopeKey ใน Task1 เพราะยังไม่มี scoped API — HTTP binding ทดสอบTask3/6 — หากไม่ตามต่อจะขาดหลักฐาน HTTP400
- Task 1: Ruling: ใช้ worktree จาก native tool และนำเฉพาะเอกสารแผนมาด้วย ไม่รวม backup ของmain — รักษางานเดิม — ไม่มีการเปลี่ยน runtime main
- Task 1: Ruling: ตรวจอิสระเฉพาะ deliverable Task1 เพิ่มหนึ่งครั้งเพราะรอบนี้ผู้ใช้จำกัดส่งมอบ Task1 และขอ Code Review — ไม่แทน whole-branch gate ของTask9 — มีต้นทุน review เพิ่มหนึ่งรอบ
- Final: Ruling: ยกระดับ negative FK coverage ของ nullable levels เป็น Important — การลบ FK workspace/project อาจหลุดแม้ testsเดิมผ่านเพราะFK siteช่วยรับไว้ — หากละเลยอาจปล่อย schema regression ข้ามพื้นที่
- Final: Ruling: API/MFA/lifecycle/race/UI ที่ reviewer set aside เป็น Tasks2–9 — ไม่ขยาย Task1 ไปทำสิ่งเหล่านั้น — ผลเสียหากใช้งานก่อนจบคือยังไม่มี scoped authorization จึงห้ามอ้าง production ready
- Final: Ruling: HTTP400 deferred ไปTask3/6 ตามขอบเขตเดิม — ยังต้องทำต่อก่อนเปิดAPI
- Final: Ruling: downgrade ฐานจริงนอกขอบเขตทดสอบนี้ — Down ลบข้อมูลโมดูลตามที่ออกแบบ — ถ้าใช้ผิดอาจสูญข้อมูล จึงต้องมีแผนรักษาข้อมูลก่อนใช้งานจริง
- Task 8: Ruling: ทำเฉพาะ Task8 ตามคำขอ ไม่เริ่ม Task9/push/merge — ขอบเขตผู้ใช้เหนือ continuous execution — whole-branch exit gate ยังไม่ผ่านจนกว่า Task9
- Task 8: Ruling: ใช้ spec/plan ที่อนุมัติแล้วและ worktree เดิม ไม่ brainstorm ใหม่หรือเปลี่ยน dependencies — งานนี้เป็นการลงมือทำตามแผน — หากแผนล้าสมัยต้องเปิดประเด็นใหม่ ไม่แก้ขอบเขตเงียบ ๆ
- Task 8: Ruling: assignment manager ใช้ UUID ที่ได้จากผู้ดูแลโครงสร้างและ minimal users/roles options — ไม่ให้ users:manage หรือ organization:manage เพิ่มเพื่อเลือกพื้นที่ — UX ต้องคัดลอก UUID จนกว่าจะมี catalog ที่จำกัดสิทธิ์เหมาะสม
- Task 8: Ruling: เพิ่ม server-scopes/useScopeQuery/ScopeFrame และ fixture ScopeSeed แยกไฟล์ — รองรับ no-store, abort/generation และ server-only boundary — มีไฟล์เพิ่มแต่ไม่เพิ่ม dependency/production API
- Task 8: Ruling: full navigation ใช้ anchor และปิดเฉพาะ next/no-html-link-for-pages ในสองไฟล์ที่จำเป็น — ทิ้ง client scope state ทุก navigation — แลกกับการโหลดเอกสารใหม่
- Task 8: Ruling: review อิสระเฉพาะ Task8 เพิ่มหนึ่งรอบตาม user quality gate ไม่แทน Task9 — ตรวจ fresh context/read-only โดย Ramanujan — ต้นทุน reviewer เพิ่มหนึ่งรอบ
- Task 8: Ruling: M1 ยกระดับ Important — รายงาน session revocation ที่ API ไม่ทำเป็นสถานะ security ที่ผิด — หากไม่แก้ผู้ดูแลอาจเชื่อว่า cookie เก่าใช้ไม่ได้แล้ว
- Task 8: Ruling: M3 ยกระดับ Important ด้านหลักฐาน — restricted DOM/export/history/accountswitch/no-store เป็น ReviewFocus5 — หากเลื่อนจะไม่มีหลักฐานตรงความเสี่ยงหลักTask8
- Task 8: Ruling: headlessFirefoxไม่รับรองOSvisibilityอัตโนมัติ — ส่ง lifecycleeventแบบควบคุมในbrowser ส่วนการเปลี่ยนบัญชีใช้UI/APIจริง — ไม่อ้างmanualcrossbrowser/BFCachecertification
- Task 8: Ruling: reviewerเว้น Focus1 isolation,Focus2MFA,Focus3races,Focus4audit ตามเจ้าของTasks2–7 — ผู้พัฒนารันBackendfull783ไม่ใช่reviewer independentlyrerun — wholebranchยังTask9
- Task 8: Ruling: reviewerเว้นruntimegates/ProductionAPI/deploymentflag/UXaccessibility/Task9 — แยกNextflagoffE2EกับBackendProductiontests,ยังไม่รับรองdeploymentจริงหรือWCAG,ไม่เพิ่มdetail/writeonlyworkflow/searchใหม่ — ขอบเขตและต้นทุนครบข้อ7–17ในรายงานไทย
- Task 9: Ruling: ปิด discovery503 URN Minorเดิมเพราะ Task9 รับผิดชอบ errorcontractโดยตรง ไม่ใช่เพิ่มruntimefixนอกงาน — เพิ่มtestที่ได้เฉพาะgeneric503 REDแล้วเติมmetadata GREEN119 — fullsuiteที่เริ่มก่อนแก้จะเป็นbaseline828 ต้องรันfinal829อีกครั้ง
- Task 9: Ruling: เอกสารมนุษย์ตรวจเทียบAPIและหลักฐาน ไม่สร้างtestgrepข้อความไทย — schema/HTTPมีcontracttestsจริง — หากคู่มือผิดยังต้องoperatorreviewก่อนProduction
- Task 9: Ruling: ต่อ architectural spec/plan ที่อนุมัติแล้ว ไม่ brainstorm ใหม่ และใช้ linked worktreeเดิม — ไม่ทำ Tasks1–8 ซ้ำ — mainมีเอกสารเดิมที่ต้องรักษา
- Task 9: Ruling: ScopeEndpointMetadata เป็น documentation only; Identity transformer อ่านแล้วเรียก ScopeOpenApiTransformer โดยตรง ไม่ลงทะเบียนoperationtransformerแข่งกัน — ไม่เปลี่ยนruntimeauthorityหรือtransaction — หากmetadataคลาดconsumerอาจเข้าใจผิด จึงมี34operation literalcontracts
- Task 9: Ruling: system-management ใช้Level noneแม้routeมีparent เพราะcontrolplaneไม่ใช่businessscope; recordใช้สามระดับ และdiscoverynone — ไม่สื่อว่าผู้ดูแลมีbusinessassignment
- Task 9: Ruling: เก็บScopeProbeBoundaryเดิมไว้สำหรับbindingauditแยกจากเอกสารใหม่ — ไม่เปลี่ยนruntimeด้วยrefactorเอกสาร — costคือmetadataสองประเภทแต่หน้าที่ไม่ซ้ำ
- Task 9: Ruling: เติมrequestschema restrictedNote string|null optionalตามruntimeแทนJsonElementกว้าง และgeneric500/defaultเฉพาะModule3 — ไม่ขยายไปปิดMinorModule2ทั้งหมด — consumerต้องตรวจContent-Typeไม่ถือทุกerror503
- Task 9: Ruling: commitฉบับสำหรับreviewซึ่งเอกสารยังระบุgatepending — ตรวจwholebranchด้วยSHAคงที่รวมTask9แทนreviewuntrackedfiles — ยังไม่ใช่commitส่งมอบสุดท้ายและไม่pushmerge
- Final: Ruling: Boole I1/I2 คงImportant — visiblewindowcrossaccountleak และsameactorswitchtabdraftlossกระทบผู้ใช้จริง — ไม่ใช้technicalonly/visibilitymockเป็นเหตุยกเว้น; หนึ่งTDDfixpassไม่มีreviewซ้ำ
- Final: Ruling: M2 runbookwrongURLยกระดับImportant operationaldocumentation — ผู้ใช้ทำตามแล้ว404 แก้URLroute/menuจริง — humanproseไม่สร้างtestgrep; ถ้าผิดคู่มือใช้งานไม่ได้
- Final: Ruling: Declined12ข้อคงขอบเขตตามตารางในmodule-3-exit-gate (policy/governance/capacity/multireplica/deployment/restore/DBadmin/futuremodules/crossbrowser/futurecallers/Module2minors/independentruntime) — ไม่อ้างProductionหรือรีวิวruntimeอิสระ — ความเสี่ยงแต่ละข้อในตาราง
- Final: Ruling: authchangeเป็นinvalidationไม่ใช่อำนาจ — BroadcastChannelใช้channelตัวเดิมไม่echoผู้ส่ง; fallbackstorageเก็บnonceเท่านั้น — หน้าต่างรับunmountprivate DOM+abort/generationและreload,ไม่มีpayloadลับในmessage; ถ้าbrowserบล็อกทั้งสองต้องfocusrevalidateไม่อ้างserverpush
- Final: Ruling: ScopeFrameรับsessionจากSSRและซ่อน/inertระหว่างrevalidate — sameactor/stage/permissions/MFAรักษาdraftmemory,changedreload/401login,503ซ่อนไม่logout — เพิ่มGETเมื่อmount/focus; หากไม่ตรวจbaselineอาจคงdraftผิดบัญชี
- Final: Ruling: nested visibility:visible overrideparenthiddenได้ — pagehideผลexportREDvisibleจึงใช้PrivateViewdisplay:none — dev11GREENรวมpagehide/BC/storage/no-messageactorchange/503preservedraft; หากไม่แก้BFCacheอาจแสดงข้อมูลเก่า
- Final: Ruling: devnavigationdiagnosticพบpendingmain-app.js documentinteractive — helperรอPortalheadingและloadก่อนnavigationถัดไป ไม่เพิ่มtimeout/skip/retry/TLSbypass — ล่าสุดdev11ผ่าน46.9sแต่ไม่อ้างrootcauseNext/FirefoxหรือModule2ปิดแล้ว
- Final: Ruling: returnTo testปิดcontextทันทีที่URLเปลี่ยน — เพิ่มPortalheading/loadassertionsก่อนcloseเหมือนscopehelper ไม่เพิ่มtimeoutหรือตัดinput — ถ้าไม่ใช่rootcauseอาจยังมีFirefoxdevnavigationflakiness จึงไม่อ้างปิดทั้งหมด; finalidentitydev/prod92211กำลังรันหลังbackendจบ

- Ruling: postcommit ใช้ Node35 และ contract119 ตรวจซ้ำ — fullbackend829 และ E2E50 ตรวจบน source ส่งมอบแล้ว ไม่รัน backend17นาทีซ้ำเพราะแก้เพียงเอกสาร — ไม่อ้างfocusedrunเป็นfullsuiteรอบใหม่
- Ruling: เก็บ scratch ของแผนนี้ใน /private/tmp แบบกู้คืนได้หลังปิด task — ไม่ลบ worktree หรือ scratch แผนอื่น — หลักฐานถาวรอยู่ในรายงานและgit; temporary archiveอาจถูกระบบล้าง

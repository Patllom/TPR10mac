# ผลตรวจและ Exit Gate — Module 2

วันที่ตรวจ: 2026-09-23 (หลักฐานคำสั่งใช้ UTC)
สถานะฉบับนี้: Task 9 ผ่านทางเทคนิค — ยังไม่ผ่าน production gate และยังไม่ push/merge
Branch: `codex/module-2-identity`; ฐาน Task 9: `55caced27d8b5f455764a9cddea654279cb6b127`; ฐานทั้ง Module 2: `6c7f1aef13fd0e2a64dce27366440479b5d74946`

## 1. แยกสถานะสามส่วน

| ส่วน | สถานะ | เงื่อนไขและผู้รับผิดชอบ |
| --- | --- | --- |
| ผ่านทางเทคนิค | ผ่าน Test/Build/Lint/E2E และ review พร้อมแก้ Important | ไม่มี Critical/Important ค้าง; Minor ใหม่ 2 และเดิม 9 เปิดเผยไว้ ไม่ถือว่าได้รับการยอมรับความเสี่ยง production |
| รอ Security owner | ยังไม่อนุมัติ | ต้องระบุผู้มีอำนาจและลงนาม policy/session/CSRF/lockout/reset/MFA/recovery รวมยอมรับข้อจำกัดคงค้าง ไม่ถือคำอนุมัติแผนพัฒนาเป็นการลงนาม production policy |
| รอ delivery adapter | ยังไม่ส่งอีเมล production | Module 5 ต่อ Gmail adapter/worker, dedup/retry/retention/monitoring; production ปัจจุบันปฏิเสธ EmailEnabled=true และไม่มี development sink |

การส่งมอบ Task 9 ไม่เท่ากับอนุมัติ deploy, pilot หรือปิด Exit Gate Module 2 โดย Security owner ห้ามใช้ผลทดสอบฐานข้อมูลแยกแทนการตรวจระบบ production ขององค์กร

## 2. สิ่งที่ส่งมอบ Task 9

- OpenAPI `/api/openapi/v1.json` ใช้ metadata ของ route และ authorization policy จริง ไม่สร้าง authority ใหม่
- 26 operations ภายใต้ `/api/v1` ใน Development/Testing (รวม probe 2 operations); probe ไม่เปิดใน Production ตามเงื่อนไขเดิม
- ทุก unsafe operation ระบุ required header `X-CSRF-Token` พร้อมคำอธิบาย Origin/Host และการผูก pre-auth/session
- Cookie scheme `SessionCookie` ระบุ `__Host-tpr10_session`, Secure/HttpOnly/SameSite=Lax/Path=/ ไม่มี Domain; ไม่เพิ่ม JWT/token ใน body
- `x-tpr10-permissions`, `x-tpr10-mfa-required`, `x-tpr10-session-stages` มาจาก policy/metadata; stage รวม Active ตาม runtime middleware ไม่ใช่ restricted metadata เพียงอย่างเดียว
- `x-tpr10-conditional-permissions` ของสร้าง/แก้บัญชีระบุ `roles:manage` เมื่อ `roleIds` ไม่เป็น null รวม array ว่าง; API ยังเป็นผู้ตรวจเงื่อนไขนี้
- `x-tpr10-scope` ระบุ identity-only-no-business-scope ไม่อ้างว่า Module 3 ทำแล้ว
- Request schema มาจาก handler request DTO; success response เติม `.Produces` บน route เพราะ `IResult`/anonymous response เดิมไม่เผย schema ครบ ส่วน response DTO สำหรับ OpenAPI ไม่เปลี่ยน payload runtime
- ระบุ status/problem schema และ `x-tpr10-problem-types` ที่มีจริง รวม 400/401/403/404/409/429/503, correlation/no-store และ Retry-After เมื่อ limiter ส่งค่า
- Login ซ้ำยังเป็น 409 body ว่างตาม implementation เดิม ไม่แต่ง schema ว่ามี Problem Details; binding/revalidation บางกรณีก็มี body ว่างได้ client ต้องตรวจ Content-Type

อ้างอิง API ของ [.NET 10 OpenAPI transformers](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/openapi/customize-openapi?view=aspnetcore-10.0) และตรวจ signature จาก package XML รุ่นที่ติดตั้งจริง ไม่อ้าง schema เป็นหลักฐานว่าการบังคับสิทธิ์ runtime สำเร็จ

## 3. Requirements coverage กับ Baseline

| ข้อกำหนด | Implementation / หลักฐานที่รันใน full suite | ขอบเขตที่ยังไม่รับรอง |
| --- | --- | --- |
| 8.1 Local provider, Argon2id, ไม่มี self-registration | `PasswordTests`, `LocalIdentityProviderTests`, `AccountTests`, `AccountProvisioningTests` | AD/LDAP/SSO ยังเป็น interface; ไม่ใช่ provider ที่ต่อใช้งานแล้ว |
| 8.1 Session hash/cookie/revoke | `LoginTests`, `LoginSecurityTests`, `SessionTests`, `SessionRotationTests`, `LogoutRotationRaceTests`, `SelfSignOutTests` | Assignment revocation consumer อยู่ Module 3; load/หลาย replica ยังไม่รับรอง |
| 8.1 Reset/forced change | `PasswordResetTests`, `AdminPasswordResetTests`, `PasswordResetSafetyTests`, `PasswordResetRaceTests`, `PasswordResetThrottleTests` | Email production รอ Module 5; operator ต้องยืนยันตัวบุคคลตาม policy |
| 8.1.1 Same origin, API authority, CSRF | `CsrfTests`, `CsrfRateLimiterTests`, `ForwardedHeadersTests`, `CsrfConfigurationTests`, HTTPS smoke และ browser E2E | เป็น localhost fixture; hostname/certificate/firewall จริงต้อง Operations review |
| 8.2 MFA/one-time/recovery | `MfaTests`, `MfaSecurityTests`, `MfaResilienceTests`, `OperatorMfaRecoveryTests` | Recovery operator/identity-proof approval และ key restore drill จริงยังค้าง |
| 8.3 Named permission/deny-by-default | `AuthorizationTests`, `RoleAuthorizationTests`, `AuthorizationRaceTests`, `AccountBoundaryTests` | ไม่มี business record/scoped repository/cross-scope tests จน Module 3 |
| 8.4 Audit atomicity/no secrets | `SecurityAuditTests`, `CorrelationAndAuditTests`, account/MFA/reset audit rollback, immutable trigger/migration tests | Free text ไม่รับรองว่าจับ secret ได้ทุกแบบ; ห้ามผู้ใช้ใส่ secret ใน reason/reference |
| 11.1–11.3 API contract/CSRF/errors/pagination | `IdentityOpenApiTests`, role/account pagination และ permission API tests | OpenAPI ไม่ใช่ idempotency ของ business mutation; ไม่ retry identity mutation โดยอัตโนมัติ |
| 18 ข้อมูล production | Runbook และรายการเจ้าของงานด้านล่าง | ยังไม่มี hostname/secret deployment/pilot sign-off ในรอบนี้ และไม่ควรใส่ secret ใน Git |
| 20.2 ไม่มี session/revoked/permission ไม่พอ | `AuthorizationTests`, `SessionTests`, `AuthorizationRaceTests` ตรวจปฏิเสธและ audit | ไม่ใช่ cross-scope acceptance ของ Module 3 |
| 20.2 Privileged ไม่มี MFA/forced change | `AuthorizationTests`, `MfaSecurityTests`, `SessionTests`, browser forced-change/MFA | ยังต้อง owner ยอมรับ policy assurance/role class |
| 20.2 CSRF/Origin/forwarded spoof | `CsrfTests`, `ForwardedHeadersTests`, browser HTTPS/cookie | ไม่ใช่หลักฐาน production proxy configuration |

## 4. TDD และรายการคำสั่ง

- ก่อนแก้: Node baseline 28/28
- OpenAPI contract 37 RED เพราะขาด CSRF/security/stages/schema → 37 GREEN หลัง transformer และ route response metadata
- Conditional role assignment 2 RED / 37 เดิมผ่าน → เพิ่ม metadata `roleIds` และ `roles:manage` → OpenAPI 39/39 ผ่าน
- เคย compile ไม่ผ่านเพราะใช้ `ApiDescription` แทน `Description` และแก้ `IOpenApiResponse` ผ่าน read-only interface; ตรวจ API ของ package แล้วใช้ concrete response ที่ generator สร้าง ไม่ถือ compile error เป็น TDD RED ของพฤติกรรม

ผลก่อน review จาก working tree บนฐาน `55caced` ตรวจผลครบเมื่อ **2026-09-23 15:37:37 UTC**; commit ที่บรรจุ diff จะระบุในผล review ด้านล่าง (ยังไม่ใช้ผลจาก Module 1 แทน):

| คำสั่ง | ผล ณ ฉบับนี้ |
| --- | --- |
| `dotnet restore backend/TPR10.sln` | exit 0 |
| `dotnet test backend/TPR10.sln` | exit 0; 348/348, 0 skipped, 4 นาที 1 วินาที รวม OpenAPI39 |
| `dotnet build backend/TPR10.sln --no-restore` | exit 0; 0 warnings/errors |
| `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` | exit 0 |
| `npm ci` / `npm test` / `npm run lint` | exit 0; Node28/28; ci มี deprecated warnings ของ ESLint8/transitive tooling ไม่ใช่ audit finding |
| `npm run build` / production E2E | exit0 ทั้งคู่; 13/13 (28.2s) และ HTTPS smoke ผ่าน |
| `node infra/nginx/smoke-identity-https.mjs 4000 --e2e` | รอบแรก exit1: 12/13 timeout goto Login ก่อนส่ง auth; เพิ่ม diagnostic type/path ไม่บันทึกข้อมูลลับ แล้วรอบแยก exit0:13/13 (40.2s) พร้อม HTTPS smoke; ยังไม่ทราบ root cause ไม่อ้างแก้แล้ว |
| `dotnet list backend/TPR10.sln package --vulnerable --include-transitive` | exit0; ไม่พบ vulnerable packages ของ API/tests จาก NuGet feed ณ เวลาตรวจ |
| `npm audit` | exit0; ไม่พบช่องโหว่จาก feed ณ เวลารัน ไม่ใช่การรับรองไม่มีช่องโหว่ทั้งหมด |
| `git diff --check` | exit0 |

ใช้ Node 22.23.2 แยกจาก runtime เครื่อง, .NET SDK ตาม `global.json`, Docker PostgreSQL แยก ไม่ทดสอบฐานข้อมูลใช้งานจริง Browser ใช้ Firefox พร้อม trust CA เฉพาะ profile/Node process ไม่มี TLS bypass และไม่แก้ macOS trust store

### ผลตรวจสุดท้ายหลังแก้ review

โค้ดที่ตรวจอยู่ใน commit `7ab2f799cc36b36960d53cb6b5cf03b128753212` (ต่อจาก OpenAPI `b29d851`); ตรวจผลครบ **2026-09-23 16:01:24 UTC** หลังจากนี้เปลี่ยนเฉพาะเอกสาร ไม่เปลี่ยน runtime หรือ tests

| คำสั่ง / หลักฐาน | ผลจริง |
| --- | --- |
| ConditionalPermissionAuditTests | RED 8/8 → GREEN 8/8 (13 วินาที), ไม่ข้าม test |
| dotnet restore / test | exit0; 356/356, 0 skipped, 4 นาที 9 วินาที รวม OpenAPI39 และ regression8 |
| dotnet build / format --verify-no-changes | exit0; build 0 warnings/errors |
| npm ci / npm test / npm run lint | exit0; Node28/28, lintไม่มี warning |
| HTTPS E2E dev4000 | exit0; 13/13 (40.4s) และ TLS smoke ผ่าน |
| npm run build / HTTPS E2E prod4001 | exit0; 13/13 (28.7s) และ TLS smoke ผ่าน |
| NuGet vulnerable transitive / npm audit | exit0; ไม่พบช่องโหว่จาก feed ณ เวลาตรวจ |
| git diff --check / local documentation links | exit0; ลิงก์ภายในรายงาน8และrunbook3ถูกต้อง |

หลักฐานคำสั่งเต็มเก็บใน workspace ของแผน: `task9-review-red.log`, `task9-review-green.log`, `task9-final-backend.log`, `task9-final-node.log`, `task9-final-browser.log` ใช้ทดสอบจริงกับ PostgreSQL และ Firefox; harness ปิด container แล้ว ผล dev รอบสุดท้ายผ่านแต่ไม่ลบข้อจำกัด root cause ของ timeout รอบแรก

## 5. Security owner / Operations decision register

รายการทั้งหมดนี้ **ยังไม่มีผู้ลงนาม** ในรอบนี้ ให้บันทึกผู้อนุมัติ วันที่ และ reference ของหลักฐานที่ควบคุมการเข้าถึงได้ ไม่คัดลอก secret มาลงเอกสาร

| เรื่อง | ค่า/แนวทางพัฒนา | Owner / หลักฐานก่อนอนุมัติ |
| --- | --- | --- |
| Session | idle 30 นาที / absolute 8 ชั่วโมง / ไม่มี remember-me / revoke และ security version ทุก request | Security owner: policy sign-off และยอมรับกรณีหมดอายุ/สูญเสีย session |
| Password และ Argon2id | 15–128 Unicode scalars; m=65536 KiB,t=3,p=1 | Security + Operations: policy และ benchmark concurrency บนเครื่องเป้าหมาย |
| Lockout/rate limit | ผิด 5 ใน 15 นาทีพัก 15 นาที; per-IP/global process budget | Security + Operations: ประเมิน targeted lockout/DoS และออกแบบ distributed limit หากหลาย replicas |
| Transport | canonical HTTPS authority/allowlist/trusted loopback hop; public เฉพาะ reverse proxy | Operations: hostname, TLS owner, firewall และ proxy test บน deployment จริง |
| CSRF/key ring | อายุ 10 นาที; persistent protected key ring/certificate/private key | Security + Operations: access review, backup/restore/certificate rotation drill; ทดสอบไม่ใช่เพียง host start |
| Reset | random 32 bytes/hash/15 นาที/one-time; ไม่ auto-login | Security: operator identity proof, ช่องทางส่ง temporary password และการรับมือ response สูญหาย |
| MFA/recovery | role classes บังคับ, TOTP ±1 step, recent assurance 15 นาที, recovery 10 ชุด one-time | Security: mandatory-role classification, operator ผู้มี users:recover-mfa, ห้าม self recovery, break-glass ที่องค์กรอนุมัติ ไม่สร้าง backdoor |
| Email | Production EmailEnabled=false | Module 5 owner: adapter mode ที่องค์กรอนุญาต, worker/dedup/retry/retention/monitoring acceptance |
| Known limitations | รายการด้านล่างและผล independent review | Security/Product: จัดลำดับแก้หรือยอมรับความเสี่ยงอย่างชัดแจ้งก่อน deployment |

## 6. Minor เดิมและข้อจำกัดที่ไม่ถูกซ่อน

รายการเหล่านี้เป็นข้อสังเกตจาก review ก่อนหน้า ไม่ใช่ข้อยืนยันว่ามี authentication bypass; Task 9 ไม่ถือว่าหายเพราะ suite ผ่าน

1. Task 1: เพิ่ม malformed/canonical Base64 hash tests ที่ผ่าน length guard แล้ว
2. Task 1: เพิ่ม negative constraints tests ของ hash length/session stage/expiry/active-factor uniqueness
3. Task 2: PFX/password/private key โหลดจริงแบบ lazy บาง configuration เสียอาจ start ได้แต่ใช้งานไม่ได้; ต้องตรวจ key operation จริงก่อน production
4. Task 3: Login ซ้ำ 409 body ว่าง; OpenAPI ฉบับนี้ระบุตามจริง ยังไม่ได้เปลี่ยน runtime
5. Task 4: audit changed-fields อาจระบุ `active,roles` ทั้งที่เปลี่ยน roles อย่างเดียว ไม่กระทบ actor/target/atomicity
6. Task 5: pending MFA factor ขวาง session ใหม่ได้จน 10 นาทีหลังเริ่ม enrollment แม้ session เก่าหาย ต้องรอแล้ว login ใหม่
7. Task 6: grant rollback test ยังไม่มี target session จึงไม่ใช่หลักฐานตรงของ session row/cookie rollback ในกรณีนั้น
8. Task 7: enumeration comparison ยังไม่เทียบ eligible email account ในเงื่อนไข keys/email ครบ แม้มี eligible issuance test แยก
9. Task 8: session หมดอายุอาจทำให้ login ครั้งแรกได้ CSRF 403 และต้องกดซ้ำ; ไม่ retry mutation อัตโนมัติ

Task 8 เคยพบ dev hydration ไม่พร้อมหนึ่งรอบ ส่วน Task 9 พบรอ DOMContentLoaded ใน login navigation หนึ่งรอบก่อน auth ยังไม่พิสูจน์ว่าเหตุเดียวกัน แม้ diagnostic rerun ผ่านก็ไม่อ้างว่าแก้ root cause แล้ว Browser evidence เฉพาะ Firefox ไม่ใช่ Chrome/WebKit; restart key tests ไม่ใช่ disaster-recovery drill ของ production; metadata no-secret allowlist ไม่ใช่ DLP ทุกชนิด

รายงานเดิม: [Task 1](module-2-task-1-verification.md), [Task 2](module-2-task-2-verification.md), [Task 3](module-2-task-3-verification.md), [Task 4](module-2-task-4-verification.md), [Task 5](module-2-task-5-verification.md), [Task 6](module-2-task-6-verification.md), [Task 7](module-2-task-7-verification.md), [Task 8](module-2-task-8-verification.md)

## 7. Code Review ทั้ง branch

ผู้ตรวจอิสระ Ramanujan ตรวจ `6c7f1ae..b29d851` ทั้ง branch เทียบ baseline/plan หนึ่งรอบ ผล Critical 0 / Important 1 / Minor ใหม่ 2 และ Minor เดิม 9 ข้อ ผู้ตรวจรัน auth helper 5/5 และ diff check เอง ส่วน full suite ใช้หลักฐานผู้ทำหลัก ไม่อ้างว่าเป็น independent full rerun

Important I1: POST/PATCH บัญชีโดยผู้มี users:manage แต่ไม่มี roles:manage ถูกปฏิเสธถูกต้อง แต่ conditional denial ไม่เขียน audit เพราะผ่าน route policy ไปแล้ว แก้ด้วย helper ร่วมเขียน actor/capability/target/outcome/correlation และ commit audit ก่อนคืน 403 โดยเรียกก่อน business mutation ทั้งสองเส้นทาง Audit ล้มต้อง fail closed 503 และไม่แก้บัญชี เพิ่ม HTTP regression 8 กรณี POST/PATCH × roleIds ว่าง/ไม่ว่าง × audit ปกติ/ล้ม; RED 8/8 จาก audit หายหรือได้ 403 แทน 503 ก่อนแก้ ไม่ถือ namespace compile error ของ test เป็น RED

Minor ใหม่ที่เลื่อนแก้โดยแจ้งชัดเจน:

1. Self logout-all ใช้ compatibility writer ทำให้ actor_id เป็น null แต่ target_id ยังระบุผู้ใช้และ audit/revoke อยู่ transaction เดียวกัน จัดเป็น Minor เพราะยังระบุตัวบุคคลจาก target ได้ ไม่ใช่ event หาย; query ด้วย actor อย่างเดียวจะตกหล่น
2. OpenAPI ยังไม่ระบุ generic 500/default ของ anonymous CSRF denial เมื่อ audit ล้มบน non-auth route ที่ไม่มี session cookie; runtime fail closed และมี test อยู่แล้ว แต่ consumer ต้องรองรับ unexpected server error ไม่ใช่เฉพาะ 503

ไม่มี review รอบสอง: fix หลัง review ตรวจด้วย TDD และ full verification โดยผู้ทำหลัก ส่วน Minor ไม่ปะปนใน fix pass นี้

ข้อสรุปหลัง verification: I1 แก้แล้วตาม regression และ full suite; Critical/Important ค้าง 0 รับเฉพาะ technical deliverable เท่านั้น

### เรื่องที่ผู้ตรวจไม่ตัดสิน และคำวินิจฉัยของผู้ทำหลัก

| เรื่อง | คำวินิจฉัย / ผลหากถือว่าผ่านโดยไม่มีหลักฐาน |
| --- | --- |
| 1. Security owner policy/known limitations | ยังรอลงนาม ไม่ใช้การอนุมัติแผนแทน; หากข้ามอาจใช้นโยบายไม่ตรงองค์กร |
| 2. Production hostname/TLS/firewall/private routing/proxy | localhost fixture เท่านั้น ต้อง Operations ตรวจ deployment จริง; หากข้ามอาจเปิด API หรือเชื่อ forwarded header ผิด |
| 3. Gmail production delivery | ยังไม่มี adapter/worker; generic 202 ไม่ยืนยันว่าส่งอีเมลแล้ว หากตีความผิดผู้ใช้จะกู้บัญชีไม่ได้ |
| 4. Adapter exactly-once/retry/dedup/retention/monitoring | รอ Module 5 และ acceptance จริง; หากข้ามอาจส่งซ้ำ สูญหาย หรือเก็บข้อมูลเกินอายุ |
| 5. Assignment/data/field/cross-project scope | รอ Module 3 ไม่อ้าง identity permission เป็น business scope; หากข้ามเสี่ยงข้อมูลข้ามโครงการ |
| 6. AD/LDAP/SSO | มี boundary เท่านั้น ไม่ใช่ provider พร้อมใช้; หากข้ามจะวาง rollout บนความสามารถที่ไม่มี |
| 7. Argon/global lock capacity และ targeted lockout/DoS | ต้อง benchmark เครื่องเป้าหมาย; หากข้ามอาจทำให้ระบบช้าหรือปฏิเสธผู้ใช้ที่ถูกต้อง |
| 8. หลาย replica/distributed limiter | budget เป็นราย process; ต้องออกแบบก่อน scale หากข้ามจะใช้เพดานรวมเกิน policy |
| 9. Key backup/restore/certificate rotation/lost key | restart tests ไม่ใช่ recovery drill ขององค์กร; หากข้ามอาจกู้ MFA/CSRF keys ไม่ได้ |
| 10. Operator identity proof/recovery channel/break-glass | reason/reference ไม่พิสูจน์ตัวบุคคล ต้อง Security owner อนุมัติกระบวนการ; หากข้ามเสี่ยง social engineering |
| 11. Chrome/WebKit/multitab/history/BFCache ทุก path | หลักฐาน Firefox เฉพาะ cases ที่มี รวมไม่รับรอง MFA secret ทุก browser/history; ต้องทดสอบเพิ่มก่อนรองรับอย่างเป็นทางการ |
| 12. Dev timeout/hydration root cause | ยังไม่ทราบและไม่ยืนยันว่าเหตุเดียวกัน; rerun ผ่านไม่เท่ากับแก้แล้ว หากละเลยอาจพบ flaky navigation อีก |
| 13. Dependency security/source audit/override compatibility | pin และ audit ณ เวลาตรวจ ไม่รับรองถาวรหรือทุก dependency path; ต้องติดตาม advisory/compatibility ต่อเนื่อง |
| 14. Free text/logs/APM/proxy secret leak | metadata allowlist ไม่ใช่ DLP และไม่ได้ตรวจระบบ logging production; หากข้ามอาจมี secret จากผู้ใช้หรือ infrastructure |
| 15. Crash/network commit-response ambiguity/direct SQL/downgrade | ทดสอบ transaction ตาม API ไม่รับรองทุก failure หรือ bypass; ห้าม retry mutation/downgrade credential โดยไม่มี recovery plan มิฉะนั้นอาจทำซ้ำหรือสูญเสีย one-time protection |
| 16. หลัง b29d851 / handoff / merge / deploy / pilot | ผู้ทำหลักตรวจ fix และหลักฐานใหม่เอง ไม่อ้าง independent review ครอบคลุม commit หลังจากนั้น; การเผยแพร่ยังรอคำสั่ง หากข้ามจะรับรองเกินขอบเขต |

## 8. ข้อวินิจฉัยของ Task 9 และผลหากผิด

1. ใช้ linked worktree เดิม ไม่สร้างซ้ำ และรักษา checkout หลัก — หากผิดอาจสูญเสียงานผู้ใช้ จึงไม่ merge/push/ลบ worktree โดยอัตโนมัติ
2. เติม route `.Produces` เพราะ IResult ไม่เปิดเผย success schema; response contract DTO ไม่เปลี่ยน handler payload — หากภายหลัง payload เปลี่ยนต้องปรับ schema/test พร้อมกัน
3. อ่าน named policy จริง รวม conditional role assignment และ Active bypass ของ restricted middleware — หากผิด consumer จะเข้าใจสิทธิ์/stage ผิด จึงมี literal contract tests
4. คง body ว่างใน Login 409 ตามจริง ไม่ขยายไปแก้ Minor เดิม — หาก client สมมติทุก error มี JSON จะ parse ไม่ได้ จึงระบุข้อจำกัดใน OpenAPI/runbook
5. รัน Playwright ผ่าน harness ที่ตั้ง database/API/HTTPS/CA จริง ไม่รัน bare command โดยไม่มี stack — หากผิด acceptance จะทดสอบคนละ environment จึงไม่ใช้ TLS bypass
6. ตรวจทางเทคนิคแยก Security owner และ email adapter โดยไม่สร้าง approval แทนผู้ใช้ — หากข้ามเสี่ยง deployment ก่อน policy/operations พร้อม
7. Audit/dependency scans และ full tests จำกัดเฉพาะเวลารัน/สภาพทดสอบ — หากถือเป็นการรับรองถาวรอาจพลาดช่องโหว่หรือ configuration ใหม่ ต้องตรวจซ้ำก่อน deploy

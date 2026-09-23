# Module 3 — Organization and Scope Implementation Plan

> **สำหรับผู้พัฒนาแบบ agent:** ต้องใช้ `superpowers:executing-plans` ตามวิธี Native/inline ที่ผู้ใช้เลือกไว้ ดำเนินงานทีละ Task โดยใช้ checkbox (`- [ ]`) และ TDD; ผู้ตรวจอิสระตรวจทั้ง branch ก่อนปิดงาน ไม่เริ่ม implementation จนผู้ใช้ตรวจและอนุมัติแผนฉบับนี้

**Goal:** สร้างการจัดการโครงสร้างและ assignment ที่ทำให้ list/detail/write/export-simulation ใช้ได้เฉพาะ exact Workspace/Project/Site และบทบาทธุรกิจที่มอบหมายจริง

**Architecture:** ใช้ Modular Monolith และฐานข้อมูลเดิม เพิ่ม Organization/Scopes ที่แยก system permission ออกจาก scoped-business permission ใช้ `ScopeContext` ที่สร้างฝั่ง API เท่านั้น ทุก operation ที่อ่าน/เขียนข้อมูล materialize ภายใน transaction และตรวจสิทธิ์ปัจจุบันร่วมกับการ revoke ใช้ FK/unique/CHECK ช่วยรักษา hierarchy ไม่เพิ่ม RLS หรือ policy engine ใหม่

**Tech Stack:** ASP.NET Core/.NET 10 ตาม `global.json` (SDK10.0.401, latestPatch), EF Core/Npgsql/PostgreSQL เดิม; Next.js15.5.26/React19.3.0/TypeScript; Nodeตาม package engines (`^22.13.0 || >=24`), xUnit/PostgreSQL integration และ Playwright ผ่าน HTTPS fixture เดิม ไม่อัปเกรด dependency เป็นส่วนหนึ่งของแผนนี้

**Spec:** [Design Spec Module 3 ที่อนุมัติ](../specs/2026-09-24-module-3-organization-scope-design.md) และ [Baseline](../specs/2026-09-18-tpr10-module-0-architecture-baseline-design.md) ส่วน6–8/11/17/20

สถานะ: Task1–5 ผ่าน Test/Build/Lint/E2E และ Code Review แล้ว; Tasks6–9 ยังไม่เริ่ม รายละเอียดและ Minor ที่ค้างอยู่ใน [รายงาน Task1](../../architecture/module-3-task-1.md), [Task2](../../architecture/module-3-task-2.md), [Task3](../../architecture/module-3-task-3.md), [Task4](../../architecture/module-3-task-4.md) และ [Task5](../../architecture/module-3-task-5.md)
ฐานที่สำรวจ: runtime `ab6f30e`, Design Spec commit `99523a8` บน main; เมื่อเริ่ม implementation ให้ใช้ HEAD ที่มีแผนนี้และบันทึก SHA จริง ห้าม checkout กลับจนทำเอกสารที่อนุมัติหาย

## Global Constraints — ข้อกำหนดร่วม

- “Project assignment ไม่ครอบคลุม Site อัตโนมัติ ต้องระบุ Site แยกทุกแห่ง รวม Site ที่สร้างใหม่”
- “ค่า null หมายถึงระดับของ record ไม่ใช่ wildcard และไม่ใช่ ‘ทุก Site’” — query ต้องเทียบ nullable tuple แบบเท่ากัน ไม่ใช้ `ProjectId == null || ...`
- “ผู้ใช้คนเดียวมีบทบาทธุรกิจต่างกันตาม Project/Site ได้” — ห้ามรวม global capability กับ scoped capability
- Site assignment ไม่ให้สิทธิ์ record ระดับ parent; discovery ให้เฉพาะ breadcrumb ที่จำเป็น
- system-administration ไม่เป็น business assignment; ผู้ดูแลไม่มี data-plane bypass; ห้าม grant/replace/revoke assignment ของตนเอง
- ไม่มี self registration, hard delete, reparent, automatic assignment restoration, global business export, NAS หรือ workflow จริง
- ทุก unsafe request ใช้ `X-CSRF-Token`, Origin/Host และ session cookie เดิม; API เป็น authority, Nextควบคุมnavigationเท่านั้น
- “ผู้มี privileged assignment อย่างน้อยหนึ่งรายการต้องผ่าน MFA ใน login flow” และ recent assurance15นาทีเมื่อoperation/fieldกำหนด
- “Grant/revoke/replace assignment และ role-grant mutation ต้อง invalidate sessions ของผู้ได้รับผล” รวม affected users จาก user_roles และ scope assignments
- auditกับmutation/revoke/versionต้องatomic; read/export/denialที่กำหนดให้auditต้องไม่ส่งข้อมูลก่อนauditcommit
- UUID, UTC, ภาษาไทย/AsiaBangkok; code trim+uppercase ASCII `[A-Z0-9_-]{1,64}`; name1–200ไม่มีcontrol; reason1–500ไม่มีcontrol; noteไม่เกิน500ไม่มีcontrol
- pagination default25/max100, versionเริ่ม1และเพิ่มทุกeffectiveupdate, exportสูงสุด100ไม่มีsilenttruncate
- Development4000 / production start4001; technicalrecord API/UIเฉพาะDevelopment/Testing; productionใช้selector/administrationได้ตามสิทธิ์
- สร้าง worktree เมื่อเริ่มลงมือผ่าน using-git-worktrees เก็บ untracked `.pre-merge-backup.md` ของ main ไว้ ไม่รวมใน commit
- ต่อ task: RED→GREEN, focusedtests, commitเฉพาะไฟล์งาน; ก่อนส่งมอบต้องfullTest/Build/Lint/E2Eและreview ไม่push/mergeจากการอนุมัติแผนโดยอัตโนมัติ

## Review Focus — 5 failure modes และ Task เจ้าของ

ข้อกำหนดระหว่าง Tasks: เมื่อเพิ่ม route ต้องผูก authentication/policy ตั้งแต่ commit แรก ไม่รอ Task9; ปรับรายการ endpoint ใน `IdentityOpenApiTests.cs` เมื่อเพิ่มแต่ละกลุ่ม โดยคง assertions ของ identity routes ทั้ง26รายการไว้ ส่วน Task9 เพิ่ม assertions ของ scoped contract ให้ครบ แก้ catalog assertions เดิมที่คาด6 permissions ใน Task1 ให้ตรวจ12 permissions พร้อม domain และยืนยัน Administrator ได้เฉพาะ8 system permissions

1. Nullable scope กลายเป็นwildcardหรือroleของAใช้ในB รวมscopeที่ไม่มีอยู่/recordIDปลอม → Task5/6 ทดสอบสามระดับทั้งHTTPและrepository
2. Scoped Approver ไม่มีglobalroleแล้วหลุดMFA หรือgrantsเปลี่ยนแต่cookieเดิมใช้ได้ → Task2/4 ทดสอบlogin/session/forced-change/recoveryและaffected-userunion
3. Revoke/deactivate/disableแข่งกับread/write/exportหลังผ่านmiddleware → Task6/7 ใช้controlledbarriersกับPostgreSQLและตรวจทั้งสองลำดับcommit
4. Auditล้มหลังแก้assignment/record หรือexportปล่อยข้อมูลก่อนcommit → Task3/4/6/7 fault-injectionพร้อมตรวจrows/session/securityversion/auditและresponse
5. scopeหรือบัญชีเปลี่ยนแต่UI/cache/responseเก่ายังแสดงrestricteddata → Task8 ทดสอบdelayresponse, back/history, logoutและno-storeผ่านHTTPSจริง

## ลำดับงานและขอบเขตไฟล์

| Task | ส่งมอบที่ทดสอบได้ | พึ่งพา |
| --- | --- | --- |
| 1 | schema, permission domains, fixtures และmigration | Module2 |
| 2 | effective-role/MFA และlifecycleเชื่อมidentity | 1 |
| 3 | Organization API พร้อมdeactivatecascade | 1–2 |
| 4 | Assignment API พร้อมaudit/revocation | 1–3 |
| 5 | ScopeContext, evaluator และdiscovery | 1–4 |
| 6 | scopedrecord list/detail/create/update | 5 |
| 7 | export-simulationและrace/fault acceptance | 6 |
| 8 | Portal selector/administration/test UI | 3–7 |
| 9 | OpenAPI, independentreview, runbookและExitGate | 1–8 |

Pathทุกตัวด้านล่างเป็นrelativeต่อrepository เพื่อใช้ได้ในworktree; ไม่ใช่คำสั่งให้แก้mainทันที Entityใช้PascalCaseในC# และsnake_caseในSQLตามpatternเดิม

## สัญญาร่วมที่ทุก Task ต้องใช้ชื่อเดียวกัน

Task1สร้าง `Scopes/ScopeKey.cs` และ Task5สร้าง `Scopes/ScopeContext.cs`/`ScopeAccess.cs` ภายใต้ `backend/src/TPR10.Api/`:

```csharp
public sealed record ScopeKey(Guid WorkspaceId, Guid? ProjectId = null, Guid? SiteId = null)
{
    public bool IsValid => WorkspaceId != Guid.Empty
        && ProjectId != Guid.Empty && SiteId != Guid.Empty
        && (SiteId is null || ProjectId is not null);
}
// Constructor internal: transport DTO ไม่ใช่ authorization token.
public sealed class ScopeContext
{
    internal ScopeContext(Guid actorId, ScopeKey key, string capability,
        Guid actingRoleId, Guid assignmentId, bool canReadRestricted)
        => (ActorId, Key, Capability, ActingRoleId, AssignmentId, CanReadRestricted)
        = (actorId, key, capability, actingRoleId, assignmentId, canReadRestricted);
    public Guid ActorId { get; }
    public ScopeKey Key { get; }
    public string Capability { get; }
    public Guid ActingRoleId { get; }
    public Guid AssignmentId { get; }
    public bool CanReadRestricted { get; }
}
public sealed record ScopeDecision(ScopeContext? Context, int? Status, string? ProblemType);
public sealed record Page<T>(T[] Items, int Total, int PageNumber, int PageSize);
public sealed record ExpectedChange(long ExpectedVersion, string Reason);
```

`ScopeDecision` สำเร็จมีContextและStatus=null; ปฏิเสธมีContext=null/Status400,401,403,404; แปลงเป็นProblemDetailsมีcorrelationIdในHTTP boundary ห้ามส่งScopeContextตรงเป็นJSON ทุกreturnต้องmaterializeก่อนcommit; repositoryไม่คืนIQueryableออกนอกservice

### Contract ทดสอบร่วม (Task1 เป็นผู้สร้าง)

สร้าง `backend/tests/TPR10.Api.IntegrationTests/ScopeFixture.cs` ใช้IdentityTestDriverและTestKeyMaterialเดิม ไม่เพิ่มproductionseedendpoint:

```csharp
internal sealed record ScopeFixture(IdentityTestDriver Driver, Guid UserId,
    ScopeKey Workspace, ScopeKey Project, ScopeKey Site,
    ScopeKey SiblingSite, ScopeKey OtherSite)
{
    public static Task<ScopeFixture> CreateAsync(IdentityTestDriver driver);
    public Task<Guid> GrantAsync(ScopeKey key, string roleClass, params string[] capabilities);
    public Task<Guid> RecordAsync(ScopeKey key, string note, string? restricted = null);
    public string Path(ScopeKey key) => "/api/v1/workspaces/" + key.WorkspaceId
        + (key.ProjectId is { } p ? "/projects/" + p : "")
        + (key.SiteId is { } s ? "/sites/" + s : "") + "/scope-probe-records";
}
```

นี่คือsignatureที่ต้องimplementในTask1 ไม่ใช่abstractclassที่นำไปcompileทันที: `CreateAsync` seedบัญชี`scope-user`ด้วย`MfaTests.Password`และglobalpermissionsว่าง, W1/P1/S1/S2กับW2/P2/S3, codeไม่ซ้ำ, actor=UserId; `GrantAsync` สร้างroleUUIDใหม่classตามparameterแล้วผูกเฉพาะcatalogpermissionsที่ระบุ+assignmentของUserIdในtupleนั้น คืนassignmentUUID; `RecordAsync` INSERT note/restrictedในtupleคืนrecordUUID ทั้งหมดผ่านDbContextจริง ไม่เรียกAPIที่ยังไม่เกิดและใช้เฉพาะเตรียมfixtureก่อนlogin ห้ามใช้helperนี้พิสูจน์audit/revokeของproductionAPI

ตัวอย่างtestในแต่ละTaskวางใน `[Collection("database")]` class ที่รับ `PostgresFixture postgres`; C# namespaceของproductionตามfolder, testsใช้ `TPR10.Api.IntegrationTests`; เติมusingของชนิดที่แสดงและimplicitusingsของproject ตัวอย่างเป็นtestที่ต้องรันจริง ไม่ใช้missingnamespace/compileerrorเป็นหลักฐานRED ต้องใส่shape/stubที่compileได้ก่อนพิสูจน์behaviorที่ยังไม่มี

## Task 1: Schema และ permission domain ที่ไม่ขยายสิทธิ์เดิม

**สร้าง:** `backend/src/TPR10.Api/Organization/Data/OrganizationEntities.cs`, `OrganizationModelConfiguration.cs`; `backend/src/TPR10.Api/Scopes/ScopeKey.cs`, `Data/ScopeEntities.cs`, `Data/ScopeModelConfiguration.cs`, `ScopeCatalog.cs`; `backend/tests/TPR10.Api.IntegrationTests/ScopeFixture.cs`, `ScopeSchemaTests.cs`, `ScopeCatalogTests.cs`
**แก้:** `backend/src/TPR10.Api/Data/Tpr10DbContext.cs`, `Identity/Data/IdentityEntities.cs`, `Identity/Data/IdentityModelConfiguration.cs`, `Identity/Accounts/IdentityCatalog.cs`; `backend/src/TPR10.Api/Data/Migrations/Tpr10DbContextModelSnapshot.cs` และmigrationที่generateจากชื่อ `AddOrganizationScopeFoundation` พร้อมDesigner (timestampใช้ค่าที่toolสร้างจริง ไม่เดา)
**รับ/ส่ง:** รับDbContextเดิม; ส่งScopeKey, entitiesและcatalog12capabilities (6เดิม+6ใหม่) โดย8system/4scoped-business

- [x] เขียนschemaREDที่อ่านcatalog PostgreSQLก่อนเพิ่มmigration:

```csharp
[Fact]
public async Task Assignment_table_is_present_after_migration()
{
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
    await using var db = d.Database.CreateContext();
    var names = await db.Database.SqlQueryRaw<string>(
        "SELECT tablename AS \"Value\" FROM pg_tables WHERE schemaname='public'").ToArrayAsync();
    Assert.Contains("user_scope_assignments", names);
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~ScopeSchemaTests` คาดFAILไม่มีtable แล้วเพิ่มentitiesตามตารางนี้; UUIDFKuser/roleใช้entitiesเดิม ไม่cascade-delete:

| Entity | Columns ที่ต้องมีเพิ่มเติมจาก Id |
| --- | --- |
| Workspace | Code,Name,IsActive,Version(long=1),CreatedAtUtc,CreatedBy,UpdatedAtUtc?,UpdatedBy? |
| Department | WorkspaceId และfieldsเดียวกับWorkspace |
| Project | WorkspaceId และfieldsเดียวกับWorkspace |
| Site | WorkspaceId,ProjectId และfieldsเดียวกับWorkspace |
| ScopeAssignment | UserId,WorkspaceId,ProjectId?,SiteId?,RoleId,CreatedAtUtc,CreatedBy,Reason,RevokedAtUtc?,RevokedBy?,RevocationReason?,Version=1 |
| ScopeProbeRecord | WorkspaceId,ProjectId?,SiteId?,Note,RestrictedNote?,Version=1,CreatedAtUtc,CreatedBy,UpdatedAtUtc?,UpdatedBy? |
| IdentityPermission (เดิม) | Domain(string) CHECK `system`/`scoped-business`, backfillเดิม=`system` |

- [x] กำหนดalternatekeys `projects(workspace_id,id)` และ `sites(workspace_id,project_id,id)`; compositeFKของSite/assignment/recordต้องตรงparent; assignmentและrecordมีworkspaceFKเสมอ และsite=>project CHECK ตั้งnullableFKแบบMATCH SIMPLEโดยมีCHECKคุมshape ไม่ใช้MATCH FULLที่ปฏิเสธworkspace-only
- [x] เพิ่มpartialuniqueindexesสามระดับและCHECKmetadata/schema testsยิงSQLตรงให้ล้มตามconstraint ไม่พึ่งvalidationAPIอย่างเดียว:

```sql
CREATE UNIQUE INDEX ux_assignment_workspace ON user_scope_assignments(user_id,workspace_id,role_id)
WHERE revoked_at_utc IS NULL AND project_id IS NULL AND site_id IS NULL;
CREATE UNIQUE INDEX ux_assignment_project ON user_scope_assignments(user_id,workspace_id,project_id,role_id)
WHERE revoked_at_utc IS NULL AND project_id IS NOT NULL AND site_id IS NULL;
CREATE UNIQUE INDEX ux_assignment_site ON user_scope_assignments(user_id,workspace_id,project_id,site_id,role_id)
WHERE revoked_at_utc IS NULL AND site_id IS NOT NULL;
-- ทั้งassignmentและrecord
CHECK (site_id IS NULL OR project_id IS NOT NULL);
-- assignment: revokeทั้งสามfieldมีค่าพร้อมกันหรือnullพร้อมกัน
CHECK ((revoked_at_utc IS NULL AND revoked_by IS NULL AND revocation_reason IS NULL)
 OR (revoked_at_utc IS NOT NULL AND revoked_by IS NOT NULL AND revocation_reason IS NOT NULL));
```

- [x] SeedpermissionUUIDคงที่ `20000000-0000-0000-0000-000000000007` ถึง`...000012`: organization:manage, scope-assignments:manage, scope-probe:read/write/export/restricted-read ตามลำดับ; สองแรกsystem ที่เหลือscoped-business ตรวจทั้งmigrationและfreshBootstrapService/IdentityCatalog path อย่าให้loopเดิมgrantทุกcapabilityแก่Administrator
- [x] สร้างScopeFixtureตามcontractข้างต้นและnegative tests: malformedScopeKey, crossworkspaceproject, wrongprojectsite, emptyUUIDAPIshape, duplicateสามระดับ, revokeแล้วgrantใหม่ได้, auditเดิมคงอยู่หลังmigrate/down/up; noorganization/assignmentproductionseed
- [x] GenerateและตรวจSQL ไม่ใช้EnsureCreatedแทนmigration:

```bash
dotnet ef migrations add AddOrganizationScopeFoundation --project backend/src/TPR10.Api
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~ScopeSchemaTests|FullyQualifiedName~ScopeCatalogTests'
git diff --check
git add backend/src/TPR10.Api/Organization backend/src/TPR10.Api/Scopes backend/src/TPR10.Api/Data backend/src/TPR10.Api/Identity backend/tests/TPR10.Api.IntegrationTests/ScopeFixture.cs backend/tests/TPR10.Api.IntegrationTests/ScopeSchemaTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopeCatalogTests.cs
git commit -m "feat: add organization scope schema and permission domains"
```

## Task 2: Effective roles, MFA และ lifecycle ของ scoped users

**สร้าง:** `backend/src/TPR10.Api/Identity/IEffectiveRolePolicy.cs`, `Organization/EffectiveRolePolicy.cs`, `Scopes/Assignments/AssignmentLifecycle.cs`; tests `ScopedMfaTests.cs`, `ScopedIdentityLifecycleTests.cs`
**แก้:** `Identity/Sessions/LoginService.cs`, `SessionService.cs`, `Identity/IdentityRegistration.cs`, `Identity/Authorization/PermissionHandler.cs`, `PermissionMutationGuard.cs`, `Identity/Accounts/RoleAdministration.cs`, `AccountProvisioning.cs` ภายใต้API; ตรวจcallers `Identity/Reset/PasswordResetService.cs`, `Identity/Mfa/MfaService.cs`, `Identity/Mfa/OperatorMfaRecovery.cs` ว่าstage/recoveryยังวิ่งผ่านpolicyเดียวกัน
**รับ:** entitiesTask1, `ISessionService.RevokeUserAsync(Guid,string,CancellationToken)` เดิม
**ส่ง:** `IEffectiveRolePolicy.RequiresMfaAsync(Guid,CancellationToken):Task<bool>`; `AffectedUsersAsync(Guid roleId,CancellationToken):Task<Guid[]>`; `AssignmentLifecycle.RevokeForUserAsync(Guid userId,Guid actorId,string reason,CancellationToken):Task<int>` และ `RevokeForScopeAsync(ScopeKey,Guid,string,CancellationToken):Task<Guid[]>` (ต้องมีcallertransaction, ไม่commit/revokeSessionเอง)

- [x] เพิ่มREDใช้scopedApproverโดยไม่มีglobalprivilegedrole:

```csharp
[Fact]
public async Task Scoped_approver_requires_enrollment_without_global_privilege()
{
    using var keys = new TestKeyMaterial();
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
    var f = await ScopeFixture.CreateAsync(d);
    await f.GrantAsync(f.Site, "approval", "scope-probe:read");
    using var response = await d.LoginAsync("scope-user", MfaTests.Password);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert.Equal("MfaEnrollmentRequired", json.RootElement.GetProperty("stage").GetString());
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~ScopedMfaTests` คาดActiveแทนEnrollment แล้วใช้policyกลางที่ OR globalprivileged กับ activeassignment/activeancestors/privilegedclass; accountinactive=false; ไม่รวมpermissionsข้ามscope

```csharp
// ในLoginService และSessionService ใช้interfaceเดียวกัน แทนqueryglobal-only
var mandatory = await roles.RequiresMfaAsync(user.Id, ct);
// session permission/global authorization queries จำกัดเฉพาะ system domain
where p.Domain == "system"
// affected-users ของrole grant mutation: distinct union แล้วOrderByก่อนrevoke
var affected = globalUsers.Union(scopedUsers).Distinct().OrderBy(id => id);
```

- [x] Testsเพิ่ม: revoke/inactiveancestorไม่สร้างmandatoryจากassignmentนั้น, confirmedfactorยังบังคับMFA, expiry15min, forcedchangeแล้วloginเข้าMFA, recoveryไม่ให้recentassurance, directValidateAsyncactivecookieไม่มีMFAเมื่อgrantprivilegedใหม่ถูกปฏิเสธ
- [x] GlobalPermissionHandler/MutationGuard/SessionViewกรองDomain=system; เพิ่มtestว่าglobalroleมีscope-probe:readไม่ปรากฏในglobalSessionView; scopedroleมีusers:manageไม่เปิดmanagementroute ไม่เปลี่ยนlastadmininvariantเดิม
- [x] ImplementAssignmentLifecycleให้setRevokedAt/By/ReasonและVersion++เฉพาะactive ระบุauditสำหรับแต่ละassignmentร่วมtransaction; disableaccountเรียกก่อนRevokeUserAsync; enableไม่unrevoke; RoleAdministration.GrantsAsyncใช้affectedunionและrevokeแต่ละuserครั้งเดียว ทั้งหมดภายใต้lock7241002เดิม
- [x] Fault-injectionaudittriggerตรวจdisable/grantchange rollbackบัญชี/assignment/securityversion/sessionจริง; ใช้targetclientloginและGETsessionยืนยันcookieยังvalidเมื่อrollback ไม่ตรวจเฉพาะrowcount
- [x] รันfocusedรวมidentityregressionก่อนcommit (Task2 focused29 และ full regression424 ผ่าน; task-done รัน filter ด้านล่างซ้ำหลัง commit):

```bash
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~ScopedMfaTests|FullyQualifiedName~ScopedIdentityLifecycleTests|FullyQualifiedName~Mfa|FullyQualifiedName~Session|FullyQualifiedName~RoleAuthorizationTests'
git add backend/src/TPR10.Api/Identity backend/src/TPR10.Api/Organization/EffectiveRolePolicy.cs backend/src/TPR10.Api/Scopes/Assignments backend/tests/TPR10.Api.IntegrationTests/ScopedMfaTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopedIdentityLifecycleTests.cs
git commit -m "feat: integrate scoped roles with MFA and identity revocation"
```

## Task 3: Organization management API และ cascade lifecycle

**สร้าง:** `backend/src/TPR10.Api/Organization/OrganizationContracts.cs`, `OrganizationService.cs`, `OrganizationEndpoints.cs`, `OrganizationRegistration.cs`, `OrganizationValidation.cs`; tests `OrganizationApiTests.cs`, `OrganizationLifecycleTests.cs`
**แก้:** `Program.cs`, `Identity/IdentityRegistration.cs`, `Auditing/AuditEventWriter.cs`
**รับ:** lifecycleTask2, PermissionMutationGuard, audit/sessionเดิม
**ส่ง:** `AddOrganizationScope():IServiceCollection`, `MapOrganizationEndpoints():IEndpointRouteBuilder`; `OrganizationService.CreateAsync(Guid actorId, OrganizationKind kind, ScopeKey? parent, CreateOrganization request, CancellationToken):Task<IResult>`; `UpdateAsync(Guid actorId,OrganizationKind kind,ScopeKey? expectedParent,Guid id,UpdateOrganization request,CancellationToken ct):Task<IResult>`; `ListAsync(OrganizationKind,ScopeKey?,int,int,CancellationToken):Task<IResult>`

```csharp
public enum OrganizationKind { Workspace, Department, Project, Site }
public sealed record CreateOrganization(string Code, string Name);
public sealed record UpdateOrganization(string Name, bool IsActive, long ExpectedVersion, string Reason);
public sealed record OrganizationView(Guid Id, Guid WorkspaceId, Guid? ProjectId,
    string Code, string Name, bool IsActive, long Version);
```

WorkspaceView.WorkspaceId=Id; Department/Project.ProjectId=null; Site.ProjectIdเป็นparent; code/parentimmutableในPATCH รับName+IsActiveแบบเต็มเพื่อไม่มีoptionalboolกำกวม

- [x] RED HTTPcreateโดยadminMFA (ก่อนmapได้404):

```csharp
[Fact]
public async Task Management_can_create_workspace_but_not_business_assignment()
{
    using var keys = new TestKeyMaterial();
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
    await RoleAuthorizationTests.AdminAsync(d);
    using var response = await d.PostAsync("/api/v1/organization/workspaces", new { code=" west ", name="ฝ่ายตะวันตก" });
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    await using var db = d.Database.CreateContext();
    Assert.Empty(await db.Set<ScopeAssignment>().ToArrayAsync());
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~OrganizationApiTests` ให้RED แล้วmapGET/POST `/api/v1/organization/workspaces`; GET/POST `/workspaces/{w}/departments`, `/workspaces/{w}/projects`, `/workspaces/{w}/projects/{p}/sites`; PATCHตามresourcepath+`/{id}` โดยทุกเส้นทางrequireorganization:manage+MFA+CSRFสำหรับunsafe
- [x] UpdateAsync ตรวจ expectedParent ภายใน transaction ของ service และตอบ404เมื่อ id อยู่นอก route parent; endpoint ส่ง tuple จาก route ตาม signature ข้างต้น ห้าม lookup id โดยไม่ตรวจ ancestry
- [x] Transactionลำดับbegin→advisorylock7241002→guardrecheck→validation/version→change→cascadeassignment→distinctaffecteduserrevoke→audit→save→commit; lifecycleTask2ไม่commitซ้อน

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
// guard/validationก่อนแก้trackedentity; เมื่อdeactivateparentสำเร็จ
var affected = await lifecycle.RevokeForScopeAsync(key, actorId, request.Reason, ct);
foreach (var id in affected.Distinct().OrderBy(x => x))
    await sessions.RevokeUserAsync(id, "organization-deactivated", ct);
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

- [x] เพิ่มtestsGETpaginationdefault25/max100/offsetoverflow, codecase/duplicateunicodecontrol, parentwrongworkspace, missingMFA403/anon401, disabledparentrejectcreate409, staleversion409, Departmentdeactivateไม่revokeSite, parentreactivateไม่restoreassignment, auditfail503rollbackallและไม่มีsecretmetadata
- [x] Auditwriterเพิ่มเฉพาะkeysตามspec; managementdenyที่เกิดหลังrouteผ่านต้องwriteauditด้วย ทำก่อนmutationหรือrollbackbusinesschangesก่อนcommitdenial ห้ามcommitdenialพร้อมtrackedchangesที่ทำไปแล้ว
- [x] รันtestsและcommit:

```bash
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~OrganizationApiTests|FullyQualifiedName~OrganizationLifecycleTests'
git add backend/src/TPR10.Api/Organization backend/src/TPR10.Api/Program.cs backend/src/TPR10.Api/Identity/IdentityRegistration.cs backend/src/TPR10.Api/Auditing/AuditEventWriter.cs backend/tests/TPR10.Api.IntegrationTests/OrganizationApiTests.cs backend/tests/TPR10.Api.IntegrationTests/OrganizationLifecycleTests.cs
git commit -m "feat: add organization administration and cascade revocation"
```

## Task 4: Assignment API ที่ไม่ให้ผู้ดูแลเพิ่มสิทธิ์ตัวเอง

**สร้าง:** `backend/src/TPR10.Api/Scopes/Assignments/AssignmentContracts.cs`, `AssignmentService.cs`, `AssignmentEndpoints.cs`; tests `AssignmentApiTests.cs`, `AssignmentAtomicityTests.cs`
**แก้:** `Organization/OrganizationRegistration.cs`, `Program.cs`
**รับ:** entities/lifecycle, systemguard, sessions/audit
**ส่ง:** `AssignmentService.GrantAsync(Guid actorId,GrantAssignment,CancellationToken):Task<IResult>`, `ReplaceAsync(Guid actorId,Guid assignmentId,ReplaceAssignment,CancellationToken):Task<IResult>`, `RevokeAsync(Guid actorId,Guid assignmentId,ExpectedChange,CancellationToken):Task<IResult>`

```csharp
public sealed record GrantAssignment(Guid UserId, ScopeKey Scope, Guid RoleId, string Reason);
public sealed record ReplaceAssignment(ScopeKey Scope, Guid RoleId, long ExpectedVersion, string Reason);
public sealed record AssignmentView(Guid Id, Guid UserId, ScopeKey Scope, Guid RoleId,
    long Version, DateTimeOffset? RevokedAtUtc);
```

- [x] RED selfgrant403ก่อนเปิดendpoint; grantuserอื่นpositive201พร้อมoldcookie401; ใช้actorจากRequestSessionเท่านั้น:

```csharp
[Fact]
public async Task Administrator_cannot_grant_business_assignment_to_self()
{
    using var keys = new TestKeyMaterial();
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
    var f = await ScopeFixture.CreateAsync(d);
    var actor = await RoleAuthorizationTests.AdminAsync(d);
    using var r = await d.PostAsync("/api/v1/scope-assignments", new {
        userId=actor, scope=f.Site, roleId=IdentityCatalog.StaffRoleId, reason="ทดสอบห้าม self grant" });
    Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    await using var db = d.Database.CreateContext();
    Assert.False(await db.Set<ScopeAssignment>().AnyAsync(x => x.UserId == actor));
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AssignmentApiTests` คาด404ไม่ใช่403 แล้วmapGET/POST `/api/v1/scope-assignments`, POST `/{id}/replace`, POST `/{id}/revoke` Require scope-assignments:manage recentMFA ทุกmethod; GETfilter userId/scope/revoked+page25/max100
- [x] เพิ่มGET `/api/v1/scope-assignments/options/users` และ`/options/roles` ด้วยcapabilityเดียวกันเพื่อUIที่ไม่มีusers:manage/roles:read: paginateและsearchprefixสูงสุด100; usersคืนId/Username/IsActiveเท่านั้น ไม่email/MFA/credential; rolesคืนId/Name/RoleClass/BusinessCapabilities ไม่grantAdministratorclass เส้นทางนี้ไม่แก้สิทธิ์Module2
- [x] Grantตรวจactiveuser/ancestors+tuple+roleclass/reasonก่อนinsert; duplicate409; Replaceห้ามเปลี่ยนUserId, optimisticversion, revokeเก่า+createใหม่atomic; Revokeซ้ำversionเก่าตอบ409; no-opreplaceคืน200เดิมไม่เพิ่มversion/revoke ไม่คืนrolepermissionจากscopeอื่น
- [x] Revocationและauditต้องใช้actorจากsessionในTX7241002; auditstatusdeniedก่อนmutationสำหรับselfgrant/roleclassinvalid; เพิ่มtestsbodyactorIdfake, systemrole forbidden400, targetinactive409, crossparent404, duplicateconcurrency201+409, replaceconflictไม่revokerows/session
- [x] Auditfaulttestติดtriggerหลังเตรียมadminและtargetlogin (รวมCSRFtokenก่อนtrigger): POSTgrant/replace/revokeได้503, assignment/user.SecurityVersion/sessionrowsเท่าเดิม, targetcookieGETsessionยัง200หลังยกfault; ไม่ใช้fixtureGrantAsyncเป็นassertproductionbehavior
- [x] GREENและcommit:

```bash
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AssignmentApiTests|FullyQualifiedName~AssignmentAtomicityTests'
git add backend/src/TPR10.Api/Scopes/Assignments backend/src/TPR10.Api/Organization/OrganizationRegistration.cs backend/src/TPR10.Api/Program.cs backend/tests/TPR10.Api.IntegrationTests/AssignmentApiTests.cs backend/tests/TPR10.Api.IntegrationTests/AssignmentAtomicityTests.cs
git commit -m "feat: manage explicit scoped role assignments atomically"
```

## Task 5: ScopeContext, scoped authorization และdiscovery

**สร้าง:** `backend/src/TPR10.Api/Scopes/ScopeContext.cs`, `ScopeAccess.cs`, `ScopeDiscovery.cs`, `ScopeEndpoints.cs`, `ScopeOperation.cs`; tests `ScopeAuthorizationTests.cs`, `ScopeDiscoveryTests.cs`
**แก้:** `Organization/OrganizationRegistration.cs`, `Program.cs`
**รับ:** ScopeKey/schema/RequestSession/IEffectiveRolePolicy; **ส่ง:** `ScopeAccess.ResolveAsync(ScopeKey key,string capability,bool requireMfa,CancellationToken):Task<ScopeDecision>` (ต้องอยู่TX7241002); `ScopeDiscovery.ListAsync(int page,int pageSize,CancellationToken):Task<IResult>`; `ScopeOperation.RunAsync(ScopeKey,string,bool,Func<ScopeContext,CancellationToken,Task<IResult>>,CancellationToken):Task<IResult>` เป็นเจ้าของtransaction

- [x] RED discoveryไม่คืนparentสิทธิ์หรือsibling:

```csharp
[Fact]
public async Task Site_discovery_does_not_inherit_parent_or_sibling_permissions()
{
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
    var f = await ScopeFixture.CreateAsync(d);
    await f.GrantAsync(f.Site, "staff", "scope-probe:read");
    Assert.Equal(HttpStatusCode.OK, (await d.LoginAsync("scope-user", MfaTests.Password)).StatusCode);
    using var response = await d.Client.GetAsync("/api/v1/scopes");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
    Assert.Equal(f.Site.SiteId, item.GetProperty("scope").GetProperty("siteId").GetGuid());
}
```

- [x] `dotnet test backend/TPR10.sln --filter FullyQualifiedName~ScopeDiscoveryTests` คาด404ก่อนmap; GET/scopesใช้authenticatedActive policyที่ไม่ต้องglobalnamedpermission มิฉะนั้นscoped-onlyuserเข้าไม่ได้ ใช้stageguardเดิมและservice recheck
- [x] ResolveAsyncrevalidate session DBภายในlock (id/user/securityversion/expiry/revoked/idle/account/forcedchange/stage) ก่อนqueryscope; checkshape400→session401→stage/MFA403→hierarchy/assignment404→capability403 ในschemaที่ไม่leakunknownscope

```csharp
var grants = await (from a in db.Set<ScopeAssignment>()
                    join rp in db.Set<RolePermission>() on a.RoleId equals rp.RoleId
                    join p in db.Set<IdentityPermission>() on rp.PermissionId equals p.Id
                    where a.UserId == actorId && a.RevokedAtUtc == null
                       && a.WorkspaceId == key.WorkspaceId && a.ProjectId == key.ProjectId
                       && a.SiteId == key.SiteId && p.Domain == "scoped-business"
                    select new { a.Id, a.RoleId, p.Capability }).ToArrayAsync(ct);
// activeancestorsและaccountตรวจแยกภายในtransactionเดียวกัน; capabilityจำเป็นต้องมีจริง
var selected = grants.Where(g => g.Capability == capability)
    .OrderBy(g => g.RoleId).ThenBy(g => g.Id).FirstOrDefault();
```

- [x] CanReadRestrictedต้องมีrestricted-readในexactscopeและrecentMFAจริง ไม่ให้fieldเพิ่มเพราะglobalrole; export/privilegedrolesmissingMFAให้403 ส่วนordinaryreadที่ไม่มีrestrictedpermissionomitfield; ผู้มีrestrictedpermissionแต่assuranceหมดไม่ปล่อยrestricteddata
- [x] Discoveryqueryassignmentก่อนjoinsmetadata คืน`ScopeChoice(ScopeKey Scope,string WorkspaceName,string? ProjectName,string? SiteName,string[] Capabilities)` paginateexacttuplesหลังdedupe stableordertuple; รวมcapabilitiesเฉพาะtuple ไม่ส่งuserlist/auditdata; emptylist200เมื่อไม่มีassignment; ไม่มีrestricteddata
- [x] Discovery เป็นเจ้าของ transaction ของตนเอง: acquire advisory lock7241002, ตรวจ session/stage ปัจจุบัน, materialize รายการและ audit ก่อน commit เช่นเดียวกับ read data plane ไม่ใช้ผลจาก middleware อย่างเดียว
- [x] ScopeOperationสร้างcontextแล้วmaterializeoperation/save/auditcommitก่อนreturn; แยกdenialauditในtransactionที่ไม่มีbusinesschanges หากcallbackตอบerrorหลังมีchangesต้องrollbackแล้วauditใหม่ ไม่commitpartialstate; auditfailure503ปิดresponse; mapเฉพาะknownDBfault ไม่เผยrawexception
- [x] เพิ่มunit/serviceHTTPtestsสามระดับ+NoAssignmentAdmin+globalbusinessgrant+scopedusersmanage+revoked/inactive/unknownsite+forgedtuple, compare404bodyexceptcorrelation; discoveryauditต้องสำเร็จก่อนคืนlist; runGREENแล้วcommit:

```bash
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~ScopeAuthorizationTests|FullyQualifiedName~ScopeDiscoveryTests'
git add backend/src/TPR10.Api/Scopes backend/src/TPR10.Api/Organization/OrganizationRegistration.cs backend/src/TPR10.Api/Program.cs backend/tests/TPR10.Api.IntegrationTests/ScopeAuthorizationTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopeDiscoveryTests.cs
git commit -m "feat: enforce exact scope authorization and safe discovery"
```

## Task 6: Scoped repository และ technical record HTTP

**สร้าง:** `backend/src/TPR10.Api/Scopes/Probes/ScopeProbeRepository.cs`, `ScopeProbeContracts.cs`, `ScopeProbeService.cs`, `ScopeProbeEndpoints.cs`; tests `ScopeRecordTests.cs`, `ScopeRecordSafetyTests.cs`, `ScopeRaceTests.cs`, `ScopeRaceBarrier.cs`
**แก้:** `Organization/OrganizationRegistration.cs`, `Program.cs`, `backend/tests/TPR10.Api.IntegrationTests/ApiFactory.cs` เฉพาะtesthookregistration
**ส่ง:** `ScopeProbeRepository.ListAsync(ScopeContext,int,int,CancellationToken):Task<Page<ScopeProbeRecord>>`, `FindAsync(ScopeContext,Guid,CancellationToken):Task<ScopeProbeRecord?>`; `ScopeProbeService.ListAsync(ScopeKey,int,int,CancellationToken):Task<IResult>`, `DetailAsync(ScopeKey,Guid,CancellationToken)`, `CreateAsync(ScopeKey,CreateScopeRecord,CancellationToken)`, `UpdateAsync(ScopeKey,Guid,UpdateScopeRecord,CancellationToken)` ที่เหลือคืนTask<IResult>

```csharp
public sealed record CreateScopeRecord(string Note, JsonElement RestrictedNote = default);
public sealed record UpdateScopeRecord(string Note, JsonElement RestrictedNote, long ExpectedVersion);
// PATCH: absent=คงrestrictedเดิม; JSON null=ล้าง; string=แทนค่า ต้องมีwrite+restricted-read+MFAถ้าส่งfield
// JsonElement.ValueKind: Undefined=ไม่ส่ง, Null=ล้าง, String=แทนค่า; ชนิดอื่นตอบ400
```

- [ ] REDIDORด้วยSiteจริงและrecordคนละWorkspace:

```csharp
[Fact]
public async Task Detail_cannot_load_foreign_record_by_known_id()
{
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
    var f = await ScopeFixture.CreateAsync(d);
    await f.GrantAsync(f.Site, "staff", "scope-probe:read");
    var foreign = await f.RecordAsync(f.OtherSite, "ข้อความข้ามพื้นที่", "ห้ามรั่ว");
    await d.LoginAsync("scope-user", MfaTests.Password);
    using var r = await d.Client.GetAsync(f.Path(f.Site) + "/" + foreign);
    Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    Assert.DoesNotContain("ห้ามรั่ว", await r.Content.ReadAsStringAsync());
    // ต้องมีpositivecontrolเพื่อไม่ให้endpoint404ที่ยังไม่สร้างทำtestผ่านหลอก
    var own = await f.RecordAsync(f.Site, "ของตนเอง");
    Assert.Equal(HttpStatusCode.OK, (await d.Client.GetAsync(f.Path(f.Site)+"/"+own)).StatusCode);
}
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~ScopeRecordTests` คาดpositivecontrol404; Map5methodsต่อscopelevelในDevelopment/Testingเท่านั้น (exportอยู่Task7) ไม่ใช้GUIDrouteconstraintที่คืน404แทนmalformedUUID400; bind Guid parametersให้frameworkvalidate400
- [ ] Repositoryใช้exactpredicateก่อนFirst/Count/Skip/Takeและupdate ห้ามfallbackFind(id) unscoped:

```csharp
private IQueryable<ScopeProbeRecord> Query(ScopeContext c) => db.Set<ScopeProbeRecord>()
    .Where(r => r.WorkspaceId == c.Key.WorkspaceId
             && r.ProjectId == c.Key.ProjectId && r.SiteId == c.Key.SiteId);
// Queryเป็นprivateเท่านั้น ไม่มีpublicunscopedoverload
var row = await Query(context).SingleOrDefaultAsync(r => r.Id == id, ct);
```

- [ ] Createใช้scopeจากcontextและactorจากsession; rejectunknownbodyfieldsด้วยper-DTO JsonUnmappedMemberHandling.Disallow เพื่อactorId/workspaceIdไม่ถูกignoreแล้วทำให้clientเข้าใจผิด; ใช้ JsonElement แบบ non-nullable ตามสัญญาข้างต้น ไม่ต้องเพิ่ม converter; ทั้ง POST/PATCH ต้องตรวจสิทธิ์เมื่อ ValueKind ไม่ใช่ Undefined รวมการส่ง null และทดสอบ absent/null/string/number แยกกัน
- [ ] Outputใช้DTOแยกPublicRecordView(Id,Note,Version,CreatedAtUtc,UpdatedAtUtc) และRestrictedRecordViewเพิ่มRestrictedNote; ไม่มีpermissionต้องไม่มีpropertyในJSONแม้ค่าnull ทุกmethodใช้selectorเดียวกัน; write-onlyresponseไม่คืนnote/restricteddataที่ไม่มีread ให้201/200 `{id,version}` พร้อมLocation
- [ ] Paginationตรวจpositive/max100/offsetoverflow stableCreatedAtUtc+Id, countในscope; version409/auditvalidationก่อนchange; commitauditread/list/create/updateพร้อมrowcountและcontextactorrole ไม่มีbodyในmetadata
- [ ] Testsเจาะnulltupleทั้ง3ระดับ, ProjectnotSite, Sitenotparent/sibling, newSite, globalAdminNoAssignment, fieldread/writeabsence/null, readonlycannotwrite, extrabodyfields400, duplicate/staleversion, auditfailure503rollbackrows/versions/session
- [ ] RaceBarrierใช้DbCommandInterceptorในApiFactoryเฉพาะtestsจับคำสั่งadvisorylock7241002กับTaskCompletionSourceเพื่อpauseก่อนlock/หลังlock ไม่สร้างproductionHTTPbarrier ใช้สองcontexts/clients; กำหนดtimeout10sเฉพาะfail-safe ไม่ใช้Sleepเป็นorder assertion; revokecommitก่อนreleaseoperationได้401หรือ404ตามsessionrecheck ห้าม200 ส่วนoperationcommitก่อนrevokeได้200และคำขอถัดไป401
- [ ] รันGREENและcommit:

```bash
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~ScopeRecord|FullyQualifiedName~ScopeRace'
git add backend/src/TPR10.Api/Scopes/Probes backend/src/TPR10.Api/Organization/OrganizationRegistration.cs backend/src/TPR10.Api/Program.cs backend/tests/TPR10.Api.IntegrationTests/ScopeRecordTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopeRecordSafetyTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopeRaceTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopeRaceBarrier.cs backend/tests/TPR10.Api.IntegrationTests/ApiFactory.cs
git commit -m "feat: add scope-bound records and field visibility enforcement"
```

## Task 7: Export-simulation และ acceptance ของ transaction

**สร้าง:** `backend/src/TPR10.Api/Scopes/Probes/ScopeExportService.cs`; tests `ScopeExportTests.cs`, `ScopeAuditFailureTests.cs`
**แก้:** `ScopeProbeContracts.cs`, `ScopeProbeEndpoints.cs`, `Organization/OrganizationRegistration.cs`, `ScopeRaceTests.cs` ตามpathTask6
**รับ:** ScopeOperation/Repository/DTOprojection; **ส่ง:** `ScopeExportService.ExportAsync(ScopeKey,ExportScopeRecords,CancellationToken):Task<IResult>`

```csharp
public sealed record ExportScopeRecords(DateTimeOffset? CreatedFrom, DateTimeOffset? CreatedTo);
// from inclusive, to exclusive; รับเฉพาะ offset 0/UTC; from>=to ตอบ400
// ช่วงในอนาคตที่เรียงถูกต้องใช้ได้และอาจได้รายการว่าง
```

- [ ] เขียนRED exportจำกัด100พร้อมpositivecontrolrolegrantexport+MFA:

```csharp
[Fact]
public async Task Export_rejects_over_limit_instead_of_silent_truncation()
{
    using var keys = new TestKeyMaterial();
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
    var f = await ScopeFixture.CreateAsync(d);
    await f.GrantAsync(f.Site, "approval", "scope-probe:export");
    for (var i=0; i<101; i++) await f.RecordAsync(f.Site, "รายการ"+i);
    await d.LoginAsync("scope-user", MfaTests.Password);
    await AuthorizationTests.ConfirmAsync(d);
    using var r = await d.PostAsync(f.Path(f.Site)+"/export-simulation", new { });
    Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
}
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~ScopeExportTests` ให้RED แล้วPOSTexportผ่านScopeOperation capabilityexport requireMfa=true; permissionexportอย่างเดียวส่งpublicfieldsได้ ไม่ต้องreadเพิ่มเติมตามspecแต่restrictedfieldต้องrestricted-read
- [ ] Queryexactscope+UTCfilterในTX stableorderแล้วTake101 materialize ถ้าLength101ตอบ400พร้อมauditeddenialโดยไม่ส่งrows; ไม่สร้างfile/outbox/stream ก่อนauditcommit:

```csharp
var rows = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Take(101).ToArrayAsync(ct);
if (rows.Length > 100) return Results.Problem(statusCode:400, title:"กรุณาลดช่วงข้อมูลส่งออก");
// สร้างprojectionแบบเดียวกับTask6, audit row-count/destination-type/filter-from/filter-to
// RunAsyncจะSaveChanges/Commitก่อนส่งIResultออกHTTP
return Results.Ok(new { items = projectedRows, rowCount = rows.Length });
```

- [ ] ขยายrace/faulttestsสำหรับread/write/export+assignmentrevoke/rolegrantchange/deactivateaccount/parent โดยbarrierTask6; assertaffectedrows/sessioncookiesจริงและ nofailuredata leakage ตรวจauditcorrelation/actor/role/scope/rowcount/nosecret/immutabletrigger
- [ ] Exporttestfilters0/100/101, wrongoffset400, missingexport403, unknownscope404, noMFA403, restrictedmissingomitfield, sameuserroleAไม่ส่งB; เปรียบresponseก่อน/หลังrolechangeโดยloginใหม่เพื่อไม่ให้401กลบข้อผิดพลาดscope
- [ ] GREENและcommit:

```bash
dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~ScopeExportTests|FullyQualifiedName~ScopeAuditFailureTests|FullyQualifiedName~ScopeRaceTests'
git add backend/src/TPR10.Api/Scopes/Probes backend/src/TPR10.Api/Organization/OrganizationRegistration.cs backend/tests/TPR10.Api.IntegrationTests/ScopeExportTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopeAuditFailureTests.cs backend/tests/TPR10.Api.IntegrationTests/ScopeRaceTests.cs
git commit -m "feat: verify scoped export and revocation atomicity"
```

## Task 8: Portal selector และหน้าจอจัดการ Organization/Assignment

**สร้าง:** `lib/scopes/scope-view.ts`, `scope-path.ts`, `scope-fetch.ts`, `components/scopes/ScopeSelector.tsx`, `OrganizationForm.tsx`, `AssignmentForm.tsx`, `ScopedRecordPanel.tsx`; `app/portal/scopes/page.tsx`, `app/portal/scopes/[workspaceId]/[[...scopePath]]/page.tsx`, `app/portal/admin/organization/page.tsx`, `app/portal/admin/assignments/page.tsx`; `tests/scope-helpers.test.mjs`, `tests/e2e/scopes.spec.ts`
**แก้:** `app/portal/page.tsx`, `lib/auth/auth-client.ts`, `components/auth/useAuthMutation.ts` รองรับ POST/PATCH อย่าง explicit โดยคง default POST ไม่เพิ่ม retry; `infra/nginx/smoke-identity-https.mjs`, `backend/tests/TPR10.E2E.Fixture/Program.cs` สำหรับ test-only seed และเลือก spec file
**ส่ง:** TypeScript `ScopeKey={workspaceId:string;projectId:string|null;siteId:string|null}`, `scopePath(key):string`, `fetchScopes(origin,cookie,transport=fetch):Promise<ScopePage>`; `ScopePage`มีitems/total/pageNumber/pageSizeตรงAPI

- [ ] Node RED pathnullไม่กลายเป็นwildcard:

```javascript
import test from 'node:test';
import assert from 'node:assert/strict';
import { loadTs } from './helpers/load-ts.mjs';
// loadTs เป็น synchronous helper ที่มีอยู่แล้ว ไม่เพิ่ม runtime TS package
test('scope URL ระบุ Site ชัดและไม่เปิด URL ภายนอก', async () => {
  const { scopePath } = await loadTs('lib/scopes/scope-path.ts');
  const w='11111111-1111-4111-8111-111111111111';
  const p='22222222-2222-4222-8222-222222222222';
  const s='33333333-3333-4333-8333-333333333333';
  assert.equal(scopePath({workspaceId:w,projectId:p,siteId:s}), `/portal/scopes/${w}/projects/${p}/sites/${s}`);
  assert.throws(() => scopePath({workspaceId:w,projectId:null,siteId:s}));
});
```

- [ ] รัน `node --test tests/scope-helpers.test.mjs` REDแล้วimplementUUIDvalidation+exactsegmentallowlist; APIoriginต้องconfiguredserveroriginไม่เชื่อHostheader, cache:no-store redirect:error timeout5s ตามfetchSession ไม่forwardcookieไปarbitraryURL

```typescript
export function scopePath(k: ScopeKey): string {
  const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  if (!uuid.test(k.workspaceId) || (k.projectId !== null && !uuid.test(k.projectId))
      || (k.siteId !== null && (!uuid.test(k.siteId) || k.projectId === null))) throw new Error('พื้นที่ไม่ถูกต้อง');
  return `/portal/scopes/${k.workspaceId}` + (k.projectId ? `/projects/${k.projectId}` : '')
    + (k.siteId ? `/sites/${k.siteId}` : '');
}
```

- [ ] HTTPAPI/clienttypederrorแยก401→login,403→สิทธิ์/MFA,404→พื้นที่ไม่พร้อม,409→reloadversion,503→บริการไม่พร้อม ไม่ตีความAPIล่มเป็นlogout; SSRdynamic/no-store และfullnavigationเมื่อเปลี่ยนscopeเพื่อไม่เก็บข้อมูลบัญชีก่อน
- [ ] Selectorดึงpaginationได้ไม่เงียบตัดscopeที่เกิน100; emptyassignmentข้อความไทย; managementformsใช้optionsendpointsTask4หรือusers/rolesAPIเดิมเมื่อมีสิทธิ์เท่านั้น ไม่ยกระดับusers:manageให้ทุกassignmentmanager
- [ ] UIรักษาcontrolledinputและปิดsubmitก่อนhydration, disabledระหว่างส่ง; explicitmethodในauthMutation; noautorertry/nooptimisticpermission; ป้องกันstale responseด้วยAbortController+requestgenerationและclearข้อมูลก่อนload
- [ ] ขยายสัญญาโดยไม่ทำ caller เดิมเสีย: `authMutation(path:string,body:unknown,method:'POST'|'PATCH'='POST'):Promise<Response>` และ `run(path,body,success,method:'POST'|'PATCH'='POST')`; hook ส่ง method ต่อไปยัง fetch หลังรับ CSRF token ต้องตรวจคู่ method/path แบบ anchored allowlist ของ routes Task3/4/6/7 เท่านั้น ไม่อนุญาตทุก `/api/` หรือ URL ภายนอก เพิ่ม Node tests สำหรับ PATCH ที่อนุญาต, method ผิด, external URL, CSRF ล้มเหลวแล้วไม่ส่ง mutation และยืนยัน auth routes เดิมยัง POST
- [ ] เพิ่มPlaywrightREDก่อนUI: noassignment, adminwithoutbusinessaccess, A/Broles, fieldomission, selfgrantdisabled+directAPI403, changeaccount, deeplinkinvalid, delayedresponseAหลังnavigateB, back/logout, keyboardmobile

```typescript
test('logout ไม่แสดงข้อมูลพื้นที่เดิมเมื่อย้อนกลับ', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('ชื่อผู้ใช้').fill('e2e-scope-staff');
  await page.getByLabel('รหัสผ่าน', { exact: true }).fill('e2e-isolated-password-123');
  await page.getByRole('button', { name: 'เข้าสู่ระบบ', exact: true }).click();
  await page.goto('/portal/scopes');
  await page.getByRole('link', { name: 'ไซต์ทดสอบ A', exact: true }).click();
  await expect(page.getByText('ข้อมูลทดสอบเฉพาะ A', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'ออกจากระบบ', exact: true }).click();
  await page.goBack();
  await expect(page.getByText('ข้อมูลทดสอบเฉพาะ A', { exact: true })).toHaveCount(0);
});
```

- [ ] Harness สร้างเฉพาะ disposable DB fixture ใช้รหัสผ่านทดสอบเดียวกับ identity suite ตามตัวอย่าง ไม่ใช่ credential จริง; seed `e2e-scope-staff` ในไซต์ชื่อ `ไซต์ทดสอบ A` พร้อม record `ข้อมูลทดสอบเฉพาะ A` และอีกบัญชีที่เข้าถึงเฉพาะไซต์ B เพื่อพิสูจน์ account isolation; ห้ามเก็บข้อมูลจริงใน trace เพิ่ม `--spec tests/e2e/scopes.spec.ts` เป็น allowlisted flag คง default identity เดิม; production Next ใน harness ใช้ server-only `TPR10_SCOPE_TEST_UI=true` พร้อม API environment Testing เท่านั้น ห้ามตั้งใน production deployment และห้ามใช้ NEXT_PUBLIC flag
- [ ] TestsproductionboundaryยืนยันtechnicalUI404เมื่อflagไม่เปิดและAPIproduction404; selector/managementยังทำงาน และไม่พึ่งTechnicalAPIเป็นbusinessfeature
- [ ] ตรวจแบบsequential dev→build→prod ไม่ให้devเขียน.nextระหว่างproductiontest:

```bash
npm test
npm run lint
node infra/nginx/smoke-identity-https.mjs 4000 --e2e --spec tests/e2e/scopes.spec.ts
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes.spec.ts
git add lib/scopes lib/auth/auth-client.ts components/scopes components/auth/useAuthMutation.ts app/portal tests/scope-helpers.test.mjs tests/e2e/scopes.spec.ts infra/nginx/smoke-identity-https.mjs backend/tests/TPR10.E2E.Fixture/Program.cs
git commit -m "feat: add scoped portal navigation and administration"
```

## Task 9: OpenAPI, review ทั้ง branch และ Exit Gate

**สร้าง:** `backend/src/TPR10.Api/Scopes/ScopeOpenApiMetadata.cs`, `ScopeOpenApiTransformer.cs`; tests `ScopeOpenApiTests.cs`; `docs/architecture/module-3-exit-gate.md`, `docs/runbooks/module-3-organization-scope.md`
**แก้:** `Identity/IdentityOpenApiTransformer.cs`, `Organization/OrganizationRegistration.cs`, endpointmetadataทุกrouteModule3, tests `IdentityOpenApiTests.cs` เฉพาะenumerationที่เปลี่ยนและคงassertidentitycontractเดิม
**รับ/ส่ง:** endpointmetadataใช้ `ScopeEndpointMetadata(string Mode,string? Capability,bool RequireMfa,string Level)` Mode=`system-management`/`scope-discovery`/`exact-business`; Level=`none`/`workspace`/`project`/`site`; transformerไม่สร้างauthority runtime

- [ ] RED contractscopeไม่ถูกtransformerเดิมเขียนidentity-onlyทับ:

```csharp
[Fact]
public async Task Site_operation_documents_exact_scope_and_csrf()
{
    using var factory = new WebApplicationFactory<Program>();
    using var client = factory.CreateClient();
    using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/openapi/v1.json"));
    var op = doc.RootElement.GetProperty("paths")
        .GetProperty("/api/v1/workspaces/{workspaceId}/projects/{projectId}/sites/{siteId}/scope-probe-records")
        .GetProperty("post");
    Assert.Equal("exact-business", op.GetProperty("x-tpr10-scope").GetString());
    Assert.Contains(op.GetProperty("parameters").EnumerateArray(), p =>
        p.GetProperty("name").GetString()=="X-CSRF-Token" && p.GetProperty("required").GetBoolean());
}
```

- [ ] `dotnet test backend/TPR10.sln --filter FullyQualifiedName~ScopeOpenApiTests` ต้องREDหลังroutesมีแล้ว; แก้Identitytransformerอ่านnewmetadataก่อนใส่scope/description ไม่แข่งtransformerorder; oneCSRFheaderไม่duplicate; docsperm/MFAconditionalrestrictedfields request/responsepublic-vsrestricted/error400401403404409429503ตามruntime
- [ ] Enumerationtestครบmanagement/discovery/recordทุกoperation ทุกscopelevel; productionOpenAPIไม่แสดงprobes; errorยังมีgeneric500/defaultที่เป็นไปได้ให้ระบุตามจริง ไม่claimทุกerrorเป็น503หรือJSONเมื่อbindingเดิมอาจempty
- [ ] Runbook: migrate/initialorganizationผ่านAPI, secondadminforassignment, noautoassignment, rolegrantdomain, disable/revoke/reactivate, cache/404handling, auditfailure/recovery, no-secrets, downgradewarning; testmatrixผูกspec12กับชื่อtestsจริง; ownerยังไม่signoffต้องระบุรอ
- [ ] Fullverificationหลังintegrationก่อนreviewและหลังreviewfix (ตั้งPATHNode/.NET/Dockerตามเครื่องจริงก่อน ไม่ติดตั้งversionอื่นเพื่อหลบfailure):

```bash
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
dotnet list backend/TPR10.sln package --vulnerable --include-transitive
npm audit
git diff --check
```

- [ ] บันทึกUTC/commit/worktree/exitcode/testcountsจริง ไม่มีskipที่ซ่อนsecurityacceptance; harnessต้องปิดcontainer/serverของตนเอง ไม่แตะฐานใช้งานจริงและไม่bypassTLS
- [ ] ผู้ตรวจอิสระfreshcontextหนึ่งคนตรวจdiffทั้งModule3กับspec/planรวม5ReviewFocus และจุดเชื่อมModule2; regradeตามผลต่อผู้ใช้ ถ้าCritical/Importantทำหนึ่งTDDfixpassและfullverificationใหม่ ไม่มีsecondreviewตามexecuting-plans; Minor/declined-to-judgeบันทึกพร้อมผลหากวินิจฉัยผิด ไม่อ้างindependentfullrerunหากreviewerไม่ได้รัน
- [ ] Commitเอกสารพร้อมruntimecontractที่ตรวจแล้ว และส่งรายงานภาษาไทย แยกtechnical/policy/productiongate; ขอผู้ใช้เลือกintegration ไม่pushmergeเอง:

```bash
git add backend/src/TPR10.Api/Scopes backend/src/TPR10.Api/Organization backend/src/TPR10.Api/Identity/IdentityOpenApiTransformer.cs backend/tests/TPR10.Api.IntegrationTests/ScopeOpenApiTests.cs backend/tests/TPR10.Api.IntegrationTests/IdentityOpenApiTests.cs docs/architecture/module-3-exit-gate.md docs/runbooks/module-3-organization-scope.md
git commit -m "docs: record module 3 scope security contract and exit gate"
```

## เกณฑ์รับงานและ self-review ของแผน

| Spec | Task ที่พิสูจน์ |
| --- | --- |
| 1–4 ขอบเขต/exactscope/ไม่inherit | 1,5,6 |
| 5 system-vs-business/scopedroles/selfgrant | 1,2,4,5 |
| 6 schema/history/version/disable-reactivate | 1–4 |
| 7 boundaries/ScopeContext/repository | 2,5,6 |
| 8 API/validation/status/pagination/fieldpolicy | 3–7,9 |
| 9 MFA/revocation/concurrency | 2–7 |
| 10 atomic audit/privacy/export | 3–7 |
| 11 Portal/controlplane/productionboundary | 8 |
| 12–13 acceptance/review/owner gates | 9 |

- แผนรักษาnoimplicitparentpermissionและroleisolation ไม่เพิ่มscopeจากUI/headers/body
- PermissionCatalogทั้งหมด12รายการ แต่Administratorได้system8เท่านั้น; existing6ยังไม่เปลี่ยนชื่อหรือUUID
- ScopeOperationเป็นtransactionownerสำหรับdata plane; managementservicesใช้TXของตนและlifecycleไม่เปิดTXซ้อน
- หน้าadminมีoptionsAPIจำกัดข้อมูล จึงไม่ต้องแจกusers:manage/roles:readเกินที่จำเป็น
- ปิดtechnicalAPIตามASPNETCORE_ENVIRONMENT และtechnicalUIตามserverfixtureflag ไม่สมมติว่าNextproductionbuildเท่ากับAPIProductionในE2E
- การรวบรวมbusinessscopepermissionsไม่เปลี่ยนความหมายSessionView.Permissionsเดิม; MFAตรวจeffectiveprivilegedrolesแยกจากรายการpermissions
- การอนุมัติแผนนี้ยังไม่อนุมัติproductionpolicyหรือdeployment; Module2Minorและownerregisterคงอยู่ในรายงานเดิม

ขั้นถัดไป: เริ่ม Task6 ใน worktree Module3 เดิมเมื่อผู้ใช้สั่ง ใช้ Native/inline และอ่าน ledger/รายงาน Task5 ก่อน ไม่ทำ Task1–5 ซ้ำ และไม่ถามเลือกวิธีทำงานซ้ำ

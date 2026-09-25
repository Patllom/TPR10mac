# Module 6A — โครงสร้างพนักงานและสิทธิ์ Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. ใช้ Native ที่ผู้ใช้เลือกในงานก่อนหน้าเป็นวิธีต่อเนื่อง เว้นแต่ผู้ใช้เปลี่ยนวิธี ต้องรออนุมัติแผนนี้ก่อนเริ่ม

**Goal:** ส่งมอบต้นสังกัด สายบังคับบัญชา HR assignments และสัญญาตัดสินสิทธิ์/เส้นทางหัวหน้า→HR ที่ใช้ต่อใน Module6 โดยไม่เปิดข้อมูลลงเวลาหรือรูปในระยะนี้

**Architecture:** เพิ่ม Attendance/Directory และ Attendance/Access ภายใน API เดิม เก็บประวัติช่วงเวลาที่ไม่ทับซ้อน ใช้ identity lock และ transaction/audit เดิม โดยแยกสิทธิ์หน่วยงานบุคลากรจาก exact Site assignment ของ Module3 ไม่ใช้ Admin เป็นผู้มีสิทธิ์ข้อมูลโดยปริยาย

**Tech Stack:** .NET10/EF Core/Npgsql/PostgreSQL จาก package lock เดิม, Next.js15/React19/TypeScript เดิม, xUnit/PostgreSQL integration และ Playwright HTTPS ไม่เพิ่ม image library หรือบริการภายนอกใน6A

**Spec:** [Module6 written Spec ที่อนุมัติ](../specs/2026-09-25-module-6-attendance-design.md)

ฐานที่ตรวจ: `b8b9c0f` บน main; ผู้ใช้อนุมัติให้ดำเนินการทุก Task จนจบ Module6A วันที่25กันยายน2026 ไม่รวม push/merge/deploy

## Global Constraints

- ขอบเขต6Aคือ Spec§5–6 และ routing contract§11.3 ไม่ใช่การส่งมอบกล้อง รูป ลงเวลา หรือคำร้องแล้ว
- `Asia/Bangkok`; เก็บ instant UTC; valid intervals เป็น `[from,to)` ไม่มีผู้ใช้ต้นสังกัดหลักซ้อนกันแม้ต่าง Workspace
- “หัวหน้า” เริ่มจาก direct reports เท่านั้น ดู GPS ลูกทีม ไม่เห็นรูป; HR เฉพาะหน่วยงานที่ได้รับมอบหมาย; own data อ่านรูป/GPSได้; Admin ไม่มี bypass
- ผู้ยื่น หัวหน้าที่อนุมัติ และ HR ที่อนุมัติต้องต่างคน; route ไม่ครบให้หยุด ไม่อนุมัติแทนด้วย global Admin
- recent MFA ใช้ `verified > now-15min && verified <= now` พร้อม confirmed non-revoked factor และ live session ไม่เชื่อ session DTO จาก browser
- Permission check หลัง lock และก่อน commit; actorจาก `RequestSession` เท่านั้น; mutationsมี CSRF/Origin/Host guardเดิม
- audit failure rollback, version conflict409, disabled ancestor fail closed, data filteringก่อนpagination; pageเริ่ม1/pageSize25 clamp100, prefixยาว0–100และตีความliteral
- ไม่รวมธุรกิจ permissionsใหม่ลง `SessionView.Permissions` เดิม ไม่เปลี่ยน PermissionHandler/PermissionMutationGuard ให้ยอมทุกdomain
- ไม่ให้ self-grant ผ่าน directory, assignment, เปลี่ยนrole grantsหรือผูกroleของตัวเอง; ไม่อ้างว่าป้องกันผู้ดูแลสองคนสมคบกันได้
- Dev4000 / prod-build4001 / HTTPS4443; ไม่รันdev/buildพร้อมกันในcheckoutเดียว; ห้ามแย่งPreviewที่ผู้ใช้เปิดอยู่
- เอกสารและข้อความUIภาษาไทย ไม่มีcredentials/MFAsecret/ภาพจริง/GPSจริงในtestsหรือGit
- ทุกtaskมี RED→GREEN และcommit; ปิด6Aได้หลัง Test/Build/Lint/backend format/security regression/whole-branch reviewครบ ไม่มีpush/mergeอัตโนมัติ

## ขอบเขตไฟล์และข้อตกลงก่อนเริ่ม

ราก path ทั้งหมดในแผนสัมพันธ์กับ repository; เมื่อ execution ให้สร้าง/ยืนยัน isolated worktree branch `codex/module-6a-directory-access` ด้วย using-git-worktrees ไม่เขียน implementation ใน main ที่มี Preview และไฟล์สำรองของผู้ใช้

| หน่วย | ไฟล์ใหม่ | หน้าที่ |
| --- | --- | --- |
| Directory data | `backend/src/TPR10.Api/Attendance/Directory/EmployeeMembership.cs`, `ReportingLine.cs`, `HrAssignment.cs`, `DirectoryModelConfiguration.cs` | ประวัติและ constraints |
| Directory rules | `DirectoryRules.cs`, `DirectoryContracts.cs`, `DirectoryService.cs`, `DirectoryLifecycle.cs`, `DirectoryEndpoints.cs`, `DirectoryBindingAuditMiddleware.cs` ในโฟลเดอร์Directoryเดียวกัน | validation/transaction/HTTP/lifecycle |
| Access | `backend/src/TPR10.Api/Attendance/Access/AttendanceCatalog.cs`, `AttendanceContracts.cs`, `AttendanceAccess.cs`, `AttendanceRouteResolver.cs`, `AttendanceGrantGuard.cs` | named capabilities + contextual scope และ route |
| Wiring/contract | `backend/src/TPR10.Api/Attendance/AttendanceRegistration.cs`, `AttendanceOpenApiTransformer.cs` | DI/endpointmetadata/OpenAPI |
| Portal | `app/portal/admin/attendance-directory/page.tsx`, `components/attendance/DirectoryForm.tsx`, `lib/attendance/directory-client.ts`, `directory-view.ts` | จัดต้นสังกัด/หัวหน้า/HR ไม่ใช่หน้าลงเวลา |

ไฟล์เดิมที่แตะได้ตามtask: `Data/Tpr10DbContext.cs`, `Data/Migrations/Tpr10DbContextModelSnapshot.cs`, `Identity/Data/IdentityModelConfiguration.cs`, `Identity/Accounts/IdentityCatalog.cs`, `Identity/Accounts/RoleAdministration.cs`, `Identity/Accounts/AccountProvisioning.cs`, `Identity/IdentityRegistration.cs`, `Identity/IdentityOpenApiTransformer.cs`, `Organization/EffectiveRolePolicy.cs`, `Organization/OrganizationService.cs`, `Program.cs`, `app/portal/page.tsx`, `lib/auth/auth-client.ts` และ test harness ตามTask6 ห้ามสร้างสมมติว่าไฟล์ `SessionRevocation.cs` หรือ `PermissionCatalog.cs` มีอยู่ — ไม่มีในฐานที่ตรวจ ใช้ `SessionService.cs`/`ISessionService` ที่มีอยู่จริง

Migration ให้ EF สร้าง `AddAttendanceDirectory` ใน `backend/src/TPR10.Api/Data/Migrations/`; timestampต้องมาจากgenerator ไม่แต่งเวลาล่วงหน้า ทุกtaskต้องstageเฉพาะไฟล์ของตน

## Review Focus

1. ผู้ใช้ย้ายแผนก/กลับแผนกเดิมและหัวหน้าเดิมยังเปิดหน้าอยู่ — Task2/5ทดสอบ half-open intervals, membershipId snapshot และcurrentgrant ไม่เผยประวัตินอกต้นสังกัด
2. ยกระดับตัวเองผ่านแก้roleที่ตนมีหรือ reportingline ของคนอื่น — Task3/4ทดสอบผลสิทธิ์ก่อน–หลัง ไม่ตรวจแค่targetId==actor
3. ปิด Workspace/Department หรือdisableผู้ใช้ขณะ directoryactionรอlock — Task3/4ทดสอบcontrolled overlapไม่ใช้sleepเดา; ไม่มีสิทธิ์ค้างจากsnapshot
4. เปลี่ยนroleclassหรือใช้business permissionผ่านglobal/scopedroleผิดdomain — Task1/3/5ทดสอบcatalog, no flattened authority, no Admin bypass
5. Browserสองบัญชี, back/pagehide และตอบAPIช้าหลังหมดสิทธิ์ — Task6ทดสอบDOMไม่คงข้อมูลเดิม, abort/generation,503ซ่อนไม่logoutปลอม

## สัญญาร่วมที่ล็อกไว้ในแผนนี้

### ตารางและเวลา

ทั้งสาม entity มี `Guid Id`, `long Version=1`, `DateTimeOffset ValidFromUtc`, `DateTimeOffset? ValidToUtc`, `DateTimeOffset CreatedAtUtc`, `Guid CreatedBy`, `Guid? EndedBy`, `string Reason` (1–500 ตัว หลังTrim ไม่อนุญาตcontrol chars)

- EmployeeMembershipเพิ่ม `Guid UserId, WorkspaceId, DepartmentId`; FK `(DepartmentId,WorkspaceId)` → Department `(Id,WorkspaceId)` และUserFK Restrict
- ReportingLineเพิ่ม `Guid EmployeeMembershipId, EmployeeUserId, SupervisorUserId`; composite FK membership `(Id,UserId)`; CHECK employee!=supervisor; supervisorเป็นUserFK
- HrAssignmentเพิ่ม `Guid UserId,WorkspaceId,DepartmentId`; Department compositeFK; ไม่ผูกกับSite
- Temporal protection: triggerตรวจ overlapภายใต้ `pg_advisory_xact_lock(7241002)` ด้วย `from < coalesce(other.to,infinity) AND other.from < coalesce(to,infinity)`; membership keys=user, reporting keys=employee, HRkeys=user/workspace/department ห้ามเปิดสองmembershipหรือสองหัวหน้าทับเวลา ไม่เพิ่ม `btree_gist` โดยไม่จำเป็น
- ทุกตาราง CHECK validTo>validFrom หรือnull, version>=1, reason bounded; indicesสำหรับkeys+period; identity/security/mfa/audit tablesไม่ถูกล้าง
- 6A mutationเป็น effective-now เท่านั้น ห้าม clientส่งย้อนหลังหรืออนาคต เก็บ historyด้วยend old/append new ไม่DELETE/rewriteowner; หัวหน้าต้องมีactive membershipในworkspaceเดียวกับลูกทีม (ต่างdepartmentได้); HRassignmentข้ามworkspaceได้เมื่อได้รับgrantชัดเจน
- การย้ายสมาชิกปิดReportingLineที่ผูกmembershipเก่า; relationที่ชี้มาเป็นลูกทีมต้องvalidate supervisorcurrent workspace ไม่ตามคนไปเปิดข้อมูลข้ามหน่วยอัตโนมัติ

### Catalog

UUIDคงที่ prefix `20000000-0000-0000-0000-` ต่อท้าย12หลักตามตาราง ห้ามเปลี่ยน12รายการเดิม:

| ท้าย UUID | Capability | Domain | แหล่งสิทธิ์ |
| --- | --- | --- | --- |
| 000000000013 | attendance:record | scoped-business | exact Site roleassignment เท่านั้น ใช้ใน6C |
| 000000000014 | attendance:team-read | attendance | global role + directReportingLine/record snapshot |
| 000000000015 | attendance:hr-read | attendance | global role + exactHrAssignment/record snapshot |
| 000000000016 | attendance:approve-supervisor | attendance | global role + route/currentReportingLine |
| 000000000017 | attendance:approve-hr | attendance | global role + route/currentHrAssignment |
| 000000000018 | attendance:directory-manage | system | global role + recentMFA เฉพาะdirectory |
| 000000000019 | attendance:storage-manage | system | global role + recentMFA ใช้ใน6B |

ขยายCHECKdomainให้มี `attendance` แต่PermissionHandlerเดิมยังอ่านsystemเท่านั้น จัดCatalogใหม่เพิ่ม19รายการในcapabilityenumeration ขณะbootstrapและmigration **ไม่แจก7รายการใหม่ให้ใครอัตโนมัติ** แก้auto-admin-seedingเป็นexplicit allowlistsystem8เดิม ใช้ผู้ดูแลเดิมที่มีroles:manageกำหนดdirectoryoperatorผ่านกระบวนการอนุมัติขององค์กรก่อนใช้

capabilitydomainattendanceต้องอยู่ในroleclass `approval`, `accounting` หรือ `finance-data-access` เมื่อใช้เป็นสิทธิ์ธุรกิจ ห้ามgrantแก่ `staff`/`system-administration`; ตารางrolesไม่เพิ่มclassใหม่ roleclassเดิมไม่mutable การสร้างroleแล้วgrantและassignต้องทดสอบแยก

### DTO และการตอบ

สร้างใน `DirectoryContracts.cs` ใช้ `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]` และ `[property: JsonRequired]` กับทุกfieldของrecordต่อไปนี้ (typeถูกต้องไม่แทนvalidationของempty Guid/string):

```csharp
public sealed record SetMembership(Guid UserId, Guid WorkspaceId, Guid DepartmentId,
    long? ExpectedVersion, string Reason);
public sealed record SetReportingLine(Guid EmployeeUserId, Guid SupervisorUserId,
    long? ExpectedVersion, string Reason);
public sealed record GrantHrAssignment(Guid UserId, Guid WorkspaceId, Guid DepartmentId,
    string Reason);
public sealed record EndDirectoryRow(long ExpectedVersion, string Reason);
public sealed record MembershipView(Guid Id, Guid UserId, Guid WorkspaceId, Guid DepartmentId,
    DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, long Version);
public sealed record ReportingView(Guid Id, Guid EmployeeUserId, Guid SupervisorUserId,
    DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, long Version);
public sealed record HrView(Guid Id, Guid UserId, Guid WorkspaceId, Guid DepartmentId,
    DateTimeOffset ValidFromUtc, DateTimeOffset? ValidToUtc, long Version);
public sealed record DirectoryPage<T>(T[] Items, int Page, int PageSize, int Total);
public sealed record DirectoryOption(Guid Id, string Label);
```

ExpectedVersion=nullหมายถึงไม่มีactive rowเท่านั้น ไม่ใช่ignoreconcurrency; POSTซ้ำHRassignmentactive409; no-opที่ค่าเดิมและversionถูกคืน200ไม่revokeอีก; replaceปิดแถวเดิมversion+1แล้วสร้างแถวใหม่ด้วยversionที่สูงกว่าประวัติทั้งหมดของพนักงานในresourceนั้นในtransactionเดียว (แก้ข้อบกพร่องของแผนเดิมที่เริ่ม1ซ้ำ ทำให้ stale client เขียนทับได้; ทดสอบREDยืนยันแล้ว) HRที่endด้วยIDเฉพาะยังเริ่มversion1

Route root `/api/v1/attendance/directory` ทั้งหมดต้อง `attendance:directory-manage`+recentMFA ยกเว้นGET `/api/v1/attendance/access` ที่คืนcapability boolean ของ actorเอง ไม่มีข้อมูลลูกทีม:

| Method/pathต่อจากroot | Request/response | Success |
| --- | --- | --- |
| GET /memberships | optionaluserId/workspaceId/departmentId/includeEnded,page,pageSize → DirectoryPage<MembershipView> | 200 |
| POST /memberships | SetMembership → MembershipView | 201ใหม่/200replaceหรือno-op |
| POST /memberships/{id}/end | EndDirectoryRow | 204 |
| GET /reporting-lines | optionalemployeeUserId/includeEnded,page,pageSize → DirectoryPage<ReportingView> | 200 |
| POST /reporting-lines | SetReportingLine → ReportingView | 201ใหม่/200replaceหรือno-op |
| POST /reporting-lines/{id}/end | EndDirectoryRow | 204 |
| GET /hr-assignments | optionaluserId/workspaceId/departmentId/includeEnded,page,pageSize → DirectoryPage<HrView> | 200 |
| POST /hr-assignments | GrantHrAssignment → HrView | 201 |
| POST /hr-assignments/{id}/end | EndDirectoryRow | 204 |
| GET /options/users | prefix,page,pageSize → DirectoryPage<DirectoryOption> (id/usernameเท่านั้น) | 200 |
| GET /options/workspaces | prefix,page,pageSize → DirectoryPage<DirectoryOption> | 200 |
| GET /options/departments | workspaceId required,prefix,page,pageSize → DirectoryPage<DirectoryOption> | 200 |

400invalid/unknownbodyfields/invalidcycle/time/range;401sessionrequired;403permission/MFA/selfgrant;404unknown/inactiveparent;409version/overlap;429rate;503dependency/audit; 500/defaultตามframeworkที่อาจไม่เป็นJSONต้องระบุ ไม่เขียนOpenAPIว่าerrorทุกชนิดเป็น503

### Contract สำหรับ6B–6D

สร้าง `Access/AttendanceContracts.cs`:

```csharp
public sealed record EmploymentSnapshot(Guid MembershipId, Guid EmployeeId,
    Guid WorkspaceId, Guid DepartmentId, DateTimeOffset OccurredAtUtc);
public enum AttendanceReadBasis { None, Own, Supervisor, Hr }
public sealed record AttendanceReadDecision(bool CanRead, bool CanReadGps,
    bool CanReadPhoto, AttendanceReadBasis Basis, int? Status);
public sealed record AttendanceRouteDecision(Guid? SupervisorId, Guid[] HrCandidateIds,
    Guid WorkspaceId, Guid DepartmentId, long MembershipVersion,
    long? ReportingVersion, int? Status);
public sealed record AttendanceAccessView(bool CanRecord, bool CanReadTeam, bool CanReadHr,
    bool CanApproveSupervisor, bool CanApproveHr, bool CanManageDirectory);
```

`CanRecord` เป็นdiscoveryhintว่ามีactiveSiteassignmentกับcapability ไม่ใช่อนุญาตทุกSite `GET /access` คืนbooleansชุดนี้เท่านั้น ไม่เพิ่มattendancecapabilitiesเข้าSessionViewเดิม

```csharp
public interface IAttendanceAccess {
    Task<EmploymentSnapshot?> CurrentEmploymentAsync(Guid employeeId, DateTimeOffset at, CancellationToken ct);
    Task<AttendanceReadDecision> ReadAsync(EmploymentSnapshot subject, CancellationToken ct);
    Task<AttendanceAccessView> DescribeAsync(CancellationToken ct);
}
public interface IAttendanceRouteResolver {
    Task<AttendanceRouteDecision> ResolveAsync(EmploymentSnapshot subject, CancellationToken ct);
}
```

วางinterfacesใน `Access/AttendanceAccess.cs` และ `Access/AttendanceRouteResolver.cs` ตามลำดับ; callerถือtransaction+lock7241002 ทุกmethodอ่านข้อมูลจริง actorจากRequestSession; implementation6Aไม่เปิดendpointรับEmploymentSnapshotจากclient เนื่องจากsnapshotต้องมาจากrecordที่โหลดแล้วใน6C/6D

## Task 1: Directory schema, catalog และ migration ที่ไม่เพิ่มสิทธิ์อัตโนมัติ

**Files:** สร้างสามentity/DirectoryModelConfiguration/AttendanceCatalogตามตาราง; แก้ Tpr10DbContext, IdentityModelConfiguration, IdentityCatalog; สร้าง migration AddAttendanceDirectory และ designer/snapshot ด้วยEF; testsใหม่ `backend/tests/TPR10.Api.IntegrationTests/AttendanceDirectorySchemaTests.cs`, `AttendanceCatalogTests.cs`

**Interfaces:** Produces `AttendanceCatalog.Permissions` เป็น `IReadOnlyList<(Guid Id,string Capability,string Domain)>`, `AttendanceCatalog.Domain="attendance"`; entityproperties/constraintsตามสัญญาข้างต้น ConsumeUser/Departmentเดิมไม่มีตารางemployeeอื่นแอบสร้าง

- [x] เขียนRED migration/schemaและcatalog โดยเริ่มจากtestที่ใช้APIชนิดเดิมจึงไม่ล้มเพราะimportก่อนถึงข้อกำหนด:

```csharp
[Fact]
public void Catalog_has_seven_attendance_capabilities_without_renaming_existing()
{
    var names = IdentityCatalog.Capabilities;
    Assert.Equal(19, names.Distinct().Count());
    Assert.Contains("attendance:record", names);
    Assert.Contains("scope-probe:restricted-read", names);
}
```

- [x] Run `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceCatalogTests`; Expected FAIL19!=12 ไม่ใช่Docker/authfailure
- [x] เพิ่มschemaด้วยentityตามcontracts พร้อมCHECK/compositeFK/indicesและtriggeroverlap ใช้effectiveintervalconditionนี้ในtriggerfunctionที่ถือlockเดียวกันก่อนSELECT:

```sql
NEW.valid_from_utc < COALESCE(existing.valid_to_utc, 'infinity'::timestamptz)
AND existing.valid_from_utc < COALESCE(NEW.valid_to_utc, 'infinity'::timestamptz)
AND existing.id <> NEW.id
```

- [x] เพิ่มcatalog19รายการ; แยกbootstrap system8allowlistให้คงUUID1–8เท่านั้น โดยfielddomainไม่ใช่เหตุให้auto-grantpermissionใหม่ ทดสอบfreshbootstrapและexistingDBmigrationได้19permissionsแต่grantsเดิมเท่าเดิม ปรับ assertion จำนวน catalog ใน `backend/tests/TPR10.Api.IntegrationTests/RoleAuthorizationTests.cs` จาก12เป็น19 โดยเพิ่ม assertion ชื่อและ ID ของ12รายการเดิมยังครบ ไม่ลบ regression test เดิม
- [x] Run `dotnet ef migrations add AddAttendanceDirectory --project backend/src/TPR10.Api`; Expectedสร้างmigration/designer/snapshotสำหรับ3tables/newdomain/newcatalog ไม่มีdropidentity/audit
- [x] เพิ่มnegativeDBtestsผ่านPostgreSQLจริง: crossDepartmentWorkspace, from>=to, membershipsซ้อนคนเดียวต่างworkspace, reportingซ้อนคนเดียว, HRซ้อนtupleเดิม, boundaryold.to==new.fromผ่าน; concurrentสองconnectionsต้องมีหนึ่งcommitหนึ่งreject ไม่ใช้EF InMemory
- [x] เพิ่มupgrade→downgrade→upgradeบนdisposableDB ยืนยันทุกfieldในidentity/session/auditเดิมและschemaรุ่นใหม่; downgradeถ้ามีattendance-domainrole grantsต้องปฏิเสธพร้อมข้อความก่อนลบข้อมูล ห้ามลบgrantเงียบ ๆ
- [x] Run `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceCatalogTests|FullyQualifiedName~AttendanceDirectorySchemaTests'`; ExpectedPASSทั้งหมด ไม่มีskip
- [x] Commitเฉพาะไฟล์ข้างต้น: `git commit -m "feat: add temporal attendance directory schema"`

## Task 2: Temporal rules และ Reporting graph ที่ไม่วน

**Files:** สร้าง `Directory/DirectoryRules.cs`; tests `AttendanceDirectoryRulesTests.cs` และขยายschema testsเฉพาะtriggerclosedhistory

**Interfaces:** `DirectoryRules.Contains(DateTimeOffset from, DateTimeOffset? to, DateTimeOffset at):bool`; `DirectoryRules.CreatesCycle(Guid employee, Guid proposedSupervisor, IReadOnlyDictionary<Guid,Guid> currentManagers):bool`; `DirectoryRules.ValidReason(string?):bool` เป็นpurefunctions; serviceต้องสร้างcurrentgraphจากDBณnowในtransaction ไม่รับgraphจากclient

- [x] เขียนtestRED:

```csharp
[Fact]
public void End_boundary_is_excluded_and_returning_to_unit_does_not_reopen_old_interval()
{
    var start = DateTimeOffset.Parse("2026-09-25T00:00:00Z");
    var end = start.AddDays(1);
    Assert.True(DirectoryRules.Contains(start, end, start));
    Assert.False(DirectoryRules.Contains(start, end, end));
    Assert.False(DirectoryRules.Contains(end.AddDays(1), null, start));
}
[Fact]
public void Proposed_manager_chain_must_not_reach_employee()
{
    var a=Guid.NewGuid(); var b=Guid.NewGuid(); var c=Guid.NewGuid();
    Assert.True(DirectoryRules.CreatesCycle(a,b,new Dictionary<Guid,Guid>{{b,c},{c,a}}));
    Assert.True(DirectoryRules.CreatesCycle(a,a,new Dictionary<Guid,Guid>()));
    Assert.False(DirectoryRules.CreatesCycle(a,b,new Dictionary<Guid,Guid>{{b,c}}));
}
```

- [x] Run `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceDirectoryRulesTests`; ExpectedREDmissingtype ก่อนใส่stubให้compileแล้วรันREDassertionอีกครั้ง
- [x] ImplementContains `from <= at && (to is null || at < to)`; cycleเดินด้วยHashSetvisitedและreturns trueเมื่อพบemployeeหรือcycleเดิม เพื่อfailclosed ไม่recursionไม่จำกัดstack:

```csharp
var visited = new HashSet<Guid>();
var cursor = proposedSupervisor;
while (true) {
    if (cursor == employee || !visited.Add(cursor)) return true;
    if (!currentManagers.TryGetValue(cursor, out cursor)) return false;
}
```

- [x] เพิ่มtests nonoverlap/end exactboundary, emptyGuid rejectedก่อนgraph, malformedUTF/controlreason/whitespace/501characters, chain1000nodes no stackoverflow, oldmanagerinactiveexcluded; แยกreasontrimค่าที่เก็บจากvalidation
- [x] Runfilterเดิม ExpectedPASS; `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` Expected0; Commit `feat: define attendance temporal and reporting rules`

## Task 3: Identity integration และป้องกันยกระดับตนเอง

**Files:** สร้าง `Access/AttendanceGrantGuard.cs`, `Directory/DirectoryLifecycle.cs`; แก้ RoleAdministration, AccountProvisioning, EffectiveRolePolicy, IdentityRegistration, OrganizationService; tests `AttendanceIdentityTests.cs`, `AttendanceDirectoryLifecycleTests.cs`

**Interfaces:** `AttendanceGrantGuard.WouldElevateSelfAsync(Guid actorId, Guid targetRoleId, Guid[] proposedPermissionIds,CancellationToken ct):Task<bool>` และ `WouldAssignSelfAsync(Guid actorId,Guid targetUserId,Guid[] proposedRoleIds,CancellationToken ct):Task<bool>`; `DirectoryLifecycle.EndForUserAsync(Guid userId,Guid actorId,string reason,CancellationToken ct):Task<Guid[]>`, `EndForUnitAsync(Guid workspaceId,Guid? departmentId,Guid actorId,string reason,CancellationToken ct):Task<Guid[]>` คืนaffectedusers ไม่save/commit/revokeเอง callerowntransactionใช้lock7241002

- [x] เพิ่มHTTPREDผ่านrole APIsจริง: actorมีroles:manageและroleของตน POST/PUTgrantattendanceprivilegedcapให้roleนั้นต้อง403และgrantไม่เปลี่ยน อีกกรณีassignroleที่มีattendancecapให้ตัวเองผ่านuser updateต้อง403; ใช้IdentityTestDriver+RoleAuthorizationTests.AdminAsyncเดิมสำหรับMFAจริง ห้ามfixturefakeผ่านmutationที่กำลังตรวจ
- [x] Representative assertionในtestหลังsetupactor/roleโดยDBสำหรับเตรียมเท่านั้น:

```csharp
using var request = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(d.Client),
    $"/api/v1/roles/{actorRoleId}/permissions");
request.Method = HttpMethod.Put;
request.Content = JsonContent.Create(new { permissionIds = desiredPermissionIds });
using var response = await d.Client.SendAsync(request);
Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
// ตรวจgrant/audit/sessionจริงต่อด้วยdbcontext ไม่ถือstatusอย่างเดียวเป็นหลักฐาน
```

`actorRoleId` คือ role class `approval` ที่สร้างและผูกกับ actor ใน fixture แยกจาก system-administration role ที่ให้ `roles:manage`; `desiredPermissionIds` คือ grants เดิมของ approval role รวม attendance:hr-read ID15 สร้างทั้งสองค่าภายใน test case ก่อน snippet ต้องตรวจผ่าน DB ว่า grants และ SecurityVersion ไม่เปลี่ยน และมี denial audit กรณีนี้ต้องถูกปฏิเสธเพราะ self-grant ไม่ใช่เพราะ role class ไม่รองรับ เพิ่ม positive control ให้อีก operator ที่ไม่ได้ถือ role นี้มอบ grant เดียวกันสำเร็จ ห้ามใช้ mock status หรือข้อมูลบัญชีจริง

- [x] Run `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceIdentityTests`; ExpectedFAIL403!=200/204 แล้ว implement guard บน RoleAdministration.GrantsAsync/AssignAsync และ `AccountProvisioning.UpdateAsync(Guid actorId, Guid userId, UpdateAccountRequest request, CancellationToken ct)` เส้นทาง RoleIds
- [x] Guardเทียบeffectiveattendancegrantsetของactorก่อน/หลัง ไม่denyการถอนสิทธิ์ของตน; denyเพิ่มcapใหม่หรือเพิ่มauthorityแก่roleที่actorได้รับอยู่ผ่านglobal/ScopeAssignment; rejectclassที่ไม่รองรับattendance domain; globalrecordcapไม่ผ่านSiteScopeAccess
- [x] เพิ่ม2systempoliciesdirectory/storageในIdentityRegistrationใช้PermissionRequirementRequireMfatrueเดิม; ไม่แก้PermissionHandler/SessionServiceให้flattenattendance domain
- [x] Globalapproval/accounting/financeclassมีMFAอยู่แล้ว ยืนยัน tests loginforcedchange/recoveryยังผ่าน และAttendanceAccessในTask5ต้องเช็คconfirmedfactor+recentMFAเองเมื่อใช้team/hrcap ไม่ให้staffroleที่แทรกDBผิดผ่าน
- [x] Lifecycleendmembership/reporting/HRที่เกี่ยวข้องแบบeffective-nowพร้อมaudit; callerรวมaffectedids distinctsortกับScopeAssignmentLifecycleเดิมก่อน `ISessionService.RevokeUserAsync` หนึ่งครั้งต่อคน ในAccountProvisioningdisableและOrganizationServicedeactivateWorkspace/Department; Departmentเดิมไม่ใช่ScopeKeyจึงbranchโดยkind ไม่cascadeSiteassignmentsผิดระดับ
- [x] เพิ่มRED→GREENสำหรับdisable→enableไม่คืนmembership/HR, deactivate→reactivateไม่คืนสิทธิ์, managerถูกdisableแล้วลูกทีมrouteหาย, auditfail rollbackทั้งdirectory/securityversion/session, grantroleเปลี่ยนsessionเก่าถอน, no-opไม่revoke; ทำbarrierหลังlockก่อนrecheckแล้วraceกับdisableให้ผลตามcommitorder
- [x] Run `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceIdentityTests|FullyQualifiedName~AttendanceDirectoryLifecycleTests|FullyQualifiedName~ScopedIdentityLifecycleTests|FullyQualifiedName~ScopedMfaTests'`; ExpectedPASS; Commit `feat: guard attendance grants and directory revocation`

## Task 4: Directory management API พร้อม version/audit

**Files:** สร้างDirectoryContracts/Service/Endpoints/BindingAuditMiddlewareและAttendanceRegistration; แก้Program, IdentityRegistration (DI); tests `AttendanceDirectoryApiTests.cs`, `AttendanceDirectoryAtomicityTests.cs`

**Interfaces:** `DirectoryService.SetMembershipAsync(SetMembership,CancellationToken):Task<IResult>`, `SetReportingAsync(SetReportingLine,CancellationToken):Task<IResult>`, `GrantHrAsync(GrantHrAssignment,CancellationToken):Task<IResult>`, `EndMembershipAsync(Guid,EndDirectoryRow,CancellationToken):Task<IResult>`, `EndReportingAsync(Guid,EndDirectoryRow,CancellationToken):Task<IResult>`, `EndHrAsync(Guid,EndDirectoryRow,CancellationToken):Task<IResult>`; Listsแยก `ListMembershipsAsync(Guid? userId,Guid? workspaceId,Guid? departmentId,bool includeEnded,int page,int pageSize,CancellationToken)`, `ListReportingAsync(Guid? employeeUserId,bool includeEnded,int page,int pageSize,CancellationToken)`, `ListHrAsync(Guid? userId,Guid? workspaceId,Guid? departmentId,bool includeEnded,int page,int pageSize,CancellationToken)` ทั้งหมดTask<IResult>; options คือ `UserOptionsAsync(string prefix,int page,int pageSize,CancellationToken):Task<IResult>`, `WorkspaceOptionsAsync(string prefix,int page,int pageSize,CancellationToken):Task<IResult>`, `DepartmentOptionsAsync(Guid workspaceId,string prefix,int page,int pageSize,CancellationToken):Task<IResult>`

- [x] เพิ่ม boundary test เมื่อการแก้จริงเกิดใน instant เดียวกับ validFrom: ส่ง409โดยไม่แก้ข้อมูล ไม่สร้างช่วงว่างและไม่บวกเวลาเทียม; คำขอที่ไม่เปลี่ยนค่าให้คง no-op200 ไม่ revoke session

- [x] HTTPRED enumerate12routesตามตารางก่อนimplement โดย anonymousต้อง401, normalstaff403, operatorwithoutrecentMFA403, forgedbodyunknownfield400, wrongparent404, missingcsrf403; route404ก่อนimplementถือREDของmissingrouteต้องยืนยันภายหลังว่าไม่ใช่testwrongURL

```csharp
[Theory]
[InlineData("memberships")]
[InlineData("reporting-lines")]
[InlineData("hr-assignments")]
public async Task Anonymous_cannot_list_directory(string resource)
{
    await using var d = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
    using var response = await d.Client.GetAsync("/api/v1/attendance/directory/"+resource);
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}
```

- [x] Run `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceDirectoryApiTests`; ExpectedRED thenwire registration/endpoints auth+CSRFตามexistingmiddleware ติดbindingboundaryแบบOrganizationเพื่อauditinvalidbodyโดยไม่logpayload
- [x] Implementservicewrapperตามลำดับนี้ในทุกmutation ไม่ให้endpointfilterเป็นsecurityauthorityเพียงชั้นเดียว:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
// ใช้current.Entity.UserId + guard.AllowsAsync(actor,"attendance:directory-manage",ct)
// เมื่อdeny: rollback, clear tracker, write bounded denial audit ในclean transaction
// เมื่อผ่าน: ตรวจversion/parents/currentgraph/self-elevation แล้วเปลี่ยนrow+audit
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

โค้ดระหว่างguardกับsaveต้องทำตามoperationตาราง: SetMembershipปิดoldline+oldmembershipและเพิ่มnew; SetReportingตรวจactiveemployee/supervisor/sameworkspace/cycleและปิดoldline; GrantHrตรวจtupledupและactiveunit; EndตรวจexpectedVersionและปิดrow ห้ามDELETE; statusตามสัญญา ไม่ใส่placeholdercallbacksข้ามvalidation

- [x] ก่อนdirectorymutationคำนวณread/approveauthorityของactorจากgraphทั้งก่อนและหลัง หากactorกลายเป็นหัวหน้าของผู้อื่นใหม่ หรือได้HRunitใหม่ให้403 แม้targetemployeeไม่ใช่actor; ห้ามเปลี่ยนmembershipของactorเองผ่านadminsurfaceนี้เพื่อย้ายscope ให้ผู้ดูแลอีกคนดำเนินการ พร้อมauditเหตุผล
- [x] ทุกผล200/201/204มี audit ใน transaction; revoke เฉพาะ affected users เมื่อข้อมูลเปลี่ยนจริง (no-op ไม่ revoke);409ไม่มีmutation; overlap/version constraint ส่ง409 ส่วน database unavailable/audit fault ส่ง503 ห้ามreturnlazyIQueryable/streamหลังcommit Listต้องprojectไม่มีsecret/filterก่อนcount/page ใช้stableorderid
- [x] เพิ่มtests grant/replace/end historycounts, no-op, exactpage100/prefix%literal, emptyGuid, actorforged, duplicate/versionraces, unauthorizedไม่มีrow, old/newmanagerrevoked, auditfailurebefore/afterSaveและdowntimeไม่returnsuccess; assertauditactor/target/reason/changedfieldsไม่มีPIIดิบ
- [x] Run `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceDirectoryApiTests|FullyQualifiedName~AttendanceDirectoryAtomicityTests'`; ExpectedPASS noSkipped; Commit `feat: add audited attendance directory administration`

## Task 5: Read policy และเส้นทางอนุมัติสำหรับผู้ใช้จริง

**Files:** สร้างAttendanceContracts/AttendanceAccess/AttendanceRouteResolver; แก้AttendanceRegistration; tests `AttendanceAccessTests.cs`, `AttendanceRouteTests.cs`; ไม่มีtest-onlyHTTPendpointในProduction

**Interfaces:** ตรงsignatureในสัญญาร่วมทุกตัว callerถือtransaction+lock7241002; reuse `ScopeAccess.ValidateSessionAsync(bool,CancellationToken)` เพื่อยืนยันsessionโดยไม่เรียก `ScopeOperation.RunAsync` ซ้อนtransaction; Resolveอ่านsubjectsnapshotที่serverloadแล้ว ห้ามtrustinputจากbrowser

- [x] TestREDสร้างrealDB membership/line/HRและrealLogin sessionsด้วยdriver: ownread→photo/GPStrue, supervisor→photo false/GPStrue, unrelated→CanReadfalse, HRwrongunitdeny, Adminonlydeny โดยassertfullrecordไม่ใช่แค่capabilitystrings

```csharp
Assert.Equal(new AttendanceReadDecision(true,true,false,AttendanceReadBasis.Supervisor,null), decision);
Assert.DoesNotContain("attendance:team-read", sessionView.Permissions);
```

สองตัวแปรจากactualservicecall/GETsessionหลังsetup—not mocksreturnconstant; เรียกserviceจากFactory.Services.CreateScope(), resolveTpr10DbContext/RequestSessionและโหลดentityของcookieที่loginจริงตามรูปแบบScope testsเดิม ไม่ปลอมactorในsubject

- [x] Run `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceAccessTests|FullyQualifiedName~AttendanceRouteTests'`; ExpectedRED ก่อนimplementselectors
- [x] ReadAsync: validatecurrent session; verify snapshotmembershipIdowner/unit/periodจริง; ownก่อน→both; จากนั้นHRglobalattendancecap+nonstaffallowedclass+recentMFA+activeHrAssignment exactsnapshotunit→both; จากนั้นsupervisorcap+recentMFA+currentdirectline+employeecurrentmembershipId==snapshotmembershipId+activeunits→GPSonly; นอกนั้น404 ไม่มีAdminfallback การกลับหน่วยเดิมด้วยmembershipใหม่ไม่เปิดrecordของmembershipเก่า
- [x] CurrentEmploymentAsyncค้นperiodมีผลณatด้วยUTCและactiveunit ไม่ดึงจากSite; DescribeAsyncquerycurrentrelationships+capabilitiesเป็นhintเท่านั้น ไม่มีrowของลูกทีม/HRnamesออกมา
- [x] ResolveAsyncตรวจsubjectemployeeยังactiveและcurrentprimarymembershipตรงsnapshotunit; หากย้ายหน่วยให้409ต้องจัดrouteใหม่ ไม่silentfollownewunit; เลือกdirectsupervisorจากcurrentlineซึ่งactiveมีcapapprove-supervisor+allowedclass (ไม่ต้องมีlive MFA ณrouting แต่ตอนactionต้องตรวจ); HRcandidateมีapprove-hr+currentHRassignmentและactiveidentity excludeemployee/supervisor;ไม่มีsupervisorหรือHRcandidateให้409; returnsortedids/versionเพื่อsnapshot ไม่ลงapprovedstateใดใน6A
- [x] เพิ่มRED→GREEN temporaltests: lineสิ้นสุดexactnow, HRexpired, inactiveparent, movedemployee, managerเปลี่ยน, actorroleถูกถอน, ownหัวหน้าดูรูปตนได้, หัวหน้าที่มีexplicitHRgrantดูรูปในHRbasis, transitivegrandchilddeny, headยื่นไปnextmanager, HRยื่นเองต้องคนอื่น, ผู้ใช้มีแต่scopeassignmentไม่globalattendancecapdeny; noMFAroutesnapshotไม่ได้ให้สิทธิ์action
- [x] ทดสอบcallerไม่มีtransactionต้องInvalidOperationException ไม่allow; rereadsessionหลังlock+raceRevocation deny; auditที่6B–6Dเป็นผู้รับผิดชอบoperation ขณะที่6A GETaccessaudits boundedได้โดยไม่เพิ่มข้อมูลภาพ/GPS
- [x] Runfilterเดิม ExpectedPASS; Commit `feat: resolve attendance privacy and two-step approver routes`

## Task 6: Portal directory, API contract และ Exit Gate 6A

**Files:** สร้างPortal/DirectoryForm/directory-client/directory-viewตามmap; `tests/attendance-directory.test.mjs`, `tests/e2e/attendance-directory.spec.ts`, `backend/tests/TPR10.Api.IntegrationTests/AttendanceDirectoryOpenApiTests.cs`; AttendanceOpenApiTransformer; docs `docs/architecture/module-6a-exit-gate.md`, `docs/runbooks/module-6a-directory.md`; แก้app/portal/page.tsx, lib/auth/auth-client.ts, IdentityOpenApiTransformer และHTTPSfixtureharness

**Interfaces:** `isDirectoryPage(value:unknown):value is DirectoryPage` ในTypeScriptต้องเลือกunionrowตามresource; `DirectoryForm({actorId}:{actorId:string})` ใช้ScopeFrame+SSRSessionเดิม; client ใช้authMutationเฉพาะPOSTallowlistdirectorytable ไม่regexเปิดทั้งattendanceprefix; queryใช้AbortController+generationและauth-change subscriptionตามuseScopeQuery

- [x] เพิ่มNodeREDทดสอบinvalidDTO/unknownrowfield/expiredview, mutationexactallowlist ปฏิเสธ`/attendance/directory/.../delete`, pathtraversal, absoluteURL, wrongmethod; ใช้node:test/assertตามtests/scope-helpers.test.mjs ไม่testgrep sourcecode
- [x] เพิ่มPlaywrightREDด้วยfixture3actor: directoryoperatorมีMFA, normalstaff, operatorอีกคน; staffเห็นdenial ไม่แสดงform; create/replace/endmembership+line+HR, selfelevationerror, stale409reload,503preservedrafthidden ข้ามaccountไม่มีDOMเก่า; dropdownข้อมูลoptionsมีpaginationไม่โหลดทั้งองค์กร

```ts
await expect(page.getByRole('heading', { name: 'จัดการบุคลากรและสายบังคับบัญชา' })).toBeVisible();
await page.getByRole('button', { name: 'บันทึกต้นสังกัด' }).click();
await expect(page.getByRole('alert').filter({ hasText: 'ข้อมูลเปลี่ยนแปลง' })).toBeVisible();
```

snippetคือส่วนassertionของtestหลังกรอกselectด้วยfixtureIDsและสร้างversionconflictผ่านAPIจากsecondclientจริง ห้ามmockให้200แทนmutationที่ต้องพิสูจน์ ก่อนimplementedpageREDต้อง404/headingmissingตามคาด

- [x] Run `node --test tests/attendance-directory.test.mjs` ExpectedRED ก่อนimplement parsing/allowlist; implement typedformsพร้อม disabled submitระหว่างรอ, reasons, expectedVersion และ noauto-retrymutation แสดงประวัติread-onlyไม่ให้แก้ย้อนหลัง
- [x] หน้าใหม่ `requirePortalSession('/portal/admin/attendance-directory')`, checksystemdirectorycapสำหรับUX และScopeFrameส่งsessionSSR; addPortalnavเฉพาะผู้มีสิทธิ์ APIยังตรวจจริง ห้ามอ้างหน้า6Aเป็นระบบลงเวลาเสร็จ
- [x] Harnessแยก `infra/nginx/smoke-attendance-directory-https.mjs` หรือrefactorsharedfixtureจากexistingharnessโดยรักษาallowlistเดิม ห้ามเพิ่มgeneric --spec arbitrarypath; newharnessรับ4000/4001และรันtestfileชื่อคงที่ attendance-directory.spec.ts พร้อมseedsyntheticoperator/unitsผ่านfixtureprojectไม่ผ่านProductionAPIseedingendpoint; ไม่เปลี่ยนCAtrustของเครื่อง
- [x] OpenAPIRED enumerate12directoryoperations+GETaccess=13operationsทั้งDevelopment/Production (ไม่มีtechnicalmutationเพิ่ม) ต้องcookie,CSRFสำหรับPOST,permission+MFA,requiredfields,200/201/204/errors/500default; enumexactdomainmetadata `attendance-directory` ไม่ปลอมเป็นexact-business scope
- [x] ImplementAttendanceOpenApiTransformerให้IdentityOpenApiTransformerเรียกแบบexplicitเช่นScopeTransformerเดิม ไม่แข่งregistrationorderหรือเขียนทับmetadataModule2/3; accessdocumentboolhintไม่ใช่authority
- [x] Run `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceDirectoryOpenApiTests` และNodefilter ExpectedPASS; runHTTPS dev/prodตามท้ายแผน Expectedทุกcaseผ่านจริง ไม่skip/เพิ่มtimeoutกลบerror
- [x] เขียนrunbookไทย: operatorgrantผ่านrolesAPIโดยoperatorอีกคน, migrationbackup, end/replacehistory,ไม่มีselfgrant, HRassignmentไม่ให้Sitepermission, disable/revoke, pendingroutesในอนาคตต้องrevalidate, ไม่เอาบัญชีPreviewจริงลงเอกสาร
- [x] Fullverificationตามท้ายแผนแล้ว freshwholebranchreviewทั้ง6tasks againstspec+5ReviewFocus หากCritical/Importantหนึ่งTDDfixpassและรันfullsuiteใหม่ ส่วนMinorระบุความเสี่ยง/owner ไม่ปิดด้วยคำว่าtestpass
- [x] Commit `feat: add attendance directory portal and verified exit gate`; บันทึกSHA/UTC/counts/commandsactualและscope6Aสำเร็จเท่านั้น ห้ามเขียนexpectedcountsเป็นผลจริง

## คำสั่งตรวจและการปิดแผน

ใช้Node/.NET/Dockerที่โปรเจกต์กำหนดไว้ ไม่ติดตั้งรุ่นอื่นเพื่อหลบfailure ขั้นเริ่มimplementationให้ตรวจbaselineก่อนtask1 และบันทึกว่าการอนุมัติแผนไม่อนุมัติหยุดPreviewผู้ใช้ หาก4000/4443ถูกใช้ต้องขอจัดช่วงตรวจหรือใช้ isolatedhostที่รองรับ ไม่killprocessอื่นเอง

```sh
dotnet restore backend/TPR10.sln
dotnet test backend/TPR10.sln
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
dotnet build backend/tests/TPR10.E2E.Fixture
dotnet format backend/tests/TPR10.E2E.Fixture --verify-no-changes --no-restore
npm ci
npm test
npm run lint
node infra/nginx/smoke-identity-https.mjs 4000 --e2e
node infra/nginx/smoke-identity-https.mjs 4000 --e2e --spec tests/e2e/scopes.spec.ts
node infra/nginx/smoke-attendance-directory-https.mjs 4000
npm run build
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes.spec.ts
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes-boundary.spec.ts
node infra/nginx/smoke-attendance-directory-https.mjs 4001
dotnet list backend/TPR10.sln package --vulnerable --include-transitive
npm audit
git diff --check
```

Expected: exit0ทุกคำสั่ง ไม่มีskipที่ซ่อนsecurityacceptance หากauditพบรายการใหม่ต้องวิเคราะห์ไม่อ้างว่าไม่พบโดยใช้ผลเก่า; build warning/deprecationต้องรายงานตรงจริง, schema roundtripใช้disposableDBไม่ฐานPreview/Production

## Pre-flight และ self-review ของแผน

| ขอบเขตรอยต่อ | ข้อสรุปที่ตรวจแล้ว |
| --- | --- |
| Catalog→seed→RoleAdministration | เพิ่ม19permissionแต่ไม่เพิ่มgrant7ตัวเอง; domainattendanceไม่ผ่านsystemguard; explicit8seedallowlist |
| Directory→Identity | lifecycleคืนaffectedIDs callerrevokeและcommitaudit; lock7241002ก่อนอ่านสิทธิ์ไม่ซ้อนScopeOperation |
| Snapshot→access | recordowner/unitมาจากsnapshotที่serverโหลด; recentcurrentgrantsไม่ให้scopeจากclient |
| Access→route | readbasisOwnไม่ใช่approveauthority; supervisor/HRคนละคนและexcludeผู้ยื่น |
| UI→API | booleanhint/ซ่อนปุ่มไม่แทนauthorization; SSRsession+authchangeclear+boundedoptions |
| 6A→6B/6C/6D | interfacesส่งออกมีsignatureชัด; ยังไม่สร้างevidence/day/requesttable ไม่มีการอ้างผ่านทั้งModule6 |

ข้อจำกัดรับรู้: identitylockรวมอาจจำกัดthroughput ต้องbenchmarkก่อนProduction; directDBadminยังนอกapplicationboundary; managementสองคนสมคบกันยังต้องgovernance; historicalbackfillก่อนมีdirectoryไม่มีหลักฐานจึงdenyไม่แต่งsnapshot; ไม่รับรองทุกbrowser/OSจากPlaywrightอย่างเดียว

ผลตรวจเอกสาร: Spec§5–6/11.3มีtaskรองรับครบ ข้อกำหนดอื่นมีowner6B–6Dในแผนรวม ไม่ใช่งานที่ลืม; interfacesใช้ชื่อเดียวตลอด; ไม่มีการอ้างว่ารันtests/build/lintแล้วจากเพียงการเขียนแผน

## จุดรออนุมัติ

ให้ผู้ใช้ตรวจแผนนี้ โดยเฉพาะการมอบสิทธิ์7รายการใหม่แบบexplicit, effective-nowdirectory, no self-elevation, ประวัติข้ามหน่วยไม่เปิดอัตโนมัติ และการแบ่ง6A–6D เมื่ออนุมัติแผน6Aจึงเริ่มTask1ด้วยNative/TDDตามวิธีที่รักษาไว้ หรือวิธีอื่นที่ผู้ใช้ระบุ ยังไม่อนุมัติpush/merge/deployจากเอกสารนี้

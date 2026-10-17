# Module 6D — Implementation Plan: คำร้องแก้ไขเวลาและรับงานรวม

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans หรือ superpowers:subagent-driven-development หลังผู้ใช้อนุมัติแผนนี้เท่านั้น ทำงานทีละ Task ตาม checkbox

**Goal:** ให้พนักงานเสนอแก้เวลา ผ่านหัวหน้าก่อน HR คนละคน เก็บทุก revision และหลักฐานเดิม พร้อมตรวจรับ Module6 ในขอบเขตที่อนุมัติ

**Architecture:** เพิ่มขอบเขต Corrections ภายใน Attendance modular monolith ใช้ Directory/Access ของ6A, Evidence ของ6B และ AttendanceDay/employee lock ของ6C ไม่สร้าง workflow engine ส่วนกลาง ไม่ให้ correction ปลอมเป็น live capture

**Tech Stack:** Next.js/TypeScript, ASP.NET Core/.NET10.0.401, PostgreSQL, xUnit/Testcontainers และ Playwright HTTPS

**Spec:** `docs/superpowers/specs/2026-09-25-module-6-attendance-design.md` โดยเฉพาะ R08–R12, §11–16; สัญญาส่งต่อ `docs/architecture/module-6c-exit-gate.md`

สถานะ: **Tasks 1–6 ผ่านการตรวจรับแล้ว** (18ตุลาคม2026 เวลาไทย) รวมPR6C #4 ตามการยืนยันผู้ใช้แล้วที่ `dc07cba` และเริ่มสาขา `codex/module-6d-corrections` จากฐานนี้; Tasks7–8 ยังไม่เริ่ม งาน6Dยังไม่push/merge และยังไม่ใช่ระบบคำร้องพร้อมใช้งานผ่านหน้าจอ

## Global Constraints

- ข้อความและเอกสารภาษาไทย; Dev4000 / production4001 / HTTPS4443
- Draft → PendingSupervisor → PendingHr → Approved; Pendingส่งกลับReturnedได้พร้อมเหตุผล ไม่มีreject-and-close
- ส่งใหม่ต้องrevisionใหม่และเริ่มหัวหน้าเสมอ ห้ามแก้ revision ที่submittedแล้ว
- Deadlineเป็นวันไทย: ก่อน00:00ของD+8 สำหรับครั้งแรก และR+8เมื่อส่งกลับ; Draftไม่ต่ออายุ; pendingไม่มีapproval SLA
- ห้าม self-approval และคนเดียวอนุมัติสองขั้น; Adminไม่มีbusiness bypass; ไม่มีrouteให้ระงับ
- หนึ่งactive requestต่อemployee/workDate; Expiredห้ามสร้างเรื่องใหม่เพื่อหลบกำหนด
- ใช้identity transaction/lock7241002ก่อน `AttendanceEmployeeLock.AcquireAsync(Tpr10DbContext, Guid, CancellationToken)`; ตรวจcurrent grantsหลังรอlock
- Original event/ภาพ/GPS immutable; adjustmentเก็บbefore/afterและapproved revision; ไม่สร้างภาพ/GPSย้อนหลัง
- ยังไม่ทำoffline, email, genericworkflow, payroll, bulkexport หรือautomatic evidence deletion
- มือถือจริงและNASพักตามคำสั่ง17ตุลาคม ไม่อ้างว่าผ่าน; Productionยังต้องload/deploymentrestore/dataowner acceptance
- ทุกTaskมีRED→GREEN, Test/Build/Lintก่อนอ้างเสร็จ; commitเฉพาะไฟล์Task ไม่push/mergeโดยอัตโนมัติ

## Review Focus

1. checkoutจริงชนHRอนุมัติ: ผู้ชนะหนึ่ง อีกฝ่าย409 ไม่มีเวลาหรือauditครึ่งเดียว (Task4)
2. ย้ายหน่วย/ถอนสิทธิ์ขณะรอ: route snapshotไม่คงอำนาจ; rerouteกลับขั้นแรกและaudit (Task3)
3. แก้วันทำงานหรือสร้างเรื่องใหม่หลบdeadline: ใช้วันต้นเรื่องและประวัติReturned ไม่ขยายdeadlineจากค่าที่ผู้ใช้เสนอ (Task2)
4. ขาดทั้งคู่กับข้อมูล6Cที่บังคับlive evidence: สร้างeffective dayผ่านadjustmentโดยไม่ใส่ภาพ/GPSปลอม และไม่ทำลายlive capture (Tasks1,4,5)
5. responseคำร้อง/รูปตอบช้าหลังเปลี่ยนบัญชี: ล้างDOM, cancellation, recheck identityก่อนแสดงผล; หัวหน้าไม่ได้evidence locator (Tasks5,7)

## สัญญาข้อมูลที่ต้องล็อก

ชื่อใหม่ด้านล่างเป็นข้อเสนอที่แผนนี้กำหนด ไม่ใช่APIที่มีอยู่แล้ว ทุกคลาสใหม่อยู่ `backend/src/TPR10.Api/Attendance/Corrections/` เว้นระบุเป็นอย่างอื่น

- `CorrectionState`: Draft, PendingSupervisor, PendingHr, Returned, Approved, Expired
- `CorrectionSite(Guid WorkspaceId, Guid ProjectId, Guid SiteId)`
- `CorrectionProposal(DateOnly WorkDate, DateTimeOffset InUtc, DateTimeOffset? OutUtc, CorrectionSite InSite, CorrectionSite? OutSite, string Reason)` เสนอeffective pairทั้งหมด ไม่ใช่patchที่เปลี่ยนความหมายเมื่อข้อมูลจริงเปลี่ยน เหตุผลtrim1–2000อักขระ UTC output เวลาออกต้องมากกว่าเข้า ห้ามเสนอเวลาอนาคต
- `SaveCorrection(Guid? DayId, DateOnly OriginalWorkDate, long? ExpectedDayVersion, CorrectionProposal Proposal)`; DayIdnullใช้ขาดทั้งคู่/ไม่มีdayเดิม ต้องมีhistorical employmentและassignmentที่พิสูจน์ได้
- `CorrectionCommand(Guid CommandId, long ExpectedVersion, int Revision, string? Reason)` สำหรับsubmit/approve/return/reroute; Reasonบังคับreturn/reroute1–2000
- `CorrectionResult<T>(T? Value, int Status, string? Problem)`; success200/201; malformed400; unauth401; permission/MFA/CSRF403; concealed404; conflict409; unavailable503
- `CorrectionView`: id, employeeId, originalWorkDate, version, state, deadlineUtc, currentRevision, proposal, baselineDayVersion, actions, readBasis; ส่งเฉพาะข้อมูลที่มีสิทธิ์ ไม่ใส่ภาพ/GPSในlist
- ทุกmutationใช้CommandId UUIDร่วมกับactor/request/action/payload digest เก็บreceiptในtransaction เดิม retrykeyเดิมและpayloadเดิมได้ผลเดิมหลังตรวจcurrent access; keyเดิมpayloadต่าง409 ไม่retry mutationอัตโนมัติ

## การเชื่อมschemaเดิมอย่างปลอดภัย

6C `AttendanceDay.EffectiveInUtc` ไม่nullable และ `AttendanceEvent` บังคับภาพ/GPSของlive ห้ามลดข้อบังคับเหล่านี้เพื่อใส่ค่าปลอม 6Dใช้ `AttendanceAdjustment` เป็นหลักฐานapproved-correction โดยไม่insert live event ถ้าไม่เคยมีday ให้สร้างdayที่มีเวลาเข้าและsnapshotประวัติจากapproved proposal; ถ้ามีเข้าเดิมแก้ออกอย่างเดียว ให้รวมเวลาเข้าเดิมเป็นproposalเต็ม

Task1ต้องอ่านtrigger `attendance_preserve_day`/publication guardsจริงก่อนเปลี่ยน: อนุญาตeffective fields/workDateที่เปลี่ยนได้เฉพาะเมื่อมีapproved adjustmentอ้างbefore versionและaftervaluesตรงกันในtransaction ไม่ใช้session flagเป็นทางผ่านทั่วไป ไม่แก้EmployeeIdหรือsnapshotของdayเดิม การเปลี่ยนWorkDateต้องตรวจmembership ณ เวลาเข้าใหม่ตรงsnapshotเดิม มิฉะนั้น409ให้ส่งกลับ ไม่ย้ายความเป็นเจ้าของข้อมูลย้อนหลังเงียบ ๆ

Correction-only dayไม่มีรูป/GPS ต้องแสดงว่าไม่มีหลักฐานถ่ายสด ประวัติต้องอ่านeffective sitesจากadjustmentล่าสุดและoriginal sitesจากeventsแยกกัน ไม่เปลี่ยนภาพที่ประทับเวลาเดิม ไม่ปลดevent immutability

## Task 1: Schema คำร้องและadjustment พร้อม migration guard

**Files:** สร้าง `Corrections/CorrectionEntities.cs`, `Corrections/CorrectionContracts.cs`, `Corrections/CorrectionModelConfiguration.cs`; แก้ `Data/Tpr10DbContext.cs`; สร้างEF migration `AddAttendanceCorrections` และguard SQLในmigration companion; tests `backend/tests/TPR10.Api.IntegrationTests/CorrectionSchemaTests.cs`

**Interfaces:** สร้าง CorrectionRequest, immutable CorrectionRevision, CorrectionApprovalAction, AttendanceAdjustment, CorrectionCommandReceipt และ CorrectionRouteSnapshot; requestversionเริ่ม1; revisionเลขเพิ่มต่อrequest; approvalunique(request,revision,step); adjustmentuniqueapprovedrevision; เก็บoriginalWorkDateและdeadlineanchorไม่ให้แก้

- [x] เขียนRED `Submitted_revision_is_immutable`, `Only_one_active_request_per_original_day`, `Expired_history_cannot_be_bypassed`, `Effective_change_requires_exact_approved_adjustment`, `Live_event_still_requires_evidence` และ migration up/down/up; rollbackที่มีbusinessdataต้องปฏิเสธ ไม่dropทิ้ง
- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~CorrectionSchemaTests` คาดFAILเพราะschema/guardsยังไม่มี
- [x] implement schema/indices/compositeFK/guardsตามสัญญาข้างต้น; uniqueactiveสำหรับOriginalWorkDate และคงuniqueวันของAttendanceDayป้องกันtargetWorkDateชนกัน; การเรียกemployee lockในบริการอนุมัติอยู่Task4ตามสัญญาร่วม
- [x] รันคำสั่งเดิมคาดผ่านทุกเคส พร้อมชุด AttendanceRecordingSchemaTests/EvidenceBindingTests
- [x] commit `feat(attendance): add correction schema and immutable adjustment guards`

### บันทึกการเชื่อมกับระบบเดิมของ Task 1

- วันทำงานสัมพันธ์กับเวลาเข้าที่มีผลหลังอนุมัติ ส่วน snapshot ต้นสังกัดและเวลาของภาพต้นฉบับยังแก้ไม่ได้ การเปลี่ยนวันต้องมี adjustment ที่ตรงทั้งค่าก่อนและหลัง
- การตรวจว่าการแก้วัน/เวลาและหลักฐานเกิดใน transaction เดียวกัน ใช้หมายเลขที่ trigger ฝั่งฐานข้อมูลเขียนเอง ไม่ใช้ค่า session ที่ผู้เรียกเปิดข้ามกติกาได้ และรองรับ savepoint ของ EF
- ปรับเฉพาะ setup ของ regression tests ที่เคยปิดรอบด้วย SQL โดยตรง ให้สร้างเหตุการณ์ออกงานใน transaction เดียวกัน คง assertion เดิมทั้งหมด
- Task4 ต้องเชื่อมการลงเวลาออกจริงกับวันที่เริ่มจากคำร้องและไม่มี CheckIn event โดยตรวจ approved adjustment; Task1 ไม่ปลด guard ของ live event ไว้ล่วงหน้า
- การตรวจสิทธิ์ปัจจุบัน เส้นทางอนุมัติ การส่งกลับ/หมดอายุ และประวัติ Site เป็นหน้าที่บริการใน Tasks2–5; schema ไม่ใช่การส่งมอบ workflow ที่ใช้งานผ่านหน้าจอแล้ว
- ปรับfixtureทดสอบmigrationเก่าให้ใช้คอลัมน์6Cที่ตรึงไว้ ไม่ใช้EFmodelล่าสุดกับschemaเก่า; เปรียบเทียบbusinessfieldsทุกช่องยกเว้นtransaction markerภายในที่เพิ่งเพิ่ม และpinรุ่นmigrationที่กำลังตรวจrollback ไม่ลดการตรวจหลักฐานเดิม
- ผลสุดท้าย: backendทั้งโครงการ **1,498/1,498** (37.6523นาที), schemaใหม่17/17, migration regression70/70, Node60/60, frontend/backend Build, Lint, backend Format และdiff checkผ่าน ไม่มีskipในผลชุดเต็ม
- รอบเต็มแรกพบ3 failures ในEvidenceSchemaTestsจากfixtureข้างต้นและหยุดตรวจเพื่อแก้ ไม่ถือว่ารอบนั้นผ่าน; รอบที่สองผ่านครบตามจำนวนข้างต้น
- ตรวจทานdiffและสัญญาTask1โดยผู้พัฒนาแล้ว; independent whole-branch reviewยังอยู่Task8ตามกระบวนการNative ไม่อ้างว่าตรวจรับModule6Dทั้งชุดแล้ว

## Task 2: Draft, Revision และกำหนด7วัน

**Files:** สร้าง `Corrections/CorrectionDeadline.cs`, `Corrections/CorrectionDraftService.cs`, `Corrections/CorrectionSiteHistory.cs`; tests `CorrectionDraftTests.cs`

**Interfaces:** `CorrectionDeadline.First(DateOnly): DateTimeOffset`, `Returned(DateTimeOffset): DateTimeOffset`; service `CreateAsync(SaveCorrection, Guid commandId, CancellationToken): Task<CorrectionResult<CorrectionView>>`, `SaveAsync(Guid id, SaveCorrection, CorrectionCommand, CancellationToken)`; `CorrectionSiteHistory.ValidateAsync(Guid employeeId, CorrectionProposal, CancellationToken): Task<int?>`

- [x] RED: D25ก.ย.deadlineคือ2ต.ค.17:00UTC (3ต.ค.00:00ไทย); ก่อน1tickบันทึกร่างได้/ตรงdeadlineไม่ได้; weekendนับ; Returnedคำนวณจากวันไทย; Draftsaveไม่ขยายdeadline; เปลี่ยนtargetdateไม่เลื่อนoriginaldeadline; actorbodyปลอมไม่ได้ การ submit จริงเชื่อมใน Task3
- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~CorrectionDraftTests` พบFAILตามคาดก่อนมีบริการ
- [x] implementเฉพาะเจ้าของและสถานะDraft/Returnedที่ยังไม่หมดอายุ; proposalเปลี่ยนสร้างrevisionใหม่ ไม่แก้revisionเก่า; ตรวจhistorical membership/assignmentจากข้อมูลที่เก็บจริง ไม่ใช้grantปัจจุบันแทนประวัติ ถ้าหาหลักฐานไม่ได้409
- [x] GREENคำสั่งเดิม พร้อม test expiredrecreate, missingboth, editsite/date, nofuturetime และinvalidtimeorder
- [x] commit `feat(attendance): add correction drafts and calendar deadlines`

### ผลตรวจและขอบเขต Task 2

- สร้าง/บันทึกร่างจากตัวตนใน session ตรวจสิทธิ์ปัจจุบันหลังถือ identity/employee lock; request, revision, audit และ receipt บันทึกใน transaction เดียว
- เก็บ revision เดิม ไม่ขยาย deadline เมื่อบันทึกหรือเปลี่ยนวันเสนอ; คำร้อง Returned ที่พ้นกำหนดเปลี่ยนเป็น Expired และไม่เพิ่ม revision
- ประวัติ Site ใช้ exact assignment ช่วง CreatedAtUtc ถึงก่อน RevokedAtUtc และ membership ช่วงที่มีผลจริง ไม่อนุมานประวัติ capability จาก role ปัจจุบัน ส่วนสิทธิ์ทำรายการปัจจุบันตรวจ CanRecord แยกตามนโยบาย6A หากต้องพิสูจน์ capability ย้อนหลังละเอียดกว่านี้ต้องมีข้อมูลประวัติเพิ่ม
- คำร้องใหม่ที่ไม่มี Day ต้องให้วันต้นเรื่องตรงวันเสนอฉบับแรก แล้วจึงแก้วันใน revision ได้โดยไม่เปลี่ยนวันต้นเรื่อง/กำหนดเดิม เพื่อป้องกันเลือกวันต้นเรื่องใหม่กว่าเวลาเพื่อยืดกำหนด
- รับเวลาแบบ UTC หรือ offset เช่น +07:00 แล้วแปลงเป็น UTC ก่อน query และบันทึก ตรวจ regression กับ PostgreSQL จริงแล้ว ไม่เปลี่ยนความหมายของ instant
- ชุดเฉพาะ **34/34**; backend ทั้งโครงการ **1,532/1,532** ใช้37.3197นาที ไม่มีskip; Node **60/60**; frontend/backend Build, Lint, backend Format และ diff check ผ่าน
- หลักฐาน TDD: รอบแรก19FAILเพราะบริการยังไม่มี; พบ audit metadata ไม่ตรง allowlist และ test DateOnly ใช้ปฏิทินของเครื่อง จึงแก้ให้ใช้สัญญาเดิมและปฏิทินชัดเจน; test เพิ่มจับช่องวันต้นเรื่องและ offset ได้ก่อนแก้ production code
- รอบเต็มแรกหยุดเพื่อเพิ่ม timezone regression จึงไม่ถือว่าผ่าน; ผลข้างต้นมาจากรอบเต็มสุดท้ายหลังแก้แล้ว ไม่ใช้ผลทดสอบบางส่วนแทนทั้งชุด
- ตรวจทานโดยผู้พัฒนาแล้ว; independent whole-branch review ยังอยู่ Task8 ยังไม่มี HTTP endpoint/หน้าจอคำร้อง (Tasks6–7) และยังไม่ได้ส่งมอบเส้นทางส่ง/อนุมัติ (Tasks3–4)
- มือถือจริง/NAS จริงยังพักตามคำสั่ง และไม่ถือว่าอนุมัติ Production

## Task 3: ส่งคำร้องและเส้นทางอนุมัติ

นโยบายที่ผู้ใช้ยืนยันเพิ่มเติม: ผู้จัดเส้นทางใหม่ต้องมี `attendance:directory-manage` และ MFA ล่าสุด พร้อม HR assignment ที่ยังมีผลตรง Workspace/Department ของคำร้องครบทั้งสามเงื่อนไข ไม่ได้สิทธิ์จากการเป็น HR หรือ Admin เพียงอย่างเดียว และไม่สร้างสิทธิ์จัดหน่วยงานชุดใหม่

**Files:** สร้าง `Corrections/CorrectionRouting.cs`, `Corrections/CorrectionSubmission.cs`; tests `CorrectionRoutingTests.cs`

**Interfaces:** ใช้ `IAttendanceRouteResolver.ResolveAsync(EmploymentSnapshot, CancellationToken): Task<AttendanceRouteDecision>` ของ6A; `CorrectionSubmission.SubmitAsync(Guid, CorrectionCommand, CancellationToken)` และ `RerouteAsync(Guid, CorrectionCommand, CancellationToken)` คืน `Task<CorrectionResult<CorrectionView>>`

- [x] RED: routeขาด/วน/self/ไม่มีHRคนอื่น409; หัวหน้ายื่นส่งหัวหน้าของตน; HRยื่นไม่อยู่candidate; membershipเปลี่ยนจนresolverปฏิเสธให้ค้างไม่override; pendingไม่expire; Returnedเกินกำหนดส่งไม่ได้
- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~CorrectionRoutingTests` พบ21FAILเพราะบริการยังไม่มี
- [x] Submitตรึงroute+revisionในtransaction; snapshotไม่ให้สิทธิ์ข้ามcurrentgrant Rerouteให้ผู้มี `attendance:directory-manage` พร้อมrecentMFAและหน่วยงานที่จัดการได้ ทำได้เฉพาะpending/routeblocked; คำนวณใหม่จากresolverเท่านั้น ไม่รับผู้อนุมัติจากbody; สร้างroute revision เก็บค่าproposalเดิม invalidates approvals กลับPendingSupervisor พร้อมเหตุผล/audit ไม่ต่อdeadlineพนักงาน
- [x] GREEN พร้อม race revokeระหว่างรอlockและ rerouteไม่ข้ามไปHR; หากcurrentgrantไม่มีหน่วยงานจัดการได้ให้403 ไม่ใช้Adminbypass
- [x] commit `feat(attendance): route correction revisions through current grants`

### ผลตรวจและขอบเขต Task 3

- ชุดเฉพาะ **37/37**; backend ทั้งโครงการ **1,569/1,569** ใช้37.7179นาที ไม่มีskip; Node **60/60**; frontend/backend Build, Lint, backend Format และ diff check ผ่าน
- ส่งคำร้องเฉพาะเจ้าของ ตรวจสถานะ/version/revision และ deadline จริงก่อนสร้าง route; ส่งใหม่หลัง Returned ใช้ revision ใหม่ถ้าฉบับเดิมเคยมี route แล้ว ไม่ใช้ผลอนุมัติเดิม
- ผู้จัดเส้นทางต้องผ่านทั้งสามเงื่อนไขที่ผู้ใช้ยืนยัน ตรวจใหม่หลังรอ identity lock รวมการ retry; หัวหน้าใหม่มาจาก resolver จริง ไม่รับรายชื่อผู้อนุมัติจากคำสั่ง
- เก็บ proposal, route และ approval ฉบับเก่าไว้; reroute สร้างฉบับใหม่กลับ PendingSupervisor และไม่เลื่อน deadline; pending ไม่หมดอายุระหว่างรอ
- routeblocked หมายถึง pending ที่หาเส้นทางปัจจุบันไม่ได้ ไม่เพิ่มสถานะใหม่หรือเปิดให้ HR ส่ง Draft/Returned แทนพนักงาน หากภายหลังต้องการสถานะแยกต้องปรับสัญญา API ก่อน
- เหตุผล reroute ครบ1–2000อักขระเก็บใน `CommandReason` ของ immutable receipt JSON; audit อ้าง command/revision เพื่อไม่ตัดข้อความตามข้อจำกัด metadata1000อักขระและไม่ใส่ข้อความอิสระใน security metadata ไม่ส่ง field นี้เพิ่มใน mutation response; Task5 ต้องอ่านบันทึกนี้ผ่านสิทธิ์ก่อนแสดงประวัติ หากเปลี่ยนที่เก็บต้องรักษาเหตุผลเดิมครบ
- รอบ GREEN แรกพบ2ข้อผิดพลาดใน fixture: ลบ HR history ที่ระบบห้าม และอ้างคอลัมน์ audit ผิด แก้ fixture ให้ใช้ history ที่ยังคงอยู่และ typed EventType โดยไม่ลด production guard จากนั้นผ่าน21/21 และเพิ่ม coverage จน37/37
- ตรวจทานโค้ดโดยผู้พัฒนาแล้ว; independent whole-branch review ยังอยู่ Task8 การอนุมัติ/ปรับเวลาจริงอยู่ Task4 และ HTTP/หน้าจออยู่ Tasks6–7 มือถือจริง/NAS จริงยังพัก และไม่ถือว่าอนุมัติ Production

## Task 4: อนุมัติ ส่งกลับ และใช้เวลาที่อนุมัติแบบatomic

**Files:** สร้าง `Corrections/CorrectionApproval.cs`, `Corrections/CorrectionAdjustmentWriter.cs`; tests `CorrectionApprovalTests.cs`, `CorrectionRaceTests.cs`; ใช้ fixture ร่วมจาก `CorrectionRoutingTests.cs`; เพิ่ม migration `AllowCorrectedAttendanceCheckout` โดยไม่แก้ migration เก่า ไม่ต้องแก้ `AttendancePairing.cs` เพราะอ่าน effective state อยู่แล้ว

**Interfaces:** `CorrectionApproval.DecideAsync(Guid id, CorrectionCommand command, string decision, CancellationToken): Task<CorrectionResult<CorrectionView>>`; decisionallowlist supervisor-approve/hr-approve/return; `CorrectionAdjustmentWriter.ApplyAsync(Guid requestId, int revision, CancellationToken): Task<int?>` ต้องอยู่ในtransactionเจ้าของเดิม ไม่commitเอง

- [x] RED: HRก่อนหัวหน้า403/409, คนเดิมสองขั้น403, self403, staleversion409, returnไม่มีเหตุผล400, retryเดิมไม่ซ้ำ, returnจากHRแล้วส่งใหม่ต้องหัวหน้าใหม่
- [x] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~CorrectionApprovalTests|FullyQualifiedName~CorrectionRaceTests'` พบ16FAILเพราะบริการยังไม่มี; เพิ่มREDอีก2กรณีสำหรับการลงเวลาออกของวันจากคำร้องและการย้อน migration
- [x] HRcommitถือidentity→employee lock ตรวจcurrentgrants, baselineDayVersion, uniqueวัน/รอบเปิด, วันไทย/เวลา/Siteประวัติ เขียนapproval+adjustment+dayversion+audit+receiptในtransactionเดียว; conflictไม่ปรับเวลาให้ส่งกลับได้ pendingไม่expire
- [x] GREEN: checkoutชนapprovalผู้ชนะ1, audit/DBfailไม่มีpartialsuccess, newdayขาดทั้งคู่ไม่สร้างภาพ/GPS, correctionปิดรอบเก่าแล้วเริ่มวันใหม่ได้, originalfilechecksum/eventไม่เปลี่ยน, disableactorขณะรอlockปฏิเสธ
- [x] commit `feat(attendance): apply two-step corrections atomically`

### ผลตรวจและขอบเขต Task 4

- เทสต์ใหม่ **32กรณี**; ชุดรวม approval/race/routing/schema **86/86** ผ่าน; backend ทั้งโครงการ **1,601/1,601** ใช้38.2753นาที ไม่มีskip; Node **60/60**; frontend/backend Build, Lint, backend Format และ diff check ผ่าน
- หัวหน้าอนุมัติก่อน HR คนอื่นเสมอ ตรวจบัญชี สิทธิ์ MFA และเส้นทางปัจจุบันก่อนใช้ receipt ซ้ำ ไม่เพิ่ม Admin bypass; คำร้องที่ pending ไม่หมดอายุเพราะเลยกำหนดยื่น
- ส่งกลับได้ทั้งสองขั้นพร้อมเหตุผล เริ่มกำหนดส่งใหม่จากวันที่ส่งกลับ; การส่งอีกครั้งสร้าง revision/route ใหม่และเริ่มหัวหน้าใหม่ ไม่ใช้ผลอนุมัติฉบับก่อน
- การอนุมัติขั้นสุดท้ายกับการลงเวลาออกจริงแข่งกันได้เพียงผู้ชนะเดียว อีกฝ่ายได้409; เมื่อ conflict ยังส่งกลับได้ ไม่มี approval/adjustment/receipt/audit ค้างบางส่วน
- วันขาดทั้งคู่สร้างเฉพาะ effective attendance และ adjustment ไม่สร้างภาพหรือ GPS ย้อนหลัง; การปิดรอบเก่าด้วยคำร้องทำให้เริ่มรอบวันใหม่ได้ โดยไฟล์และ event เดิมไม่เปลี่ยน
- ข้อตัดสินเพิ่มเติม: เพิ่ม migration ใหม่ให้กฎ checkout ใช้ approved adjustment ที่ตรง effective-in เป็นหลักฐานแทน live CheckIn ได้สำหรับวันที่มาจากคำร้อง ไม่แก้ frozen migration เก่าหรือลดกฎ evidence/immutability; rollback ปฏิเสธเมื่อมี adjustment ที่ต้องเก็บรักษา หากกฎนี้ผิดอาจทำให้ checkout ถูกปฏิเสธหรือไร้หลักฐาน จึงมี real recorder และ migration regression กำกับ
- รอบขยายครั้งแรกผ่าน85/86 เพราะ fixture ถอน Site assignment ขาดผู้ถอนและเหตุผล แก้เฉพาะ fixture ให้ครบ constraint เดิม แล้วผ่าน86/86 ไม่ลดกฎ production
- ตรวจทานโค้ดโดยผู้พัฒนาแล้ว; independent whole-branch review ยังอยู่ Task8; HTTP/หน้าจออยู่ Tasks6–7 ไม่ได้เปิดใช้งานคำร้องบนหน้าเว็บใน Taskนี้ ไม่มี push/merge และไม่ใช่การอนุมัติ Production; มือถือจริง/NAS จริงยังพัก

## Task 5: อ่านคำร้อง คิวอนุมัติ และประวัติหลังแก้ไข

**Files:** สร้าง `Corrections/CorrectionQueries.cs`; แก้ `History/AttendanceHistory.cs`, `History/HistoryContracts.cs`; tests `CorrectionReadTests.cs`, `CorrectionDeliveryTests.cs`

**Interfaces:** `CorrectionQueries.ListAsync(string audience, int pageNumber, int pageSize, CancellationToken): Task<CorrectionResult<CorrectionPage>>`; `GetAsync(Guid, CancellationToken): Task<CorrectionResult<CorrectionView>>`; `CorrectionPage` มีitems,total,pageNumber,pageSize; audience mine/supervisor/hr; pageเริ่ม1,size1–100

- [x] RED: scopeก่อนpagination; original/adjustedเวลาแยก; correction-onlyไม่มีimage/GPS; หัวหน้าไม่มีevidenceIDs/URLs; HRข้ามหน่วย404; adminไม่มีbypass; expiredอ่านประวัติได้
- [x] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~CorrectionReadTests|FullyQualifiedName~CorrectionDeliveryTests'` คาดFAIL
- [x] implementผ่านpolicy6Aและcurrentroute; workflowgrantให้เฉพาะคำร้องที่ต้องตัดสิน ไม่เปิดประวัติทั้งองค์กร ใช้delivery lock/recheck/no-storeแบบAttendanceJsonResultเดิม
- [x] GREENพร้อม revokeระหว่างquery/audit/serialize, cursor/pageinvalid400, late responseไม่เผยข้อมูล และhistory6Cทั้งหมด
- [x] commit `feat(attendance): expose scoped correction history and queues`

### ขอบเขตและข้อตัดสิน Task 5

- รายการ `mine` อ่านเฉพาะของตนเอง; คิว `supervisor`/`hr` จำกัดตามขั้นที่รอตัดสิน เส้นทางและสิทธิ์ปัจจุบันก่อนนับจำนวนและแบ่งหน้า ไม่ให้ route เก่าแทนสิทธิ์ปัจจุบัน
- สิทธิ์ workflow อ่านเฉพาะคำร้องที่ตนต้องตัดสิน ไม่ทำให้ได้สิทธิ์อ่านประวัติลงเวลาหรือภาพทั้งหน่วยงาน ผู้มีสิทธิ์ประวัติ6Aอ่านรายละเอียดคำร้องที่เคยส่งแล้วได้ตามขอบเขตเดิม ร่างที่ยังไม่เคยส่งเป็นส่วนตัวของเจ้าของ หากต้องการเปิดร่างแก่ผู้อื่นต้องอนุมัตินโยบายเพิ่ม
- เพิ่ม `History` แบบ optional เฉพาะรายละเอียดคำร้อง: revision, เส้นทาง, ผลอนุมัติ/ส่งกลับ และเหตุผลเปลี่ยนเส้นทางเต็มจาก immutable receipt; ไม่ใส่ใน list และไม่เปลี่ยน constructor/receipt เดิม ลดผลกระทบต่อ retry ทั้งนี้ต้องมี regression ตรวจสัญญาเดิม
- ประวัติลงเวลาแยก event/ภาพ/GPS เดิมออกจาก effective time และ adjustment; วันที่มาจากคำร้องมี `in: null` ได้เมื่อมี adjustment รองรับ ไม่สร้างหลักฐานถ่ายสดปลอม หัวหน้าไม่ได้ evidence ID หรือ URL
- สถานที่ effective มาจาก revision ที่อนุมัติล่าสุด เว้นกรณีลงเวลาออกจริงหลัง adjustment ใช้ Site ของ event ออก; ถ้าตีความผิดอาจแสดงสถานที่เก่า จึงตรวจทั้ง correction-only, เวลาเข้า–ออกจากคำร้อง และ checkout จริงหลังคำร้อง
- ขยายไฟล์ Task5 ไปยัง parser/type/component ประวัติเดิมเพื่อรองรับข้อมูลใหม่ หากไม่ปรับหน้าเดิมจะปฏิเสธวันที่ไม่มี live event; ไม่ใช่การเริ่มหน้าคำร้องใหม่ใน Task7
- ส่ง JSON ผ่าน `AttendanceJsonResult` เดิม: ถือ identity delivery lock, ตรวจสิทธิ์ซ้ำหลัง audit และก่อนส่ง byte แรก, no-store และจำกัดเวลาส่ง 5 วินาที มีการทดสอบการถอนสิทธิ์ก่อน query, หมดอายุระหว่าง audit/หลังอ่าน และปล่อย lock เมื่อสำเร็จหรือ abort
- ชุดเฉพาะ Task5 และประวัติเดิมผ่าน58/58 (Task5เพิ่ม25กรณี); backend ทั้งชุดผ่าน1,626/1,626 ไม่มีข้าม ใช้เวลา38.4260นาที สิ้นสุด17ตุลาคม2026 ประมาณ19:20UTC; Node61/61, frontend Build/Lint ผ่าน; backend Build0warnings0errors และ Format ผ่าน
- HTTPS history บน production-build พอร์ต4001 ผ่าน11/11 พร้อม TLS/CSRF/cookie/host checks ไม่ใช่การ deploy Production และไม่แทนผลมือถือจริง/NAS จริง
- ตรวจทานโค้ดโดยผู้พัฒนา; review อิสระทั้งสาขาอยู่ Task8, API/หน้าคำร้องอยู่ Tasks6–7, ไม่มี push/merge และมือถือจริง/NAS จริงยังพัก

## Task 6: HTTP API, OpenAPI และ client contracts

**Files:** สร้าง `Corrections/CorrectionEndpoints.cs`, `lib/attendance/correction-types.ts`, `lib/attendance/correction-client.ts`, `lib/attendance/correction-view.ts`; แก้ `Attendance/AttendanceRegistration.cs`, `Program.cs`; tests `CorrectionApiTests.cs`, `CorrectionOpenApiTests.cs`, `tests/attendance-correction-client.test.mjs`

**Interfaces:** base `/api/v1/attendance/corrections`; POSTbase(Create), GETbase(mine), GET`/{id}`, POST`/{id}/revisions`(Save), POST`/{id}/submit`, `/supervisor-approve`, `/hr-approve`, `/return`, `/reroute`; GET`/approvals?stage=supervisor|hr` ภายใต้base ข้อมูลmutationตามSaveCorrection/CorrectionCommand ไม่รับactorId

- [x] REDสำหรับทุกroute/status/requiredfields/JSONenum; CSRF/MFA/permission/revocation/unknownfields; forgedbodyไม่เปลี่ยนstate;503ไม่logout
- [x] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~CorrectionApiTests|FullyQualifiedName~CorrectionOpenApiTests'` และ `node --test tests/attendance-correction-client.test.mjs` คาดFAIL
- [x] implementtyped binding/allowlists/problem-details/limits/no-store ใช้session identityไม่เชื่อbody; clientmutationsรับCommandIdเดิมสำหรับการตรวจผล/explicitretry ไม่replayเอง; GETก่อนretryเพื่อตรวจrevisionปัจจุบัน
- [x] GREENและOpenAPIrouteinventoryเดิม อธิบายreadbasis/fieldomission/401403404409400429503; capJSON64KiB ไม่รับmultipart
- [x] commit `feat(attendance): add secured correction API contracts`

### สัญญา HTTP ที่ยืนยันใน Task6

- ผู้ใช้ยืนยัน: เมื่ออนุมัติสำเร็จให้แสดงข้อความสำเร็จแล้วกลับคิว ไม่เพิ่มสิทธิ์อ่านย้อนหลังจากการเคยอนุมัติ รายละเอียดภายหลังตรวจสิทธิ์ประวัติ6Aเดิม
- ทุก mutation ตอบเฉพาะ `{ id, commandId }`; ไม่ส่ง proposal, history, employeeId หรือ readBasis จาก receipt ภายใน เก็บ receipt เดิมโดยไม่เปลี่ยน schema และตรวจ current authorization ซ้ำก่อนส่งผลภายใต้ identity delivery lock เดิม ระยะส่งสูงสุด5วินาที
- POST สร้างใช้ `{ commandId, input: SaveCorrection }`; POST revisions ใช้ `{ input: SaveCorrection, command: CorrectionCommand }`; คำสั่งอื่นใช้ `CorrectionCommand` ตรง ๆ ทุก field ต้องมี แม้ค่าที่เป็น nullable ให้ส่ง null ปฏิเสธ field ไม่รู้จัก/ซ้ำทุกระดับและจำกัด JSON64KiB ไม่รับ multipart
- GET รายการตนเองใช้ `?page=1&pageSize=20`; คิวใช้ `/approvals?stage=supervisor|hr&page=1&pageSize=20`; pageSize สูงสุด100 ค่านอกขอบเขต/ตัวกรองไม่รู้จัก/ซ้ำตอบ400 ขอบเขตถูกจำกัดก่อนนับและแบ่งหน้า
- state ใน JSON คงเลขเดิม: 0 Draft, 1 PendingSupervisor, 2 PendingHr, 3 Returned, 4 Approved, 5 Expired; client ปฏิเสธค่าที่ไม่รู้จัก ไม่เปลี่ยน enum ของบริการหรือ receipt เดิม
- client ไม่ retry/logout อัตโนมัติเมื่อ503หรือเครือข่ายล้มเหลว การ retry ที่ผู้ใช้สั่งเองอ่าน GET ก่อน ตรวจ version/revision/action สำหรับรายการเดิมและใช้ commandId/payload เดิม สำหรับ create ที่ผลหายยังไม่รู้ id ให้ตรวจรายการตนเองก่อน แล้วอาศัย receipt ฝั่ง API เป็นหลักฐาน ไม่อนุมานผลจากวันทำงานหรือรายการหน้าแรก
- Task7 ต้องยกเลิกและล้างข้อมูลเมื่อเปลี่ยนบัญชี ตรวจ acknowledgment ให้ตรงคำสั่งที่ส่งก่อนแจ้งสำเร็จ และไม่ถือ404/503หลังส่งว่า rollback เสมอไป เพราะคำสั่งอาจ commit แล้วแต่สิทธิ์อ่านหรือ MFA สิ้นสุดก่อนส่ง response
- ชุดเฉพาะ API/OpenAPI/delivery ผ่าน94กรณี; Node70กรณีและเว็บ Build/Lint ผ่านแล้ว ชุด backend ทั้งโครงการผ่าน1,655กรณี ไม่มีข้ามการทดสอบ ส่วนข้อความ OpenAPI แก้ฐานสิทธิ์เป็น `Supervisor` ให้ตรง enum จริง และตรวจ Build/Format/ชุดเฉพาะ94กรณีซ้ำผ่านก่อน commit ไม่มีการเปลี่ยน business logic หลังชุดเต็ม
- ตรวจทานโดยผู้พัฒนาแล้ว; review อิสระทั้งสาขาอยู่ Task8 หน้าคำร้องใหม่อยู่ Task7 มือถือจริง/NAS จริงยังพัก ไม่ถือว่า Module6D หรือการตรวจรับ Production เสร็จแล้ว

## Task 7: หน้าคำร้องและคิวหัวหน้า/HR

**Files:** สร้าง `app/portal/attendance/corrections/page.tsx`, `app/portal/attendance/approvals/page.tsx`, `components/attendance/CorrectionPortal.tsx`, `components/attendance/ApprovalPortal.tsx`; แก้เมนูPortalและhistory6C; สร้าง `tests/e2e/attendance-corrections.spec.ts`; เพิ่มspecในallowlist `infra/nginx/https-smoke-fixture.mjs`

**Interfaces:** ใช้types/clientจากTask6; UIแสดงbefore/proposed/effective/revision/deadline/actionhistoryและฐานสิทธิ์ ป้าย “เวลาที่อนุมัติแก้ไข”; approvalปุ่มไม่ให้แก้proposalแทนพนักงาน; returnบังคับเหตุผล

- [ ] REDbrowser: สร้าง/ส่ง/ส่งกลับ/แก้/ส่งใหม่/สองขั้น, conflictให้โหลดใหม่ไม่overwrite, expiredread-only, clocktimezoneไทย, missingbothไม่มีรูปปลอม, keyboard390pxไม่ล้น
- [ ] รัน HTTPSfixtureport4000 specใหม่คาดFAILเพราะหน้า/flowยังไม่มี
- [ ] implementหน้าไทย protected, cancel+clear DOMเมื่อaccount/visibilityเปลี่ยน ตรวจidentityก่อนแสดงlate result ห้ามเก็บreason/privatepayloadในlocalStorage; ไม่ซ่อน503ด้วยlogout
- [ ] GREENdev4000 แล้วproduction-build4001 พร้อม latebody/read/approve accountchange, revoke, multi-tabduplicate, successresponseหายตรวจGETก่อนretry ไม่มีtimeout relaxation
- [ ] commit `feat(attendance): add correction requests and approval inbox`

## Task 8: รับงานรวมและส่งต่อ

**Files:** สร้าง `docs/architecture/module-6d-exit-gate.md`; แก้deliverymapและแผนนี้; เพิ่ม `CorrectionRecoveryTests.cs` สำหรับrestore original+adjustment+approvalสัมพันธ์ครบ

**Interfaces:** รับผลงานTasks1–7 ไม่เพิ่มAPIธุรกิจใหม่

- [ ] เพิ่มacceptance restore DB+รูปสังเคราะห์ ตรวจoriginalchecksum/adjustedtimes/revision/ผู้อนุมัติไม่หาย ไม่อ้างว่าเป็นprocess-killหรือNASจริง
- [ ] รันbackendทั้งโครงการ `dotnet test backend/TPR10.sln`, `dotnet build backend/TPR10.sln --no-restore`, `dotnet format backend/TPR10.sln --verify-no-changes --no-restore`; `npm test`, `npm run lint`, `npm run build`, `git diff --check` คาดexit0ทั้งหมด ไม่มีskipซ่อนacceptance
- [ ] รันHTTPSmatrixเดิม6Cทั้งdev/prodและcorrectionsใหม่; independent whole-branchreviewหนึ่งครั้ง ตรวจspecและแก้Importantด้วยRED→GREENก่อนตรวจซ้ำ
- [ ] รายงานไทยแยกtechnical/currentpassing tests/known timeout risk/mobileNAS deferred/Productionยังรอ; ระบุผู้รับผิดชอบข้อมูลretentioncapacitybackup ยังไม่ลบภาพอัตโนมัติ
- [ ] commit `docs(attendance): record module 6d verification and handoff`; ไม่push/mergeจนผู้ใช้สั่ง

## ตรวจแผนกับSpec

R08–R12: Tasks1–4,6–7; R14–R15: Tasks3,5–7; R01–R07/R13: regressionและหลักฐานเดิมTasks1,4,5,8; §10atomicity/restore: Tasks4,8; §14security: Tasks3–7; §15acceptance: Task8 โดยmobileNAS deferredตามคำสั่งล่าสุด

ข้อเสนอในแผนที่ต้องยืนยันพร้อมอนุมัติ: reason1–2000, JSON64KiB, pagination1–100, การจัดrouteใหม่ผ่านdirectory-manageพร้อมscope/MFA, การใช้Adjustmentสำหรับcorrection-onlyแทนการปลอมlive event และการปฏิเสธเปลี่ยนวันเมื่อhistoricalmembershipไม่ตรงsnapshotเดิม ไม่เพิ่มadmin bypassเพื่อแก้ข้อขัดแย้งเหล่านี้

ใช้Native/TDDและwhole-branchreviewตามวิธีเดิม งานแต่ละTaskพึ่งschema/contractของTaskก่อนหน้า ไม่เปิดimplementerหลายคนแก้sharedschemaพร้อมกัน Tasks1–4 ผ่านแล้ว; ขั้นถัดไปคือTask5 เมื่อผู้ใช้ให้ดำเนินการต่อ

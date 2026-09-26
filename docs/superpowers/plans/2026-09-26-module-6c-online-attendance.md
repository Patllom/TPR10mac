# Module 6C — Implementation Plan: ลงเวลาออนไลน์พร้อมกล้องและ GPS

> **สำหรับผู้ดำเนินงาน:** ใช้ `superpowers:executing-plans` ตามวิธี Native ที่เลือกไว้ ทำทีละ Task ด้วย `superpowers:test-driven-development` และให้ผู้ตรวจอิสระตรวจทั้งสาขาก่อนส่งมอบ ผู้ใช้อนุมัติแผนและให้เริ่ม Task 1 วันที่ 26 กันยายน 2026

**Goal:** พนักงานลงเวลาเข้า–ออกออนไลน์ด้วยภาพสดและ GPS วันละหนึ่งคู่ รองรับข้ามวันและอ่านประวัติตามสิทธิ์จริง

**Architecture:** ใช้ modular monolith เดิม เพิ่มขอบเขต Recording และ History ภายใน Attendance ใช้ policy 6A และ image/storage pipeline 6B ไม่สร้างระบบสิทธิ์หรือที่เก็บรูปซ้ำ แยกเตรียมไฟล์นอก transaction ออกจากการ commit รายการเวลา หลักฐาน และ audit พร้อมกัน

**Tech Stack:** Next.js 15.5.26 / React / TypeScript, ASP.NET Core .NET 10 / EF Core / PostgreSQL, browser camera/geolocation และ HTTPS ไม่เพิ่มแพ็กเกจโดยไม่มีเหตุผล

**Spec:** [ข้อกำหนด Module 6 ที่อนุมัติแล้ว](../specs/2026-09-25-module-6-attendance-design.md) โดยเฉพาะ R01–R07, R14–R15 และ §7–10, §13–16

สถานะ 26 กันยายน 2026: PR #3 ของ 6B merge เข้า GitHub main แล้วที่ `8a965af2b47463c58b40005e99ef6e5a51f18adb` สาขาทำงาน `codex/module-6c-online-attendance` เริ่มจาก commit นี้ ใช้ worktree แยกเดิมที่ชื่อโฟลเดอร์ยังเป็น module-6b-evidence-storage ไม่แตะ checkout หลักที่มีงานค้าง แผนอนุมัติแล้ว Task 1 ผ่านชุดเฉพาะ33/33, backend1,303/1,303, Node43/43, Build/Lint/format ดู [รายงาน Task 1](../../architecture/module-6c-task1-recording-schema.md); Tasks 2–9 ยังไม่เริ่ม

## Global Constraints — ข้อบังคับร่วม

- ถ่ายจากกล้องสดทั้งเข้าและออก ไม่มี file-picker/drop/paste upload ไม่มี offline queue หรือ sync
- GPS ต้องมี: latitude −90 ถึง 90, longitude −180 ถึง 180, accuracyMeters มากกว่า 0 ทุกค่า finite; sample อายุไม่เกิน 60 วินาที ไม่มี geofence และไม่ปฏิเสธเพียงเพราะ accuracy ต่ำ
- Challenge อายุ 120 วินาที ผูก actor/session/action/exact Site/idempotency key ใช้ได้ครั้งเดียว หมดอายุต้องถ่ายใหม่ ยกเว้นอ่านผล operation ที่ commit แล้ว
- รับ JPEG/PNG ที่ decode ได้จริง ขนาดภาพไม่เกิน 10 MiB และ 20 ล้านพิกเซล ใช้ JPEG ประทับเวลา/ลบ metadata จาก 6B ไม่เก็บไฟล์ต้นฉบับเพิ่มถาวร
- ใช้ UTC เก็บเวลาและ `Asia/Bangkok` คำนวณวัน/แสดงผล เวลาใน pixels ใช้ instant เดียวกับ event ไม่ใช้เวลามือถือแทนเวลาเซิร์ฟเวอร์
- Unique คน+วันทำงานทั่วระบบ และมีรอบเปิดไม่เกินหนึ่งต่อคน เวลาออกต้องหลังเวลาเข้า วันทำงานยึดวันที่เข้า
- ต่าง Site/Workspace ได้เฉพาะ exact assignment ของแต่ละเหตุการณ์ ข้ามวันต้องยืนยันทำงานต่อเนื่อง ไม่กำหนดเพดานชั่วโมงเอง
- ลืมลงเวลา/แก้ย้อนหลังต้องผ่าน 6D เท่านั้น ใน 6C แสดงคำอธิบายว่ายังเปิดคำร้องไม่ได้ ไม่ทำปุ่มลิงก์เสียหรือทางแก้ตรง
- พนักงานอ่านรูป/GPS ตนเอง; หัวหน้าลูกทีมโดยตรงอ่าน GPS แต่ไม่ได้รูป; HR อ่านเฉพาะหน่วยงานที่ได้รับมอบหมาย Admin ไม่มี bypass
- สิทธิ์อ่านทั้งคู่ยึด employment snapshot ตอนเข้าและ current grants ไม่เปลี่ยนตาม Site หรือ membership ตอนออก หัวหน้า/HR ต้องผ่าน recent MFA เดิม
- ไม่ลด CSRF/session/MFA/TLS ไม่บันทึกภาพ/GPS ลง localStorage, service-worker cache, URL, logs หรือ analytics
- ไม่ลบรูปอัตโนมัติ ไม่แก้ root ของ storage เดิม การทดสอบ NAS จริงของ 6B และ Production gate ยังไม่ผ่าน
- พอร์ต dev 4000, production-build 4001, HTTPS preview 4443; ไม่เปลี่ยน Landing Page และไม่กระทบ Preview ที่เปิดอยู่

## Review Focus — จุดเสี่ยงที่ต้องมีหลักฐานทดสอบ

1. เวลาไทยข้ามเที่ยงคืนระหว่างตรวจคำขอกับเตรียมภาพ: ตรึงเวลาเพียงครั้งเดียวและคืนผลเดิมเมื่อ retry (Task 3–4)
2. ย้ายแผนกระหว่างเข้า–ออก: รูปขาออกใช้เวลาปัจจุบัน แต่สิทธิ์อ่านใช้แผนกตอนเข้า (Task 2, 5)
3. NAS สำเร็จแต่ DB/audit ล้มเหลว หรือสองแท็บส่งพร้อมกัน: ไม่มี partial success หรือคู่ซ้ำ (Task 4)
4. เปลี่ยนบัญชี/ถอนสิทธิ์ขณะรูปหรือรายการกำลังตอบกลับ: ไม่แสดงข้อมูลบัญชีเดิมและตรวจสิทธิ์ก่อนส่งจริง (Task 2, 5, 7–8)
5. GPS เก่า/นาฬิกามือถือคลาดเคลื่อน/กล้องถูกระงับเมื่อกลับแท็บ: แจ้งเหตุผล ถ่ายใหม่ได้ ไม่ส่งร่างเก่าเงียบ ๆ (Task 3, 7, 9)

## สัญญาเทคนิคที่แผนนี้กำหนด

### โครงสร้างและขอบเขต transaction

เส้นทางไฟล์ในเอกสารเป็น relative ต่อราก worktree; ชื่อไฟล์ migration ที่ EF สร้างมี timestamp จริงตอนทำ Task ไม่แก้ migration เก่าที่ merge แล้ว

| ข้อมูล | ข้อบังคับ |
| --- | --- |
| AttendanceDay | Id, EmployeeId, WorkDate, immutable EmploymentSnapshot ตอนเข้า, EffectiveInUtc/EffectiveOutUtc, Version; unique EmployeeId+WorkDate และ partial unique EmployeeId เมื่อยังเปิด |
| AttendanceEvent | Id, DayId, ActorId, Action, OccurredAtUtc, DeviceCapturedAtUtc, exact ScopeKey, GPS, EvidenceId, Source=`live`; immutable และ unique DayId+Action/EvidenceId; ยังไม่เปิด approved-correction ใน 6C |
| CaptureChallenge | Id, actor/session, action, exact ScopeKey, idempotency key, issued/expiry, consumed operation; ไม่รับ actor จาก client |
| AttendanceOperation | Id, actor/action/key unique, session/challenge binding, canonical payload digest รวม SHA256 bytes ภาพ, fixed OccurredAtUtc/event membership/day/version, state, lease/fence, committed result; ไม่เก็บ image bytes ใน DB |

ใช้ `AttendanceEmployeeLock.AcquireAsync(Tpr10DbContext db, Guid employeeId, CancellationToken ct): Task` ภายใต้ transaction ที่ `ScopeOperation.BeginAsync` เปิดและถือ identity lock `7241002` ก่อนเสมอ จากนั้น advisory transaction lock namespace `7241003` กับ hash แบบ SHA256 ของ UUID รูปแบบ canonical (4 bytes แรกเป็น signed big-endian int32; collision ทำให้รอเพิ่มเท่านั้น) 6D ต้องใช้ helper นี้ ไม่สร้าง lock ชุดใหม่ ห้าม filesystem I/O ใต้ lock

การส่งหนึ่งครั้ง: ตรวจ identity/CSRF/ขนาด/ชนิด/GPS/challenge → ตรวจ pairing และตรึงเวลา UTC precision microsecond/operation ใน transaction สั้น → `StorageRegistry.PinAsync` และ `EvidenceWriter.PrepareAsync` นอก transaction → transaction ใหม่ identity lock แล้ว employee lock → ตรวจสิทธิ์/challenge/lease/version/pairing ซ้ำ → เพิ่ม event/day, `StagePublicationAsync`, audit, result และ consume challenge → commit → ตอบสำเร็จ

หากเริ่มคำขอทันเวลาแต่ challenge หมดอายุก่อน business commit ให้ไม่ลงเวลาและต้องถ่ายใหม่; ภาพ prepared ไม่เผยแพร่และเข้ารายงาน orphan เดิม ไม่มี auto-delete ไม่ใช่การขยาย challenge ด้วย retry

Operation lease 60 วินาที พร้อม fencing token เพิ่มทุก claim; worker เก่าห้าม commit หลังถูกแทนที่ งานภาพใช้ budget 6B เดิม 50 วินาที ห้ามถือ employee lock รอไฟล์ Timeout ที่ client ไม่ได้แปลว่าฝั่ง server ล้มเหลว ต้องอ่านสถานะก่อนและไม่เปลี่ยน key อัตโนมัติ

### การเชื่อมสิทธิ์รูปกับวันทำงาน

คงสัญญา `PinAsync`/`StagePublicationAsync` ของ 6B: EvidenceObject snapshot เป็นต้นสังกัด ณ event และตรงกับเวลาประทับ ส่วนสิทธิ์อ่านเปลี่ยนใน `EvidenceReader.AuthorizeAsync` ให้ resolve binding → AttendanceEvent → AttendanceDay แล้วส่ง day snapshot เข้า `IAttendanceAccess.ReadAsync` ใช้ helper เดียวกันในการตรวจซ้ำก่อนส่งภาพ ไม่มี fallback กลับไปใช้ snapshot ของรูปเมื่อ day/event หาย

เพิ่ม FK จาก EvidenceBinding.EventId ไป event และ deferred constraint ตรวจ owner/action/time/EvidenceId ตรงกันเมื่อ commit ใช้ deferred publication guards เดิม ไม่เปิด HTTP publish ทั่วไป ปรับ synthetic fixtures 6B ให้มี day/event จริง Migration ต้อง preflight และหยุดพร้อมรายงานเมื่อพบ binding เก่าที่ไม่มี event ห้ามลบหรือแต่งประวัติเพื่อให้ migrate ผ่าน; rollback เมื่อมี business data ให้หยุดแทนการ drop หลักฐาน

### DTO และ HTTP

ชนิดร่วมใน `Recording/RecordingContracts.cs`:

```csharp
record CaptureChallengeRequest(EvidenceAction Action, ScopeKey Site, Guid IdempotencyKey);
record CaptureChallengeView(Guid Id, DateTimeOffset ServerNowUtc, DateTimeOffset ExpiresAtUtc);
record GpsSample(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset SampledAtUtc);
record CaptureMetadata(Guid ChallengeId, Guid IdempotencyKey, ScopeKey Site,
    DateTimeOffset DeviceCapturedAtUtc, GpsSample Gps, bool ConfirmContinuousWork, long? ExpectedDayVersion);
record CaptureReceipt(Guid OperationId, Guid DayId, Guid EventId, DateOnly WorkDate,
    DateTimeOffset OccurredAtUtc, Guid EvidenceId, long DayVersion);
record RecordingResult<T>(T? Value, int Status, string? Code, int? RetryAfterSeconds);
record CaptureOperationView(string State, CaptureReceipt? Receipt);
```

| Route ใต้ `/api/v1/attendance` | คำขอ/คำตอบ |
| --- | --- |
| POST `/capture-challenges` | JSON CaptureChallengeRequest → 201 CaptureChallengeView |
| POST `/check-ins`, `/check-outs` | multipart: `metadata` JSON หนึ่งส่วน <=16 KiB และ `photo` หนึ่งส่วน <=10 MiB; reject field/part ซ้ำหรือไม่รู้จัก → 201 CaptureReceipt; replay สำเร็จ → 200 receipt เดิม |
| GET `/operations/{idempotencyKey}?action=CheckIn\|CheckOut` | เฉพาะ actor ปัจจุบันใน session ที่ผูกไว้ → 200 CaptureOperationView (`Processing`, `Committed`, `Expired`, `Failed`); ไม่รับ ownerId; ไม่พบ/คนอื่น → 404 |
| GET `/current` | 200 CurrentAttendanceView: OpenDay, TodayCompleted, ServerNowUtc; null OpenDay ไม่เท่ากับ error |
| GET `/mine`, `/team`, `/hr` | filter `from`,`to` วันไทย ช่วงไม่เกิน 31 วัน, pageSize default20/max100, opaque cursor → 200 AttendancePage |
| GET `/days/{dayId}` | 200 AttendanceDayView ตามฐานสิทธิ์ปัจจุบัน; ไม่พบ/ข้ามขอบเขต 404 |

ชนิด history ใน `History/HistoryContracts.cs`: `AttendanceListQuery(DateOnly From, DateOnly To, int PageSize=20, string? Cursor=null)`, `AttendanceAudience { Mine, Team, Hr }`, `AttendancePage(AttendanceDayView[] Items, string? NextCursor)`, `CurrentAttendanceView(AttendanceDayView? OpenDay, bool TodayCompleted, DateTimeOffset ServerNowUtc)`; `AttendanceDayView` มี Id/WorkDate/Version/EmployeeId/In/Out/ReadBasis โดย In/Out เป็น event view มี Id/OccurredAtUtc/Site/Gps/Photo?; Photo มี EvidenceId เท่านั้นและ **omit field** เมื่อไม่มีสิทธิ์ ไม่ส่ง locator/path/public URL ส่วนผล effective time แสดงแยกจาก original event เพื่อพร้อมต่อ 6D

401 session ไม่ผ่าน, 403 MFA/CSRF/capability, 404 resource นอก scope, 409 duplicate/open round/version/idempotency mismatch หรือ operation กำลังทำ (Retry-After 2), 400 validation/challenge หมดอายุ, 413 ขนาด, 415 ชนิด, 429 budget พร้อม Retry-After, 503 dependency ห้ามแปลง 503 เป็น logout ทุก response ส่วนตัวใช้ no-store

GPS ใช้เวลาที่ browser รับ sample เทียบเวลาขณะ submit: client ตรวจอายุด้วย monotonic clock; server ตรวจ `0 <= occurredAtUtc - sampledAtUtc <= 60s` และไม่รับ sample ในอนาคต นาฬิกาเครื่องคลาดเคลื่อนให้แจ้งเปิดเวลาตั้งอัตโนมัติ ไม่แก้ event time ตาม client ค่า accuracy มากกว่า100เมตรเตือนแต่ยังส่งได้ ขีดเตือนนี้เป็น UX ไม่ใช่ข้อจำกัดธุรกิจ

## ลำดับงาน — 9 Task

ทุก Task: บันทึก RED ที่ล้มเหลวเพราะพฤติกรรมยังไม่มี → เขียนขั้นต่ำให้ GREEN → review diff/format/ทดสอบเกี่ยวข้อง → commit เฉพาะไฟล์ Task ไม่ใช้ `git add .` งานภายหลังพึ่งสัญญาข้างบนและ Task ก่อนหน้า

### Task 1: Schema วันทำงาน เหตุการณ์ และ lock ร่วม

**ไฟล์สร้าง:** `backend/src/TPR10.Api/Attendance/Recording/RecordingEntities.cs`, `RecordingContracts.cs`, `RecordingModelConfiguration.cs`, `AttendanceEmployeeLock.cs` ในโฟลเดอร์เดียวกัน; `backend/tests/TPR10.Api.IntegrationTests/AttendanceRecordingSchemaTests.cs` และ `AttendanceRecordingFixture.cs`

**ไฟล์แก้:** `backend/src/TPR10.Api/Data/Tpr10DbContext.cs`, `Attendance/AttendanceRegistration.cs` ใต้ API; เพิ่ม migration `AddAttendanceRecording` และ model snapshot ผ่าน EF

**Interfaces:** ส่ง entity/DTO และ `AttendanceEmployeeLock.AcquireAsync` ตามสัญญาร่วม; fixture `CreateOpenDayAsync(Guid employeeId, DateTimeOffset occurredAtUtc): Task<Guid>` ใช้ข้อมูลสังเคราะห์เท่านั้น

- [x] RED: `DuplicateDayAcrossWorkspacesRejected`, `OnlyOneOpenDayPerEmployee`, `ImmutableEventCannotBeUpdated`, `CheckoutMustFollowCheckin`, `ConcurrentEmployeeLocksSerialize` ตรวจ SQLSTATE constraint จริงจาก PostgreSQL ไม่ใช้ in-memory provider
- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceRecordingSchemaTests` ต้อง fail ที่ assertion/schema ที่ยังไม่มี
- [x] เพิ่ม schema unique/check/FK Site tuple, event/day owner, finite GPS/time, version เพิ่ม และ immutable snapshot; เตรียม operation/challenge tables แต่ยังไม่เปิด endpoints
- [x] GREEN คำสั่งเดิม พร้อม migrate ฐานว่าง/ฐาน6Bและตรวจ down ปฏิเสธเมื่อมีข้อมูล ปิด dependency ทางวงก่อน commit
- [x] `git diff --check` แล้ว stage เฉพาะไฟล์ข้างบนและ commit `feat(attendance): add recording schema and employee lock`

### Task 2: ผูกรูปกับ event จริงและใช้สิทธิ์ของวันทำงาน

**ไฟล์สร้าง:** `backend/src/TPR10.Api/Attendance/History/AttendanceEvidenceSubject.cs`; tests `AttendanceEvidenceBindingTests.cs` ใน integration tests

**ไฟล์แก้:** `Attendance/Evidence/EvidenceReader.cs`, `EvidenceModelConfiguration.cs`; `EvidenceReadTests.cs`, `EvidenceSchemaTests.cs`, `EvidencePublicationTests.cs` ใน integration tests; เพิ่ม migration `BindEvidenceToAttendanceEvents`

**Interfaces:** `AttendanceEvidenceSubject.ResolveAsync(Tpr10DbContext db, Guid evidenceId, CancellationToken ct): Task<EmploymentSnapshot?>` อ่าน day snapshot ผ่าน binding; ส่งต่อ policy 6A ใน shared authorizer เดิม ไม่เปลี่ยน stamp/publication DTO ของ6B

- [ ] RED: `CheckoutAfterTransferUsesCheckinDepartmentForRead`, `SupervisorCannotReadEitherPhoto`, `MissingEventFailsClosed`, `BindingOwnerActionTimeMismatchRejected`, `RevokedGrantBeforeDeliveryDenied`; assert HR หน่วยเดิมเห็นทั้งคู่ HR หน่วยใหม่ไม่เห็นแม้ checkout เกิดหลังย้าย
- [ ] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceEvidenceBindingTests|FullyQualifiedName~EvidenceReadTests|FullyQualifiedName~EvidenceSchemaTests|FullyQualifiedName~EvidencePublicationTests'` เก็บผล RED
- [ ] เพิ่ม FK/deferred guards/preflight และ resolver; ปรับ fixture 6B ให้เพิ่ม live event/day ใน transaction เดียวกับ publication ไม่ผ่อน constraints เพื่อให้ tests ผ่าน
- [ ] GREEN คำสั่งเดิม พร้อม full/thumbnail/download และ revocation ที่ `AuthorizedImageResult` ใช้ authorizer เดียวกัน
- [ ] ตรวจ diff แล้ว commit `feat(attendance): authorize evidence through attendance day`

### Task 3: Challenge, validation และ operation status

**ไฟล์สร้าง:** ใน `backend/src/TPR10.Api/Attendance/Recording/`: `CaptureChallengeService.cs`, `CaptureValidation.cs`, `CaptureOperationStore.cs`; tests `CaptureChallengeTests.cs`, `CaptureOperationTests.cs`

**Interfaces:** `CaptureChallengeService.IssueAsync(CaptureChallengeRequest request, CancellationToken ct): Task<RecordingResult<CaptureChallengeView>>`; `CaptureValidation.Validate(CaptureMetadata metadata, DateTimeOffset now): string?`; `CaptureOperationStore.GetAsync(Guid key, EvidenceAction action, CancellationToken ct): Task<RecordingResult<CaptureOperationView>>`; Store ใช้ entity Task1 สำหรับ claim/fence/digest/result ไม่สร้างผลลงเวลาเอง

- [ ] RED: `ChallengeExpiresAt120Seconds`, `ChallengeBindsSessionActionAndExactSite`, `GpsAge60SecondsAcceptedButOlderRejected`, `FutureOrNonfiniteGpsRejected`, `SameKeyDifferentImageConflicts`, `OtherActorCannotReadOperation` ตรวจขอบก่อน/ตรง/หลัง expiry โดย injected TimeProvider
- [ ] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~CaptureChallengeTests|FullyQualifiedName~CaptureOperationTests'` ให้ fail ตรงกรณีใหม่
- [ ] Implement ตัวตรวจและสถานะตามตาราง HTTP; key เป็น UUID, canonical digest ใช้ normalized typed JSON+raw image SHA256 ไม่ hash multipart boundary; retry keyเดิมห้ามเปลี่ยน metadata/image/เวลาเดิม

ตัวอย่าง assertion ของขอบเวลา GPS (ให้ `metadata` เป็น CaptureMetadata ที่ส่วนอื่นถูกต้อง และ `now` เป็นเวลาทดสอบคงที่):

```csharp
Assert.Null(CaptureValidation.Validate(metadata with {
    Gps = metadata.Gps with { SampledAtUtc = now.AddSeconds(-60) }
}, now));
Assert.NotNull(CaptureValidation.Validate(metadata with {
    Gps = metadata.Gps with { SampledAtUtc = now.AddSeconds(-60).AddTicks(-10) }
}, now));
```

- [ ] GREEN พร้อมเพิกถอน assignment/session ขณะรอ lock และ finite/bounds GPS; ไม่มี generic challenge ที่ client เลือก actor ได้
- [ ] ตรวจ diff แล้ว commit `feat(attendance): add capture challenges and operation tracking`

### Task 4: บันทึกเข้า–ออกแบบ atomic และ idempotent

**ไฟล์สร้าง:** ใน Recording: `AttendanceRecorder.cs`, `AttendancePairing.cs`; tests `AttendanceRecordingTests.cs`, `AttendanceRecordingFailureTests.cs`

**Interfaces:** `AttendanceRecorder.RecordAsync(EvidenceAction action, CaptureMetadata metadata, Stream photo, CancellationToken ct): Task<RecordingResult<CaptureReceipt>>` ประสาน Task1–3 กับ `StorageRegistry.PinAsync`, `EvidenceWriter.PrepareAsync`, `StagePublicationAsync`; `AttendancePairing.WorkDate(DateTimeOffset instant): DateOnly` ใช้ Asia/Bangkok

- [ ] RED: `ParallelCheckinsProduceOneDay`, `ParallelCheckoutsProduceOneEvent`, `RetryAfterLostResponseReturnsSameReceipt`, `MidnightDuringImagePreparationKeepsOriginalInstant`, `OvernightNeedsContinuousConfirmation`, `CrossWorkspaceCheckoutRequiresExactAssignment`, `AuditFailurePublishesNothing`, `ExpiredLeaseCannotCommit`
- [ ] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceRecordingTests|FullyQualifiedName~AttendanceRecordingFailureTests'`; assert event/binding/audit/result พร้อมกัน หรือไม่เกิดทั้งหมด; preparedไฟล์อาจอยู่แต่ห้ามอ่านได้
- [ ] Implement protocol ที่กำหนด ตรวจ current actor/session/capability/exact Site ณ commit ซ้ำ; checkout ใช้ open day+ExpectedDayVersion; checkin ไม่รับ expected version ของวันอื่น; ไม่มี auto-new-key หรือ backdate
- [ ] GREEN เพิ่ม crash หลัง prepare/ก่อนcommit/หลังcommit, storage outage, membershipเปลี่ยนระหว่างเตรียมภาพ, same-dayปิดแล้วเปิดใหม่409, ข้ามวันปิดแล้วเปิดวันปัจจุบันได้; เทียบ stampเวลาในไฟล์จริงกับ receipt ไม่เรียก clock ใหม่
- [ ] ตรวจ diff แล้ว commit `feat(attendance): commit online check-in and check-out atomically`

### Task 5: Current และประวัติที่กรองสิทธิ์ก่อนแบ่งหน้า

**ไฟล์สร้าง:** `backend/src/TPR10.Api/Attendance/History/HistoryContracts.cs`, `AttendanceHistory.cs`; `backend/src/TPR10.Api/Attendance/Access/AttendanceReadScope.cs`; tests `AttendanceHistoryTests.cs`

**ไฟล์แก้:** `Attendance/Access/AttendanceAccess.cs` ให้ shared read predicate ไม่เกิด policyซ้ำ

**Interfaces:** `AttendanceReadScope.VisibleDaysAsync(AttendanceAudience audience, CancellationToken ct): Task<IQueryable<AttendanceDay>>` สร้าง query ภายใน caller identity transaction; `AttendanceHistory.CurrentAsync(ct): Task<RecordingResult<CurrentAttendanceView>>`, `ListAsync(AttendanceAudience audience, AttendanceListQuery query, ct): Task<RecordingResult<AttendancePage>>`, `GetAsync(Guid dayId, ct): Task<RecordingResult<AttendanceDayView>>` โดย ct เป็น CancellationToken ทุกเมธอด

- [ ] RED: `ScopeAppliedBeforePagination`, `NewSupervisorCannotReadPreviousMembershipHistory`, `TeamJsonOmitsPhotoField`, `HrGrantRemovalHidesOldDays`, `AdminWithoutGrantGetsNoBusinessData`, `ForgedCursorCannotChangeScope`
- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AttendanceHistoryTests` ให้ fail; ใส่ unauthorized rows ก่อน authorized rows เพื่อจับการ filter หลัง pagination
- [ ] Implement keysetเรียง WorkDate/Id ลดหลั่น cursor bind audience/filter/session, ไม่ส่ง total ข้ามscope; reuse6A own→HR→supervisor precedence; recentMFAตามฐานสิทธิ์; revalidateก่อนserializeใน transactionสั้นมี timeout ไม่มีfilesystem I/O
- [ ] GREEN เปรียบเทียบ querypolicyกับ `IAttendanceAccess.ReadAsync` ทุกฐานสิทธิ์, date-range31วัน, pageSize100, ไม่เผยแถว/ชื่อ/รูปผ่าน countsหรือerrors
- [ ] ตรวจ diff แล้ว commit `feat(attendance): add scoped current and history queries`

### Task 6: HTTP multipart, CSRF, OpenAPI และ client transport

**ไฟล์สร้าง:** `backend/src/TPR10.Api/Attendance/Recording/RecordingEndpoints.cs`, `Attendance/History/HistoryEndpoints.cs`; `lib/attendance/recording-client.ts`, `lib/attendance/recording-types.ts`; tests `AttendanceRecordingApiTests.cs`, `AttendanceRecordingOpenApiTests.cs` ใน integration tests และ `tests/attendance-recording-client.test.mjs`

**ไฟล์แก้:** `AttendanceRegistration.cs`, `AttendanceOpenApiTransformer.cs`, `Program.cs` ใต้ API; `infra/nginx/identity-local-https.conf.template`

**Interfaces:** exact routes/DTO ตามตาราง; TS `issueCaptureChallenge(input: CaptureChallengeRequest, signal: AbortSignal): Promise<Response>`, `submitCapture(action: 'CheckIn'|'CheckOut', metadata: CaptureMetadata, photo: Blob, signal: AbortSignal): Promise<Response>`, `getCaptureOperation(key: string, action: 'CheckIn'|'CheckOut', signal: AbortSignal): Promise<Response>`; typesสะท้อน JSONจากbackend

- [ ] RED: multipart ขาด/ซ้ำpart, forgedCSRF, fakeJPEG, 10MiB+1,20ล้านpixels+1, exactrouteเท่านั้น, 429RetryAfter,503ไม่logout; OpenAPIระบุทุกresponseและsecurity
- [ ] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~AttendanceRecordingApiTests|FullyQualifiedName~AttendanceRecordingOpenApiTests'` และ `node --test tests/attendance-recording-client.test.mjs` เก็บ RED
- [ ] Implement streaming multipart ที่ bounded ไม่รับURL/base64; limit request11MiBเฉพาะสองuploadroutesทั้งKestrel/nginx, metadata16KiB; routeอื่นคงlimitเดิม; browserให้สร้าง boundaryเอง ไม่ตั้ง Content-Type แบบJSON
- [ ] CSRFใช้flow same-originเดิม, redirect:error, credentials:same-origin, no-store; CSRF timeout10s, upload timeout75s; timeoutแล้วคืนสถานะไม่แน่นอนให้UIตรวจoperation ไม่ส่งใหม่อัตโนมัติ; ไม่ขยาย `authMutation` allowlist เป็น arbitrarypath
- [ ] GREEN คำสั่งเดิม รวม HTTPSproxy body-limit/same-origin และข้อความไทย แล้ว commit `feat(attendance): expose secure recording and history APIs`

### Task 7: หน้าลงเวลากล้องสดและ GPS

**ไฟล์สร้าง:** `app/portal/attendance/page.tsx`, `components/attendance/AttendanceCapture.tsx`, `components/attendance/useLiveCapture.ts`; `tests/e2e/attendance-capture.spec.ts`

**ไฟล์แก้:** `app/portal/page.tsx` เพิ่มทางเข้าตามสิทธิ์ และ `infra/nginx/https-smoke-fixture.mjs` เพิ่มเฉพาะspecใหม่ในallowlist

**Interfaces:** `useLiveCapture(): { phase, error, previewUrl, gps, start, capture, reset, stop }`; startรับaction/siteและขอchallengeก่อนcapture, captureสร้างBlobจากvideo stream; UIเรียก clientTask6 และ currentTask5 ไม่เก็บsecret/payloadถาวร

- [ ] RED browser: permission denied, GPStimeout/stale, challengeexpiry,ไม่มีfileinput/drop, offlineไม่queue,กดซ้ำ/timeoutต้องตรวจoperation,ออกข้ามวันยืนยันต่อเนื่อง,ลืมออกแจ้งรอ6D
- [ ] รันผ่าน `runHttpsSmoke` ตามคำสั่งด้านล่างเฉพาะcapture บน port4000 ให้ fail ที่พฤติกรรมใหม่
- [ ] Implementสถานะ idle→permissions→ready→captured→submitting→success/uncertain/error; ปิดปุ่มขณะส่ง; sampleGPSใหม่ก่อนยืนยัน; warning accuracy>100m ไม่block; แสดงเวลาserverไม่เรียกภาพpreviewว่าประทับแล้ว
- [ ] เมื่อ visibility hidden/navigation/logout/authchange: abortrequest, stopทุกMediaStreamTrack, revokeObjectURL, ล้างรูป/GPS; กลับแท็บโหลดsession/currentใหม่และถ่ายใหม่ ไม่แสดงlate responseข้ามauth epoch
- [ ] GREEN keyboard/label/live error/portrait-landscape และ no browserstorage; commit `feat(attendance): add live camera and GPS capture portal`

### Task 8: หน้าประวัติส่วนตัว ลูกทีม และ HR

**ไฟล์สร้าง:** `app/portal/attendance/team/page.tsx`, `app/portal/attendance/hr/page.tsx`, `components/attendance/AttendanceHistory.tsx`, `components/attendance/useAttendanceHistory.ts`; `tests/e2e/attendance-history.spec.ts`

**ไฟล์แก้:** หน้าลงเวลาTask7 เพิ่มประวัติตนเอง และ smoke allowlist

**Interfaces:** `AttendanceHistory({ audience }: { audience: 'Mine'|'Team'|'Hr' })`; hook `useAttendanceHistory(audience, query)` คืน `{data,error,loading,reload}` ใช้ DTOTask5/6; ภาพเรียกevidenceendpoint6BเดิมหลังAPIให้EvidenceIdเท่านั้น

- [ ] RED: ownเห็นรูป/GPS, supervisorไม่โหลด/ไม่มีphotofieldหรือDOM, HRเห็นเฉพาะหน่วย, adminไม่มีสิทธิ์, ถอนHRแล้วกลับแท็บไม่ค้างภาพ,เปลี่ยนบัญชีระหว่างfetchไม่แสดงข้อมูลเก่า
- [ ] รัน HTTPSfixture history บนport4000 ให้ fail ก่อนimplementation
- [ ] Implementวันที่ไทย/คู่ข้ามวัน/แยกSiteเข้าออก/ฐานสิทธิ์/pagination/error; ไม่มีexportหรือแก้เวลา; ใช้ `subscribeAuthChanges` และfocusrevalidation, abort+epoch+clearDOM ตามpattern6A/6B
- [ ] GREEN mobile/keyboard และ directURL evidence ยังผ่านbackendgate; commit `feat(attendance): add private attendance history views`

### Task 9: ตรวจทั้งสาขาและเอกสารรับงาน

**ไฟล์สร้าง:** `docs/architecture/module-6c-exit-gate.md`, `docs/architecture/module-6c-device-acceptance.md`

**ไฟล์แก้:** แผนนี้และdelivery-map; แก้source/testsเฉพาะfindingที่มีREDพิสูจน์ก่อนแก้

**Interfaces:** ส่ง event/day/version/employee-lock contract ให้6D ไม่เพิ่มcorrectionendpointใน6C

- [ ] ให้ผู้ตรวจอิสระตรวจ `8a965af..HEAD` เทียบspec/planทั้งสาขาตามNative; findingsสำคัญต้องมีregression RED→GREEN ไม่เรียกself-reviewว่าindependent review
- [ ] รัน Test/Build/Lint/format/OpenAPIและHTTPS dev/prod ครบตามคำสั่งด้านล่าง เก็บ exitcode/จำนวนtests/SHA/เวลา ไม่ใช้ผล6Bเป็นผล6C
- [ ] ทดสอบมือถือจริง Safari/iOS และ Chrome/Android บนHTTPSที่เชื่อถือได้: allow/denycameraGPS,กล้องหน้า/หลัง,หมุนจอ,background/resume,networkdrop,ภาพดาวน์โหลดอ่านเวลาไทยได้ตรงevent ถ้ายังไม่มีอุปกรณ์ให้ระบุยังรอตรวจ ไม่อ้างmockแทน
- [ ] ตรวจ localstorageจริง/ย้ายwrite-targetกับรายการเข้าออกเก่า,backup/restoreDB+รูปในสภาพแวดล้อมทดสอบ; NASจริงยังเป็นgateแยกหากไม่มีshare ไม่ใช้โฟลเดอร์จำลองแทน
- [ ] จัดทำรายงานภาษาไทยแยก Technical / Mobile-device / NAS / Production,ข้อจำกัด6D,ภาพ/GPSdataownerและretentionpolicy; ไม่มีcredentialsหรือรูปจริงในGit
- [ ] commit `docs(attendance): record module 6c verification and handoff` เมื่อหลักฐานตรงโค้ดจริง; commit/push/PR/mergeตามอำนาจที่ผู้ใช้อนุมัติในรอบนั้น ไม่mergeเอง

## คำสั่งตรวจรับ

รันจากworktreeที่แยกและใช้runtimeที่ติดตั้งแล้ว Dockerต้องพร้อมสำหรับPostgreSQLtests ไม่หยุดPreviewเดิมเพื่อแย่งport หากportไม่ว่างต้องประสานก่อนรันfixture

```sh
npm test
npm run lint
npm run build
dotnet test backend/TPR10.sln
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
git diff --check
```

หลังเพิ่มspecในallowlist ใช้คำสั่งนี้สำหรับ capture; ทำซ้ำโดยเปลี่ยนspecเป็น `attendance-history.spec.ts` และ portเป็น4001 รวม4ชุด แล้วรันชุดidentity/scopes/directory/storageเดิมตามrunbook6Bด้วย:

```sh
node --input-type=module -e "import { runHttpsSmoke } from './infra/nginx/https-smoke-fixture.mjs'; await runHttpsSmoke({port:'4000',spec:'tests/e2e/attendance-capture.spec.ts',e2e:true});"
```

เกณฑ์ผ่าน: exit0, ไม่มีfailed/skippedที่ซ่อนacceptance, reviewสำคัญปิดแล้ว; NAS/mobile/Productionที่ยังไม่ได้ตรวจต้องแสดงแยก ไม่ใช้คำว่า Module6 ทั้งหมดเสร็จ

## ตรวจความครบถ้วนของแผนและการอนุมัติ

R01/02/04 อยู่Task3/4/6/7/9; R05–07อยู่Task1/4; R14/15อยู่Task2/5/8; R03/13รักษาpipeline6BและgateTask9; R08–12ไม่implementจน6D ไม่มีทางแก้ตรงเป็นข้อยกเว้น

ตรวจสัญญาreader/writerจริงแล้ว: Pinยังใช้event-time snapshot, readerจะใช้day snapshotในTask2, StagePublicationไม่commitเอง; migration/fixturesต้องทำร่วมกับFKใหม่ ข้อกำหนดทางเทคนิคในแผนได้รับอนุมัติแล้ว แต่ไม่ใช่ผลimplementationหรือผลtestsของทุกTask

ลำดับ execution: Task1 ด้วยNative/TDD แล้วทำTaskถัดไปตามคำสั่งผู้ใช้; ตรวจอิสระทั้งสาขาในTask9 ไม่ใช้การผ่านTask1แทนการส่งมอบทั้ง6C

การตรวจรอบจัดทำแผน 26 กันยายน 2026: `npm test` ผ่าน43/43, `npm run lint` และ `npm run build` ผ่านบนฐาน8a965af โดยเปลี่ยนเฉพาะเอกสาร ยังไม่ได้รันbackend suiteซ้ำหรือทดสอบฟีเจอร์6C ผลนี้ไม่ใช่การผ่านexit gateของ6C

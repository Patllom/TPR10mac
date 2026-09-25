# Module 6B — Implementation Plan: หลักฐานภาพและที่เก็บแบบย้ายได้

> **สำหรับผู้ดำเนินงาน:** ใช้ `superpowers:executing-plans` ตามวิธี Native ที่เลือกไว้ หรือ `superpowers:subagent-driven-development` หากผู้ใช้เปลี่ยนวิธี ทำทีละ Task และติดตามขั้นตอนด้วย `- [ ]` ห้ามเริ่ม implementation จนผู้ใช้อนุมัติเอกสารนี้

**Goal:** สร้างหลักฐานภาพประทับเวลา อ่านตามสิทธิ์ และเปลี่ยน/ย้ายที่เก็บ local-folder หรือ NAS โดยรหัสรูปและเนื้อหาหลักฐานเดิมไม่เปลี่ยน

**Architecture:** เพิ่มขอบเขต Evidence และ Storage ภายใน Attendance modular monolith เดิม PostgreSQL เก็บ metadata/สถานะ/งานย้าย ส่วนไฟล์ immutable อยู่นอก webroot ใช้ protocol prepare–finalize–publish และตรวจสิทธิ์จาก 6A; ไม่อ้างว่า filesystem กับ database เป็น transaction เดียวกัน

**Tech Stack:** ASP.NET Core/.NET 10, EF Core/PostgreSQL, Next.js/TypeScript, SkiaSharp/HarfBuzz และ Noto Sans Thai; รุ่นและที่มาอยู่หัวข้อ dependency

**Spec:** [แบบ Module 6 ที่อนุมัติแล้ว](../specs/2026-09-25-module-6-attendance-design.md) โดยเฉพาะ R02/R03/R13/R14/R15 และข้อ8–10,13–15; [แผนส่งมอบรวม](2026-09-25-module-6-delivery-map.md)

สถานะ: **รอผู้ใช้ตรวจอนุมัติแผน — ยังไม่เขียนโค้ด 6B** วันที่25กันยายน2026

ฐาน: PR #2 รวมแล้วบน GitHub เมื่อ2026-09-25T02:39:21Z; merge commit `27a184e44a78ab656fd6a618614c4eb13fe10c53` มีต้นไม้ไฟล์ตรงกับ6A `34db688` สาขาแผน `codex/module-6b-evidence-storage` สร้างจากฐานนี้ ไม่แก้ไฟล์ค้างใน checkout main เดิม

## Global Constraints — ข้อบังคับทุก Task

- “วัน–เวลาต้องประทับอยู่ในไฟล์ภาพจริง ไม่ใช่เฉพาะข้อความบนหน้าเว็บ” ใช้ instant ที่6Cตรึงให้ ไม่อ่านนาฬิกาใหม่ตอนประทับ
- “รองรับโฟลเดอร์บนเซิร์ฟเวอร์และ NAS เปลี่ยนที่เก็บได้โดยไม่กระทบรูปเก่า” แต่ต้นทางต้องยังอยู่จนย้ายและตรวจครบ ไม่รับประกันเมื่อผู้ดูแลถอดหรือทำข้อมูลต้นทางสูญหาย
- “ระยะแรกยังไม่ลบรูปลงเวลาอัตโนมัติ” รวมต้นฉบับที่ย้ายแล้ว ไม่มี endpoint ลบรูป/retire storage ใน6B; staging/orphan แสดงรายงานเท่านั้น ไม่เปิด cleanup อัตโนมัติ
- “Admin ไม่ได้สิทธิ์ข้อมูลลงเวลาอัตโนมัติ” storage-manage ไม่ให้ดูภาพ; เจ้าของดูของตน หัวหน้าห้ามดูรูปลูกทีม HRต้องมี current grant ของหน่วยงาน snapshot
- อินพุต JPEG/PNG เท่านั้น สูงสุด10 MiB และ20,000,000พิกเซล ตรวจเนื้อหาจริง ไม่เชื่อ MIME/ชื่อไฟล์/EXIF; output JPEG มี checksum SHA-256
- UTC ในฐานข้อมูล; วันเวลาบนภาพ `Asia/Bangkok` แสดงปีค.ศ.และ `(UTC+7)` พร้อม `เข้า` หรือ `ออก` ไม่รับข้อความประทับจาก browser
- ไม่มี public/static image URL ไม่มี signed URL อายุยาว ไม่มีข้อมูลภาพ/พิกัดใน localStorage หรือ service-worker cache; no-store ทุกเส้นทางอ่าน
- ไม่ทำกล้อง/GPS UI, challenge, clock-in/out, attendance pairing, correction, email, offline, export หรือ generic file manager ใน6B; ส่วนเหล่านี้เป็น6C/6D
- ไม่มี SMB credentials/path จาก browser เลือกได้เฉพาะ alias ที่ Operations จัดเตรียม; ไม่ mount NAS จริงโดยไม่ได้รับอนุญาต
- คง MFA/CSRF/session/permission เดิม, Dev4000/production-build4001/HTTPS4443, ไม่หยุด Preview เดิมโดยไม่ขออนุญาต
- ไม่ถือผล mock เป็นหลักฐาน NAS จริง และไม่อ้าง6Bเสร็จทั้งหมดหาก NAS drill ที่ต้องมีหลักฐานยังไม่ได้รัน

## Review Focus — ห้าความเสี่ยงที่ต้องมี test

1. NAS ถูก unmount แต่โฟลเดอร์ mount point ยังอยู่: ต้องปฏิเสธเขียน ไม่ตกไป local diskเงียบ ๆ — Task3/4/8
2. เปลี่ยนสิทธิ์หลังอ่านไฟล์แต่ก่อนส่ง bytes: ตรวจใหม่และไม่ส่งข้อมูลถ้า revocation ชนะ — Task5/7
3. copy สำเร็จแต่ audit/DB fail หรือ processตาย: IDเดิมไม่เปลี่ยน สำเนายังไม่ถูกเปิดใช้และ resumeไม่เพิ่มซ้ำ — Task5/6
4. รูปมี orientation/metadata/หลายเฟรม หรือ dimensions overflow: ไม่หลุดเพดาน ไม่เสียอักษรไทย/วันเวลา — Task2
5. เปลี่ยน write target ขณะมี upload/งานย้ายค้าง: reservationเดิม pinรุ่นเดิม ย้ายต้องเก็บ late arrivalsและไม่ประกาศครบก่อนตรวจ — Task4/6

## การตัดสินใจทางเทคนิคที่เสนอในแผนนี้

รายการต่อไปนี้เป็นค่าระดับ implementation ที่ผู้ใช้อนุมัติผ่านแผนฉบับนี้ ไม่ใช่ข้อกำหนดเดิมที่เพิ่มโดยเงียบ:

- backend filesystem runtime รองรับ Linux server/container และ macOS development ในระยะนี้ NASต้องmountไว้ก่อน Windows serverต้องแยกตรวจ handle/reparse-point และnative librariesก่อนอ้างรองรับ
- JPEG quality90, จำกัดด้านยาวภาพ2048pxโดยรักษาสัดส่วนและไม่ขยายภาพเล็ก เพิ่มแถบดำด้านล่างสูง112px ตัวอักษรขาว ขนาด32px ห้ามทับเนื้อภาพเดิม; ถ้าภาพแคบให้ขยายเฉพาะ canvasเป็นอย่างน้อย800px พื้นขาว
- final JPEGไม่เกิน10 MiB; thumbnailด้านยาว640pxสร้างจากไฟล์ประทับแล้ว ไม่อ่าน uploadต้นทาง และใช้สิทธิ์เดียวกับภาพเต็ม
- งานต่อstorage timeout10วินาที, งานแปลงภาพ deadline10วินาที/queueสูงสุด8/concurrency2ต่อprocess; queueเต็ม429 พร้อม Retry-After:5, timeout503 ไม่ปล่อยงานnativeที่ยังรันให้หลุดเพดานconcurrency
- retryงานย้ายสูงสุด5ครั้งต่อitem หน่วง1/5/30/120/300วินาที จากนั้นสถานะBlocked ต้องกดresumeอย่างชัดเจน ไม่retrychecksum mismatchอัตโนมัติ
- health scanทุก60วินาที พื้นที่ว่างต่ำกว่า10GiBหรือ10%ให้ warning; ไม่มีข้อมูลcapacityให้unknownไม่ใช่healthy; capacityต่ำไม่เปิด silent fallback
- readiness probeมีอายุ60วินาทีและผูกstorage version/mount identity; operationจริงยังต้องตรวจmountทุกครั้ง

## Dependency และฟอนต์ที่ล็อกสำหรับ Task2

ตรวจแหล่งทางการ25กันยายน2026 เลือกรุ่นstableที่ระบุ ไม่อ้างว่าเป็นรุ่นล่าสุดหรือไม่มีช่องโหว่ตลอดไป:

| รายการ | รุ่น/ใบอนุญาต | แหล่งหลักฐาน |
| --- | --- | --- |
| SkiaSharp และ SkiaSharp.NativeAssets.Linux.NoDependencies | 3.119.4 / MIT และ third-party noticesของnative | [แพ็กเกจ](https://www.nuget.org/packages/SkiaSharp/3.119.4), [Linux](https://www.nuget.org/packages/SkiaSharp.NativeAssets.Linux.NoDependencies/3.119.4), [license](https://github.com/mono/SkiaSharp/blob/main/LICENSE.txt) |
| SkiaSharp.HarfBuzz | 3.119.4 / MIT | [แพ็กเกจและdependency](https://packages.nuget.org/packages/SkiaSharp.HarfBuzz/3.119.4) |
| HarfBuzzSharp และ HarfBuzzSharp.NativeAssets.Linux | 8.3.1.5 / MIT และnative notices | [Linux package](https://www.nuget.org/packages/HarfBuzzSharp.NativeAssets.Linux/8.3.1.5) |
| Noto Sans Thai | v2.002 / SIL OFL1.1 | [metadata](https://raw.githubusercontent.com/google/fonts/main/ofl/notosansthai/METADATA.pb), [release](https://github.com/notofonts/thai/releases/tag/NotoSansThai-v2.002), [OFL](https://raw.githubusercontent.com/google/fonts/main/ofl/notosansthai/OFL.txt) |

Task2ดาวน์โหลดจากreleaseที่ระบุและเก็บfont/licenseในrepo พร้อมบันทึกSHA-256ของbytesจริงในmanifest ห้ามใช้floating mainเป็นruntimefont; source commitที่metadataอ้างคือ `f8f3f024703f9d939d02f4e2fe16f1d5a39ca963` เปิดrestore lockและตรวจpackage auditก่อนเพิ่ม หากรุ่นติดช่องโหว่ให้เสนอรุ่นแก้แทน ไม่เดินต่อด้วยคำว่าapprovedplan

ไม่ใช้ImageSharpในแผนนี้เพื่อไม่เพิ่มการตัดสินใจด้านcommercial license และไม่ใช้System.Drawingเป็นserver backend; nativecodecยังต้องมีresource limitsและตรวจLinux runtimeจริง

## ผังไฟล์และสัญญาหลัก

โค้ดใหม่อยู่ใต้ `backend/src/TPR10.Api/Attendance/Evidence/` และ `Attendance/Storage/` แยกentities, models, adapter, pipeline, reader, migration worker คนละไฟล์ ไม่เพิ่มbusiness logicในProgram.cs

ไฟล์เดิมที่เชื่อม: `Data/Tpr10DbContext.cs`, `Attendance/AttendanceRegistration.cs`, `Attendance/AttendanceOpenApiTransformer.cs`, `Program.cs`, `lib/auth/auth-client.ts`, `app/portal/page.tsx` และintegration test OpenAPI allowlistsเดิม

ใช้สัญญาจริง6A ไม่เปลี่ยนความหมาย:

```csharp
// มีอยู่แล้วใน Attendance/Access ไม่ประกาศซ้ำ
// EmploymentSnapshot(Guid MembershipId, Guid EmployeeId, Guid WorkspaceId,
//                    Guid DepartmentId, DateTimeOffset OccurredAtUtc)
// IAttendanceAccess.ReadAsync(EmploymentSnapshot subject, CancellationToken ct)
// คืน AttendanceReadDecision: CanRead, CanReadGps, CanReadPhoto, Basis, Status
```

ประเภทใหม่กำหนดใน `Evidence/EvidenceContracts.cs` และ `Storage/StorageContracts.cs`:

```csharp
public enum EvidenceAction { CheckIn, CheckOut }
public enum EvidenceState { Reserved, Prepared, Published, Orphan }
public enum CopyState { Pending, Verified, Active, Fallback, Quarantined }
public sealed record StampRequest(DateTimeOffset OccurredAtUtc, EvidenceAction Action);
public sealed record StampedImage(byte[] Jpeg, string Sha256, int Width, int Height,
    string StampText);
public sealed record EvidenceReservation(Guid EvidenceId, Guid OperationId,
    Guid StorageId, long StorageVersion, string ObjectKey);
public sealed record PreparedEvidence(Guid EvidenceId, Guid OperationId,
    Guid LocationId, string Sha256, long Length, int Width, int Height);
public sealed record EvidencePublication(Guid EvidenceId, Guid OperationId,
    Guid EventId, EmploymentSnapshot Subject, StampRequest Stamp);
public sealed record StoredCopy(string Sha256, long Length);
public sealed record StorageDefinition(string Alias, string Kind,
    string RootPath, string ExpectedVolumeId, Guid MarkerId);
public sealed record StorageView(Guid Id, string Alias, string Kind,
    long Version, bool AcceptWrites, string Health, DateTimeOffset? CheckedAtUtc);
public sealed record StorageTargetView(Guid? StorageId, long Version);
public sealed record RegisterStorage(string Alias, string Reason);
public sealed record StorageOptionView(string Alias, string Kind);
public sealed record ProbeStorage(long ExpectedVersion, string Reason);
public sealed record SwitchWriteTarget(Guid StorageId, long ExpectedVersion, string Reason);
public sealed record StartMigration(Guid RequestId, Guid SourceId, Guid TargetId,
    long ExpectedSourceVersion, long ExpectedTargetVersion, string Reason);
public sealed record ResumeMigration(long ExpectedVersion, string Reason);
public sealed record MigrationView(Guid Id, string Status, long Version,
    int Total, int Verified, int Blocked);
public sealed record StorageHealthView(Guid StorageId, string Status, long? FreeBytes,
    long? TotalBytes, int MissingObjects, int OrphanObjects, DateTimeOffset CheckedAtUtc);
public sealed record Page<T>(T[] Items, int Offset, bool HasMore);
```

StorageDefinitionเป็นdeployment-only ไม่ใช่HTTP DTO; rootจริงและvolume IDไม่ออกresponse/log/audit Payloadต้องreject unknown properties เช่นrootPath/userIdแทนการignore

## API ที่ล็อกสำหรับ 6B

ทุกstorage endpointใช้ `attendance:storage-manage` ในsystem domainจาก6Aและrecent MFA ไม่เพิ่มautomatic grant; ทุกPOSTผ่านCSRFเดิมและaudit ให้ตรวจauthorizationก่อนvalidationรายละเอียดresource

| Method/route ภายใต้ `/api/v1/attendance` | Request | Success |
| --- | --- | --- |
| GET `/storage/options` | offset≥0, limit1–100(default25) | 200 Page<StorageOptionView> ไม่มีroot |
| GET `/storage/locations` | offset/limit | 200 Page<StorageView> |
| POST `/storage/locations` | RegisterStorage | 201 StorageView ไม่มีLocationheaderที่ชี้GETdetailที่ไม่มี |
| POST `/storage/locations/{id}/probe` | ProbeStorage | 200 StorageHealthView |
| GET `/storage/write-target` | ไม่มี | 200 StorageTargetView |
| POST `/storage/write-target` | SwitchWriteTarget | 200 StorageTargetView |
| GET `/storage/health` | offset/limit | 200 Page<StorageHealthView> |
| POST `/storage/migrations` | StartMigration | 202 MigrationView |
| GET `/storage/migrations` | offset/limit | 200 Page<MigrationView> |
| GET `/storage/migrations/{id}` | ไม่มี | 200 MigrationView |
| POST `/storage/migrations/{id}/resume` | ResumeMigration | 202 MigrationView |
| GET `/evidence/{id}` | ไม่มี | 200 image/jpeg inline |
| GET `/evidence/{id}/thumbnail` | ไม่มี | 200 image/jpeg inline |
| GET `/evidence/{id}/download` | ไม่มี | 200 image/jpeg attachmentชื่อสุ่มจากid |

รวม14operations; ไม่มีpublic upload/publish endpointใน6B HTTP evidence GETต้องผ่านReadAsyncและCanReadPhotoไม่ใช่แค่CanRead หัวหน้ารูปลูกทีม404; no session401/MFAไม่ครบ403ตาม6A; evidenceไม่Published404; storage/data/auditไม่พร้อม503; version/stale readiness/duplicateต่างpayload409; invalidbody400; limiter429 ไม่ส่งexception/pathผ่านProblemDetails

Range/conditional GETไม่เปิด ใช้200เต็มหรือปฏิเสธRange416หลังตรวจสิทธิ์ ไม่คืน304จากcache; HEADไม่ส่งmetadataก่อนauth; content-type/nosniff/no-storeและCSPตามมาตรฐานเดิม

## Task 1: แบบจำลองหลักฐานและฐานข้อมูลที่รักษาประวัติ

**Files:** สร้าง `Evidence/EvidenceContracts.cs`, `Evidence/EvidenceEntities.cs`, `Storage/StorageContracts.cs`, `Storage/StorageEntities.cs`, `Evidence/EvidenceModelConfiguration.cs`; แก้ `Data/Tpr10DbContext.cs`; สร้าง migration `AddAttendanceEvidenceStorage` ด้วยEF; tests `backend/tests/TPR10.Api.IntegrationTests/EvidenceSchemaTests.cs`

**Interfaces:** ผลิตrecordsข้างต้นและentities EvidenceObject, EvidenceLocation, EvidenceBinding, StorageLocation, StorageWriteTarget, MigrationJob, MigrationItem ให้Tasks3–6ใช้ `db.Set<T>()` ทั้งหมด; ไม่มีfilesystem I/OในTaskนี้

- [ ] เขียนREDในPostgresFixture: insertEvidenceObjectพร้อมsnapshot membershipปลอม/crossunitต้องถูกFKปฏิเสธ; สร้างbindingซ้ำevent/evidenceต้องunique violation; ห้ามแก้checksum/owner/stampของPublishedและห้ามDELETE; downgradeฐานที่มีหลักฐานต้องหยุดโดยไม่ถอนtriggerอื่น

```csharp
// ขั้นmigrationใช้SQLให้DBป้องกัน ไม่พึ่งvalidationในUI
// evidence_locations: UNIQUE(evidence_id, storage_id, variant)
// evidence_bindings: UNIQUE(evidence_id), UNIQUE(event_id)
// evidence_objects: UNIQUE(operation_id), sha256 char(64), length > 0
// Published ต้องมีbindingและactive copyที่verified checksumตรงobject
// ใช้deferred constraint trigger ตรวจท้ายtransactionเพื่อinsertร่วมกันได้
model.Entity<EvidenceLocation>().HasIndex(x => new { x.EvidenceId, x.StorageId, x.Variant }).IsUnique();
model.Entity<EvidenceBinding>().HasIndex(x => x.EvidenceId).IsUnique();
model.Entity<EvidenceBinding>().HasIndex(x => x.EventId).IsUnique();
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~EvidenceSchemaTests` ให้เห็นassertion/schemaที่ขาด ไม่ใช้missing SDKเป็นRED
- [ ] เพิ่มentity: EvidenceObjectเก็บid,operation,owner,snapshotทั้ง5field,UTC/action,sha/length/dimensions/state/version; Locationเก็บid,evidence,storage,key,variant(full/thumbnail),sha/length/state/version; Bindingเก็บeventId/evidenceId/publishedUtc immutable โดย6Cต้องเพิ่มFKไปAttendanceEventเมื่อมีตาราง ไม่สร้างeventปลอมในproduction
- [ ] StorageLocationเก็บalias/config fingerprint/version/kind/acceptWrites; targetเป็นsingletonversionเพิ่มต่อเนื่อง; rootและkind immutableเมื่อregister เปลี่ยนconfigใต้aliasเดิมให้failclosed; migrationitemunique(job,evidence,variant) เก็บexpected checksum, source/target IDs, attempt/lease/fencing version/error code ไม่มีrawexception
- [ ] migrationใช้ON DELETE RESTRICT, partialuniqueหนึ่งActiveต่อevidence/variant, enumcheckและlength/dimensioncheck, publicationต้องครบfull+thumbnail ก่อนdowngradeถ้ามีobject/binding/jobให้ปฏิเสธทั้งmigrationtransaction ทดสอบemptydb up/down/up และpre6B grantsยังอยู่
- [ ] รันfocusedอีกครั้งผ่าน แล้ว `dotnet format backend/TPR10.sln --verify-no-changes`; commit `feat: add immutable evidence and storage schema` เฉพาะไฟล์Taskนี้

## Task 2: ตรวจภาพและประทับเวลาไทยลงpixels

**Files:** สร้าง `Evidence/ImageStampService.cs`, `Evidence/StampFormatter.cs`, `Evidence/ImageLimits.cs`, `Evidence/Assets/NotoSansThai.ttf`, `Evidence/Assets/OFL.txt`, `Evidence/Assets/manifest.json`; แก้ `TPR10.Api.csproj`; tests `EvidenceStampTests.cs` และsyntheticfixturesในtestproject

**Interfaces:** `IImageStampService.StampAsync(Stream source, StampRequest stamp, CancellationToken ct) : Task<StampedImage>` และ `Thumbnail(StampedImage full) : StampedImage`; `StampFormatter.Format(StampRequest) : string` ไม่มีTimeProviderในformatter

- [ ] เขียนREDสำหรับข้ามเที่ยงคืนไทย/เครื่องservertimezoneอื่น:

```csharp
[Fact]
public void Stamp_uses_fixed_server_instant_and_Thai_calendar_day()
{
    var stamp = new StampRequest(DateTimeOffset.Parse("2026-09-24T17:00:00Z"), EvidenceAction.CheckIn);
    Assert.Equal("25/09/2026 00:00:00 (UTC+7) — เข้า", StampFormatter.Format(stamp));
}
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~EvidenceStampTests`; เพิ่มtests bytes10MiB±1, pixels20M±1/overflow, invalid/truncated JPEG, SVGชื่อjpg, GIF/APNGหลายเฟรม, alphaPNGพื้นขาว, orientation8แบบ, EXIF/GPS/commentไม่อยู่ในoutput
- [ ] เพิ่มpackagesรุ่นที่ตารางกำหนดและfontrelease พร้อมlicenses/hashmanifest; decodeheaderก่อนallocate ตรวจwidth*heightด้วยchecked longและrejectzero/multiframe; limitจำนวนbytesแม้Streamไม่มีLength อย่าdecodeก่อนตรวจเพดาน

```csharp
public static string Format(StampRequest value)
{
    var thai = TimeZoneInfo.ConvertTime(value.OccurredAtUtc, TimeZoneInfo.FindSystemTimeZoneById("Asia/Bangkok"));
    var action = value.Action switch { EvidenceAction.CheckIn => "เข้า", EvidenceAction.CheckOut => "ออก", _ => throw new ArgumentOutOfRangeException() };
    return thai.ToString("dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " (UTC+7) — " + action;
}
```

- [ ] decode→orientation→resize→newcleanbitmap→blackfooter→shapeThaiผ่านHarfBuzzพร้อมfontที่bundle→JPEG90→decodeoutputตรวจ→SHA256; ไม่copyEXIF/textchunk เพิ่มboundedsemaphoreและbudgetไม่ปล่อยnativejobหนีqueueเมื่อcancel
- [ ] ตรวจpixelsในfooterเทียบblank, outputเวลาเดียวกับStampText; ตรวจภาพจริงอย่างน้อยเข้า/ออก/portrait/landscape/midnightด้วยตาไม่ใช้textmetadataแทนหลักฐาน ตัวthumbnailต้องอ่านวันเวลาได้หากไม่ได้ให้failvisualgateไม่ลดเกณฑ์เงียบ ๆ
- [ ] focusedtestsผ่าน, Linuxcontainerทำdecode/shapeจริง, auditdependenciesรวมtransitive, บันทึกรูปสังเคราะห์เท่านั้นในรายงาน; commit `feat: stamp immutable attendance evidence images`

## Task 3: Adapter โฟลเดอร์และNASที่failclosed

**Files:** สร้าง `Storage/IStorageAdapter.cs`, `Storage/FolderStorageAdapter.cs`, `Storage/StorageRootResolver.cs`, `Storage/SafeFileHandles.cs`, `Storage/MountIdentity.cs`; tests `StorageAdapterTests.cs`

**Interfaces:** adapter resolveจากStorageDefinitionฝั่งserver; `WriteImmutableAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct):Task<StoredCopy>`; `ReadVerifiedAsync(string key,string sha256,long length,CancellationToken ct):Task<byte[]>`; `ProbeAsync(CancellationToken ct):Task<StorageHealthView>` IDของhealthมาจากbindingregistry ไม่ใช่clientpath

- [ ] RED tests generatedkey+collision, traversal/absolute/UNC/encodedseparator, symlinkเปลี่ยนก่อนopen, unmountเหลือdirectory, markerไม่ตรง, permissiondeny, diskfull, readpartial/timeout, corruptedcopy ไม่มีไฟล์นอกrootถูกสร้าง:

```csharp
[Theory]
[InlineData("../outside.jpg")]
[InlineData("/tmp/outside.jpg")]
[InlineData("objects/link/escape.jpg")]
public async Task Unsafe_key_cannot_escape_configured_root(string key)
{
    await Assert.ThrowsAsync<IOException>(() => adapter.WriteImmutableAsync(key, new byte[] { 1 }, CancellationToken.None));
}
// adapterเป็นfieldที่testconstructorสร้างด้วยtemporaryroot; case linkสร้างsymlinkก่อนเรียก
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~StorageAdapterTests` ให้ล้มที่พฤติกรรมที่ยังขาด
- [ ] implementkeysเฉพาะ `objects/{first2hex}/{guid:N}/{full|thumbnail}.jpg`; operationใช้root-directoryhandle+openat nofollowทีละcomponentและexclusivecreate ไม่ใช้Path.GetFullPathเพียงอย่างเดียว; ตรวจinode/volume+markerผ่านhandleทั้งก่อนและหลังI/O ถ้าruntimeไม่รองรับให้readinessfail ไม่ fallbackunsafe
- [ ] เขียน `.partial-{operationGuid}` ภายในvolumeเดิม, flushdisk, renameแบบno-replace, syncdirectoryแล้วเปิดอ่านchecksumจริง; retryfinalkeyเดิมต้องตรวจlength/checksumก่อนคืนsuccess ต่างกันให้quarantine/error ไม่overwrite
- [ ] NASต้องตรวจmountedfilesystemidentityกับprotectedconfigและmarkerสองอย่าง ไม่สร้างmarkerอัตโนมัติในrequest; permissionต้องserviceaccountไม่ใช่world-writable; nativehandleimplementationทดสอบLinux/macOSจริง แยกWindowsunsupported
- [ ] รันfocusedครบและfailurematrix ตรวจว่าไม่เติมmarkerหรือเขียนเมื่อunmounted; commit `feat: add guarded local and mounted NAS adapters`

## Task 4: ลงทะเบียนที่เก็บ เปลี่ยนปลายทาง และhealth

**Files:** สร้าง `Storage/StorageRegistry.cs`, `Storage/StorageEndpoints.cs`, `Storage/StorageHealthWorker.cs`, `Storage/StorageOptions.cs`; แก้AttendanceRegistration/Programเฉพาะwiring; tests `StorageRegistryTests.cs`, `StorageApiTests.cs`

**Interfaces:** `PinAsync(Guid operationId,EmploymentSnapshot subject,StampRequest stamp,CancellationToken ct):Task<EvidenceReservation>` บันทึกreservationและowner/snapshot/stampก่อนI/O ตรวจsubject.EmployeeIdเป็นactorปัจจุบันและsnapshotถูกต้อง แต่ไม่บังคับstorage-manageกับพนักงานผู้ลงเวลา; 6Cเป็นผู้ตรวจchallenge/exactSiteก่อนเรียก internal service นี้ ไม่เปิดHTTPpin endpoint; registryรับHTTP DTOตามตาราง; `StorageRegistry.Capability = "attendance:storage-manage"`; probe/requestreturnเป็นIResultสไตล์DirectoryService ไม่มีระบบสิทธิ์ใหม่

- [ ] RED usingIdentityTestDriver+PostgresFixture: anonymous401/staff403/adminไม่grant403/MFAexpired403, forgedroot400หลังauth, aliasunknown404, duplicate409, switchexpectedVersionเก่า409, readinessหมดอายุ409, probeล้ม503, simultaneousswitchผู้ชนะหนึ่ง

```csharp
using var response = await d.PostAsync("/api/v1/attendance/storage/write-target",
    new { storageId = targetId, expectedVersion = 1, reason = "ทดสอบเปลี่ยนปลายทาง" });
Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); // จัดfixture singleton version2
// ตรวจauditและtargetversionด้วยdbใหม่ ไม่อ่านtrackedentityเก่า
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~StorageRegistryTests|FullyQualifiedName~StorageApiTests"`
- [ ] auth/auditใช้patternDirectoryService: `ScopeOperation.BeginAsync` lock7241002→ตรวจidentity/capability/recentMFA→version/alias→audit+commit; probefilesystemทำนอกlock ก่อนกลับมาrevalidate actor/version/mountfingerprint; auditfailไม่เปลี่ยนtarget
- [ ] Pin targetในshorttransactionเพิ่มreservation(operationIdunique) ก่อนrelease; switchใหม่ไม่เปลี่ยนreservationเดิม; ไม่fallbackwritingwhenoutage; ขณะmigrationซีลsourceAcceptWrites=falseและตรวจsourceไม่ใช่active target ต้องswitchก่อนเริ่ม
- [ ] healthworkerใช้readinessและmanifestscanboundedbatch100 รักษาlastChecked/unknown/errorcode แจ้งในAPI/Portalไม่ส่งemail; ไม่มีbytes/pathส่วนตัวในhealth ผลreadonlyไม่ให้สิทธิ์ดูรูป
- [ ] focusedtestsผ่านรวมrevokeระหว่างprobe, rootconfigเปลี่ยนภายใต้aliasเดิมทำให้503; commit `feat: manage versioned attendance storage targets`

## Task 5: เตรียมหลักฐานและอ่านรูปตามสิทธิ์จริง

**Files:** สร้าง `Evidence/EvidenceWriter.cs`, `Evidence/EvidenceReader.cs`, `Evidence/EvidenceEndpoints.cs`, `Evidence/AuthorizedImageResult.cs`, `Evidence/EvidenceReconciler.cs`; tests `EvidencePublicationTests.cs`, `EvidenceReadTests.cs`, `EvidenceRevocationTests.cs`

**Interfaces:** `PrepareAsync(EvidenceReservation reservation,Stream image,StampRequest stamp,CancellationToken ct):Task<PreparedEvidence>`; `StagePublicationAsync(EvidencePublication publication,CancellationToken ct):Task` เพิ่มrowsแต่ไม่commit ต้องมีcallertransaction; `ReadAsync(Guid id,string variant,bool download,CancellationToken ct):Task<IResult>`

- [ ] REDtests stateReserved/Prepared/Orphan GET404, owner200, ownsupervisor200, supervisorของคนอื่น404, HRcurrent+MFA+snapshotunit200, crossunit404, admin/storage-managerไม่มีbusinessgrant404, revoked401/403/404ตามsessiondecision ห้ามส่งแม้thumbnail

```csharp
using var response = await d.Client.GetAsync($"/api/v1/attendance/evidence/{evidenceId}/download");
Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // loginหัวหน้าลูกทีม ไม่ใช่owner/HR
Assert.DoesNotContain("image/", response.Content.Headers.ContentType?.ToString() ?? "");
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~EvidencePublicationTests|FullyQualifiedName~EvidenceReadTests|FullyQualifiedName~EvidenceRevocationTests"`
- [ ] protocol: reserveDB→ตรวจstampตรงreservation→stamp/filesystemfinalizeทั้งfull+thumbnail→shortDBtransactionmarkPreparedและstorechecksum→caller6Ctransactionตรวจsession/assignment/challenge/pairซ้ำ→StagePublicationตรวจoperation/owner/stamp/snapshot/evidenceตรงกัน→binding+statePublished+event+audit→callercommit; rejectcallไม่มีtransaction ห้ามreaderเชื่อclientowner/eventId ใช้reservationlease/fencingversionกันPrepareซ้ำ; sameoperation+sameinputคืนผลเดิม ต่างinput409 ไม่เขียนไฟล์ทับ
- [ ] 6Bสร้างPublishedผ่านtest-only fixture/service compositionเท่านั้น ไม่mapupload/publishในproduction; transactionrollbackต้องไม่มีbinding/Published; orphanreportสแกนreservationที่leaseหมดหลัง24ชั่วโมงและไม่มีbinding ไม่unlinkไฟล์ รายงานจำนวนไม่เปิดlocator
- [ ] readerขั้นแรกshortlockตรวจReadAsync/CanReadPhotoและPublished→โหลดbytesนอกidentitylockจากactivecopychecksumverified (fallbackเฉพาะสำเนาที่metadataอนุญาตและchecksumตรง)→finalsession/ReadAsyncอีกครั้งก่อนเปิดresponse
- [ ] finaldeliveryใช้connection-scoped shared advisory lock7241002สำหรับช่วงauditcommitและส่งbufferเท่านั้น เพื่อserializeกับrevocationที่ใช้exclusive xactlockเดียวกัน; ไม่มีfilesystem I/Oขณะถือlock จำกัดresponse10MiB/deadline5วินาที ยกเลิกแล้วabortresponseก่อนปล่อยlock ใช้dedicatedconnectionและfinallyunlock/disposeไม่คืนconnectionที่ยังถือlockเข้าpool; readerไม่เรียกScopeOperation.BeginAsyncบนconnectionอื่นขณะsharedlockอยู่
- [ ] นิยามrace: revokecommitก่อนsharedlock→ไม่มีbytes; readerได้lockก่อน→ส่งในboundedwindowก่อนrevocationcommit ภาพที่ส่งไปแล้วเรียกคืนไม่ได้ ห้ามอ้างกำจัดภาพที่ผู้ใช้ดาวน์โหลดแล้ว; auditfailureก่อนheaders→503และไม่มีbytes หลังส่งบางส่วนnetworkfail→abortไม่ส่งJSONต่อท้าย
- [ ] controlledbarrier tests: pauseก่อนfileload/หลังfileload/ก่อนsharedlock แล้วrevoke/disable/HRend; auditfail/connectioncancel/slowclientไม่ทำให้lockค้าง; no-store/nosniff/Range/HEAD/conditionalและfallbackcorruptไม่หลุด; commit `feat: publish and authorize attendance evidence atomically`

## Task 6: ย้ายสำเนาแบบresumeและไม่เปลี่ยนหลักฐาน

**Files:** สร้าง `Storage/MigrationService.cs`, `Storage/MigrationWorker.cs`, `Storage/MigrationEndpoints.cs`; tests `EvidenceMigrationTests.cs`, `EvidenceMigrationRaceTests.cs`

**Interfaces:** `StartAsync(StartMigration,CancellationToken):Task<IResult>`, `ResumeAsync(Guid,ResumeMigration,CancellationToken):Task<IResult>`, `RunBatchAsync(Guid jobId,int limit,CancellationToken):Task<int>` limit1–100; workerใช้StorageAdapterและEvidenceLocationเดิมไม่ใช้ImageStampService

- [ ] REDfixtureเก็บรูปสังเคราะห์2variants: ย้ายแล้วevidenceId/eventId/checksum/stampเหมือนเดิม; sourcecopyยังอยู่; corruptdestinationไม่switch; sameRequestId+samepayloadคืนjobเดิม ต่างpayload409; duplicatejobsourceที่ยังactive409

```csharp
// ใช้fixturesจากTask5 ให้publishedEvidenceIdและoriginalShaเป็นค่าก่อนย้าย
await worker.RunBatchAsync(jobId, 100, CancellationToken.None);
await using var db = d.Database.CreateContext();
var after = await db.Set<EvidenceObject>().SingleAsync(x => x.Id == publishedEvidenceId);
Assert.Equal(originalSha, after.Sha256);
Assert.True(await db.Set<EvidenceLocation>().AnyAsync(x => x.EvidenceId == after.Id && x.State == CopyState.Fallback));
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~EvidenceMigrationTests|FullyQualifiedName~EvidenceMigrationRaceTests"`
- [ ] Startตรวจstorage-manage/MFA/currentversion/source≠target/probeพร้อม/sourceไม่activewritetarget→ซีลsourceและสร้างdurablejob+manifest+auditในtransactionเดียว; includePreparedและPublished ไม่ย้ายrawupload ยังรอreservationที่pinไว้ก่อนซีล
- [ ] workerclaimด้วยlease60วินาทีและfencingversion; copyผ่านadapterนอกidentitylock→อ่านปลายทางขนาด/SHA→transactionตรวจleaseversion/sourceversion/objectchecksum/สถานะjob/ผู้สั่งยังมีactivecapability→เพิ่มverifiedcopy→สลับActive/Fallback+auditcommit; งานเบื้องหลังไม่ต้องใช้sessionMFAที่หมดใน15นาที แต่resume/startต้องrecentMFAเสมอ และถูกถอนcapabilityให้Blockedไม่cutover
- [ ] checksummismatchเป็นBlockedไม่retryอัตโนมัติ; I/Otransientretryตามตาราง; leaseหมดworkerเก่าห้ามcutover; crashหลังcopyก่อนmetadataใช้keyเดิมและverifyก่อนadopt ไม่overwrite; full/thumbnailแต่ละcopyมีchecksumของตน
- [ ] ก่อนCompletedต้องreconcilelatePreparedจากreservationเดิมและไม่มีunfinishedreservation/sourceactivecopyเหลือ; ถ้าreservationไม่จบให้Blocked/รายงาน ไม่แกล้งCompleted งานที่Completedยังไม่อนุญาตถอดsourceหรือDELETE; sourcefallbackอ่านเฉพาะexplicitmetadata/configและauditbasis
- [ ] tests crashทุกboundary,2workers,lateupload,readขณะcutover,actorgrantrevokedก่อนcutover,auditfail,restorejobแล้วresume,manifestduplicateไม่เพิ่มรายการ; commit `feat: migrate evidence copies with resumable verification`

## Task 7: Portal จัดการstorageและสัญญาAPI

**Files:** สร้าง `app/portal/admin/attendance-storage/page.tsx`, `components/attendance/StorageForm.tsx`, `components/attendance/useStorageQuery.ts`, `lib/attendance/storage-client.ts`, `lib/attendance/storage-view.ts`, `tests/storage-client.test.mjs`, `tests/e2e/attendance-storage.spec.ts`, `infra/nginx/smoke-attendance-storage-https.mjs`, `backend/tests/TPR10.Api.IntegrationTests/StorageOpenApiTests.cs`; แก้auth-client/portal/AttendanceOpenApiTransformerและE2Efixture

**Interfaces:** clientส่งPOSTเฉพาะroutesตามตาราง ไม่รับURLอิสระ; parserรับStorageView/Target/Migration/Healthเท่านั้น failclosedเมื่อunknownvariant; hookqueryรักษาepoch/abort/account-changeเหมือนDirectory UI ไม่คัดลอกสิทธิ์จากUIมาแทนAPI

- [ ] REDbrowsertests หน้าไม่ปรากฏสำหรับadminไม่มีcapability, กรอกrootไม่ได้, healthunknownไม่แสดงพร้อม, switch409โหลดversionใหม่, migrationมีprogress/resume,503ไม่logout, lateHRresponseหลังเปลี่ยนบัญชีไม่ขึ้นDOM; storage-managerดูรูปไม่ได้แม้รู้ID

```ts
await page.goto('/portal/admin/attendance-storage');
await expect(page.getByRole('heading', { name: 'จัดการที่เก็บรูปลงเวลา' })).toBeVisible();
await expect(page.getByLabel('พาธโฟลเดอร์')).toHaveCount(0);
await expect(page.getByRole('button', { name: 'เปลี่ยนที่เก็บรูปใหม่' })).toBeDisabled();
// fixtureปลายทางhealthunknown; ต้องไม่เปิดปุ่มก่อนprobeสำเร็จ
```

- [ ] รันentrypointใหม่ขณะUIยังไม่มีเพื่อRED (ใช้พอร์ต4000/4001ตามรอบและขอหน้าต่างหยุดPreviewก่อนถ้าพอร์ตใช้แล้ว)
- [ ] implementหน้าจากaliasdropdown, สถานะปลายทาง, health, คำเตือนรูปเก่ายังอยู่ที่เก่า, manifestcounts, ปุ่มprobe/switch/start/resumeพร้อมreasonและexpectedVersion ไม่แสดงthumbnailบนหน้าstorageadmin; ไม่มีปุ่มdelete/retire/rawpath
- [ ] authMutationเพิ่มexactPOSTallowlistและtestrejectอื่น; loader/hookซ่อนข้อมูลระหว่างauthrecheck/logout/focus/accountchange และไม่แสดงร่างก่อนqueriesจำเป็นกลับมาครบ ใช้บทเรียนDirectoryForm ไม่ลดการป้องกัน503
- [ ] entrypointคงfixedspec:

```js
import assert from 'node:assert/strict';
import { runHttpsSmoke } from './https-smoke-fixture.mjs';
assert.ok(process.argv.length <= 3, 'รับเฉพาะพอร์ต ไม่รับ --spec');
await runHttpsSmoke({ port: process.argv[2] ?? '4001', spec: 'tests/e2e/attendance-storage.spec.ts', e2e: true });
```

- [ ] OpenAPIassert14routesทีละoperation ทั้งdev/prod; schemaห้ามrootPath/GPS/rawimage; securitycookie/CSRF/MFA/permission/status/nocacheครบ ไม่มีupload/publishtestfixtureในprod; `npm test`, OpenAPIfocused และHTTPSstorageทั้ง4000/4001ผ่าน; commit `feat: add Thai storage administration portal`

## Task 8: ตรวจรับทั้งระยะ คู่มือกู้คืน และNASจริง

**Files:** สร้าง `docs/runbooks/module-6b-storage.md`, `docs/architecture/module-6b-exit-gate.md`, `backend/tests/TPR10.Api.IntegrationTests/EvidenceStorageExitTests.cs`; แก้delivery-mapเฉพาะสถานะจริง และเอกสารนี้เฉพาะcheckboxหลักฐาน

**Interfaces:** ใช้contractsทั้งหมดข้างต้น ไม่เพิ่มbusinessfeatureในTaskปิดงาน; 6CรับEvidenceReservation/Prepare/StagePublicationและreader โดยต้องมีtransactioncallerและFKeventจริงของ6C

- [ ] เขียนREDexit contractปิดtest-onlypublisherและpublic/staticpathในProduction:

```csharp
using var upload = await d.PostAsync("/api/v1/attendance/evidence/publish", new { });
Assert.Equal(HttpStatusCode.NotFound, upload.StatusCode);
using var direct = await d.Client.GetAsync("/evidence-files/example.jpg");
Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
```

- [ ] รันfocusedexit แล้วเติมproductionwiringปิดเส้นทางทดลองหากพบจนผ่าน ไม่เพิ่มproductionseedบัญชี/รูปจริง
- [ ] รันตรวจทั้งหมดบนcommitเดียวกันและเก็บcommand/UTC/exitcode/count ไม่อ้างผลรุ่นเก่าแทน:

```sh
npm ci
npm test
npm run lint
npm run build
dotnet restore backend/TPR10.sln
dotnet build backend/TPR10.sln
dotnet format backend/TPR10.sln --verify-no-changes
dotnet test backend/TPR10.sln
npm audit
dotnet list backend/TPR10.sln package --vulnerable --include-transitive
node infra/nginx/smoke-identity-https.mjs 4000 --e2e
node infra/nginx/smoke-identity-https.mjs 4001 --e2e
node infra/nginx/smoke-identity-https.mjs 4000 --e2e --spec tests/e2e/scopes.spec.ts
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes.spec.ts
node infra/nginx/smoke-identity-https.mjs 4001 --e2e --spec tests/e2e/scopes-boundary.spec.ts
node infra/nginx/smoke-attendance-directory-https.mjs 4000
node infra/nginx/smoke-attendance-directory-https.mjs 4001
node infra/nginx/smoke-attendance-storage-https.mjs 4000
node infra/nginx/smoke-attendance-storage-https.mjs 4001
```

- [ ] entrypointidentityเดิมเลือกspecทีละชุดและต้องมี--e2eตามคำสั่งข้างต้น ต้องตรวจoutputว่าครบ ไม่รันHTTPแทนHTTPS; แยกartifacts backendจากfixtureป้องกันbinaryเปลี่ยนระหว่างfullsuite; build/runimageบนLinuxต้องโหลดSkia/HarfBuzzและfontจริงได้
- [ ] ตรวจCode Reviewหนึ่งfresh whole-branch reviewerตามNative ตรวจprivacy/race/migration/recoveryพร้อมแก้Critical/Importantด้วยTDD แล้วรันTest/Build/Lintครบใหม่ หากยังมีfailureห้ามระบุเสร็จ
- [ ] NASจริง: ขอผู้ใช้/Operationsจัดเตรียมtestshareที่ไม่ใช่ข้อมูลจริงและอนุญาตdisruptionก่อน ตั้งserviceidentity+mountaliasผ่านprotectedconfig ไม่บันทึกcredentialในเอกสาร ทดสอบwrite/read/rename/flush, disconnect/reconnect, mountหายแต่directoryยังอยู่,สิทธิ์readonly,พื้นที่เต็มในquotaทดลอง,DB/auditfail,copycrashและresume ตรวจSHAfull+thumbnailก่อน/หลัง; ถ้าไม่มีNASให้รายงานtechnical-onlyและNAS gateยังค้าง ห้ามmockแล้วติ๊กผ่าน
- [ ] Backup/restoredrill: ใช้ฐานข้อมูลชั่วคราว+สำเนาไฟล์สังเคราะห์ร่วมกัน restoremetadataและfiles ตรวจทุกbindingยังมีchecksumตรงและสิทธิ์ยังบังคับ, key/config/permissionกลับถูกต้อง ไม่restoreทับPreview/production
- [ ] คู่มือภาษาไทยระบุregisteralias→probe→switch→migration→verify→retainold ไม่มีลบอัตโนมัติ, alertunknown/full/missing/orphan, retrylimits, incidentwho/when/referenceไม่ใส่พาธลับ และข้อจำกัดnativecodec/DBA/SMBdurability/lockthroughput ต้องมีผู้รับผิดชอบsign-offก่อนProduction
- [ ] สรุปtechnicalgate/NASgate/productiongateแยกกัน, หลักฐานรูปstampดูจริง และR02/R03/R13–15coverage; commit `docs: record module 6b verification and storage recovery` แล้วให้ผู้ใช้เลือกpush/PR/merge ไม่ทำอัตโนมัติ

## ตารางส่งต่องานและความครบถ้วน

| ข้อกำหนด | Taskรับผิดชอบ | สิ่งที่ยังไม่อ้างว่าสำเร็จ |
| --- | --- | --- |
| R02 เวลาอยู่ในpixels | 2,5,8 | เวลาeventและcamera challengeจริงเป็น6C |
| R03 local/NAS/switch/migrate | 1,3,4,6,7,8 | ถอดต้นทาง/ลบรูปต้องอนุมัติแยก; NASจริงต้องมีdrill |
| R13 ไม่ลบอัตโนมัติ | 1,5,6,8 | orphanreportไม่ใช่cleanup |
| R14/R15 privacy | 5,7,8 ใช้6AReadAsync | GPS projection/หน้าประวัติเป็น6C |
| ข้อ10 filesystem/DB/audit | 1,3,5,6,8 | event/challenge/idempotencyรวมเป็น6C แต่operationreservationไม่ซ้ำใน6B |
| ข้อ13–15 API/security/operations | 4–8 | productionapprovalไม่เกิดจากการอนุมัติแผน |

## เงื่อนไขก่อนเริ่มและผลตรวจเอกสาร

- ฐาน6Aรวมแล้ว ต้นไม้mergeตรงกับโค้ดที่ส่งมอบ; ไม่ต้องทำ6Aซ้ำ
- อ่านไฟล์interfacesจริง6Aและplanรวมแล้ว ไม่มีการเปลี่ยนpolicyown/supervisor/HR; capabilitystorageมีอยู่แล้ว
- แผนกำหนด8Tasks พร้อมขอบเขตไฟล์/สัญญา/RED–GREEN/commit/review/verification; ห้าประเด็นReview Focusมีเจ้าของtestตามตาราง
- สถานะเอกสารเป็นข้อเสนอจนผู้ใช้อนุมัติ โดยเฉพาะshared-delivery-lock, image limits/format, timeout/retry/healthค่าใหม่ ต้องตรวจthroughput/slowclientก่อนProduction
- การเขียนแผนครั้งนี้ไม่ใช่หลักฐาน6Bผ่านtests; ผลbaselineรายงานแยก ไม่มีการติดตั้งimagepackagesหรือmountNASในรอบวางแผน
- ก่อนexecutionใช้Native+TDD+whole-branch reviewตามที่เลือกไว้ หากผู้ใช้เปลี่ยนวิธีให้บันทึกใหม่; ต้องตรวจเอกสารนี้ก่อนเริ่มTask1

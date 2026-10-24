# Module 7B Implementation Plan แผนพัฒนาคำขอเงินทดรองและสายอนุมัติ

> **สำหรับผู้ดำเนินงาน:** ใช้ `superpowers:subagent-driven-development` แยกผู้ลงมือและผู้ตรวจใหม่ในแต่ละ Task ตามที่ตกลงไว้ หากเครื่องมือไม่รองรับให้แจ้งก่อนเปลี่ยนวิธีเป็น `superpowers:executing-plans` ห้ามอ้างว่าเป็นการตรวจอิสระเมื่อเป็นผู้ลงมือคนเดิม ใช้ช่อง `- [ ]` ติดตามผลจริง

**เป้าหมาย:** พัฒนาการขอเงินทดรองออกสนาม ยื่นแทน ยืนยันผู้รับผิดชอบ และอนุมัติเป็นลำดับจนถึง “อนุมัติแล้ว รอดำเนินการจ่าย” โดยไม่ข้ามสิทธิ์และไม่แก้ประวัติเดิม

**สถาปัตยกรรม:** ต่อระบบเดิมด้วยคำขอหลักและฉบับแยก ตรึงกติกา 7A เมื่อส่ง แต่ตรวจสิทธิ์และตัวบุคคลปัจจุบันทุกคำสั่ง แยกการคำนวณ สายอนุมัติ บริการคำสั่ง และส่วนอ่านข้อมูล พร้อมธุรกรรม audit และการกันคำสั่งซ้ำ

**เทคโนโลยี:** ASP.NET Core/.NET 10, EF Core 10, PostgreSQL/Npgsql, Next.js 15, React 19, TypeScript, xUnit และ Playwright ตาม dependencies เดิม ไม่เพิ่ม workflow framework

**เอกสารออกแบบ:** `docs/superpowers/specs/2026-10-22-module-7b-requests-approvals-design.md` ผู้ใช้อนุมัติแล้ว ผู้ลงมือและผู้ตรวจต้องอ่านทั้งแบบและแผน

**สถานะแผน:** ผู้ใช้อนุมัติแล้ว 22 ตุลาคม 2569 อัปเดต 24 ตุลาคม 2569: Task 1–10 ผ่านครบโดยแยกผู้ลงมือและผู้ตรวจ ผลสุดท้าย Backend 2,561/2,561, Frontend 146/146, diagnostics 5/5, HTTPS ทั้ง 4000/4001 คำขอ/Policy/Grant พอร์ตละ 10/13/11 พร้อม Build, Format และ Lint ผ่าน คง M2/M3, T7-M1, Minor ใหม่ T10-M1 และข้อจำกัด 504 ที่ยังไม่ยืนยันสาเหตุ พักมือถือจริงและ NAS จริง ไม่รวม push/PR/merge/เงินจริง รายละเอียดใน `docs/reports/2026-10-22-module-7b-verification.md`

## ข้อกำหนดร่วม

- หนึ่งโครงการและหนึ่งผู้รับผิดชอบเงินต่อคำขอ รองรับรายบุคคล/ทีม ใช้บาทเท่านั้น สมาชิกทีมไม่มีสิทธิ์การเงินอัตโนมัติ
- จำนวนไม่เกิน 4 ตำแหน่ง อัตรา/ยอดเหมารวมไม่เกิน 2 ตำแหน่ง กรอกเกินแจ้งแก้ ไม่ปรับเงียบ ๆ ปัดผลแต่ละรายการเป็นสตางค์แบบหลักที่สามตั้งแต่ 5 ปัดขึ้น แล้วรวมเพื่อกำหนดขั้นอนุมัติ
- หมวดมาตรฐานใช้จำนวน × อัตราเท่านั้น หมวดไม่มีมาตรฐานเลือกสองวิธีได้ ยอดรวมต้องมากกว่าศูนย์
- ผู้รับผิดชอบยืนยันทุกฉบับ ผู้ยื่นแทนยืนยัน/ถอนไม่ได้ กติกาหรือเนื้อหาเปลี่ยนก่อนยืนยันให้ทบทวนใหม่
- ตรึงกติกาที่มีผลสำหรับโครงการขณะส่ง ไม่เปลี่ยนกลางฉบับ โครงการเฉพาะยังผูกฐานบริษัทที่อนุมัติไว้
- ตรวจสิทธิ์ปัจจุบันในโครงการจริง แยกผู้เสนอ/ผู้อนุมัติ/ผู้รับสิทธิ์สามคน ไม่ให้ admin หรือ role ทั่วไปข้ามข้อจำกัด
- ผู้อนุมัติหนึ่งคนได้หนึ่งขั้นต่อฉบับ กลับมาอนุมัติฉบับใหม่ได้ด้วยผลใหม่ แต่ข้อห้ามข้ามหน้าที่ของทั้งคำขอไม่หาย
- ผ่านหัวหน้าตามสายงาน → ผู้จัดการโครงการ → ทุกระดับตามยอด หากไม่มีคนเข้าเงื่อนไขให้พัก ไม่ลดขั้น/เพิ่มสิทธิ์เอง
- ส่งกลับต้องเริ่มฉบับใหม่และอนุมัติใหม่ทั้งหมด ปฏิเสธ/ถอนเป็นปลายทางถาวร ถอนเฉพาะผู้รับผิดชอบที่ยังมีสิทธิ์ก่อนอนุมัติครบ พร้อมเหตุผล
- 7C/7D การจ่าย ปรับปรุงหลังอนุมัติ เคลียร์ คืนเงิน และปิดคำขอ ไม่อยู่ในแผนนี้
- ข้อความผู้ใช้และเอกสารเป็นภาษาไทย dev 4000, production preview 4001 ผ่าน HTTPS fixture 4443 สำหรับ flow ที่ต้องใช้ secure session
- คง M2/M3 ไว้ติดตาม พักมือถือจริงและ NAS จริง ไม่ปิดรายการเหล่านี้ด้วยผลจำลอง

## จุดเน้นสำหรับผู้ตรวจ

1. ตัวเลขแบบ `1.00000`, `10.000`, exponential notation และ JSON number ไม่ต้องถูกแปลงจนตรวจความละเอียดไม่ได้: Task 1/8 ทดสอบสัญญาตัวเลขเป็นข้อความโดยตรง
2. ผู้ใช้หลายบัญชีของบุคคลเดียว การรวมสิทธิ์จากหลาย grant และ grant เก่าไม่มีข้อมูลระดับ: Task 3/5 ต้องไม่รวมวงเงินหรืออนุมานระดับให้เอง
3. ผู้ใช้เปิดหน้าค้างแล้วเปลี่ยนบัญชี/หมดสิทธิ์ระหว่างรอ response: Task 8/9 ต้องทิ้ง response เก่าและไม่เผยข้อมูลค้าง
4. คำสั่งบันทึกสำเร็จแต่ response หาย จากนั้น retry ชนถอนสิทธิ์/เปลี่ยนฉบับ: Task 4/7/8 ต้องไม่ทำซ้ำและไม่คืนข้อมูลที่ไม่มีสิทธิ์
5. worker ประเมินสายอนุมัติซ้ำหรือชนการอนุมัติ/ถอน: Task 7/10 ต้องไม่สร้าง audit ซ้ำทุก poll หรือย้อนผลที่ลงแล้ว

## ภาพรวม 10 Task

| Task | ผลงานที่ตรวจรับได้ | อาศัย |
|---|---|---|
| 1 | สัญญาข้อมูลและการคำนวณเงิน | แบบที่อนุมัติ |
| 2 | ฐานข้อมูลคำขอ/ฉบับ/ประวัติและ migration | 1 |
| 3 | สิทธิ์ยื่นเอง/แทน และผูกระดับอนุมัติกับสิทธิ์ | 1–2 |
| 4 | ร่างคำขอและธุรกรรมคำสั่งที่กันซ้ำ | 1–3 |
| 5 | หาผู้อนุมัติ ไล่ระดับ และเหตุผลพัก | 2–3 |
| 6 | ยืนยันผู้รับผิดชอบ ตรึงกติกา และเริ่มสายอนุมัติ | 4–5 |
| 7 | ลงผล ส่งกลับ ฉบับใหม่ ปฏิเสธ ถอน และประเมินผู้รับงานใหม่ | 6 |
| 8 | API อ่าน/คำสั่งและ client ที่รักษาขอบเขตสิทธิ์ | 4–7 |
| 9 | หน้าจอคำขอ ยืนยัน อนุมัติ และประวัติ | 8 |
| 10 | ตรวจรวม ความพร้อม migration และเอกสารส่งมอบ | 1–9 |

แต่ละ Task ทำ Red → Green → ตรวจตามแบบ → ตรวจคุณภาพโค้ด → แก้ข้อพบ → รันชุดตรวจ → commit เฉพาะไฟล์ Task ห้ามเริ่ม Task ถัดไปเมื่อยังมี Critical/Important ค้าง ผู้ตรวจต้องไม่ใช่ผู้ลงมือ

## สัญญาทางเทคนิคที่แผนกำหนด

### ชนิดข้อมูลร่วม

สร้างใน `backend/src/TPR10.Api/FieldAdvances/Requests/RequestContracts.cs` ไม่เปลี่ยนชื่อชนิดเดิมของ 7A:

- `EstimateLineInput(Guid Id, Guid CategoryId, string Description, string Mode, string? Quantity, string? UnitRate, string? LumpSum)`; Mode คือ `QuantityRate`/`LumpSum` ตัวเลข wire เป็นข้อความฐานสิบ ASCII ไม่มี separator หรือ exponent
- `EstimateLine(Guid Id, Guid CategoryId, string Description, string Mode, decimal? Quantity, decimal? UnitRate, decimal Amount)` และ `Estimate(EstimateLine[] Lines, decimal Total)`
- `RequestContent(string Kind, string Purpose, DateOnly FieldStart, DateOnly FieldEnd, DateOnly NeededOn, Guid[] TeamPersonIds, EstimateLineInput[] Lines)`; Kind คือ `Individual`/`Team`
- `CreateRequest(FinanceScope Scope, Guid OwnerPersonId, RequestContent Content)`; ผู้ยื่นแทนคำนวณจาก actor ไม่รับ client ระบุเอง
- `RequestCommand(Guid CommandId, long ExpectedVersion, string Reason)`; สร้างใหม่ใช้ ExpectedVersion=0 คำสั่งอื่นต้องตรง aggregate version ที่เซิร์ฟเวอร์คืน
- `RequestView(Guid Id, Guid RevisionId, long Version, string State, FinanceScope Scope, Guid OwnerPersonId, Guid? DelegatePersonId, RequestContent Content, Estimate? Estimate, string? HoldCode)`
- `ConfirmationQuote(Guid RequestId, Guid RevisionId, long Version, string ConfirmationHash, Estimate Estimate, PolicySnapshot Policy, string[] Levels)`; hash ครอบคลุมเนื้อหา/ยอด/กติกาที่แสดง ไม่ใช้เป็นสิทธิ์
- `RequestDecision(string Outcome, Guid StepId)`; Outcome คือ `Approve`/`Return`/`Reject`
- `RequestParticipationHistory(Guid[] OwnerPersonIds, Guid[] DelegatePersonIds, Guid[] ApprovedPersonIds, Guid[] CheckerPersonIds, Guid[] PayerPersonIds)` เก็บข้อห้ามทั้งคำขอ; ชุดผู้อนุมัติในฉบับส่งแยกต่างหาก

ชนิดข้อมูลบริการใช้ `FinanceResult<T>` เดิม เวลาใช้ `TimeProvider` และ actor ใช้ `RequestSession` เดิม มี wire view ใน Task 8 แปลง decimal ออกเป็น string ไม่ให้ JavaScript สูญเสียสตางค์

### ทางเดินสิทธิ์ระดับอนุมัติ

ส่วนนี้เป็นการกำหนดวิธีผูกระดับตามแบบ ไม่ให้วงเงินสูงแทนบทบาท:

- ขยายข้อเสนอสิทธิ์ 7A ด้วย `ApprovalLevels`, `DelegableApprovalLevels`, `PrimaryApprovalLevels`, `DelegablePrimaryApprovalLevels` ชนิด `string[]` ค่าเริ่มต้นว่าง ห้าม backfill grant เก่าเป็นทุกระดับ สองชุดแรกคืออำนาจทำขั้น/ให้ต่อ สองชุดหลังคือการแต่งตั้งผู้ดำรงบทบาทหลัก/อำนาจแต่งตั้งต่อ โดยชุดหลักต้องเป็น subset ของชุดอำนาจที่ตรงกัน
- `supervisor`/`project-manager` เป็นรหัสขั้นคงที่ ส่วนระดับเพิ่มใช้ `LevelCode` ตามกติกาที่มีผล การให้ระดับต้องผ่านเสนอ–อนุมัติสามคนเดิม พร้อมวงเงิน ช่วงเวลา และโครงการ
- การมี `request.approve` ต้องมีระดับที่ตรงและวงเงินไม่เป็น null จึงลงมือได้ การให้ต่อแต่ละระดับต้องมีแหล่งสิทธิ์เดียวที่ครอบคลุม capability ระดับ วงเงิน และช่วงเวลาทั้งชุด ห้ามรวมแหล่งเพื่อต่อสิทธิ์
- หัวหน้าค้นจาก ReportingLine ของผู้รับผิดชอบที่มีผลปัจจุบัน ไม่ใช้ PrimaryApprovalLevels สำหรับ supervisor ส่วนผู้จัดการโครงการ/ระดับเพิ่มค้นผู้ดำรงบทบาทหลักจาก PrimaryApprovalLevels ในโครงการก่อน ผู้รับช่วงอาจมี ApprovalLevels เดียวกันแต่ไม่ใช่ผู้ดำรงบทบาทหลัก จึงไม่ทำให้กำกวม กรณีผู้ดำรงบทบาทหลักมีหลายบุคคลให้พัก ไม่เลือกตาม ID; หากไม่มีหลักให้พัก ไม่เอาผู้รับช่วงคนใดมาเป็นหลักเอง
- บัญชีหลายบัญชีให้รวมด้วย person identity ก่อนตรวจจำนวนผู้ดำรงบทบาท หากต้องไล่ผู้บังคับบัญชา ผู้ถูกเลือกยังต้องมีอำนาจของขั้นเดิม ไม่ข้ามขั้น
- ผู้บริหารสิทธิ์เก่าที่ไม่มีสิทธิ์ให้ต่อระดับใหม่ต้องผ่านการตั้งต้น/อนุมัติสิทธิ์โดยรายชื่อบริษัทรับรองตาม runbook เดิม ห้าม migration เติมอำนาจให้ admin เพื่อให้เริ่มใช้งานได้

### สถานะและการล็อก

สถานะคำขอ: `Draft`, `AwaitingOwner`, `PendingApproval`, `Held`, `Returned`, `Approved`, `Rejected`, `Withdrawn` ฉบับที่ส่งแล้ว immutable; ฉบับเก่าใช้ประวัติและ `Superseded` เมื่อสร้างฉบับแก้ไข คำขอมีฉบับปัจจุบันได้หนึ่งฉบับ

project/owner/delegate เป็นตัวตนคำขอ ไม่เปลี่ยนข้ามฉบับเพื่อหลบประวัติ; ต้องการโครงการหรือผู้รับผิดชอบอื่นให้สร้างคำขอใหม่ ผู้ยื่นเองไม่มี DelegatePersonId การแก้ draft หรือ returned-draft ไม่เปลี่ยนประวัติผู้มีส่วนร่วม

ใช้ transaction กับ advisory lock `7241002` เดิมก่อนตรวจสิทธิ์ และล็อกแถวคำขอก่อนเขียน ใช้ลำดับล็อกเดียวกันทุกบริการ/worker ไม่ลดการล็อกเพื่อแก้ M2 ใน Task นี้ คำสั่งไม่เรียกเครือข่ายภายนอกขณะถือ transaction

## ชุดตรวจร่วมก่อนปิดแต่ละ Task

ให้รันจาก repository root หลังแก้และหลัง review รอบสุดท้าย เก็บ log และ exit code โดยไม่บันทึก secrets:

```bash
dotnet build backend/TPR10.sln --no-restore --verbosity minimal
dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm test
npm run build
npm run lint
git diff --check
```

ทุกคำสั่งต้อง exit 0; tests ไม่มี failed/การ skip ที่ไม่ได้อธิบาย Build ไม่มี error, Lint ใช้ max-warnings 0 ตามเดิม หาก dependencies ยังไม่พร้อม ให้ restore/install ตาม lockfile แล้วรันใหม่ ห้ามใช้ `--no-build` กับ assembly เก่า ขณะ Red ใช้คำสั่ง test ที่ build ใหม่ตามแต่ละ Task

ชุด backend เดิมใช้เวลาประมาณหนึ่งชั่วโมง จึงใช้ filtered tests ระหว่าง TDD ได้ แต่ไม่แทนชุดตรวจร่วมตอนปิด Task หาก Docker/runtime/ทรัพยากรไม่พร้อม ให้รายงานว่ายังไม่ผ่าน gate ไม่ลดเทสต์หรือใช้ผลเก่า

### Task 1: สัญญาข้อมูลและการคำนวณประมาณการ

**ไฟล์:** สร้าง `backend/src/TPR10.Api/FieldAdvances/Requests/RequestContracts.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/EstimateCalculator.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceEstimateTests.cs`

**อินเทอร์เฟซ:** รับ `PolicySnapshot`/`CategoryRule` เดิม; สร้าง `EstimateCalculator.Calculate(EstimateLineInput[] lines, PolicySnapshot policy) : FinanceResult<Estimate>` และชนิดร่วมข้างต้น

- [x] เขียนเทสต์ `Rounds_each_line_before_sum`, `Rejects_excess_lexical_precision`, `Cannot_override_standard_rate`, `Rejects_overflow_and_negative` พร้อม assertion ต่อไปนี้ (สร้าง policy จำลองในไฟล์เทสต์ ไม่ใช่นโยบายจริง):

```csharp
Assert.Equal(24.70m, EstimateCalculator.Calculate(twoLinesOf1_2345Times10, policy).Value!.Total);
Assert.Equal(400, EstimateCalculator.Calculate(linesWithQuantity1_00000, policy).Status);
Assert.Equal(400, EstimateCalculator.Calculate(linesWithRate10_000, policy).Status);
Assert.Equal(400, EstimateCalculator.Calculate(lumpSumInStandardCategory, policy).Status);
```

- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceEstimateTests` ต้องแดงเพราะยังไม่มี calculator/ยังคำนวณผิด บันทึกหลักฐาน Red
- [x] ทำ parser ตามจำนวนหลักจริงก่อนแปลง decimal ใช้ checked arithmetic และ `decimal.Round(lineTotal, 2, MidpointRounding.AwayFromZero)` ไม่รับ exponent/NaN/Infinity/JSON number ตรวจหมวด active; อัตรามาตรฐานมาจาก snapshot และถ้าส่งค่าต่างให้ปฏิเสธ
- [x] เพิ่มเทสต์ยอด 0, ค่ามากกว่าขีดจำกัด 7A `9999999999999999.99m`, ผลคูณล้น, ผลรวมล้น, 0.005 → 0.01 และหมวดไม่มาตรฐานทั้งสองแบบ แล้วรันคำสั่งเดิมต้องผ่านทุกเคส
- [x] ผู้ตรวจเทียบ B02/B03 ตรวจชุดร่วม และ commit เฉพาะไฟล์นี้: `feat: add field advance estimate calculation`

ผล Task 1: commits `7a5e8b8` และ `6531274`; แก้ขอบเขต quantity ตาม `numeric(20,4)` จาก review พร้อม assertion Red 4 เคสก่อนแก้ เทสต์เฉพาะ 42/42 ผ่าน ผู้ตรวจซ้ำไม่มี Critical/Important ค้าง และชุดตรวจร่วมผ่านทั้งหมด

### Task 2: ฐานข้อมูลคำขอ ฉบับ และประวัติ

**ไฟล์:** สร้าง `backend/src/TPR10.Api/FieldAdvances/Data/RequestEntities.cs`, `backend/src/TPR10.Api/FieldAdvances/Data/RequestModelConfiguration.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestSchemaTests.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestFixture.cs`; แก้ `backend/src/TPR10.Api/Data/Tpr10DbContext.cs`; สร้าง migration `AddFieldAdvanceRequests` ด้วย EF และเก็บไฟล์ timestamp/Designer ที่ EF สร้างพร้อม `backend/src/TPR10.Api/Data/Migrations/Tpr10DbContextModelSnapshot.cs`

**อินเทอร์เฟซ:** entity `AdvanceRequest`, `AdvanceRequestRevision`, `AdvanceRequestLine`, `AdvanceApprovalStep`, `AdvanceRequestEvent`, `AdvanceRequestParticipant` ใช้ผ่าน `db.Set<T>()`; fixture `CreateAsync(PostgresFixture postgres) : Task<FieldAdvanceRequestFixture>` และ `CreateContext() : Tpr10DbContext`

- [x] เขียนเทสต์ `Migration_preserves_7A`, `One_current_revision`, `Cross_scope_reference_is_rejected`, `Submitted_content_is_immutable` บน PostgreSQL จริงแบบ disposable: `Assert.ThrowsAsync<DbUpdateException>(...)` สำหรับ FK/unique ผิด และ `Assert.Equal(before, retainedPolicy)` หลัง migrate
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRequestSchemaTests` ต้องแดงเพราะยังไม่มี schema/guards
- [x] กำหนด FK คำขอ–ฉบับ–รายการ–ขั้น–event ให้ scope ตรง, unique `(RequestId,RevisionNumber)`, `(RevisionId,StepOrder)`, unique ผลอนุมัติสำเร็จต่อ person ต่อ revision; numeric จำนวน `(20,4)` และเงิน `(18,2)` ตรวจ overflow ก่อน DB; เก็บ content/policy snapshot JSON พร้อม hash และ aggregate version
- [x] เพิ่ม participant แบบ append-only, snapshot/content guards หลังส่ง, Restrict delete ประวัติ; เก็บ assignee เป็นข้อมูลเปลี่ยนได้แยกจาก snapshot ใช้ `FinanceCommandReceipt` เดิมไม่สร้าง receipt ซ้ำอีกระบบ
- [x] สร้าง migration ด้วย `dotnet ef migrations add AddFieldAdvanceRequests --project backend/src/TPR10.Api --output-dir Data/Migrations` ตรวจ SQL แล้วทดสอบ upgrade จากฐาน7Aและฐานเปล่า ไม่ migrate ฐานผู้ใช้จริง รัน filtered tests ต้องผ่าน
- [x] ผู้ตรวจเทียบ B04/B13 ตรวจชุดร่วม และ commit: `feat: persist field advance request revisions`

ผล Task 2: commits `91a17dd` และ `efd7513`; เทสต์ schema 45/45 ผ่าน ตรวจอิสระและตรวจรอบแก้ Format ผ่าน ไม่มีข้อพบค้าง ชุดตรวจร่วมทั้งหมดผ่านบนโค้ดหลังแก้ ยังไม่เปิด API หรือพัฒนาสิทธิ์ Task 3

### Task 3: สิทธิ์ยื่นคำขอและอำนาจตามระดับ

**ไฟล์:** แก้ `backend/src/TPR10.Api/FieldAdvances/Access/FinanceCapabilities.cs`, `backend/src/TPR10.Api/FieldAdvances/FieldAdvanceContracts.cs`, `backend/src/TPR10.Api/FieldAdvances/Data/GrantEntities.cs`, `backend/src/TPR10.Api/FieldAdvances/Data/FieldAdvanceModelConfiguration.cs`, `backend/src/TPR10.Api/FieldAdvances/Grants/GrantService.cs`, `backend/src/TPR10.Api/FieldAdvances/Grants/GrantHttp.cs`, `backend/src/TPR10.Api/FieldAdvances/Grants/GrantReadService.cs`, `backend/src/TPR10.Api/FieldAdvances/Grants/FinanceBootstrapCommand.cs`, `lib/field-advances/grant-client.ts`, `app/portal/field-advances/settings/GrantEditor.tsx`; สร้าง `backend/src/TPR10.Api/FieldAdvances/Requests/RequestAccess.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestAccessTests.cs`; แก้ `tests/field-advance-grant.test.mjs`; migration `AddFieldAdvanceApprovalLevels` พร้อม snapshot/Designer ที่ EF สร้าง

**อินเทอร์เฟซ:** ขยาย `GrantSpec`/wire และ proposal ด้วย properties ระดับทั้งสี่ชุดตามหัวข้อทางเดินสิทธิ์; `RequestAccess.CheckAsync(Guid actorId, Guid requestId, string action, CancellationToken ct) : Task<FinanceDecision>` ตรวจภายใน transaction ของ caller และ `RequestSeparation.Check(Guid personId, string action, RequestParticipationHistory history, Guid[] approvedInRevision) : FinanceDecision` ในไฟล์ใหม่ `backend/src/TPR10.Api/FieldAdvances/Requests/RequestSeparation.cs`

actionของ RequestAccess จำกัด `read`/`edit`/`send-to-owner`/`confirm`/`return-to-author`/`review`/`revise`/`withdraw`/`copy` ไม่รับค่าอื่น ส่วน RequestSeparation ใช้ `request.approve`/`finance.check`/`payment.record` เดิม; createยังไม่มีrequestIdจึงตรวจ FinanceAccess ตรงscopeและowner eligibility ภายในDraftService ไม่เปิดทาง CheckAsync ด้วยGuid.Empty

- [x] เขียน `Only_explicit_project_grants_allow_create_or_delegate`, `One_person_alias_cannot_self_approve`, `Previous_revision_approver_is_allowed_but_cannot_check_or_pay`, `One_source_must_cover_level_amount_and_period`; assertion `Allowed == false` สำหรับ admin/ทีม/ข้ามโครงการ/รวม grant, `Allowed == true` สำหรับผู้อนุมัติฉบับก่อนที่ไม่ซ้ำฉบับปัจจุบัน
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRequestAccessTests` และ `node --test tests/field-advance-grant.test.mjs` ให้แดงตรงความสามารถ/level ใหม่
- [x] เพิ่ม capability สองตัวและ field ระดับตามข้อกำหนดทางเทคนิค ปรับ validation จำนวน capability เดิมที่จำกัด 10 ให้รองรับรายการใหม่ครบ ไม่แก้เป็นรับ string อิสระ คุมให้ต่อระดับไม่เกินแหล่งอำนาจ ไม่ตีความ null limit ว่าไม่จำกัด
- [x] เพิ่ม UI เสนอ/ตรวจข้อมูลระดับและการแต่งตั้งหลักพร้อม wire/read serialization; การให้ต่อ PrimaryApprovalLevels ต้องมีแหล่งที่มี DelegablePrimaryApprovalLevels ครอบคลุมด้วย ไม่อนุมานจากสิทธิ์อนุมัติทั่วไป; bootstrap รองรับเฉพาะรายการที่บริษัทรับรองใหม่ ไม่ยกระดับข้อมูลเก่า อัปเดต hash/receipt ให้รวม fields ใหม่และเทสต์ HTTP tampering; ตรวจ DB guards ใน migration เดิมแล้วต่อด้วย migration ใหม่ ไม่แก้ประวัติ migration ที่ใช้งานแล้ว
- [x] ทดสอบ revoke/regrant, assignmentเก่า levelsว่าง, source grant ที่หมดอายุ และ alias คนเดียว รันคำสั่งเดิมผ่าน พร้อมเทสต์7A Grant/Bypass/Bootstrap เพื่อไม่เปลี่ยนกฎสามคน
- [x] ผู้ตรวจเทียบ B01/B06/B07/B14 ตรวจชุดร่วม และ commit: `feat: scope request access and approval levels`

ผล Task 3: commits `ae3bb9b`, `73358c9`, `6fddc5a`; ตรวจอิสระและรอบแก้ครบ Backend 2,398/2,398, Frontend 118/118, เบราว์เซอร์ UI 1/1 และ Build/Format/Lint ผ่านวันที่ 23 ตุลาคม 2569 ไม่มีข้อพบ Task นี้ค้าง รายละเอียดรวมการแก้ fixture เก่าอยู่ในรายงาน ยังไม่เริ่ม Task 4

### Task 4: ร่างคำขอและธุรกรรมคำสั่ง

**ไฟล์:** สร้าง `backend/src/TPR10.Api/FieldAdvances/Requests/RequestCommandExecutor.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/RequestDraftService.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestDraftTests.cs`; แก้ `backend/src/TPR10.Api/FieldAdvances/FieldAdvanceRegistration.cs`

**อินเทอร์เฟซ:** `RequestDraftService.CreateAsync(Guid actorId, CreateRequest input, RequestCommand meta, CancellationToken ct)` และ `UpdateAsync(Guid actorId, Guid requestId, RequestContent content, RequestCommand meta, CancellationToken ct)` ทั้งคู่คืน `Task<FinanceResult<RequestView>>`; executor เป็น internal ใช้ transaction/receipt/audit ร่วมกับ Task6/7 ไม่เปิด generic action API

- [x] เขียน `Self_and_delegate_draft_keep_actor_history`, `Duplicate_command_returns_one_request`, `Same_command_different_payload_conflicts`, `Rollback_removes_request_event_and_receipt`: assert request/event/receipt count ไม่เพิ่มเมื่อ retry และทั้งหมดเท่าเดิมเมื่อ inject failure
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRequestDraftTests` ให้แดง
- [x] ทำ create/update ตรวจ scope/owner eligibility, ช่วงวันที่ start<=end, ชนิดคำขอ, team ไม่ซ้ำ และข้อมูลรายการตาม Task1 ไม่รับ client total/creator; draft version เพิ่มเมื่อแก้ ห้ามแก้ AwaitingOwner/submitted/terminal ตรง ๆ
- [x] สร้าง payload hash รวม action/actor/scope/content/version; receipt ผูก commandกับactor/action ไม่คืนผลก่อนตรวจสิทธิ์ปัจจุบัน; บันทึก event/participant/audit/receipt ภายใน transaction เดียว ปฏิเสธรหัสเดิมกับ payload ต่างด้วย409
- [x] รัน filtered tests ผ่าน เพิ่ม concurrent create/update และ revoked-after-success retry ไม่รั่วข้อมูล บันทึกข้อผิดพลาด DB ต้องไม่ค้าง entity state ทำให้ retry รอบถัดไปปน
- [x] ผู้ตรวจเทียบ B01/B12/B13 ตรวจชุดร่วม และ commit: `feat: add transactional advance request drafts`

ผล Task 4: commits `88f6dcf`, `68a29be`; Dalton ลงมือ Carson ตรวจ และ Pascal ตรวจรอบแก้ query สิทธิ์ซ้ำให้ใช้ชุดเดียว เทสต์เฉพาะ Task 38 ผ่าน ผลสุดท้าย Backend 2,436/2,436, Frontend 118/118 และ Build/Format/Lint ผ่าน ไม่มีข้อพบ Task นี้ค้าง ไม่เริ่ม Task 5

### Task 5: เลือกผู้อนุมัติและหาทางพักอย่างปลอดภัย

**ไฟล์:** สร้าง `backend/src/TPR10.Api/FieldAdvances/Requests/ApprovalRouteResolver.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/ApprovalCandidateReader.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/ApprovalRouteContracts.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestRouteTests.cs`

**อินเทอร์เฟซ:** `RouteChoice(Guid? PersonId, Guid? AccountId, string? HoldCode, Guid[] TraversedPersonIds)`; `ApprovalRouteResolver.ResolveAsync(Guid requestId, Guid revisionId, string levelCode, CancellationToken ct) : Task<RouteChoice>` ภายใต้ transaction caller; ใช้ `ApprovalLevelEvaluator.Evaluate` เดิมกำหนดลำดับ ไม่เปลี่ยนผลนั้น

- [x] เขียน `Resolves_owner_supervisor_not_delegate_supervisor`, `Same_person_escalates_without_skipping_level`, `High_limit_without_level_denied`, `Cycle_or_ambiguous_role_holds`: assert Level sequence ยังเดิม, PersonId เป็นผู้มีสิทธิ์ขั้นเดิม, HoldCode ไม่ว่างเมื่อวงวน/ไม่มีผู้เหมาะสม
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRequestRouteTests` ให้แดง
- [x] อ่าน ReportingLine/employee membership ที่ยังมีผลและแปลงเป็น person ผ่าน mapping ที่รับรอง; อ่าน grantsปัจจุบันโดยไม่ใช้ session ของ candidateเป็น actor ของ caller ตรวจ user active, scope, levels, limit, ช่วงเวลาและข้อห้าม
- [x] เริ่มต้น supervisor จากหัวหน้าของ owner; PM/ระดับเพิ่มจาก PrimaryApprovalLevels ปัจจุบัน ถ้าหลายคนต่างpersonกำกวมให้พัก; เมื่อติดข้อห้ามไล่หัวหน้าของผู้สมัครเดิมโดยมี visited set และตรวจ ApprovalLevels ของขั้นเดิม ไม่มี fallback admin; เทสต์หลักหนึ่งคนกับผู้รับช่วงหนึ่งคนต้องไม่ถูกมองว่ามีหลักสองคน
- [x] รันผ่านพร้อมเคสวงเงินตรงขอบ/ต่ำกว่า0.01, aliasหลายบัญชีคนเดียว, grantหลายแหล่งไม่รวมวงเงิน, สิทธิ์หมดอายุตรงเวลา และขอบเขตต่างโครงการ ไม่ mutate request ใน resolver
- [x] ผู้ตรวจเทียบ B05/B06/B08 ตรวจชุดร่วม และ commit: `feat: resolve current advance approval routes`

ผล Task 5: commit `3d459b0`; Beauvoir ลงมือและ Sartre ตรวจอิสระ ผ่านโดยไม่มีข้อพบเพิ่มเติม เทสต์ routing 41 กรณีอยู่ใน regression 238 ที่ผ่าน ผลสุดท้าย Backend 2,477/2,477, Frontend 118/118 และ Build/Format/Lint ผ่านครบ รายละเอียด RED แยกข้อผิดพลาด fixture ไว้ในรายงาน ยังไม่เริ่ม Task 6

ข้อกำหนดต่อ Task 6–7: caller ถือ transaction และ identity lock `7241002` ตลอดคำสั่ง บันทึก frozen snapshot/total ก่อน resolve และบันทึกเนื้อหา/เส้นทางก่อน SubmittedAtUtc; การลงผล มอบหมายใหม่ พัก/กลับมาทำขั้นเดิมและ audit ยังเป็นงานเชื่อมใน Task ถัดไป

### Task 6: ยืนยันผู้รับผิดชอบและตรึงฉบับที่ส่ง

**ไฟล์:** สร้าง `backend/src/TPR10.Api/FieldAdvances/Requests/RequestSubmissionService.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestSubmissionTests.cs`; แก้ `backend/src/TPR10.Api/FieldAdvances/FieldAdvanceRegistration.cs`

**อินเทอร์เฟซ:** `RequestSubmissionService.QuoteAsync(Guid actorId, Guid requestId, CancellationToken ct) : Task<FinanceResult<ConfirmationQuote>>`; `SendToOwnerAsync(Guid actorId, Guid requestId, RequestCommand meta, CancellationToken ct)` และ `ConfirmAsync(Guid actorId, Guid requestId, string confirmationHash, RequestCommand meta, CancellationToken ct)` คืน `Task<FinanceResult<RequestView>>`

- [x] เขียน `Delegate_cannot_confirm`, `Policy_or_content_changed_requires_new_quote`, `Override_keeps_approved_company_basis`, `Confirmation_freezes_all_steps`: assert409เมื่อhashเปลี่ยนและไม่มีsnapshotใหม่; confirmสำเร็จเก็บ policy IDs/hash/ยอด/ทุกstep ครบในธุรกรรมเดียว
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRequestSubmissionTests` ให้แดง
- [x] Quote อ่านผ่าน RequestAccess และ `PolicyResolver.ResolveAuthorizedAsync` ภายใต้lock; hashรวม revision/version/content/estimate/policyIDs/contenthash; ไม่ให้ผู้ขออ่าน management endpoint ทั้งชุด
- [x] SendToOwner เปลี่ยน Draftเป็นAwaitingOwner; Confirm เฉพาะowner (selfใช้Draftได้) ตรวจ quoteใหม่และสิทธิ์ปัจจุบันก่อน freeze สร้างทุกขั้นตามยอดโดยรอทีละขั้น; หากกติกาไม่มี/ยอดเกินทุกbandให้แจ้งแก้ก่อนส่ง หากมีขั้นแต่ไม่มีคนให้ส่งแล้วเป็นHeld พร้อมเหตุผล
- [x] รันเทสต์ผ่าน เพิ่มpolicy activationชนconfirm, content editชนconfirm, snapshot immutability และaudit failure ไม่มีpartial submission
- [x] ผู้ตรวจเทียบ B01/B04/B05/B13 ตรวจชุดร่วม และ commit: `feat: confirm and freeze advance request submissions`

ผล Task 6: commit `fa5c499`; Helmholtz ลงมือและ Socrates ตรวจอิสระ ผ่านโดยไม่มีข้อพบค้าง เทสต์เฉพาะ 24/24, regression ที่เกี่ยวข้อง 249/249 ผลสุดท้าย Backend 2,501/2,501, Frontend 118/118 พร้อม Build/Format/Lint ผ่านวันที่ 23 ตุลาคม 2569 ยังไม่เริ่ม Task 7

ข้อกำหนดส่งต่อ: Task 7 ลงผล/ประเมินใหม่เฉพาะขั้นปัจจุบันภายใต้ lock เดิม ใช้ snapshot ที่ตรึงไว้ ไม่ข้ามขั้นที่ยังไม่มีผู้รับงาน; Task 9 ต้องจับคู่รายละเอียดและ quote ด้วย request/revision/version เมื่อได้409ให้ทบทวนใหม่ และหลัง replay ให้อ่านสถานะปัจจุบันใหม่ ไม่ใช้ receipt เป็นสถานะล่าสุดโดยอัตโนมัติ

### Task 7: ลงผล ฉบับแก้ไข ถอน และประเมินผู้รับงานใหม่

**ไฟล์:** สร้าง `backend/src/TPR10.Api/FieldAdvances/Requests/RequestReviewService.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/RequestRevisionService.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/RequestRoutingWorker.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestLifecycleTests.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestRaceTests.cs`; แก้ Registration และ `RequestDraftService.cs`

**อินเทอร์เฟซ:** `RequestReviewService.DecideAsync(Guid actorId, Guid requestId, RequestDecision decision, RequestCommand meta, CancellationToken ct)`; `RequestRevisionService.ReturnToAuthorAsync`, `ReviseAsync`, `WithdrawAsync`, `CopyAsync` ใช้ `(Guid actorId, Guid requestId, RequestCommand meta, CancellationToken ct)` ทั้งหมดคืน `Task<FinanceResult<RequestView>>`; `RequestReviewService.ReevaluateAsync(Guid requestId, CancellationToken ct) : Task<bool>` เป็น internal worker API ไม่มีทางอนุมัติ/เพิ่มสิทธิ์

- [x] เขียน `Returned_revision_restarts_all_steps`, `Prior_approver_can_vote_again_only_once`, `Owner_only_can_withdraw_before_final`, `Final_approval_races_with_withdrawal`, `Reevaluation_is_idempotent`: assertมีterminalหนึ่งทาง, effect/auditไม่ซ้ำ, ฉบับเก่า/ข้อห้ามเดิมไม่หาย
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter 'FullyQualifiedName~FieldAdvanceRequestLifecycleTests|FullyQualifiedName~FieldAdvanceRequestRaceTests'` ให้แดง
- [x] Decideตรวจผู้รับงาน/step/version/สิทธิ์ใหม่ก่อนลงผล Return/Rejectต้องreason; Approveไปขั้นถัดไปหรือApproved ไม่เรียกจ่ายเงิน ReturnToAuthorเฉพาะownerตอนAwaitingOwner คืนDraftพร้อมเหตุผล; ReviseเฉพาะReturnedสร้างฉบับDraftใหม่ไม่ยกvotes
- [x] Withdraw ownerเท่านั้นก่อนApprovedและไม่ใช่Rejected/Withdrawn ปิดpendingstepsพร้อมประวัติ; CopyเฉพาะRejected/Withdrawnที่ยังอ่านได้ สร้างIDใหม่/เชื่อมต้นทาง/ตรวจสิทธิ์ยื่นและownerใหม่ตามข้อมูลเดิม ไม่copyผล; APIไม่มีแก้Approvedตรง
- [x] Reevaluateเก็บsnapshot/ผลก่อนหน้า ใช้resolverปัจจุบันเฉพาะขั้นค้าง เมื่อเปลี่ยนจริงจึงเพิ่มversion/audit; worker `BackgroundService` ใช้ scopeใหม่ต่อrequest, default interval30วินาที batch50 กำหนดค่าได้ วนแบบcursorไม่อดตายที่หน้าแรก ทดสอบด้วยTimeProvider ไม่sleepจริง และตรวจซ้ำในทุกคำสั่งก่อนลงผล; auditระบุsystem re-evaluationตามรูปแบบระบบ ไม่ปลอมเป็นผู้อนุมัติและไม่มีสิทธิ์ส่งคำขอหรืออนุมัติแทน
- [x] รันfilteredผ่าน เพิ่มสองworkerแข่ง, revokeชนapprove, reassignmentชนwithdraw, rollbackหลังreceipt/audit, terminalไม่ถูกworkerเปิดกลับ; ไม่มีjobแก้7Cpayment hold
- [x] ผู้ตรวจเทียบ B07–B13 ตรวจชุดร่วม และ commit: `feat: complete advance request approval lifecycle`

### Task 8: API และ client ที่ไม่รั่วสิทธิ์

ข้อผูกพันจาก Task 7: history ต้องรวม `AdvanceRequestEvent` ของบุคคลกับ `audit_events` action `field-advance.request.reevaluate` ของระบบ หลังตรวจสิทธิ์อ่านคำขอ โดยคง revision/version/step และค่าก่อน–หลังใน metadata ไม่เปิด audit ทั้งระบบให้ผู้ขอ และไม่แสดงเหตุการณ์ระบบเป็นการกระทำของเจ้าของหรือผู้อนุมัติ คิวงานต้องตรวจสถานะคำขอ/ฉบับปัจจุบัน/ขั้นตามลำดับ ไม่ใช้ `Outcome=null` อย่างเดียว เพราะขั้นที่ถูกปิดด้วยการถอนหรือส่งกลับยังไม่มีผลอนุมัติ

**ไฟล์:** สร้าง `backend/src/TPR10.Api/FieldAdvances/Requests/RequestReadService.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/RequestEndpoints.cs`, `backend/src/TPR10.Api/FieldAdvances/Requests/RequestHttpContracts.cs`, `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestHttpTests.cs`, `lib/field-advances/request-types.ts`, `lib/field-advances/request-client.ts`, `tests/field-advance-request.test.mjs`; แก้ Registration และ `backend/src/TPR10.Api/FieldAdvances/FieldAdvanceOpenApiTransformer.cs`

**อินเทอร์เฟซ:** ภายใต้ `/api/v1/field-advances/requests` เปิด GET `/scopes`, `/people`, `/`, `/review-queue`, `/{id}`, `/{id}/history`, `/{id}/quote`; POST `/`, `/{id}/send-to-owner`, `/{id}/confirm`, `/{id}/return-to-author`, `/{id}/review`, `/{id}/revise`, `/{id}/withdraw`, `/{id}/copy`; PUT `/{id}` สำหรับdraftเท่านั้น

`RequestReadService.ListAsync(Guid actorId, FinanceScope scope, string mode, int page, int pageSize, CancellationToken ct) : Task<FinanceResult<RequestView[]>>`, `DetailsAsync(Guid actorId, Guid id, CancellationToken ct) : Task<FinanceResult<RequestView>>`; mode=`mine`/`delegated`/`review`/`report` (reportต้องสิทธิ์แยก ไม่ใช่dashboardใหม่); history DTOใช้ eventId/revisionId/action/actor/time/reason; scopesและpeopleคืนเฉพาะID/nameที่มีสิทธิ์เลือก ไม่เปิดรายชื่อทุกบริษัท

- [x] เขียน HTTPเทสต์ `Direct_url_cannot_bypass_scope`, `No_financial_access_for_admin_or_team`, `Json_number_cannot_bypass_precision`, `Commands_require_mfa_csrf`; assert401/403ตามmiddleware, ไม่มีเนื้อหาหรือcountข้ามscope และไม่มีside effect
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRequestHttpTests` และ `node --test tests/field-advance-request.test.mjs` ให้แดง
- [x] ต่อAPIเข้าบริการไม่ย้ายbusinessrulesเข้าหน้าเว็บ เงินออกเป็นstring; ปฏิเสธunknown fieldsที่อ้างสิทธิ์/ยอด; pagingมีลำดับคงที่และpageSize1–100; history/scopes/peopleมีขอบเขตและlimitเช่นเดียวกับlist ไม่โหลดทั้งระบบแล้วค่อยกรองในbrowser
- [x] client `createRequestClient(userId: string, transport: typeof fetch = fetch)` คืน `list`, `details`, `quote`, `create`, `update`, `sendToOwner`, `confirm`, `returnToAuthor`, `review`, `revise`, `withdraw`, `copy`, `history`, `scopes`, `people` ตรงAPIข้างต้น ใช้ request-types mirrored contracts เงินstring/วันที่ISO; ทุกcallรับAbortSignal ตรวจบัญชีก่อน/หลังresponseและCSRFเหมือนgrant-client
- [x] เพิ่ม clientเทสต์ `Drops_response_after_account_switch`, `Unknown_result_keeps_original_command_id`, `Large_money_remains_string`; retryไม่สร้างIDใหม่จนทราบผลเดิม 409โหลดใหม่ 401/403ล้างข้อมูลที่ป้องกัน ตรวจreceiptยังต้องมีสิทธิ์อ่าน
- [x] รันfilteredและNodeผ่าน เพิ่มOpenAPIcontractเก่าไม่ถดถอย ผู้ตรวจเทียบ B02/B12/B14/B15 ตรวจชุดร่วม แล้ว commit: `feat: expose scoped advance request API`

### Task 9: หน้าจอใช้งานภาษาไทย

ข้อยืนยันผู้ใช้ 23 ตุลาคม 2569: ผู้มีสิทธิ์ยื่นคำขอในโครงการค้นหาเฉพาะชื่อพนักงานที่ยังทำงานในบริษัทเดียวกันเพื่อเลือกสมาชิกทีมได้ คืนเฉพาะชื่อและ ID ที่จำเป็นต่อการเลือก ไม่เปิดข้อมูลส่วนบุคคลอื่น ไม่เพิ่มสิทธิ์ดูคำขอหรือข้อมูลการเงินแก่ผู้ยื่นหรือสมาชิกทีม ตรวจสิทธิ์ปัจจุบันและขอบเขตบริษัทฝั่งเซิร์ฟเวอร์ พร้อมจำกัดผลค้นและแบ่งหน้า

ข้อผูกพันจาก Task 8: `/people` เป็น owner picker ไม่ใช่รายชื่อทีมทั้งหมด ต้องจัดแหล่งเลือกสมาชิกทีมที่ตรวจสิทธิ์เหมาะสมโดยไม่เพิ่มสิทธิ์การเงิน ใช้ current หลังคำสั่ง ไม่ใช้ receipt เก่าเป็นสถานะหน้าจอ และเก็บ command ID/payload เดิมเฉพาะบริบทบัญชีผู้ส่งสำหรับ reconciliation เมื่อยังไม่ทราบผล พร้อมล้างข้อมูลป้องกันเมื่อเปลี่ยนบัญชีหรือพบ 401/403

**ไฟล์:** สร้าง `app/portal/field-advances/page.tsx`, `app/portal/field-advances/new/page.tsx`, `app/portal/field-advances/[requestId]/page.tsx`, `app/portal/field-advances/approvals/page.tsx`, `app/portal/field-advances/RequestEditor.tsx`, `app/portal/field-advances/RequestDetails.tsx`, `app/portal/field-advances/RequestQueue.tsx`, `lib/field-advances/request-view.ts`, `tests/e2e/field-advance-request.spec.ts`; แก้ `app/portal/page.tsx`, `tests/field-advance-request.test.mjs`, `backend/tests/TPR10.E2E.Fixture/FieldAdvanceSeed.cs`, `infra/nginx/https-smoke-fixture.mjs`

**อินเทอร์เฟซ:** ใช้client Task8; view helpers `requestStateLabel(state: string): string`, `diffRequestContent(previous: RequestContent, current: RequestContent): string[]`; component props ใช้ `RequestView`/`ConfirmationQuote` ไม่สร้างdomain rulesชุดที่สอง

- [x] เขียนPlaywright flowแยกowner/delegate/approver: draft→ownerconfirm→ทุกstep→Approved; return→revise→confirmใหม่; withdraw พร้อมreason; assertข้อความ “อนุมัติแล้ว รอดำเนินการจ่าย” และไม่มีปุ่มจ่าย
- [x] เพิ่มspecใหม่ในallowlistของ HTTPS fixture และseedเฉพาะฐานdisposable ไม่มีข้อมูลจริง; รันคำสั่งHTTPSด้านล่างที่4001 ต้องแดงเพราะยังไม่มีหน้า/flow
- [x] ทำรายการmy/delegatedและqueue, editorคงrawstringและแสดงserverquote, detailsแสดงความต่าง/snapshot/history/hold reason; confirm/withdrawมีdialogทบทวน เหตุผลบังคับที่server; ไม่เลือกผู้อนุมัติเองและไม่แสดงหน้าหลังapprovedเป็นแบบแก้ตรง
- [x] ทดสอบdwellหลังทุกcommandและpoll15วินาทีด้วยเวลาเทสต์ หน้าไม่แจ้งconflictเทียมจากversionเก่า; change-account/sessionหมดอายุทิ้งresponseและข้อมูลค้าง; unknown network resultแสดงไม่ทราบผลก่อนretry ห้ามแสดงสำเร็จโดยเดา
- [x] รันNode/view testsและHTTPS flowทั้ง4000/4001ผ่าน รวมไม่มีสิทธิ์เปิดตรง/มือถือจำลองviewport (ไม่แทนมือถือจริง) ผู้ตรวจเทียบ B01/B10/B11/B14/B15 ตรวจชุดร่วม แล้ว commit: `feat: add Thai advance request screens`

คำสั่งHTTPSหลังเพิ่มallowlist ใช้fixtureเดิม ไม่ติดตั้งCAลงเครื่องหรือปิดcertificate validation:

```bash
node --input-type=module -e 'import { runHttpsSmoke } from "./infra/nginx/https-smoke-fixture.mjs"; await runHttpsSmoke({port:"4000",spec:"tests/e2e/field-advance-request.spec.ts",e2e:true});'
node --input-type=module -e 'import { runHttpsSmoke } from "./infra/nginx/https-smoke-fixture.mjs"; await runHttpsSmoke({port:"4001",spec:"tests/e2e/field-advance-request.spec.ts",e2e:true});'
```

### Task 10: ตรวจรวมและเตรียมส่งมอบ

**ไฟล์:** สร้าง `backend/tests/TPR10.Api.IntegrationTests/FieldAdvanceRequestDeliveryTests.cs`, `docs/reports/2026-10-22-module-7b-verification.md`, `docs/runbooks/module-7b-requests-approvals.md`; แก้เทสต์Task7/9เมื่อพบช่องว่าง และอัปเดตสถานะแผนนี้ตามผลจริง

**อินเทอร์เฟซ:** ใช้public APIทั้งหมดและmigration ที่Task2/3สร้าง ไม่เพิ่มช่องทางข้ามสิทธิ์สำหรับdelivery; รายงานบันทึกcommit/คำสั่ง/exit code/จำนวนเทสต์/ข้อจำกัด/ความเสี่ยง

- [x] เขียนdelivery testsอ้างB01–B16: selfและdelegateต่างproject, revisionใหม่ใช้กติกาใหม่, whole-request SoD, ไม่มีผู้อนุมัติ→แก้สิทธิ์→ขั้นเดิม, revoke และrollbackผ่านHTTP; assertไม่มีpartial commit ไม่มีpayment rows และประวัติเดิมเท่าเดิม
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRequestDeliveryTests` เพิ่มregressionแดงก่อนแก้ข้อพบ ไม่สร้างการแก้dummyเพื่อให้ได้Redถ้าflowเดิมถูกอยู่แล้ว
- [x] ผู้ตรวจอิสระตรวจทั้งbranchเทียบspec/plan/DBguards/authorization/locks/receipt/replay เก็บข้อพบและแก้พร้อมregression; Critical/Importantต้องปิดก่อนส่งมอบ Minorใหม่ต้องแจกแจงไม่รวมเงียบกับM2/M3
- [x] ทดสอบmigrationupจากฐาน7Aสำเนาdisposable ตรวจจำนวนข้อมูลเดิมและrollbackของtransaction; ออกidempotent SQLด้วย `dotnet ef migrations script --idempotent --project backend/src/TPR10.Api` ไม่รันบนฐานผู้ใช้ ไม่เสนอdropข้อมูลคำขอเป็นrollbackผลิตจริง
- [x] รันชุดตรวจร่วมครบหลังแก้สุดท้าย พร้อมHTTPSTask9ทั้งสองพอร์ต และHTTPS7A policy/grant regressionผ่านfixtureเดิม ตรวจaccount/MFA/attendanceจากsuiteรวม ไม่มีการใช้ผลก่อนแก้มาอ้าง
- [x] เขียนrunbookขั้นตั้งสิทธิ์ยื่น/ระดับที่ผ่านสามคน จัดการHeldโดยแก้โครงสร้าง/สิทธิ์ ไม่overrideคำขอ วิธีตรวจunknowncommand วิธีสำรอง/restoreก่อนmigration และข้อจำกัดยังไม่มี7C/7D; ไม่ใส่บัญชีจริง/รหัสผ่าน/คีย์
- [x] ลงผลM2/M3เป็น“ยังติดตาม” มือถือจริง/NASจริงเป็น“พัก ยังไม่ทดสอบ” และหลักฐานperformanceของpathใหม่โดยไม่อ้างปิดM2เดิม
- [x] ตรวจรายงานกับผลจริงแล้ว commit: `test: verify module 7b delivery` การpush/PR/mergeหรือเปิดใช้เงินจริงต้องรอคำสั่งผู้ใช้ ไม่รวมอยู่ในTask10

## ตารางความครอบคลุมแบบและแผน

| เกณฑ์ | Task หลัก |
|---|---|
| B01 ยื่นเอง/แทน/ผู้รับผิดชอบ | 3,4,6,9 |
| B02–B03 การคำนวณและอัตรา | 1,8 |
| B04 ฉบับและกติกา | 2,6,7 |
| B05–B06 ทุกขั้น/ต่างคน/ขอบเขต | 3,5,6 |
| B07 ประวัติข้ามฉบับและข้ามหน้าที่ | 3,7 |
| B08–B09 สิทธิ์เปลี่ยน/พัก/คืนขั้น | 5,7 |
| B10–B11 ส่งกลับ/ปฏิเสธ/ถอน/คัดลอก | 7,9 |
| B12–B13 concurrency/idempotency/rollback | 4,6,7,8,10 |
| B14 การไม่รั่วข้อมูล | 3,8,9 |
| B15 หน้าจอ/ผลคำสั่ง/ข้อมูลล่าสุด | 8,9 |
| B16 การถดถอยระบบเดิม | ชุดร่วมทุกTask และ10 |

## ผลตรวจแผนและการอนุมัติก่อนเริ่ม

- ครอบคลุมส่วนที่1–5และB01–B16; แยกscope7C/7DและMinorเดิมออกจากงานที่อ้างว่าส่งมอบ
- มีชื่อไฟล์ สัญญาบริการ และเทสต์เฉพาะในทุกTask; migrationใช้ชื่อที่ระบุแต่ให้EFสร้างtimestampจริง ไม่สร้างชื่อวันที่ปลอม
- จุดเสี่ยงทั้งห้าผูกกับเทสต์Taskที่รับผิดชอบแล้ว ไม่ให้metadata/alias/retryข้ามสิทธิ์
- ข้อมูลโครงการ/ผู้รับผิดชอบเป็นตัวตนคำขอ และข้อมูลระดับอนุมัติผ่านgrantสามคน เป็นรายละเอียดการลงมือที่ต้องตรวจพร้อมแผนนี้ก่อนเริ่ม
- ผู้ใช้อนุมัติแผนและสั่ง Task 1–10 แยกตามลำดับแล้ว ผู้ลงมือและผู้ตรวจแยกกันทุก Task ผลสุดท้ายผ่านครบตามรายงาน คง Minor และข้อจำกัดที่ระบุ ไม่รวมการ push, PR, merge หรือเปิดใช้เงินจริง

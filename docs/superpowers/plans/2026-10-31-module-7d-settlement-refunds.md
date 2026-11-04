# Module 7D Implementation Plan — แผนพัฒนาเคลียร์ค่าใช้จ่าย คืนเงิน และปิดคำขอ

> **สำหรับผู้ดำเนินงาน:** REQUIRED SUB-SKILL: ใช้ `superpowers:subagent-driven-development` แยกผู้ลงมือกับผู้ตรวจใหม่ทุก Task ตามวิธีที่ผู้ใช้เลือกไว้ ห้ามแทนผู้ตรวจด้วยการตรวจของผู้ลงมือเอง หากเครื่องมือไม่รองรับให้แจ้งก่อนเปลี่ยนเป็น `superpowers:executing-plans` ติดตามด้วยช่อง `- [ ]`

**เป้าหมาย:** ต่อวงจรเงินหลัง 7C ให้รับรองค่าใช้จ่าย รับคืน แก้รายการและสะสางเหตุจ่ายผิดอย่างตรวจย้อนหลังได้ แล้วปิดคำขอเมื่อไม่มีภาระค้าง

**สถาปัตยกรรม:** เพิ่มโมดูล Settlement แยกจากรายการจ่ายเดิม ใช้ธุรกรรมและ family lock ร่วมกับ 7B–7C รายการที่รับรองแล้วคงประวัติและใช้ผลต่างที่ตรวจแล้วคำนวณยอดปัจจุบัน แยกวงเงิน ภาระเจ้าของ เงินผิดผู้รับ และยอดรอตรวจ ไม่ให้การคืนเงินทำให้เปิดวงเงินหรือจ่ายใหม่อัตโนมัติ

**เทคโนโลยี:** ASP.NET Core/.NET 10, EF Core 10/Npgsql/PostgreSQL, Next.js 15/React 19/TypeScript, xUnit/Testcontainers และ Playwright ตาม lockfile เดิม ไม่เพิ่ม workflow framework หรือเชื่อมธนาคาร

**เอกสารออกแบบ:** [แบบ 7D ที่ผู้ใช้อนุมัติ](../specs/2026-10-31-module-7d-settlement-refunds-design.md) ฉบับ commit `9e45ec8` ผู้ลงมือและผู้ตรวจต้องอ่านแบบกับแผนร่วมกัน

**สถานะแผน:** Task 1–18 ผ่านการตรวจรับตามขอบเขตแล้ว รวมผู้ตรวจอิสระและ Test/Build/Lint ครบ คงรายการติดตามและพักมือถือจริง/NAS จริง ไม่มี push/merge

## ข้อกำหนดร่วม (Global Constraints)

- ใช้เงินบาทเท่านั้น จำนวนไม่เกิน 4 ตำแหน่ง อัตรา/ยอดเหมารวมไม่เกิน 2 ตำแหน่ง ปัดแต่ละรายการเป็นสตางค์ หลักที่สามตั้งแต่ 5 ปัดขึ้น แล้วรวม ไม่ปรับข้อมูลเกินตำแหน่งเงียบ ๆ
- เคลียร์และคืนแยกคำขอ ไม่ย้ายยอดจ่ายเดิม ไม่ใช้ใบเสร็จข้ามคำขอแม้เป็นส่วนเพิ่ม แสดงภาพรวมเฉพาะที่มีสิทธิ์และไม่รั่วผ่านผลรวม
- รอบค่าใช้จ่าย: เจ้าของยืนยัน → หัวหน้า → ผู้จัดการโครงการ → ผู้ตรวจการเงิน คนละคนในแต่ละขั้นและใช้สิทธิ์ปัจจุบัน ส่งกลับเริ่มอนุมัติรอบนั้นใหม่ทั้งหมด
- คงประวัติตัวบุคคลทั้งชุด ผู้ยื่นแทน/เจ้าของไม่อนุมัติ ตรวจ หรือจ่ายของตนเอง ผู้อนุมัติ/ผู้ตรวจ/ผู้จ่ายไม่สลับหน้าที่ ไม่มีสิทธิ์การเงินอัตโนมัติจาก admin หรือสมาชิกทีม
- เอกสารรับรอง รอตรวจ และส่งกลับที่ยังกันยอด รวมกันต้องไม่เกินยอดใบ ลดรายการคืนเฉพาะส่วนต่าง; การแก้ลดรอบที่ผ่านแล้วไม่คืนยอดใบจนผลแก้ผ่าน
- กำหนดเป็นวันปฏิทิน วันถัดจากวันจบงานเป็นวันที่ 1; วันคืนเงินตรงวันเคลียร์ ใช้ Asia/Bangkok เก็บกำหนด/ประวัติเดิม แยกส่งทันรอตรวจออกจากรับรองสำเร็จ
- เงินคืนรอตรวจไม่ใช่รับคืนสำเร็จ หลังรับรองแก้ด้วยรายการใหม่ ก่อนปิดต้องไม่มีรายการค้าง หลังปิดใช้เคสแยกไม่เปิดคำขอเดิม
- เจ้าของรับผิดเฉพาะเงินจริงที่ได้รับ เงินผิดคนติดตามแยก คืนไม่ครบคงเหตุไว้ ปลดเฉพาะเหตุที่ตรวจแล้ว ไม่ล้างเหตุอื่น และงวดส่วนขาดต้องตรวจใหม่
- ส่วนเพิ่มที่มีอยู่ก่อนหลักปิดเดินต่อได้ รวมส่งแก้ แต่ห้ามสร้างใหม่หลังหลักปิด ต้องตัดสิน create/close ด้วย transaction ไม่เชื่อเวลาจากผู้ใช้
- ใบเดียวเกินวงเงินพักส่วนเกินให้การเงินพิจารณาภายนอก ไม่รับรอง ไม่โยนภาระพนักงาน และไม่ล้างข้อค้างด้วยหมายเหตุอย่างเดียว
- ภาษาผู้ใช้และเอกสารเป็นไทย dev 4000, production preview 4001, HTTPS 4443; ไม่ลด TLS/session/MFA ไม่พิมพ์ secrets/logข้อมูลการเงินจริง
- คง M2-R1/R2-C1, HTTPS 504, Minor log, OpenAPI 500, T13-M1/T14-M1 พักมือถือจริง/NAS จริง ไม่รวม deploy/migrateข้อมูลจริง/push/mergeโดยปริยาย

## จุดเน้นสำหรับผู้ตรวจ (Review Focus)

1. เจ้าของแนบใบเสร็จแต่ถูกลงประวัติ Payer จากบริการเดิม — Task 4 ต้องพิสูจน์อัปโหลดได้โดยไม่มี payment.record และไม่มี Payer participation
2. คำขอหลักปิดระหว่างส่วนเพิ่มถูกส่งกลับ — Task 13 ต้องส่งใหม่ได้เฉพาะส่วนเพิ่มที่ commit ก่อนปิด และ creation ใหม่แข่งแล้วแพ้ต้องไม่เกิด
3. แก้ยอดคืนหลังปิดสองครั้งด้วยเวอร์ชันเก่า — Task 9 ต้อง conflict ครั้งที่ใช้ฐานเก่า ไม่หักผลต่างซ้ำและไม่เปิด State เดิม
4. เอกสารสำเนาต่างไฟล์ถูกส่งสองโครงการพร้อมกัน — Task 4–5 ต้องพักซ้ำ/กันยอดโดยไม่เผยคำขออีกฝั่ง ไม่เชื่อ hashอย่างเดียว
5. พบจ่ายผิดก่อนและหลัง Paid แล้ว retry/เรียกคืนบางส่วน — Task 11–12 ต้องได้ภาระสุทธิเท่ากัน ไม่มี automatic capacity restoration หรือจ่ายสองครั้ง

## ภาพรวม 18 Task

| Task | ผลงานที่ตรวจรับได้ | ต้องผ่านก่อน |
|---|---|---|
| 1 | โครงสร้างข้อมูลและสัญญายอดที่รักษาประวัติ | แบบอนุมัติ |
| 2 | กติกาจำนวนวันแก้ไขและความเข้ากันได้ 7A | 1 |
| 3 | สิทธิ์ คำสั่งกันซ้ำ และธุรกรรมร่วม | 1 |
| 4 | หลักฐานสำหรับผู้ยื่นและตรวจเอกสารซ้ำ | 3 |
| 5 | ร่างค่าใช้จ่ายและการกันยอดเอกสาร | 2,4 |
| 6 | ยืนยันและอนุมัติรอบค่าใช้จ่าย | 5 |
| 7 | จบงาน ส่งครบ และกำหนดเวลา | 2,6 |
| 8 | ทยอยคืนเงินและตรวจรับคืน | 6 |
| 9 | แก้เงินคืนและเคสหลังปิด | 8 |
| 10 | ปรับปรุงค่าใช้จ่ายที่รับรองและส่วนเกิน | 6,9 |
| 11 | สะสางจ่ายผิดและแยกภาระเงินจริง | 3,8 |
| 12 | ตรวจงวดส่วนขาดและเชื่อมบัญชีกันวงเงิน | 11 |
| 13 | ส่วนเพิ่มเดิมหลังหลักปิด | 3 |
| 14 | ปิดอัตโนมัติและสรุปยอดตามสิทธิ์ | 7–13 |
| 15 | API และ client พร้อม guardการส่งข้อมูล | 1–14 |
| 16 | หน้าพนักงานส่งเคลียร์ คืนเงิน และติดตาม | 15 |
| 17 | หน้าผู้อนุมัติ/การเงินและรายการปรับปรุง | 15–16 |
| 18 | ตรวจรวม migration/rollback/HTTPS และคู่มือ | 1–17 |

ทำตามลำดับในตารางเพื่อรักษาสัญญาร่วม แม้บาง Task มี dependencyน้อยกว่า ไม่ให้ผู้ลงมือหลายคนแก้ context/schema พร้อมกัน เมื่อจบแต่ละ Task ต้องตรวจแยกผู้ลงมือ–ผู้ตรวจ แก้ Critical/Important ก่อนเดินต่อ และ commit เฉพาะไฟล์งานนั้น

## ฐานงานและแผนที่ไฟล์

โค้ด 7C บน main คือ `9435f97` (treeเดียวกับ `f85d14b`); แบบ7Dฉบับอนุมัติอยู่ที่ `9e45ec8` บน worktreeเดิม ผู้ดำเนินงานต้องตรวจ HEAD/AGENTS ใหม่ก่อนเริ่ม แผนนี้ไม่ให้ reset หรือรวบ 14 paths งาน HTTPS504/รายงาน7Bที่ค้างเข้ามา ต้องเลือก checkoutที่มีทั้งโค้ดฐานและแบบอนุมัติโดยรักษางานเดิม

พาธย่อขยายตรงตัว ไม่ใช่ตัวแปร shell:

- `FA/` = `backend/src/TPR10.Api/FieldAdvances/`
- `ST/` = `backend/src/TPR10.Api/FieldAdvances/Settlements/` (ใหม่)
- `DB/` = `backend/src/TPR10.Api/Data/`
- `IT/` = `backend/tests/TPR10.Api.IntegrationTests/`
- `UI/` = `app/portal/field-advances/`
- `CLIENT/` = `lib/field-advances/`

Task 1 เป็นเจ้าของ `FA/Data/SettlementEntities.cs`, `SettlementModelConfiguration.cs`, `ST/SettlementContracts.cs`, `SettlementMath.cs` และ migrationชื่อ `AddFieldAdvanceSettlements` (ชื่อ timestampไฟล์สร้างด้วย EF จริง ไม่แต่ง timestampล่วงหน้า) Task2เพิ่ม `AddSettlementCorrectionDays` หากยังไม่รวมคอลัมน์nullableใน Task1 ทุก migrationสร้าง designer/snapshotและตรวจSQL ไม่รันฐานจริง

แต่ละบริการด้านล่างแยกหน้าที่หนึ่งไฟล์ ห้ามรวมทุก workflowไว้ใน endpoints หรือแก้ PaymentCommandExecutorให้รับ Owner เป็น Payer ชื่อใหม่ในแผนเป็นชื่อที่จะสร้าง มิใช่อ้างว่ามีแล้ว

### สัญญาที่ทุก Task ใช้

Task1ประกาศใน `ST/SettlementContracts.cs`:

```csharp
public sealed record SettlementCommand(Guid CommandId, long RequestVersion, long FamilyVersion,
    long? EntityVersion, string Reason);
public sealed record SettlementCommandResult(Guid RequestId, Guid? EntityId, string State,
    long RequestVersion, long FamilyVersion, long? EntityVersion);
public sealed record SettlementTotals(decimal OwnerReceived, decimal ApprovedExpense,
    decimal ConfirmedRefund, decimal PendingRefund, decimal OwnerOutstanding,
    decimal WrongRecipientOutstanding, bool HasOpenCase);
public sealed record SettlementAccessDecision(bool Allowed, Guid? PersonId, string Code);
public sealed record EvidenceAllocation(Guid DocumentId, decimal Amount);
public sealed record ExpenseLineInput(Guid? SourceLineId, Guid CategoryId, string Description,
    DateOnly IncurredOn, decimal? Quantity, decimal? Rate, decimal? LumpSum,
    EvidenceAllocation[] Allocations);
public sealed record RoundInput(ExpenseLineInput[] Lines);
public sealed record ReviewInput(string Decision, string Reason);
```

ทุก command service คืน `Task<FinanceResult<SettlementCommandResult>>` (เรียกย่อ `CommandResult` ในข้อความอธิบายเท่านั้น) และรับ `Guid actor` จาก session กับ `CancellationToken ct` ท้ายสุด ไม่รับ actor/person/roleที่ clientอ้างมาใช้เป็น authority `ReviewInput.Decision` รับเฉพาะ Approve/Return/Rejectตาม transitionที่นิยามใน Taskนั้น ไม่อนุมานว่าทุกสถานะใช้ได้ทุก workflow

Task1กำหนดข้อมูล: Round/Revision/Line/ApprovalStep, Document/Allocation/DuplicateCase, WorkCompletion/Declaration, Refund/Correction, AdjustmentCase, IncidentResolution/Recovery, ClosureRecord พร้อม request/project/workspace/family IDs, version, immutableaccepted snapshots, proposer/reviewer person/account/time และ uniquecommand refs ต้นฉบับรับรองแล้วแก้ไม่ได้; ยอดสุทธิมาจากรายการรับรองและผลต่างเพียงครั้งเดียว

Task3สร้าง executorสัญญาคงที่:

```csharp
Task<FinanceResult<SettlementCommandResult>> ExecuteAsync(Guid actor, Guid requestId,
    string action, SettlementCommand command, string payloadHash,
    Func<CancellationToken, Task<FinanceResult<SettlementCommandResult>>> mutate,
    CancellationToken ct);
Task<SettlementAccessDecision> CheckAsync(Guid actor, Guid requestId,
    string action, Guid? entityId, CancellationToken ct);
```

ทุกบริการต้องระบุ operation+entityIdในhash, ตรวจ currentroleก่อน replay, lock family→request→entity, recheckก่อนcommit และส่งตอบผ่าน guardของ Task15 เอกสารซ้ำข้ามfamilyใช้ lockทะเบียนเอกสารก่อน familyตามลำดับGUIDคงที่ในทุกcallsite ห้าม acquireเอกสารlockหลังถือfamilyโดยบางcallsite ใช้ transactionsจริงและ constraintsไม่ใช่ mutexในprocess

สิทธิ์ต่อ action: owner/delegateเตรียมร่าง/แนบ; ownerยืนยัน/ส่งครบ; assignedsupervisor/PMตามขั้นอนุมัติ; finance.checkตรวจ/รับคืน/ตรวจแก้วัน; payment.recordเสนอเหตุจ่ายผิด/เตรียมงวดส่วนขาด; report.readอ่านตามขอบเขต ไม่มี capabilityใหม่เพิ่มให้อัตโนมัติ ต้องตรวจบุคคลทั้งfamily และการเปลี่ยนผู้มีตำแหน่งเหมือน7B

### ชุดตรวจร่วม G — ทุก Task ก่อนแจ้งเสร็จ

รันแยกคำสั่ง บันทึกเวลาจริง exitcode จำนวนผ่าน และ sourcehashก่อน/หลัง ไม่ใช้ผลจากคนละcommitหรือผลที่ถูกยกเลิก การรันทดสอบ backendใช้Docker/PostgreSQLจำลอง ห้ามชี้ฐานจริง

```sh
dotnet build backend/TPR10.sln --no-restore --verbosity minimal
dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm test
npm run build
npm run lint
git diff --check
```

ถ้ายังไม่มี restore/build assets ให้ restoreจากlockfileก่อนตามสภาพแวดล้อม ไม่รายงาน dependencyfailureเป็น productRED ผลผ่านคือทุกคำสั่งexit0 ไม่มี failingtests/lint/format; จำนวนต้องนับจริง ไม่ตั้ง expectedcountจาก7C ผู้ลงมือรันfocusedRED/GREEN ผู้ประสานงานรันGครั้งเดียวบน sourceที่หยุดแก้แล้ว ไม่รันซ้ำโดยไม่มีการเปลี่ยน/ข้อกังวลใหม่

ก่อนcommit: ผู้ตรวจใหม่อ่านแบบ+diff+หลักฐาน ไม่ใช่รับรองจากชื่อเทสต์ แก้feedbackแล้วทดสอบส่วนกระทบใหม่ stageเฉพาะไฟล์ใน Task ตรวจcached diffและcommit ไม่ใช้ `git add .`

ทุกเทสต์ตัวอย่างด้านล่างเป็นแกน assertionsใน xUnit สำหรับเคสชื่อที่กำหนด ไม่ใช่บังคับรูปแบบfixture ผู้ลงมือสร้าง Arrange/Actด้วย PostgresFixture และ ManualTimeProviderเดิม ค่าในตัวอย่างเป็นข้อมูลทดสอบเท่านั้นไม่ใช่นโยบายบริษัท

---

### Task 1: โครงสร้างข้อมูลและยอดที่รักษาประวัติ

**ต้องผ่านก่อน:** แบบอนุมัติ

**สถานะ:** Task 1 ผ่าน Test/Build/Lint และผู้ตรวจอิสระ 2026-10-31; ดู [รายงานผล](../../reports/2026-10-31-module-7d-task1-verification.md) ข้อตกลง: ยอดสุทธิคงค้างติดลบได้ตามแบบ §10; constraints ไม่ติดลบใช้กับองค์ประกอบยอดเงิน

**ไฟล์:** สร้าง: FA/Data/SettlementEntities.cs, FA/Data/SettlementModelConfiguration.cs, ST/SettlementContracts.cs, ST/SettlementMath.cs; แก้: DB/Tpr10DbContext.cs, FA/FieldAdvanceRegistration.cs; สร้าง migration AddFieldAdvanceSettlements พร้อม designer/snapshot; เทสต์: IT/FieldAdvanceSettlementSchemaTests.cs, IT/FieldAdvanceSettlementMathTests.cs

**Interfaces:** ผลิต contractsส่วนกลาง และ `SettlementMath.Totals(decimal ownerReceived, decimal incidentOwnerReturns, decimal approvedExpense, decimal ordinaryRefund, decimal pendingRefund, decimal wrongRecipientOutstanding, bool hasOpenCase) -> SettlementTotals` กับ `SettlementMath.Line(decimal? quantity, decimal? rate, decimal? lumpSum) -> decimal`; ใช้ FinanceResult<T>เดิม

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementMathTests.Decimal_line_and_owner_balance_are_exact`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(1.01m, SettlementMath.Line(0.5m, 2.01m, null));
Assert.Equal(1500m, SettlementMath.Totals(10000m, 0m, 8000m, 500m, 1000m, 0m, false).OwnerOutstanding);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementMathTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** เพิ่ม constraintsเงินไม่ติดลบสำหรับยอดรายการ, uniquecommand/acceptedrevision/closure และ immutablehistory โดยผลต่าง signedแยกตารางพร้อมยอดสุทธิไม่ติดลบ ทดสอบmigrationจาก7Cไม่สร้างเงิน/สิทธิ์และรันซ้ำได้; pendingRefundไม่หักOwnerOutstanding; ตรวจจำนวน5ตำแหน่ง/เงิน3ตำแหน่ง/overflowเป็นvalidationerror
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: add settlement data foundation` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 2: กติกาจำนวนวันแก้ไขและการคง snapshot เดิม

**ต้องผ่านก่อน:** 1

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ 2026-10-31; ดู [รายงานผล Task 2](../../reports/2026-10-31-module-7d-task2-verification.md)

**ไฟล์:** แก้: FA/FieldAdvanceContracts.cs, FA/Data/PolicyEntities.cs, FA/Data/FieldAdvanceModelConfiguration.cs, FA/Policies/PolicyValidation.cs, PolicyService.cs, PolicyReadData.cs, PolicyHttpContracts.cs, CLIENT/policy-types.ts, UI/settings/PolicyEditor.tsx; สร้าง: ST/SettlementPolicyResolver.cs; migration AddSettlementCorrectionDays; เทสต์: IT/FieldAdvanceSettlementPolicyTests.cs

**Interfaces:** เพิ่ม `int? CorrectionDays` แบบoptionalบน PolicyContent/row/wire ไม่เปลี่ยนhashหรือJSONของsnapshotเก่าที่ไม่มีfield; `ResolveCorrectionAsync(Guid requestId, CancellationToken ct) -> Task<FinanceResult<CorrectionWindow>>`; `CorrectionWindow(int Days, Guid PolicyRevisionId, string ContentHash)`

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementPolicyTests.Legacy_snapshot_is_readable_but_missing_correction_window_is_not_invented`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(oldHash, legacy.ContentHash);
Assert.Null(legacy.Content.CorrectionDays);
Assert.Equal("correction-policy-not-ready", window.Code);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementPolicyTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** ค่าที่กำหนดใหม่ต้องเป็นจำนวนเต็มบวก บริษัท/โครงการผ่านการอนุมัติเดิมและrebaseตาม7A; ไม่แก้snapshot/hashย้อนหลัง ไม่เติมเลขวันในmigration; UIแสดงยังไม่ตั้งค่าได้และยังอ่านคำขอเก่าได้ เวลาส่งกลับใช้policyปัจจุบันแล้วตรึงค่าไว้ในรอบ
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: configure settlement correction windows` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 3: สิทธิ์และธุรกรรมคำสั่งเคลียร์

**ต้องผ่านก่อน:** 1

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 3](../../reports/2026-10-31-module-7d-task3-verification.md)

**ไฟล์:** สร้าง: ST/SettlementAccess.cs, SettlementCommandExecutor.cs, SettlementActions.cs; แก้: FA/FieldAdvanceRegistration.cs; เทสต์: IT/FieldAdvanceSettlementAccessTests.cs, FieldAdvanceSettlementCommandTests.cs

**Interfaces:** ผลิต CheckAsync/ExecuteAsync ตามสัญญากลาง; ใช้ FinanceAccess, FinancePersonResolver, FamilyParticipation, FinanceCommandReceiptเดิม; actionnamesเก็บรวม SettlementActions ไม่ใช้unknownactionfallback

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementAccessTests.Alias_or_revoked_grant_cannot_replay_finance_result`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(403, replayAfterRevocation.Status);
Assert.Equal(403, ownerSecondAccountReview.Status);
Assert.Equal(beforeReceiptCount, afterDeniedReceiptCount);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementAccessTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** ตรวจสิทธิ์รายโครงการ/assignedstep/currentpersonทั้งfamilyก่อนreceiptlookup; samecommandต่างpayloadเป็น409; failedcommit rollbackยอด/receipt/audit; ไม่เขียนPayerให้owner/delegate และอนุญาตตรวจเคสหลังClosedโดยไม่อนุญาตeditคำขอเดิม ทดสอบ actorสองบัญชี/ผู้เสนอ=ผู้ตรวจ/rolehistoryข้ามส่วนเพิ่ม
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: enforce settlement access and atomic commands` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 4: หลักฐานพนักงานและการตรวจเอกสารซ้ำ

**ต้องผ่านก่อน:** 3

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 4](../../reports/2026-11-01-module-7d-task4-verification.md) คง T4-M1 สำหรับตรวจ migration เพิ่มใน Task 18

**ไฟล์:** สร้าง: ST/SettlementEvidenceService.cs, ExpenseDocumentService.cs, DuplicateDocumentService.cs; ใช้ FA/Evidence/IFinanceEvidenceStore.cs, PdfValidationProcess.csเดิม; เทสต์: IT/FieldAdvanceExpenseDocumentTests.cs, FieldAdvanceSettlementEvidenceTests.cs

**Interfaces:** `UploadAsync(Guid actor, Guid requestId, Stream content, string contentType, CancellationToken ct) -> Task<FinanceResult<Guid>>`; `DocumentInput(string Issuer, string? Number, bool HasNoNumber, DateOnly IssuedOn, decimal Amount, Guid[] EvidenceIds)`; `RegisterAsync(Guid actor, Guid requestId, DocumentInput input, SettlementCommand command, CancellationToken ct)` และ `ReviewDuplicateAsync(Guid actor, Guid requestId, Guid duplicateId, bool SameDocument, SettlementCommand command, CancellationToken ct)` คืนCommandResult

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementEvidenceTests.Owner_upload_does_not_create_payment_role`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(201, upload.Status);
Assert.DoesNotContain(participation, x => x.PersonId == ownerPerson && x.Role == "Payer");
Assert.Equal(403, crossProjectDownload.Status);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementEvidenceTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** ห้ามเรียก FinanceEvidenceService.UploadAsyncเดิมที่บังคับrecordและบันทึกPayer; reuseเฉพาะ immutable store/validatedIObudget/PDFprocess โดยเอกสาร7Dมีownauthorizationและlinkvalidation การuploadเตรียมIOnอกfinancialtransactionแล้วrecheckก่อนpersist; ตรวจhash+issuer/number/date/amount; ไม่มีเลขใช้flag; suspiciousพักส่ง checkerต้องสิทธิ์ทั้ง2projectsและแยกหน้าที่ทั้ง2; returnข้อมูลทั่วไปให้ผู้ยื่น ไม่เปิดอีกrequest; แก้metadataสำคัญinvalidateผลตรวจเดิม
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: add private expense evidence and duplicate review` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 5: ร่างค่าใช้จ่ายและการจัดสรรยอดเอกสาร

**ต้องผ่านก่อน:** 2,4

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 5](../../reports/2026-11-01-module-7d-task5-verification.md) คงรายการติดตามเดิมรวม T4-M1 สำหรับ Task 18

**ไฟล์:** สร้าง: ST/SettlementDraftService.cs, ExpenseAllocationLedger.cs; เทสต์: IT/FieldAdvanceSettlementDraftTests.cs, FieldAdvanceExpenseAllocationTests.cs

**Interfaces:** `SaveAsync(Guid actor, Guid requestId, Guid? roundId, RoundInput input, SettlementCommand command, CancellationToken ct)` คืนCommandResult; `ReserveAsync(Guid requestId, Guid roundId, EvidenceAllocation[] allocations, CancellationToken ct) -> Task<FinanceDecision>` ใช้ภายในexecutorเท่านั้น

- [x] **1. เขียนเทสต์ `FieldAdvanceExpenseAllocationTests.Pending_and_returned_rounds_share_one_document_budget`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(409, competingReservation.Status);
Assert.Equal(700m, returnedRoundReserved);
Assert.Equal(300m, documentRemaining);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceExpenseAllocationTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** รายการอิงcategory/rateจากapprovedrequest snapshot รวมค่าตามSettlementMath; draftไม่ทำให้ยอดรับรองเปลี่ยน กันยอดเมื่อส่งตรวจ ตรวจครบหลักฐาน/ไฟล์Ready/ใบอยู่คำขอเดียว; ส่งกลับคงreserved ลด/ลบคืนส่วนต่าง atomically; testcaseใบ1000รอบแรก700รอบสอง400ต้องfailแม้คนละconnection; ห้ามอ่านยอดแล้วเขียนแยกtransaction
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: draft settlement rounds and reserve document amounts` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 6: ยืนยันและอนุมัติรอบค่าใช้จ่าย

**ต้องผ่านก่อน:** 5

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 6](../../reports/2026-11-01-module-7d-task6-verification.md) คงรายการติดตามเดิมรวม T4-M1 สำหรับ Task 18

**ไฟล์:** สร้าง: ST/SettlementSubmissionService.cs, SettlementRouteResolver.cs, SettlementReviewService.cs; เทสต์: IT/FieldAdvanceSettlementReviewTests.cs

**Interfaces:** `SubmitAsync(Guid actor, Guid requestId, Guid roundId, SettlementCommand command, CancellationToken ct)`; `ReviewAsync(Guid actor, Guid requestId, Guid roundId, ReviewInput input, SettlementCommand command, CancellationToken ct)` คืนCommandResult; `ResolveAsync(Guid requestId, Guid roundId, CancellationToken ct) -> Task<SettlementRoute>`; `SettlementRoute(RouteChoice Supervisor, RouteChoice ProjectManager, RouteChoice FinanceChecker)` ใช้ RouteChoiceเดิม

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementReviewTests.Returned_round_requires_owner_and_all_new_decisions`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(0, newRevision.AcceptedStepCount);
Assert.Equal("AwaitingOwner", revisedRound.State);
Assert.Equal(oldAcceptedTotal, totals.ApprovedExpense);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementReviewTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** routeเป็นหัวหน้า→PM→finance ไม่เรียกrequest.CanReviewที่มอบหมายเฉพาะ7Bแทน; assignedcurrentคนละคน, หาlevelถัดไปเมื่อชนข้อห้ามและพักถ้าขาด; ownerconfirmผูกrevision/hash/policyก่อนส่ง ถ้าเปลี่ยนทบทวนใหม่; Approve/Returnพร้อมเหตุผลตามcontract ยังไม่เพิ่มการrejectถาวรของexpenseโดยไม่มีข้อกำหนด; finalapproveย้ายreservedเป็นconsumedและอัปเดตยอดครั้งเดียว; roundเก่าไม่เปลี่ยน
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: approve settlement rounds through separated roles` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 7: วันจบงาน การส่งครบ และกำหนดแก้ไข

**ต้องผ่านก่อน:** 2,6

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 7](../../reports/2026-11-01-module-7d-task7-verification.md) คงรายการติดตามเดิมรวม T4-M1 สำหรับ Task 18

**ไฟล์:** สร้าง: ST/SettlementCompletionService.cs, SettlementDeadlineCalculator.cs; เทสต์: IT/FieldAdvanceSettlementDeadlineTests.cs

**Interfaces:** `ConfirmEndAsync(Guid actor, Guid requestId, DateOnly actualEnd, SettlementCommand command, CancellationToken ct)`; `ProposeEndCorrectionAsync(Guid actor, Guid requestId, DateOnly actualEnd, SettlementCommand command, CancellationToken ct)`; `ConfirmEndCorrectionAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)`; `DeclareCompleteAsync(Guid actor, Guid requestId, bool complete, SettlementCommand command, CancellationToken ct)` คืนCommandResult; `DueOn(DateOnly end, int days) -> DateOnly`

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementDeadlineTests.Calendar_deadline_and_return_window_keep_original_submission`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(new DateOnly(2026, 11, 2), SettlementDeadlineCalculator.DueOn(new(2026, 10, 31), 2));
Assert.True(history.InitialSubmissionOnTime);
Assert.False(history.CorrectionOnTime);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementDeadlineTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** PMยืนยันวันจริง ownerยืนยันส่งครบ/ถอนพร้อมเหตุผล; PMแก้วันต้องfinancecheckerอื่นตรวจไม่ใช่owner/delegate; policyCorrectionDaysตรึงตอนส่งกลับ วันถัดไปday1; แยกinitial/completion/correction timestampsไม่ล้างประวัติเดิม; ทดสอบUTCข้ามวันไทย/วันหยุด/รอตรวจ/แก้วันยังไม่ยืนยัน; Closedห้ามถอนcompleteเพื่อเพิ่มexpenseใหม่
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: track settlement completion and correction deadlines` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 8: ทยอยคืนเงินและตรวจรับคืน

**ต้องผ่านก่อน:** 6

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 8](../../reports/2026-11-01-module-7d-task8-verification.md) คงรายการติดตามเดิมรวม T4-M1 สำหรับ Task 18

**ไฟล์:** สร้าง: ST/RefundService.cs, RefundReviewService.cs; เทสต์: IT/FieldAdvanceRefundTests.cs

**Interfaces:** `RefundInput(decimal Amount, DateOnly ReturnedOn, string Channel, string CompanyRecipient, Guid[] EvidenceIds, Guid? AdjustmentCaseId)`; `ProposeAsync(Guid actor, Guid requestId, RefundInput input, SettlementCommand command, CancellationToken ct)`; `ReviewAsync(Guid actor, Guid requestId, Guid refundId, ReviewInput input, SettlementCommand command, CancellationToken ct)` คืนCommandResult

- [x] **1. เขียนเทสต์ `FieldAdvanceRefundTests.Pending_refund_does_not_reduce_balance_or_allow_duplicate_proposal`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(2000m, beforeReview.OwnerOutstanding);
Assert.Equal(1500m, beforeReview.PendingRefund);
Assert.Equal(409, secondProposalFor600.Status);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceRefundTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** เจ้าของ/ผู้ยื่นแทนprepareแต่ownerยืนยันเงินคืน; transfer/cash requiredestination+evidence companyreceiverตรวจfinance; checkcurrentcapเมื่อfinalconfirmภายใต้lockร่วมexpense; approveครั้งเดียว pendingreserveไปconfirmed, Returnคงประวัติและpendingclaimจนลด/ยกเลิกตามสิทธิ์ไม่ล้างเงินจริงที่รับรอง; ไม่ใช้slipเป็นproofโดยอัตโนมัติ; recordedactualdate+completesubmittimeใช้กำหนดเดียวกับเคลียร์
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: record and verify installment refunds` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 9: แก้เงินคืนและรายการปรับปรุงหลังปิด

**ต้องผ่านก่อน:** 8

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 9](../../reports/2026-11-01-module-7d-task9-verification.md) คงรายการติดตามเดิมรวม T4-M1 สำหรับ Task 18

**ไฟล์:** สร้าง: ST/RefundCorrectionService.cs, AdjustmentCaseService.cs; เทสต์: IT/FieldAdvanceRefundCorrectionTests.cs

**Interfaces:** `RefundCorrectionInput(Guid RefundId, decimal CorrectAmount, Guid[] EvidenceIds)`; `ProposeAsync(Guid actor, Guid requestId, RefundCorrectionInput input, SettlementCommand command, CancellationToken ct)`; `ConfirmAsync(Guid actor, Guid requestId, Guid correctionId, SettlementCommand command, CancellationToken ct)` คืนCommandResult; `OpenAsync(Guid requestId, string kind, Guid sourceId, CancellationToken ct) -> Task<Guid>` เป็นAdjustmentCaseServiceภายในexecutor; `CasePaymentInput(Guid CaseId, decimal Amount, DateOnly PaidOn, Guid[] EvidenceIds)`; `ProposeCasePaymentAsync(Guid actor, Guid requestId, CasePaymentInput input, SettlementCommand command, CancellationToken ct)` และ `ConfirmCasePaymentAsync(Guid actor, Guid requestId, Guid proposalId, SettlementCommand command, CancellationToken ct)` คืนCommandResult สำหรับบันทึกผลจ่ายภายนอกที่ตรวจแล้วเท่านั้น ไม่ใช่APIสั่งโอน

- [x] **1. เขียนเทสต์ `FieldAdvanceRefundCorrectionTests.Closed_request_correction_uses_latest_net_version_once`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal("Closed", request.State);
Assert.Equal(500m, adjustment.Outstanding);
Assert.Equal(409, staleSecondCorrection.Status);
Assert.Single(closures);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceRefundCorrectionTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** แก้2000→1500ลงผลต่าง-500เมื่อcheckerอื่นconfirmเท่านั้น; คงClosureRecordและoriginalrefund; caseใหม่ทำfamilyunsettled ไม่เพิ่มexpenseใหม่หรือเปิดวงเงิน; รับเพิ่มผ่านRefundServiceผูกcase, กรณีบริษัทคืนส่วนเกินให้มีpayerเสนอexternalpaymentproof/checkerยืนยันในcaseและป้องกันdup; positive/negativecasebalancesไม่clamp; เคสรอตรวจblockclose
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: correct refunds without reopening closed requests` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 10: ปรับปรุงค่าใช้จ่ายที่รับรองและรายการส่วนเกิน

**ต้องผ่านก่อน:** 6,9

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 10](../../reports/2026-11-02-module-7d-task10-verification.md) คงรายการติดตามเดิมรวม T4-M1 สำหรับ Task 18

**ไฟล์:** สร้าง: ST/ExpenseAdjustmentService.cs, ExpenseExcessService.cs; เทสต์: IT/FieldAdvanceExpenseAdjustmentTests.cs

**Interfaces:** `ProposeAsync(Guid actor, Guid requestId, Guid sourceRoundId, RoundInput replacement, SettlementCommand command, CancellationToken ct)` คืนCommandResultและใช้Task6submit/reviewบนRoundKindAdjustment; `RecordDispositionAsync(Guid actor, Guid requestId, Guid excessId, string disposition, Guid[] evidenceIds, SettlementCommand command, CancellationToken ct)` คืนCommandResult

- [x] **1. เขียนเทสต์ `FieldAdvanceExpenseAdjustmentTests.Reduction_keeps_document_consumed_until_full_route_approves`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(8000m, pendingTotals.ApprovedExpense);
Assert.Equal(8000m, pendingDocument.Consumed);
Assert.Equal(6000m, approvedTotals.ApprovedExpense);
Assert.Equal(2000m, approvedTotals.OwnerOutstanding);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceExpenseAdjustmentTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** ทุกlineadjustmentอ้างsourceที่รับรองและlatestversion ownerconfirm→head→PM→checkerครบ ผลต่างapplyonce; ลดไม่คืนconsumedก่อนผ่าน เพิ่มกันdifferenceและcap; Closedใช้AdjustmentCaseแต่ห้ามแทรกexpenseใหม่; excessinvoice12000cap10000พัก2000ภายนอก ไม่ใช่ตัดยอดเองหรือใช้ใบข้ามsupplement; checkerdispositionมีreason/evidence ต้อง linkจริงกับผลเงินก่อนเคสจบ ไม่มีคำสั่งmarkzeroจากnote
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: adjust approved expenses and track excess cases` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 11: สะสางจ่ายผิดและภาระเงินจริง

**ต้องผ่านก่อน:** 3,8

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 11](../../reports/2026-11-02-module-7d-task11-verification.md) คงรายการติดตามเดิมรวม T4-M1 และเพิ่ม T11-M1 สำหรับ Task 18

**ไฟล์:** สร้าง: ST/PaymentIncidentService.cs, IncidentAccounting.cs; ใช้ FA/Payments/PaymentHoldService.cs และข้อมูล7C; เทสต์: IT/FieldAdvancePaymentIncidentTests.cs

**Interfaces:** `IncidentInput(Guid AttemptId, decimal OwnerReceived, decimal WrongRecipientReceived, Guid[] EvidenceIds)`; `ProposeAsync(Guid actor, Guid requestId, IncidentInput input, SettlementCommand command, CancellationToken ct)`; `ConfirmAsync(Guid actor, Guid requestId, Guid incidentId, SettlementCommand command, CancellationToken ct)`; `RecordRecoveryAsync(Guid actor, Guid requestId, Guid incidentId, decimal amount, string source, Guid[] evidenceIds, SettlementCommand command, CancellationToken ct)` คืนCommandResult; `ConfirmRecoveryAsync(Guid actor, Guid requestId, Guid recoveryId, SettlementCommand command, CancellationToken ct)` คืนCommandResultโดยcheckerคนละคน; source Owner/WrongRecipientตรวจallowlist

- [x] **1. เขียนเทสต์ `FieldAdvancePaymentIncidentTests.Before_and_after_paid_have_equal_actual_owner_liability`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(8000m, beforePaid.OwnerOutstanding);
Assert.Equal(beforePaid.OwnerOutstanding, afterPaid.OwnerOutstanding);
Assert.Equal(0m, whollyWrongRecipient.OwnerOutstanding);
Assert.True(whollyWrongRecipient.HasOpenCase);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvancePaymentIncidentTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** payerเสนอcheckerอื่นยืนยันactualprojectionครั้งเดียว uniqueattempt+revision, ไม่แก้Paid/recipientเดิมและไม่sumactualเพิ่มซ้ำ; คงownerexcess12000จากงวด10000ต้องคืน2000; wrongrecipientrecoverpartialยังhold; RecordRecoveryเป็นproposalรับคืนก่อนcheckerconfirmไม่หักยอดทันที ใช้typeแยกordinaryrefundให้ไม่หักซ้ำ; ทุกยอดrecovered<=relatedactual; เตรียมadjustmenteventsสำหรับTask12 ไม่แก้spendableโดยTaskนี้
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: reconcile actual money in payment incidents` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 12: งวดส่วนขาดและการปลดเหตุอย่างชัดเจน

**ต้องผ่านก่อน:** 11

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 12](../../reports/2026-11-02-module-7d-task12-verification.md) คงรายการติดตามเดิมรวม T4-M1/T11-M1 สำหรับ Task 18 และ T12-M1 ภายใต้ Minor log

**ไฟล์:** สร้าง: ST/IncidentPaymentBridge.cs; แก้: FA/Payments/ReservationLedger.cs, PaymentExecutionService.cs, InstallmentService.cs, InstallmentReviewService.cs, PaymentReadService.cs; เทสต์: IT/FieldAdvanceIncidentPaymentBridgeTests.cs

**Interfaces:** `ReleaseHoldAsync(Guid actor, Guid requestId, Guid incidentId, SettlementCommand command, CancellationToken ct)`; `PrepareShortfallAsync(Guid actor, Guid requestId, Guid incidentId, decimal amount, SettlementCommand command, CancellationToken ct)` คืนCommandResult; `EffectivePaymentUsageAsync(Guid requestId, CancellationToken ct) -> Task<decimal>` ใช้ภายในaccountlockedtx

- [x] **1. เขียนเทสต์ `FieldAdvanceIncidentPaymentBridgeTests.Recovery_does_not_restore_capacity_or_reuse_old_review`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(beforeRecoveryAvailable, afterRecoveryAvailable);
Assert.Equal("PendingReview", shortfall.State);
Assert.Equal(409, startBeforeNewReview.Status);
Assert.Contains("OtherHold", afterRelease.Holds);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceIncidentPaymentBridgeTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** explicitreleaseเฉพาะholdidเมื่อwrongrecipientoutstanding0และข้อเท็จจริงตรวจครบ; prepareส่วนขาดต้องประเมินวงเงินปัจจุบัน−effectiveusage−reserved−terminated และclaimcapacityในtransactionเดิม ผลตรวจงวดเก่าใช้ไม่ได้; ordinaryrefundไม่ลดeffectiveusageและไม่สร้างspendable; incidentadjustmentreplacesเงินที่ผิดเท่านั้น, effectiveusageไม่ได้เปลี่ยนapprovedbasis; consumedpaidoriginalเก็บhistory ห้ามทำaccount.Paid-=recoveryตรงๆ; samecommand/2connections/หลายincidentต้องไม่ใช้entitlementซ้ำ
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: review incident shortfalls without duplicate payments` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 13: ให้ส่วนเพิ่มเดิมเดินต่อหลังคำขอหลักปิด

**ต้องผ่านก่อน:** 3

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 13](../../reports/2026-11-02-module-7d-task13-verification.md) คงรายการติดตามเดิมรวม T4-M1/T11-M1 สำหรับ Task 18 และ T12-M1 ภายใต้ Minor log

**ไฟล์:** แก้: FA/Requests/SupplementService.cs, RequestSubmissionService.cs, FA/Payments/PaymentUiService.cs, FA/Data/RequestEntities.cs, RequestModelConfiguration.cs; สร้าง migration AddSupplementClosureOrdering พร้อมsnapshotและเทสต์legacybackfill; เทสต์: IT/FieldAdvanceSupplementClosureTests.cs

**Interfaces:** แยก `CanCreateAsync(Tpr10DbContext db, Guid familyId, CancellationToken ct) -> Task<bool>` กับ `CanContinueAsync(Tpr10DbContext db, Guid supplementId, CancellationToken ct) -> Task<bool>`; createต้องrootไม่Closed continueตรวจrelationshipที่เกิดก่อนClosureRecordและสิทธิ์/วงเงินปัจจุบัน

- [x] **1. เขียนเทสต์ `FieldAdvanceSupplementClosureTests.Existing_returned_supplement_continues_but_new_creation_is_denied`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(200, resubmitExisting.Status);
Assert.Equal(409, createAfterClosure.Status);
Assert.Equal(oldApprovalBasis, sourceHistoricalBasis);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSupplementClosureTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** เก็บfamilyversionatcreationและclosureเพื่อproofcommitorder; migrationต้องรับรองส่วนเพิ่มที่มีอยู่ก่อนเริ่ม7Dอย่างชัดเจนจากประวัติฐานเดิม ไม่แต่งเวลา/ปฏิเสธlegacydraftทุกใบ และมีเทสต์สร้าง7Cก่อนmigrateแล้วปิดหลัก/ส่งส่วนเพิ่มต่อได้; ใช้ข้อมูลนี้ ไม่ใช้clienttimestamp; concurrentcreate/closeมีผลเดียวและdraftที่createแพ้ไม่เกิด; เส้นทางUIactionและsubmit/reviseต้องใช้predicateถูกตัว; คง1pendingsupplementslotและpersonhistory, ไม่reopenroot; rejected/withdrawnไม่มีเงินนับterminal
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: continue existing supplements after source closure` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 14: ปิดอัตโนมัติและยอดสรุปตามสิทธิ์

**ต้องผ่านก่อน:** 7–13

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 14](../../reports/2026-11-02-module-7d-task14-verification.md) คงรายการติดตามเดิมรวม T4-M1/T11-M1 สำหรับ Task 18 และ T12-M1 ภายใต้ Minor log

**ไฟล์:** สร้าง: ST/SettlementReadService.cs, SettlementClosureService.cs, SettlementClosureWorker.cs; แก้: FA/FieldAdvanceRegistration.cs และจุดcommitของSTบริการที่เกี่ยวข้อง; เทสต์: IT/FieldAdvanceSettlementClosureTests.cs

**Interfaces:** `ReadAsync(Guid actor, Guid requestId, CancellationToken ct) -> Task<FinanceResult<SettlementSummary>>`; `SettlementSummary(Guid RequestId, string State, SettlementTotals Totals, string[] Holds, string[] AllowedActions)`; `TryCloseAsync(Guid requestId, CancellationToken ct) -> Task<bool>` เป็นinternalworkeroperation ไม่รับactorปลอมจากAPI

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementClosureTests.Open_adjustment_or_family_hold_prevents_closure`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.False(await closure.TryCloseAsync(requestId, ct));
Assert.Equal("Closed", alreadyClosedRoot.State);
Assert.False(familySummary.Settled);
Assert.Single(originalClosureRecords);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementClosureTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** candidatequeueจากcommitหรือworkerค้นboundedแล้วlock/recheckทุกconditionในspec§10; ownerdeclared PMend totals0 noopenround/refund/proposal/reservation/unpaidobligation/holds/excess; closureauditระบบอ้างsourceids ไม่ปลอมuser; ชนnewrefund/shortfall/adjustmentแล้วไม่closeบนstale; readfamilyเฉพาะสิทธิ์ครบแสดงtotalครบ มิฉะนั้นยอดเฉพาะที่อนุญาตและไม่เผยว่าคำขอซ่อนมีเงินเท่าไร; workeridempotent retrycrashไม่ปิดซ้ำ
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: close fully reconciled requests automatically` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 15: API และ client พร้อมป้องกันข้อมูลหลุด

**ต้องผ่านก่อน:** 1–14

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 15](../../reports/2026-11-03-module-7d-task15-verification.md) คงรายการติดตามเดิมรวม T4-M1/T11-M1 สำหรับ Task 18 และ T12-M1 ภายใต้ Minor log

**ไฟล์:** สร้าง: ST/SettlementEndpoints.cs, SettlementHttpContracts.cs, SettlementUiService.cs, CLIENT/settlement-types.ts, settlement-client.ts; แก้: FA/FieldAdvanceRegistration.cs, FieldAdvanceOpenApiTransformer.cs; เทสต์: IT/FieldAdvanceSettlementHttpTests.cs, tests/field-advance-settlement-client.test.mjs

**Interfaces:** APIprefix `/api/v1/field-advances/requests/{requestId}/settlement`; GETsummary/rounds/documents/refunds/adjustments/incidents; POSTตามtypedserviceแต่ละTask; `loadSettlement(requestId: string, signal?: AbortSignal): Promise<SettlementSummary>` และ `sendSettlementCommand(requestId: string, action: string, body: unknown, command: SettlementCommand, signal?: AbortSignal): Promise<SettlementCommandResult>`; wireเงินstring parsevalidateddecimalserver

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementHttpTests.Revocation_before_delivery_does_not_send_prepared_payload`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(403, response.StatusCode);
Assert.DoesNotContain(secretExpense, response.Body);
Assert.Equal("private, no-store", response.CacheControl);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementHttpTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** exactoperationallowlistไม่รับ arbitraryroute/action; serverMFA/CSRF/session,currentauthorizationก่อนserializeและก่อนส่งเหมือน7C; staleversionที่สิทธิ์ยังมี409 ไม่คืนpreparedpayload denial403; owner/delegate evidenceownpath noPayer; OpenAPIรวม400401403409413415422503ตามactualcontract ห้ามอ้างปิดwholeOpenAPI500; serverpaginationbounded stablecursor,test crossrequestid/sessionexpired/filedownloadscope
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: expose scoped settlement APIs and client` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 16: หน้าพนักงานส่งเคลียร์และคืนเงิน

**ต้องผ่านก่อน:** 15

**สถานะ:** ผ่าน Test/Build/Lint และผู้ตรวจอิสระ; ดู [รายงาน Task 16](../../reports/2026-11-03-module-7d-task16-verification.md) คงรายการติดตามเดิมรวม T4-M1/T11-M1 สำหรับ Task 18 และ T12-M1 ภายใต้ Minor log พร้อมติดตาม T16-M1/T16-M2

**ไฟล์:** สร้าง: UI/SettlementDetails.tsx, SettlementRoundEditor.tsx, RefundEditor.tsx, SettlementStatus.tsx, [requestId]/settlement/page.tsx, CLIENT/settlement-view.ts; แก้: UI/RequestDetails.tsx; เทสต์: tests/field-advance-settlement.test.mjs, tests/e2e/field-advance-settlement.spec.ts; แก้เฉพาะallowlistใน infra/nginx/https-smoke-fixture.mjs โดยรักษางาน504เดิม

**Interfaces:** props `{ requestId: string }` สำหรับSettlementDetails; editorรับserverAllowedActionsและversionจากTask15 ไม่คำนวณauthorityในbrowser; สถานะเวลา/เงินแสดงแยก; commandIDคงเดิมจนอ่านผลชัดเจน

- [x] **1. เขียนเทสต์ `field-advance-settlement.spec.ts: response_lost_keeps_same_command_without_automatic_resend`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```typescript
await expect(page.getByText("รอตรวจสอบผลรายการ")).toBeVisible();
expect(uniqueCommandIds.size).toBe(1);
expect(automaticRetryPosts).toBe(0);
```

- [x] **2. รัน RED:** `npm test และคำสั่ง HTTPS ของ Task นี้ตามส่วนท้ายแผน` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** แสดงรอบหลายรอบ attachphoto/filePDF, ไม่บังคับlivecamera; previewrateจากsnapshotแต่totalserverauthority; returnwhole-roundพร้อมlineerrors, ownerconfirmผูกrevision; refundconfirmed/pending/balanceต่างกัน; ส่งครบ/ถอนต้องreason; clearprotectedstate403/identitychange ไม่ล้างpendingcommandในnetworkunknown; ไม่มีfinancevaluesในlocalStorage; responsiveviewportไม่อ้างmobileจริง
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: add employee settlement and refund screens` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

### Task 17: หน้าผู้อนุมัติและการเงินสะสางครบวงจร

**ต้องผ่านก่อน:** 15–16

**ไฟล์:** สร้าง: UI/SettlementReviewQueue.tsx, SettlementReviewDetails.tsx, SettlementAdjustmentPanel.tsx, IncidentSettlementPanel.tsx, settlements/page.tsx; แก้: UI/PaymentRecoveryPanel.tsx, payments/page.tsx; เทสต์: tests/field-advance-settlement-review.test.mjs, tests/e2e/field-advance-settlement-review.spec.ts

**Interfaces:** queueโหลดassigned/scopedAPI Task15; reviewerpanelรับserveractionsเท่านั้น; IncidentSettlementPanelแยกconfirmfacts/recordrecovery/releasehold/prepareshortfall ห้ามปุ่มเดียวทำสี่ขั้น

- [x] **1. เขียนเทสต์ `field-advance-settlement-review.spec.ts: closing_source_does_not_strand_existing_supplement_or_hide_adjustment`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```typescript
await expect(page.getByText("ปิดแล้ว มีรายการปรับปรุง")).toBeVisible();
await expect(page.getByRole("button", { name: "สร้างคำขอส่วนเพิ่ม" })).toHaveCount(0);
await expect(existingSupplementSubmit).toBeEnabled();
```

- [x] **2. รัน RED:** `npm test และคำสั่ง HTTPS ของ Task นี้ตามส่วนท้ายแผน` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** headPMcheckerqueuesคนละscope, evidenceซ้ำข้อมูลอีกrequestเฉพาะauthorizedchecker; correctionbefore/afterและวันเก่าใหม่ชัด; wrongrecipientreceivableไม่รวมownerdebt; receiptpendingไม่success; มีreasonproofทุกรายการ; ไม่autoPOSTเมื่อโหลด/สลับtab; E2Eทั้งactorrole/expiry/stale/newreview/multipleholdและธนาคารภายนอกไม่มีปุ่มสั่งโอน
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `feat: add settlement review and incident operations screens` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

**ผลตรวจรับ:** Backend 3,551/3,551, Frontend 260/260, Build/Lint/Format ผ่าน; HTTPS dev/prod 7/7 ต่อโหมด ผู้ตรวจอิสระยืนยันแก้ Important 6 ข้อครบ คง T17-M1/T17-M2 และรายการเดิม ดู [รายงาน Task 17](../../reports/2026-11-03-module-7d-task17-verification.md) สำหรับข้อจำกัดและหลักฐาน

### Task 18: ตรวจรวม แข่งพร้อมกัน และเอกสารส่งมอบ

**ต้องผ่านก่อน:** 1–17

**ไฟล์:** สร้าง: IT/FieldAdvanceSettlementRaceTests.cs, FieldAdvanceSettlementDeliveryTests.cs, docs/reports/2026-10-31-module-7d-verification.md, docs/runbooks/field-advance-settlements.md; แก้เฉพาะสถานะจริงในแผนนี้

**Interfaces:** ใช้ทุกservicecontractsโดยไม่เพิ่มpublicAPI; รายงานmappingD01–D18→testcase→command/time/exit/sourcehash; runbookระบุpending/refund/incident/closedcaseไม่มีการลบ/ปลดล็อกด้วยSQLนอกworkflow

- [x] **1. เขียนเทสต์ `FieldAdvanceSettlementRaceTests.Commit_failure_rolls_back_money_allocation_receipt_and_closure`** พร้อม Arrange/Actจริงและ assertionsหลักต่อไปนี้ เพิ่มเคสที่ระบุในขั้น3ให้อยู่ในไฟล์เทสต์ของTask

```csharp
Assert.Equal(beforeLedger, afterFailureLedger);
Assert.Equal(beforeReceipts, afterFailureReceipts);
Assert.Equal(beforeClosures, afterFailureClosures);
Assert.Equal(200, sameCommandAfterRecovery.Status);
```

- [x] **2. รัน RED:** `dotnet test backend/TPR10.sln --filter "FullyQualifiedName~FieldAdvanceSettlementRaceTests" --logger "console;verbosity=normal"` ต้องล้มจากพฤติกรรมที่ยังไม่มี/ไม่ตรงข้อกำหนด บันทึกสาเหตุ ไม่ใช้compileerrorจากfixtureผิดหรือserviceไม่ขึ้นเป็นRED หากเป็นriskcoverageที่โค้ดเดิมผ่านอยู่แล้ว บันทึกGREENตามจริงไม่สร้างfailureเทียม
- [x] **3. ลงมือขั้นต่ำตามสัญญา:** ใช้2PGconnectionsและbarrierพิสูจน์waitจริง, forceCOMMITfailure/response-lost/revocationduringdownload; migration7Cclone→7Dtwice/idempotentSQLtwice ไม่mintmoney/grants; backuprestore pendingrefund+closedcase+revokedrights; ย้ายfolderlocalพร้อมdigestไม่ใช่NAS; ทั้งbranchreviewindependentหลังsourcefreezeแก้Importantก่อนfinalG; cleancommittedtreeHTTPSหรือเปิดเผยdirtyfixtureความต่าง ห้ามclaimexactcleanถ้าไม่ใช่
- [x] **4. รัน GREEN:** ใช้คำสั่งfocusedเดียวกับขั้น2 ต้องexit0และassertionsครบ จากนั้นรันชุดตรวจร่วมGบนsourceสุดท้าย
- [x] **5. ตรวจแยกคน:** ส่งแบบ+diff+ผลจริงให้ผู้ตรวจใหม่ ตรวจspecและcodequality แก้Critical/Importantและทดสอบส่วนกระทบก่อนยอมรับTask คงรายการติดตามเดิมอย่างเปิดเผย
- [x] **6. Commit:** stageเฉพาะไฟล์งานที่ตรวจแล้วและตรวจcached diff; ใช้ข้อความ `test: verify module 7d settlement safety and delivery` อัปเดตสถานะและcheckpointตามผลจริง ไม่push/mergeโดยปริยาย

**สถานะ:** ผ่านผู้ตรวจอิสระและชุดตรวจทั้งโครงการ ดู [รายงานส่งมอบ](../../reports/2026-10-31-module-7d-verification.md) และ [คู่มือ](../../runbooks/field-advance-settlements.md) คงรายการติดตามตามรายงานและพักมือถือจริง/NAS จริง

## คำสั่ง HTTPS และการส่งมอบ

Task16–17 เพิ่ม spec allowlist อย่างเจาะจง ต้องไม่มีการstage hunksของ504ที่มีอยู่ก่อนเริ่มงาน ใส่flagสำหรับtestseedแบบopt-inในfixtureที่มีอยู่ ห้ามproductiondefaultsให้สร้างบัญชีหรือสิทธิ์จริง เช็คว่าชื่อflagตรงโค้ดจริงก่อนรัน

ตัวอย่างเรียกตัวrunnerเดิมหลังเพิ่มallowlist (ตัวrunnerเป็นผู้ดูแลweb process/containerของตน ไม่เปิดwebซ้ำทับพอร์ต):

```sh
TPR10_PAYMENT_E2E=1 TPR10_SMOKE_DIAGNOSTICS=1 node --input-type=module -e 'import {runHttpsSmoke} from "./infra/nginx/https-smoke-fixture.mjs"; await runHttpsSmoke({port:"4000",spec:"tests/e2e/field-advance-settlement.spec.ts",e2e:true});'
TPR10_PAYMENT_E2E=1 TPR10_SMOKE_DIAGNOSTICS=1 node --input-type=module -e 'import {runHttpsSmoke} from "./infra/nginx/https-smoke-fixture.mjs"; await runHttpsSmoke({port:"4001",spec:"tests/e2e/field-advance-settlement.spec.ts",e2e:true});'
```

ทำเช่นเดียวกันกับ `field-advance-settlement-review.spec.ts` ทั้งสองพอร์ต ก่อนproductionต้องมี `npm run build` ใหม่หากdevเปลี่ยน `.next` ไม่claimผ่านจากbuildที่หาย ชุดท้ายรวมidentity/scopes/7Apolicy/grant/7Brequests/7Cpayment/supplementที่ถูกกระทบ ไม่เพิ่มtimeout/retryหรือปิดTLSเพื่อให้ผ่าน logชื่อไม่ซ้ำเพื่อไม่เขียนทับfailureหลักฐาน

ผลจบModule: Gครบ, HTTPSผ่านตามscopeจริง, mappingD01–D18ครบ, reviewerอิสระไม่มีCritical/Importantค้าง, reportไทยระบุข้อจำกัดและfollowups, backup/migrationทดสอบฐานจำลอง, checkpointdurable ไม่มีเงินจริง/mobileจริง/NASจริงโดยปริยาย

## แผนที่แบบ → Task และการตรวจแผน

| แบบ | Task |
|---|---|
| §1–4 ขอบเขต/สิทธิ์ | 1,3,13–15 |
| §5 รอบและการคำนวณ | 1,5–6,10 |
| §6 หลักฐานและซ้ำ | 4–5,15–17 |
| §7 จบงานและเวลา | 2,7,16–17 |
| §8 คืนและแก้ | 8–9 |
| §9/12.1 จ่ายผิดและส่วนขาด | 11–12 |
| §10 ปิด/ยอดรวม | 9,13–14 |
| §12.2 ใบเกิน | 10 |
| §12.3 ส่วนเพิ่มเดิม | 13 |
| §12.4 หลังปิด | 9–10,14 |
| §12.5 แก้วัน/เวลาส่งกลับ | 2,7 |
| §12.6 แก้ค่าใช้จ่าย | 10 |
| D01–D18และส่งมอบ | 18ยืนยันcoverageของทุกTask |

ผู้จัดทำตรวจแผนเองตาม Writing Plans: หนึ่งTaskมีผลส่งมอบและวงจรเทสต์ของตน, typesกลางตรงกับconsumer, ReviewFocusทั้ง5มีเจ้าของเทสต์, ไม่รวมbusinesspolicyใหม่หรือimplementationล่วงหน้า รายละเอียดfixture/SQLindexเลือกได้ตามสัญญาและข้อบังคับ แต่ห้ามเปลี่ยนผลเงิน/สิทธิ์โดยอ้างว่าเป็นรายละเอียดเทคนิค

วิธีทำงานที่ผู้ใช้เลือกไว้ยังเป็นแยกผู้ลงมือกับผู้ตรวจทุกTask ไม่ถามเลือกวิธีใหม่ หลังผู้ใช้ตรวจอนุมัติแผนจึงเริ่มTask1 ระหว่างนี้เป็นเอกสารเท่านั้น ไม่อ้างว่าเทสต์7Dผ่านแล้ว

# Module 7C Implementation Plan — แผนพัฒนาการจ่ายเงินทดรองและคำขอส่วนเพิ่ม

> **สำหรับผู้ดำเนินงาน:** ต้องใช้ `superpowers:subagent-driven-development` แยกผู้ลงมือและผู้ตรวจใหม่ทุก Task ตามวิธีที่ผู้ใช้เลือก หากเครื่องมือไม่รองรับ ให้แจ้งก่อนเปลี่ยนเป็น `superpowers:executing-plans` ห้ามเรียกการตรวจของผู้ลงมือเองว่าการตรวจอิสระ ติดตามงานด้วยช่อง `- [ ]`

**เป้าหมาย:** จัดงวด ตรวจ จ่ายภายนอกและบันทึกหลักฐานอย่างไม่ซ้ำหรือเกินวงเงิน พร้อมปรับวงเงินก่อนจ่ายและขอส่วนเพิ่มหลังจ่ายตามกติกาที่อนุมัติ

**สถาปัตยกรรม:** ต่อยอดคำขอและสายอนุมัติ 7B เพิ่มชุดคำขอ ฐานอำนาจอนุมัติ และบัญชีกันวงเงินรายคำขอ แยกบริการงวด หลักฐาน และการสะสาง แต่ใช้ธุรกรรมและประวัติแยกหน้าที่ร่วมกัน ไม่มีบริการสั่งโอนเงินและไม่อ้างว่าธุรกรรมฐานข้อมูลย้อนการจ่ายภายนอกได้

**เทคโนโลยี:** ASP.NET Core/.NET 10, EF Core 10, PostgreSQL/Npgsql, Next.js 15, React 19, TypeScript, xUnit, Testcontainers และ Playwright ตาม lockfile เดิม ไม่เพิ่ม workflow framework

**เอกสารออกแบบ:** `docs/superpowers/specs/2026-10-24-module-7c-payments-supplements-design.md` ผู้ใช้อนุมัติ 24 ตุลาคม 2569 ผู้ลงมือและผู้ตรวจต้องอ่านทั้งแบบและแผน

**สถานะแผน:** Task 1–9 ผ่านแล้ว Task 9 โค้ด `ac1b085` ผู้ลงมือและผู้ตรวจแยกคน ผ่าน Test Backend 2,964 / Frontend 155, Build, Format และ Lint ครบ ดู `docs/reports/2026-10-27-module-7c-task9-verification.md` คงรายการติดตามเดิม พักมือถือจริง/NAS จริง Task 10–15 ยังไม่เริ่ม

## ข้อกำหนดร่วม (Global Constraints)

- ใช้เงินบาทและการคำนวณฐานสิบฝั่งเซิร์ฟเวอร์: จำนวนไม่เกิน 4 ตำแหน่ง อัตรา/ยอดเหมารวมไม่เกิน 2 ตำแหน่ง ปัดแต่ละบรรทัดเป็นสตางค์แบบหลักที่สามตั้งแต่ 5 ปัดขึ้น แล้วรวมตาม 7B ไม่ปัดข้อมูลนำเข้าที่เกินความละเอียดเงียบ ๆ
- ยอดงวดและยอดจ่ายเป็นจำนวนบวกไม่เกิน 2 ตำแหน่ง ตรวจ overflow และขอบเขตชนิดข้อมูล ไม่มีการสร้างเพดานธุรกิจใหม่ขึ้นเอง
- `ยอดจ่ายสำเร็จสะสม + ยอดกันไว้ของงวดที่ยังไม่เสร็จ <= วงเงินอนุมัติที่ใช้จ่ายได้`
- การตรวจผ่านและการกันวงเงินไม่เท่ากับจ่ายเงินจริง ระยะแรกเจ้าหน้าที่โอนเงินหรือจ่ายเงินสดภายนอกระบบ ไม่มีการสั่งโอนผ่านธนาคารอัตโนมัติ
- จ่ายให้ผู้รับผิดชอบเงินเท่านั้น ทั้งโอนและเงินสด เปลี่ยนยอด/ช่องทาง/บัญชีต้องมีเหตุผลและตรวจใหม่เสมอ
- ก่อนมีการจ่ายจริงปรับคำขอเดิมได้ หลังจ่ายจริงแม้บางส่วนต้องขอใหม่เฉพาะส่วนเพิ่ม โครงการและผู้รับผิดชอบเดิม
- ฐานอำนาจอนุมัติใช้วงเงินหลักและส่วนเพิ่มที่อนุมัติแล้วรวมส่วนเพิ่มครั้งนี้ แต่จ่ายได้เฉพาะวงเงินของแต่ละคำขอ ไม่รวมฉบับซ้ำ
- มีส่วนเพิ่มที่ส่งแล้วและยังพิจารณาไม่เสร็จได้หนึ่งรายการต่อชุด รวมส่งกลับ/พัก ไม่จำกัดจำนวนงวดที่กันวงเงินได้ถูกต้อง
- ใช้สิทธิ์ปัจจุบันและประวัติตัวบุคคลทั้งชุด ไม่เพิ่มสิทธิ์ให้ admin/สมาชิกทีม ผู้รับผิดชอบ/ผู้ยื่นแทนห้ามอนุมัติ ตรวจ หรือจ่าย และห้ามสลับสามหน้าที่หลังมีส่วนร่วม
- ผลไม่ชัดต้องคงล็อก/ยอดกันไว้ ไม่มี timeout ปลดเอง จ่ายผิดยอด/ผิดคนระงับเริ่มจ่ายทั้งชุด ไม่มีคำสั่งปลดเหตุนี้ใน 7C
- ยุติเฉพาะยอดยังไม่จ่าย ไม่ล้างยอดจ่าย ไม่ลดฐานสะสมเดิม และไม่ปิดคำขอทางการเงิน เคลียร์/คืนเงิน/ปิดคำขอเป็น 7D
- เอกสารและข้อความผู้ใช้เป็นภาษาไทย dev 4000, production preview 4001, HTTPS ทดสอบผ่าน 4443 ไม่ลด TLS/session/MFA เพื่อให้ผ่าน
- คง M2-R1/R2-C1 และ HTTPS 504 พักมือถือจริง/NAS จริง ไม่รวมการแก้รายการเหล่านี้หรือการ push/merge/deploy โดยปริยาย

## จุดเน้นสำหรับผู้ตรวจ (Review Focus)

1. คำสั่งเริ่มจ่ายสำเร็จแต่ response หาย: อ่านผลคำสั่งเดิม ไม่ส่งผู้ใช้ไปโอนซ้ำ — เทสต์ `StartResponseLost` ใน Task 6 และ UI Task 13
2. ถอนสิทธิ์/หมดอายุ/ใช้บัญชีที่สองระหว่างรอผลหรือดาวน์โหลด: ตรวจบัญชีผู้ลงมือและตัวบุคคลใหม่ ไม่คืนข้อมูลจาก receipt เก่า — Task 2, 3, 12
3. การย้ายที่เก็บชนการอ่านหรือไฟล์หายหลังจ่ายจริง: ไม่เปลี่ยน ID หลักฐาน ไม่เปิดจ่ายซ้ำ ไม่เปลี่ยนที่อ่านก่อนตรวจ digest — Task 5, 7
4. แท็บเก่าถือฉบับที่ตรวจผ่านแต่มีการลดวงเงิน/ตั้งเหตุผิดปกติ: คำสั่ง stale ต้องไม่ผ่าน และไม่ล้างเหตุพักอื่น — Task 4, 9, 13
5. ยุติยอดเหลือแล้วสร้างส่วนเพิ่มหลายทอด: รวมวงเงินอนุมัติเดิมครั้งเดียว ไม่รวมยอดสะสมซ้ำหรือหักยอดยุติเพื่อลดระดับ — Task 10, 11, 14

## ภาพรวม 15 Task

| Task | ผลงานที่ตรวจรับได้ | ต้องผ่านก่อน |
|---|---|---|
| 1 | โครงสร้างข้อมูล เงิน และ migration ที่รักษาข้อมูลเดิม | แบบที่อนุมัติ |
| 2 | สิทธิ์และการแยกหน้าที่ทั้งชุดคำขอ | 1 |
| 3 | ธุรกรรม คำสั่งกันซ้ำ และบัญชีกันวงเงิน | 1–2 |
| 4 | จัดงวด ส่งตรวจ ส่งกลับ และตรวจผ่าน | 3 |
| 5 | หลักฐานส่วนตัวและที่เก็บที่เปลี่ยนได้ | 2–3 |
| 6 | เริ่มจ่ายแบบล็อกคนเดียวและบันทึกจ่ายครั้งเดียว | 4–5 |
| 7 | สะสางผลไม่ชัด ระงับทั้งชุด และรับช่วงงาน | 6 |
| 8 | ยกเลิกงวดยังไม่จ่ายและแก้ข้อมูลหลังจ่าย | 7 |
| 9 | ปรับวงเงินก่อนจ่ายและกลับใช้เงื่อนไขเดิมอย่างมีผู้ตรวจ | 8 |
| 10 | คำขอส่วนเพิ่มและฐานอำนาจอนุมัติสะสม | 9 |
| 11 | ยุติยอดที่ยังไม่จ่ายโดยรักษาประวัติและฐานสะสม | 10 |
| 12 | API อ่าน/สั่งงาน/หลักฐานและ client ที่ไม่รั่วสิทธิ์ | 1–11 |
| 13 | หน้าจอฝ่ายการเงินครบวงจรงวดจ่าย | 12 |
| 14 | หน้าจอปรับวงเงิน ส่วนเพิ่ม และยุติยอด | 13 |
| 15 | ตรวจรวม แข่งพร้อมกัน migration และคู่มือส่งมอบ | 1–14 |

ลำดับนี้เป็นสายงานเดียว ไม่ควรทำ Task ที่พึ่งกันพร้อมกัน แต่แยกผู้ตรวจแต่ละ Task ได้ งานหนึ่งจบเมื่อ Red → Green → ตรวจตามแบบ → ตรวจคุณภาพ → แก้ข้อพบ → ชุดตรวจร่วมผ่าน → commit เฉพาะงานนั้น ห้ามข้าม Critical/Important ที่ยังไม่แก้

## แผนที่ไฟล์และสัญญาที่ใช้ร่วมกัน

ฐานตรวจคือ commit `1307976` บน worktree Module 7 โค้ดผลิตภัณฑ์ฐานเดียวกับ `71c006d` อย่าเชื่อสถานะสำเร็จในแผนเก่าแทนการรันใหม่ ก่อนลงมือใช้ worktree ที่แยกงานได้และอ่าน AGENTS ของที่นั้น ไม่ reset หรือรวบงาน HTTPS 504 ที่ค้างเข้ามา

พาธย่อในแผนนี้ขยายแบบตรงตัวดังนี้ (ไม่ใช่ตัวแปร shell):

- `FA/` = `backend/src/TPR10.Api/FieldAdvances/`
- `DB/` = `backend/src/TPR10.Api/Data/`
- `IT/` = `backend/tests/TPR10.Api.IntegrationTests/`
- `UI/` = `app/portal/field-advances/`
- `CLIENT/` = `lib/field-advances/`

ทุกไฟล์ที่ระบุ Create เป็นข้อเสนอชื่อใหม่ ไม่อ้างว่ามีอยู่แล้ว ไฟล์บริการให้รับ actor จาก session ไม่รับ person/role ที่ client อ้าง ตัวบริการคืน `Task<FinanceResult<T>>` ตามรูปแบบเดิม และรับ `CancellationToken ct` ตัวสุดท้าย

### แบบข้อมูลใหม่ที่ Task 1 เป็นเจ้าของ

- `PaymentCommand(Guid CommandId, long FamilyVersion, long RequestVersion, long? InstallmentVersion, string Reason)` แยกจาก `RequestCommand` เดิมเพื่อไม่ทำ receipt เก่าเสีย
- `PaymentDestination(string Channel, Guid RecipientPersonId, string? BankCode, string? AccountNumber, string? AccountHolder)` ใช้ `Transfer`/`Cash`; เก็บรายละเอียดเฉพาะระบบการเงิน ไม่ใส่ audit/log ทั่วไป
- `InstallmentInput(string Amount, PaymentDestination Destination)` ตัวเลข wire เป็นข้อความ ASCII ฐานสิบ ไม่ exponent/comma/JSON number
- `ActualPaymentInput(string Amount, Guid RecipientPersonId, DateOnly PaidOn, string? Reference, Guid[] EvidenceIds)`
- `PaymentMetadataInput(DateOnly PaidOn, string? Reference, Guid[] EvidenceIds)` ไม่มี Amount/Recipient/Channel/Account
- `PaymentOutcomeInput(string Outcome, ActualPaymentInput? Actual, Guid[] EvidenceIds)` Outcome = `Matched`, `Unpaid`, `Unknown`, `Mismatch`
- `PaymentView(Guid RequestId, Guid? InstallmentId, long FamilyVersion, long RequestVersion, long? InstallmentVersion, string State, decimal Approved, decimal Spendable, decimal Reserved, decimal Paid, decimal Available, string[] HoldCodes)` ใช้ภายใน; HTTP แปลง decimal เป็นข้อความสองตำแหน่ง
- `ApprovalBasis(decimal Amount, ApprovalBasisItem[] Items)` และ `ApprovalBasisItem(Guid RequestId, Guid RevisionId, decimal ApprovedAmount)` เป็น snapshot จาก server; UI ฉายเฉพาะเลขคำขอ/ยอดที่อนุญาต
- `PaymentAccessDecision(bool Allowed, Guid? PersonId)`; `EvidenceTicket(Guid Id, string Sha256, long Length, string ContentType, string State)` ไม่มี path
- `BalanceSnapshot(decimal Approved, decimal Spendable, decimal Reserved, decimal Paid)` และ `BalanceMath.Available(BalanceSnapshot value): decimal` ใช้ checked arithmetic; ไม่ให้ผลติดลบผ่านไปใช้

ตารางใหม่แยกเป็น `AdvanceFamily`, `AdvancePaymentAccount`, `AdvanceInstallment`, `AdvanceInstallmentRevision`, `AdvanceReservationEntry`, `AdvancePaymentAttempt`, `AdvancePaymentResult`, `AdvancePaymentProposal`, `AdvancePaymentParticipation`, `AdvancePaymentHold`, `AdvancePaymentEvent`, `AdvanceEvidence`, `AdvanceEvidenceLocation` การมี proposal ไม่ใช่ผลยืนยัน และแต่ละ proposal ผูก hash/รุ่น/หลักฐานที่ผู้ตรวจเห็น

Request เพิ่ม `FamilyId`, `RelationKind` (`Normal`/`Supplement`), `SupplementOfRequestId` โดย `SourceRequestId` ยังเป็น copy reference; Revision เพิ่ม `ApprovalBasisAmount`, `ApprovalBasisJson` ไม่แทน `Total` ที่เป็นเงินคำขอนั้น Family เก็บรุ่นและช่อง `PendingSupplementId`; Account เก็บฉบับอนุมัติที่ใช้จ่ายจริงแยกจากฉบับแก้ไขที่กำลังพิจารณา และ `EverPaid` ที่ไม่ย้อนกลับเมื่อคืนเงินในอนาคต

สถานะงวด: `Draft`, `PendingReview`, `Returned`, `Reviewed`, `Paying`, `OutcomePending`, `CancelPending`, `Cancelled`, `Paid` เหตุพักแยกหลายรายการ ไม่ใช้ string เดียวเขียนทับทุกเหตุ การจ่ายผิดเก็บ hold `PaymentMismatch` ระดับ Family; 7C ไม่มี transition ปลด hold นี้

### ธุรกรรมและข้อบังคับฐานข้อมูล

คง advisory lock `7241002` ของบริการเขียนการเงินเดิม แล้วล็อก Family → Request → Installment ตาม UUID เรียงคงที่เมื่อมีหลายแถว ใช้ลำดับเดียวกันในบริการ 7B, worker, grant และ 7C ไม่ตัด advisory lock เพื่อแก้ performance ในงานนี้ เมื่อคำสั่งแตะทั้งชุดต้องเพิ่ม FamilyVersion; replay ตรวจสิทธิ์ก่อนเทียบรุ่นเพื่ออ่านคำสั่งที่เคยสำเร็จได้ แต่ไม่ข้ามการถอนสิทธิ์

DB บังคับ scope/FK, จำนวนบวก, revision ปัจจุบันหนึ่งฉบับ, attempt ที่ยังเปิดหนึ่งครั้งต่องวด, result สำเร็จหนึ่งรายการต่องวด, command ID ไม่ซ้ำ และ ledger entry ไม่ซ้ำตาม command/action/งวด คำขอส่วนเพิ่มค้างหนึ่งรายการใช้ Family.PendingSupplementId ภายใต้ row lock พร้อม FK ตรวจในธุรกรรมเดียวกับสถานะ ไม่สร้าง slot จากร่าง

`AdvanceReservationEntry` เป็น append-only: Reserve/Release/Consume; Consume ลด reserved และเพิ่ม paid ในธุรกรรมเดียว Audit/event/receipt/account/participation สำเร็จร่วมกัน ล้มเหลวรวม commit ต้อง rollback และ `ChangeTracker.Clear()`; transaction ไม่ครอบคลุมการโอนภายนอกและไม่อ่าน/ย้ายไฟล์ใหญ่ขณะถือ lock

## ชุดตรวจร่วม G ก่อนปิดทุก Task

รันจาก repository root หลัง review และแก้รอบสุดท้าย ใช้ dependency ตาม lockfile ถ้ายังไม่ restore ให้ restore ก่อน ห้าม `--no-build` อ้าง assembly เก่า:

```bash
dotnet build backend/TPR10.sln --no-restore --verbosity minimal
dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm test
npm run build
npm run lint
git diff --check
```

ทุกคำสั่ง exit 0, ไม่มี failed/skip ที่ซ่อนอยู่, Build ไม่มี error และ Lint max-warnings 0 เก็บเวลา/commit/จำนวน tests/exit code โดยไม่เก็บ secrets ช่วง TDD ใช้ filtered tests ได้ แต่ไม่แทน G เมื่อปิด Task ถ้าสภาพแวดล้อมรันไม่ได้ให้บอกว่ายังติด gate ไม่ประกาศเสร็จ

คำสั่ง Red/Green ของ Task backend ใช้รูปแบบต่อไปนี้ โดยชื่อ class ระบุในแต่ละ Task และไม่มี `--no-build`:

```bash
dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvancePaymentSchemaTests --verbosity normal
```

แต่ละ Task มี Step สุดท้ายร่วม: ผู้ตรวจใหม่ตรวจ spec/โค้ด/เทสต์ → แก้ข้อพบ → รัน G → `git add` เฉพาะรายชื่อไฟล์ Task ที่ตรวจ diff แล้ว → `git commit -m "<ข้อความใน Task>"` ห้าม `git add .` ห้าม push/merge อัตโนมัติ

### Task 1: โครงสร้างข้อมูลและ migration ที่ไม่สร้างเงินให้ข้อมูลเก่า

**ไฟล์:** Create `FA/Payments/PaymentContracts.cs`, `FA/Payments/BalanceMath.cs`, `FA/Data/PaymentEntities.cs`, `FA/Data/PaymentModelConfiguration.cs`, `IT/FieldAdvancePaymentSchemaTests.cs`, `IT/FieldAdvancePaymentFixture.cs`; Modify `FA/Data/RequestEntities.cs`, `FA/Data/RequestModelConfiguration.cs`, `DB/Tpr10DbContext.cs`; Generate migration ชื่อ `AddFieldAdvancePayments` ใน `DB/Migrations/` พร้อม Designer และ `Tpr10DbContextModelSnapshot.cs` (timestamp ใช้ EF สร้างจริง)

**Interfaces:** รับ schema 7B; ส่งชนิดร่วมข้างต้นและตารางให้ Task 2–11 Fixture ใช้ `FieldAdvanceFixture`/`PostgresFixture` เดิม มี `CreateContext(): Tpr10DbContext`, `SeedApprovedAsync(decimal amount, CancellationToken ct): Task<Guid>` สร้างเจ้าของ ผู้อนุมัติ ผู้ตรวจ ผู้จ่ายคนละคนและคืน RequestId ใช้ฐานข้อมูลทดสอบเท่านั้น

- [x] Step 1 เขียน `LegacyCopyRemainsIndependent` ตรวจ migration ข้อมูล 7B: copy มี Family ของตัวเอง, ไม่มี ledger/result; `BalanceNeverNegative` ใช้ `Assert.Equal(8000m, BalanceMath.Available(new(10000m,10000m,0m,2000m)));` และยอดเกินต้อง throw ไม่ truncate
- [x] Step 2 รัน filter `FieldAdvancePaymentSchemaTests` ต้อง FAIL เพราะโครงสร้าง/ข้อบังคับยังไม่มี ไม่ใช่เพราะ Docker ล้ม
- [x] Step 3 เพิ่มชนิด/ตาราง/constraints ตามสัญญา Backfill หนึ่ง Family ต่อคำขอเดิม, Normal, basis=Total, EverPaid=false; approved account ใช้ฉบับอนุมัติเดิม ไม่มีเงินเพิ่ม ร่างยังไม่มีวงเงินใช้จ่าย
- [x] Step 4 รัน Green รวม migration จาก schema 7B, precision/overflow, FK ข้าม scope, unique attempt/result และ no duplicate current revision
- [x] Step 5 ผู้ตรวจใหม่ + G + commit `feat: add field advance payment foundation` (ลำดับหลักฐานและข้อสังเกต Minor ตามรายงาน Task 1)

### Task 2: สิทธิ์และประวัติหน้าที่ทั้งชุด

**ไฟล์:** Create `FA/Payments/PaymentAccess.cs`, `FA/Payments/FamilyParticipation.cs`, `IT/FieldAdvancePaymentAccessTests.cs`; Modify `FA/Requests/RequestSeparation.cs`, `FA/Requests/RequestAccess.cs`, `FA/Requests/ApprovalRouteResolver.cs`, `FA/Requests/RequestReviewService.cs`

**Interfaces:** `PaymentAccess.CheckAsync(Guid actor, Guid requestId, string action, CancellationToken ct): Task<PaymentAccessDecision>`; `FamilyParticipation.ReadAsync(Guid familyId, CancellationToken ct): Task<RequestParticipationHistory>` รวมประวัติเดิมและใหม่; `RecordAsync(Guid familyId, Guid requestId, Guid personId, string role, CancellationToken ct): Task` ใช้ใน transaction เท่านั้น action = `read`, `prepare`, `check`, `start`, `record`, `reconcile`, `takeover`, `terminate-propose`, `terminate-confirm`, `evidence-read`

- [x] Step 1 เขียน `SamePersonCannotSwitchAcrossFamily` ตั้งผู้อนุมัติใน root แล้วลองตรวจ/จ่าย supplement: `Assert.False(decision.Allowed);` รวม owner/delegate, returned checker, ผู้เตรียมงวด และ alias account; ทดสอบหมดอายุ/ถอนและให้สิทธิ์ใหม่ไม่ล้างประวัติ
- [x] Step 2 รัน filter `FieldAdvancePaymentAccessTests` ให้ FAIL จากขอบเขตเดิมที่ยังเป็นรายคำขอ
- [x] Step 3 ใช้ FinancePersonResolver + grant ของ actor ปัจจุบันและ scope จริง ไม่เอา grant บัญชีอื่นมาให้ actor; รวม history ด้วย person ID ตรวจ request.approve/finance.check/payment.record แยกกัน รายการ/รายละเอียดใช้บทบาทที่ได้รับจริง ไม่ให้ read กว้างจาก family membership
- [x] Step 4 Green รวมสองบทบาทแข่งกัน ต้องล็อกและบันทึก participation ตั้งแต่เตรียม/ตรวจส่งกลับ; อ่านรายงานไม่ให้อำนาจลงมือ ไม่มีผู้เข้าเงื่อนไขให้ปฏิเสธ/พัก ไม่ grant เอง
- [x] Step 5 ผู้ตรวจใหม่ + G + commit `feat: enforce advance family separation` (ลำดับหลักฐานตามรายงาน Task 2; Full G ก่อน review บนโค้ดเดียวกัน)

### Task 3: บัญชีกันวงเงินและคำสั่งที่มีผลครั้งเดียว

**ไฟล์:** Create `FA/Payments/PaymentCommandExecutor.cs`, `FA/Payments/ReservationLedger.cs`, `IT/FieldAdvancePaymentAtomicityTests.cs`; Modify `FA/Requests/RequestCommandExecutor.cs` ให้ใช้ลำดับ lock ร่วมโดยไม่เปลี่ยน receipt 7B

**Interfaces:** `PaymentCommandExecutor.ExecuteAsync(Guid actor, Guid requestId, Guid? installmentId, string action, PaymentCommand meta, string payloadHash, Func<CancellationToken, Task<FinanceResult<PaymentView>>> body, CancellationToken ct): Task<FinanceResult<PaymentView>>`; `ReservationLedger.ReserveAsync/ReleaseAsync/ConsumeAsync(Guid requestId, Guid installmentId, decimal amount, Guid commandId, CancellationToken ct): Task<BalanceSnapshot>` ต้องถูกเรียกใน executor เท่านั้น

- [x] Step 1 `ConcurrentReservationsCannotExceedCeiling` วงเงิน 10000 ส่งสองงวด 6000 พร้อมกันผ่านสอง DbContext: `Assert.Equal(1, results.Count(x => x.Status == 200));` และ `Assert.Equal(6000m, account.Reserved);` เพิ่ม replay ต่าง payload/actor และ injected SaveChanges/Commit/audit failure
- [x] Step 2 filter `FieldAdvancePaymentAtomicityTests` Red
- [x] Step 3 ทำ locks/receipt/recheck สิทธิ์ก่อน commit และ clear tracked state ทั้งสำเร็จ/ล้มเหลว ยอดเป็น checked decimal, debit/credit event append-only; hash ครอบคลุมทุก input รวม evidence/version ไม่บันทึกบัญชีลง log
- [x] Step 4 Green ตรวจ rollback จาก context ใหม่ ledger/account/audit/receipt/participation ไม่ค้าง; replay หลังถอนสิทธิ์ denied แม้สำเร็จมาก่อน; consume ซ้ำไม่เพิ่ม paid
- [x] Step 5 ผู้ตรวจใหม่ + G + commit `feat: add atomic advance reservations`

### Task 4: จัดงวดและตรวจใหม่ทุกครั้งที่ข้อมูลจ่ายเปลี่ยน

**ไฟล์:** Create `FA/Payments/InstallmentService.cs`, `FA/Payments/InstallmentReviewService.cs`, `IT/FieldAdvanceInstallmentTests.cs`; Modify `FA/FieldAdvanceRegistration.cs`

**Interfaces:** `CreateAsync(Guid actor, Guid requestId, InstallmentInput input, PaymentCommand meta, CancellationToken ct)`; `UpdateAsync(Guid actor, Guid installmentId, InstallmentInput input, PaymentCommand meta, CancellationToken ct)`; `SubmitAsync(Guid actor, Guid installmentId, PaymentCommand meta, CancellationToken ct)`; `ReviewAsync(Guid actor, Guid installmentId, string outcome, PaymentCommand meta, CancellationToken ct)` คืน `FinanceResult<PaymentView>`; outcome = `Approve`/`Return`

- [x] Step 1 `ReturnedKeepsReservation` ตรวจ `Assert.Equal(6000m, view.Reserved);` เมื่อส่งกลับ; ลดเป็น 4000 => reserved4000, เพิ่มเป็น11000 rejected; ลด/เปลี่ยนบัญชีเดิม/ช่องทางหลังตรวจผ่าน => ไม่เหลือสถานะ Reviewed
- [x] Step 2 filter `FieldAdvanceInstallmentTests` Red
- [x] Step 3 draft ไม่กันเงิน ส่งตรวจจึงกัน เก็บ revision/hash/result เก่า พร้อม reason ทุกการเปลี่ยน; ตรวจ owner-only recipient และข้อมูลบัญชีครบสำหรับ Transfer/Cash; ผลตรวจผูก revision ไม่ผูกแค่งวด
- [x] Step 4 Green รวม reason ว่าง, amount `0`, `-1`, `1.001`, `1e3`, numeric JSON, overflow, update จากแท็บเก่า, ผู้ตรวจเป็นผู้เตรียมเดียวกัน; reserve ไม่ถูก release เมื่อ Return
- [x] Step 5 ผู้ตรวจใหม่ + G + commit `feat: add installment preparation and review`

### Task 5: หลักฐานการเงินส่วนตัวและย้ายที่เก็บโดยไม่เสียประวัติ

**ส่วนเพิ่มที่ผู้ใช้อนุมัติ 24 ตุลาคม 2026:** เพิ่มตัวตรวจ PDF แยก process พร้อมจำกัดหน่วยความจำและเวลา ไม่ตรวจ PDF ภายใน API โดยตรง รายละเอียดทางเทคนิคต่อไปนี้เป็นค่าเริ่มต้นที่ผู้ลงมือและผู้ตรวจต้องพิสูจน์ ไม่ใช่ค่าที่ผู้ใช้กำหนดเอง

- ใช้ console worker `backend/src/TPR10.PdfValidator/` (.NET 10, PdfPig 0.1.16, Apache-2.0 พร้อมประกาศ license) ใน container ชั่วคราวต่อไฟล์ เพิ่ม Dockerfile และโครงการใน solution พร้อม lockfile; เพิ่ม `FA/Evidence/PdfValidationProcess.cs` และ interface ที่จำเป็น รวมเทสต์ใน `IT/FieldAdvanceEvidenceTests.cs` หรือ `IT/FinancePdfValidationTests.cs` และ test utilities ในโฟลเดอร์เทสต์เท่านั้น
- จำกัดด้วยระบบปฏิบัติการ: memory 512 MiB, memory+swap เท่ากับ memory, CPU 1, pids 64, deadline 15 วินาที รวมเริ่ม process/ส่งข้อมูล/รับผล; สูงสุด 2 งานพร้อมกันและไม่สะสมคิวไม่จำกัด รับผล stdout ไม่เกิน 4 KiB และจำกัด stderr เช่นกัน ค่า configuration ต้องตรวจช่วงและปฏิเสธค่าที่ทำให้ไม่มีเพดาน
- รัน non-root, read-only root, no network, drop capabilities และ no-new-privileges; ไม่ mount โฟลเดอร์หลักฐาน socket หรือ secrets เข้า worker ส่ง bytes ทาง stdin และรับ verdict ขนาดจำกัด ไม่ใช้ shell interpolation ใช้ image ID ที่เตรียมไว้และตรวจสอบแล้ว ไม่ pull/build ระหว่างรับไฟล์
- API ใช้ trusted host runner ที่เรียก Docker ได้ ซึ่งเป็นขอบเขตสิทธิ์สูง ต้องระบุ prerequisite และข้อจำกัดในคู่มือ ไม่เพิ่มสิทธิ์เครื่อง production ในงานนี้ ถ้า runtime/image/ข้อจำกัดทรัพยากรไม่พร้อม ให้ปฏิเสธ PDF แบบ fail closed ไม่มี fallback ตรวจใน API
- timeout/cancellation/output เกินกำหนด/OOM/worker crash ให้ปฏิเสธ ต้องหยุดและเก็บกวาด container ของงานนั้นโดยเฉพาะ ไม่เพียง kill Docker CLI แล้วทิ้ง worker ทำงานต่อ ตรวจการเก็บกวาดหลังยกเลิกและการตรวจไฟล์ปกติครั้งถัดไป
- ตัว parser ตรวจโครงสร้างและ graph อย่างมีขอบเขต ปฏิเสธ encryption, embedded files, JavaScript และโครงสร้างที่ตรวจไม่ได้; เพดาน container เป็นแนวป้องกันชั้นนอก ไม่ใช้แทน validation
- TDD เพิ่มไฟล์ PDF ปกติ/เสียหาย/เนื้อหาต้องห้าม และเทสต์ resource/time/cancellation กับ container จริงโดย fixture ที่จำกัดขอบเขต เทสต์ต้องพิสูจน์การบังคับใช้และ fail closed ไม่ใช่แค่ตรวจข้อความ command; ห้าม skip เงียบเมื่อ Docker ไม่พร้อม Build worker image และรันเทสต์เหล่านี้เพิ่มจาก G
- Locator ที่ controller เลือก: `v1:<storage-guid-N>:<object-guid-N>` ตรวจรูปแบบเข้มงวดและ mapping root จาก config ไม่รับ path จากผู้ใช้ ไม่ต้องเพิ่ม migration ฐานข้อมูล การย้ายตรวจตำแหน่งปัจจุบันซ้ำก่อน atomic switch และคงไฟล์/แถวเดิม
- งานนี้ยังจำกัด Task 5 คง M2-R1/R2-C1, HTTPS 504 และ Minor log; พักมือถือจริง/NAS จริง ไม่เริ่ม Task 6 ไม่ push/merge/deploy
- กติกา StageAsync สำหรับหลักฐานจ่ายใน 7C: ต้องผ่านสิทธิ์ `payment.record` และการแยกหน้าที่ปัจจุบัน (`PaymentAccess` action `record`) ไม่ใช้สิทธิ์อ่านแทนสิทธิ์เขียน ตรวจสิทธิ์ก่อน I/O แล้วปล่อย transaction ระหว่างตรวจ/เก็บไฟล์ จากนั้นตรวจสิทธิ์ซ้ำใน transaction สั้นและบันทึกหลักฐานพร้อม `Payer` participation แบบ atomic เมื่อสำเร็จเท่านั้น การตรวจไฟล์ล้มเหลว/ถอนสิทธิ์ก่อน commit/rollback ไม่สร้าง participation การเก็บกวาดรายงานไฟล์ตกค้างแต่ไม่ลบ ถือการเตรียมหลักฐานสำเร็จเป็นการลงมือในหน้าที่ตามแบบข้อ 8 ไม่เพิ่มสิทธิ์อัปโหลดให้ผู้ตรวจหรือผู้มีสิทธิ์อ่าน และยังไม่ใช่ workflow เคลียร์เงิน 7D

**ไฟล์:** Create `FA/Evidence/FinanceEvidenceOptions.cs`, `FA/Evidence/FinanceEvidenceService.cs`, `FA/Evidence/IFinanceEvidenceStore.cs`, `FA/Evidence/FolderFinanceEvidenceStore.cs`, `FA/Evidence/FinanceEvidenceMigration.cs`, `FA/Evidence/FinanceEvidenceReconciler.cs`, `IT/FieldAdvanceEvidenceTests.cs`; Modify `FA/FieldAdvanceRegistration.cs` และ `backend/src/TPR10.Api/appsettings.json` เฉพาะค่าที่ไม่ใช่ secrets

**Interfaces:** `StageAsync(Guid actor, Guid requestId, Stream content, string declaredType, CancellationToken ct): Task<FinanceResult<EvidenceTicket>>`; `ValidateLinksAsync(Guid actor, Guid requestId, Guid[] evidenceIds, CancellationToken ct): Task<bool>`; `ReadAsync(Guid actor, Guid evidenceId, CancellationToken ct): Task<FinanceResult<byte[]>>`; store `WriteImmutableAsync(Guid storageId, string key, Stream content, CancellationToken ct): Task<EvidenceTicket>`, `ReadVerifiedAsync(Guid storageId, string key, string sha256, long length, CancellationToken ct): Task<byte[]>`; migration `CopyAndSwitchAsync(Guid evidenceId, Guid targetStorageId, CancellationToken ct): Task<bool>` เป็นคำสั่งผู้ปฏิบัติที่มีสิทธิ์ filesystem ไม่ให้ admin สิทธิ์อ่านการเงินผ่าน API

- [x] Step 1 `MovedEvidenceKeepsIdentity` และ `PendingLinkNeverDeletesReferencedFile`: `Assert.Equal(before.Id, after.Id); Assert.Equal(before.Sha256, after.Sha256);` ทดสอบ digest ผิด/ไฟล์หาย/commit ล้มเหลวไม่ switch หรือ delete ไฟล์ที่อ้างแล้ว
- [x] Step 2 filter `FieldAdvanceEvidenceTests` Red
- [x] Step 3 ค่าเริ่มต้น configurable: JPEG/PNG/PDF เท่านั้น, 10 MiB ต่อไฟล์, 10 ไฟล์และรวม 50 MiB ต่อข้อเสนอ, ภาพไม่เกิน 25 ล้านพิกเซล PDF เป็น download attachment ไม่ render inline ตรวจโครงสร้าง/เนื้อหาไม่เชื่อ extension/MIME อย่างเดียว; ไม่รับ SVG/HTML/encrypted PDF และ PDF ฝังไฟล์/JavaScript; เลือก parser ที่ปลอดภัยและตรวจ license ก่อนเพิ่ม dependency หากยังไม่มีตัวตรวจ PDF ต้องหยุด gate ของ feature PDF ไม่อ้างรองรับด้วย magic bytes อย่างเดียว
- [x] Step 4 เก็บ opaque ID+storage ID, root allowlist จาก configuration, ป้องกัน traversal/symlink, immutable, no public URL; staged file ตรวจเสร็จก่อน transaction ผูกผลจ่าย ย้าย copy→verify→atomic location switch คงต้นฉบับ; reconciler รายงาน orphan เท่านั้น ระยะแรกไม่ลบอัตโนมัติ
- [x] Step 5 Green รวม fake MIME, oversized streaming upload, duplicate IDs, evidence ข้ามคำขอ, revoked reader, ทดสอบ local สองโฟลเดอร์และ adapter จำลอง NAS offline ไม่อ้างทดสอบ NAS จริง
- [x] Step 6 ผู้ตรวจใหม่ + G + commit `feat: add private finance evidence storage`

ผล Task 5: โค้ด `4774d82`, focused 74 ผ่าน, Backend 2,815 และ Frontend 155 ผ่าน, G ทุกคำสั่ง exit 0 และ 24 ไฟล์ตรงกับ checksum ที่ review แล้ว ตัวตรวจยอมรับ PDF เฉพาะ subset ที่ตรวจได้ รายการ cleanup ที่ผล create ไม่แน่นอนต้อง reconcile ก่อน recycle runner ตามคู่มือ ส่วน HTTP attachment/private/no-store/nosniff เป็น gate ของ Task 12 เมื่อเพิ่ม endpoint ยังไม่มีการเปิด route ใน Task 5

### Task 6: เริ่มจ่ายและบันทึกผลที่ตรงงวดครั้งเดียว

**ขอบเขตลงมือ 27 ตุลาคม 2569:** ทำเฉพาะบริการเริ่ม/บันทึกผล ไม่เพิ่ม endpoint หรือโอนเงินภายนอก อนุญาตแก้ `FA/FieldAdvanceRegistration.cs` เพื่อลงทะเบียนบริการ และขยาย fixture ทดสอบเดิมเฉพาะที่จำเป็น รักษา receipt/locking/participation ของ executor เดิม กรณีต้องเปลี่ยน contract ของ executor หรือ evidence ให้แจ้ง controller พร้อมเหตุผลก่อนแก้

**ข้อควรตรวจจาก Task 5:** `ValidateLinksAsync` ทำ file I/O จึงต้องเรียกก่อน financial transaction และตรวจ Ready/request/digest/length/จำนวนซ้ำอีกครั้งใน transaction ที่ผูกผลจ่าย ผลเตรียมไฟล์ไม่ใช่สิทธิ์ถาวร การ replay คำสั่งที่ commit แล้วต้องตรวจสิทธิ์ปัจจุบันและคืน receipt เดิมได้ โดยไม่ Consume ซ้ำหรือสั่งจ่ายใหม่ แม้ไฟล์ภายหลังไม่พร้อม; แยก replay จากคำสั่งใหม่ที่หลักฐานยังไม่ผ่านให้ชัดเจน กรณียอดหรือผู้รับไม่ตรงปฏิเสธผลปกติและคง attempt/ยอดกันไว้ ไม่สร้าง success หรือ implement workflow Task 7 ล่วงหน้า

**ไฟล์:** Create `FA/Payments/PaymentExecutionService.cs`, `IT/FieldAdvancePaymentExecutionTests.cs`

**Interfaces:** `StartAsync(Guid actor, Guid installmentId, PaymentCommand meta, CancellationToken ct)`; `RecordAsync(Guid actor, Guid installmentId, ActualPaymentInput input, PaymentCommand meta, CancellationToken ct)` คืน `FinanceResult<PaymentView>`

- [x] Step 1 `StartResponseLost` จำลอง commit start สำเร็จแต่ response หาย ส่ง command เดิมและใหม่: `Assert.Single(attempts); Assert.Equal(0m, account.Paid);` record3000 สำเร็จครั้งเดียว => `Assert.Equal(3000m, account.Paid); Assert.Equal(0m, account.Reserved);`
- [x] Step 2 filter `FieldAdvancePaymentExecutionTests` Red
- [x] Step 3 start ตรวจ revision/destination/สิทธิ์/hold ล่าสุด ล็อก payer person+actor ไม่จ่ายภายนอกใน service; record เฉพาะผู้จ่ายที่ล็อกมีสิทธิ์ปัจจุบัน ยอด/ผู้รับตรง วันจ่ายและหลักฐานครบ Consume ครั้งเดียว; ไม่ตรงส่งเข้า incident ผ่าน Task 7 ไม่ทำเป็น payment สำเร็จ
- [x] Step 4 Green สอง payer/สอง account/สอง command ชน, ลืม start, result ซ้ำ, revoked payer, evidence ไม่พร้อมหลังจ่าย, commit failure, clock เดินหลายวันไม่ปลดล็อก; อ่านผลเดิมก่อน retry
- [x] Step 5 ผู้ตรวจใหม่ + G + commit `feat: lock and record external advance payments`

### Task 7: สะสางผลไม่ชัด เหตุผิดปกติ และรับช่วง

**ขอบเขตลงมือ 27 ตุลาคม 2569:** ทำบริการและเทสต์เท่านั้น ไม่เพิ่ม endpoint หรือโอนเงินจริง อนุญาตลงทะเบียนบริการใน `FA/FieldAdvanceRegistration.cs` และปรับจุดเชื่อมของบริการเดิมเท่าที่จำเป็นเพื่อให้ Unpaid กลับตรวจใหม่และรับช่วงได้ตามแบบ โดยต้องรักษาประวัติฉบับ/ผลตรวจ/ผู้จ่ายเดิม ไม่ปลอม prepare event หรือแก้ผลเดิมเพื่อข้ามการตรวจ ให้ผู้ลงมือเสนอรายละเอียด contract ที่ต้องปรับแก่ controller ก่อนลงมือส่วนร่วม

**ข้อกำหนดต่อเนื่อง:** ใช้ executor/ledger/สิทธิ์/ประวัติตัวบุคคลเดิม การ replay ตรวจสิทธิ์ปัจจุบันก่อนคืนผล ไม่มี file I/O ขณะถือธุรกรรมการเงิน และตรวจ metadata หลักฐานซ้ำก่อน commit ผู้ตรวจยืนยันด้วยสิทธิ์ตรวจการเงินจริง ไม่เพิ่มสิทธิ์เป็นผู้จ่ายเพื่อให้ตรวจไฟล์ได้ การรับช่วงไม่แก้ผู้ปฏิบัติเดิมใน attempt และไม่เปิดทาง Record ปกติของผู้จ่ายเดิมหรือคนใหม่ให้ข้ามขั้นตรวจผล

**ลำดับตรวจ:** ผู้ลงมือทำ TDD ชุดเฉพาะและตรวจตัวเอง รวม Format ให้ผ่านก่อนส่งผู้ตรวจใหม่ จากนั้นแก้ข้อพบแล้วรัน G บนโค้ดสุดท้ายครั้งเดียวก่อน commit ไม่ทำ Task 8 ต่อโดยปริยาย

**ไฟล์:** Create `FA/Payments/PaymentOutcomeService.cs`, `FA/Payments/PaymentHoldService.cs`, `FA/Payments/PaymentTakeoverService.cs`, `IT/FieldAdvancePaymentRecoveryTests.cs`

**Interfaces:** Outcome `ProposeAsync(Guid actor, Guid installmentId, PaymentOutcomeInput input, PaymentCommand meta, CancellationToken ct)` และ `ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)`; Hold `ReportMismatchAsync(Guid actor, Guid installmentId, ActualPaymentInput observed, PaymentCommand meta, CancellationToken ct)`; Takeover `ProposeAsync(Guid actor, Guid installmentId, Guid[] evidenceIds, PaymentCommand meta, CancellationToken ct)` และ `ConfirmAsync(...)` รูปเดียวกับ Outcome คืน `FinanceResult<PaymentView>` ทั้งหมด

- [x] Step 1 `ThreeOutcomesNeverAutoRetry`: Matched=>paid ครั้งเดียว, Unpaid=>PendingReview คง reserved, Unknown=>OutcomePending; `MismatchHoldsEntireFamily` ตรวจ `Assert.Contains("PaymentMismatch", view.HoldCodes);` ทั้ง root/supplement ก่อนและหลัง paid
- [x] Step 2 filter `FieldAdvancePaymentRecoveryTests` Red
- [x] Step 3 ทุกข้อเสนอมี reason+evidence ผู้ตรวจอีกคนยืนยัน hash/version; mismatch หยุดเริ่มทั้งชุดทันทีเมื่อบันทึกเหตุ แต่ยอมให้ reconcile attempt ที่เริ่มแล้ว ไม่ปรับยอด paid/reserved ให้เท่าความจริงที่ยังไม่รับรอง เก็บ observed amount/person แยก
- [x] Step 4 Takeover ผู้จ่ายใหม่มีสิทธิ์อยู่แล้วเสนอ ผู้ตรวจต่างคนยืนยัน เปลี่ยนผู้เสนอสะสางปัจจุบันหนึ่งคน ไม่ลบ operator เดิม ไม่ grant/ปลด hold/เริ่มใหม่ อดีตผู้จ่ายส่งผลแข่งต้อง conflict; Unpaid ต้องตรวจใหม่ก่อนเริ่มครั้งใหม่
- [x] Step 5 Green ทดสอบไม่มีผู้ตรวจเข้าเงื่อนไข, ไฟล์หาย/ย้ายหลังเงินจริง, hold ซ้อน, สิทธิ์หมดอายุก่อน confirm, ทั้งสามผลแข่งกัน, mismatch ที่พบผ่าน metadata ไม่มี endpoint ปลดใน 7C
- [x] Step 6 ผู้ตรวจใหม่ + G + commit `feat: reconcile payment uncertainty without duplicate payouts`

**ผลตรวจรับ:** โค้ด `44150d9` ผู้ตรวจ Kepler ผ่านรอบแก้ 1; เพิ่ม final proposer authorization ใน executor ตาม ruling เพื่อปฏิเสธสิทธิ์ที่หมดอายุระหว่างธุรกรรม RED8/GREEN8, focused47, full Backend2,906/Frontend155 ผ่าน ไม่มีการขยายไป Task 8

### Task 8: ยกเลิกงวดและแก้เฉพาะข้อมูลประกอบหลังจ่าย

**การเชื่อมเหตุผิดปกติ:** metadata ไม่มีช่องยอด/ผู้รับและไม่อ่านความหมายใบเสร็จอัตโนมัติ เมื่อพบยอดหรือผู้รับผิด ให้ใช้ `ReportMismatchAsync` เดิมบันทึกเหตุก่อน แล้วโหลดรุ่นล่าสุดจึงแก้ metadata ต่อ โดยต้องคง hold ทั้งชุดและผลจ่ายเดิม เทสต์ต้องครอบคลุมลำดับนี้และแท็บเก่าที่ใช้รุ่นก่อนเกิดเหตุ งาน API/UI ใน Task 12/13 ต้องแสดงทางรายงานเหตุนี้ ไม่ใช้ metadata กลบเหตุผิดปกติ


**ไฟล์:** Create `FA/Payments/PaymentCancellationService.cs`, `FA/Payments/PaymentMetadataService.cs`, `IT/FieldAdvancePaymentAmendmentTests.cs`

**Interfaces:** Cancellation `ProposeAsync(Guid actor, Guid installmentId, PaymentCommand meta, CancellationToken ct)`, `ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)`; Metadata `ProposeAsync(Guid actor, Guid installmentId, PaymentMetadataInput input, PaymentCommand meta, CancellationToken ct)`, `ConfirmAsync(...)` คืน `FinanceResult<PaymentView>`

- [x] Step 1 `CancelReleasesOnlyAfterConfirmation`: proposal ยัง reserved3000, confirm เป็น0; `MetadataNeverChangesMoney` => `Assert.Equal(before.Paid, after.Paid); Assert.Equal("Paid", after.State);` ทั้งหลักฐานก่อน/หลังยังอ่านได้ตามสิทธิ์
- [x] Step 2 filter `FieldAdvancePaymentAmendmentTests` Red
- [x] Step 3 CancelPending บล็อก start; Paying/Unknown ต้องสะสางยืนยัน Unpaid ก่อนยกเลิก; Paid ห้ามยกเลิก ร่างที่ยังไม่ส่งให้ยุติแบบเก็บประวัติ ไม่ลบ referenced rows; metadata จำกัดวันที่/reference/evidence ห้ามยอด/ผู้รับ/ช่องทาง/บัญชี
- [x] Step 4 Green cancel vs start/record แข่งได้ผลเดียว, ส่งคืน reserved ครั้งเดียว, same checker denied, metadata หลักฐานพบผิดตั้ง family hold ไม่ใช่ช่องทางล้างเหตุ
- [x] Step 5 ผู้ตรวจใหม่ + G + commit `feat: cancel unpaid installments and retain payment corrections`

**ผลตรวจรับ:** โค้ด `ed0648f` ผู้ลงมือ Euler ผู้ตรวจ Helmholtz ผ่าน ไม่มี Critical/Important; Minor หัว snapshot แก้แล้ว focused29/regression253/full Backend2,935 และ Frontend155 ผ่าน ตรวจ hash ตรงทั้ง4ไฟล์ ยังไม่มี endpoint/UI ใน Task นี้

### Task 9: ปรับวงเงินเดิมเฉพาะก่อนจ่ายจริง

**ไฟล์:** Create `FA/Payments/CeilingAmendmentService.cs`, `FA/Payments/PaymentDispositionService.cs`, `IT/FieldAdvanceCeilingAmendmentTests.cs`; Modify `FA/Requests/RequestRevisionService.cs`, `FA/Requests/RequestSubmissionService.cs`, `FA/Requests/RequestReviewService.cs`

**Interfaces:** `BeginAsync(Guid actor, Guid requestId, RequestContent content, PaymentCommand meta, CancellationToken ct)` และ `ActivateAsync(Guid actor, Guid requestId, PaymentCommand meta, CancellationToken ct)` คืน `FinanceResult<PaymentView>`; Disposition `ProposeAsync(Guid actor, Guid requestId, string outcome, PaymentCommand meta, CancellationToken ct)` (`ResumeOriginal`/`TerminateRemaining`), `ConfirmAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct)`

- [x] Step 1 `ActivationNeedsApprovalAndOldReservationsCleared` สลับลำดับอนุมัติ/ยกเลิกทั้งสองทาง: `Assert.Equal(10000m, beforeReady.Spendable); Assert.Equal(13000m, ready.Spendable);` ก่อน ready ห้าม start; `EverPaidBlocksOriginalAmendment` แม้อนาคตเงินคืนครบ
- [x] Step 2 filter `FieldAdvanceCeilingAmendmentTests` Red
- [x] Step 3 Begin ตั้ง hold แต่ไม่คืน reservation เอง Reuse owner confirmation/new policy snapshot/full approval; approval ใหม่ยังไม่เปลี่ยน account จนงวดเดิมยกเลิกยืนยันครบ reserved0 และไม่มี actualpaid/unknown สร้างงวดใหม่ไม่เปิดของยกเลิกกลับ
- [x] Step 4 ปฏิเสธ/ถอนฉบับแก้ไขคง account ฉบับอนุมัติเดิมและ hold; finance checker เสนอ Resume/Terminate ผู้ดำรง final-role เดิมปัจจุบันที่วงเงินครอบคลุม basis และ SoD ยืนยันต่างคน Resume ล้างเฉพาะ amendment hold ไม่ล้าง mismatch/เหตุอื่น; ใน Task นี้ทำ Terminate สำหรับกรณียังไม่เคยจ่ายให้ครบ: รอ cancel เดิมยืนยันและ reserved0 จึงทำ spendable0 โดยเก็บ approved เดิม Task 11 จึงขยายกรณีจ่ายบางส่วน ไม่เรียก implementation ที่ยังไม่มี
- [x] Step 5 Green รวม started-unknown ต้องรอ independent Unpaid, activation vs start/cancel ชน, ไม่มี eligible final holder ให้ held, policy เปลี่ยนก่อนส่งให้ยืนยันใหม่, stale revision ห้ามเริ่มจ่าย
- [x] Step 6 ผู้ตรวจใหม่ + G + commit `feat: amend advance ceilings only before payment`

**ผลตรวจรับ:** โค้ด `ac1b085` ผู้ลงมือ Sagan ผู้ตรวจ Arendt ผ่านตามแบบและคุณภาพ ไม่มีข้อพบเพิ่ม focused29/regression310/full Backend2,964 และ Frontend155 ผ่าน พร้อม Build/Format/Lint ตรวจ hash ตรงทั้ง10ไฟล์ รันชุดเต็มหลัง review ไม่มี API/UI ใหม่ใน Task นี้

### Task 10: ส่วนเพิ่มและการตรวจอำนาจจากยอดสะสมทุกจุด

**ไฟล์:** Create `FA/Requests/SupplementService.cs`, `FA/Requests/ApprovalBasisCalculator.cs`, `IT/FieldAdvanceSupplementTests.cs`; Modify `FA/Requests/RequestDraftService.cs`, `FA/Requests/RequestSubmissionService.cs`, `FA/Requests/ApprovalRouteResolver.cs`, `FA/Requests/RequestAccess.cs`, `FA/Requests/RequestReviewService.cs`, `FA/Requests/RequestRevisionService.cs`, `FA/Requests/RequestReadService.cs`, `FA/Requests/RequestRoutingWorker.cs`, `FA/Requests/RequestContracts.cs`, `FA/Requests/RequestHttpContracts.cs`

**Interfaces:** `SupplementService.CreateAsync(Guid actor, Guid sourceRequestId, RequestContent content, RequestCommand meta, CancellationToken ct): Task<FinanceResult<RequestView>>`; `ApprovalBasisCalculator.CalculateAsync(Guid requestId, decimal requested, CancellationToken ct): Task<ApprovalBasis>`; ConfirmationQuote/HTTP เพิ่ม basis snapshot และ hash รวม FamilyVersion/basis; copy service เดิมไม่สร้าง membership

- [ ] Step 1 `AggregateAuthorityIncrementOnlyPayout` root10000 paid2000, supplement3000: `Assert.Equal(13000m, quote.Basis.Amount); Assert.Equal(3000m, supplementAccount.Spendable);` ต่ออีก2000 basis15000 ไม่ใช่28000 และคนวงเงิน10000 อนุมัติ supplement3000 ไม่ได้
- [ ] Step 2 filter `FieldAdvanceSupplementTests` Red
- [ ] Step 3 server resolve family/project/owner, ต้องมี actual payment แล้วและ root ไม่ Rejected/Withdrawn/Closed; ยื่นแทนด้วยสิทธิ์เดิม owner ยืนยันเอง ตรึง basis/กติกาตอนยืนยัน ถ้าฐานเปลี่ยนก่อน submit ให้ยืนยันใหม่ ทุกฉบับลงอนุมัติใหม่
- [ ] Step 4 ใช้ basis แทน Total เฉพาะประเมินระดับ/หา candidate/CanReview/Review/worker ทุก gate ไม่เปลี่ยน payout/estimate; one pending slot transaction รวม Returned/Held, ปลดเมื่อ Approved/Rejected/Withdrawn เท่านั้น; original payout ไม่ตั้ง hold เพราะ supplement อย่างเดียว
- [ ] Step 5 Green สอง submit ชน, re-submit ใช้กติกาปัจจุบัน, delegate/owner ต่าง project denied, source copy ไม่เป็น family, approval histories เก่าไม่กลับมาใช้, role history ทั้งชุด, basis breakdown ไม่คืน source content/evidence
- [ ] Step 6 ผู้ตรวจใหม่ + G + commit `feat: add supplemental advances with cumulative authority`

### Task 11: ยุติยอดยังไม่จ่ายและรักษาฐานอนุมัติเดิม

**ไฟล์:** Modify `FA/Payments/PaymentDispositionService.cs`; Create `IT/FieldAdvancePaymentTerminationTests.cs`

**Interfaces:** ใช้ `ProposeAsync`/`ConfirmAsync` ของ Task 9; เพิ่ม `FinalizeAsync(Guid actor, Guid proposalId, PaymentCommand meta, CancellationToken ct): Task<FinanceResult<PaymentView>>` ตรวจรุ่นล่าสุดและสิทธิ์ผู้ยืนยันอีกครั้ง ไม่เพิ่ม service ปิดคำขอ 7D

- [ ] Step 1 `TerminationPreservesApprovalBasis` root10000 paid2000 ยุติส่วนเหลือ => `Assert.Equal(10000m, account.Approved); Assert.Equal(2000m, account.Spendable); Assert.Equal(0m, view.Available);` ขอเพิ่ม3000 basis13000 ไม่ใช่5000
- [ ] Step 2 filter `FieldAdvancePaymentTerminationTests` Red
- [ ] Step 3 ผู้ตรวจการเงินเสนอและ current final-role ยืนยันต่างคน ครอบคลุม stored basis ไม่ใช่แค่ยอดคงเหลือ proposal หยุด start คำขอนั้น ไม่ระงับทั้งชุดโดยไร้เหตุ; finalize รอ unknowns สะสางและ cancel unpaid ยืนยันครบ reserved0 ไม่ลดยอด paid ไม่ล้าง hold อื่น
- [ ] Step 4 Green finalize แข่ง record, เปลี่ยน holder/หมดสิทธิ์ให้ held, cancelled items ไม่กลับมา, request ยังไม่ Closed, ประวัติ approvals ไม่เปลี่ยน, supplement ใช้ฐานเดิมหลัง termination
- [ ] Step 5 ผู้ตรวจใหม่ + G + commit `feat: terminate only unpaid advance balances`

### Task 12: API อ่านและ client ตามขอบเขตจริง

**ไฟล์:** Create `FA/Payments/PaymentEndpoints.cs`, `FA/Payments/PaymentReadService.cs`, `FA/Payments/PaymentHttpContracts.cs`, `FA/Evidence/FinanceEvidenceEndpoints.cs`, `CLIENT/payment-client.ts`, `CLIENT/payment-types.ts`, `CLIENT/payment-view.ts`, `IT/FieldAdvancePaymentHttpTests.cs`, `tests/field-advance-payment.test.mjs`; Modify `FA/FieldAdvanceRegistration.cs`, `FA/FieldAdvanceOpenApiTransformer.cs`, `FA/Requests/RequestEndpoints.cs`, `CLIENT/request-client.ts`, `CLIENT/request-types.ts`

**Interfaces:** `PaymentReadService.GetAsync(Guid actor, Guid requestId, CancellationToken ct): Task<FinanceResult<PaymentView>>`, `ListAsync(Guid actor, FinanceScope scope, int skip, int take, CancellationToken ct): Task<FinanceResult<PaymentView[]>>`; queue paging take1–100 ค่าเริ่มต้น25; เพิ่ม read detail/revisions/proposals/evidence metadata DTO โดย whitelist ไม่ serialize EF entity; client `getPaymentSummary(requestId: string, signal?: AbortSignal): Promise<PaymentSummary>` และ `sendPaymentCommand(requestId: string, action: PaymentAction, payload: unknown, signal?: AbortSignal): Promise<PaymentSummary>` พร้อม runtime action/schema guard ชนิด `PaymentSummary` ใน payment-types.ts ตรง PaymentView แบบ camelCase แต่ GUID/decimal เป็น string, versions เป็น number ที่ตรวจ safe integer, installmentId/version nullable; `PaymentAction` เป็น union ของ action ใน routes ด้านล่าง ห้ามรับ arbitrary string

- [ ] Step 1 `EveryRouteRechecksCurrentRights` ทดสอบ HTTP session/MFA, CSRF ตามโครงการ, cross-project/family/receipt/evidence: `Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);` สำหรับผู้ไม่มีสิทธิ์ และ response body ไม่ประกอบข้อมูลบัญชี/หลักฐาน
- [ ] Step 2 filter `FieldAdvancePaymentHttpTests` และ `node --test tests/field-advance-payment.test.mjs` Red
- [ ] Step 3 route ใต้ `/api/field-advances`: `requests/{id}/payments` GET, `requests/{id}/installments` GET/POST, `installments/{id}` GET (รวม destination/revisions ที่มีสิทธิ์), `installments/{id}/{update|submit|review|start|record|outcome|mismatch|cancel|metadata|takeover}` POST; proposal detail `payment-proposals/{id}` GET และ confirm `payment-proposals/{id}/confirm` POST; `requests/{id}/{ceiling-amendment|ceiling-activate|disposition|supplements}` POST; disposition finalize `payment-proposals/{id}/finalize` POST; `requests/{id}/payment-evidence` POST และ `payment-evidence/{id}` GET ใช้ dispatcher แบบ enum คงที่ไม่ reflection
- [ ] Step 4 read queue `/payments` GET ตาม scope และ action จริง, command lookup `/payment-commands/{commandId}` GET ตรวจ actor+current access; DTO decimal string, errors code stable+ข้อความไทย, evidence Cache-Control private/no-store, attachment/nosniff, no public paths; source link ตรวจปลายทางแยก basis breakdown เลขคำขอ/ยอดเท่านั้น
- [ ] Step 5 Green strict unknown fields, replayหลัง revoke, staleversion409, emptyreason400, file streaming limits, permissionเปลี่ยนตอนอ่าน/download, client abort/oldresponse ไม่ทับ actorใหม่; OpenAPI ทุก command มี contracts/error responses
- [ ] Step 6 ผู้ตรวจใหม่ + G + commit `feat: expose scoped advance payment APIs`

### Task 13: หน้าจอฝ่ายการเงินและคำเตือนห้ามจ่ายซ้ำ

**ไฟล์:** Create `UI/payments/page.tsx`, `UI/PaymentQueue.tsx`, `UI/PaymentDetails.tsx`, `UI/InstallmentEditor.tsx`, `UI/PaymentRecoveryPanel.tsx`, `tests/e2e/field-advance-payment.spec.ts`; Modify `UI/RequestDetails.tsx`, `IT/FieldAdvancePaymentHttpTests.cs`, `backend/tests/TPR10.E2E.Fixture/Program.cs`, `infra/nginx/https-smoke-fixture.mjs` เฉพาะเพิ่ม allowlist spec เมื่อแยกงานค้างเดิมเรียบร้อย

**Interfaces:** React components รับ requestId/string, summary/PaymentSummary และ callback reload ใช้ client Task 12; ไม่มีคำนวณยอด authoritative ด้วย JavaScript float ไม่ส่งรหัสบุคคลแทน actor

- [ ] Step 1 E2E `payment review start record` ตรวจแสดง Approved/Reserved/Paid/Available แยกกัน, ผู้ตรวจไม่เห็นปุ่ม payer, review ไม่แสดงจ่ายแล้ว, exact copy `await expect(page.getByText('กำลังตรวจสอบผล ห้ามจ่ายซ้ำ')).toBeVisible();` เมื่อ response ขาด
- [ ] Step 2 รัน HTTPS E2E ตามคำสั่งด้านล่างต้อง Red จาก UI ที่ยังไม่มี ไม่ใช่จาก cert/504
- [ ] Step 3 ทำ queue/detail/editor/review/preflight แสดงคนรับ ยอด ช่องทาง บัญชีฉบับตรวจผ่าน; หลัง start มี only record/status ไม่ startซ้ำ Networkerror อ่าน command เดิม/สถานะ ไม่สร้าง command ใหม่เพื่อโอนซ้ำ
- [ ] Step 4 RecoveryPanel รองรับ outcome/takeover/cancel/metadata พร้อมเหตุผลหลักฐานและผู้ตรวจ; revoked identity ล้างข้อมูล, abort oldrequests, conflict reload ไม่ทับฉบับใหม่; component ต้องเข้าถึงด้วย keyboard/error label ภาษาไทย
- [ ] Step 5 Green E2E สอง session แยกคน, เงินสด/โอน, ปุ่ม doubleclick, หลักฐานหาย, mismatch familyhold, layout viewport mobile จำลอง (ไม่ใช่มือถือจริง) และ dev/prod ทั้งสองพอร์ต
- [ ] Step 6 ผู้ตรวจใหม่ + G + commit `feat: add advance payment operations screens`

### Task 14: หน้าจอปรับวงเงิน ส่วนเพิ่ม และยุติยอด

**ไฟล์:** Create `UI/SupplementEditor.tsx`, `UI/CeilingAmendmentPanel.tsx`, `UI/PaymentDispositionPanel.tsx`, `tests/e2e/field-advance-supplement.spec.ts`; Modify `UI/RequestEditor.tsx`, `UI/RequestDetails.tsx`, `UI/RequestRevisionDetails.tsx`, `UI/approvals/page.tsx`, `infra/nginx/https-smoke-fixture.mjs` เฉพาะ allowlist

**Interfaces:** ใช้ request-client 7B + payment-client Task12; supplement editor lock project/owner จาก server, ประมาณการเฉพาะส่วนเพิ่ม; confirmation แสดง RequestedTotal กับ ApprovalBasis และ breakdown ที่ API อนุญาต

- [ ] Step 1 E2E `supplement shows 3000 payout against 13000 authority`: `await expect(page.getByText('ยอดส่วนเพิ่ม 3,000.00 บาท')).toBeVisible();` และ `await expect(page.getByText('ฐานอำนาจอนุมัติ 13,000.00 บาท')).toBeVisible();` ผู้อนุมัติไม่มีสิทธิ์ source เปิด source detail ไม่ได้
- [ ] Step 2 รัน spec HTTPS Red
- [ ] Step 3 ก่อนจ่ายแสดงปรับวงเงิน+รอสะสางงวด/อนุมัติสองเงื่อนไข หลังจ่ายเสนอส่วนเพิ่มเท่านั้น; unknown บอกพัก, Returned/Held supplement บอกมีรายการค้าง, ownerยืนยันใหม่เมื่อ quoteเปลี่ยน; แสดงผลส่งกลับ/ปฏิเสธ/ถอนเป็นของฉบับแก้ไข ไม่ลบวงเงินอนุมัติเดิม
- [ ] Step 4 Disposition เสนอ/ยืนยัน/สะสาง/finalize แสดง “ยุติเฉพาะยอดที่ยังไม่จ่าย ยังต้องเคลียร์เงินที่จ่ายแล้ว” ไม่ใช้คำว่าปิดครบ; ResumeOriginal ไม่ล้าง mismatch; ไม่มีหน้าจอ grant เพื่อแก้การหาผู้ยืนยันไม่ได้อัตโนมัติ
- [ ] Step 5 Green E2E cumulativeหลายทอด, terminateแล้วbasisเดิม, pendingเดิมจ่ายได้แต่ส่วนเพิ่มยังจ่ายไม่ได้, roleเก่า/บัญชีสอง, stale quote และ source dataไม่รั่ว หลังเปลี่ยนassignmentข้อมูลตามassignmentหาย
- [ ] Step 6 ผู้ตรวจใหม่ + G + commit `feat: add ceiling and supplemental request screens`

### Task 15: ตรวจระบบรวมและคู่มือส่งมอบ

**ไฟล์:** Create `IT/FieldAdvancePaymentRaceTests.cs`, `IT/FieldAdvancePaymentDeliveryTests.cs`, `docs/reports/2026-10-24-module-7c-verification.md`, `docs/runbooks/field-advance-payments.md`; Modify แผนนี้เฉพาะผลจริง/checkbox และไฟล์ทดสอบที่พบช่องว่างจาก C01–C25 ไม่ใช้ Task นี้ซ่อน feature ที่ยังไม่ทำ

**Interfaces:** ใช้ services/HTTP/fixture จาก Taskก่อนหน้า ไม่มี public interface ใหม่; runbook บอกจัดเก็บ/สำรอง/migrate/กู้คืน/การจ่ายภายนอกและการอ่าน command outcome

- [ ] Step 1 เติม Red regression สำหรับความเสี่ยงที่ยังไม่มี เช่น start vs amendment/termination/mismatch, checker/payer grantrevocation vs command, partialcommit vs replay, filelocation switch vs download ด้วย PostgreSQL จริงสอง connection และ barrier ไม่ใช้ timing sleep เดา
- [ ] Step 2 filter `FieldAdvancePaymentRaceTests|FieldAdvancePaymentDeliveryTests` โดย syntax `--filter 'FullyQualifiedName~FieldAdvancePaymentRaceTests|FullyQualifiedName~FieldAdvancePaymentDeliveryTests'` ตรวจ FAIL ตรง assertion ก่อนแก้
- [ ] Step 3 แก้เฉพาะข้อบกพร่องที่พิสูจน์ได้; ทดสอบ migration clone schema7B, data copyสัมพันธ์เดิม, ไม่มี seedเงินจริง/grantเอง; idempotent apply, สำรองและ forward-recovery ก่อนใช้งาน ไม่ใช้ down migration ที่ลบหลักฐานบนฐานจริง
- [ ] Step 4 รัน G และ HTTPS specs identity/scopes/7A policy/grant/7B request/7C payment/supplement ทั้ง4000/4001 ด้วย TLS ที่ตรวจจริง รวม file move local; ถ้า 504 เกิดเก็บหลักฐานและระบุ blocked ไม่เปลี่ยนเป็น retryจนดูผ่าน ไม่อ้างว่า 504 ถูกแก้
- [ ] Step 5 ผู้ตรวจใหม่ตรวจทั้ง branch เทียบ C01–C25 และรายการเดิม ปิด Critical/Important ก่อนส่งมอบ รายงานแยก passed/failed/blocked/not-run พร้อม command/exit/time/commit ไม่รวมมือถือ/NASในpassed
- [ ] Step 6 เขียนคู่มือ “ตรวจผ่านยังไม่จ่าย”, เริ่มก่อนเงินจริง, responseหายห้ามโอนซ้ำ, ใช้ผู้ตรวจสะสาง, mismatchคงพักรอ7D, backup DBพร้อมstorage, secret/file access และผู้มีสิทธิ์จัดการroot เก็บ M2-R1/R2-C1/HTTPS504/มือถือ/NASในรายการคงค้าง
- [ ] Step 7 รัน G หลังแก้สุดท้าย + commit `test: verify module 7c payment safety and delivery` หยุดรอคำสั่งผู้ใช้ก่อน push/PR/merge

## คำสั่ง HTTPS E2E เมื่อถึง Task หน้าจอ

Fixture ปัจจุบัน export `runHttpsSmoke` และมี allowlist จึงต้องเพิ่มสอง spec ใหม่ก่อนรัน งานไฟล์ fixture มีการแก้ HTTPS504 ค้างอยู่ ให้ตรวจ diff และใช้เฉพาะการเพิ่ม allowlist ที่จำเป็น ไม่เขียนทับ/รวบ capture work หรือเปิด capture โดยไม่ได้สั่ง

```bash
node --input-type=module -e "import { runHttpsSmoke } from './infra/nginx/https-smoke-fixture.mjs'; await runHttpsSmoke({port:'4000',spec:'tests/e2e/field-advance-payment.spec.ts',e2e:true});"
node --input-type=module -e "import { runHttpsSmoke } from './infra/nginx/https-smoke-fixture.mjs'; await runHttpsSmoke({port:'4001',spec:'tests/e2e/field-advance-payment.spec.ts',e2e:true});"
node --input-type=module -e "import { runHttpsSmoke } from './infra/nginx/https-smoke-fixture.mjs'; await runHttpsSmoke({port:'4000',spec:'tests/e2e/field-advance-supplement.spec.ts',e2e:true});"
node --input-type=module -e "import { runHttpsSmoke } from './infra/nginx/https-smoke-fixture.mjs'; await runHttpsSmoke({port:'4001',spec:'tests/e2e/field-advance-supplement.spec.ts',e2e:true});"
```

ต้อง exit0 และ Playwright ทุกกรณีผ่าน ไม่เปิด ignoreHTTPSErrors ไม่ลด secure cookie ใช้บัญชี/เงิน/เอกสารทดสอบใน fixture เท่านั้น หาก trust/port/Docker ไม่พร้อมแก้เฉพาะสภาพแวดล้อมที่ได้รับอนุญาตและรายงาน gate ไม่ผ่าน ไม่แตะเงินจริง

## ตารางครอบคลุมแบบที่อนุมัติ

| เกณฑ์ | Task เจ้าของและจุดตรวจ |
|---|---|
| C01, C02 | 3–4 ledger/reserve/return/concurrency |
| C03, C04 | 4–6 revision/destination/evidence; 13 UI |
| C05, C06, C07 | 3, 6 start/replay/consume/rollback; 13 responseหาย |
| C08, C21, C23 | 7 outcome/familyhold/takeover; 13 recovery |
| C09, C10 | 8 cancellation/metadata; 13 UI |
| C11, C22 | 9 amendment gates; 14 UI |
| C12, C13, C14, C15 | 10 supplement/basis/slot/original payout; 14 UI |
| C16 | 2 family history; 7, 10 และ 15 races |
| C17, C25 | 2, 12 scoped read/download/basis; 14 source link |
| C18 | 5 private evidence/move/validation; 12 HTTP |
| C19 | 3 atomicity; 15 PostgreSQL races |
| C20 | 1 migration; 10 copy/reference; 15 regression7A/7B |
| C24 | 11 termination; 10 basis regression; 14 UI |

## เงื่อนไขส่งต่อและรายการติดตาม

แผนนี้ยังไม่ใช่หลักฐานพัฒนาเสร็จ อนุมัติแผนแล้วจึงเริ่ม Task 1 ด้วยผู้ลงมือและผู้ตรวจแยกกันตามเดิม ทุก Task ต้องผ่าน G ก่อนแจ้งเสร็จ การผ่าน 7C ไม่แปลว่าเคลียร์เงิน/คืนเงิน/ปิดคำขอของ 7D พร้อมใช้

- M2-R1: คง Minor เรื่องความครอบคลุม regression สองบัญชีบุคคลเดียวที่ grant ต่างกัน ไม่ประกาศปิดเพราะมีเทสต์ใหม่ที่คล้ายกัน
- R2-C1: คง Minor เรื่อง fixture environment normalization callsite ไม่รวมการแก้ในแผนนี้
- HTTPS 504: คงรอหลักฐานจริง ไม่ถือว่าหายจาก build/E2E ที่ผ่านบางรอบ
- มือถือจริง/NAS จริง: พักตามคำสั่ง การทดสอบ viewport/local folders/adapter ไม่ทดแทนอุปกรณ์จริง
- หลังอนุมัติแผน: เริ่ม Task 1 เท่านั้นตามคำสั่งครั้งถัดไป ไม่ต้องเลือกรูปแบบผู้ลงมือ/ผู้ตรวจใหม่

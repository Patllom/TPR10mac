# Module 7A — แผนพัฒนากติกาและสิทธิ์เงินทดรองออกสนาม (Implementation Plan)

> **สำหรับผู้ดำเนินงาน:** ใช้ `superpowers:subagent-driven-development` (แนะนำ) หรือ `superpowers:executing-plans` เพื่อดำเนินงานทีละ Task รายการ `- [ ]` ใช้ติดตามผลจริง ห้ามข้ามขั้นทดสอบหรือเริ่มก่อนผู้ใช้อนุมัติแผนและเลือกวิธีดำเนินงาน

**เป้าหมาย:** ส่งมอบหน้าจอและบริการกติกาแบบมีฉบับ พร้อมการมอบหมายสิทธิ์สามคนและการตรวจสิทธิ์ที่ 7B–7D ใช้ร่วมกันได้

**สถาปัตยกรรม:** เพิ่มพื้นที่ `FieldAdvances` ใน API เดิม แยกข้อมูล กฎคำนวณ การตรวจสิทธิ์ การมอบหมาย และการอนุมัติกติกา ใช้บัญชี/MFA โครงการ session และ audit เดิม แต่ไม่ใช้สิทธิ์ admin หรือ workflow ลงเวลาแทนสิทธิ์การเงิน ฝั่งเว็บใช้ Next.js และตัวเรียก API/session เดิม โดยทุกกฎสำคัญตรวจที่เซิร์ฟเวอร์

**เทคโนโลยี:** .NET 10, EF Core 10, PostgreSQL, xUnit/Testcontainers; Next.js 15, React 19, TypeScript, Node test runner, Playwright และ ESLint ตามเวอร์ชันที่ล็อกใน repository ไม่เพิ่มไลบรารีโดยไม่มีเหตุผล

**เอกสารออกแบบ:** `docs/superpowers/specs/2026-10-19-module-7a-policy-permissions-design.md` — ผู้ใช้อนุมัติแล้ว 19 ตุลาคม 2569

**สถานะแผน:** Task 1–8 เสร็จและผ่าน Test, Build, Lint พร้อมผู้ตรวจอิสระแล้ว ณ 21 ตุลาคม 2569; Task 9–10 ยังไม่เริ่ม ยังไม่ถือว่า Module 7A ส่งมอบครบ ไม่มี push/PR/merge และยังพักทดสอบมือถือจริง/NAS จริง

## ข้อจำกัดร่วมทุก Task

- ผู้แก้กติกากับผู้อนุมัติต่างคน รวมผู้ร่วมแก้ฉบับนั้นทั้งหมด
- ผู้เสนอ ผู้อนุมัติ และผู้รับสิทธิ์ต้องเป็นสามคนต่างกัน
- เพิ่มสิทธิ์ไม่ล้างข้อห้ามจากประวัติคำขอ; admin และสมาชิกทีมไม่มีสิทธิ์การเงินอัตโนมัติ
- โครงการเฉพาะตรึงฉบับบริษัทฐานจนอนุมัติรับฉบับใหม่; คำขอส่งแล้วไม่เปลี่ยนย้อนหลัง
- จำนวนเงินเป็นบาทและสตางค์ ใช้ decimal; จำนวนวันเคลียร์เป็นวันปฏิทินและเริ่มวันถัดจากวันจบงาน
- ไม่ตั้งยอดวงเงิน อัตราจริง หรือจำนวนวันจริงให้บริษัทเอง ตัวเลขในเทสต์เป็นข้อมูลจำลองเท่านั้น
- ไม่ทำคำขอ จ่าย เคลียร์ คืนเงินจริง หรือระบบเบิกคืนใน7A ไม่ขยาย Module4/5
- พักมือถือจริงและ NAS จริง ไม่แสดงว่าทดสอบผ่าน; เทสต์เบราว์เซอร์จำลองไม่แทนมือถือจริง
- ข้อความผู้ใช้และเอกสารภาษาไทย; Dev 4000, Production 4001, HTTPS preview 4443 ไม่เปลี่ยนพอร์ต
- ไม่เก็บ secrets ลง Git/log/report และไม่ใช้ฐานข้อมูลผู้ใช้จริงในเทสต์
- ทุก Task ใช้ TDD และตรวจ diff ก่อน commit; ก่อนแจ้งพัฒนาเสร็จต้องผ่าน Test, Build, Lint และ Code Review ทั้งชุด

## จุดเน้นตรวจทานความเสี่ยง (Review Focus)

1. ถอนสิทธิ์พร้อมอนุมัติ: ต้องไม่มีการอนุมัติด้วยสิทธิ์ที่ถูกถอนก่อนธุรกรรมเริ่มมีผล — Task3/4/10
2. ให้สิทธิ์ทางอ้อมผ่าน role/scope/account API: ต้องไม่กลายเป็นสิทธิ์การเงินโดยข้ามสามคน — Task3
3. คนเดียวใช้หลายบัญชี/หลายบทบาท: การแยกหน้าที่ต้องยึดตัวตนบุคคลที่เชื่อถือได้ ไม่เชื่อรหัสที่ client ส่ง — Task1/3/5
4. การรับบริษัทฉบับใหม่และกลับไปใช้บริษัททั้งหมด: ต้องผ่านอนุมัติ ไม่มีค่าปนข้ามฉบับ — Task6/7
5. กดซ้ำหลังเครือข่ายขาด หรือยอดอยู่ตรงขอบ: ไม่สร้างผลซ้ำ ไม่เลือกระดับผิด — Task2/4/6/10

## ฐานโค้ดและการเตรียมก่อนลงมือ

ตรวจอ้างอิง `origin/main` ที่ `d657c16408ae52cb1ba43d991a438ea5c0daf895` ไม่ใช้ main ใน checkout นี้ซึ่งยังอยู่ `b8b9c0f...` เป็นฐานพัฒนาโดยไม่ตรวจ เอกสารเดิมมีการแก้ค้างอยู่ ห้าม reset/clean/pull ทับ

- [ ] เมื่อแผนอนุมัติ ใช้ skill `using-git-worktrees` ตรวจ worktree ที่มีอยู่ก่อน เลือกหรือสร้าง checkout แยกบน main ที่ตรวจแล้ว ใช้ branch `codex/module-7a-policy-permissions` หากยังไม่มี
- [ ] นำเฉพาะ spec และแผนฉบับนี้เข้า checkout เป้าหมายโดยไม่แก้ dirty docs เดิม ตรวจ diff ก่อน commit เอกสาร
- [ ] ตรวจ SDK, Node, Docker/Testcontainers, PostgreSQL fixture และ HTTPS ที่เชื่อถือได้ ห้ามปิดตรวจ certificate เพื่อให้เทสต์ผ่าน
- [ ] รัน baseline ตามชุดคำสั่งท้ายเอกสารก่อนเปลี่ยนโค้ด บันทึก failure เดิมแยกจากผลของ7A หากฐานเปลี่ยนให้ตรวจ path/signature ใหม่ก่อนทำงาน

คำสั่งในแผนรันจาก root ของ checkout ที่ใช้พัฒนา ไม่ใช่สั่งให้รันในรอบจัดทำแผนนี้

## แผนที่ไฟล์และสัญญาร่วม

ใช้ชื่อย่อต่อไปนี้เฉพาะเพื่อให้อ่านแผนง่าย ต้องขยายเป็น path จริงเมื่อแก้ไฟล์:

- `FA` = `backend/src/TPR10.Api/FieldAdvances`
- `IT` = `backend/tests/TPR10.Api.IntegrationTests`
- `WEB` = `app/portal/field-advances/settings`

| พื้นที่ | ไฟล์ใหม่และหน้าที่ |
|---|---|
| ข้อมูล | `FA/Data/PolicyEntities.cs`, `GrantEntities.cs`, `FieldAdvanceModelConfiguration.cs` — ฉบับ เนื้อหา ผลตรวจ สิทธิ์ และประวัติคำสั่ง |
| สัญญา | `FA/FieldAdvanceContracts.cs` — ชนิดข้อมูลร่วม; `FieldAdvanceRegistration.cs` — DI |
| กฎ | `FA/Policies/PolicyValidation.cs`, `ApprovalLevelEvaluator.cs`, `PolicyResolver.cs`, `PolicyService.cs` |
| สิทธิ์ | `FA/Access/FinanceCapabilities.cs`, `FinanceAccess.cs`, `SeparationRules.cs`, `FinancePersonResolver.cs` |
| มอบหมาย | `FA/Grants/GrantService.cs`, `GrantLifecycle.cs`, `FinanceBootstrapCommand.cs` |
| HTTP | `FA/Policies/PolicyEndpoints.cs`, `FA/Grants/GrantEndpoints.cs`, `FA/FieldAdvanceOpenApiTransformer.cs` |
| เว็บ | `lib/field-advances/policy-types.ts`, `policy-client.ts`, `policy-view.ts`, `grant-client.ts` |
| เทสต์ | `IT/FieldAdvanceFixture.cs` และชุด `FieldAdvance*Tests.cs`; `tests/field-advance-*.test.mjs`; `tests/e2e/field-advance-*.spec.ts` |

ไฟล์เดิมที่จะเชื่อม: `Data/Tpr10DbContext.cs`, `Program.cs`, `Organization/EffectiveRolePolicy.cs`, `Organization/OrganizationService.cs`, `Scopes/Assignments/AssignmentService.cs`, `Identity/Accounts/RoleAdministration.cs`, `Identity/Accounts/AccountProvisioning.cs` ใต้ API; เว็บเชื่อม `app/portal/page.tsx` และใช้ `lib/auth/session-fetch.ts` เดิม

### สัญญาที่ต้องสร้างใน Task1

```csharp
record FinanceScope(Guid WorkspaceId, Guid? ProjectId);
record FinanceDecision(bool Allowed, string Code);
record ApprovalBand(int Order, string LevelCode, decimal UpperInclusive);
record CategoryRule(Guid Id, string Name, bool Active, string? Unit,
    decimal? StandardRate, string[] EvidenceTypes);
record PolicyContent(int ClearingDays, ApprovalBand[] Bands, CategoryRule[] Categories);
record PolicySnapshot(Guid CompanyVersionId, Guid? ProjectVersionId,
    PolicyContent Content, string ContentHash);
record CommandMeta(Guid CommandId, long ExpectedVersion, string Reason);
record FinanceResult<T>(int Status, string Code, T? Value);
record PolicyView(Guid Id, FinanceScope Scope, string State, long Version,
    Guid? BaseCompanyVersionId, PolicyContent Content, string ContentHash);
record GrantSpec(Guid RecipientId, FinanceScope Scope, string[] Capabilities,
    string[] DelegableCapabilities, decimal? ApprovalLimit,
    DateTimeOffset StartsAt, DateTimeOffset? EndsAt);
record GrantView(Guid Id, string State, long Version, GrantSpec Spec);
record RequestParticipation(Guid OwnerPersonId, Guid? DelegatePersonId,
    Guid[] ApproverPersonIds, Guid[] CheckerPersonIds, Guid[] PayerPersonIds);
```

`RecipientId` เป็นบัญชีที่ resolve เป็นบุคคลฝั่ง server; `Guid` ใน RequestParticipation เป็นตัวตนบุคคล ไม่ใช่บทบาท ชนิดส่วนนี้ใช้เป็นบริบททดสอบก่อนมีคำขอจริง ห้ามเปิด API ให้ client อ้างประวัติมาอนุญาตตัวเอง

ผลผิดพลาดร่วม: 400 ข้อมูลไม่ถูกต้อง, 403 ไม่มีสิทธิ์, 404 ไม่พบในขอบเขต, 409 ฉบับหรือสถานะขัดแย้ง, 503 ระบบที่จำเป็นไม่พร้อม ข้อความไทยไม่เผยข้อมูลต่างโครงการ

## วงจรตรวจและ commit ประจำ Task

ทุก Task ด้านล่างต้องผ่านขั้นแดง → เขียว → ทบทวน โดย test แดงต้องเป็นเหตุจากพฤติกรรมที่ยังไม่มี ไม่ใช่ Docker/ฐานข้อมูลเสีย หลังแก้ให้รันชุดของ Task และเทสต์ส่วนที่กระทบ รัน Build/Lint ก่อน checkpoint commit ใช้ `git add` เฉพาะไฟล์ Task และตรวจ `git diff --cached --check` ไม่ใช้ `git add .` ทับงานผู้อื่น Commit เป็นจุดย่อย ไม่ใช่การรับรอง Module7A เสร็จ

### Task 1: ข้อมูลฉบับกติกาและสิทธิ์ที่รักษาประวัติ

**ไฟล์:** สร้างไฟล์กลุ่มข้อมูล/สัญญาตามแผนที่; เพิ่ม `IT/FieldAdvanceSchemaTests.cs`, `FieldAdvanceFixture.cs`; แก้ `backend/src/TPR10.Api/Data/Tpr10DbContext.cs`; migration `AddFieldAdvancePolicyFoundation` ใต้ `backend/src/TPR10.Api/Data/Migrations` พร้อม snapshot ที่ EF สร้าง (timestamp ให้เครื่องมือสร้างจริง)

**สัญญา:** สร้าง records ด้านบน; `FieldAdvanceModelConfiguration.Configure(ModelBuilder model): void` และ `FieldAdvanceFixture.CreateAsync(PostgresFixture postgres): Task<FieldAdvanceFixture>` สำหรับฐานข้อมูลแยกตามแนว IdentityDatabase เดิม

- [x] เขียน `FieldAdvanceSchemaTests` ให้พิสูจน์ FK ขอบเขต, ห้ามลบฉบับถูกอ้าง, unique ฉบับ active ต่อ scope, contributor history, command id ซ้ำ และการคืนฐานจาก migration
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceSchemaTests` ต้องแดงเพราะ schema ยังไม่มี
- [x] สร้าง entities: policy revision/contributor/review, active pointer, grant proposal/review/assignment, command receipt และ bootstrap receipt; ใช้ FK restrict, version bigint, เวลา UTC, เงิน numeric(18,2) ไม่ cascade ลบประวัติ
- [x] ตรวจ directory เดิมและวาง schema ตัวตน: EmployeeMembership ไม่มีบุคคลอิสระ จึงเพิ่ม FinancePerson และการผูกบัญชีที่ระบุชัด/เก็บประวัติ ไม่อนุมานจาก role หรือ client; บริการ resolve สมาชิกที่ยังใช้งานและปฏิเสธข้อมูลขาด/กำกวมอยู่ Task3 และ governance อยู่ Task5 ตามคำตัดสินขอบเขต
- [x] สร้าง migration ด้วย `dotnet ef migrations add AddFieldAdvancePolicyFoundation --project backend/src/TPR10.Api --output-dir Data/Migrations`; รันเทสต์ซ้ำเขียว และพิสูจน์ไม่มี active policy/financial grant ถูก seed ให้ admin
- [x] ตรวจและ commit: `feat: add field advance policy data foundation`

**ผลส่งมอบ Task 1 — commit `dcd222d`:**

- โครงสร้างฉบับกติกา สิทธิ์ ประวัติ ตัวตน สัญญาข้อมูล และ migration `20261019142031_AddFieldAdvancePolicyFoundation`; ยังไม่มีหน้าจอ/API ใช้งานการเงิน
- TDD: schema ขาดก่อนพัฒนา → เทสต์เขียว; เพิ่มเทสต์ rollback และการเขียนประวัติพร้อมกัน ผลเฉพาะ 43/43; รวม regression ที่เกี่ยวข้อง 58/58
- ผู้ตรวจอิสระพบกรณี Repeatable Read ที่ล็อกอย่างเดียวกันข้อมูลซ้อนไม่ได้: พิสูจน์แดง แก้เป็น write conflict โดยไม่เปลี่ยน security_version และตรวจซ้ำผ่านทั้งสามระดับ transaction
- Full run แรกหลังแก้พบเทสต์เดิมสองกรณีเลือก migration ตามลำดับซึ่งเปลี่ยนเมื่อเพิ่ม migration ใหม่: แก้เฉพาะชื่อเป้าหมายใน CorrectionRaceTests ไม่ลด assertions; ผู้ตรวจอีกคนตรวจส่วนแก้ผ่าน
- Full run สุดท้าย backend **1,726/1,726**, ไม่มี fail/skip, exit0, **43.1539 นาที**; Node **74/74**; frontend/backend Build, Lint, Format และ diff check ผ่าน
- ตรวจ hash ไฟล์ส่งมอบทั้ง 12 ไฟล์ตรงกับชุดที่ผ่านรีวิว ไม่แก้ฐานข้อมูลจริง ไม่ทดสอบมือถือจริง/NAS จริง และไม่ใช้ผล browser จำลองอ้างแทน
- ผู้ใช้อนุมัติเริ่ม Task 2 แล้ว ผลและสถานะอยู่ในหัวข้อถัดไป

### Task 2: กฎหมวด อัตรา และตัวประเมินระดับวงเงิน

**ไฟล์:** `FA/Policies/PolicyValidation.cs`, `ApprovalLevelEvaluator.cs`; `IT/FieldAdvanceRulesTests.cs`

**สัญญา:** `PolicyValidation.Validate(PolicyContent content): string[]`; `ApprovalLevelEvaluator.Evaluate(decimal total, PolicySnapshot snapshot): FinanceResult<string[]>` ใช้ records Task1

- [x] เขียนเทสต์ข้อมูลจำลอง bands `1000.00 / 5000.00`: ยอด `1000.00` ได้หัวหน้า+PM+ระดับแรก, `1000.01` ได้ทุกระดับถึงระดับสอง, `5000.01` คืนไม่รองรับ; ยอด0/ติดลบ/เกินสองตำแหน่งถูกปฏิเสธ
- [x] เขียนเทสต์อัตรามาตรฐานขาดหน่วย/ติดลบ, หมวดไม่มีหลักฐาน, IDหมวดซ้ำ, จำนวนวันไม่เป็นบวก และ bandไม่เรียงเพิ่มต้องไม่ผ่าน; ใช้หมวดเริ่มต้นห้าชื่อตาม spec โดยไม่มีอัตราธุรกิจสมมติเปิดใช้
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceRulesTests` ให้แดง
- [x] ทำ validator และ evaluator: ช่วงเป็น `(ขอบก่อนหน้า, UpperInclusive]`; order ต่อเนื่อง; คืนหัวหน้าและPMเสมอแล้วสะสมระดับบริหาร ห้ามใช้ float หรือฝัง threshold จริง
- [x] รันเขียว พร้อม assert ว่า evaluator ไม่สร้างคำขอ/แก้ฐานข้อมูล ตัวอย่าง `Assert.Equal(new[]{"supervisor","project-manager","L1","L2"}, result.Value);`
- [x] ตรวจและ commit: `feat: validate field advance policy rules`

**ผลส่งมอบ Task 2 — commit `19027af`, 20 ตุลาคม 2026 (เวลาไทย):**

- เพิ่มตัวตรวจหมวด/อัตรา/หลักฐาน/วันเคลียร์ และตัวประเมินลำดับอนุมัติ ใช้ records เดิม ไม่มี DB/API/UI หรือการตั้งค่าธุรกิจเปิดใช้
- TDD: behavioral RED 109 กรณี และ RED ข้อจำกัด schema เพิ่ม 14 กรณี ก่อนแก้ให้ focused GREEN 132/132; regression schema เดิม 43/43
- ผู้ลงมือ Feynman และผู้ตรวจอิสระ Galileo แยกกัน; ตรวจสเปกและคุณภาพผ่าน ไม่มีข้อพบค้าง ตรึง hash ของสามไฟล์ก่อนรันชุดรวม
- ชุด backend ทั้งหมด **1,858/1,858 ผ่าน**, 0 fail/skip, exit 0, ใช้เวลา 42.9047 นาที; Node **74/74 ผ่าน**, 0 fail/skip
- `dotnet build backend/TPR10.sln --no-restore` ผ่าน 0 warnings/errors; `dotnet format backend/TPR10.sln --verify-no-changes --no-restore`, `npm run build` และ `npm run lint` ผ่าน exit 0
- คำสั่งชุดรวม: `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'`; ทดสอบด้วยฐานทิ้งได้ ไม่แก้ข้อมูลจริง ตรวจ hash สามไฟล์ตรงกับชุดรีวิวหลังเทสต์จบ
- ข้อจำกัด: hash ตรวจรูปแบบตาม schema ไม่ใช่การยืนยันเนื้อหา; การเลือก snapshot และตรวจสิทธิ์เป็นงานถัดไป ไม่ทำคำขอเงินจริงหรือทดสอบ UI 7A ที่ยังไม่มีใน Task นี้
- พักมือถือจริง/NAS จริงตามเดิม ไม่ใช้ผลจำลองแทน; ไม่เริ่ม Task 3 และไม่ push/PR/merge
- ไม่รัน Playwright ใน Task 2 ซึ่งเพิ่มเฉพาะ pure rules ไม่มี route/UI; การตรวจเบราว์เซอร์และเกณฑ์ส่งมอบทั้งโมดูลยังอยู่ Task 8–10 ไม่อ้างว่า Module 7A เสร็จ

### Task 3: ประเมินสิทธิ์ปัจจุบันและปิดช่องทางข้ามข้อห้าม

**ไฟล์:** กลุ่ม `FA/Access` ทั้งสี่ไฟล์; `IT/FieldAdvanceAccessTests.cs`, `FieldAdvanceBypassTests.cs`; แก้ `Organization/EffectiveRolePolicy.cs`, `Scopes/Assignments/AssignmentService.cs`, `Identity/Accounts/RoleAdministration.cs`, `Identity/Accounts/AccountProvisioning.cs` ตามจุดให้สิทธิ์ที่ตรวจพบ

**สัญญา:** `FinancePersonResolver.ResolveAsync(Guid userId, Guid workspaceId, CancellationToken ct): Task<Guid?>`; `FinanceAccess.CheckAsync(Guid actorId, FinanceScope scope, string capability, CancellationToken ct): Task<FinanceDecision>`; `SeparationRules.Check(Guid personId, string action, RequestParticipation participation): FinanceDecision`

- [x] เขียน tests admin/PM/teamไม่มี grant → denied; grantคนละproject → denied; ผู้ถูกระงับหรือ MFAไม่ครบ → denied; `SeparationRules.Check(owner,"payment.record",history).Allowed` ต้อง false
- [x] เพิ่มชุด pairwise owner/delegate/approver/checker/payer และกรณีเปลี่ยน role/ถอนแล้วให้ใหม่ไม่ล้าง history; บัญชีที่resolveเป็นpersonเดียวกันยังถูกห้าม
- [x] รัน filter `FullyQualifiedName~FieldAdvanceAccessTests|FullyQualifiedName~FieldAdvanceBypassTests` ให้แดง
- [x] ทำ capability catalog แยก `policy.edit.company`, `policy.edit.project`, `policy.review`, `grant.propose`, `grant.review`, `grant.revoke`, `request.approve`, `finance.check`, `payment.record`, `report.read`; ใช้ grant7A ที่อนุมัติแล้วเท่านั้นเป็นแหล่งอนุญาต ไม่ให้ generic role เปลี่ยนมาเป็น grant โดยอัตโนมัติ
- [x] เพิ่มการตรวจ MFA/session และ scoped lifecycle; บังคับว่าการมอบหมายระดับบริษัทไม่หมายถึงขยายสิทธิ์อ่านการเงินทุกโครงการ ใช้ขอบเขตของ capability โดยตรง
- [x] รันเทสต์เขียว รวม RoleAuthorizationTests, AssignmentApiTests และ ScopedMfaTests; ตรวจทุก endpoint เก่าที่เพิ่ม role/scope ไม่เป็นทางลัด
- [x] ตรวจและ commit: `feat: enforce field advance access boundaries`

**ผลตรวจ Task 3 — 20 ตุลาคม 2569:**

- ผู้ใช้ยืนยันสิทธิ์บริหารระดับบริษัทใช้กับโครงการได้เฉพาะที่ได้รับมอบหมายเป็นรายโครงการ ทุก capability จับคู่บริษัทและโครงการตรงกัน; grant บริษัทที่ไม่ระบุโครงการไม่เป็น wildcard และไม่เพิ่มสิทธิ์ดูข้อมูลการเงิน
- เพิ่ม `FinanceCapabilities`, `FinancePersonResolver`, `FinanceAccess`, `SeparationRules` และเทสต์; แก้ `AssignmentLifecycle` ให้ถอนสิทธิ์การเงินพร้อมประวัติและ session เมื่อปิดบริษัทหรือโครงการ ไม่ฟื้นสิทธิ์เก่าเมื่อเปิดใหม่ และไม่ถอนทั้งโครงการจากการปิดเพียงไซต์
- ตรวจเส้นทาง role/account/scope เดิมด้วย HTTP tests แล้ว ไม่มีการแปลง generic role เป็น finance grant จึงไม่แก้ไฟล์เดิมที่มี guard เพียงพอ
- ผู้ลงมือ Meitner และผู้ตรวจอิสระ Beauvoir แยกกัน ผู้ตรวจให้ผ่านทั้งข้อกำหนดและคุณภาพโค้ด ไม่มี findings; ทบทวนไฟล์ implementation/tests 8 ไฟล์ที่ตรึง hash ไว้
- TDD: หลังแก้ข้อมูล fixture แล้ว RED 64 → GREEN 64; เพิ่ม lifecycle tests พบ assertion ล้มเหลว 3 กรณีและแก้ implementation จน GREEN 87; เพิ่ม catalog regression แล้วชุดสุดท้ายเป็น Task 3 จำนวน 88 เทสต์ + regression 104 รวม **192 ผ่าน ไม่มีล้มเหลวหรือข้าม**
- Backend ทั้งชุด **1,946/1,946 ผ่าน ไม่มีล้มเหลวหรือข้าม** exit 0 ใช้เวลา 43.4753 นาที รัน `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'` หลังผ่านรีวิว; ผล 192 รายการข้างต้นเป็นชุดย่อย ไม่บวกซ้ำเป็นจำนวนเทสต์ทั้งหมด
- ตรวจรอบสุดท้ายหลังชุดรวม: `dotnet build backend/TPR10.sln --no-restore` ผ่าน 0 warnings/errors, `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` ผ่าน; `npm test` **74/74**, `npm run build`, `npm run lint` และ `git diff --check` ผ่าน exit 0 ตรวจ SHA-256 implementation/tests ทั้ง 8 ไฟล์ตรงกับชุดที่รีวิว
- HTTPS/Firefox บนฐานทดสอบทิ้งได้: Production 4001 ผ่าน identity 13 + scopes 11 + boundary 2; Dev 4000 ผ่าน identity 13 + scopes 11 รวม **50/50 ตามโหมดที่กำหนด** โดยไม่ปิดตรวจ TLS ไม่ใช้ข้อมูลจริง และไม่ใช่มือถือจริง
- Production ในชุดเบราว์เซอร์หมายถึง Next production build; API ใช้ environment `Testing` สำหรับ fixture ไม่ใช่หลักฐานทดสอบ API deployment จริง ส่วน API Production boundary ตรวจในชุด backend เดิม
- ประวัติการตรวจผิดโหมด: นำ `scopes-boundary.spec.ts` ซึ่งระบุ Production ไปรันใน Dev ได้ 1 failed/1 passed เพราะ Dev เปิด technical UI ตาม `lib/scopes/server-scopes.ts:6` และแบบเดิม `docs/architecture/module-3-task-8.md` คาดไว้ ตรวจซ้ำหลัง Production build ได้ 2/2 ผ่านโดยไม่แก้ assertion หรือโค้ด ไม่มีการนับรอบ Dev ที่ผิดโหมดว่าเป็นผลผ่าน
- log เบราว์เซอร์รอบแรกมีคำเตือนตัวแปรสีชนกัน และ Dev มี diagnostic `NS_BINDING_ABORTED` ระหว่าง navigation; เทสต์ในโหมดที่กำหนดผ่าน แต่ไม่ได้อ้างว่า log ทุกชุดว่างจากคำเตือน
- ข้อผูกพันสำหรับ Task 4–7: caller ต้องถือ transaction และ identity lock `7241002` ตลอดรายการที่คุ้มครองสิทธิ์ ใช้ scope ของเป้าหมายจริง และส่งประวัติบุคคลจากเซิร์ฟเวอร์ให้ `SeparationRules` ไม่รับประวัติจาก client; Task 3 ยังไม่สร้าง endpoint/workflow การเงิน
- Implementation commit `ddcf6809deb5923dbf3e24721527662f5d80c4a9` — `feat: enforce field advance access boundaries`; ไม่มี push/PR/merge ยังพักมือถือจริง/NAS จริง และรออนุมัติเริ่ม Task 4

### Task 4: เสนอและอนุมัติสิทธิ์สามคน พร้อมการถอนและหมดอายุ

**ไฟล์:** `FA/Grants/GrantService.cs`, `GrantLifecycle.cs`, `GrantEndpoints.cs`; `IT/FieldAdvanceGrantTests.cs`, `FieldAdvanceGrantRaceTests.cs`; เชื่อม `Organization/OrganizationService.cs` เพื่อถอนตามโครงการปิดใช้

**สัญญา:** `GrantService.ProposeAsync(Guid actorId, GrantSpec spec, CommandMeta meta, CancellationToken ct): Task<FinanceResult<GrantView>>`; `ReviewAsync(Guid actorId, Guid proposalId, string decision, CommandMeta meta, CancellationToken ct): Task<FinanceResult<GrantView>>`; `GrantLifecycle.RevokeAsync(Guid actorId, Guid grantId, CommandMeta meta, CancellationToken ct): Task<FinanceResult<GrantView>>`

- [x] เขียนเทสต์สามคนต่างกันสำเร็จ; คู่ใดซ้ำต้อง403และไม่มี grant; อำนาจมอบหมายไม่ครอบคลุม capability/scope/วงเงินต้อง403; สิทธิ์เริ่มอนาคตยังใช้ไม่ได้และตรงหมดอายุใช้ไม่ได้
- [x] เพิ่ม tests reviewส่งกลับ/ปฏิเสธต้องมีเหตุผล; ขยายโครงการ/วงเงิน/เวลาเป็นข้อเสนอใหม่; คืนสิทธิ์ไม่แก้แถว revoked ให้ active; โครงการเปิดใหม่ไม่ฟื้นสิทธิ์
- [x] รัน filter `FullyQualifiedName~FieldAdvanceGrant` ให้แดง
- [x] ทำ service ด้วย transaction, current identity lock ตามรูปแบบเดิม และ command receipt ผูก actor+scope+payload hash: ส่ง commandเดิมเนื้อหาเดิมคืนผลเดิม ส่งIDเดิมเนื้อหาใหม่409; ระหว่างเปลี่ยนสถานะและ auditต้อง atomic
- [x] ตรวจต้นทาง delegable capabilities ของผู้เสนอและผู้ตรวจทั้งเวลาส่งและอนุมัติ; สิทธิ์ grant.propose/review ไม่ทำให้มอบทุก capability ได้; ไม่มีผู้ตรวจให้ค้างไม่ autoapprove
- [x] รันเทสต์ race ถอนสิทธิ์กับ review และ audit failure rollback ให้เขียว: ผลสุดท้ายต้องไม่เหลือ grantจากผลอนุมัติที่ใช้สิทธิ์ไม่ถูกต้อง
- [x] ตรวจและ commit: `feat: add three-person finance grant workflow`

**ผลส่งมอบ Task 4 — 20 ตุลาคม 2569, commit `22471c5`:**

- เพิ่ม `GrantService`, `GrantLifecycle`, `GrantEndpoints` และเทสต์สองไฟล์ รวม 5 ไฟล์; ไม่มี schema/migration ใหม่ ไม่เปิด route/DI ซึ่งอยู่ Task 6 และใช้ hook ถอนสิทธิ์เมื่อปิดบริษัท/โครงการของ Task 3 เดิมโดยไม่เพิ่ม audit ซ้ำ
- ผู้ใช้ยืนยัน: ไม่ระบุวงเงินไม่ใช่ไม่จำกัด ไม่รวมวงเงินหลายสิทธิ์; อายุสิทธิ์ที่มอบไม่เกินอำนาจมอบหมายทั้งผู้เสนอและผู้ตรวจ; ผู้ถอนต้องมีสิทธิ์ถอนและอำนาจมอบหมายครอบคลุมประเภทสิทธิ์/วงเงินเป้าหมาย
- รองรับเสนอ → อนุมัติ/ส่งกลับ/ปฏิเสธ, ถอนพร้อมรักษาประวัติ, ตรวจบุคคลสามคนต่างกัน, scope ตรงบริษัทและโครงการ, current session/MFA, ตรวจอำนาจซ้ำก่อนบันทึก และ receipt กันคำสั่งซ้ำใน transaction เดียวกับ audit
- ผู้ลงมือ Planck และผู้ตรวจ Lagrange แยกกัน; ผู้ตรวจให้ผ่านข้อกำหนดและคุณภาพ ไม่มี Critical/Important
- ข้อเสนอ Minor ส่งต่อ Task 10: เพิ่มกรณี replay หลัง session/capability ถูกถอน และ workflow สำเร็จใน company scope ที่ ProjectId=null; ผู้ตรวจพบว่าโค้ดปัจจุบันถูกต้อง เป็นข้อเสนอเพิ่ม coverage ไม่ใช่บั๊กที่พบ
- TDD: behavioral RED ของบริการ 3 กรณีและ adapter 6 กรณี ก่อน implementation; focused สุดท้าย **80/80** ไม่มี fail/skip; impacted regression **508/508** ไม่มี fail/skip exit 0 ใช้เวลา 10m26s
- ประวัติ failure: แก้ EF concurrency โดย attach entity ที่ version เดิมก่อนเปลี่ยน version; แก้ fixture เตรียม MFA ก่อน concurrent commands ไม่เปลี่ยน assertions หรือกฎ production เพื่อให้ผ่าน
- Backend Build/Format ของผู้ลงมือผ่าน; Node รอบส่งมอบ **74/74**, frontend Build/Lint และ diff check ผ่าน exit 0
- HTTPS/Firefox regression ผ่าน **50/50**: Next Production 4001 (identity 13, scopes 11, boundary 2) และ Dev 4000 (identity 13, scopes 11), API Testing บนฐานทิ้งได้ ไม่ปิดตรวจ TLS; Dev มี diagnostic `NS_BINDING_ABORTED` ระหว่าง navigation แต่ assertions ผ่าน ไม่ใช่การรับรองมือถือจริงหรือ production API deployment
- Backend ทั้งชุด **2,026/2,026 ผ่าน ไม่มี fail/skip**, exit 0 ใช้เวลา **44.4000 นาที** ด้วย `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'`; จำนวน focused/regression ด้านบนเป็นชุดย่อย ไม่บวกซ้ำกับยอดรวม
- หลังชุดรวมจบ `dotnet build backend/TPR10.sln --no-restore` ผ่าน 0 warnings/errors; `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` และ diff check ผ่าน exit 0; SHA-256 ทั้ง **5/5 ไฟล์** ตรงชุดที่ผู้ตรวจอ่านและที่รันทดสอบ ก่อน commit
- Node `npm test` 74/74, `npm run build`, `npm run lint` ผ่านรอบส่งมอบ; ไม่ใช้ผลการรันจาก Task เก่าเป็นหลักฐานใหม่
- ใช้ `ExpectedVersion=1` ตอนเสนอ; review ใช้ proposal version; revoke ใช้ assignment ID/version ส่วน GrantView จาก propose/review เป็น proposal ID — Task 6 ต้องเชื่อม target ให้ตรง
- ส่งกลับเป็น Draft ตาม schema แต่ไม่เปิดการแก้ประวัติ approved/revoked; เสนอข้อมูลที่แก้เป็นข้อเสนอใหม่ได้ ยังไม่สร้าง workflow คำขอเงินจริง 7B–7D
- ยังพักมือถือจริง/NAS จริง ไม่มี push/PR/merge และไม่เริ่ม Task 5

### Task 5: ตั้งต้นผู้บริหารสิทธิ์โดยไม่ให้ admin อัตโนมัติ

**ไฟล์:** `FA/Grants/FinanceBootstrapCommand.cs`; เชื่อม `backend/src/TPR10.Api/Program.cs`; `IT/FieldAdvanceBootstrapTests.cs`; สร้าง `docs/runbooks/module-7a-policy-permissions.md`

**สัญญา:** `FinanceBootstrapCommand.RunAsync(string[] args): Task<int>` เป็น local operator command ไม่ใช่ HTTP endpoint และไม่รันระหว่าง startเว็บปกติ

- [x] เขียน tests ฐานใหม่ไม่มี grant; รายชื่อรับ/ผู้รับรอง/ผู้ตรวจขาดหรือคนไม่ตรงข้อกำหนดปฏิเสธ; รันซ้ำหลังตั้งต้นแล้วปฏิเสธแม้ภายหลัง revokeทั้งหมด; failauditไม่ทิ้งgrantครึ่งชุด
- [x] รัน `dotnet test backend/tests/TPR10.Api.IntegrationTests --filter FullyQualifiedName~FieldAdvanceBootstrapTests` ให้แดง
- [x] รับ manifest ผ่าน stdin มีบัญชีจริง/บุคคล/บริษัท, อำนาจมอบหมาย, ผู้รับรอง, ผู้ตรวจอิสระ และเลขอ้างอิงหลักฐานภายนอก ตรวจ user active/MFA enrollmentและตัวตนต่างกันตามข้อกำหนด; ไม่มี defaultadmin; บันทึก receiptถาวรครั้งเดียวต่อบริษัท
- [x] ระบุใน runbook ว่ารหัสผู้รับรองในmanifestไม่ใช่ลายเซ็นดิจิทัล ผู้ปฏิบัติการต้องตรวจเอกสารบริษัทจริงและผู้ตรวจอิสระก่อนรัน การควบคุมผู้เข้าถึงเครื่อง/DBอยู่นอกแอป; ไม่อ้างว่าการกรอกชื่อเป็นการรับรองที่ตรวจด้วยคริปโต
- [x] รันเขียวบนDBทิ้งได้เท่านั้น ห้ามตั้งต้นสิทธิ์บนข้อมูลจริงระหว่างพัฒนา; บันทึกการกู้คืนกรณีผู้บริหารสิทธิ์หายว่าไม่เปิดbootstrapซ้ำอัตโนมัติ ต้องกระบวนการบริษัทอนุมัติแยก
- [x] ตรวจและ commit: `feat: add controlled finance permission bootstrap` — `901f6a6`

#### ผลตรวจ Task 5 — 21 ตุลาคม 2569

- ผู้ลงมือ Faraday แยกจากผู้ตรวจ Anscombe; ผู้ตรวจอนุมัติทั้งข้อกำหนดและคุณภาพ ไม่พบข้อแก้ไขจากชุด 4 ไฟล์
- ยืนยันสามกติกาตาม spec §3.4: mapping ระบุชัด/ปฏิเสธขัดแย้ง, แยกผู้รับรอง–ผู้ตรวจ–ผู้รันออกจากกันและผู้รับทั้งหมด, สิทธิ์ใช้งานตั้งต้นเฉพาะบริหารสิทธิ์
- TDD พิสูจน์ CLI stub คืนผลไม่ตรงที่คาดก่อน implementation; focused **76/76** และ impacted regression **178/178** ผ่าน ไม่มี fail/skip ใช้ executable และ PostgreSQL ทิ้งได้จริง
- แยก fixture/setup failures ออกจาก RED; แก้ fixture ประวัติ immutable และ DbContext ต่อคำขอ ไม่ลด assertion หรือเปลี่ยน GrantService เดิมเพื่อให้ผ่าน
- Format รอบแรกไม่ผ่าน whitespace สองไฟล์ใหม่ แก้เฉพาะช่องว่างและตรวจเนื้อหาตัด whitespace ตรงเดิม; Build/Format หลังแก้ผ่าน
- เว็บรอบสุดท้าย Node **74/74**, Build และ Lint ผ่าน; HTTPS **50/50** ผ่าน (Next Production 26, Dev 24; API Testing บนฐานทิ้งได้) ไม่ปิดตรวจ TLS ไม่ทดแทนมือถือจริง; Dev มี diagnostic `NS_BINDING_ABORTED` ระหว่าง navigation แต่ assertions ผ่าน
- Backend ทั้ง solution **2,102/2,102 ผ่าน ไม่มี fail/skip**, exit 0 ใช้เวลา **45.2692 นาที** จาก `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'`; focused/regression เป็นชุดย่อย ไม่บวกซ้ำกับยอดรวม
- รอบส่งมอบ `dotnet build backend/TPR10.sln --no-restore` ผ่าน 0 warnings/errors; `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` และ `git diff --check` ผ่าน exit 0; SHA-256 ทั้ง **4/4 ไฟล์** ตรงกับชุดที่รีวิวและรันเทสต์ก่อน commit
- บันทึกโค้ด เทสต์ และคู่มือใน local commit `901f6a6`; ไม่ใช้ผลจาก Task ก่อนเป็นหลักฐานของรอบนี้
- ไม่รัน bootstrap บนข้อมูลจริง ไม่ push/PR/merge ไม่เริ่ม Task 6; มือถือจริงและ NAS จริงยังพักไว้

### Task 6: วงจรฉบับกติกาและการเปิดใช้แบบ atomic

**ไฟล์:** `FA/Policies/PolicyService.cs`, `PolicyEndpoints.cs`; `FA/FieldAdvanceRegistration.cs`, `FieldAdvanceOpenApiTransformer.cs`; `IT/FieldAdvancePolicyTests.cs`, `FieldAdvancePolicyRaceTests.cs`, `FieldAdvanceOpenApiTests.cs`; เชื่อม `Program.cs`

**สัญญา:** `CreateDraftAsync(Guid actorId, FinanceScope scope, Guid? baseCompanyVersionId, PolicyContent content, CommandMeta meta, CancellationToken ct): Task<FinanceResult<PolicyView>>`; `UpdateDraftAsync(Guid actorId, Guid id, PolicyContent content, CommandMeta meta, CancellationToken ct): Task<FinanceResult<PolicyView>>`; `SubmitAsync(Guid actorId, Guid id, CommandMeta meta, CancellationToken ct)` และ `ReviewAsync(Guid actorId, Guid id, string decision, CommandMeta meta, CancellationToken ct)` คืน `Task<FinanceResult<PolicyView>>` ทั้งคู่

- [x] เขียน tests สร้าง/แก้/ส่ง/อนุมัติ, contributorคนใดอนุมัติไม่ได้, pendingแก้ตรงไม่ได้, returnedแก้แล้วต้องส่งใหม่, rejectedไม่reuseapproval, activeimmutable
- [x] เพิ่ม tests โครงการอ่อน/เข้มกว่าบริษัทได้แต่ผู้ตรวจต้องมีสิทธิ์บริษัทที่มอบหมายตรงโครงการ ไม่ต้องมี grant ระดับ null-project เพิ่ม; เปลี่ยนกลับ inheritผ่านreview; เปิดสองdraftพร้อมกันต้องมีactiveเดียวและตรวจ expected active revision
- [x] รัน filter `FullyQualifiedName~FieldAdvancePolicy|FullyQualifiedName~FieldAdvanceOpenApi` ให้แดง
- [x] ทำ endpointsใต้ `/api/v1/field-advances` สำหรับ `policies`, `policies/{id}`, `policies/{id}/submit`, `policies/{id}/review`, `grants`, `grants/{id}/review`, `grants/{id}/revoke` โดย POST/PUTมีCSRF, MFA, no-storeและขอบเขตตรวจจากserver; ไม่มี DELETEประวัติ
- [x] ผูก reviewกับhash/versionที่ผู้ตรวจเห็น เก็บcontributorข้ามการส่งกลับและrevisionsที่แก้จากฉบับเดิม เปิดใช้/retireฉบับก่อน/audit/receiptในธุรกรรมเดียว พร้อมใช้ผล Task2/3
- [x] รันเขียวรวม tests faultก่อนcommit/ระหว่างaudit และ staleversion409; OpenAPIไม่เปิดprobes production
- [x] ตรวจและ commit: `feat: add versioned finance policy approval` — `c2aeb9e`

#### ผลตรวจ Task 6 — 21 ตุลาคม 2569

- ผู้ลงมือ Schrodinger แยกจากผู้ตรวจ Turing; ผ่านทั้งข้อกำหนดและคุณภาพ ไม่มี Critical/Important จากชุด backend 17 ไฟล์
- ผู้ใช้ยืนยัน: ผู้แก้บริษัทฐานไม่เป็น contributor โครงการอัตโนมัติ แต่ผู้ร่วมแก้สายกติกาโครงการทุกแขนงยังห้ามตรวจ; การกลับ inherit ถ้าบริษัทเปลี่ยนระหว่างรอตรวจต้อง409 โหลดฐานล่าสุดและส่งตรวจใหม่
- เพิ่มวงจร Draft → Pending → Active/Returned/Rejected, เก็บ review hash/version/content ที่เห็นจริง, ตรวจ current authority ก่อน replay และเปิดใช้/retire/audit/receipt ใน transaction เดียว
- เพิ่ม migration `20261020233422_AddFieldAdvancePolicyLifecycle`; หลักฐาน review เก่าคง unknown ไม่สร้างย้อนหลัง และ downgrade ปฏิเสธเมื่อจะทำข้อมูล lifecycle ใหม่สูญหาย ไม่รันกับฐานข้อมูลจริง
- Contract เพิ่ม `CommandMeta.Policy` แบบ optional และไม่ serialize null เพื่อรักษา receipt ของ grant เดิม; `PolicyView.ExpectedActive*` คือฐานเปรียบเทียบที่ฉบับนั้นใช้ ไม่ใช่ pointer หลังอนุมัติ Task 7 ต้องอ่าน pointer ปัจจุบันเอง
- TDD: service/OpenAPI RED17 และ race/rollback RED4 ก่อน implementation พร้อม regression RED สำหรับ lineage, receipt compatibility, downgrade และ contributor ของการ submit; focused สุดท้าย **56/56 ผ่าน** รวม API inventory ที่แก้แล้ว
- Impacted รอบแรก **636/637** ล้มเฉพาะ `IdentityOpenApiTests.Enumerates_every_versioned_operation_and_cookie_flags` ที่ยังขาด7 routes ใหม่ แก้ expected inventory และยืนยันผ่านใน targeted56; ไม่อ้างว่ารอบเดิมผ่าน
- Backend ทั้ง solution **2,157/2,157 ผ่าน ไม่มี fail/skip**, exit0 ใช้เวลา **47.1080 นาที** ด้วย `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'`; focused เป็นชุดย่อย ไม่บวกซ้ำ
- เว็บรอบสุดท้าย **74/74**, Build และ Lint ผ่าน; backend Build **0 warnings/errors**, Format verify และ diff check ผ่าน; SHA-256 **17/17** ตรงชุด review
- HTTPS ครบ50กรณี: production26 และ dev24 บนฐานข้อมูลทิ้งได้/CA เฉพาะ profile; รอบแรก dev scopes timeout1ที่โหลด `/login` รอ `app/login/page.js` ก่อนส่งข้อมูลเข้า API ต่อมารันทั้ง11กรณีพร้อม timing diagnostics ผ่านโดยไม่แก้โค้ดหรือผ่อน assertion ยังไม่ทราบต้นเหตุถาวร จัดเป็นความไม่สม่ำเสมอของ dev asset loading ไม่ปกปิด failure แรก
- ความผิดพลาดเครื่องมือ: เริ่ม full suite รอบแรกด้วย PATH ที่ไม่มี `rg` จึงเก็บ log ไม่ได้ หยุดเฉพาะ process รอบนั้นแล้วรันใหม่ด้วย absolute path ผล2,157ข้างต้นมาจากรอบใหม่ที่จบ exit0 ไม่ใช่รอบที่หยุด
- Minor ส่งต่อ Task10: ปรับการค้น lineage ที่อาจ O(n²) เมื่อประวัติยาว และเพิ่ม coverage แข่งแทน active ที่มีอยู่แล้ว; ไม่พบความผิดพลาดของ pointer compare ปัจจุบัน
- Local code commit `c2aeb9e`; ไม่ push/PR/merge ไม่เริ่ม Task7 ไม่สร้างสิทธิ์จริง มือถือจริง/NAS จริงยังพัก

### Task 7: เลือกฉบับกติกา ประเมินตัวอย่าง และ API อ่านที่ไม่รั่วข้อมูล

**ไฟล์:** `FA/Policies/PolicyResolver.cs`, `PolicyReadService.cs`; เพิ่ม routesใน `PolicyEndpoints.cs`; `IT/FieldAdvanceResolverTests.cs`, `FieldAdvanceReadTests.cs`

**สัญญา:** `PolicyResolver.ResolveAsync(FinanceScope scope, DateTimeOffset asOf, CancellationToken ct): Task<FinanceResult<PolicySnapshot>>`; `PolicyReadService.ListAsync(Guid actorId, FinanceScope scope, int page, int pageSize, CancellationToken ct): Task<FinanceResult<PolicyView[]>>`; `PreviewAsync(Guid actorId, FinanceScope scope, decimal total, CancellationToken ct): Task<FinanceResult<string[]>>`

- [x] เขียน tests inheritได้บริษัทล่าสุด, overrideยังใช้baseเก่าและรายงานต้องทบทวน, rebaseหลังreviewจึงใช้ใหม่, กลับinheritต้องreview, ไม่มีกติกาคืน409ไม่สร้างค่าเอง
- [x] เขียน tests snapshotก่อน/หลังเปลี่ยนกติกา: `Assert.Equal(oldHash, savedSnapshot.ContentHash); Assert.NotEqual(oldHash,newSnapshot.ContentHash);` เก็บชุดsnapshotเป็นข้อมูลimmutable ไม่คืน EF trackedobjectให้แก้
- [x] รัน filter `FullyQualifiedName~FieldAdvanceResolver|FullyQualifiedName~FieldAdvanceRead` ให้แดง
- [x] ทำ GET policy/history/review queue ภายใต้scopeที่ตรวจ และ POST `policies/preview` เป็นread-onlyด้านธุรกิจ; filterก่อนนับ/แบ่งหน้า ห้ามเผยชื่อโครงการหรือจำนวนรายการต่างscope; snapshotมีIDบริษัท/โครงการ/hashและเนื้อหาที่คำนวณแล้ว
- [x] รันเขียวเพิ่ม unauthorized list/details/preview/history, pagingผิด, directIDต่างบริษัท และยืนยันpreviewไม่มีwriteธุรกิจ; freezeกติกาไม่freezeสิทธิ์
- [x] ตรวจและ commit: `feat: resolve and preview effective finance policies`

#### ผลตรวจ Task 7 — 21 ตุลาคม 2569

- ผู้ใช้ยืนยันสิทธิ์อ่านสองข้อแล้วตาม spec ส่วน3.1: editor หรือ reviewer ต้องตรง scope; คิวอนุมัติต้องเป็น reviewer และสิทธิ์โครงการเปิดได้เฉพาะเนื้อหาบริษัทฐานที่จำเป็น ไม่เปิดรายการ/ประวัติบริษัททั้งหมด
- เพิ่ม resolver และ API อ่าน6รายการ: รายการ รายละเอียด ประวัติ คิวรออนุมัติ กติกาที่มีผล และประเมินระดับอนุมัติ ตรวจ current session/MFA/person/grant ก่อนและหลังอ่านภายใต้ transaction/identity lock; POST preview ยังต้องผ่าน CSRF และไม่มี business write
- Inherit ติดตามบริษัทตามเวลาที่พิสูจน์ได้; override ตรึงฐานและแสดงต้องทบทวนจนผ่าน rebase; snapshot แยกสำเนา nested arrays และแยก actual pointer จาก `ExpectedActive*` ไม่เปลี่ยน receipt เดิม
- ข้อยุติทางเทคนิค: `asOf` ไม่ย้อนสิทธิ์ปัจจุบัน เวลาอนาคตคืน400 และหลักฐานย้อนหลังไม่พอ/ขัดกันคืน409 ไม่สร้างประวัติขึ้นเอง ข้อมูล legacy อาจต้องตรวจหลักฐานก่อนอ่านย้อนหลังได้ ไม่ backfill ใน Task นี้
- TDD: เริ่ม37 behavioral RED ก่อน implementation เพิ่ม RED กรณี content/hash ไม่ตรงและ history ไม่มีผู้ตรวจ แล้วแก้จน focused **57/57** ผ่าน; impacted **691/691** ผ่าน ก่อนรีวิว การแก้ fixture สองกรณีเรื่อง audit/version ไม่ถูกนับเป็น production defect
- แยก Peirce ผู้ลงมือกับ Boole ผู้ตรวจ: รอบแรก spec ผ่าน แต่พบ loader ซ้ำระดับ Important; รวมให้ใช้ implementation เดียว รัน covering **105/105** ผ่าน แล้ว re-review อนุมัติทั้ง spec และ quality ไม่มี Critical/Important ค้าง
- Backend ทั้ง solution **2,211/2,211 ผ่าน ไม่มี fail/skip**, exit0 ใช้เวลา **49.0872 นาที** จาก `dotnet test backend/TPR10.sln --no-build --no-restore --logger 'console;verbosity=normal'`; focused/impacted เป็นชุดย่อย ไม่บวกซ้ำ
- Backend Build **0 warnings/errors**, `dotnet format backend/TPR10.sln --verify-no-changes --no-restore` และ diff check ผ่าน ตรวจ SHA-256 ตรงกับโค้ดที่รีวิวครบ **13/13 ไฟล์** หลัง full tests
- เว็บ `npm test` **74/74**, `npm run build`, `npm run lint` ผ่านหลังรัน HTTPS Dev แล้ว
- HTTPS ครอบคลุม **50 กรณี** จากรอบที่สำเร็จ: Production4001 identity13 + scopes11 + boundary2, Dev4000 identity13 + scopes11 ผ่าน proxy4443 โดย CA เชื่อถือเฉพาะ profile ทดสอบ ไม่แก้ trust store เครื่อง
- ข้อสังเกตที่คงไว้: รอบแรก Production boundary timeout ที่ `page.goto('/login')` ก่อนกรอกข้อมูล รันซ้ำพร้อม diagnostics ผ่าน2/2 โดยไม่เปลี่ยนโค้ดหน้าเว็บหรือ assertion; หน้า login ตอบ200ใน11–62ms และ assets200ประมาณ1–7ms ในรอบวินิจฉัย ยังพิสูจน์สาเหตุ timeout ครั้งแรกไม่ได้ จึงไม่อ้างว่าแก้ถาวร
- Minor สำหรับ Task10: ลด N+1 queries ใน resolver/list เมื่อประวัติมาก โดยคงการตรวจหลักฐานและ identity lock; เป็นข้อยุติของ controller จากระดับความเสี่ยงในรีวิว ยังไม่ได้ทำ performance benchmark และไม่อ้างว่าแก้แล้ว
- Code commit: `e3e11fd48433ed0878bc07e7ba52da4d5c97a716` — local เท่านั้น ไม่ push/PR/merge ไม่มี migration/bootstrap หรือข้อมูลจริงถูกเปลี่ยน พักมือถือจริง/NAS จริง; Task8 หน้าจอกติกาเป็นขั้นถัดไปเมื่อผู้ใช้สั่งเริ่ม

### Task 8: หน้าจอกติกาบริษัท โครงการ หมวด และการตรวจฉบับ

**ข้อยืนยันก่อนลงมือ (21 ตุลาคม 2569):** ผู้ใช้ยืนยันให้เพิ่มรายการขอบเขตการเงินเฉพาะโครงการ/ชื่อ/สิทธิ์ที่บัญชีปัจจุบันเข้าถึงได้จริง ไม่เปิดรายชื่อโครงการอื่นหรือทะเบียนสิทธิ์ ผู้แก้ไขและผู้ตรวจในขอบเขตโครงการอ่านเฉพาะรหัส/เนื้อหา/hash ของกติกาบริษัทฐานเดิมและฉบับล่าสุดเพื่อเทียบและจัดทำฉบับใหม่ได้ แต่ไม่เปิดประวัติบริษัททั้งหมดหรือค้นฉบับอื่นโดยอิสระ จำนวนเงินบน HTTP ของกติกาและเว็บใช้ข้อความเพื่อรักษาสตางค์ โดย API ยังคงรับตัวเลขจาก client เดิมผ่าน .NET decimal โดยตรง และไม่เปลี่ยน hash หลักฐานหรือใบรับคำสั่งภายในเดิม

**ขอบเขตทดสอบ:** ย้ายเฉพาะ fixture กติกาแบบทิ้งได้จากส่วนเตรียม Task 9 มาใช้พิสูจน์หน้าจอ Task 8 ไม่ทำหน้าจอมอบหมายสิทธิ์หรือ bootstrap ฐานจริง ตัวแก้ไขกติกาแก้อัตรามาตรฐานได้ตามสิทธิ์และต้องผ่านอนุมัติ ส่วนการใช้จำนวน × อัตราในคำขอเงินจริงยังเป็นงานถัดไป

**ไฟล์:** `lib/field-advances/policy-types.ts`, `policy-client.ts`, `policy-view.ts`; สร้าง `WEB/page.tsx`, `WEB/company/page.tsx`, `WEB/projects/[projectId]/page.tsx`, `WEB/categories/page.tsx`, `WEB/reviews/page.tsx`, `WEB/PolicyEditor.tsx`; เชื่อม `app/portal/page.tsx`; `tests/field-advance-policy.test.mjs`, `tests/e2e/field-advance-policy.spec.ts`

**สัญญา:** TS typesตรงrecords Task1; `loadPolicies(scope: FinanceScope): Promise<PolicyView[]>`, `savePolicy(id: string | null, scope: FinanceScope, content: PolicyContent, meta: CommandMeta): Promise<PolicyView>`, `reviewPolicy(id: string, decision: 'approve'|'return'|'reject', meta: CommandMeta): Promise<PolicyView>`; clientใช้session-fetchเดิม ไม่เก็บtokenใหม่ในlocalStorage

- [x] เขียนNode testsจากแนว `tests/helpers/load-ts.mjs`: mappingสถานะ/ที่มา, decimalเป็นstringบนwireไม่ผ่าน JS float, 409บังคับโหลดใหม่, 403ไม่โชว์ข้อมูลเก่า
- [x] เขียนPlaywrightแยกบัญชีeditor/reviewer: draft→submit→compare→approve; editorเห็นปุ่มไม่พอแต่เรียกAPIก็403; overrideแสดงbaseเก่า+ต้องทบทวน; categoryปิดไม่ลบhistory
- [x] รันเทสต์ให้แดงก่อนพัฒนาบน fixture ทิ้งได้ตามบันทึก TDD และเพิ่ม RED สำหรับข้อพบจากรีวิวทั้งสองจุด
- [x] ทำหน้าจอไทยครบตามspec แสดงฉบับ/ที่มา/diff/reason/error/empty/loading และpreviewวงเงิน ไม่ทำฟอร์มคำขอเงินจริง; ตัวแก้ไขกติกาแก้อัตราได้ตามสิทธิ์และต้องผ่านอนุมัติตามข้อยืนยันข้างต้น
- [x] รันเขียว; ตรวจkeyboard labels และlayoutจอแคบในเบราว์เซอร์จำลอง โดยระบุชัดว่าไม่ใช่ทดสอบมือถือจริง
- [x] ตรวจและ commit: `feat: add finance policy management screens` — `fb98728`

**ผลตรวจ Task 8 — 21 ตุลาคม 2569**

- แยก Linnaeus ผู้ลงมือและ Gibbs ผู้ตรวจ ใช้ TDD; แก้ Important สองจุดแล้วตรวจซ้ำผ่าน: การเทียบฉบับต้องใช้ฐานของฉบับร่างหลังเปลี่ยนฐาน/บันทึก และต้องล้างผล preview เดิมเมื่ออนุมัติกติกาใหม่ มี regression RED→GREEN ทั้งคู่ ไม่มี Critical/Important ค้างจากรีวิว Task นี้
- Backend ทั้ง solution ผ่าน **2,218/2,218** ไม่มี fail/skip ใช้เวลา **50.2824 นาที**; focused 76 และ impacted 698 เป็นชุดย่อย ไม่บวกซ้ำ
- เว็บ **92/92** ผ่าน; Build และ Lint ผ่านครบ Backend Build 0 warnings/errors, format verify และ diff check ผ่าน ตรวจ SHA-256 ตรงกับไฟล์ที่รีวิว **24/24**
- HTTPS Task 8 ผ่าน **Dev 8/8 และ Production 8/8**; regression เดิมครอบคลุม **50 กรณี** จากรอบที่สำเร็จ (Production 26, Dev 24) ใช้ข้อมูลและ CA เฉพาะชุดทดสอบ ไม่เปลี่ยน trust store หรือข้อมูลจริง
- ข้อจำกัดที่ยังติดตาม: regression login มี timeout ก่อนกรอกข้อมูลเพราะช่องยัง disabled ในบางรอบ รันซ้ำพร้อม diagnostics ผ่านโดยไม่เปลี่ยน assertion หรือเพิ่ม timeout แต่ยังพิสูจน์สาเหตุไม่ได้ จึงไม่อ้างว่าแก้ถาวร; คำเตือน NO_COLOR/FORCE_COLOR เก็บติดตามใน Task 10
- หน้าจอและ API ใช้สิทธิ์การเงินปัจจุบัน/ขอบเขตจริง ไม่ให้ admin หรือสมาชิกทีมเข้าถึงอัตโนมัติ; จำนวนเงินบน wire เป็นข้อความ ไม่คำนวณผ่าน JS float
- Commit เฉพาะในเครื่อง ยังไม่ push/PR/merge ไม่มี migration/bootstrap จริง พักมือถือจริงและ NAS จริง งานถัดไปคือ Task 9 เมื่อผู้ใช้สั่ง

### Task 9: หน้าจอมอบหมายสิทธิ์ งานรอตรวจ และประวัติ

**ไฟล์:** `lib/field-advances/grant-client.ts`; `WEB/grants/page.tsx`, `WEB/history/page.tsx`, `WEB/GrantEditor.tsx`; รวมqueueสิทธิ์ใน `WEB/reviews/page.tsx`; `tests/field-advance-grant.test.mjs`, `tests/e2e/field-advance-grant.spec.ts`; เพิ่ม `backend/tests/TPR10.E2E.Fixture/FieldAdvanceSeed.cs` และเชื่อมfixture `Program.cs`

**สัญญา:** `proposeGrant(spec: GrantSpec, meta: CommandMeta): Promise<GrantView>`; `reviewGrant(id: string, decision: 'approve'|'return'|'reject', meta: CommandMeta): Promise<GrantView>`; `revokeGrant(id: string, meta: CommandMeta): Promise<GrantView>` — ข้อมูลบุคคลที่เลือกมาจากAPIที่กรองscope ไม่โหลดผู้ใช้ทั้งระบบลงbrowser

- [ ] เขียนNode testsช่วงเวลา/สถานะ/เหตุผล และmappingข้อผิดพลาดโดยไม่แสดงpayloadต่างscope
- [ ] เขียนE2Eใช้สามบัญชีและผู้รับจริง: เสนอ→ตรวจ→เริ่มใช้, ผู้รับreviewตัวเองไม่ได้, ถอนแล้วsessionเดิมทำต่อไม่ได้, ไม่มีautoapproveเมื่อผู้ตรวจไม่พอ
- [ ] รัน `node --test tests/field-advance-grant.test.mjs` และ `npx playwright test tests/e2e/field-advance-grant.spec.ts` ให้แดง
- [ ] ทำUIระบุcapabilities/ขอบเขต/เวลา/ผู้เกี่ยวข้อง แสดงdiffก่อนต่ออายุหรือเพิ่มอำนาจ; ทำhistoryตามสิทธิ์; fixtureใช้เฉพาะTesting ไม่เพิ่มบัญชีจริงหรือเปิดbootstrapในproduction
- [ ] รันเขียวรวม scenarioเปลี่ยนroleแล้วข้อมูลไม่มีสิทธิ์ต้องหายจากหน้าจอและAPI; ตารางประวัติไม่รวมยอดคำขอหรือหลักฐานการเงินจริง
- [ ] ตรวจและ commit: `feat: add finance permission workflow screens`

### Task 10: ตรวจรวม ความเสียหายจำลอง และเตรียมส่งมอบ7A

**ไฟล์:** `IT/FieldAdvanceDeliveryTests.cs`, `FieldAdvanceAtomicityTests.cs`; `docs/runbooks/module-7a-policy-permissions.md`; สร้าง `docs/reports/2026-10-19-module-7a-verification.md` โดยเขียนเวลารันจริงในเนื้อหา ไม่อ้างวันที่ชื่อไฟล์เป็นวันทดสอบ

**สัญญา:** ใช้บริการTask1–9โดยไม่เพิ่มendpointธุรกิจใหม่ ส่งมอบรายงานคำสั่ง/exit code/จำนวนผ่าน-ไม่ผ่าน/commit/ข้อจำกัด และรายการตรวจเกณฑ์ A01–A12

- [ ] เพิ่ม testsโจมตีทางAPIเดิม/aliasperson, parallelapprove/revoke, replaycommandข้ามscope, faultaudit, company/project inactive→activeไม่ฟื้นสิทธิ์ และsnapshotยังอ่านย้อนหลังได้โดยไม่คงสิทธิ์เก่า
- [ ] รัน filter `FullyQualifiedName~FieldAdvanceDelivery|FullyQualifiedName~FieldAdvanceAtomicity` ก่อนแก้ หากพบช่องโหว่ให้เก็บเทสต์แดงแล้วแก้เฉพาะเจ้าของโค้ด ไม่ลดassertionเพื่อผ่าน
- [ ] รันชุดตรวจทั้งหมดด้านล่างและบันทึกผลจริง ไม่ใช้จำนวนเทสต์จากModule6เป็นหลักฐานใหม่
- [ ] ทำCode Reviewทั้งbranchกับspec A01–A12: แก้ข้อพบ เพิ่มregressionและรันชุดที่กระทบซ้ำ; ไม่มีประเด็นความถูกต้อง/สิทธิ์ค้างที่ยังไม่ตัดสิน
- [ ] ตรวจ migration บนฐานทิ้งได้: applyจากฐานเดิม, backup/restore และ downgradeเมื่อไม่มีข้อมูล7A; หากมีประวัติแล้ว downgradeต้องหยุดอย่างปลอดภัย ไม่ลบประวัติการเงินเพื่อย้อนรุ่น
- [ ] เติมrunbookตั้งต้น/เปลี่ยนฉบับ/revoke/เหตุขัดข้อง/ข้อจำกัดinfra และรายงานว่า realmobile/NAS deferred, requestเงินจริง7B–7Dยังไม่ส่งมอบ
- [ ] ตรวจและ commit: `test: verify field advance policy delivery`; หยุดให้ผู้ใช้ตรวจผล ไม่ push/เปิดPR/mergeจนได้รับคำสั่ง

## ชุดคำสั่งตรวจจริงก่อนแจ้งพัฒนาเสร็จ

ใช้ฐานทดสอบจากTestcontainersตาม `IT/PostgresFixture.cs` และE2E fixtureที่แยกจากข้อมูลจริง; เริ่มAPI/เว็บ/HTTPSตามrunbookเดิมและยืนยัน environmentก่อนรัน ไม่ใส่secretsในคำสั่งหรือเอกสาร

```sh
dotnet test backend/tests/TPR10.Api.IntegrationTests
dotnet build backend/src/TPR10.Api
dotnet format backend/src/TPR10.Api --verify-no-changes
npm test
npm run build
npm run lint
npx playwright test
git diff --check
```

คาดหวัง: ทุกคำสั่ง exit0 และไม่มีtestsที่ข้ามด้วยเหตุไม่ได้เตรียมระบบโดยไม่รายงาน Playwrightใช้Firefox/HTTPSจริงตามconfigและไม่ปิดTLSตรวจสอบ รันชุด7AและregressionบนDev4000 แล้วบนProduction build4001ผ่านproxy4443ทีละชุด บันทึกโหมดแยกกัน; `npm run build`อย่างเดียวไม่แทนการทดสอบproduction

หาก environmentไม่พร้อม ให้รายงานว่า “ยังตรวจไม่ครบ” ไม่แจ้งว่างานพัฒนาเสร็จ Test/Build/Lintไม่ต้องรันเพื่ออ้างผลในรอบเขียนแผน เพราะยังไม่มีโค้ด7A

## การครอบคลุมเอกสารและผลทบทวนแผน

| เกณฑ์จากspec | Taskเจ้าของ |
|---|---|
| A01 ฉบับและผู้ร่วมแก้ | 1,6,8 |
| A02 วงเงินและระดับ | 2,7 |
| A03 override/snapshot | 6,7,8 |
| A04 หมวดอัตราหลักฐาน | 2,6,8 |
| A05 สามคนต่างกัน | 1,3,4,5,9 |
| A06 ปิดทางลัด | 3,4,10 |
| A07 ข้อมูลข้ามscope/admin | 3,7,8,9 |
| A08 อายุและประวัติข้อห้าม | 3,4,9,10 |
| A09 คำสั่งซ้ำและฉบับชน | 4,6,8,10 |
| A10 atomic rollback | 1,4,5,6,10 |
| A11 หน้าจอไทย | 8,9 |
| A12 regressionระบบเดิม | 3,10 |

แผนตรวจชื่อสัญญาระหว่างTasks ขอบเขตและจุดเสี่ยงทั้งห้ากลุ่มแล้ว จุดตั้งค่าเฉพาะบริษัทไม่ถูกแทนด้วยตัวเลขสมมติ รายละเอียดการปัดเศษจำนวน×อัตราของรายการคำขอยังเป็นขอบเขต7Bตามspec ไม่ทำเครื่องคำนวณค่าใช้จ่ายเพิ่มใน7A

## วิธีดำเนินงานให้ผู้ใช้เลือกหลังตรวจแผน

1. **แยกผู้ลงมือและผู้ตรวจราย Task (แนะนำ):** ใช้ subagentใหม่ทำแต่ละTask และผู้ตรวจใหม่ตรวจทุกTaskก่อนต่อ แล้วตรวจรวมbranch เหมาะกับสิทธิ์และข้อมูลการเงิน แต่ใช้ทรัพยากรมากกว่า
2. **ดำเนินงานในแชตนี้:** ผู้ช่วยหลักทำทีละTaskและให้ผู้ตรวจอิสระตรวจbranchตอนท้าย ใช้ทรัพยากรน้อยกว่า แต่ไม่มีผู้ตรวจอิสระคั่นทุกTask

ทั้งสองวิธีต้องได้รับอนุมัติแผนก่อนเริ่ม และทำ TDD/Code Review/Test/Build/Lint ตามเกณฑ์เดียวกัน

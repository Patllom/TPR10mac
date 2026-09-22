# แผนการพัฒนา Module 1: API / Web / Database Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task.

**เป้าหมาย:** สร้างฐานรากที่รันได้จริงสำหรับ Web, ASP.NET API และ PostgreSQL พร้อม migration, health checks, correlation ID และหลักฐาน audit ที่แก้ไขย้อนหลังไม่ได้ โดยยังไม่เพิ่มการยืนยันตัวตน สิทธิ์การใช้งาน หรือโมดูลธุรกิจ

**สถาปัตยกรรม:** Next.js ยังคงเป็น Landing Page ที่ `/` และเพิ่มเปลือกพื้นที่ภายในที่ `/portal` ส่วน ASP.NET Core เป็นเจ้าของ API และฐานข้อมูล PostgreSQL ทั้งหมด เว็บเรียก API ผ่านเส้นทาง same-origin `/api/*`; ในเครื่อง Next.js rewrite ไปยัง API และสภาพใช้งานจริง Nginx/reverse proxy จะส่ง `/api/*` ไป API กับส่งเส้นทางอื่นไป Next.js

**Tech Stack:** Next.js 14 / React 18 / Node 20, ASP.NET Core net10.0, EF Core 10 + Npgsql, PostgreSQL 17, Testcontainers PostgreSQL, Nginx

**ข้อกำหนดอ้างอิง:** [Architecture Baseline Module 0](../specs/2026-09-18-tpr10-module-0-architecture-baseline-design.md)

## ขอบเขตและข้อห้าม

- Module นี้จบที่ foundation เท่านั้น: ไม่มี login, session, MFA, RBAC, Workspace, Project, Site, ScopeContext หรือหน้าธุรกิจ
- `/portal` เป็นเพียงโครงหน้าและข้อความว่าการเข้าสู่ระบบจะเริ่มใน Module 2; ห้ามทำ middleware หรือ cookie/session จำลอง
- API ต้องเป็นเจ้าของข้อมูลและ event audit; Next.js ห้ามเชื่อม PostgreSQL โดยตรง
- Audit ที่สร้างแล้วห้ามมีเส้นทางแก้ไขหรือลบ ทั้งจาก application และ direct SQL
- endpoint เขียนข้อมูลมีเพียง technical probe ที่เปิดได้เฉพาะ environment `Development` และ `Testing` เพื่อพิสูจน์ foundation; `Production` ต้องตอบ 404
- ผู้ใช้งานภายนอกเห็น API ผ่าน hostname เดียวกับเว็บที่ `/api/*`; ห้ามเปิด CORS เป็นทางออกแทน reverse proxy
- Next.js development ใช้ port 4000 และ `next start` ใช้ port 4001 ตามคำตัดสินที่อนุมัติแล้ว; ASP.NET API ใช้ port 5080 ภายในเครื่อง

## จุดทบทวนก่อนเริ่มลงมือ

1. เครื่องปัจจุบันยังไม่มี `dotnet` และ `docker`; ผู้ดำเนินการต้องขออนุญาตติดตั้งหรือจัด runtime ที่ใช้ได้ก่อน Task 1
2. ใช้ .NET SDK 10.0.401 และแพ็กเกจเวอร์ชันที่ล็อกในแผนนี้เพื่อให้ build ทำซ้ำได้
3. เริ่มแต่ละ task ด้วย test ที่ล้มเหลวตามที่ระบุ แล้วเขียน implementation ขั้นต่ำเพื่อให้ผ่าน
4. รันคำสั่งยืนยันของ task นั้นก่อน commit; ไม่ข้ามไป task ถัดไปเมื่อฐานตรวจยังไม่ผ่าน

## โครงสร้างไฟล์ปลายทาง

```text
backend/
  TPR10.sln
  Directory.Build.props
  src/TPR10.Api/
  tests/TPR10.Api.IntegrationTests/
  docker-compose.foundation.yml
  .env.foundation.example
infra/nginx/
  default.conf.template
docs/
  architecture/module-1-exit-gate.md
  runbooks/module-1-foundation.md
tests/
  next-config.test.mjs
app/portal/
components/Navbar.tsx
components/Footer.tsx
next.config.js
package.json
.gitignore
.config/dotnet-tools.json
```

## Task 1: เตรียม solution, runtime contract และการตรวจสอบเครื่องมือ

**Files:**

- Create: `global.json`
- Create: `backend/TPR10.sln`
- Create: `backend/Directory.Build.props`
- Create: `.config/dotnet-tools.json`
- Create: `backend/src/TPR10.Api/TPR10.Api.csproj`
- Create: `backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj`
- Modify: `.gitignore`
- Modify: `README.md`

**Step 1: เขียน preflight check ที่ต้องล้มเหลวก่อนมี solution**

จาก root ของ repository รัน:

```bash
dotnet --info
docker version --format '{{.Server.Version}}'
test -f backend/TPR10.sln
```

ก่อนเตรียมเครื่อง คำสั่งทั้งสามบรรทัดจะล้มเหลวใน workspace ปัจจุบัน; เก็บผลไว้ในบันทึกงานและหยุดรอการอนุญาตติดตั้ง runtime แทนการเดา path หรือดาวน์โหลดเอง

**Step 2: กำหนดเวอร์ชันที่ใช้ซ้ำได้**

สร้าง `global.json`:

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestPatch",
    "allowPrerelease": false
  }
}
```

สร้าง `backend/Directory.Build.props` ให้ทุก project ใช้ `net10.0`, nullable enabled, implicit usings enabled, `TreatWarningsAsErrors=true` และ `AnalysisLevel=latest`

สร้าง `.config/dotnet-tools.json` เพื่อ pin `dotnet-ef` ที่ `10.0.12`

**Step 3: สร้าง solution และ project ขั้นต่ำ**

รันคำสั่งต่อไปนี้จาก root:

```bash
dotnet new sln --name TPR10 --output backend
dotnet new webapi --name TPR10.Api --output backend/src/TPR10.Api --framework net10.0 --use-controllers false
dotnet new xunit --name TPR10.Api.IntegrationTests --output backend/tests/TPR10.Api.IntegrationTests --framework net10.0
dotnet sln backend/TPR10.sln add backend/src/TPR10.Api/TPR10.Api.csproj
dotnet sln backend/TPR10.sln add backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj
dotnet add backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj reference backend/src/TPR10.Api/TPR10.Api.csproj
dotnet tool restore
```

เพิ่ม package แบบ version lock:

```bash
dotnet add backend/src/TPR10.Api/TPR10.Api.csproj package Npgsql.EntityFrameworkCore.PostgreSQL --version 10.0.3
dotnet add backend/src/TPR10.Api/TPR10.Api.csproj package Microsoft.EntityFrameworkCore.Design --version 10.0.12
dotnet add backend/src/TPR10.Api/TPR10.Api.csproj package Microsoft.AspNetCore.OpenApi --version 10.0.12
dotnet add backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj package Microsoft.AspNetCore.Mvc.Testing --version 10.0.12
dotnet add backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj package Testcontainers.PostgreSql --version 4.15.0
dotnet add backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj package Npgsql --version 10.0.3
```

ลบตัวอย่าง WeatherForecast ที่ template สร้างทั้งหมดเพื่อไม่ให้กลายเป็น API ที่ไม่อยู่ใน scope

**Step 4: กำหนด ignore และคู่มือเริ่มต้น**

เพิ่ม `backend/**/bin/`, `backend/**/obj/`, `backend/.env.foundation` และไฟล์ certificate สำหรับ local development ลง `.gitignore` โดยคงกฎ ignore เดิมไว้ครบ

ปรับ README เป็นภาษาไทยให้แยกคำสั่งชัดเจน:

```bash
npm run dev
npm run build
npm run start
dotnet run --project backend/src/TPR10.Api --urls http://127.0.0.1:5080
```

ระบุว่า `npm run dev` ฟังที่ 4000, `npm run start` ฟังที่ 4001 และ API ไม่ใช่ public port ใน production

**Step 5: ตรวจให้ผ่าน**

```bash
dotnet restore backend/TPR10.sln
dotnet build backend/TPR10.sln --configuration Release --no-restore
git check-ignore -q backend/src/TPR10.Api/bin/Release/net10.0/TPR10.Api.dll
```

ผลที่คาดหวัง: build สำเร็จ และไฟล์ build ถูก ignore

**Step 6: Commit**

```bash
git add global.json .config backend .gitignore README.md
git commit -m "chore: scaffold module 1 foundation solution"
```

## Task 2: สร้าง API host, liveness และ OpenAPI contract

**Files:**

- Modify: `backend/src/TPR10.Api/Program.cs`
- Create: `backend/src/TPR10.Api/Properties/AssemblyInfo.cs`
- Create: `backend/tests/TPR10.Api.IntegrationTests/HealthAndOpenApiTests.cs`
- Create: `backend/tests/TPR10.Api.IntegrationTests/ApiFactory.cs`

**Step 1: เขียน integration tests ที่ล้มเหลว**

สร้าง `HealthAndOpenApiTests` ด้วย `WebApplicationFactory<Program>` แล้วกำหนด assertion เหล่านี้:

- `GET /api/health/live` ตอบ 200, `application/json`, และ body มี `status: "live"`
- `GET /api/openapi/v1.json` ตอบ 200 และ body มี `openapi`
- `GET /health` ตอบ 404 เพื่อบังคับ API namespace เดียว

เพิ่ม `public partial class Program { }` ใน `AssemblyInfo.cs` เพื่อให้ test host อ้างอิง entry point ได้

**Step 2: รัน test เพื่อยืนยันว่าแดง**

```bash
dotnet test backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj --filter FullyQualifiedName~HealthAndOpenApiTests
```

ผลที่คาดหวัง: ล้มเหลวเพราะยังไม่มี route

**Step 3: เขียน API host ขั้นต่ำ**

ใน `Program.cs`:

- เรียก `AddProblemDetails()`, `AddOpenApi()` และ `AddHealthChecks()`
- ใช้ `UseExceptionHandler()` เมื่อไม่ใช่ `Development`
- map `GET /api/health/live` ให้ตอบ object คงที่ `{ status = "live" }`
- map OpenAPI ที่ `/api/openapi/{documentName}.json`
- ไม่ map controller, Swagger UI หรือ endpoint ธุรกิจ

แนวโค้ดของ liveness endpoint:

```csharp
app.MapGet("/api/health/live", () =>
    Results.Ok(new { status = "live" }))
    .WithName("ApiLiveness")
    .ExcludeFromDescription();
```

**Step 4: รัน test ให้ผ่าน**

```bash
dotnet test backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj --filter FullyQualifiedName~HealthAndOpenApiTests
```

ผลที่คาดหวัง: ทั้งสาม assertion ผ่าน

**Step 5: Commit**

```bash
git add backend/src/TPR10.Api backend/tests/TPR10.Api.IntegrationTests
git commit -m "feat: add module 1 api health foundation"
```

## Task 3: สร้าง PostgreSQL schema, migration, readiness และฐานพิสูจน์ audit immutable

**Files:**

- Create: `backend/src/TPR10.Api/Data/Tpr10DbContext.cs`
- Create: `backend/src/TPR10.Api/Data/Entities/TechnicalProbe.cs`
- Create: `backend/src/TPR10.Api/Data/Entities/AuditEvent.cs`
- Create: `backend/src/TPR10.Api/Data/Entities/AuditEventMetadata.cs`
- Create: `backend/src/TPR10.Api/Data/Migrations/*_InitialFoundation.cs`
- Create: `backend/src/TPR10.Api/Data/Migrations/*_InitialFoundation.Designer.cs`
- Create: `backend/src/TPR10.Api/Data/Migrations/Tpr10DbContextModelSnapshot.cs`
- Create: `backend/src/TPR10.Api/Health/DatabaseReadinessHealthCheck.cs`
- Modify: `backend/src/TPR10.Api/Program.cs`
- Create: `backend/tests/TPR10.Api.IntegrationTests/PostgresFixture.cs`
- Create: `backend/tests/TPR10.Api.IntegrationTests/DatabaseFoundationTests.cs`
- Modify: `backend/tests/TPR10.Api.IntegrationTests/ApiFactory.cs`
- Create: `backend/docker-compose.foundation.yml`
- Create: `backend/.env.foundation.example`
- Create: `ops/migrations/apply-local.sh`
- Create: `ops/migrations/rollback-local.sh`

**Step 1: เขียน test database ที่ล้มเหลว**

สร้าง `PostgresFixture` ด้วย `PostgreSqlContainer` และให้ `ApiFactory` เปลี่ยน connection string ของ `Tpr10DbContext` ไปยัง container นั้น จากนั้นเขียน `DatabaseFoundationTests`:

- apply migration กับ database ว่างแล้วตรวจว่ามีตาราง `technical_probes`, `audit_events`, `audit_event_metadata`
- เมื่อ PostgreSQL พร้อม `GET /api/health/ready` ตอบ 200 และ `{ status: "ready" }`
- เมื่อ factory ใช้ connection string ที่ต่อไม่ได้ readiness ตอบ 503 แต่ `/api/health/live` ยังตอบ 200
- insert audit row ผ่าน context แล้วสั่ง `UPDATE audit_events` และ `DELETE FROM audit_events` ผ่าน Npgsql; ทั้งสองคำสั่งต้องถูก PostgreSQL ปฏิเสธ

รัน:

```bash
dotnet test backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj --filter FullyQualifiedName~DatabaseFoundationTests
```

ผลที่คาดหวัง: ล้มเหลวก่อนมี DbContext, migration และ health check

**Step 2: กำหนด model ที่เป็น technical foundation เท่านั้น**

สร้างตารางและ mapping ตามนี้:

| ตาราง | คอลัมน์หลัก | เหตุผล |
| --- | --- | --- |
| `technical_probes` | `id uuid`, `note varchar(500)`, `created_at_utc timestamptz`, `correlation_id varchar(36)` | ข้อมูลทดสอบที่ควบคุมได้ |
| `audit_events` | `id uuid`, `event_type varchar(120)`, `occurred_at_utc timestamptz`, `correlation_id varchar(36)`, `actor_id uuid null`, `workspace_id uuid null`, `project_id uuid null`, `site_id uuid null`, `target_id uuid null` | ร่องรอย audit และ reserved identifiers สำหรับ Module ถัดไป |
| `audit_event_metadata` | `id uuid`, `audit_event_id uuid`, `key varchar(100)`, `value varchar(1000)` | metadata ที่ตรวจสอบได้โดยไม่ต้องเปลี่ยน schema ทุก event |

กำหนด primary key เป็น UUID, index `audit_events(correlation_id)`, index `audit_events(occurred_at_utc)`, unique index `audit_event_metadata(audit_event_id, key)` และ foreign key จาก metadata ไป audit event

ห้ามสร้าง foreign key ไปยังผู้ใช้, workspace, project หรือ site ใน Module 1; UUID nullable ใน audit เป็นเพียง schema reserve ไม่ใช่โมเดลสิทธิ์

**Step 3: สร้าง migration และ immutability trigger**

ใช้คำสั่งนี้แล้วแก้ migration ที่สร้างขึ้นให้เพิ่ม PostgreSQL function และ trigger สำหรับทั้ง `audit_events` กับ `audit_event_metadata`:

```bash
dotnet ef migrations add InitialFoundation \
  --project backend/src/TPR10.Api \
  --startup-project backend/src/TPR10.Api \
  --output-dir Data/Migrations
```

```sql
CREATE FUNCTION prevent_audit_mutation() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'audit records are immutable';
END;
$$ LANGUAGE plpgsql;
```

สร้าง trigger แบบ `BEFORE UPDATE OR DELETE` ที่เรียก function นี้กับสองตาราง และใน `Down` ต้อง drop trigger และ function ก่อน drop table เสมอ

**Step 4: เพิ่ม readiness check**

สร้าง `DatabaseReadinessHealthCheck` ที่เรียก `Database.CanConnectAsync(cancellationToken)` ของ `Tpr10DbContext`; map ที่ `GET /api/health/ready` พร้อม response JSON:

```json
{ "status": "ready" }
```

เมื่อ database ติดต่อไม่ได้ endpoint นี้ต้องตอบ 503 และไม่เปิดเผย connection string หรือ exception detail

**Step 5: สร้าง local database contract**

ตั้ง `backend/docker-compose.foundation.yml` ให้ใช้ image `postgres:17.11-alpine3.24`, service name `postgres`, host port `54329`, database `tpr10`, user `tpr10_app` และอ่านรหัสผ่านจาก `backend/.env.foundation` ที่ถูก ignore

ให้ `backend/.env.foundation.example` มีชื่อ variable เท่านั้นและไม่มี secret:

```dotenv
POSTGRES_PASSWORD=change-me-for-local-only
TPR10_CONNECTION_STRING=Host=127.0.0.1;Port=54329;Database=tpr10;Username=tpr10_app;Password=change-me-for-local-only
```

สคริปต์ `ops/migrations/apply-local.sh` ต้องตรวจว่า `TPR10_CONNECTION_STRING` ถูกกำหนดก่อนเรียก `dotnet ef database update`; `rollback-local.sh` ต้องรับ migration เป้าหมายเป็น argument บังคับและปฏิเสธ string ว่าง

**Step 6: รัน test ให้ผ่าน**

```bash
dotnet test backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj --filter FullyQualifiedName~DatabaseFoundationTests
docker compose --env-file backend/.env.foundation --file backend/docker-compose.foundation.yml config
```

ผลที่คาดหวัง: schema, readiness และ immutable audit test ผ่าน; compose config parse ได้โดยไม่พิมพ์ secret

**Step 7: Commit**

```bash
git add backend ops
git commit -m "feat: add postgres foundation and immutable audit schema"
```

## Task 4: สร้าง correlation context และ controlled technical mutation ที่เขียน audit แบบ atomic

**Files:**

- Create: `backend/src/TPR10.Api/Correlation/ICorrelationContext.cs`
- Create: `backend/src/TPR10.Api/Correlation/CorrelationContext.cs`
- Create: `backend/src/TPR10.Api/Correlation/CorrelationIdMiddleware.cs`
- Create: `backend/src/TPR10.Api/Auditing/IAuditEventWriter.cs`
- Create: `backend/src/TPR10.Api/Auditing/AuditEventWriter.cs`
- Create: `backend/src/TPR10.Api/TechnicalProbes/CreateTechnicalProbeRequest.cs`
- Create: `backend/src/TPR10.Api/TechnicalProbes/TechnicalProbeEndpoints.cs`
- Modify: `backend/src/TPR10.Api/Program.cs`
- Create: `backend/tests/TPR10.Api.IntegrationTests/CorrelationAndAuditTests.cs`
- Create: `backend/tests/TPR10.Api.IntegrationTests/Support/FailNextAuditInsertInterceptor.cs`

**Step 1: เขียน tests ที่ล้มเหลว**

เพิ่ม `CorrelationAndAuditTests` ให้ครอบคลุมพฤติกรรมต่อไปนี้:

- ส่ง `X-Correlation-ID` เป็น UUID ที่ถูกต้องไปยัง `POST /api/v1/system/technical-probes`; response ต้องสะท้อน UUID เดิมในรูปแบบ lowercase `D` และทั้ง technical probe กับ audit event ต้องเก็บค่าเดียวกัน
- ส่ง `X-Correlation-ID: not-a-guid`; response ต้องมี UUID ใหม่ที่ parse ได้ และห้ามสะท้อนค่าที่ไม่ถูกต้องกลับ
- ไม่ส่ง header; response ต้องมี UUID ใหม่ที่ parse ได้
- ใน environment `Testing` ส่ง `{ "note": "module-1-proof" }`; response ตอบ 201 พร้อม id, note, createdAtUtc และ correlationId
- ใน environment `Production` route `/api/v1/system/technical-probes` ต้องตอบ 404
- เปิด `FailNextAuditInsertInterceptor` เพื่อให้ insert audit ล้มเหลว; endpoint ต้องตอบ Problem Details และ transaction ต้องไม่เหลือทั้ง probe และ audit row

ก่อนมี middleware และ endpoint ให้รัน:

```bash
dotnet test backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj --filter FullyQualifiedName~CorrelationAndAuditTests
```

ผลที่คาดหวัง: tests ล้มเหลวเพราะ route และ correlation contract ยังไม่มี

**Step 2: เขียน correlation context ต่อ request**

สร้าง scoped `ICorrelationContext` ที่ expose `Guid CorrelationId` และ implementation ที่ตั้งค่าได้เพียงครั้งเดียวต่อ request

ให้ `CorrelationIdMiddleware` ทำงานหลัง `UseForwardedHeaders()` และก่อน exception handler กับ endpoint mapping:

1. อ่าน header `X-Correlation-ID`
2. ใช้ `Guid.TryParse` ตรวจค่า
3. ถ้า parse สำเร็จ ให้ normalize ด้วย `ToString("D")`; ถ้าไม่สำเร็จหรือไม่มี header ให้ `Guid.NewGuid()`
4. บันทึกค่าใน scoped context
5. ใช้ `Response.OnStarting` เติม `X-Correlation-ID` ใน response ทุกครั้ง รวมถึง response ที่เป็น error

ห้ามใช้ header เป็น string ดิบใน query, log property หรือ response

**Step 3: เขียน audit writer และ transaction boundary**

กำหนด API ของ `IAuditEventWriter`:

```csharp
Task WriteAsync(
    string eventType,
    Guid targetId,
    IReadOnlyDictionary<string, string> metadata,
    CancellationToken cancellationToken);
```

`AuditEventWriter` ต้องอ่าน correlation ID จาก context, สร้าง `AuditEvent` ด้วย `eventType = "technical.probe.created"`, `targetId` ของ probe และ metadata อย่างน้อย `source = "module-1-controlled-endpoint"`

ใน endpoint ให้เปิด explicit transaction ผ่าน `await db.Database.BeginTransactionAsync(cancellationToken)` แล้วปฏิบัติตามลำดับนี้:

1. validate `note` ว่าไม่ว่างหลัง trim และยาวไม่เกิน 500; ค่าไม่ผ่านตอบ 400 Problem Details
2. เพิ่ม `TechnicalProbe` ที่มี `CreatedAtUtc = TimeProvider.System.GetUtcNow()`
3. บันทึก probe เพื่อได้หลักฐานว่า insert ทำได้
4. เรียก `IAuditEventWriter.WriteAsync(...)`
5. `SaveChangesAsync` สำหรับ audit และ metadata
6. commit transaction
7. ตอบ 201 พร้อม `Location: /api/v1/system/technical-probes/{id}`

exception ในขั้นตอนใดขั้นตอนหนึ่งต้อง rollback transaction แล้วส่ง Problem Details; ห้าม catch exception แล้วตอบ success

**Step 4: จำกัด endpoint ตาม environment**

สร้าง `MapTechnicalProbeEndpoints` และเรียกเฉพาะเมื่อ:

```csharp
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    app.MapTechnicalProbeEndpoints();
}
```

อย่า map route เปล่าหรือ route ที่ตอบ message แทนใน Production เพราะ contract ที่กำหนดคือ 404

**Step 5: รัน tests ให้ผ่าน**

```bash
dotnet test backend/tests/TPR10.Api.IntegrationTests/TPR10.Api.IntegrationTests.csproj --filter FullyQualifiedName~CorrelationAndAuditTests
dotnet test backend/TPR10.sln --configuration Release
```

ผลที่คาดหวัง: ทุกกรณี correlation ผ่าน, audit/probe ใช้ UUID เดียวกัน และ test ที่บังคับ audit failure ยืนยันว่าไม่มีข้อมูลค้าง

**Step 6: Commit**

```bash
git add backend
git commit -m "feat: add correlated immutable audit proof"
```

## Task 5: เชื่อม Next.js ไป API แบบ same-origin และเพิ่มทางเข้าพื้นที่ภายใน

**Files:**

- Modify: `next.config.js`
- Create: `tests/next-config.test.mjs`
- Modify: `package.json`
- Create: `app/portal/layout.tsx`
- Create: `app/portal/page.tsx`
- Modify: `components/Navbar.tsx`
- Modify: `components/Footer.tsx`
- Create: `.env.example`

**Step 1: เขียน configuration tests ที่ล้มเหลว**

สร้าง `tests/next-config.test.mjs` โดยใช้ Node built-in test runner และ reload `next.config.js` ต่อ case:

- เมื่อ `TPR10_API_ORIGIN=http://127.0.0.1:5080`, `rewrites()` คืน rule จาก `/api/:path*` ไป `http://127.0.0.1:5080/api/:path*`
- เมื่อ variable ว่าง, `rewrites()` คืน array ว่าง
- เมื่อ origin มี scheme อื่น, path prefix, query string หรือ fragment เช่น `ftp://host`, `http://host/base`, `http://host?x=1`, config ต้อง throw Error ที่บอกชื่อ variable

เพิ่ม script:

```json
{
  "scripts": {
    "test:config": "node --test tests/next-config.test.mjs"
  }
}
```

รันก่อนปรับ config:

```bash
npm run test:config
```

ผลที่คาดหวัง: fail เพราะ rewrite validation ยังไม่มี

**Step 2: เขียน rewrite ที่ตรวจ origin อย่างเข้มงวด**

ให้ `next.config.js` อ่าน `process.env.TPR10_API_ORIGIN` ครั้งเดียวตอนโหลด config และใช้ `new URL(...)` ตรวจเงื่อนไข:

- protocol ต้องเป็น `http:` หรือ `https:`
- pathname ต้องเป็น `/`
- `search` และ `hash` ต้องว่าง
- เมื่อผ่าน ให้ตัด trailing slash ออกก่อนต่อ `/api/:path*`

ค่าไม่มี variable ใช้สำหรับ UI-only development เท่านั้น จึงคืน rewrite ว่าง; เอกสาร runbook ต้องสั่ง export variable เมื่อทดสอบ API ผ่าน Next.js

เพิ่ม `.env.example`:

```dotenv
TPR10_API_ORIGIN=http://127.0.0.1:5080
```

ห้ามใส่ connection string, password หรือ API secret ในไฟล์นี้

**Step 3: สร้าง `/portal` ที่ไม่แอบทำ authentication**

สร้าง server component แบบ static:

- title: `TPR10 Portal`
- heading ภาษาไทย: `ระบบปฏิบัติการภายใน`
- ข้อความ: `การเข้าสู่ระบบและสิทธิ์ใช้งานจะเปิดใช้ใน Module 2`
- link กลับ `/`

`app/portal/layout.tsx` ต้องกำหนด metadata เฉพาะ portal และใช้ semantic `main`; ห้ามเรียก API, อ่าน cookie หรือ redirect

**Step 4: เพิ่มจุดเชื่อมจาก Landing Page**

ใน `Navbar.tsx` เพิ่ม secondary link `เข้าสู่ระบบพนักงาน` ไป `/portal` ทั้ง desktop และ mobile menu โดยรักษา CTA หลัก `ติดต่อเรา` ไว้

ใน `Footer.tsx` เพิ่ม link ข้อความเดียวกันไป `/portal`; link นี้ต้องเป็น secondary navigation ไม่ใช่ CTA หลัก

**Step 5: รัน tests และ production build**

```bash
npm run test:config
npm run lint
npm run build
npm run start
```

เมื่อ process start แล้วให้ตรวจจากอีก terminal:

```bash
curl --fail --silent http://127.0.0.1:4001/portal
```

ผลที่คาดหวัง: config tests ผ่าน, build สำเร็จ และ `/portal` ตอบ 200 โดยไม่มี route auth

**Step 6: Commit**

```bash
git add next.config.js package.json tests app/portal components/Navbar.tsx components/Footer.tsx .env.example
git commit -m "feat: add same-origin api boundary and portal entry"
```

## Task 6: เพิ่ม reverse proxy contract และ runbook การรัน/ย้าย schema ที่ปลอดภัย

**Files:**

- Create: `infra/nginx/default.conf.template`
- Create: `infra/nginx/render-config.sh`
- Modify: `backend/src/TPR10.Api/Program.cs`
- Create: `docs/runbooks/module-1-foundation.md`
- Modify: `README.md`

**Step 1: เขียน static proxy checks ที่ล้มเหลวก่อนมี config**

สร้าง shell assertions ใน runbook และให้ผู้ดำเนินการรัน:

```bash
test -f infra/nginx/default.conf.template
rg -n 'location /api/|proxy_pass|X-Forwarded-For|X-Forwarded-Proto' infra/nginx/default.conf.template
```

ก่อนสร้าง template คำสั่งแรกต้องล้มเหลว

**Step 2: สร้าง Nginx template แบบ same-origin**

สร้าง `infra/nginx/default.conf.template` โดยใช้ environment substitution ก่อน start Nginx และรองรับตัวแปร:

```text
TPR10_WEB_UPSTREAM=127.0.0.1:4001
TPR10_API_UPSTREAM=127.0.0.1:5080
```

config ที่ render แล้วต้องมีพฤติกรรมเหล่านี้:

- `location = /api` redirect 308 ไป `/api/`
- `location /api/` proxy ไป `http://${TPR10_API_UPSTREAM}`
- `location /` proxy ไป `http://${TPR10_WEB_UPSTREAM}`
- ส่ง `Host`, `X-Real-IP`, `X-Forwarded-For`, `X-Forwarded-Proto` และ `X-Forwarded-Host` ให้ upstream
- ไม่เพิ่ม `Access-Control-Allow-Origin` หรือ CORS header ใด
- API upstream และ web upstream ฟังเฉพาะ loopback; public listener เป็นหน้าที่ของ Nginx

`render-config.sh` ต้องตรวจว่าตัวแปรทั้งสองเป็น `host:port` ที่ไม่มี slash, query หรือ whitespace ก่อนใช้ `envsubst` สร้างไฟล์ render ใน temporary path ที่ caller ระบุ ห้ามเขียนทับ template ต้นฉบับ

**Step 3: จำกัด forwarded headers ใน API**

ก่อน middleware อื่นที่ใช้ scheme/host ให้ตั้ง `ForwardedHeadersOptions` ใน `Program.cs`:

- เปิดเฉพาะ `XForwardedFor`, `XForwardedHost`, `XForwardedProto`
- clear ค่า default แล้ว trust เฉพาะ IPv4 loopback และ IPv6 loopback
- เรียก `UseForwardedHeaders()`

deployment ที่จะใช้ reverse proxy คนละเครื่องหรือคนละ container ต้องมี change แยกต่างหาก ซึ่งระบุ IP/CIDR ของ proxy แบบ allow-list พร้อม test; Module 1 ห้ามตั้ง `KnownNetworks` กว้างเพื่อแก้ปัญหาเฉพาะหน้า

**Step 4: เขียน runbook ภาษาไทยที่ปฏิบัติได้**

สร้าง `docs/runbooks/module-1-foundation.md` ตามลำดับนี้:

1. คัดลอก `backend/.env.foundation.example` เป็น `backend/.env.foundation` และเปลี่ยนรหัสผ่านเฉพาะ local
2. สตาร์ต PostgreSQL: `docker compose --env-file backend/.env.foundation --file backend/docker-compose.foundation.yml up -d postgres`
3. export `TPR10_CONNECTION_STRING` จากไฟล์ local โดยไม่ paste ค่า secret ลง console log
4. apply migration: `dotnet ef database update --project backend/src/TPR10.Api --startup-project backend/src/TPR10.Api`
5. รัน API ที่ `127.0.0.1:5080`
6. รัน Next development ที่ 4000 พร้อม `TPR10_API_ORIGIN=http://127.0.0.1:5080`
7. ตรวจ `curl --fail http://127.0.0.1:4000/api/health/live` และ `curl --fail http://127.0.0.1:4000/portal`
8. build และรัน Next production process ที่ 4001; Nginx รับ public traffic แล้วส่งต่อไป 4001 และ 5080 ตาม template

หัวข้อ rollback ต้องจำกัดชัดเจน:

- ใช้ `dotnet ef database update <target-migration>` ได้เฉพาะ database local/test ที่ระบุชื่อและ port ชัดเจน
- ก่อน rollback ต้อง verify hostname, database name และ migration ปัจจุบัน; หยุดเมื่อค่าใดค่าหนึ่งไม่ตรง runbook
- production migration ต้องมี backup ที่ตรวจ restore ได้, approved SQL script, maintenance window และผู้รับผิดชอบ; ห้ามรัน rollback script อัตโนมัติบน production

**Step 5: validate syntax และเส้นทาง proxy**

render config ด้วย upstream loopback:

```bash
TPR10_WEB_UPSTREAM=127.0.0.1:4001 TPR10_API_UPSTREAM=127.0.0.1:5080 \
  infra/nginx/render-config.sh /private/tmp/tpr10-nginx.conf
docker run --rm -v /private/tmp/tpr10-nginx.conf:/etc/nginx/conf.d/default.conf:ro nginx:1.28-alpine nginx -t
```

เมื่อ Web, API และ Nginx ทำงานจริงบน host เดียวกัน ให้ตรวจ:

```bash
curl --fail --silent http://127.0.0.1/api/health/live
curl --fail --silent http://127.0.0.1/portal
```

ผลที่คาดหวัง: Nginx syntax ผ่าน, `/api/health/live` มาจาก API และ `/portal` มาจาก Next.js โดย browser ไม่ต้องเรียก hostname แยก

**Step 6: Commit**

```bash
git add infra backend/src/TPR10.Api/Program.cs docs/runbooks README.md
git commit -m "docs: add module 1 proxy and migration runbook"
```

## Task 7: รัน exit gate ของ Module 1 และบันทึกหลักฐานก่อนเปิด Module 2

**Files:**

- Create: `docs/architecture/module-1-exit-gate.md`
- Modify: `README.md`

**Step 1: สร้าง exit-gate checklist ที่ยังไม่มีผลการรัน**

เอกสาร `docs/architecture/module-1-exit-gate.md` ต้องมีหัวข้อหลักฐานแบบ dated record สำหรับแต่ละรายการต่อไปนี้:

| Gate | หลักฐานที่ต้องแนบเมื่อรันจริง | เกณฑ์ผ่าน |
| --- | --- | --- |
| Web/API boundary | ผล `npm run test:config`, header และ status จาก curl ผ่าน port 4000/4001 | browser เรียก API ที่ `/api/*` |
| API contract | ผล integration test ของ health/OpenAPI | live 200, ready แยกจาก live, OpenAPI มี namespace `/api` |
| Database migration | ชื่อ migration, database local/test, ผล apply และ rollback ไป migration เป้าหมาย | schema ขึ้นและลงได้ใน database ที่แยกไว้ |
| Audit atomicity | ผล `CorrelationAndAuditTests` รวม fault injection | ไม่เกิด probe ค้างเมื่อ audit write ล้มเหลว |
| Audit immutability | ผล SQL update/delete ที่ถูก PostgreSQL ปฏิเสธ | audit event และ metadata เปลี่ยน/ลบไม่ได้ |
| Production boundary | ผล test environment `Production` | technical probe route ตอบ 404 |
| Scope discipline | ผล scan และ review diff | ไม่มี auth, RBAC, Workspace, Project, Site หรือ ScopeContext |

หัวเอกสารต้องระบุสถานะเริ่มต้นว่า `ยังไม่ผ่านจนกว่าจะมีผลคำสั่งจากการรันจริง` เพื่อป้องกันการตีความว่าแผนเท่ากับหลักฐาน

**Step 2: รันชุดตรวจรวม**

หลัง Task 1–6 ผ่านทั้งหมด รัน:

```bash
npm ci
npm run test:config
npm run lint
npm run build
dotnet test backend/TPR10.sln --configuration Release
docker compose --env-file backend/.env.foundation --file backend/docker-compose.foundation.yml config
git diff --check
```

จากนั้นรัน scan ขอบเขตเฉพาะ source ที่สร้างใหม่:

```bash
rg -n -i 'AddAuthentication|AddAuthorization|UseAuthentication|UseAuthorization|ScopeContext|Map.*(Login|Password|Mfa)|class (Workspace|Project|Site)\b|DbSet<(Workspace|Project|Site)>' \
  backend/src/TPR10.Api app/portal
```

ผลที่คาดหวังคือคำสั่ง exit 1 โดยไม่มีผลลัพธ์; ถ้า exit 0 ให้ตรวจทุกบรรทัดที่พบ และลบ route, entity หรือ middleware ที่ทำหน้าที่ด้าน auth/scope ก่อนถือว่า gate ผ่าน UUID reserve ใน audit event ใช้ได้และไม่เป็นเหตุให้ scan นี้ล้มเหลว

**Step 3: ตรวจ migration ขึ้น/ลงใน isolated database**

หลังยืนยันว่า Docker database คือ local/test instance ที่ชื่อ `tpr10` บน port `54329`:

```bash
dotnet ef database update --project backend/src/TPR10.Api --startup-project backend/src/TPR10.Api
dotnet ef database update 0 --project backend/src/TPR10.Api --startup-project backend/src/TPR10.Api
dotnet ef database update --project backend/src/TPR10.Api --startup-project backend/src/TPR10.Api
```

รอบสุดท้ายต้องรัน `DatabaseFoundationTests` และ `CorrelationAndAuditTests` ซ้ำเพื่อยืนยันว่าสภาพหลัง migration ใช้งานได้

**Step 4: บันทึกผลจริงและ review**

เพิ่ม command, เวลารัน, commit SHA, environment และผล pass/fail ลงตารางหลักฐานของ exit gate โดยไม่บันทึก connection string หรือรหัสผ่าน

ให้ reviewer ตรวจ diff ต่อไปนี้เป็นพิเศษ:

- `Program.cs`: order ของ correlation, forwarded headers และ exception handling
- migration: trigger อยู่ใน `Up` และถูกลบแบบย้อนลำดับใน `Down`
- technical probe endpoint: ไม่มีใน Production
- Next rewrite: origin ถูก validate และไม่มี cross-origin workaround
- Nginx: `/api/*` แยก upstream จาก web และไม่ใส่ CORS header

**Step 5: Commit**

```bash
git add docs/architecture README.md
git commit -m "docs: record module 1 exit gate"
```

## ลำดับการทำงานและเกณฑ์ส่งต่อ

```text
Task 1: Toolchain + solution
    ↓
Task 2: API liveness + OpenAPI
    ↓
Task 3: PostgreSQL + migration + readiness + audit immutable
    ↓
Task 4: Correlation + atomic controlled mutation
    ↓
Task 5: Next rewrite + /portal + Landing Page entry
    ↓
Task 6: Nginx + operational runbook
    ↓
Task 7: Exit gate evidence and review
```

เริ่ม Module 2 ได้เมื่อ Task 7 มีหลักฐานผ่านครบทุก gate เท่านั้น โดย Module 2 เป็นจุดเริ่มสำหรับ local identity, session, MFA และ RBAC; Module 1 ไม่สร้างร่องรอย implementation ของความสามารถเหล่านั้นนอกเหนือจาก UUID nullable ที่สงวนไว้ใน audit schema

## แหล่งอ้างอิงด้าน runtime และ dependency

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Npgsql Entity Framework Core PostgreSQL provider](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL)
- [Testcontainers PostgreSQL for .NET](https://www.nuget.org/packages/Testcontainers.PostgreSql)
- [Docker Official Image: PostgreSQL](https://hub.docker.com/_/postgres)

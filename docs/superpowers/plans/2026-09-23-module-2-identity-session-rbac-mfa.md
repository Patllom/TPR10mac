# แผนการพัฒนา Module 2: บัญชีผู้ใช้ Session สิทธิ์ และ MFA

> **สำหรับผู้พัฒนาแบบ agent:** ต้องใช้ `superpowers:executing-plans` ตามวิธี Native/inline ที่ผู้ใช้เลือกไว้ ดำเนินงานทีละ Task โดยใช้ checkbox (`- [ ]`) และ TDD; ให้ผู้ตรวจอิสระทำ Code Review ก่อนปิดงาน ไม่เปลี่ยนเป็นการแบ่งงานให้หลาย agent โดยไม่ได้รับคำขอ

**เป้าหมาย:** ผู้ใช้เข้าสู่ Portal จาก Landing Page ด้วยบัญชีภายในได้ โดย API เป็นผู้ตรวจ session, named permission, MFA และ CSRF จริง พร้อมหลักฐานปฏิเสธการเข้าถึงที่ไม่ถูกต้อง

**สถาปัตยกรรม:** เพิ่ม Identity module ภายใน ASP.NET Core modular monolith เดิม ใช้ PostgreSQL เดิม และคง Next.js เป็นหน้าเว็บ/API consumer เท่านั้น ผู้ใช้เข้า HTTPS origin เดียวผ่าน reverse proxy; ไม่เพิ่ม authentication server หรือ JWT ใน browser

**เทคโนโลยี:** Next.js 14/React 18/TypeScript, .NET 10, EF Core/Npgsql, PostgreSQL, xUnit/Testcontainers เดิม; เพิ่ม Argon2id และ TOTP ผ่าน library ที่ตรวจ dependency ก่อนติดตั้ง เพิ่ม Playwright สำหรับ browser acceptance

**ข้อกำหนดอ้างอิง:** [Architecture Baseline ที่อนุมัติแล้ว](../specs/2026-09-18-tpr10-module-0-architecture-baseline-design.md) หัวข้อ 8, 11, 18 และ 20

**สถานะ:** Tasks 1–6 ผ่าน implementation, review อิสระ และ Test/Build/Lint เมื่อ 2026-09-23 ดู [รายงาน Task 1](../../architecture/module-2-task-1-verification.md), [Task 2](../../architecture/module-2-task-2-verification.md), [Task 3](../../architecture/module-2-task-3-verification.md), [Task 4](../../architecture/module-2-task-4-verification.md), [Task 5](../../architecture/module-2-task-5-verification.md) และ [Task 6](../../architecture/module-2-task-6-verification.md) รวม backend259/Node23ผ่าน มี Minor ที่เปิดเผยในแต่ละรายงาน Tasks 7–9 ยังไม่เริ่ม จึงยังไม่ใช่หลักฐานผ่าน Security Exit Gate

## ข้อกำหนดร่วมทุก Task

- API เป็น session authority เพียงจุดเดียว; cookie `__Host-tpr10_session` มี `Secure`, `HttpOnly`, `Path=/`, `SameSite=Lax` และไม่มี `Domain`
- ทุก `POST`, `PUT`, `PATCH`, `DELETE` ต้องมี `X-CSRF-Token` และผ่าน Origin/Host allowlist รวม login, reset และ MFA; ห้ามยกเว้น endpoint เดิมโดยไม่ได้ทดสอบ
- Session token เก็บในฐานข้อมูลเป็น hash เท่านั้น ห้ามเก็บ token ใน localStorage, React state, URL หรือ log; CSRF token เก็บในหน่วยความจำหน้าเว็บได้
- MFA บังคับกับ role class system administration, approval, accounting และ finance-data access; named permission ไม่ผูกกับชื่อ role ใน business code
- ปิดบัญชี/reset password/ถอด role/ถอด assignment/admin sign-out ต้อง revoke session; assignment implementation อยู่ Module 3 แต่ Module 2 เตรียม interface เพิกถอนให้เรียก
- Audit เก็บ actor, acting role, scope, action, target type/id, outcome, correlation และเวลา; mutation กับ audit อยู่ transaction เดียวกัน ไม่มี password, reset/session token หรือ MFA secret ใน audit
- ไม่ทำ self-registration, AD/LDAP/SSO integration, business approval หรือ workspace/project/site authorization ในโมดูลนี้; ห้ามอ้างว่าป้องกัน cross-scope แล้ว
- เอกสารและข้อความสำหรับผู้ใช้เป็นภาษาไทย; identifier, endpoint และชื่อเครื่องมือคงภาษาเทคนิค
- Next dev คง `4000`, production คง `4001`; HTTPS proxy local เสนอ `4443` ซึ่งเป็นทางเข้าทดสอบ cookie ไม่ใช่การเปลี่ยนพอร์ต Next
- เริ่ม implementation ใน native managed worktree ใหม่จาก main; ไม่แก้ migration Module 1 ย้อนหลัง และไม่ใช้ฐานข้อมูล production ทดสอบ

## นโยบายเสนอเพื่ออนุมัติพร้อมแผน

ค่าต่อไปนี้เป็นข้อเสนอสำหรับพัฒนา/ทดสอบ ไม่ใช่นโยบาย production ที่ได้รับอนุมัติแล้ว Security owner ต้องลงนามก่อน Exit Gate Module 2

| เรื่อง | ค่าเริ่มต้นที่เสนอ |
| --- | --- |
| Session | idle 30 นาที, absolute 8 ชั่วโมง, ไม่เปิด remember-me; ตรวจ account/security-version ทุก request |
| Password | 15–128 Unicode scalar values, ไม่ trim/normalize password; username trim + FormKC + uppercase invariant และ unique index |
| Argon2id | memory 65536 KiB, iterations 3, parallelism 1, salt 16 bytes, output 32 bytes; benchmark ภายใต้ concurrency ก่อนรับโหลดจริง |
| Lockout | ผิด 5 ครั้งใน 15 นาทีพัก 15 นาที; จำกัดตาม account และ IP; ไม่เปิดเผยว่าบัญชีมีอยู่ |
| CSRF/pre-auth | อายุ 10 นาที, จำกัดจำนวน issuance ต่อ IP; signed token ผูก flow/session และ purpose; ทุก response เป็น no-store |
| Password reset | random 32 bytes, hash ใน DB, อายุ 15 นาที, ใช้ครั้งเดียว; ไม่ auto-login หลัง reset |
| MFA | TOTP 6 หลัก/30 วินาที ยอมรับ ±1 step, ห้ามใช้ timestep ซ้ำ; assurance อายุ 15 นาทีสำหรับ privileged action |
| Recovery | recovery code random 128-bit จำนวน 10 ชุด แสดงครั้งเดียว เก็บ hash และ consume atomically; recovery ไม่ยกระดับ MFA assurance อัตโนมัติ |
| Email reset | ทำ request/consume จริงกับ delivery interface และ sink เฉพาะ development/test; Gmail worker จริงต่อ Module 5 ห้ามอ้างส่ง email production สำเร็จ |

ก่อน deploy ต้องระบุ canonical HTTPS hostname, TLS certificate owner, exact allowlist, key-ring directory ที่ persistent และได้รับการป้องกัน, recovery operator และช่องทางยืนยันตัวบุคคล ไม่มีการใส่ secret จริงใน Git

## จุดเน้นการตรวจทาน 5 เรื่อง

1. Cookie เก่าหรือ role ถูกถอดระหว่างใช้งาน: request ถัดไปต้องถูกปฏิเสธ — Task 3/6
2. CSRF ข้าม session, Origin ปลอม หรือ forwarded host จากผู้ส่งที่ไม่เชื่อถือ: ไม่มี mutation — Task 2/3
3. Reset/recovery/TOTP ส่งพร้อมกันสองครั้ง: สำเร็จได้ครั้งเดียว — Task 5/7
4. บัญชี privileged ยังไม่ตั้ง MFA หรืออยู่สถานะบังคับเปลี่ยน password: เข้า business/test-protected route ไม่ได้ — Task 4/5/7
5. Next cache และ return URL: ไม่รั่ว session ข้ามผู้ใช้ ไม่ redirect ไปโดเมนภายนอก — Task 8

## โครงสร้างไฟล์และ interface กลาง

ทุก path ต่อไปนี้อ้างจาก repository root `backend/src/TPR10.Api` ย่อเป็น `API`, `backend/tests/TPR10.Api.IntegrationTests` ย่อเป็น `TEST` ในตารางเท่านั้น; Task ระบุ path เต็ม

| กลุ่มไฟล์ใหม่ | หน้าที่ |
| --- | --- |
| `API/Identity/IdentityContracts.cs`, `IdentityOptions.cs`, `IdentityRegistration.cs` | DTO, configuration validation, service/endpoint registration |
| `API/Identity/Data/IdentityEntities.cs`, `IdentityModelConfiguration.cs` | identity schema และ constraints ไม่รวม service logic |
| `API/Identity/Passwords/ArgonPasswordHasher.cs`, `LocalIdentityProvider.cs` | hash/verify และ local credential verification |
| `API/Identity/Sessions/SessionService.cs`, `SessionAuthenticationHandler.cs` | ออก/ตรวจ/revoke session และสร้าง principal |
| `API/Identity/Csrf/CsrfService.cs`, `CsrfMiddleware.cs` | pre-auth binding และ unsafe-request protection |
| `API/Identity/Authorization/PermissionHandler.cs`, `PermissionRequirement.cs` | named permission + MFA assurance |
| `API/Identity/Accounts/AccountEndpoints.cs`, `BootstrapCommand.cs` | account lifecycle และ admin แรกผ่านคำสั่งในเครื่อง |
| `API/Identity/Mfa/MfaService.cs`, `MfaEndpoints.cs` | enroll/challenge/recovery |
| `API/Identity/Reset/PasswordResetService.cs`, `ResetDelivery.cs` | reset และ delivery boundary |
| `API/Identity/AuthEndpoints.cs`, `IdentityOpenApiTransformer.cs` | auth routes และ OpenAPI security contract |
| `TEST/IdentityTestDriver.cs`, `ManualTimeProvider.cs` | HTTPS client, test-only seed และเวลาที่ควบคุมได้ |
| `lib/auth/server-session.ts`, `auth-client.ts`, `safe-return-path.ts` | Next session navigation, CSRF transport, redirect validation |
| `app/login/page.tsx`, `app/login/LoginForm.tsx`, `app/auth/mfa/page.tsx`, `app/auth/reset/page.tsx`, `app/auth/change-password/page.tsx` | หน้า authentication นอก protected Portal |
| `tests/e2e/identity.spec.ts`, `playwright.config.ts` | browser acceptance ผ่าน HTTPS proxy |

`IdentityContracts.cs` ต้องประกาศสัญญาต่อไปนี้ก่อนใช้งานใน Task ถัดไป (namespace `TPR10.Api.Identity`):

```csharp
public enum SessionStage { PasswordChangeRequired, MfaEnrollmentRequired, MfaChallengeRequired, Active }
public sealed record SessionView(Guid UserId, SessionStage Stage,
    string[] Permissions, DateTimeOffset? MfaVerifiedAtUtc);
public sealed record IssuedSession(string Token, SessionView View);
public sealed record PasswordCheck(Guid UserId, bool MustChangePassword);
public interface IPasswordHasher {
    Task<string> HashAsync(string password, CancellationToken ct);
    Task<bool> VerifyAsync(string password, string encoded, CancellationToken ct);
}
public interface IIdentityProvider {
    Task<PasswordCheck?> VerifyAsync(string username, string password, CancellationToken ct);
}
public interface ISessionService {
    Task<IssuedSession> IssueAsync(Guid userId, SessionStage stage, CancellationToken ct);
    Task<SessionView?> ValidateAsync(string token, CancellationToken ct);
    Task RevokeUserAsync(Guid userId, string reason, CancellationToken ct);
}
public interface IResetDelivery {
    Task EnqueueAsync(Guid requestId, string recipient, string protectedPayload, CancellationToken ct);
}
```

บริการ mutation เพิ่ม tracked changes ใน DbContext ของ request; endpoint/use-case เป็นเจ้าของ transaction และ `SaveChangesAsync` เพื่อให้ revoke, credential change และ audit commit พร้อมกัน ห้าม service เปิด transaction ซ้อนเอง

สัญญา test helper `IdentityTestDriver` เป็น `IAsyncDisposable`: `CreateAsync(string connectionString)`, property `HttpClient Client`, `SeedUserAsync(string username, string password, string[] permissions, bool requiresMfa = false, bool mustChangePassword = false): Task<Guid>`, `PostAsync(string path, object body): Task<HttpResponseMessage>` โดย helper GET csrf และส่ง Origin ก่อน POST, `LoginAsync(string username, string password): Task<HttpResponseMessage>`, `CountAsync(string table): Task<int>` ใช้ table allowlist เท่านั้น และ `Advance(TimeSpan delta): void` ผ่าน `ManualTimeProvider` ห้ามมี test seeding HTTP endpoint ใน production

## Task 1: Credentials และ identity schema ที่พิสูจน์ได้

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/IdentityContracts.cs`, `IdentityOptions.cs`, `IdentityRegistration.cs`, `Data/IdentityEntities.cs`, `Data/IdentityModelConfiguration.cs`, `Passwords/ArgonPasswordHasher.cs`, `Passwords/LocalIdentityProvider.cs`; แก้ `backend/src/TPR10.Api/Data/Tpr10DbContext.cs`, `backend/src/TPR10.Api/TPR10.Api.csproj`; สร้าง `backend/tests/TPR10.Api.IntegrationTests/PasswordTests.cs`, `IdentitySchemaTests.cs`

**รับ/ส่ง:** ใช้ `TimeProvider`/`Tpr10DbContext` เดิม; ส่ง `IPasswordHasher`, `IIdentityProvider` ตามสัญญาข้างต้น และ schema ให้ Task 2–7

- [x] เขียน test hash ก่อน implementation:

```csharp
[Fact]
public async Task Hash_is_salted_and_rejects_wrong_password() {
    IPasswordHasher hasher = new ArgonPasswordHasher();
    var a = await hasher.HashAsync("ทดสอบรหัสผ่าน-123456", default);
    var b = await hasher.HashAsync("ทดสอบรหัสผ่าน-123456", default);
    Assert.NotEqual(a, b);
    Assert.StartsWith("$argon2id$", a);
    Assert.True(await hasher.VerifyAsync("ทดสอบรหัสผ่าน-123456", a, default));
    Assert.False(await hasher.VerifyAsync("รหัสไม่ถูกต้อง", a, default));
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~PasswordTests` ต้องแดงเพราะยังไม่มี hasher
- [x] ตรวจ package license/advisories/compatibility จากแหล่งผู้ผลิต แล้ว pin exact resolved version พร้อม lock file; ใช้ `Konscious.Security.Cryptography.Argon2` เป็น candidate ไม่รับรองความปลอดภัยจากชื่อ package เพียงอย่างเดียว ใช้ salt ใหม่แต่ละครั้ง และ compare แบบ fixed-time:

```csharp
using var argon = new Konscious.Security.Cryptography.Argon2id(
    System.Text.Encoding.UTF8.GetBytes(password));
argon.Salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
argon.MemorySize = 65536;
argon.Iterations = 3;
argon.DegreeOfParallelism = 1;
var digest = await argon.GetBytesAsync(32);
```

- [x] ทำ encoded format `$argon2id$v=19$m=65536,t=3,p=1$<salt-base64>$<digest-base64>`; parse ต้องจำกัด algorithm/version/parameter และขนาดก่อนจัดสรร memory, malformed hash คืน false ไม่ 500; เพิ่ม test password Unicode, salt ต่างกัน, malformed format, oversized cost, username normalized ชนกัน
- [x] สร้างตาราง `users`, `local_credentials`, `external_identities`, `roles`, `permissions`, `role_permissions`, `user_roles`, `mfa_factors`, `sessions`, `password_reset_requests`, `pre_auth_flows`, `mfa_recovery_codes`, `identity_delivery_outbox`; GUID keys, UTC timestamps, unique normalized username และ provider/subject, composite unique role mappings, FK restrictive, indexes token hash/expiry; user มี `security_version` และ active; session มี stage/expiry/idle/MFA time/version; factor มี encrypted secret/last-used-step; token table มี consumed/revoked timestamp
- [x] สร้าง migration ด้วย `dotnet ef migrations add AddIdentityFoundation --project backend/src/TPR10.Api --startup-project backend/src/TPR10.Api --output-dir Data/Migrations`; เก็บไฟล์ timestamp ที่ EF สร้างและ snapshot โดยไม่แตะ migration เดิม ทดสอบ migrate ฐานว่างและ upgrade จาก Module 1, unique/FK และ audit trigger ยังคงทำงาน
- [x] รัน `dotnet test backend/TPR10.sln --filter 'FullyQualifiedName~PasswordTests|FullyQualifiedName~IdentitySchemaTests'` ให้ผ่าน แล้ว commit `feat: add identity schema and Argon2id credentials`

## Task 2: Pre-auth CSRF และ HTTPS transport

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/Csrf/CsrfService.cs`, `CsrfMiddleware.cs`, `AuthEndpoints.cs`; แก้ `backend/src/TPR10.Api/Program.cs`, `backend/tests/TPR10.Api.IntegrationTests/ApiFactory.cs`; สร้าง `backend/tests/TPR10.Api.IntegrationTests/IdentityTestDriver.cs`, `ManualTimeProvider.cs`, `CsrfTests.cs`, `infra/nginx/identity-local-https.conf.template`, `docs/runbooks/module-2-identity.md`

**รับ/ส่ง:** ใช้ schema Task 1; `CsrfService.IssueAsync(HttpContext, CancellationToken): Task<string>` และ `ValidateAsync(HttpContext, CancellationToken): Task<bool>`; `GET /api/v1/auth/csrf` คืน `{token}` พร้อม no-store และ pre-auth cookie `__Host-tpr10_preauth` ซึ่งมี security flags แบบ session

- [x] เขียน test ปฏิเสธก่อน mutation โดยตั้ง HTTPS base address และ Origin ที่อนุญาตใน ApiFactory:

```csharp
[Fact]
public async Task Login_without_csrf_is_forbidden() {
    await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
    var response = await driver.Client.PostAsJsonAsync("/api/v1/auth/login",
        new { username = "ไม่มีบัญชี", password = "ไม่ใช่รหัสจริง" });
    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal(0, await driver.CountAsync("sessions"));
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~CsrfTests` ต้องแดงก่อนติดตั้ง middleware
- [x] ใช้ ASP.NET Data Protection สร้าง token ตาม purpose แยกจาก secret encryption และ server record binding; validate expiry ด้วย TimeProvider, ใช้ exact origin tuple scheme/host/port และ configured host; ไม่เชื่อ arbitrary forwarded headers โดยรักษา trusted proxy config Module 1:

```csharp
if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
    || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)) {
    if (!await csrf.ValidateAsync(context, context.RequestAborted)) {
        await Results.Problem(statusCode: 403, type: "urn:tpr10:csrf-invalid",
            title: "คำขอไม่ผ่านการตรวจสอบความปลอดภัย").ExecuteAsync(context);
        return;
    }
}
await next(context);
```

- [x] เพิ่ม tests token หาย/แก้ไข/หมดอายุ, flow ผิด, Origin/Host ไม่อนุญาต, forwarded host จาก remote ไม่เชื่อถือ, GET ไม่มี side-effect ทางธุรกิจ; issuer GET สร้างได้เฉพาะ bounded pre-auth record พร้อม rate limit/expiry cleanup ไม่เพิ่ม cookie domain
- [x] จัด pipeline forwarded headers → correlation → exception handling → routing → rate limit → authentication → CSRF → authorization → endpoint; บันทึก denial ด้วย sanitized audit แยก transaction ไม่ให้ mutation เกิดก่อนตรวจ — ใน Task 2 ติดตั้งส่วนที่มีจริงและเว้น authentication/authorization ให้ Task 3/6 ตาม ruling ไม่สร้าง handler ปลอม
- [x] ทำ HTTPS proxy local 4443 ไป Next 4000 หรือ4001 และ API private origin; certificate/key อยู่นอก Git บันทึกวิธี trust local CA ไม่ใช้ `ignoreHTTPSErrors` เป็นผล acceptance; HTTP port ใช้ landing preview เท่านั้น
- [x] รัน CsrfTests และ ForwardedHeadersTests ให้ผ่าน พร้อมตรวจ cookie flags/ไม่มี Domain แล้ว commit `feat: enforce same-origin pre-auth CSRF`

## Task 3: Login, session และ logout

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/Sessions/SessionService.cs`, `SessionAuthenticationHandler.cs`; แก้ `backend/src/TPR10.Api/Identity/AuthEndpoints.cs`, `IdentityRegistration.cs`, `Csrf/CsrfService.cs`; สร้าง `backend/tests/TPR10.Api.IntegrationTests/SessionTests.cs`

**รับ/ส่ง:** ใช้ `IIdentityProvider`, ส่ง `ISessionService`; `POST /api/v1/auth/login` `{username,password}`, `GET /api/v1/auth/session` คืน `SessionView`, `POST /api/v1/auth/logout` คืน204; session stage serialize เป็นชื่อ string ไม่ใช่เลข

- [x] เขียน test การเพิกถอน:

```csharp
[Fact]
public async Task Logout_invalidates_the_old_cookie() {
    await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
    await driver.SeedUserAsync("staff", "รหัสทดสอบยาวพอ-123456", []);
    (await driver.LoginAsync("staff", "รหัสทดสอบยาวพอ-123456")).EnsureSuccessStatusCode();
    Assert.Equal(HttpStatusCode.OK, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
    Assert.Equal(HttpStatusCode.NoContent, (await driver.PostAsync("/api/v1/auth/logout", new {})).StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, (await driver.Client.GetAsync("/api/v1/auth/session")).StatusCode);
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~SessionTests` ให้แดง
- [x] ออก random token 32 bytes; token transport base64url, DB ใช้ SHA-256 hash; authentication handler ตรวจ session active, user active/version, idle/absolute expiry และอ่าน permission ปัจจุบัน ไม่ใช้ cached role claim เป็นแหล่งอำนาจ:

```csharp
var raw = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
var token = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(raw);
var tokenHash = System.Security.Cryptography.SHA256.HashData(raw);
```

- [x] หลัง login เปลี่ยน pre-auth binding เป็น session binding ยกเลิก flow เก่า; rotate token เมื่อ privilege/assurance เปลี่ยน; csrf เก่าต้องใช้ไม่ได้ กำหนด login fail เป็น401 generic, locked/rate limited เป็น429 generic พร้อม Retry-After; nonexistent user ใช้ dummy hash path ไม่เปิดเผย account existence
- [x] เพิ่ม tests idle/absolute expiry ด้วยเวลาจำลอง, malformed cookie, DB unavailable fail closed, token raw ไม่อยู่ DB/log/response body, session fixation, cross-session CSRF, logout replay cookie ด้วย client ใหม่, session endpoint no-store และ responses ไม่ cache
- [x] รัน SessionTests/CsrfTests ให้ผ่าน แล้ว commit `feat: add authoritative revocable sessions`

## Task 4: บัญชีผู้ใช้และผู้ดูแลเริ่มต้น

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/Accounts/AccountEndpoints.cs`, `BootstrapCommand.cs`; แก้ `backend/src/TPR10.Api/Program.cs`, `Identity/IdentityRegistration.cs`; สร้าง `backend/tests/TPR10.Api.IntegrationTests/AccountTests.cs`

**รับ/ส่ง:** bootstrap เป็น CLI branch ก่อน `app.Run` ใช้ `--bootstrap-admin` รับ username ผ่าน prompt และ password แบบไม่ echo ไม่มี default password; `POST /api/v1/users` และ `PATCH /api/v1/users/{id}` ใช้ permission `users:manage` + MFA เมื่อ Task 6 เปิด mapping; ก่อนนั้นไม่ map admin endpoints บน production

- [x] เขียน test ไม่มี self-registration:

```csharp
[Fact]
public async Task Public_registration_is_not_available() {
    await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
    var response = await driver.PostAsync("/api/v1/auth/register", new { username = "intruder" });
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal(0, await driver.CountAsync("users"));
}
```

- [x] เพิ่ม bootstrap test สอง process แข่งกันสร้าง admin ต้องสำเร็จหนึ่งครั้ง, password ไม่ปรากฏ stdout, บัญชีแรก stage `MfaEnrollmentRequired`; รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AccountTests` ให้แดงที่ bootstrap behavior (registration404 เดิมอาจผ่านอยู่แล้ว)
- [x] ทำ bootstrap transaction พร้อม PostgreSQL advisory transaction lock; หากมี user อยู่แล้วให้ปฏิเสธ ไม่ overwrite; seed named permissions/role classes ด้วย deterministic IDs ไม่ seed demo accounts:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", ct);
// ตรวจ users ว่างใน transaction ก่อนสร้าง user, credential, role mapping และ audit
```

- [x] สร้างบัญชีโดย admin ให้ must-change-password; update active=false ต้องเพิ่ม security_version และ revoke ใน transaction เดียว; ป้องกันปิด/ถอดผู้ดูแล active คนสุดท้ายด้วย lock; GET users pagination default25 max100 ไม่คืน credential/factor/token
- [x] ทดสอบ normalized username duplicate409, field validation400, disabled login401, bootstrap ซ้ำไม่เพิ่ม row และ account audit rollback; รัน AccountTests ผ่าน แล้ว commit `feat: add controlled account provisioning`

ผลส่งมอบ Task 4: [รายงานตรวจสอบและข้อค้าง](../../architecture/module-2-task-4-verification.md) — backend179/179, Node23/23, Build/Lint/HTTPS4000/4001ผ่านหลังแก้ review; API จัดการบัญชียังไม่เปิดจน Task 6

## Task 5: MFA enrollment, challenge และ recovery

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/Mfa/MfaService.cs`, `MfaEndpoints.cs`; แก้ `backend/src/TPR10.Api/Identity/AuthEndpoints.cs`, `IdentityRegistration.cs`, `backend/src/TPR10.Api/TPR10.Api.csproj`; สร้าง `backend/tests/TPR10.Api.IntegrationTests/MfaTests.cs`

**รับ/ส่ง:** `MfaService.VerifyTotpAsync(Guid userId, string code, CancellationToken ct): Task<bool>`; POST `/api/v1/auth/mfa/enroll`, `/mfa/confirm`, `/mfa/challenge`, `/mfa/recover` ผูก session/flow ที่พิสูจน์ password แล้ว; enroll ไม่ใช่ public anonymous flow

- [x] เขียน test จำกัด stage ก่อน MFA:

```csharp
[Fact]
public async Task Privileged_login_requires_mfa_enrollment() {
    await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
    await driver.SeedUserAsync("admin", "รหัสทดสอบยาวพอ-123456", ["users:manage"], requiresMfa: true);
    var response = await driver.LoginAsync("admin", "รหัสทดสอบยาวพอ-123456");
    var view = await response.Content.ReadFromJsonAsync<SessionView>();
    Assert.Equal(SessionStage.MfaEnrollmentRequired, view!.Stage);
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~MfaTests` ให้แดง
- [x] ใช้ `Otp.NET` หลังตรวจ/pin dependency; encrypt secret ด้วย Data Protection purpose เฉพาะและ persistent protected keyring; enroll คืน provisioning URI ครั้งจำเป็นผ่าน no-store และไม่ log; confirm code ก่อนเปิด factor จริง:

```csharp
var totp = new OtpNet.Totp(secret);
var valid = totp.VerifyTotp(clock.GetUtcNow().UtcDateTime, code,
    out var step, new OtpNet.VerificationWindow(previous: 1, future: 1));
```

- [x] การรับ code ต้อง update last-used-step แบบ conditional ใน transaction และตรวจ affected rows=1 ก่อนให้ assurance; การตรวจ library อย่างเดียวไม่ป้องกัน replay เพิ่ม test concurrent code สอง request ผ่านเพียงหนึ่ง, drift เกิน window, brute-force429 และ expired challenge
- [x] restricted stage อนุญาตเฉพาะ session/logout/CSRF และ action ที่ตรง stage; successful confirm/challenge rotate session+CSRF, assurance expiry ต้อง step-up ใหม่ ไม่เชื่อ MFA boolean จาก client
- [x] recovery codes เก็บ hash, consume ด้วย conditional update; recovery ลดเป็น `MfaEnrollmentRequired`, revoke session/factor เก่าและต้องตั้ง factor ใหม่ก่อน privileged access; operator-assisted recovery ต้องสิทธิ์แยก `users:recover-mfa`, recent MFA, ห้าม self-recovery ผ่าน admin route และต้อง audit เหตุผล ไม่ข้ามหลักฐานยืนยันตัวบุคคลนอกระบบ
- [x] ทดสอบ restart โดยใช้ keyring เดิม decrypt factor ได้, key สูญหาย fail closed, recovery replay/concurrency, audit ไม่มี secret/code; รัน MfaTests ผ่านแล้ว commit `feat: add MFA assurance and recovery controls`

ผลส่งมอบ Task5: [รายงานและข้อค้าง](../../architecture/module-2-task-5-verification.md) — commit `bb2b4d0`; backend215/215, Node23/23, Build/Lint/HTTPS4000/4001ผ่าน; reviewไม่มีCritical/Important มีMinor pending enrollment1ข้อ; operator recoveryยังเป็นusecaseไม่mapHTTPจนTask6

## Task 6: Named permissions, revocation และ audit contract

สถานะ: implementation commit `001658a` และreviewfixpagination6RED→6GREEN; backend259/259, Node23/23, Build/Lint/HTTPS4000/4001ผ่านหลังแก้ ไม่มีCritical/Importantค้าง Minor1ข้อด้านsessionrollbacktest ดู [รายงาน Task6](../../architecture/module-2-task-6-verification.md)

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/Authorization/PermissionRequirement.cs`, `PermissionHandler.cs`, `Identity/Accounts/RoleEndpoints.cs`, `Auditing/SecurityAuditRequest.cs`; แก้ `backend/src/TPR10.Api/Auditing/IAuditEventWriter.cs`, `AuditEventWriter.cs`, `Data/Entities/AuditEvent.cs`, `Data/Tpr10DbContext.cs`, `TechnicalProbes/TechnicalProbeEndpoints.cs`, `Identity/IdentityRegistration.cs`; สร้าง `backend/tests/TPR10.Api.IntegrationTests/AuthorizationTests.cs`; แก้ `CorrelationAndAuditTests.cs`

**รับ/ส่ง:** `PermissionRequirement(string Capability, bool RequireMfa)` เป็น authorization requirement; เพิ่ม audit overload `Task WriteAsync(SecurityAuditRequest request, CancellationToken ct)` โดยคง signature เดิมให้ Module 1; request มี ActorId, ActingRoleId, WorkspaceId/ProjectId/SiteId nullable, Action, TargetType, TargetId, Outcome, Metadata

- [x] เขียน rejection matrix โดย test fixture seed ใหม่ต่อ test:

```csharp
[Fact]
public async Task Anonymous_cannot_read_protected_probe() {
    await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
    var response = await driver.Client.GetAsync("/api/v1/system/identity-probe");
    Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
}
```

- [x] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~AuthorizationTests` ให้แดง (routeยังไม่มี)
- [x] map GET protected identity probe ใน Testing/Development ด้วย capability `system:probe`; POST technical-probes เดิมเพิ่ม capability และ MFA ไม่ทิ้ง bypass; identity schema ไม่มี scope record จึงบันทึก scope เป็น null ไม่สร้างสิทธิ์ cross-project:

```csharp
options.AddPolicy("system:probe", policy => policy.RequireAuthenticatedUser()
    .AddRequirements(new PermissionRequirement("system:probe", RequireMfa: true)));
```

- [x] เพิ่ม matrix valid session ไม่มี permission403, privileged ไม่มี/หมดอายุ MFA403, restricted stage403, valid permission+fresh MFAสำเร็จ; problem types `urn:tpr10:session-required`, `permission-denied`, `mfa-required`, `stage-restricted` พร้อม correlation
- [x] map users/roles/permissions endpoints: roles GET/POST/PATCH และ PUT `/roles/{id}/permissions`, PUT `/users/{id}/roles` ต้อง `roles:manage`+MFA; GET permissions ต้อง `roles:read`; catalog capabilities เป็นรายการระบบห้ามสร้าง arbitrary capability; การเปลี่ยน grants เพิ่ม security_version/revoke affected users ใน transaction, client ไม่เลือก acting role ที่ตนไม่มี
- [x] เพิ่ม tests ถอด role/เปลี่ยน permission ขณะถือ cookie, admin sign-out-everywhere, ไม่ลบ adminสุดท้าย, audit failure rollback user mutation, denialไม่มี probe row และ immutable audit triggersยังทำงาน; เพิ่ม migration `ExpandIdentityAudit` สำหรับ acting role/target type/outcome โดยไม่ UPDATE audit rows เดิม ให้ historical nullable columns
- [x] ปรับ CorrelationAndAuditTests ให้ขอ session+MFA+CSRF ก่อน POST เดิม ตรวจ intended validation/rollback ยังถึง handler จริง ไม่ผ่านเพราะ403; รันทั้งสอง test classes ผ่าน แล้ว commit `feat: enforce permissions and auditable revocation`

## Task 7: Password reset และ forced password change

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/Reset/PasswordResetService.cs`, `ResetDelivery.cs`; แก้ `backend/src/TPR10.Api/Identity/AuthEndpoints.cs`, `Accounts/AccountEndpoints.cs`, `IdentityRegistration.cs`; สร้าง `backend/tests/TPR10.Api.IntegrationTests/PasswordResetTests.cs`

**รับ/ส่ง:** `POST /api/v1/auth/password-reset/request` `{username}` คืน202เหมือนกัน, `/password-reset/complete` `{token,password}` คืน204, `/password/change` `{currentPassword,newPassword}` คืน204; admin `POST /api/v1/users/{id}/password-reset` ต้อง `users:manage`+MFA

- [ ] เขียน enumeration test:

```csharp
[Theory]
[InlineData("known")]
[InlineData("unknown")]
public async Task Reset_request_does_not_reveal_account(string username) {
    await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
    await driver.SeedUserAsync("known", "รหัสทดสอบยาวพอ-123456", []);
    var response = await driver.PostAsync("/api/v1/auth/password-reset/request", new { username });
    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    Assert.Equal("หากบัญชีรองรับการกู้คืน ระบบจะดำเนินการตามช่องทางที่กำหนด", await response.Content.ReadAsStringAsync());
}
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~PasswordResetTests` ให้แดง
- [ ] สร้าง token 32 random bytes; request tableเก็บ hash/expiry; queue payloadเข้ารหัสสำหรับ delivery ใน transactionเดียวกับ request/audit ไม่เก็บ raw token plaintextใน outbox; test sink เปิดเฉพาะ Testing/Development อ่านผ่าน DI ไม่ใช่ public HTTP และไม่ log token:

```csharp
await using var tx = await db.Database.BeginTransactionAsync(ct);
await delivery.EnqueueAsync(requestId, recipient, protectedPayload, ct);
await db.SaveChangesAsync(ct);
await tx.CommitAsync(ct);
```

- [ ] production ที่ยังไม่มี delivery adapter ต้องไม่อ้างว่าส่งแล้ว: startup/config gateปิด email-reset readiness แต่ generic endpointไม่เผย account; document dependency Module5 ชัดเจน ทดสอบ fake delivery failure ไม่ทำให้ credential เปลี่ยนและ outbox retryไม่สร้าง token ใหม่
- [ ] complete reset ล็อก request/consume once, hash passwordใหม่, revokeทุก session และ requestเก่า, auditในtransactionเดียว ไม่มี auto-login; admin resetสร้าง one-use temporary credential ผ่านช่องทางผู้ดูแลที่ได้รับอนุญาต ไม่ส่ง credentialในemail/log บังคับ stage PasswordChangeRequired และ revokeทันที
- [ ] เพิ่ม tests expiry, replay, concurrent completeสำเร็จหนึ่งครั้ง, reset tokenจากบัญชีอื่น, forced changeเข้า probeไม่ได้, หลังchangeต้องloginใหม่/MFAตามrole และ audit rollbackไม่ consume token; รัน PasswordResetTestsผ่าน แล้ว commit `feat: add one-time reset and forced password change`

## Task 8: เชื่อม Landing Page → Login → Portal

**ไฟล์:** สร้าง `lib/auth/server-session.ts`, `auth-client.ts`, `safe-return-path.ts`, `app/login/page.tsx`, `app/login/LoginForm.tsx`, `app/auth/mfa/page.tsx`, `app/auth/reset/page.tsx`, `app/auth/change-password/page.tsx`, `app/portal/account/page.tsx`, `tests/e2e/identity.spec.ts`, `playwright.config.ts`; แก้ `components/Navbar.tsx`, `components/Footer.tsx`, `app/portal/layout.tsx`, `app/portal/page.tsx`, `package.json`, `package-lock.json`

**รับ/ส่ง:** `readServerSession(): Promise<SessionView | null>` ใช้ server-only module, private API origin และ forwarding cookie เฉพาะAPIที่ตั้งค่า; `safeReturnPath(value: string | null): string` คืนเฉพาะ `/portal` หรือ pathใต้ `/portal/`; `authMutation(path: string, body: unknown): Promise<Response>` ดึงCSRFใหม่ก่อน mutation

- [ ] ติดตั้ง/pin Playwright dev dependency และ browser runtime หลังตรวจversion; เขียน E2E ก่อน UI:

```typescript
import { test, expect } from '@playwright/test';
test('ผู้ใช้ไม่เข้าสู่ระบบถูกส่งไปหน้า login', async ({ page }) => {
  await page.goto('/portal');
  await expect(page).toHaveURL(/\/login\?returnTo=%2Fportal$/);
  await expect(page.getByRole('button', { name: 'เข้าสู่ระบบ' })).toBeVisible();
});
```

- [ ] รัน `npx playwright test tests/e2e/identity.spec.ts` กับlocalHTTPSproxy ต้องแดงเพราะ portalยังไม่ redirect
- [ ] Navbar/Footer ชี้ login; loginสำเร็จ routeตามstage ก่อนreturnTo; server session fetch `cache: 'no-store'`, portal `dynamic = 'force-dynamic'`; 401เท่านั้นredirectlogin ส่วน APIล่มแสดงสถานะบริการไม่พร้อม ห้ามตีความว่าsessionใช้ได้:

```typescript
export function safeReturnPath(value: string | null): string {
  if (!value || /[\\\r\n]/.test(value)) return '/portal';
  const url = new URL(value, 'https://tpr10.invalid');
  return url.origin === 'https://tpr10.invalid' &&
    (url.pathname === '/portal' || url.pathname.startsWith('/portal/'))
      ? url.pathname + url.search : '/portal';
}
```

- [ ] ฟอร์มมีlabel, keyboard focus, loadingป้องกันsubmitซ้ำและข้อความไทย; หน้า MFA แสดง URI/manual keyในenrollmentพร้อมคำเตือนไม่แชร์ มีchallenge/recovery; หน้าresetใช้ tokenจาก URL fragment ย้ายเข้าmemoryแล้วล้างfragmentทันที ห้ามใช้query/log; account pageมีlogout/signout-allและMFA enrollmentสำหรับผู้ใช้ทั่วไปตามpolicy
- [ ] เพิ่ม E2E loginผิด, forced-change, MFArequired, logout/back, sessionหมดอายุ, APIล่ม, external/protocol-relative/backslash/malformed returnTo, สองbrowser contextไม่เห็นข้อมูลกัน, cookieflags และหน้าจอมือถือ; catch malformed URLในhelperแล้วfallback `/portal`
- [ ] browser testเข้าผ่านHTTPSที่trustแล้ว API testยืนยันauthorizationโดยไม่ผ่านUI; รัน E2E, `npm test`, `npm run lint`, `npm run build` ผ่าน แล้ว commit `feat: connect landing login and protected portal`

## Task 9: OpenAPI, ตรวจทานอิสระ และ Exit Gate

**ไฟล์:** สร้าง `backend/src/TPR10.Api/Identity/IdentityOpenApiTransformer.cs`, `backend/tests/TPR10.Api.IntegrationTests/IdentityOpenApiTests.cs`, `docs/architecture/module-2-exit-gate.md`; แก้ `backend/src/TPR10.Api/Identity/IdentityRegistration.cs`, `docs/runbooks/module-2-identity.md`

**รับ/ส่ง:** ใช้ route metadataจากTasks2–7 ส่ง OpenAPIและหลักฐานexit gate ไม่เพิ่มbusinesscapability

- [ ] เขียน contract testก่อน transformer:

```csharp
[Fact]
public async Task OpenApi_documents_csrf_on_login() {
    await using var driver = await IdentityTestDriver.CreateAsync(fixture.ConnectionString);
    var document = await driver.Client.GetStringAsync("/api/openapi/v1.json");
    using var json = System.Text.Json.JsonDocument.Parse(document);
    var login = json.RootElement.GetProperty("paths").GetProperty("/api/v1/auth/login").GetProperty("post");
    Assert.Contains(login.GetProperty("parameters").EnumerateArray(), p =>
        p.GetProperty("name").GetString() == "X-CSRF-Token" && p.GetProperty("required").GetBoolean());
}
```

- [ ] รัน `dotnet test backend/TPR10.sln --filter FullyQualifiedName~IdentityOpenApiTests` ให้แดง; transformerเพิ่มcsrfทุกunsafe method, cookie scheme, permission/MFA/stage metadata, request/response schemaและ400/401/403/409/429/503 problemsที่endpointใช้จริง; test enumerateทุกoperation ไม่ตรวจloginตัวเดียว
- [ ] ตรวจ requirements coverage กับbaseline8/11/20; ทำ runbook bootstrap/migrate/restore-keyring/revoke/reset/recovery/HTTPS/no-secret-log และ gateแยก “ผ่านทางเทคนิค”, “รอ Security owner”, “รอ delivery adapter” ห้ามรวมเป็นพร้อมproduction
- [ ] รันคำสั่งเต็มต่อไปนี้จากworktreeและบันทึกเวลาUTC/commit/exitcode/จำนวนtestจริง ไม่ใช้ผล Module1แทน:

```bash
export PATH="/private/tmp/tpr10-dotnet:/Applications/Docker.app/Contents/Resources/bin:$PATH"
export DOTNET_ROOT=/private/tmp/tpr10-dotnet
dotnet restore backend/TPR10.sln
dotnet test backend/TPR10.sln
dotnet build backend/TPR10.sln --no-restore
dotnet format backend/TPR10.sln --verify-no-changes --no-restore
npm ci
npm test
npm run lint
npm run build
npx playwright test tests/e2e/identity.spec.ts
git diff --check
```

  SDK pathเป็นruntimeที่เคยติดตั้งชั่วคราว ต้องตรวจว่ามีจริง ถ้าไม่มีติดตั้งSDKตามglobal.jsonก่อน ไม่เปลี่ยนversionเพื่อให้คำสั่งผ่าน Dockerต้องพร้อมและbrowserstackใช้เฉพาะฐานทดสอบ ปิดserversหลังตรวจ
- [ ] ขอ reviewerอิสระตรวจdiffทั้งbranchกับแผนและbaseline โดยเน้น5failure modesด้านบน, crypto/dependency, authorizationทุกroute, resetdeliveryboundaryและauditatomicity; ถ้าพบข้อบกพร่องใช้receiving-code-reviewและsystematic-debuggingตามเหตุ แล้วเพิ่มregressiontestก่อนfix
- [ ] รันTest/Build/Lint/E2Eครบอีกครั้งหลังแก้review บันทึกข้อจำกัดที่ยังไม่ผ่าน ห้ามแจ้งเสร็จหากคำสั่งใดไม่ผ่าน; commit `docs: record module 2 security verification and exit gate` และส่งสรุปภาษาไทย ไม่push/mergeโดยถือว่าการอนุมัติแผนเท่ากับอนุมัติเผยแพร่

## เกณฑ์รับงานและลำดับการเริ่ม

เริ่ม1→2→3→4→5→6→7→8→9 โดยรักษาขอบเขตadminrouteปิดจนpermission/MFAพร้อม การเริ่มแต่ละTaskต้องอ่านtestเดิมและred failureที่เกี่ยวข้องจริง ไม่ถือcompileerrorจากsetupผิดเป็นหลักฐานREDของbehavior หลังจบทุกTaskมีfocusedtestsและcommitที่ไม่รวมsecret

รับทางเทคนิคเมื่อ login/logout/reset/forced-change/MFA/permission/CSRF มีทั้งทางผ่านและทางปฏิเสธ, race testsใช้PostgreSQLจริง, auditrollbackและimmutabletriggerผ่าน, หน้าเว็บเชื่อมกัน, Test/Build/Lint/E2Eผ่าน และไม่มีCritical/Important review findingค้าง

รับ Exit Gate Module2 เมื่อเพิ่มการอนุมัติpolicyโดยSecurity owner และบันทึกขอบเขตemaildeliveryตามจริง; หากต้องการใช้อีเมลproductionก่อนModule5 ต้องอนุมัติขยายขอบเขตadapterก่อน ไม่ส่งงานโดยอ้างว่ามีdeliveryจริงแล้ว

ขั้นถัดไปหลังผู้ใช้ตรวจทานแผน: สร้างworktreeใหม่และเริ่มTask1แบบNative/inlineตามที่เลือกไว้ ไม่ต้องเลือกวิธีดำเนินงานซ้ำ

## แหล่งอ้างอิงทางเทคนิคที่ตรวจขณะวางแผน

- [Konscious.Security.Cryptography](https://github.com/kmaragon/Konscious.Security.Cryptography): มี Argon2id API; การเลือก candidateยังต้องตรวจrelease/advisoryและpinตอนเริ่มTask1
- [Otp.NET](https://github.com/kspearrin/Otp.NET): `VerifyTotp` คืน timestepที่ตรง แต่ผู้ใช้libraryต้องเก็บและป้องกันone-time replayเอง จึงต้องมีconditional database updateในTask5

## ผลตรวจทานแผนด้วยตนเอง

- ครอบคลุมbaseline authentication/providerboundary/passwordreset (Tasks1/3/4/7), transport/CSRF (2/3), MFA (5), authorization/audit (6), OpenAPIและowner gate (9)
- failure modesทั้ง5มีTaskรับผิดชอบและtestcaseระบุชัด; scopebusinessdataตั้งใจเลื่อนไปModule3 ไม่ใช่ส่วนที่ผ่านแล้ว
- interfaceกลางใช้ชื่อเดียวกันทุกTask; testhelperไม่เพิ่มproductionendpoint; ตัวอย่างcodeเป็นส่วนที่ต้องประกอบกับfixture/class/usings ไม่ใช่ไฟล์implementationสำเร็จรูป
- หลักฐานTest/Build/LintของModule2จะเกิดเมื่อพัฒนาจริงเท่านั้น การจัดทำเอกสารนี้ไม่รับรองว่าระบบใหม่ทำงานแล้ว

using Microsoft.AspNetCore.Http;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Identity.Data;
using TPR10.Api.Identity.Mfa;
using static TPR10.Api.IntegrationTests.MfaTests;
using static TPR10.Api.IntegrationTests.MfaSecurityTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class OperatorMfaRecoveryTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operator_recovery_revokes_real_target_factor_codes_and_sessions_atomically(bool failAudit)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await EnrollConfirmedAsync(driver);
        await using var db = driver.Database.CreateContext();
        var actorSession = await db.Set<IdentitySession>().SingleAsync(x => x.RevokedAtUtc == null);
        var permission = new IdentityPermission { Id = Guid.NewGuid(), Capability = "users:recover-mfa" };
        db.Add(permission);
        db.Add(new RolePermission { RoleId = (await db.Set<UserRole>().SingleAsync()).RoleId, PermissionId = permission.Id });
        await db.SaveChangesAsync();
        var target = await driver.SeedUserAsync("target", Password, [], requiresMfa: true);
        using var client = driver.NewClient();
        using var login = IdentityTestDriver.Mutation(await IdentityTestDriver.TokenAsync(client), "/api/v1/auth/login");
        login.Content = JsonContent.Create(new { username = "target", password = Password });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(login)).StatusCode);
        var secret = Secret(await (await PostAsync(client, "enroll", "")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, "confirm", Code(secret, driver.Clock.GetUtcNow()))).StatusCode);
        if (failAudit) await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION fail_operator_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'test audit unavailable'; END $$; CREATE TRIGGER fail_operator_audit BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION fail_operator_audit();");
        using var scope = driver.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OperatorMfaRecovery>();
        if (failAudit) await Assert.ThrowsAsync<DbUpdateException>(() => service.RecoverAsync(actorSession.Id, target, "อุปกรณ์สูญหาย", "CASE-2026-002", default));
        else Assert.Equal(204, ((IStatusCodeHttpResult)await service.RecoverAsync(actorSession.Id, target, "อุปกรณ์สูญหาย", "CASE-2026-002", default)).StatusCode);
        await using var verify = driver.Database.CreateContext();
        Assert.Equal(failAudit, (await verify.Set<MfaFactor>().SingleAsync(x => x.UserId == target)).RevokedAtUtc is null);
        Assert.Equal(failAudit ? 0 : 10, await verify.Set<MfaRecoveryCode>().CountAsync(x => x.UserId == target && x.RevokedAtUtc != null));
        Assert.Equal(failAudit ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/session")).StatusCode);
    }

    [Theory]
    [InlineData("success", 204)]
    [InlineData("no-permission", 403)]
    [InlineData("stale-mfa", 403)]
    [InlineData("self", 403)]
    [InlineData("missing-evidence", 400)]
    [InlineData("missing-reason", 400)]
    public async Task Operator_recovery_requires_separate_permission_recent_mfa_nonself_and_evidence(string scenario, int status)
    {
        using var keys = new TestKeyMaterial();
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString, keys.Settings);
        await EnrollConfirmedAsync(driver);
        await using var db = driver.Database.CreateContext();
        var actor = await db.Set<IdentityUser>().SingleAsync();
        var actorSession = await db.Set<IdentitySession>().SingleAsync(x => x.RevokedAtUtc == null);
        if (scenario != "no-permission")
        {
            var permission = new IdentityPermission { Id = Guid.NewGuid(), Capability = "users:recover-mfa" };
            db.Add(permission);
            var roleId = (await db.Set<UserRole>().SingleAsync()).RoleId;
            db.Add(new RolePermission { RoleId = roleId, PermissionId = permission.Id });
            await db.SaveChangesAsync();
        }
        var target = scenario == "self" ? actor.Id : await driver.SeedUserAsync("target", Password, [], requiresMfa: true);
        if (scenario == "stale-mfa") driver.Advance(TimeSpan.FromMinutes(16));
        using var scope = driver.Factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<OperatorMfaRecovery>();
        var result = await service.RecoverAsync(actorSession.Id, target,
            scenario == "missing-reason" ? "" : "อุปกรณ์ยืนยันตัวตนสูญหาย",
            scenario == "missing-evidence" ? "" : "CASE-2026-001", default);
        Assert.Equal(status, ((IStatusCodeHttpResult)result).StatusCode);
        await using var verify = driver.Database.CreateContext();
        var user = await verify.Set<IdentityUser>().SingleAsync(x => x.Id == target);
        Assert.Equal(status == 204 ? 1 : 0, user.SecurityVersion);
        var audits = await verify.AuditEvents.Where(x => x.EventType == "identity.mfa.operator-recovery").ToListAsync();
        Assert.Equal(status == 204 ? 1 : 0, audits.Count);
        if (status == 204)
        {
            Assert.Equal(actor.Id, audits[0].ActorId);
            Assert.Equal(target, audits[0].TargetId);
            Assert.Contains(await verify.AuditMetadata.Select(x => x.Value).ToListAsync(), x => x == "CASE-2026-001");
        }
    }

    [Fact]
    public async Task Bootstrap_catalog_includes_separate_recovery_capability()
    {
        await using var driver = await IdentityTestDriver.CreateAsync(postgres.ConnectionString);
        await AccountProvisioningTests.BootstrapAsync(driver);
        await using var db = driver.Database.CreateContext();
        Assert.True(await db.Set<IdentityPermission>().AnyAsync(x => x.Capability == "users:recover-mfa"));
    }
}

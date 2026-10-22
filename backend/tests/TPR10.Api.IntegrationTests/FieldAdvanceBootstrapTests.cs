using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.Identity.Data;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceBootstrapTests(PostgresFixture postgres)
{
    private static readonly Guid OperatorPersonId = Guid.Parse("72000000-0000-0000-0000-000000000034");

    [Fact]
    public async Task Certified_new_manifest_can_delegate_levels_without_granting_usage_or_reopening_bootstrap()
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        var grant = manifest["grants"]![1]!;
        grant["delegableCapabilities"] = new JsonArray("request.create", "request.delegate", "request.approve");
        grant["approvalLevels"] = new JsonArray();
        grant["delegableApprovalLevels"] = new JsonArray("supervisor", "project-manager");
        grant["primaryApprovalLevels"] = new JsonArray();
        grant["delegablePrimaryApprovalLevels"] = new JsonArray("project-manager");
        Assert.Equal(0, (await RunAsync(f, manifest)).Code);
        await using var db = f.CreateContext();
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.ProjectId == ProjectId);
        Assert.Empty(proposal.ApprovalLevels);
        Assert.Equal(["supervisor", "project-manager"], proposal.DelegableApprovalLevels);
        Assert.Equal(["project-manager"], proposal.DelegablePrimaryApprovalLevels);
        Assert.DoesNotContain("request.approve", proposal.Capabilities);
        Assert.Equal(2, (await RunAsync(f, Manifest())).Code);
    }

    [Theory]
    [InlineData("startsAtUtc")]
    [InlineData("endsAtUtc")]
    public async Task Timestamps_without_explicit_timezone_cannot_change_meaning_on_another_operator_machine(string field)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        manifest["grants"]![0]![field] = DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal(64, (await RunAsync(f, manifest)).Code);
        await AssertEmptyAsync(f);
    }

    [Theory]
    [InlineData("projectId")]
    [InlineData("approvalLimit")]
    [InlineData("endsAtUtc")]
    [InlineData("startsAtUtc")]
    [InlineData("recipient")]
    [InlineData("capabilities")]
    [InlineData("delegableCapabilities")]
    public async Task Omitted_grant_fields_never_default_to_company_scope_or_unbounded_authority(string field)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        manifest["grants"]![0]!.AsObject().Remove(field);
        Assert.Equal(64, (await RunAsync(f, manifest)).Code);
        await AssertEmptyAsync(f);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-nested")]
    [InlineData("oversized")]
    [InlineData("null-grant")]
    [InlineData("null-control")]
    public async Task Ambiguous_or_oversized_json_fails_closed_without_echoing_input(string defect)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        if (defect == "unknown") manifest["ignoredAuthority"] = "DO-NOT-ECHO";
        if (defect == "null-grant") manifest["grants"]![0] = null;
        if (defect == "null-control") manifest["certifier"] = null;
        var raw = manifest.ToJsonString();
        if (defect == "duplicate-root") raw = raw.Insert(1, "\"reason\":\"DO-NOT-ECHO\",");
        if (defect == "duplicate-nested") raw = raw.Replace("\"capabilities\":[", "\"projectId\":null,\"capabilities\":[", StringComparison.Ordinal);
        if (defect == "oversized") raw = new string(' ', 65537) + raw;
        var result = await RunAsync(f, manifest, raw: raw);
        Assert.Equal(64, result.Code);
        Assert.DoesNotContain("DO-NOT-ECHO", result.Output);
        await AssertEmptyAsync(f);
    }

    // These are actual capability checks against the records the CLI created, not just row counts.
    [Fact]
    public async Task Bootstrap_grants_enable_management_only_at_the_exact_certified_scope()
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        ((JsonArray)manifest["grants"]!).RemoveAt(1); // company only; no project authority
        Assert.Equal(0, (await RunAsync(f, manifest)).Code);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recipientSession = await f.SessionAsync(RecipientId);
        var adminSession = await f.SessionAsync(AuthorId);
        await using var db = f.CreateContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var access = FieldAdvanceAccessTests.Access(db, recipientSession, f.Clock);
        foreach (var capability in new[] { "grant.propose", "grant.review", "grant.revoke" })
        {
            Assert.True((await access.CheckAsync(RecipientId, new(WorkspaceId, null), capability, default)).Allowed);
            Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, ProjectId), capability, default)).Allowed);
            Assert.False((await access.CheckAsync(RecipientId, new(OtherWorkspaceId, null), capability, default)).Allowed);
        }
        foreach (var capability in new[] { "policy.edit.company", "policy.edit.project", "policy.review",
            "request.approve", "finance.check", "payment.record", "report.read" })
            Assert.False((await access.CheckAsync(RecipientId, new(WorkspaceId, null), capability, default)).Allowed);
        Assert.False((await FieldAdvanceAccessTests.Access(db, adminSession, f.Clock)
            .CheckAsync(AuthorId, new(WorkspaceId, null), "grant.propose", default)).Allowed);
    }

    [Fact]
    public async Task Two_certified_managers_can_use_normal_three_person_grant_workflow()
    {
        await using var f = await ReadyAsync();
        var secondManager = Guid.Parse("71000000-0000-0000-0000-000000000035");
        var secondPerson = Guid.Parse("72000000-0000-0000-0000-000000000035");
        await using (var seed = f.CreateContext())
        {
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO users(id,username,normalized_username) VALUES ({secondManager},'fa-second-manager','FA-SECOND-MANAGER');
                INSERT INTO employee_memberships(id,user_id,workspace_id,department_id,valid_from_utc,created_by,reason)
                SELECT gen_random_uuid(),{secondManager},workspace_id,department_id,valid_from_utc,created_by,'สมาชิกจำลอง'
                FROM employee_memberships WHERE user_id={RecipientId};
                """);
        }
        await f.SessionAsync(secondManager); // enroll MFA in the disposable fixture
        var manifest = Manifest();
        manifest["grants"]![1]!["projectId"] = null;
        manifest["grants"]![1]!["recipient"]!["userId"] = secondManager;
        manifest["grants"]![1]!["recipient"]!["personId"] = secondPerson;
        Assert.Equal(0, (await RunAsync(f, manifest)).Code);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var proposerSession = await f.SessionAsync(RecipientId);
        var reviewerSession = await f.SessionAsync(secondManager);
        await using var db = f.CreateContext();
        var proposer = new GrantService(db, FieldAdvanceAccessTests.Access(db, proposerSession, f.Clock),
            new FinancePersonResolver(db, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var proposed = await proposer.ProposeAsync(RecipientId,
            new GrantSpec(AuthorId, new(WorkspaceId, null), ["policy.review"], [], null,
                f.Clock.GetUtcNow(), f.Clock.GetUtcNow().AddHours(1)),
            new CommandMeta(Guid.NewGuid(), 1, "มอบหมายจำลองหลังตั้งต้น"), default);
        Assert.Equal(201, proposed.Status);
        await using var reviewDb = f.CreateContext();
        var reviewer = new GrantService(reviewDb, FieldAdvanceAccessTests.Access(reviewDb, reviewerSession, f.Clock),
            new FinancePersonResolver(reviewDb, f.Clock), new AuditEventWriter(reviewDb, new CorrelationContext(), f.Clock), f.Clock);
        Assert.Equal(200, (await reviewer.ReviewAsync(secondManager, proposed.Value!.Id, "approve",
            new CommandMeta(Guid.NewGuid(), 1, "ตรวจอิสระจำลองหลังตั้งต้น"), default)).Status);
        Assert.Equal(3, await db.Set<GrantAssignment>().CountAsync());
    }

    [Fact]
    public async Task Eligibility_change_during_audit_rolls_back_mapping_grants_audit_and_receipts()
    {
        await using var f = await ReadyAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION withdraw_mfa_during_bootstrap() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.event_type='field-advance.bootstrap.completed' THEN
              UPDATE mfa_factors SET revoked_at_utc=now(); END IF; RETURN NEW; END $$;
            CREATE TRIGGER withdraw_mfa_during_bootstrap AFTER INSERT ON audit_events
              FOR EACH ROW EXECUTE FUNCTION withdraw_mfa_during_bootstrap();
            """);
        Assert.Equal(2, (await RunAsync(f, Manifest())).Code);
        await AssertEmptyAsync(f);
        Assert.All(await db.Set<MfaFactor>().ToArrayAsync(), factor => Assert.Null(factor.RevokedAtUtc));
    }

    // Missing fields must not become implicit/default authority.
    [Theory]
    [InlineData("grants")]
    [InlineData("operator")]
    [InlineData("certifier")]
    [InlineData("reviewer")]
    [InlineData("evidenceReference")]
    [InlineData("workspaceId")]
    [InlineData("commandId")]
    [InlineData("reason")]
    public async Task Missing_required_authorization_is_rejected_without_state(string field)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        manifest.Remove(field);
        Assert.Equal(64, (await RunAsync(f, manifest)).Code);
        await AssertEmptyAsync(f);
    }

    [Theory]
    [InlineData("empty-grants")]
    [InlineData("empty-evidence")]
    [InlineData("empty-person")]
    [InlineData("business-use")]
    [InlineData("unknown-delegable")]
    [InlineData("duplicate-capability")]
    [InlineData("negative-limit")]
    [InlineData("fractional-satang")]
    [InlineData("overflow-limit")]
    [InlineData("expired")]
    [InlineData("invalid-period")]
    [InlineData("empty-project")]
    [InlineData("control-reason")]
    [InlineData("duplicate-grant")]
    public async Task Invalid_manifest_cannot_create_state(string defect)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        var grant = manifest["grants"]![0]!;
        switch (defect)
        {
            case "empty-grants": manifest["grants"] = new JsonArray(); break;
            case "empty-evidence": manifest["evidenceReference"] = " "; break;
            case "empty-person": manifest["operator"]!["personId"] = Guid.Empty; break;
            case "business-use": grant["capabilities"] = new JsonArray("payment.record"); break;
            case "unknown-delegable": grant["delegableCapabilities"] = new JsonArray("*"); break;
            case "duplicate-capability": grant["capabilities"] = new JsonArray("grant.propose", "grant.propose"); break;
            case "negative-limit": grant["approvalLimit"] = -1; break;
            case "fractional-satang": grant["approvalLimit"] = 12.345m; break;
            case "overflow-limit": grant["approvalLimit"] = 10000000000000000m; break;
            case "expired": grant["endsAtUtc"] = DateTimeOffset.UtcNow.AddSeconds(-10); break;
            case "invalid-period": grant["endsAtUtc"] = DateTimeOffset.UtcNow.AddDays(-2); break;
            case "empty-project": grant["projectId"] = Guid.Empty; break;
            case "control-reason": manifest["reason"] = "not\nallowed"; break;
            case "duplicate-grant": ((JsonArray)manifest["grants"]!).Add(grant.DeepClone()); break;
        }
        Assert.Equal(64, (await RunAsync(f, manifest)).Code);
        await AssertEmptyAsync(f);
    }

    // Person-level separation must include the last recipient, not just the first.
    [Theory]
    [InlineData("certifier", "reviewer")]
    [InlineData("certifier", "operator")]
    [InlineData("reviewer", "operator")]
    [InlineData("certifier", "recipient")]
    [InlineData("reviewer", "recipient")]
    [InlineData("operator", "recipient")]
    public async Task Every_control_person_is_distinct_from_controls_and_every_recipient(string first, string second)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        var target = second == "recipient" ? manifest["grants"]![1]!["recipient"]! : manifest[second]!;
        target["personId"] = manifest[first]!["personId"]!.DeepClone();
        Assert.Equal(2, (await RunAsync(f, manifest)).Code);
        await AssertEmptyAsync(f);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("certifier")]
    [InlineData("reviewer")]
    public async Task One_account_cannot_claim_distinct_person_ids_to_bypass_separation(string role)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        manifest["grants"]![1]!["recipient"]!["userId"] = manifest[role]!["userId"]!.DeepClone();
        Assert.Equal(2, (await RunAsync(f, manifest)).Code);
        await AssertEmptyAsync(f);
    }

    [Theory]
    [InlineData("inactive-account")]
    [InlineData("missing-account")]
    [InlineData("missing-mfa")]
    [InlineData("unconfirmed-mfa")]
    [InlineData("revoked-mfa")]
    [InlineData("future-mfa")]
    [InlineData("inactive-company")]
    [InlineData("inactive-project")]
    [InlineData("foreign-project")]
    [InlineData("missing-employment")]
    public async Task Current_identity_and_exact_scope_are_checked_before_writing(string defect)
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        await using (var db = f.CreateContext())
        {
            switch (defect)
            {
                case "inactive-account":
                    (await db.Set<IdentityUser>().SingleAsync(x => x.Id == AliasAccountId)).IsActive = false; break;
                case "missing-account": manifest["operator"]!["userId"] = Guid.NewGuid(); break;
                case "missing-mfa": db.Remove(await db.Set<MfaFactor>().SingleAsync(x => x.UserId == RecipientId)); break;
                case "unconfirmed-mfa":
                    (await db.Set<MfaFactor>().SingleAsync(x => x.UserId == AuthorId)).ConfirmedAtUtc = null; break;
                case "revoked-mfa":
                    (await db.Set<MfaFactor>().SingleAsync(x => x.UserId == ReviewerId)).RevokedAtUtc = DateTimeOffset.UtcNow; break;
                case "future-mfa":
                    (await db.Set<MfaFactor>().SingleAsync(x => x.UserId == AliasAccountId)).ConfirmedAtUtc = DateTimeOffset.UtcNow.AddDays(1); break;
                case "inactive-company":
                    await db.Database.ExecuteSqlRawAsync("UPDATE workspaces SET is_active=false"); break;
                case "inactive-project":
                    await db.Database.ExecuteSqlRawAsync("UPDATE projects SET is_active=false"); break;
                case "foreign-project": manifest["grants"]![1]!["projectId"] = OtherProjectId; break;
                case "missing-employment":
                    await db.Database.ExecuteSqlRawAsync("""
                        UPDATE employee_memberships SET valid_to_utc=now(), ended_by=created_by, version=version+1
                        """); break;
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, (await RunAsync(f, manifest)).Code);
        await AssertEmptyAsync(f);
    }

    [Theory]
    [InlineData("current")]
    [InlineData("ended")]
    [InlineData("future")]
    [InlineData("foreign-person")]
    public async Task Existing_identity_conflicts_are_never_replaced(string defect)
    {
        await using var f = await ReadyAsync();
        await f.SeedPeopleAsync();
        var manifest = Manifest();
        await using var db = f.CreateContext();
        if (defect == "foreign-person")
            manifest["operator"]!["personId"] = Guid.Parse("72000000-0000-0000-0000-000000000041");
        else
        {
            var link = await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == RecipientId);
            if (defect == "current")
            {
                var conflictingPerson = Guid.NewGuid();
                foreach (var grant in manifest["grants"]!.AsArray())
                    grant!["recipient"]!["personId"] = conflictingPerson;
            }
            if (defect == "ended")
            {
                link.EndedAtUtc = DateTimeOffset.UtcNow; link.EndedBy = AuthorId; link.EndReason = "สิ้นสุดจำลอง";
                link.Version++;
            }
            if (defect == "future")
            {
                db.Add(new FinancePerson
                {
                    Id = OperatorPersonId,
                    WorkspaceId = WorkspaceId,
                    CreatedBy = AuthorId,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Reason = "บุคคลจำลอง"
                });
                db.Add(new FinanceAccountLink
                {
                    Id = Guid.NewGuid(),
                    UserId = AliasAccountId,
                    PersonId = OperatorPersonId,
                    WorkspaceId = WorkspaceId,
                    CreatedBy = AuthorId,
                    CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                    Reason = "mapping ในอนาคตจำลอง"
                });
            }
            await db.SaveChangesAsync();
        }
        Assert.Equal(2, (await RunAsync(f, manifest)).Code);
        Assert.Equal(defect == "future" ? 5 : 4, await db.Set<FinancePerson>().CountAsync());
        Assert.Equal(defect == "future" ? 4 : 3, await db.Set<FinanceAccountLink>().CountAsync());
        Assert.Empty(await db.Set<GrantAssignment>().ToArrayAsync());
    }

    [Fact]
    public async Task Matching_mappings_are_reused_and_explicit_null_amount_remains_without_monetary_authority()
    {
        await using var f = await ReadyAsync();
        await f.SeedPeopleAsync();
        var manifest = Manifest();
        manifest["grants"]![0]!["approvalLimit"] = null;
        manifest["grants"]![0]!["endsAtUtc"] = null;
        Assert.Equal(0, (await RunAsync(f, manifest)).Code);
        await using var db = f.CreateContext();
        Assert.Equal(5, await db.Set<FinancePerson>().CountAsync());
        Assert.Equal(4, await db.Set<FinanceAccountLink>().CountAsync());
        var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.ProjectId == null);
        Assert.Null(proposal.ApprovalLimit);
        Assert.Null(proposal.EndsAtUtc);
    }

    // Guarding only active grants breaks replay after revocation.
    [Fact]
    public async Task Repeat_is_rejected_even_after_all_grants_are_revoked_and_command_id_changes()
    {
        await using var f = await ReadyAsync();
        var manifest = Manifest();
        Assert.Equal(0, (await RunAsync(f, manifest)).Code);
        Assert.Equal(2, (await RunAsync(f, manifest)).Code);
        await using var db = f.CreateContext();
        foreach (var grant in await db.Set<GrantAssignment>().ToArrayAsync())
        {
            grant.RevokedAtUtc = DateTimeOffset.UtcNow; grant.RevokedBy = AuthorId; grant.RevocationReason = "ถอนจำลอง";
        }
        await db.SaveChangesAsync();
        manifest["commandId"] = Guid.NewGuid();
        Assert.Equal(2, (await RunAsync(f, manifest)).Code);
        Assert.Equal(2, await db.Set<FinanceBootstrapReceipt>().CountAsync());
        Assert.Equal(2, await db.Set<GrantAssignment>().CountAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task Existing_grant_history_without_bootstrap_receipt_also_blocks_bootstrap()
    {
        await using var f = await ReadyAsync();
        await f.SeedAssignmentAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM field_advance_bootstrap_receipts");
        Assert.Equal(2, (await RunAsync(f, Manifest())).Code);
        Assert.Single(await db.Set<GrantAssignment>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceBootstrapReceipt>().ToArrayAsync());
    }

    // Losing the transaction leaves mappings, partial grants or audit rows behind.
    [Fact]
    public async Task Second_audit_failure_rolls_back_entire_batch_and_allows_clean_retry()
    {
        await using var f = await ReadyAsync();
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_second_bootstrap_audit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.event_type='field-advance.bootstrap.completed'
              AND EXISTS(SELECT 1 FROM audit_events WHERE event_type='field-advance.bootstrap.completed')
              THEN RAISE EXCEPTION 'test-only-audit-failure'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER fail_second_bootstrap_audit BEFORE INSERT ON audit_events
              FOR EACH ROW EXECUTE FUNCTION fail_second_bootstrap_audit();
            """);
        var manifest = Manifest();
        var result = await RunAsync(f, manifest);
        Assert.Equal(1, result.Code);
        Assert.DoesNotContain("test-only-audit-failure", result.Output);
        await AssertEmptyAsync(f);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_second_bootstrap_audit ON audit_events");
        Assert.Equal(0, (await RunAsync(f, manifest)).Code);
    }

    [Fact]
    public async Task Concurrent_commands_allow_exactly_one_batch()
    {
        await using var f = await ReadyAsync();
        await using var barrier = f.CreateContext();
        await using var tx = await barrier.Database.BeginTransactionAsync();
        await barrier.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)");
        var first = RunAsync(f, Manifest());
        var second = RunAsync(f, Manifest());
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (await barrier.Database.SqlQueryRaw<int>("""
                SELECT count(*)::integer AS "Value" FROM pg_locks
                WHERE locktype='advisory' AND objid=7241002 AND NOT granted
                """).SingleAsync(deadline.Token) < 2)
            {
                Assert.False(first.IsCompleted);
                Assert.False(second.IsCompleted);
                await Task.Delay(20, deadline.Token);
            }
        }
        finally { await tx.RollbackAsync(); }
        var results = await Task.WhenAll(first, second);
        Assert.Equal([0, 2], results.Select(x => x.Code).Order().ToArray());
        await using var db = f.CreateContext();
        Assert.Equal(2, await db.Set<GrantAssignment>().CountAsync());
        Assert.Equal(2, await db.Set<FinanceBootstrapReceipt>().CountAsync());
    }

    [Theory]
    [InlineData("--bootstrap-finance=anything")]
    [InlineData("--bootstrap-finance-extra")]
    [InlineData("--bootstrap-admin")]
    public async Task Mixed_flags_exit_instead_of_starting_web_or_admin_bootstrap(string extra)
    {
        await using var f = await ReadyAsync();
        Assert.Equal(64, (await RunAsync(f, Manifest(), ["--bootstrap-finance", extra])).Code);
        await AssertEmptyAsync(f);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"workspaceId\":\"not-a-guid\"}")]
    public async Task Invalid_json_has_no_internal_detail_or_partial_state(string input)
    {
        await using var f = await ReadyAsync();
        var result = await RunAsync(f, Manifest(), raw: input);
        Assert.Equal(64, result.Code);
        Assert.DoesNotContain("Exception", result.Output);
        await AssertEmptyAsync(f);
    }

    private static async Task AssertEmptyAsync(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<FinancePerson>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceAccountLink>().ToArrayAsync());
        Assert.Empty(await db.Set<GrantProposal>().ToArrayAsync());
        Assert.Empty(await db.Set<GrantReview>().ToArrayAsync());
        Assert.Empty(await db.Set<GrantAssignment>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceBootstrapReceipt>().ToArrayAsync());
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    // Missing command execution or mapping/grant persistence breaks this observable CLI contract.
    [Fact]
    public async Task Certified_manifest_creates_explicit_people_and_only_requested_management_grants()
    {
        await using var f = await ReadyAsync();
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<GrantAssignment>().ToArrayAsync());
        Assert.Empty(await db.Set<FinancePerson>().ToArrayAsync());
        var manifest = Manifest();
        var result = await RunAsync(f, manifest);
        Assert.Equal(0, result.Code);
        Assert.Equal(4, await db.Set<FinancePerson>().CountAsync());
        Assert.Equal(4, await db.Set<FinanceAccountLink>().CountAsync());
        var proposals = await db.Set<GrantProposal>().OrderBy(x => x.ProjectId).ToArrayAsync();
        Assert.Equal(2, proposals.Length);
        Assert.All(proposals, p =>
        {
            Assert.Equal(RecipientId, p.RecipientId);
            Assert.Equal(RecipientPersonId, p.RecipientPersonId);
            Assert.Equal(AuthorPersonId, p.ProposedByPersonId);
            Assert.Equal(["grant.propose", "grant.review", "grant.revoke"], p.Capabilities);
            Assert.Equal(["policy.review", "request.approve", "grant.propose", "grant.review", "grant.revoke"], p.DelegableCapabilities);
            Assert.Equal(1234.56m, p.ApprovalLimit);
            Assert.Equal("Approved", p.State);
        });
        Assert.Contains(proposals, p => p.ProjectId is null);
        Assert.Contains(proposals, p => p.ProjectId == ProjectId);
        Assert.Equal(2, await db.Set<GrantAssignment>().CountAsync());
        Assert.Equal(2, await db.Set<FinanceBootstrapReceipt>().CountAsync());
        Assert.Single(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Equal(2, await db.AuditEvents.CountAsync(x => x.EventType == "field-advance.bootstrap.completed"));
        Assert.All(await db.Set<GrantReview>().ToArrayAsync(), r => Assert.Equal(ReviewerPersonId, r.ReviewerPersonId));
        Assert.DoesNotContain("EVIDENCE-TEST-ONLY", result.Output);
        Assert.DoesNotContain(RecipientId.ToString(), result.Output);
        var receipt = await db.Set<FinanceCommandReceipt>().SingleAsync();
        using var evidence = JsonDocument.Parse(receipt.ResultJson);
        Assert.Equal(AliasAccountId, evidence.RootElement.GetProperty("operator").GetProperty("userId").GetGuid());
        Assert.Equal(AuthorPersonId, evidence.RootElement.GetProperty("certifier").GetProperty("personId").GetGuid());
        Assert.Equal(ReviewerPersonId, evidence.RootElement.GetProperty("reviewer").GetProperty("personId").GetGuid());
        Assert.Equal("EVIDENCE-TEST-ONLY", evidence.RootElement.GetProperty("evidenceReference").GetString());
        Assert.Equal(2, evidence.RootElement.GetProperty("grantAssignmentIds").GetArrayLength());
    }

    private async Task<FieldAdvanceFixture> ReadyAsync()
    {
        var f = await FieldAdvanceFixture.CreateAsync(postgres);
        await using var db = f.CreateContext();
        foreach (var id in new[] { AuthorId, ReviewerId, RecipientId, AliasAccountId })
            db.Add(new MfaFactor
            {
                Id = Guid.NewGuid(),
                UserId = id,
                ProtectedSecret = "test-only-unused",
                CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                ConfirmedAtUtc = DateTimeOffset.UtcNow.AddDays(-1)
            });
        await db.SaveChangesAsync();
        return f;
    }

    private static JsonObject Manifest()
    {
        static JsonObject Person(Guid account, Guid person) => new() { ["userId"] = account, ["personId"] = person };
        JsonObject Grant(Guid? project) => new()
        {
            ["recipient"] = Person(RecipientId, RecipientPersonId),
            ["projectId"] = project is null ? null : JsonValue.Create(project.Value),
            ["capabilities"] = new JsonArray("grant.propose", "grant.review", "grant.revoke"),
            ["delegableCapabilities"] = new JsonArray("policy.review", "request.approve", "grant.propose", "grant.review", "grant.revoke"),
            ["approvalLimit"] = 1234.56m,
            ["startsAtUtc"] = DateTimeOffset.UtcNow.AddMinutes(-1),
            ["endsAtUtc"] = DateTimeOffset.UtcNow.AddDays(1)
        };
        return new JsonObject
        {
            ["commandId"] = Guid.NewGuid(),
            ["workspaceId"] = WorkspaceId,
            ["operator"] = Person(AliasAccountId, OperatorPersonId),
            ["certifier"] = Person(AuthorId, AuthorPersonId),
            ["reviewer"] = Person(ReviewerId, ReviewerPersonId),
            ["reason"] = "ตั้งต้นจากเอกสารจำลอง",
            ["evidenceReference"] = "EVIDENCE-TEST-ONLY",
            ["grants"] = new JsonArray(Grant(null), Grant(ProjectId))
        };
    }

    private static async Task<(int Code, string Output)> RunAsync(FieldAdvanceFixture f, JsonObject manifest,
        string[]? args = null, string? raw = null)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        foreach (var arg in args ?? ["--bootstrap-finance"]) start.ArgumentList.Add(arg);
        start.Environment["TPR10_CONNECTION_STRING"] = f.Database.ConnectionString;
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            try { await process.StandardInput.WriteAsync(raw ?? manifest.ToJsonString()); }
            catch (IOException) { } // Early rejection can close stdin; still assert the real process exit code.
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
            return (process.ExitCode, await stdout + await stderr);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}

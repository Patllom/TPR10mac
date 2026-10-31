using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TPR10.Api.FieldAdvances;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.Identity.Data;
using TPR10.Api.Organization.Data;
using TPR10.Api.Attendance.Directory;
using Xunit.Abstractions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvancePolicyHttpTests;
using L = TPR10.Api.IntegrationTests.FieldAdvanceRequestLifecycleTests;
using P = TPR10.Api.IntegrationTests.FieldAdvancePolicyTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceRequestDeliveryTests(PostgresFixture postgres, ITestOutputHelper output)
{
    private const string Root = "/api/v1/field-advances/requests";
    private static string Query => $"workspaceId={WorkspaceId}&projectId={ProjectId}";
    private static readonly Guid Category = Guid.Parse("78000000-0000-0000-0000-000000000001");
    private static RequestCommand Meta(long version = 0) => new(Guid.NewGuid(), version, "เหตุผลทดสอบส่งมอบ");
    private static CreateRequest Input() => new(new(WorkspaceId, ProjectId), AuthorPersonId,
        new("Individual", "สำรวจพื้นที่", new(2026, 10, 24), new(2026, 10, 25), new(2026, 10, 23), [],
            [new(Guid.NewGuid(), Category, "เดินทาง", "QuantityRate", "1.2345", null, null)]));
    private static ApiFactory Factory(FieldAdvanceFixture f, TestKeyMaterial keys, DbCommandInterceptor? counter = null)
        => new(f.Database.ConnectionString, "Production", f.Clock,
            new(keys.Settings) { ["FieldAdvances:RequestRouting:IntervalSeconds"] = "3600", ["Logging:LogLevel:Default"] = "Warning" }, counter);
    private static async Task<JsonElement> Get(HttpClient client, string suffix)
    {
        using var response = await client.GetAsync(Root + suffix);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task<RequestView> Command(HttpClient client, string path, object body, HttpStatusCode status = HttpStatusCode.OK)
    {
        using var response = await Send(client, HttpMethod.Post, "requests" + path, body);
        Assert.True(response.StatusCode == status, $"{path}: {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<RequestView>())!;
    }
    private static Task<RequestView> Create(HttpClient client, CreateRequest? input = null)
        => Command(client, "", new { input = input ?? Input(), meta = Meta() }, HttpStatusCode.Created);
    private static async Task<RequestView> Confirm(HttpClient owner, RequestView row)
    {
        var quote = await Get(owner, $"/{row.Id}/quote");
        return await Command(owner, $"/{row.Id}/confirm", new { confirmationHash = quote.GetProperty("confirmationHash").GetString(), meta = Meta(row.Version) });
    }
    private static async Task<RequestView> Decide(HttpClient client, RequestView row, string outcome)
    {
        var current = await Get(client, $"/{row.Id}");
        return await Command(client, $"/{row.Id}/review", new
        {
            decision = new RequestDecision(outcome, current.GetProperty("currentStep").GetProperty("id").GetGuid()),
            meta = Meta(current.GetProperty("version").GetInt64())
        });
    }
    private static async Task RouteUntil(ApiFactory factory, FieldAdvanceFixture f, Func<Task<bool>> done)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["FieldAdvances:RequestRouting:IntervalSeconds"] = "0.02" }).Build();
        using var worker = new RequestRoutingWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), config,
            f.Clock, factory.Services.GetRequiredService<ILogger<RequestRoutingWorker>>());
        await worker.StartAsync(default);
        try
        {
            var timer = Stopwatch.StartNew();
            while (!await done())
            {
                Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), "routing worker did not reach expected state");
                await Task.Delay(20);
            }
        }
        finally { await worker.StopAsync(default); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task B01_B06_B14_Self_and_delegate_are_project_scoped_and_only_owner_confirms(bool delegated)
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId); using var proxy = await Client(factory, f, RecipientId);
        using var reviewer = await Client(factory, f, ReviewerId);
        var author = delegated ? proxy : owner;
        foreach (var project in new[] { Guid.Parse("71000000-0000-0000-0000-000000000012"), OtherProjectId })
        {
            using var cross = await Send(author, HttpMethod.Post, "requests", new { input = Input() with { Scope = new(WorkspaceId, project) }, meta = Meta() });
            Assert.Equal(HttpStatusCode.Forbidden, cross.StatusCode);
        }
        var row = await Create(author);
        Assert.Equal(AuthorPersonId, row.OwnerPersonId);
        Assert.Equal(delegated ? RecipientPersonId : (Guid?)null, row.DelegatePersonId);
        if (delegated)
        {
            row = await Command(proxy, $"/{row.Id}/send-to-owner", Meta(row.Version));
            var quote = await Get(owner, $"/{row.Id}/quote");
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(proxy, HttpMethod.Post, $"requests/{row.Id}/confirm",
                new { confirmationHash = quote.GetProperty("confirmationHash").GetString(), meta = Meta(row.Version) })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(proxy, HttpMethod.Post, $"requests/{row.Id}/withdraw", Meta(row.Version))).StatusCode);
        }
        row = await Confirm(owner, row);
        Assert.Equal("PendingApproval", row.State);
        row = await Decide(reviewer, row, "Approve");
        // Delegate is also the configured project manager: retained participation must exclude them.
        Assert.Equal(delegated ? "Held" : "PendingApproval", row.State);
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<AdvanceRequest>().ToArrayAsync());
        Assert.Equal(delegated ? 3 : 2, await db.Set<AdvanceRequestParticipant>().CountAsync());
        await NoPaymentEffects(f);
    }

    [Theory]
    [InlineData("precision")]
    [InlineData("overflow")]
    [InlineData("rate")]
    [InlineData("lump")]
    public async Task B02_B03_Invalid_estimates_have_no_partial_effects(string defect)
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys); using var owner = await Client(factory, f, AuthorId);
        var input = Input(); var line = input.Content.Lines[0];
        line = defect switch
        {
            "precision" => line with { Quantity = "1.23456" },
            "overflow" => line with { Quantity = "999999999999999999999999999999999999999" },
            "rate" => line with { UnitRate = "0.01" },
            _ => line with { Mode = "LumpSum", Quantity = null, LumpSum = "0.01" }
        };
        var before = await BusinessSnapshot(f);
        using var response = await Send(owner, HttpMethod.Post, "requests", new { input = input with { Content = input.Content with { Lines = [line] } }, meta = Meta() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await BusinessSnapshot(f));
    }

    [Fact]
    public async Task B02_B03_Rounds_each_line_before_sum_and_supports_both_nonstandard_modes()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys); using var owner = await Client(factory, f, AuthorId);
        var active = Assert.Single((await owner.GetFromJsonAsync<PolicyView[]>($"/api/v1/field-advances/policies?workspaceId={WorkspaceId}"))!);
        var other = Guid.NewGuid();
        var policy = P.Content() with { Categories = [new(Category, "มาตรฐาน", true, "หน่วย", 10m, ["receipt"]), new(other, "อิสระ", true, "หน่วย", null, ["receipt"])] };
        await P.Review(f, await P.Submit(f, await P.Create(f, source: active, active: active, content: policy)));
        var input = Input();
        input = input with { Content = input.Content with { Lines = [input.Content.Lines[0], input.Content.Lines[0] with { Id = Guid.NewGuid() }] } };
        var row = await Create(owner, input);
        Assert.Equal(24.70m, row.Estimate!.Total); Assert.All(row.Estimate.Lines, l => Assert.Equal(12.35m, l.Amount));
        input = Input() with
        {
            Content = Input().Content with
            {
                Lines =
            [new(Guid.NewGuid(), other, "เหมา", "LumpSum", null, null, "10.01"), new(Guid.NewGuid(), other, "อัตราเอง", "QuantityRate", "1.2345", "10.00", null)]
            }
        };
        row = await Create(owner, input);
        Assert.Equal(22.36m, row.Estimate!.Total);
        Assert.Equal(new[] { 10.01m, 12.35m }, row.Estimate.Lines.Select(x => x.Amount));
    }

    [Fact]
    public async Task B05_B12_Final_HTTP_approval_races_with_withdrawal_and_has_exactly_one_effect()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        await using var db = f.CreateContext(); var directorPerson = Guid.NewGuid();
        db.Add(new FinancePerson { Id = directorPerson, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "ทดสอบผู้อนุมัติสุดท้าย" });
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = directorPerson,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            Reason = "บุคคลจำลองแยกกัน"
        });
        await db.SaveChangesAsync();
        var grant = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, directorPerson);
        await db.Set<GrantProposal>().Where(x => db.Set<GrantAssignment>().Any(a => a.Id == grant && a.ProposalId == x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve", "report.read" })
                .SetProperty(p => p.ApprovalLevels, new[] { "director" }).SetProperty(p => p.PrimaryApprovalLevels, new[] { "director" }));
        using var owner = await Client(factory, f, AuthorId); using var supervisor = await Client(factory, f, ReviewerId);
        using var manager = await Client(factory, f, RecipientId); using var director = await Client(factory, f, AliasAccountId);
        var row = await Confirm(owner, await Create(owner));
        row = await Decide(supervisor, row, "Approve"); row = await Decide(manager, row, "Approve");
        var current = await Get(director, $"/{row.Id}");
        var approve = new { decision = new RequestDecision("Approve", current.GetProperty("currentStep").GetProperty("id").GetGuid()), meta = Meta(row.Version) };
        var withdraw = Meta(row.Version);
        var responses = await Task.WhenAll(Send(director, HttpMethod.Post, $"requests/{row.Id}/review", approve),
            Send(owner, HttpMethod.Post, $"requests/{row.Id}/withdraw", withdraw));
        try
        {
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, x => x.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Conflict);
            var saved = await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync();
            Assert.Equal(row.Version + 1, saved.Version); Assert.Contains(saved.State, new[] { "Approved", "Withdrawn" });
            Assert.Equal(2, await db.Set<AdvanceApprovalStep>().CountAsync(x => x.RequestId == row.Id && x.StepOrder < 3 && x.Outcome == "Approved"));
            Assert.Single(await db.Set<FinanceCommandReceipt>().Where(x => x.CommandId == approve.meta.CommandId || x.CommandId == withdraw.CommandId).ToArrayAsync());
            Assert.Single(await db.Set<AdvanceRequestEvent>().Where(x => x.CommandId == approve.meta.CommandId || x.CommandId == withdraw.CommandId).ToArrayAsync());
            var before = await BusinessSnapshot(f);
            if (responses[0].IsSuccessStatusCode) await Command(director, $"/{row.Id}/review", approve);
            else await Command(owner, $"/{row.Id}/withdraw", withdraw);
            Assert.Equal(before, await BusinessSnapshot(f)); await NoPaymentEffects(f);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task B06_B07_Retained_checker_excludes_new_revision_even_after_regrant_to_an_alias()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId); using var reviewer = await Client(factory, f, ReviewerId);
        using var manager = await Client(factory, f, RecipientId);
        var first = await Confirm(owner, await Create(owner)); var voted = await Decide(reviewer, first, "Approve");
        var returned = await Decide(manager, voted, "Return");
        var revised = await Command(owner, $"/{first.Id}/revise", Meta(returned.Version));
        await using var db = f.CreateContext();
        db.Add(new AdvanceRequestParticipant
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            ProjectId = ProjectId,
            RequestId = first.Id,
            PersonId = ReviewerPersonId,
            Role = "Checker",
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow()
        });
        db.Add(new FinanceAccountLink
        {
            Id = Guid.NewGuid(),
            WorkspaceId = WorkspaceId,
            UserId = AliasAccountId,
            PersonId = ReviewerPersonId,
            CreatedBy = AuthorId,
            CreatedAtUtc = f.Clock.GetUtcNow(),
            Reason = "certified alias fixture"
        });
        await db.SaveChangesAsync();
        await db.Set<GrantAssignment>().Where(x => db.Set<GrantProposal>().Any(p => p.Id == x.ProposalId && p.ProjectId == ProjectId && p.RecipientId == ReviewerId))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAtUtc, f.Clock.GetUtcNow()).SetProperty(p => p.RevokedBy, AuthorId).SetProperty(p => p.RevocationReason, "ทดสอบถอน"));
        var assignment = await FieldAdvanceGrantTests.AuthorityAsync(f, AliasAccountId, ReviewerPersonId);
        await db.Set<GrantProposal>().Where(x => db.Set<GrantAssignment>().Any(a => a.Id == assignment && a.ProposalId == x.Id))
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve" }).SetProperty(p => p.ApprovalLevels, new[] { "supervisor" }));
        using var alias = await Client(factory, f, AliasAccountId);
        var frozen = await L.Frozen(f, first.RevisionId);
        var second = await Confirm(owner, revised); Assert.Equal("Held", second.State);
        var step = await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.RevisionId == second.RevisionId && x.StepOrder == 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(alias, HttpMethod.Post, $"requests/{first.Id}/review",
            new { decision = new RequestDecision("Approve", step.Id), meta = Meta(second.Version) })).StatusCode);
        Assert.Equal(frozen, await L.Frozen(f, first.RevisionId));
        Assert.Single(await db.Set<AdvanceRequestParticipant>().Where(x => x.PersonId == ReviewerPersonId && x.Role == "Approver").ToArrayAsync());
        Assert.Equal("Approved", (await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.RevisionId == first.RevisionId && x.StepOrder == 1)).Outcome);
    }

    [Fact]
    public async Task B05_B08_B09_Held_after_first_approval_resumes_same_step_after_three_person_HTTP_grant()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId); using var reviewer = await Client(factory, f, ReviewerId);
        using var manager = await Client(factory, f, RecipientId);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId && x.RecipientId == RecipientId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.EndsAtUtc, f.Clock.GetUtcNow()));
        foreach (var actor in new[] { AuthorId, ReviewerId })
        {
            var grant = await db.Set<GrantProposal>().SingleAsync(x => x.ProjectId == ProjectId && x.RecipientId == actor);
            grant.Capabilities = [.. grant.Capabilities, actor == AuthorId ? "grant.propose" : "grant.review"];
            grant.DelegableCapabilities = ["request.approve"];
            grant.DelegableApprovalLevels = ["project-manager"];
            grant.DelegablePrimaryApprovalLevels = ["project-manager"];
        }
        await db.SaveChangesAsync();
        var row = await Confirm(owner, await Create(owner)); row = await Decide(reviewer, row, "Approve");
        Assert.Equal("Held", row.State);
        var completed = JsonSerializer.Serialize(await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.RequestId == row.Id && x.StepOrder == 1));
        var pending = await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.RequestId == row.Id && x.StepOrder == 2);
        var frozen = await L.Frozen(f, row.RevisionId);
        var spec = new TPR10.Api.FieldAdvances.Grants.GrantHttpSpec(RecipientId, new(WorkspaceId, ProjectId), ["request.approve"], [], 1000m, f.Clock.GetUtcNow(), null)
        { ApprovalLevels = ["project-manager"], PrimaryApprovalLevels = ["project-manager"] };
        using var proposed = await Send(owner, HttpMethod.Post, "grants", new { spec, meta = new CommandMeta(Guid.NewGuid(), 1, "แก้สิทธิ์ผู้จัดการ") });
        Assert.Equal(HttpStatusCode.Created, proposed.StatusCode);
        var proposal = await proposed.Content.ReadFromJsonAsync<JsonElement>();
        using var approved = await Send(reviewer, HttpMethod.Post, $"grants/{proposal.GetProperty("id").GetGuid()}/review",
            new { decision = "approve", meta = new CommandMeta(Guid.NewGuid(), proposal.GetProperty("version").GetInt64(), "สามบุคคลต่างกัน") });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        await RouteUntil(factory, f, async () => (await db.Set<AdvanceRequest>().AsNoTracking().SingleAsync(x => x.Id == row.Id)).State == "PendingApproval");
        var current = await Get(manager, $"/{row.Id}");
        Assert.Equal(pending.Id, current.GetProperty("currentStep").GetProperty("id").GetGuid());
        Assert.Equal(RecipientPersonId, current.GetProperty("currentStep").GetProperty("assigneePersonId").GetGuid());
        Assert.Null((await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.Id == pending.Id)).Outcome);
        Assert.Equal(completed, JsonSerializer.Serialize(await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.RequestId == row.Id && x.StepOrder == 1)));
        Assert.Equal(frozen, await L.Frozen(f, row.RevisionId));
        Assert.Contains((await Get(owner, $"/{row.Id}/history")).EnumerateArray(), x => x.GetProperty("action").GetString() == "field-advance.request.reevaluate");
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("receipt")]
    [InlineData("commit")]
    public async Task B13_HTTP_failure_rolls_back_audit_receipt_assignments_history_and_retry_commits_once(string failure)
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId); using var reviewer = await Client(factory, f, ReviewerId);
        var row = await Confirm(owner, await Create(owner));
        var current = await Get(reviewer, $"/{row.Id}");
        var body = new { decision = new RequestDecision("Approve", current.GetProperty("currentStep").GetProperty("id").GetGuid()), meta = Meta(row.Version) };
        var before = await BusinessSnapshot(f);
        await using var db = f.CreateContext();
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION delivery_fail() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'delivery-injected'; END $$;");
        await db.Database.ExecuteSqlRawAsync(failure switch
        {
            "audit" => "CREATE TRIGGER delivery_fail BEFORE INSERT ON audit_events FOR EACH ROW EXECUTE FUNCTION delivery_fail();",
            "receipt" => "CREATE TRIGGER delivery_fail AFTER INSERT ON field_advance_command_receipts FOR EACH ROW EXECUTE FUNCTION delivery_fail();",
            _ => "CREATE CONSTRAINT TRIGGER delivery_fail AFTER INSERT ON field_advance_command_receipts DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION delivery_fail();"
        });
        using var failed = await Send(reviewer, HttpMethod.Post, $"requests/{row.Id}/review", body);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(before, await BusinessSnapshot(f));
        await db.Database.ExecuteSqlRawAsync("DROP FUNCTION delivery_fail() CASCADE");
        var success = await Command(reviewer, $"/{row.Id}/review", body);
        var after = await BusinessSnapshot(f);
        Assert.Equal(JsonSerializer.Serialize(success), JsonSerializer.Serialize(await Command(reviewer, $"/{row.Id}/review", body)));
        Assert.Equal(after, await BusinessSnapshot(f));
        Assert.Single(await db.Set<FinanceCommandReceipt>().Where(x => x.CommandId == body.meta.CommandId).ToArrayAsync());
        await NoPaymentEffects(f);
    }

    [Fact]
    public async Task B11_B12_B14_Withdraw_replay_conflict_and_HTTP_revocation_preserve_history()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId); using var reviewer = await Client(factory, f, ReviewerId);
        var row = await Confirm(owner, await Create(owner)); var meta = Meta(row.Version);
        var closed = await Command(owner, $"/{row.Id}/withdraw", meta);
        Assert.Equal("Withdrawn", closed.State);
        Assert.Equal(JsonSerializer.Serialize(closed), JsonSerializer.Serialize(await Command(owner, $"/{row.Id}/withdraw", meta)));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(owner, HttpMethod.Post, $"requests/{row.Id}/withdraw", meta with { Reason = "changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Post, $"requests/{row.Id}/revise", Meta(closed.Version))).StatusCode);
        var copy = await Command(owner, $"/{row.Id}/copy", Meta(closed.Version), HttpStatusCode.Created);
        Assert.NotEqual(row.Id, copy.Id); Assert.Equal("Draft", copy.State);
        await using var db = f.CreateContext();
        var source = await db.Set<GrantProposal>().SingleAsync(x => x.ProjectId == ProjectId && x.RecipientId == ReviewerId);
        source.Capabilities = [.. source.Capabilities, "grant.revoke"]; source.DelegableCapabilities = ["request.create"];
        // The shared authority fixture seeds unrelated delegable rights. This owner has only create.
        var target = await db.Set<GrantProposal>().SingleAsync(x => x.ProjectId == ProjectId && x.RecipientId == AuthorId);
        target.DelegableCapabilities = [];
        await db.SaveChangesAsync();
        var assignment = await (from a in db.Set<GrantAssignment>()
                                join p in db.Set<GrantProposal>() on a.ProposalId equals p.Id
                                where p.ProjectId == ProjectId && p.RecipientId == AuthorId
                                select a).SingleAsync();
        using var revoked = await Send(reviewer, HttpMethod.Post, $"grants/{assignment.Id}/revoke", new CommandMeta(Guid.NewGuid(), assignment.Version, "ถอนสิทธิ์ทดสอบ"));
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var before = await BusinessSnapshot(f);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Post, $"requests/{row.Id}/withdraw", meta)).StatusCode);
        foreach (var suffix in new[] { "", "/screen", "/history", "/revisions", $"/revisions/{row.RevisionId}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Root}/{row.Id}{suffix}")).StatusCode);
        Assert.Equal(before, await BusinessSnapshot(f));
        Assert.Empty(await db.Set<AdvanceApprovalStep>().Where(x => x.RequestId == copy.Id).ToArrayAsync());
    }

    [Fact]
    public async Task B10_HTTP_rejection_is_permanent_and_copy_does_not_inherit_decisions()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId); using var reviewer = await Client(factory, f, ReviewerId);
        var row = await Confirm(owner, await Create(owner));
        var current = await Get(reviewer, $"/{row.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(reviewer, HttpMethod.Post, $"requests/{row.Id}/review",
            new { decision = new RequestDecision("Reject", current.GetProperty("currentStep").GetProperty("id").GetGuid()), meta = Meta(row.Version) with { Reason = " " } })).StatusCode);
        row = await Decide(reviewer, row, "Reject"); Assert.Equal("Rejected", row.State);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(owner, HttpMethod.Post, $"requests/{row.Id}/revise", Meta(row.Version))).StatusCode);
        var evidence = await Get(owner, $"/{row.Id}/revisions/{row.RevisionId}");
        var copy = await Command(owner, $"/{row.Id}/copy", Meta(row.Version), HttpStatusCode.Created);
        Assert.Equal("Draft", copy.State); Assert.NotEqual(row.Id, copy.Id);
        Assert.Equal(evidence.GetRawText(), (await Get(owner, $"/{row.Id}/revisions/{row.RevisionId}")).GetRawText());
        await using var db = f.CreateContext();
        Assert.Equal(row.Id, (await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == copy.Id)).SourceRequestId);
        Assert.Empty(await db.Set<AdvanceApprovalStep>().Where(x => x.RequestId == copy.Id).ToArrayAsync());
        Assert.Null((await db.Set<AdvanceRequestRevision>().SingleAsync(x => x.Id == copy.RevisionId)).SubmittedAtUtc);
    }

    [Fact]
    public async Task B16_Disposable_7A_backup_upgrade_preserves_all_legacy_rows_and_failed_migration_is_atomic()
    {
        await using var source = new IdentityDatabase(postgres.ConnectionString); await source.InitializeAsync();
        await using var target = new IdentityDatabase(postgres.ConnectionString); await target.InitializeAsync();
        await using var old = source.CreateContext();
        await old.GetService<IMigrator>().MigrateAsync(FieldAdvanceRequestFixture.PreviousMigration);
        await SeedDirectoryAsync(old);
        var seed = new FieldAdvanceFixture(source); await seed.SeedPoliciesAsync();
        await old.Database.ExecuteSqlRawAsync("""
            INSERT INTO field_advance_grant_proposals
              (id,workspace_id,project_id,recipient_id,recipient_person_id,proposed_by,proposed_by_person_id,state,capabilities,delegable_capabilities,approval_limit,starts_at_utc,reason)
            VALUES ('74000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000001',NULL,
              '71000000-0000-0000-0000-000000000033','72000000-0000-0000-0000-000000000033',
              '71000000-0000-0000-0000-000000000031','72000000-0000-0000-0000-000000000031',
              'Approved',ARRAY['report.read'],ARRAY[]::text[],1234.56,'2026-01-01T00:00:00Z','legacy grant');
            INSERT INTO field_advance_grant_reviews(id,workspace_id,proposal_id,reviewer_id,reviewer_person_id,outcome,reason)
            VALUES ('74000000-0000-0000-0000-000000000002','71000000-0000-0000-0000-000000000001',
              '74000000-0000-0000-0000-000000000001','71000000-0000-0000-0000-000000000032',
              '72000000-0000-0000-0000-000000000032','Approved','legacy review');
            INSERT INTO field_advance_grant_assignments(id,workspace_id,project_id,proposal_id,review_id,created_by,reason)
            VALUES ('74000000-0000-0000-0000-000000000003','71000000-0000-0000-0000-000000000001',NULL,
              '74000000-0000-0000-0000-000000000001','74000000-0000-0000-0000-000000000002',
              '71000000-0000-0000-0000-000000000032','legacy assignment');
            """);
        var columns = await old.Database.SqlQueryRaw<string>("""
            SELECT table_name || ':' || string_agg(quote_ident(column_name), ',' ORDER BY ordinal_position) AS "Value"
            FROM information_schema.columns WHERE table_schema='public' AND table_name <> '__EFMigrationsHistory'
            GROUP BY table_name ORDER BY table_name
            """).ToArrayAsync();
        static async Task<string[]> Rows(TPR10.Api.Data.Tpr10DbContext db, string[] columns)
        {
            var rows = new List<string>();
            foreach (var entry in columns)
            {
                var split = entry.IndexOf(':'); var table = entry[..split]; var projection = entry[(split + 1)..];
                // Identifiers come exclusively from this disposable database's schema, quoted by PostgreSQL.
#pragma warning disable EF1002 // Catalog-only identifiers; columns use quote_ident and table name is escaped above.
                var json = await db.Database.SqlQueryRaw<string>($"SELECT coalesce(jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text), '[]'::jsonb)::text AS \"Value\" FROM (SELECT {projection} FROM \"{table.Replace("\"", "\"\"")}\") r").SingleAsync();
#pragma warning restore EF1002
                rows.Add(table + ":" + json);
            }
            return rows.ToArray();
        }
        var before = await Rows(old, columns);
        await postgres.BackupRestoreAsync(source.ConnectionString, target.ConnectionString);
        await using var db = target.CreateContext();
        Assert.Equal(before, await Rows(db, columns));
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION delivery_migration_fail() RETURNS event_trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF EXISTS(SELECT 1 FROM pg_event_trigger_ddl_commands() WHERE object_identity='public.field_advance_request_revisions') THEN
                RAISE EXCEPTION 'delivery migration rollback';
              END IF;
            END $$;
            CREATE EVENT TRIGGER delivery_migration_fail ON ddl_command_end WHEN TAG IN ('CREATE TABLE') EXECUTE FUNCTION delivery_migration_fail();
            """);
        var migrator = db.GetService<IMigrator>();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync());
        Assert.Equal("P0001", error.SqlState);
        Assert.Equal(before, await Rows(db, columns));
        Assert.Equal(FieldAdvanceRequestFixture.PreviousMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.field_advance_requests') IS NOT NULL AS \"Value\"").SingleAsync());
        await db.Database.ExecuteSqlRawAsync("DROP EVENT TRIGGER delivery_migration_fail; DROP FUNCTION delivery_migration_fail();");
        await migrator.MigrateAsync();
        Assert.Equal(before, await Rows(db, columns));
        Assert.Empty(await db.Set<AdvanceRequest>().ToArrayAsync());
        var grant = await db.Set<GrantProposal>().SingleAsync();
        Assert.Equal(1234.56m, grant.ApprovalLimit); Assert.Empty(grant.ApprovalLevels);
        Assert.Single(await db.Set<GrantAssignment>().ToArrayAsync());
        Assert.Equal(2, await db.Set<PolicyRevision>().CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var script = migrator.GenerateScript(FieldAdvanceRequestFixture.PreviousMigration, options: MigrationsSqlGenerationOptions.Idempotent);
        await using (var connection = new Npgsql.NpgsqlConnection(target.ConnectionString))
        {
            await connection.OpenAsync();
            // Execute generated SQL verbatim; EF's raw-command formatter interprets SQL braces.
            await using var command = new Npgsql.NpgsqlCommand(script, connection);
            await command.ExecuteNonQueryAsync(); await command.ExecuteNonQueryAsync();
        }
        Assert.Equal(before, await Rows(db, columns));
        var expectedMigrations = db.Database.GetMigrations().Where(x => StringComparer.Ordinal.Compare(x, FieldAdvanceRequestFixture.PreviousMigration) > 0).ToArray();
        Assert.Equal(expectedMigrations, (await db.Database.GetAppliedMigrationsAsync()).Where(x => StringComparer.Ordinal.Compare(x, FieldAdvanceRequestFixture.PreviousMigration) > 0).ToArray());
        output.WriteLine($"7A backup/restore -> 7B: {columns.Length} legacy tables compared using all original columns; 2 policies, 1 grant/review/assignment; injected DDL failure rolled back; idempotent SQL replayed twice; no pending model changes.");
    }

    [Fact]
    public async Task New_HTTP_read_path_measurement_keeps_paged_queries_independent_of_unpaged_requests()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        var counter = new DeliveryQueries(); await using var factory = Factory(f, keys, counter);
        using var owner = await Client(factory, f, AuthorId); RequestView? first = null;
        var csrf = await IdentityTestDriver.TokenAsync(owner);
        var counts = new List<int>();
        foreach (var size in new[] { 4, 20 })
        {
            // Separate setup batches across the existing limiter's window using the fixture clock.
            if (size == 20) f.Clock.Advance(TimeSpan.FromMinutes(2));
            for (var i = size == 4 ? 0 : 4; i < size; i++)
            {
                // Reuse a valid CSRF token, as a browser does, without changing rate-limit policy.
                using var request = IdentityTestDriver.Mutation(csrf, Root);
                request.Content = JsonContent.Create(new { input = Input(), meta = Meta() });
                using var response = await owner.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                first ??= (await response.Content.ReadFromJsonAsync<RequestView>())!;
            }
            var before = await BusinessSnapshot(f);
            counter.Count = 0; counter.Capture = true; var timer = Stopwatch.StartNew();
            var rows = await Get(owner, $"?{Query}&mode=mine&pageSize=1");
            await Get(owner, $"/{first!.Id}/screen");
            await Get(owner, $"/{first.Id}/history?pageSize=1");
            await Get(owner, $"/{first.Id}/revisions?pageSize=1");
            await Get(owner, $"/{first.Id}/revisions/{first.RevisionId}");
            timer.Stop(); counter.Capture = false;
            Assert.Single(rows.EnumerateArray()); counts.Add(counter.Count);
            Assert.Equal(before, await BusinessSnapshot(f));
            output.WriteLine($"request HTTP rows={size}; pageSize=1; five GETs; queries={counter.Count}; elapsed-ms={timer.Elapsed.TotalMilliseconds:F1}; includes session/current-authority locks, no concurrent load; NOT SLA/M2 closure");
        }
        Assert.True(counts[1] <= counts[0] + 2, $"Unpaged request query amplification {counts[0]} -> {counts[1]}");
    }
    private sealed class DeliveryQueries : DbCommandInterceptor
    {
        public bool Capture { get; set; }
        public int Count { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (Capture) Count++; return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Capture) Count++; return ValueTask.FromResult(result); }
    }

    private static async Task<string> BusinessSnapshot(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        return JsonSerializer.Serialize(new
        {
            requests = await db.Set<AdvanceRequest>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            revisions = await db.Set<AdvanceRequestRevision>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            lines = await db.Set<AdvanceRequestLine>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            steps = await db.Set<AdvanceApprovalStep>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            participants = await db.Set<AdvanceRequestParticipant>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            receipts = await db.Set<FinanceCommandReceipt>().AsNoTracking().OrderBy(x => x.CommandId).ToArrayAsync(),
            events = await db.Set<AdvanceRequestEvent>().AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            audit = await db.AuditEvents.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
            metadata = await db.AuditMetadata.AsNoTracking().OrderBy(x => x.AuditEventId).ThenBy(x => x.Key).ToArrayAsync()
        });
    }
    private static async Task NoPaymentEffects(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        Assert.Empty(await db.Set<AdvanceInstallment>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvancePaymentAttempt>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvancePaymentResult>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvanceReservationEntry>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvancePaymentProposal>().ToArrayAsync());
        Assert.Empty(await db.Set<AdvancePaymentEvent>().ToArrayAsync());
        Assert.All(await db.Set<AdvancePaymentAccount>().ToArrayAsync(), account =>
        {
            Assert.Equal(0m, account.Reserved);
            Assert.Equal(0m, account.Paid);
            Assert.False(account.EverPaid);
        });
    }

    // WB-I1: removing current-route validation leaks reads while the worker has not assigned B.
    [Fact]
    public async Task B08_B14_Former_route_holder_loses_reads_before_worker_but_candidate_waits_for_assignment()
    {
        output.WriteLine("WB-I1: fresh former-holder session; routing interval 3600 seconds until explicit worker run");
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId);
        using var oldSession = await Client(factory, f, ReviewerId);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId && x.RecipientId == ReviewerId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve" }));
        await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId && x.RecipientId == RecipientId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve" })
                .SetProperty(p => p.ApprovalLevels, new[] { "supervisor", "project-manager" }));
        var draft = await Create(owner); var confirmMeta = Meta(draft.Version);
        var quote = await Get(owner, $"/{draft.Id}/quote");
        var body = new { confirmationHash = quote.GetProperty("confirmationHash").GetString(), meta = confirmMeta };
        var row = await Command(owner, $"/{draft.Id}/confirm", body);
        Assert.Equal("PendingApproval", row.State);
        var firstView = await Get(oldSession, $"/{row.Id}");
        Assert.Equal(ReviewerPersonId, firstView.GetProperty("currentStep").GetProperty("assigneePersonId").GetGuid());
        var previousDecision = new { decision = new RequestDecision("Approve", firstView.GetProperty("currentStep").GetProperty("id").GetGuid()), meta = Meta(row.Version) };
        row = await Command(oldSession, $"/{row.Id}/review", previousDecision);
        using (var manager = await Client(factory, f, RecipientId)) row = await Decide(manager, row, "Return");
        row = await Confirm(owner, await Command(owner, $"/{row.Id}/revise", Meta(row.Version)));
        // Directory mutation committed; emulate its session invalidation and authenticate A afresh.
        await db.Set<ReportingLine>().Where(x => x.EmployeeUserId == AuthorId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.SupervisorUserId, RecipientId));
        await db.Set<IdentitySession>().Where(x => x.UserId == ReviewerId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.RevokedAtUtc, f.Clock.GetUtcNow()));
        using var former = await Client(factory, f, ReviewerId);
        using var candidate = await Client(factory, f, RecipientId);
        foreach (var client in new[] { former, candidate })
        {
            foreach (var suffix in new[] { "", "/screen", "/history", "/revisions", $"/revisions/{row.RevisionId}" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Root}/{row.Id}{suffix}")).StatusCode);
            Assert.Empty((await Get(client, $"/review-queue?{Query}")).EnumerateArray());
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(former, HttpMethod.Post, $"requests/{row.Id}/review", previousDecision)).StatusCode);
        Assert.Equal(row.Id, (await Get(owner, $"/{row.Id}")).GetProperty("id").GetGuid());
        // An independent report right remains valid, and does not authorize a stale decision.
        await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId && x.RecipientId == ReviewerId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, new[] { "request.approve", "report.read" }));
        await Get(former, $"/{row.Id}/history");
        var step = await db.Set<AdvanceApprovalStep>().SingleAsync(x => x.RevisionId == row.RevisionId && x.StepOrder == 1);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(former, HttpMethod.Post, $"requests/{row.Id}/review",
            new { decision = new RequestDecision("Approve", step.Id), meta = Meta(row.Version) })).StatusCode);
        await RouteUntil(factory, f, async () => (await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.Id == step.Id)).AssigneePersonId == RecipientPersonId);
        Assert.Equal(row.Id, (await Get(candidate, $"/{row.Id}")).GetProperty("id").GetGuid());
        Assert.Null((await db.Set<AdvanceApprovalStep>().AsNoTracking().SingleAsync(x => x.Id == step.Id)).Outcome);
    }

    // WB-I2: current-only projection loses P1/lines/decisions after the third revision.
    [Fact]
    public async Task B04_B07_B10_B15_Three_revisions_keep_first_policy_lines_and_outcomes_readable_with_current_authority()
    {
        await using var f = await L.Ready(postgres); using var keys = new TestKeyMaterial();
        await using var factory = Factory(f, keys);
        using var owner = await Client(factory, f, AuthorId); using var reviewer = await Client(factory, f, ReviewerId);
        using var outsider = await Client(factory, f, AliasAccountId);
        var first = await Confirm(owner, await Create(owner));
        var returned = await Decide(reviewer, first, "Return");
        var evidence = await Get(owner, $"/{first.Id}/revisions/{first.RevisionId}");
        Assert.Equal(1, evidence.GetProperty("revisionNumber").GetInt32());
        Assert.Equal("123.45", evidence.GetProperty("estimate").GetProperty("total").GetString());
        Assert.Equal("100.00", evidence.GetProperty("estimate").GetProperty("lines")[0].GetProperty("unitRate").GetString());
        Assert.Equal(7, evidence.GetProperty("frozenPolicy").GetProperty("content").GetProperty("clearingDays").GetInt32());
        Assert.Equal("Returned", evidence.GetProperty("steps")[0].GetProperty("outcome").GetString());
        Assert.Equal(ReviewerPersonId, evidence.GetProperty("steps")[0].GetProperty("decidedByPersonId").GetGuid());
        var policies = await owner.GetFromJsonAsync<PolicyView[]>($"/api/v1/field-advances/policies?workspaceId={WorkspaceId}");
        var active = Assert.Single(policies!);
        var content = P.Content(14) with { Categories = P.Content().Categories.Select(c => c with { StandardRate = 200m }).ToArray() };
        var createMeta = new CommandMeta(Guid.NewGuid(), 1, "เปลี่ยนกติกาจำลอง")
        { Policy = new("Company", active.Id, null, active.Id, 1) };
        using var created = await Send(owner, HttpMethod.Post, "policies", new CreatePolicyRequest(new(WorkspaceId, null), null, content, createMeta));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var policy = (await created.Content.ReadFromJsonAsync<PolicyView>())!;
        using var sent = await Send(owner, HttpMethod.Post, $"policies/{policy.Id}/submit", P.Meta(policy));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode); policy = (await sent.Content.ReadFromJsonAsync<PolicyView>())!;
        using var approved = await Send(reviewer, HttpMethod.Post, $"policies/{policy.Id}/review", new ReviewPolicyRequest("approve", P.Meta(policy)));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var second = await Confirm(owner, await Command(owner, $"/{first.Id}/revise", Meta(returned.Version)));
        Assert.Equal(246.90m, second.Estimate!.Total);
        Assert.Equal(14, (await Get(owner, $"/{first.Id}/revisions/{second.RevisionId}")).GetProperty("frozenPolicy").GetProperty("content").GetProperty("clearingDays").GetInt32());
        returned = await Decide(reviewer, second, "Return");
        var third = await Command(owner, $"/{first.Id}/revise", Meta(returned.Version));
        Assert.NotEqual(first.RevisionId, third.RevisionId);
        Assert.Equal(evidence.GetRawText(), (await Get(owner, $"/{first.Id}/revisions/{first.RevisionId}")).GetRawText());
        var rows = await Get(owner, $"/{first.Id}/revisions?pageSize=2");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Single((await Get(owner, $"/{first.Id}/revisions?pageSize=2&page=2")).EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync($"{Root}/{first.Id}/revisions?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"{Root}/{first.Id}/revisions/{first.RevisionId}")).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(2)); // New setup batch; retain the production CSRF limiter.
        var unrelated = await Create(owner);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Root}/{unrelated.Id}/revisions/{first.RevisionId}")).StatusCode);
        await using var db = f.CreateContext();
        await db.Set<GrantProposal>().Where(x => x.ProjectId == ProjectId && x.RecipientId == AuthorId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Capabilities, Array.Empty<string>()));
        foreach (var suffix in new[] { "/revisions", $"/revisions/{first.RevisionId}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync($"{Root}/{first.Id}{suffix}")).StatusCode);
    }
}

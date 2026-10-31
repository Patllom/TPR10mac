using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.FieldAdvances;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceInstallmentTests(PostgresFixture postgres)
{
    private static readonly Guid CheckerPerson = Guid.Parse("72000000-0000-0000-0000-000000000044");
    // A missing registration prevents the application from preparing or reviewing installments.
    [Theory]
    [InlineData("InstallmentService")]
    [InlineData("InstallmentReviewService")]
    public void Installment_workflows_are_available_to_the_application(string name)
    {
        var services = new ServiceCollection();
        services.AddFieldAdvances();
        Assert.Contains(services, x => x.ServiceType.FullName == "TPR10.Api.FieldAdvances.Payments." + name);
    }

    // C02: releasing all funds on Return, or reserving the full new amount on edit, breaks these balances.
    [Fact]
    public async Task ReturnedKeepsReservation()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var draft = Value(await Call(f, request, null, "Create", Input("6000")));
        Assert.Equal(0m, draft.Reserved);
        var id = draft.InstallmentId!.Value;
        Assert.Equal(6000m, Value(await Call(f, request, id, "Submit")).Reserved);
        var view = Value(await Call(f, request, id, "Return", actor: AliasAccountId));
        Assert.Equal("Returned", view.State); Assert.Equal(6000m, view.Reserved);
        view = Value(await Call(f, request, id, "Update", Input("4000")));
        Assert.Equal(4000m, view.Reserved); Assert.NotEqual("Reviewed", view.State);
        Assert.Equal(409, (await Call(f, request, id, "Update", Input("11000"))).Status);
        view = Value(await Call(f, request, id, "Update", Input("7000")));
        Assert.Equal(7000m, view.Reserved);
        var meta = await Meta(f, request, id);
        var submitted = await Call(f, request, id, "Submit", meta: meta);
        Assert.Equal(7000m, Value(submitted).Reserved);
        Assert.Equal(JsonSerializer.Serialize(submitted), JsonSerializer.Serialize(await Call(f, request, id, "Submit", meta: meta)));
        Assert.Equal(409, (await Call(f, request, id, "Submit")).Status);
        await using var db = f.CreateContext();
        Assert.Equal(new[] { "Reserve:6000", "Release:2000", "Reserve:3000" },
            (await db.Set<AdvanceReservationEntry>().OrderBy(x => x.CreatedAtUtc).ToArrayAsync())
                .Select(x => x.Action + ":" + x.Amount.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(0m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Paid);
    }

    // 7C-T3-M1: creation must traverse the executor with null installment ID, persist and replay the generated ID.
    [Fact]
    public async Task Create_new_installment_persists_versions_receipt_and_replays_without_reserving()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var meta = await Meta(f, request, null); var input = Input("6000");
        var result = await Call(f, request, null, "Create", input, meta);
        var view = Value(result);
        Assert.NotNull(view.InstallmentId); Assert.Equal("Draft", view.State);
        Assert.Equal(2L, view.FamilyVersion); Assert.Equal(2L, view.RequestVersion); Assert.Equal(1L, view.InstallmentVersion);
        Assert.Equal(10000m, view.Available); Assert.Equal(0m, view.Reserved); Assert.Equal(0m, view.Paid);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(await Call(f, request, null, "Create", input, meta)));
        Assert.Equal(409, (await Call(f, request, null, "Create", Input("6001"), meta)).Status);
        foreach (var destination in new[] {
            input.Destination with { BankCode = "changed" }, input.Destination with { AccountNumber = "changed" },
            input.Destination with { AccountHolder = "changed" }, input.Destination with { RecipientPersonId = RecipientPersonId },
            new PaymentDestination("Cash", AuthorPersonId, null, null, null) })
            Assert.Equal(409, (await Call(f, request, null, "Create", input with { Destination = destination }, meta)).Status);
        await using var db = f.CreateContext();
        var installment = Assert.Single(await db.Set<AdvanceInstallment>().ToArrayAsync());
        Assert.Equal(view.InstallmentId, installment.Id); Assert.Equal(view.InstallmentVersion, installment.Version);
        Assert.Single(await db.Set<AdvanceInstallmentRevision>().ToArrayAsync());
        var receipt = Assert.Single(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(JsonSerializer.Deserialize<FinanceResult<PaymentView>>(receipt.ResultJson)));
        Assert.Single(await db.Set<AdvancePaymentEvent>().ToArrayAsync());
        var participation = Assert.Single(await db.Set<AdvancePaymentParticipation>().ToArrayAsync());
        Assert.Equal(RecipientId, participation.CreatedBy); Assert.Equal(RecipientPersonId, participation.PersonId);
        Assert.Empty(await db.Set<AdvanceReservationEntry>().ToArrayAsync());
    }

    // C03: even a lower amount or another account for the same owner invalidates approval, preserving the old revision.
    [Theory]
    [InlineData("amount")]
    [InlineData("account")]
    [InlineData("channel")]
    public async Task Editing_reviewed_content_preserves_history_and_requires_review_of_new_revision(string change)
    {
        await using var f = await Ready(); var request = await Approved(f);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        Value(await Call(f, request, id, "Submit"));
        var reviewMeta = await Meta(f, request, id);
        var approval = await Call(f, request, id, "Approve", meta: reviewMeta, actor: AliasAccountId);
        Assert.Equal("Reviewed", Value(approval).State);
        Guid originalId; string originalHash;
        await using (var db = f.CreateContext())
        {
            var old = await db.Set<AdvanceInstallmentRevision>().SingleAsync();
            originalId = old.Id; originalHash = old.ContentHash;
            Assert.Equal(CheckerPerson, old.ReviewedByPersonId); Assert.Equal(AliasAccountId, old.ReviewedBy);
        }
        var edited = change switch
        {
            "amount" => Input("4000"),
            "account" => Input("6000") with { Destination = Input("6000").Destination with { AccountNumber = "second-owner-account" } },
            _ => Input("6000") with { Destination = new("Cash", AuthorPersonId, null, null, null) }
        };
        var view = Value(await Call(f, request, id, "Update", edited));
        Assert.NotEqual("Reviewed", view.State); Assert.Equal(change == "amount" ? 4000m : 6000m, view.Reserved);
        Assert.Equal(409, (await Call(f, request, id, "Approve", meta: reviewMeta with { CommandId = Guid.NewGuid() }, actor: AliasAccountId)).Status);
        Assert.Equal(JsonSerializer.Serialize(approval), JsonSerializer.Serialize(await Call(f, request, id, "Approve", meta: reviewMeta, actor: AliasAccountId)));
        await using (var db = f.CreateContext())
        {
            var revisions = await db.Set<AdvanceInstallmentRevision>().OrderBy(x => x.RevisionNumber).ToArrayAsync();
            Assert.Equal(2, revisions.Length); Assert.False(revisions[0].IsCurrent); Assert.True(revisions[1].IsCurrent);
            Assert.Equal(originalId, revisions[0].Id); Assert.Equal(originalHash, revisions[0].ContentHash);
            Assert.NotEqual(originalHash, revisions[1].ContentHash); Assert.Null(revisions[1].ReviewedBy);
            Assert.Equal(AliasAccountId, revisions[0].ReviewedBy); Assert.All(revisions, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        }
        Value(await Call(f, request, id, "Submit"));
        Assert.Equal("Reviewed", Value(await Call(f, request, id, "Approve", actor: AliasAccountId)).State);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.001")]
    [InlineData("1e3")]
    [InlineData("1000.000")]
    [InlineData("1,000")]
    [InlineData(" 1000")]
    [InlineData("١٠")]
    [InlineData("10000000000000000")]
    [InlineData("79228162514264337593543950336")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Invalid_amount_does_not_create_or_change_installment(string? amount)
    {
        await using var f = await Ready(); var request = await Approved(f);
        Assert.Equal(400, (await Call(f, request, null, "Create", Input(amount!))).Status);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        Assert.Equal(400, (await Call(f, request, id, "Update", Input(amount!))).Status);
        await using var db = f.CreateContext();
        Assert.Single(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
        Assert.Equal(6000m, (await db.Set<AdvanceInstallmentRevision>().SingleAsync()).Amount);
    }

    // Wire boundary: JSON numbers are rejected by the typed installment contract, independent of a future API.
    [Theory]
    [InlineData("1000")]
    [InlineData("1e3")]
    public void Numeric_JSON_cannot_be_deserialized_as_installment_amount(string number)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<InstallmentInput>("{\"Amount\":" + number + ",\"Destination\":null}"));

    [Theory]
    [InlineData("recipient")]
    [InlineData("bank")]
    [InlineData("account")]
    [InlineData("holder")]
    [InlineData("cash-bank")]
    [InlineData("channel")]
    [InlineData("null")]
    public async Task Destination_must_be_complete_and_owner_only(string defect)
    {
        await using var f = await Ready(); var request = await Approved(f); var input = Input("6000");
        input = input with
        {
            Destination = defect switch
            {
                "recipient" => input.Destination with { RecipientPersonId = RecipientPersonId },
                "bank" => input.Destination with { BankCode = " " },
                "account" => input.Destination with { AccountNumber = null },
                "holder" => input.Destination with { AccountHolder = "" },
                "cash-bank" => input.Destination with { Channel = "Cash" },
                "channel" => input.Destination with { Channel = "Other" },
                _ => null!
            }
        };
        Assert.Equal(400, (await Call(f, request, null, "Create", input)).Status);
        await using var db = f.CreateContext(); Assert.Empty(await db.Set<AdvanceInstallment>().ToArrayAsync());
        Assert.Empty(await db.Set<FinanceCommandReceipt>().ToArrayAsync());
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Submit")]
    [InlineData("Approve")]
    [InlineData("Return")]
    public async Task Every_operation_requires_reason(string operation)
    {
        await using var f = await Ready(); var request = await Approved(f);
        Guid? id = operation == "Create" ? null : Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId;
        if (operation is "Approve" or "Return") Value(await Call(f, request, id, "Submit"));
        var meta = (await Meta(f, request, id)) with { Reason = "  " };
        Assert.Equal(400, (await Call(f, request, id, operation, Input("4000"), meta,
            operation is "Approve" or "Return" ? AliasAccountId : RecipientId)).Status);
    }

    [Fact]
    public async Task Stale_update_and_operation_reuse_do_not_overwrite_current_revision()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        var meta = await Meta(f, request, id);
        Value(await Call(f, request, id, "Update", Input("4000"), meta));
        Assert.Equal(409, (await Call(f, request, id, "Update", Input("5000"), meta with { CommandId = Guid.NewGuid() })).Status);
        Assert.Equal(409, (await Call(f, request, id, "Submit", meta: meta)).Status);
        await using var db = f.CreateContext();
        Assert.Equal(4000m, (await db.Set<AdvanceInstallmentRevision>().SingleAsync(x => x.IsCurrent)).Amount);
        Assert.Equal(0m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
    }

    [Theory]
    [InlineData("Paying")]
    [InlineData("OutcomePending")]
    [InlineData("CancelPending")]
    [InlineData("Cancelled")]
    [InlineData("Paid")]
    public async Task Blocked_states_cannot_be_edited_submitted_or_reviewed(string state)
    {
        await using var f = await Ready(); var request = await Approved(f);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        await using (var db = f.CreateContext())
        { (await db.Set<AdvanceInstallment>().SingleAsync()).State = state; await db.SaveChangesAsync(); }
        Assert.Equal(409, (await Call(f, request, id, "Update", Input("4000"))).Status);
        Assert.Equal(409, (await Call(f, request, id, "Submit")).Status);
        Assert.Equal(409, (await Call(f, request, id, "Approve", actor: AliasAccountId)).Status);
        Assert.Equal(409, (await Call(f, request, id, "Return", actor: AliasAccountId)).Status);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("approval")]
    [InlineData("request")]
    public async Task Funds_require_approved_account_and_no_active_hold(string defect)
    {
        await using var f = await Ready(); var request = await Approved(f);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        await using (var db = f.CreateContext())
        {
            var r = await db.Set<AdvanceRequest>().SingleAsync();
            if (defect == "approval")
            {
                var account = await db.Set<AdvancePaymentAccount>().SingleAsync();
                account.ApprovedRevisionId = null; account.Approved = 0; account.Spendable = 0;
            }
            if (defect == "request") r.State = "Returned";
            if (defect == "hold") db.Add(new AdvancePaymentHold
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                ProjectId = ProjectId,
                FamilyId = r.FamilyId,
                Code = "PaymentMismatch",
                Reason = "ทดสอบระงับ",
                CreatedBy = RecipientId,
                CreatedByPersonId = RecipientPersonId,
                CreatedAtUtc = f.Clock.GetUtcNow()
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(409, (await Call(f, request, null, "Create", Input("1000"))).Status);
        Assert.Equal(409, (await Call(f, request, id, "Submit")).Status);
        Assert.Equal(409, (await Call(f, request, id, "Update", Input("4000"))).Status);
        await using var verify = f.CreateContext(); Assert.Equal(0m, (await verify.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Current_actor_cannot_borrow_alias_grant_or_check_own_preparation()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        Value(await Call(f, request, id, "Submit"));
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        Assert.Equal(403, (await Call(f, request, id, "Approve", actor: RecipientId)).Status);
        await using (var db = f.CreateContext())
        {
            var link = await db.Set<FinanceAccountLink>().SingleAsync(x => x.UserId == AliasAccountId);
            link.EndedAtUtc = f.Clock.GetUtcNow(); link.EndedBy = AuthorId; link.EndReason = "สิ้นสุดบัญชีจำลอง"; link.Version++;
            var proposal = await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == AliasAccountId);
            proposal.EndsAtUtc = f.Clock.GetUtcNow(); await db.SaveChangesAsync();
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = RecipientPersonId,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow(),
                Reason = "บัญชีเดียวกันสำหรับทดสอบ SoD"
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(403, (await Call(f, request, id, "Update", Input("4000"), actor: AliasAccountId)).Status);
        await Grant(f, AliasAccountId, RecipientPersonId, "finance.check");
        Assert.Equal(403, (await Call(f, request, id, "Approve", actor: AliasAccountId)).Status);
    }

    [Fact]
    public async Task Revocation_blocks_create_receipt_replay()
    {
        await using var f = await Ready(); var request = await Approved(f); var meta = await Meta(f, request, null);
        Value(await Call(f, request, null, "Create", Input("6000"), meta));
        await using (var db = f.CreateContext())
        { (await db.Set<GrantProposal>().SingleAsync(x => x.RecipientId == RecipientId)).EndsAtUtc = f.Clock.GetUtcNow(); await db.SaveChangesAsync(); }
        Assert.Equal(403, (await Call(f, request, null, "Create", Input("6000"), meta)).Status);
    }

    [Fact]
    public async Task Concurrent_submissions_do_not_share_available_balance()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var first = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        var second = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        var a = await Meta(f, request, first); var b = await Meta(f, request, second);
        var results = await Task.WhenAll(Call(f, request, first, "Submit", meta: a), Call(f, request, second, "Submit", meta: b));
        Assert.Single(results, x => x.Status == 200); Assert.Single(results, x => x.Status == 409);
        var loser = results[0].Status == 409 ? first : second;
        Assert.Equal(409, (await Call(f, request, loser, "Submit")).Status);
        await using var db = f.CreateContext(); Assert.Equal(6000m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
        Assert.Single(await db.Set<AdvanceReservationEntry>().ToArrayAsync());
    }

    [Fact]
    public async Task Every_return_and_approval_retains_exact_revision_hash_actor_reason_and_outcome()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        Value(await Call(f, request, id, "Submit"));
        var first = (await Meta(f, request, id)) with { Reason = "ตรวจครั้งแรก" };
        var returned = await Call(f, request, id, "Return", meta: first, actor: AliasAccountId);
        Assert.Equal(6000m, Value(returned).Reserved);
        Assert.Equal(409, (await Call(f, request, id, "Approve", meta: first, actor: AliasAccountId)).Status);
        Assert.Equal(JsonSerializer.Serialize(returned), JsonSerializer.Serialize(await Call(f, request, id, "Return", meta: first, actor: AliasAccountId)));
        Value(await Call(f, request, id, "Submit"));
        var second = (await Meta(f, request, id)) with { Reason = "ตรวจครั้งที่สอง" };
        Value(await Call(f, request, id, "Return", meta: second, actor: AliasAccountId));
        Value(await Call(f, request, id, "Submit"));
        var third = (await Meta(f, request, id)) with { Reason = "ตรวจครบแล้ว" };
        Assert.Equal("Reviewed", Value(await Call(f, request, id, "Approve", meta: third, actor: AliasAccountId)).State);
        await using var db = f.CreateContext();
        var revision = await db.Set<AdvanceInstallmentRevision>().SingleAsync();
        var history = await db.Set<AdvancePaymentProposal>().ToDictionaryAsync(x => x.CommandId);
        Assert.Equal(3, history.Count);
        foreach (var command in new[] { first, second, third })
        {
            var row = history[command.CommandId];
            Assert.Equal(command.Reason, row.Reason); Assert.Equal(revision.ContentHash, row.ContentHash);
            Assert.Equal(command.InstallmentVersion, row.InstallmentVersion);
            Assert.Equal(RecipientId, row.ProposedBy); Assert.Equal(RecipientPersonId, row.ProposedByPersonId);
            Assert.Equal(CheckerPerson, row.ReviewedByPersonId); Assert.Equal(AliasAccountId, row.ReviewedBy);
            using var before = JsonDocument.Parse(row.BeforeJson); using var after = JsonDocument.Parse(row.AfterJson);
            Assert.Equal(revision.Id, before.RootElement.GetProperty("RevisionId").GetGuid());
            Assert.Equal(revision.Id, after.RootElement.GetProperty("RevisionId").GetGuid());
            Assert.Equal(command == third ? "Approve" : "Return", after.RootElement.GetProperty("Outcome").GetString());
            Assert.Equal(command == third ? "Approved" : "Returned", row.State);
        }
        Assert.Single(await db.Set<AdvanceReservationEntry>().ToArrayAsync());
        Assert.Equal(6000m, (await db.Set<AdvancePaymentAccount>().SingleAsync()).Reserved);
    }

    [Fact]
    public async Task Cash_owner_can_be_prepared_but_other_recipient_cannot()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var cash = new InstallmentInput("0.01", new("Cash", AuthorPersonId, null, null, null));
        var id = Value(await Call(f, request, null, "Create", cash)).InstallmentId!.Value;
        Assert.Equal(0.01m, Value(await Call(f, request, id, "Submit")).Reserved);
        Assert.Equal(400, (await Call(f, request, null, "Create", cash with { Destination = cash.Destination with { RecipientPersonId = RecipientPersonId } })).Status);
        Assert.Equal("Reviewed", Value(await Call(f, request, id, "Approve", actor: AliasAccountId)).State);
    }

    [Fact]
    public async Task Pending_edit_requires_resubmission_and_unknown_review_outcome_fails_closed()
    {
        await using var f = await Ready(); var request = await Approved(f);
        var id = Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId!.Value;
        Value(await Call(f, request, id, "Submit"));
        Assert.Equal(400, (await Call(f, request, id, "Unknown", actor: AliasAccountId)).Status);
        var edit = Value(await Call(f, request, id, "Update", Input("5000")));
        Assert.NotEqual("Reviewed", edit.State); Assert.Equal(5000m, edit.Reserved);
        Assert.Equal(409, (await Call(f, request, id, "Approve", actor: AliasAccountId)).Status);
        Value(await Call(f, request, id, "Submit"));
        Assert.Equal("Reviewed", Value(await Call(f, request, id, "Approve", actor: AliasAccountId)).State);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Update")]
    [InlineData("Approve")]
    [InlineData("Return")]
    public async Task Save_failure_rolls_back_revision_history_balances_and_receipt(string operation)
    {
        await using var f = await Ready(); var request = await Approved(f);
        Guid? id = operation == "Create" ? null : Value(await Call(f, request, null, "Create", Input("6000"))).InstallmentId;
        if (id is not null) Value(await Call(f, request, id, "Submit"));
        var before = await Snapshot(f);
        await using (var db = f.CreateContext())
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_installment_event() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN RAISE EXCEPTION 'test-installment-event-fault'; END $$;
                CREATE TRIGGER fail_installment_event BEFORE INSERT ON field_advance_payment_events
                FOR EACH ROW EXECUTE FUNCTION fail_installment_event();
                """);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => Call(f, request, id, operation, Input("4000"),
            actor: operation is "Approve" or "Return" ? AliasAccountId : RecipientId));
        Assert.Equal("P0001", Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState);
        Assert.Equal(before, await Snapshot(f));
    }

    private static async Task<string> Snapshot(FieldAdvanceFixture f)
    {
        await using var db = f.CreateContext();
        return JsonSerializer.Serialize(new
        {
            Families = await db.Set<AdvanceFamily>().OrderBy(x => x.Id).ToArrayAsync(),
            Requests = await db.Set<AdvanceRequest>().OrderBy(x => x.Id).ToArrayAsync(),
            Accounts = await db.Set<AdvancePaymentAccount>().OrderBy(x => x.Id).ToArrayAsync(),
            Installments = await db.Set<AdvanceInstallment>().OrderBy(x => x.Id).ToArrayAsync(),
            Revisions = await db.Set<AdvanceInstallmentRevision>().OrderBy(x => x.Id).ToArrayAsync(),
            History = await db.Set<AdvancePaymentProposal>().OrderBy(x => x.Id).ToArrayAsync(),
            Reservations = await db.Set<AdvanceReservationEntry>().OrderBy(x => x.Id).ToArrayAsync(),
            Participation = await db.Set<AdvancePaymentParticipation>().OrderBy(x => x.Id).ToArrayAsync(),
            Receipts = await db.Set<FinanceCommandReceipt>().OrderBy(x => x.CommandId).ToArrayAsync(),
            Events = await db.Set<AdvancePaymentEvent>().OrderBy(x => x.Id).ToArrayAsync()
        });
    }

    private static InstallmentInput Input(string amount) => new(amount, new("Transfer", AuthorPersonId, "test-bank", "owner-account", "เจ้าของเงิน"));
    private static PaymentView Value(FinanceResult<PaymentView> result)
    { Assert.Equal(200, result.Status); return Assert.IsType<PaymentView>(result.Value); }
    private static Task<Guid> Approved(FieldAdvanceFixture f) => new FieldAdvancePaymentFixture(f).SeedApprovedAsync(10000m, default);

    private async Task<FieldAdvanceFixture> Ready()
    {
        var f = await CreateAsync(postgres); await f.SeedPoliciesAsync();
        await using (var db = f.CreateContext())
        {
            db.Add(new FinancePerson { Id = CheckerPerson, WorkspaceId = WorkspaceId, CreatedBy = AuthorId, Reason = "ผู้ตรวจจำลอง" });
            db.Add(new FinanceAccountLink
            {
                Id = Guid.NewGuid(),
                WorkspaceId = WorkspaceId,
                UserId = AliasAccountId,
                PersonId = CheckerPerson,
                CreatedBy = AuthorId,
                CreatedAtUtc = f.Clock.GetUtcNow().AddMinutes(-1),
                Reason = "บัญชีผู้ตรวจจำลอง"
            });
            await db.SaveChangesAsync();
        }
        await Grant(f, RecipientId, RecipientPersonId, "payment.record");
        await Grant(f, AliasAccountId, CheckerPerson, "finance.check"); return f;
    }

    private static async Task Grant(FieldAdvanceFixture f, Guid actor, Guid person, string capability)
    {
        var id = await FieldAdvanceGrantTests.AuthorityAsync(f, actor, person);
        await using var db = f.CreateContext();
        var assignment = await db.Set<GrantAssignment>().SingleAsync(x => x.Id == id);
        (await db.Set<GrantProposal>().SingleAsync(x => x.Id == assignment.ProposalId)).Capabilities = [capability];
        await db.SaveChangesAsync();
    }

    private static async Task<PaymentCommand> Meta(FieldAdvanceFixture f, Guid request, Guid? installment)
    {
        await using var db = f.CreateContext();
        var r = await db.Set<AdvanceRequest>().SingleAsync(x => x.Id == request);
        var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == r.FamilyId);
        var i = installment is null ? null : await db.Set<AdvanceInstallment>().SingleAsync(x => x.Id == installment);
        return new(Guid.NewGuid(), family.Version, r.Version, i?.Version, "จัดงวดตามคำขอ");
    }

    private static async Task<FinanceResult<PaymentView>> Call(FieldAdvanceFixture f, Guid request, Guid? installment,
        string operation, InstallmentInput? input = null, PaymentCommand? meta = null, Guid? actor = null)
    {
        var user = actor ?? RecipientId; var session = await f.SessionAsync(user);
        await using var db = f.CreateContext();
        meta ??= await Meta(f, request, installment);
        var access = FieldAdvanceAccessTests.Access(db, session, f.Clock); var people = new FinancePersonResolver(db, f.Clock);
        var executor = new PaymentCommandExecutor(db, new(db, access, people, new(db, access, people, f.Clock), f.Clock),
            new(db, session, access, people, f.Clock), new AuditEventWriter(db, new CorrelationContext(), f.Clock), f.Clock);
        var service = new InstallmentService(db, executor, new(db, f.Clock), f.Clock);
        return operation switch
        {
            "Create" => await service.CreateAsync(user, request, input!, meta, default),
            "Update" => await service.UpdateAsync(user, installment!.Value, input!, meta, default),
            "Submit" => await service.SubmitAsync(user, installment!.Value, meta, default),
            _ => await new InstallmentReviewService(db, executor, people, f.Clock).ReviewAsync(user, installment!.Value, operation, meta, default)
        };
    }
}

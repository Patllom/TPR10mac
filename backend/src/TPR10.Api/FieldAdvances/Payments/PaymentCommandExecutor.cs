using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Auditing;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Payments;

public sealed class PaymentCommandExecutor(Tpr10DbContext db, PaymentAccess access,
    FamilyParticipation participation, IAuditEventWriter audit, TimeProvider clock)
{
    private static readonly ConditionalWeakTable<Tpr10DbContext, CommandScope> Active = new();

    // The body owns workflow transitions; this boundary owns versions, identity, balances and atomic persistence.
    // Hash the complete typed input (including evidence IDs) with Hash before entering this boundary.
    public async Task<FinanceResult<PaymentView>> ExecuteAsync(Guid actor, Guid requestId, Guid? installmentId,
        string action, PaymentCommand meta, string payloadHash,
        Func<CancellationToken, Task<FinanceResult<PaymentView>>> body, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("คำสั่งจ่ายเงินต้องเป็นเจ้าของธุรกรรม");
        try
        {
            // Discard snapshots from a previous read on a reused context before taking locks.
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var request = await FamilyParticipation.LockRequestAsync(db, requestId, ct);
            var decision = await access.CheckAsync(actor, requestId, action, ct);
            if (request is null || !decision.Allowed || decision.PersonId is null) return Denied();
            var role = action switch
            {
                "prepare" or "start" or "record" or "takeover" => "Payer",
                "check" or "reconcile" or "terminate-propose" => "Checker",
                "terminate-confirm" => "Approver",
                "ceiling-amend" => "Author",
                _ => null
            };
            if (role is null) return Denied();
            if (meta.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(meta.Reason) || meta.Reason.Length > 2000
                || payloadHash.Length != 64 || payloadHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                return Invalid();
            var hash = Hash(new { actor, requestId, installmentId, action, meta, payloadHash });
            var receiptAction = "payment." + action;
            var receipt = await db.Set<FinanceCommandReceipt>().AsNoTracking().SingleOrDefaultAsync(x => x.CommandId == meta.CommandId, ct);
            if (receipt is not null)
            {
                if (receipt.ActorId != actor || receipt.WorkspaceId != request.WorkspaceId
                    || receipt.Action != receiptAction || receipt.PayloadSha256 != hash) return Conflict();
                var replay = JsonSerializer.Deserialize<FinanceResult<PaymentView>>(receipt.ResultJson);
                if (replay?.Value is null || replay.Value.RequestId != requestId) return Conflict();
                return replay;
            }
            var family = await db.Set<AdvanceFamily>().SingleAsync(x => x.Id == request.FamilyId, ct);
            AdvanceInstallment? installment = null;
            if (installmentId is { } id)
            {
                installment = await db.Set<AdvanceInstallment>().FromSqlInterpolated(
                    $"SELECT * FROM field_advance_installments WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct);
                if (installment is null || installment.RequestId != requestId || installment.FamilyId != family.Id
                    || installment.WorkspaceId != request.WorkspaceId || installment.ProjectId != request.ProjectId) return Denied();
            }
            if (family.Version != meta.FamilyVersion || request.Version != meta.RequestVersion
                || installment?.Version != meta.InstallmentVersion) return Conflict();

            // RecordAsync saves participation and increments FamilyVersion, inside this same transaction.
            if (role != "Author") await participation.RecordAsync(family.Id, requestId, decision.PersonId.Value, role, ct);
            receipt = new FinanceCommandReceipt
            {
                CommandId = meta.CommandId,
                WorkspaceId = request.WorkspaceId,
                ActorId = actor,
                Action = receiptAction,
                PayloadSha256 = hash,
                Status = 200,
                ResultJson = "{}",
                CreatedAtUtc = clock.GetUtcNow()
            };
            db.Add(receipt);
            var scope = new CommandScope(tx.TransactionId, requestId, installmentId, meta.CommandId);
            Active.Add(db, scope);
            FinanceResult<PaymentView> result;
            try { result = await body(ct); }
            finally { Active.Remove(db); }
            if (scope.Failed) return Conflict();
            if (result.Status is < 200 or >= 300) return result;
            if (result.Value is null || result.Value.RequestId != requestId) throw new InvalidOperationException("ผลคำสั่งไม่ตรงคำขอ");
            // A prepare command may create a new installment; never accept a different existing scope.
            if (installment is null && result.Value.InstallmentId is { } createdId)
            {
                installment = db.Set<AdvanceInstallment>().Local.SingleOrDefault(x => x.Id == createdId);
                if (installment is null || db.Entry(installment).State != EntityState.Added || installment.RequestId != requestId
                    || installment.FamilyId != family.Id) throw new InvalidOperationException("งวดใหม่ไม่ตรงคำขอ");
            }
            else if (result.Value.InstallmentId != installmentId) throw new InvalidOperationException("ผลคำสั่งไม่ตรงงวด");
            family.Version = Math.Max(family.Version, checked(meta.FamilyVersion + 1));
            request.Version = Math.Max(request.Version, checked(meta.RequestVersion + 1));
            if (installment is not null && meta.InstallmentVersion is { } version)
                installment.Version = Math.Max(installment.Version, checked(version + 1));
            var account = await db.Set<AdvancePaymentAccount>().SingleAsync(x => x.RequestId == requestId, ct);
            var balance = new BalanceSnapshot(account.Approved, account.Spendable, account.Reserved, account.Paid);
            // Include the body's pending inserts/releases; a scalar database projection misses them.
            var storedHolds = await db.Set<AdvancePaymentHold>().Where(x => x.FamilyId == family.Id
                && (x.RequestId == null || x.RequestId == requestId)).ToArrayAsync(ct);
            var holds = storedHolds.Concat(db.Set<AdvancePaymentHold>().Local).DistinctBy(x => x.Id)
                .Where(x => x.FamilyId == family.Id && x.ReleasedAtUtc == null
                    && (x.RequestId == null || x.RequestId == requestId)
                    && (x.InstallmentId == null || x.InstallmentId == result.Value.InstallmentId))
                .Select(x => x.Code).Distinct().OrderBy(x => x).ToArray();
            result = result with
            {
                Value = new(requestId, installment?.Id, family.Version, request.Version, installment?.Version,
                installment?.State ?? request.State, balance.Approved, balance.Spendable, balance.Reserved, balance.Paid, BalanceMath.Available(balance), holds)
            };
            receipt.Status = result.Status; receipt.ResultJson = JsonSerializer.Serialize(result);
            db.Add(new AdvancePaymentEvent
            {
                Id = Guid.NewGuid(),
                WorkspaceId = request.WorkspaceId,
                ProjectId = request.ProjectId,
                FamilyId = family.Id,
                RequestId = requestId,
                InstallmentId = installment?.Id,
                CommandId = meta.CommandId,
                ActorId = actor,
                ActorPersonId = decision.PersonId.Value,
                Kind = action,
                Reason = meta.Reason,
                DataJson = JsonSerializer.Serialize(new { PayloadSha256 = hash }),
                FamilyVersion = family.Version,
                RequestVersion = request.Version,
                InstallmentVersion = installment?.Version,
                CreatedAtUtc = clock.GetUtcNow()
            });
            await audit.WriteAsync(new SecurityAuditRequest(actor, null, request.WorkspaceId, request.ProjectId, null,
                "field-advance.payment." + action, "field-advance-request", requestId, "success",
                new Dictionary<string, string> { ["scope-validation"] = "authorized" }), ct);
            await db.SaveChangesAsync(ct);
            var final = await access.CheckAsync(actor, requestId, action, ct);
            if (!final.Allowed || final.PersonId != decision.PersonId) return Denied();
            // Scheduled expiry can cross the body/persistence interval even while identity locks are held.
            if (scope.FinalAuthorization is { } validate && !await validate(ct)) return Denied();
            await tx.CommitAsync(ct);
            return result;
        }
        catch (ReservationException ex) { return ex.Invalid ? Invalid() : Conflict(); }
        catch (DbUpdateConcurrencyException) { return Conflict(); }
        finally { Active.Remove(db); db.ChangeTracker.Clear(); }
    }

    public static string Hash<T>(T input) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(input)));

    internal static void RequireScope(Tpr10DbContext db, Guid request, Guid? installment, Guid command)
    {
        if (!Active.TryGetValue(db, out var scope) || db.Database.CurrentTransaction?.TransactionId != scope.Transaction
            || scope.Request != request || scope.Command != command || scope.Installment != installment)
            throw new InvalidOperationException("บัญชีกันวงเงินต้องทำภายในคำสั่งและงวดที่ได้รับอนุญาต");
    }

    // Server-only authorization check for an additional participant; no file I/O or grants.
    // Registration is bound to this command's active body, and replay never executes the body.
    internal static void RegisterFinalAuthorization(Tpr10DbContext db, Guid request, Guid? installment, Guid command,
        Func<CancellationToken, Task<bool>> validate)
    {
        RequireScope(db, request, installment, command);
        if (!Active.TryGetValue(db, out var scope) || scope.FinalAuthorization is not null)
            throw new InvalidOperationException("ลงทะเบียนตรวจสิทธิ์สุดท้ายได้ครั้งเดียวต่อคำสั่ง");
        scope.FinalAuthorization = validate;
    }

    internal static void FailScope(Tpr10DbContext db)
    {
        if (Active.TryGetValue(db, out var scope)) scope.Failed = true;
    }

    private sealed record CommandScope(Guid Transaction, Guid Request, Guid? Installment, Guid Command)
    {
        internal bool Failed { get; set; }
        internal Func<CancellationToken, Task<bool>>? FinalAuthorization { get; set; }
    }
    private static FinanceResult<PaymentView> Denied() => new(403, "finance-access-denied", null);
    private static FinanceResult<PaymentView> Conflict() => new(409, "payment-conflict", null);
    private static FinanceResult<PaymentView> Invalid() => new(400, "invalid-payment", null);
}

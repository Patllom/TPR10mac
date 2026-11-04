using System.Data.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;
using TPR10.Api.FieldAdvances.Settlements;
using TPR10.Api.Identity.Sessions;
using static TPR10.Api.IntegrationTests.FieldAdvanceFixture;
using static TPR10.Api.IntegrationTests.FieldAdvanceSettlementAccessTests;

namespace TPR10.Api.IntegrationTests;

[Collection("database")]
public sealed class FieldAdvanceSettlementFix1Tests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("queue")]
    [InlineData("comparison")]
    [InlineData("receipt")]
    public async Task Actual_duplicate_delivery_and_draft_writer_complete_in_registry_order(string lane)
    {
        await using var f = await Ready(postgres);
        using var files = new FieldAdvanceSettlementEvidenceTests.Files();
        await Grant(f, AuthorId, AuthorPersonId, "request.create");
        await Grant(f, RecipientId, RecipientPersonId, "finance.check");
        var first = await Request(f); var second = await Request(f);
        await using (var seed = f.CreateContext())
        {
            var docs = await FieldAdvanceExpenseDocumentTests.Documents(f, seed, files);
            foreach (var request in new[] { first, second })
                Assert.Equal(201, (await docs.RegisterAsync(AuthorId, request,
                    await FieldAdvanceExpenseDocumentTests.Input(f, files, request), await Meta(f, request), default)).Status);
        }
        await using var inspect = f.CreateContext();
        var duplicate = await inspect.Set<ExpenseDuplicateCase>().AsNoTracking().SingleAsync();
        var command = await Meta(f, duplicate.RequestId, duplicate.Version);
        if (lane == "receipt")
        {
            var review = new DuplicateDocumentService(inspect, await Access(f, inspect, RecipientId),
                await Executor(f, inspect, RecipientId), f.Clock);
            Assert.Equal(200, (await review.ReviewDuplicateAsync(RecipientId, duplicate.RequestId, duplicate.Id, false, command, default)).Status);
        }
        var writerRequest = await FieldAdvanceSettlementDraftTests.Approved(f);
        using var keys = new TestKeyMaterial();
        await using var factory = new ApiFactory(f.Database.ConnectionString, "Production", f.Clock, keys.Settings);
        await using var scope = factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<RequestSession>().Entity = (await f.SessionAsync(RecipientId)).Entity;
        var work = scope.ServiceProvider.GetRequiredService<SettlementWorkService>();
        var result = lane switch
        {
            "queue" => await work.QueueAsync(RecipientId, WorkspaceId, ProjectId, "duplicates", 25, null, default),
            "comparison" => await work.DuplicateComparisonAsync(RecipientId, duplicate.RequestId, duplicate.Id, default),
            _ => await scope.ServiceProvider.GetRequiredService<SettlementUiService>().ReceiptAsync(
                RecipientId, duplicate.RequestId, command.CommandId, "duplicates/review", duplicate.Id, default)
        };
        var deliveryDb = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
        await deliveryDb.Database.OpenConnectionAsync();
        var deliveryPid = await deliveryDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var gate = new RegistryGate();
        await using var writerDb = new Tpr10DbContext(new DbContextOptionsBuilder<Tpr10DbContext>()
            .UseNpgsql(f.Database.ConnectionString).AddInterceptors(gate).Options);
        var executor = await Executor(f, writerDb, AuthorId);
        var draft = new SettlementDraftService(writerDb, await Access(f, writerDb, AuthorId),
            executor, new ExpenseAllocationLedger(writerDb, executor), f.Clock);
        var writing = draft.SaveAsync(AuthorId, writerRequest, null, FieldAdvanceSettlementDraftTests.Input(10),
            await Meta(f, writerRequest), default);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Response.Body = new MemoryStream();
        var delivering = result.ExecuteAsync(context);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (await inspect.Database.SqlQuery<int>($"SELECT cardinality(pg_blocking_pids({deliveryPid})) AS \"Value\"").SingleAsync(timeout.Token) == 0)
            {
                Assert.False(delivering.IsCompleted);
                await Task.Delay(10, timeout.Token);
            }
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(201, (await writing.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await delivering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(200, context.Response.StatusCode);
        Assert.True(context.Response.Body.Length > 0);
        Assert.Equal("private, no-store", context.Response.Headers.CacheControl.ToString());
    }

    private sealed class RegistryGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock(7241005)") && Reached.TrySetResult())
                await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}

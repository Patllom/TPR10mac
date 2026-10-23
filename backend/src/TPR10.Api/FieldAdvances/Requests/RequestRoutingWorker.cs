using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TPR10.Api.Data;
using TPR10.Api.FieldAdvances.Data;

namespace TPR10.Api.FieldAdvances.Requests;

public sealed class RequestRoutingWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    TimeProvider clock, ILogger<RequestRoutingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = configuration.GetValue("FieldAdvances:RequestRouting:IntervalSeconds", 30d);
        var batchSize = configuration.GetValue("FieldAdvances:RequestRouting:BatchSize", 50);
        if (!double.IsFinite(seconds) || seconds <= 0 || batchSize <= 0)
            throw new InvalidOperationException("ช่วงเวลาประเมินสายอนุมัติและขนาดชุดต้องมากกว่าศูนย์");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), clock);
        Guid? cursor = null;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    Guid[] ids;
                    await using (var scope = scopes.CreateAsyncScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<Tpr10DbContext>();
                        var query = db.Set<AdvanceRequest>().AsNoTracking().Where(x => x.State == "PendingApproval" || x.State == "Held");
                        if (cursor is { } after) query = query.Where(x => x.Id.CompareTo(after) > 0);
                        ids = await query.OrderBy(x => x.Id).Select(x => x.Id).Take(batchSize).ToArrayAsync(stoppingToken);
                    }
                    foreach (var id in ids)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        // เลื่อน cursor แม้คำขอนี้ผิดพลาด เพื่อไม่กีดกันคำขอถัดไป
                        cursor = id;
                        try
                        {
                            await using var scope = scopes.CreateAsyncScope();
                            await scope.ServiceProvider.GetRequiredService<RequestReviewService>().ReevaluateAsync(id, stoppingToken);
                        }
                        catch (Exception error) when (error is DbException or DbUpdateException)
                        {
                            var (kind, sqlState) = Classify(error);
                            logger.LogWarning("ประเมินสายอนุมัติไม่สำเร็จ จะลองใหม่ในรอบถัดไป Phase={Phase} RequestId={RequestId} ExceptionKind={ExceptionKind} SqlState={SqlState}",
                                "reevaluate", id, kind, sqlState);
                        }
                    }
                    if (ids.Length < batchSize) cursor = null;
                }
                catch (Exception error) when (error is DbException or DbUpdateException)
                {
                    var (kind, sqlState) = Classify(error);
                    logger.LogWarning("อ่านรายการรอประเมินสายอนุมัติไม่ได้ จะลองใหม่ในรอบถัดไป Phase={Phase} ExceptionKind={ExceptionKind} SqlState={SqlState}",
                        "batch-read", kind, sqlState);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private static (string Kind, string? SqlState) Classify(Exception error)
    {
        // Fixed labels only: never render an exception, runtime type name or provider message.
        var kind = error switch
        {
            DbUpdateException => "DbUpdateException",
            PostgresException => "PostgresException",
            NpgsqlException => "NpgsqlException",
            _ => "DbException"
        };
        var databaseError = error is DbUpdateException ? error.InnerException : error;
        var sqlState = databaseError is PostgresException postgres ? postgres.SqlState : null;
        // Even provider SQLSTATE is untrusted input; admit only the five-character code grammar.
        if (sqlState is not { Length: 5 } || sqlState.Any(c => c is not (>= '0' and <= '9' or >= 'A' and <= 'Z')))
            sqlState = null;
        return (kind, sqlState);
    }
}

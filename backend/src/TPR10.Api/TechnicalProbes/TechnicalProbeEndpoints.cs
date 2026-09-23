using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using TPR10.Api.Identity.Authorization;
using TPR10.Api.Identity.Sessions;
namespace TPR10.Api.TechnicalProbes;

public static class TechnicalProbeEndpoints
{
    public static void MapTechnicalProbeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/system/identity-probe", () => Results.Ok(new { status = "authorized" }))
            .RequireAuthorization("system:probe");
        app.MapPost("/api/v1/system/technical-probes", async (
            CreateTechnicalProbeRequest request, Tpr10DbContext db, IAuditEventWriter audit,
            ICorrelationContext correlation, TimeProvider clock, RequestSession session, PermissionContext permission,
            PermissionMutationGuard guard, CancellationToken cancellationToken) =>
        {
            var note = request.Note?.Trim();
            if (string.IsNullOrEmpty(note) || note.Length > 500)
                return Results.Problem(statusCode: 400, title: "หมายเหตุต้องมีความยาว 1–500 ตัวอักษร");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7241002)", cancellationToken);
            if (!await guard.AllowsAsync(session.Entity!.UserId, "system:probe", cancellationToken))
                return Results.Problem(statusCode: 403, type: "urn:tpr10:permission-denied", title: "สิทธิ์หรือ session เปลี่ยนแปลง กรุณาเข้าสู่ระบบใหม่");
            var probe = new TechnicalProbe
            {
                Id = Guid.NewGuid(),
                Note = note,
                CreatedAtUtc = clock.GetUtcNow(),
                CorrelationId = correlation.CorrelationId.ToString("D")
            };
            db.TechnicalProbes.Add(probe);
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync(new SecurityAuditRequest(session.Entity.UserId, permission.ActingRoleId, null, null, null,
                "technical.probe.created", "technical-probe", probe.Id, "success",
                new Dictionary<string, string> { ["source"] = "module-1-controlled-endpoint" }), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Json(new { probe.Id, probe.Note, probe.CreatedAtUtc, probe.CorrelationId }, statusCode: 201);
        }).RequireAuthorization("system:probe");
    }
}

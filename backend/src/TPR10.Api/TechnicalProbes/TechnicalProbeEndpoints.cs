using TPR10.Api.Auditing;
using TPR10.Api.Correlation;
using TPR10.Api.Data;
using TPR10.Api.Data.Entities;
namespace TPR10.Api.TechnicalProbes;

public static class TechnicalProbeEndpoints
{
    public static void MapTechnicalProbeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/system/technical-probes", async (
            CreateTechnicalProbeRequest request, Tpr10DbContext db, IAuditEventWriter audit,
            ICorrelationContext correlation, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var note = request.Note?.Trim();
            if (string.IsNullOrEmpty(note) || note.Length > 500)
                return Results.Problem(statusCode: 400, title: "หมายเหตุต้องมีความยาว 1–500 ตัวอักษร");
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var probe = new TechnicalProbe
            {
                Id = Guid.NewGuid(),
                Note = note,
                CreatedAtUtc = clock.GetUtcNow(),
                CorrelationId = correlation.CorrelationId.ToString("D")
            };
            db.TechnicalProbes.Add(probe);
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("technical.probe.created", probe.Id,
                new Dictionary<string, string> { ["source"] = "module-1-controlled-endpoint" }, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Json(new { probe.Id, probe.Note, probe.CreatedAtUtc, probe.CorrelationId }, statusCode: 201);
        });
    }
}

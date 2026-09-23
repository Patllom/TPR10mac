namespace TPR10.Api.Identity.Reset;

public sealed class ResetOptions
{
    public bool EmailEnabled { get; set; }
}

public static class ResetRegistration
{
    public static void AddPasswordResetDelivery(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        services.AddOptions<ResetOptions>().Configure(x => x.EmailEnabled = local)
            .Bind(config.GetSection("Identity:Reset"))
            .Validate(x => local || !x.EmailEnabled, "Production email reset requires the Module 5 delivery adapter; leave Identity:Reset:EmailEnabled=false")
            .ValidateOnStart();
        if (!local) return;
        services.AddSingleton<DevelopmentResetSink>();
        services.AddSingleton<IResetTransport>(x => x.GetRequiredService<DevelopmentResetSink>());
        services.AddScoped<ResetOutboxDispatcher>();
    }
}

// Local DI-only inbox, bounded and destructive-read. No HTTP route and no logging.
public sealed class DevelopmentResetSink(TimeProvider clock) : IResetTransport
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, (string Token, DateTimeOffset At)> inbox = new();

    public Task SendAsync(Guid requestId, string recipient, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate)
        {
            Prune();
            if (inbox.Count >= 1000 && !inbox.ContainsKey(requestId)) throw new IOException("Local reset sink capacity reached");
            inbox[requestId] = (token, clock.GetUtcNow());
        }
        return Task.CompletedTask;
    }

    public string? Take(Guid requestId)
    {
        lock (gate)
        {
            Prune();
            return inbox.Remove(requestId, out var item) ? item.Token : null;
        }
    }

    private void Prune()
    {
        foreach (var id in inbox.Where(x => x.Value.At <= clock.GetUtcNow().AddMinutes(-15)).Select(x => x.Key).ToArray()) inbox.Remove(id);
    }
}

using System.Security.Claims;

namespace TPR10.Api.Identity.Accounts;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/users").RequireAuthorization("users:manage");
        group.MapPost("/{id:guid}/password-reset", (HttpContext context, Reset.PasswordResetService reset, Guid id, CancellationToken ct)
            => Actor(context, out var actor) ? reset.AdminResetAsync(actor, id, ct) : Task.FromResult(Results.Unauthorized()));
        group.MapGet("", ListAsync);
        group.MapPost("", async (HttpContext context, AccountProvisioning accounts, CreateAccountRequest request, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Actor(context, out var actor) ? await accounts.CreateAsync(actor, request, ct) : Results.Unauthorized();
        });
        group.MapPatch("/{id:guid}", async (HttpContext context, AccountProvisioning accounts, Guid id, UpdateAccountRequest request, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Actor(context, out var actor) ? await accounts.UpdateAsync(actor, id, request, ct) : Results.Unauthorized();
        });
    }

    public static async Task<IResult> ListAsync(HttpContext context, AccountProvisioning accounts, int? page, int? pageSize, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Actor(context, out var actor) ? await accounts.ListAsync(actor, page ?? 1, pageSize ?? 25, ct) : Results.Unauthorized();
    }

    private static bool Actor(HttpContext context, out Guid actor)
    {
        actor = default;
        return context.User.Identity?.IsAuthenticated == true && Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out actor);
    }
}

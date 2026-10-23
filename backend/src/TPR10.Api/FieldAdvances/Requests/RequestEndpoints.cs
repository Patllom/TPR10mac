using System.Text.Json;
using TPR10.Api.Identity.Sessions;

namespace TPR10.Api.FieldAdvances.Requests;

public static class RequestEndpoints
{
    public static async Task<IResult> Revisions(RequestReadService s, RequestSession session, Guid id, int? page, int? pageSize, CancellationToken ct) =>
        RequestHttp.Result(await s.RevisionsAsync(Actor(session), id, page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> Revision(RequestReadService s, RequestSession session, Guid id, Guid revisionId, CancellationToken ct) =>
        RequestHttp.Result(await s.RevisionAsync(Actor(session), id, revisionId, ct));
    public static async Task<IResult> Configuration(RequestReadService s, RequestSession session, Guid workspaceId, Guid? projectId, CancellationToken ct) =>
        RequestHttp.Result(await s.ConfigurationAsync(Actor(session), new(workspaceId, projectId), ct));
    public static async Task<IResult> Screen(RequestReadService s, RequestSession session, Guid id, CancellationToken ct) =>
        RequestHttp.Result(await s.ScreenAsync(Actor(session), id, ct));
    public static async Task<IResult> TeamPeople(RequestReadService s, RequestSession session, Guid workspaceId, Guid? projectId, string? prefix, int? page, int? pageSize, CancellationToken ct) =>
        RequestHttp.Result(await s.TeamPeopleAsync(Actor(session), new(workspaceId, projectId), prefix ?? "", page ?? 1, pageSize ?? 20, ct));
    private static Guid Actor(RequestSession session) => session.Entity?.UserId ?? Guid.Empty;
    public static Task<IResult> Create(RequestDraftService s, RequestSession session, HttpRequest request, CancellationToken ct) =>
        Command<CreateRequestHttp>(request, x => s.CreateAsync(Actor(session), x.Input, x.Meta, ct), ct);
    public static Task<IResult> Update(RequestDraftService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<UpdateRequestHttp>(request, x => s.UpdateAsync(Actor(session), id, x.Content, x.Meta, ct), ct);
    public static Task<IResult> SendToOwner(RequestSubmissionService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<RequestCommand>(request, x => s.SendToOwnerAsync(Actor(session), id, x, ct), ct);
    public static Task<IResult> Confirm(RequestSubmissionService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<ConfirmRequestHttp>(request, x => s.ConfirmAsync(Actor(session), id, x.ConfirmationHash, x.Meta, ct), ct);
    public static Task<IResult> Review(RequestReviewService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<ReviewRequestHttp>(request, x => s.DecideAsync(Actor(session), id, x.Decision, x.Meta, ct), ct);
    public static Task<IResult> ReturnToAuthor(RequestRevisionService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<RequestCommand>(request, x => s.ReturnToAuthorAsync(Actor(session), id, x, ct), ct);
    public static Task<IResult> Revise(RequestRevisionService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<RequestCommand>(request, x => s.ReviseAsync(Actor(session), id, x, ct), ct);
    public static Task<IResult> Withdraw(RequestRevisionService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<RequestCommand>(request, x => s.WithdrawAsync(Actor(session), id, x, ct), ct);
    public static Task<IResult> Copy(RequestRevisionService s, RequestSession session, Guid id, HttpRequest request, CancellationToken ct) =>
        Command<RequestCommand>(request, x => s.CopyAsync(Actor(session), id, x, ct), ct);
    public static async Task<IResult> List(RequestReadService s, RequestSession session, Guid workspaceId, Guid? projectId, string? mode, int? page, int? pageSize, CancellationToken ct) =>
        RequestHttp.Result(await s.ListAsync(Actor(session), new(workspaceId, projectId), mode ?? "mine", page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> Queue(RequestReadService s, RequestSession session, Guid workspaceId, Guid? projectId, int? page, int? pageSize, CancellationToken ct) =>
        RequestHttp.Result(await s.ListAsync(Actor(session), new(workspaceId, projectId), "review", page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> Details(RequestReadService s, RequestSession session, Guid id, CancellationToken ct) =>
        RequestHttp.Result(await s.DetailsAsync(Actor(session), id, ct));
    public static async Task<IResult> History(RequestReadService s, RequestSession session, Guid id, int? page, int? pageSize, CancellationToken ct) =>
        RequestHttp.Result(await s.HistoryAsync(Actor(session), id, page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> Quote(RequestSubmissionService s, RequestSession session, Guid id, CancellationToken ct) =>
        RequestHttp.Result(await s.QuoteAsync(Actor(session), id, ct));
    public static async Task<IResult> Scopes(RequestReadService s, RequestSession session, int? page, int? pageSize, CancellationToken ct) =>
        RequestHttp.Result(await s.ScopesAsync(Actor(session), page ?? 1, pageSize ?? 20, ct));
    public static async Task<IResult> People(RequestReadService s, RequestSession session, Guid workspaceId, Guid? projectId, string? prefix, int? page, int? pageSize, CancellationToken ct) =>
        RequestHttp.Result(await s.PeopleAsync(Actor(session), new(workspaceId, projectId), prefix ?? "", page ?? 1, pageSize ?? 20, ct));
    private static async Task<IResult> Command<T>(HttpRequest request, Func<T, Task<FinanceResult<RequestView>>> execute, CancellationToken ct)
    {
        T? body;
        try { body = await request.ReadFromJsonAsync<T>(RequestHttp.InputOptions, ct); }
        catch (JsonException) { return Results.Problem(statusCode: 400, title: "ข้อมูลคำขอไม่ถูกต้อง"); }
        catch (BadHttpRequestException) { return Results.Problem(statusCode: 400, title: "ข้อมูลคำขอไม่ถูกต้อง"); }
        if (body is null) return Results.Problem(statusCode: 400, title: "ข้อมูลคำขอไม่ถูกต้อง");
        return RequestHttp.Result(await execute(body));
    }
}

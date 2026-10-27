using TPR10.Api.FieldAdvances.Access;
using TPR10.Api.FieldAdvances.Grants;
using TPR10.Api.FieldAdvances.Policies;
using TPR10.Api.FieldAdvances.Requests;
using TPR10.Api.FieldAdvances.Payments;
using TPR10.Api.Identity;
using TPR10.Api.FieldAdvances.Evidence;

namespace TPR10.Api.FieldAdvances;

public static class FieldAdvanceRegistration
{
    public static IServiceCollection AddFieldAdvances(this IServiceCollection services)
    {
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("FinanceEvidence").Get<FinanceEvidenceOptions>() ?? new());
        services.AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection("FinanceEvidence:Pdf").Get<PdfValidationOptions>() ?? new());
        services.AddSingleton<IFinanceEvidenceStore, FolderFinanceEvidenceStore>();
        services.AddSingleton<IFinancePdfValidator, PdfValidationProcess>();
        services.AddScoped<FinanceEvidenceService>();
        services.AddScoped<FinanceEvidenceMigration>();
        services.AddScoped<FinanceEvidenceReconciler>();
        services.AddScoped<FinancePersonResolver>();
        services.AddScoped<FinanceAccess>();
        services.AddScoped<RequestAccess>();
        services.AddScoped<PaymentAccess>();
        services.AddScoped<FamilyParticipation>();
        services.AddScoped<PaymentCommandExecutor>();
        services.AddScoped<ReservationLedger>();
        services.AddScoped<InstallmentService>();
        services.AddScoped<InstallmentReviewService>();
        services.AddScoped<PaymentExecutionService>();
        services.AddScoped<PaymentOutcomeService>();
        services.AddScoped<PaymentHoldService>();
        services.AddScoped<PaymentTakeoverService>();
        services.AddScoped<PaymentCancellationService>();
        services.AddScoped<PaymentMetadataService>();
        services.AddScoped<RequestReadService>();
        services.AddScoped<RequestDraftService>();
        services.AddScoped<RequestSubmissionService>();
        services.AddScoped<RequestReviewService>();
        services.AddScoped<RequestRevisionService>();
        services.AddHostedService<RequestRoutingWorker>();
        services.AddScoped<ApprovalRouteResolver>();
        services.AddScoped<PolicyService>();
        services.AddScoped<PolicyResolver>();
        services.AddScoped<PolicyReadService>();
        services.AddScoped<PolicyWorkspaceService>();
        services.AddScoped<GrantService>();
        services.AddScoped<GrantReadService>();
        services.AddScoped<GrantLifecycle>();
        services.AddOpenApi(options => options.AddOperationTransformer<FieldAdvanceOpenApiTransformer>());
        return services;
    }

    public static IEndpointRouteBuilder MapFieldAdvances(this IEndpointRouteBuilder endpoints)
    {
        // Session middleware handles authentication/CSRF; services require current MFA and exact finance scope.
        var group = endpoints.MapGroup("/api/v1/field-advances").RequireAuthorization()
            .WithMetadata(new IdentityProblemTypes(409));
        group.MapGet("/policies/scopes", PolicyEndpoints.ScopesAsync).Produces<PolicyScopeChoice[]>();
        group.MapGet("/policies/company-context", PolicyEndpoints.CompanyContextAsync).Produces<PolicyCompanyContext>();
        group.MapPost("/policies", PolicyEndpoints.CreateAsync).Produces<PolicyHttpView>(201);
        group.MapGet("/policies", PolicyEndpoints.ListAsync).Produces<PolicyHttpView[]>();
        group.MapGet("/policies/{id}", PolicyEndpoints.DetailsAsync).Produces<PolicyHttpView>();
        group.MapGet("/policies/{id}/history", PolicyEndpoints.HistoryAsync).Produces<PolicyHttpHistory[]>();
        group.MapGet("/policies/review-queue", PolicyEndpoints.ReviewQueueAsync).Produces<PolicyHttpView[]>();
        group.MapGet("/policies/effective", PolicyEndpoints.EffectiveAsync).Produces<PolicyHttpSnapshot>();
        group.MapPost("/policies/preview", PolicyEndpoints.PreviewAsync).Produces<string[]>();
        group.MapPut("/policies/{id}", PolicyEndpoints.UpdateAsync).Produces<PolicyHttpView>();
        group.MapPost("/policies/{id}/submit", PolicyEndpoints.SubmitAsync).Produces<PolicyHttpView>();
        group.MapPost("/policies/{id}/review", PolicyEndpoints.ReviewAsync).Produces<PolicyHttpView>();
        group.MapGet("/grants/scopes", GrantHttp.Scopes).Produces<GrantScopeChoice[]>();
        group.MapGet("/grants/recipients", GrantHttp.Recipients).Produces<GrantPersonChoice[]>();
        group.MapGet("/grants", GrantHttp.List).Produces<GrantRecord[]>();
        group.MapGet("/grants/review-queue", GrantHttp.Queue).Produces<GrantRecord[]>();
        group.MapGet("/grants/{id}", GrantHttp.Details).Produces<GrantRecord>();
        group.MapGet("/grants/{id}/history", GrantHttp.History).Produces<GrantHistory[]>();
        group.MapPost("/grants", GrantHttp.Propose).Produces<GrantHttpView>(201);
        group.MapPost("/grants/{id}/review", GrantHttp.Review).Produces<GrantHttpView>();
        group.MapPost("/grants/{id}/revoke", GrantHttp.Revoke).Produces<GrantHttpView>();
        group.MapGet("/requests/scopes", RequestEndpoints.Scopes).Produces<RequestScopeChoice[]>();
        group.MapGet("/requests/people", RequestEndpoints.People).Produces<RequestPersonChoice[]>();
        group.MapGet("/requests", RequestEndpoints.List).Produces<RequestHttpView[]>();
        group.MapGet("/requests/team-people", RequestEndpoints.TeamPeople).Produces<RequestPersonChoice[]>();
        group.MapGet("/requests/configuration", RequestEndpoints.Configuration);
        group.MapGet("/requests/{id}/screen", RequestEndpoints.Screen).Produces<RequestScreen>();
        group.MapGet("/requests/review-queue", RequestEndpoints.Queue).Produces<RequestHttpView[]>();
        group.MapGet("/requests/{id}", RequestEndpoints.Details).Produces<RequestHttpView>();
        group.MapGet("/requests/{id}/history", RequestEndpoints.History).Produces<RequestHistory[]>();
        group.MapGet("/requests/{id}/revisions", RequestEndpoints.Revisions).Produces<RequestRevisionSummary[]>();
        group.MapGet("/requests/{id}/revisions/{revisionId}", RequestEndpoints.Revision).Produces<RequestRevisionDetail>();
        group.MapGet("/requests/{id}/quote", RequestEndpoints.Quote).Produces<RequestQuoteHttp>();
        group.MapPost("/requests", RequestEndpoints.Create).Accepts<CreateRequestHttp>("application/json").Produces<RequestHttpView>(201);
        group.MapPut("/requests/{id}", RequestEndpoints.Update).Accepts<UpdateRequestHttp>("application/json").Produces<RequestHttpView>();
        group.MapPost("/requests/{id}/send-to-owner", RequestEndpoints.SendToOwner).Accepts<RequestCommand>("application/json").Produces<RequestHttpView>();
        group.MapPost("/requests/{id}/confirm", RequestEndpoints.Confirm).Accepts<ConfirmRequestHttp>("application/json").Produces<RequestHttpView>();
        group.MapPost("/requests/{id}/return-to-author", RequestEndpoints.ReturnToAuthor).Accepts<RequestCommand>("application/json").Produces<RequestHttpView>();
        group.MapPost("/requests/{id}/review", RequestEndpoints.Review).Accepts<ReviewRequestHttp>("application/json").Produces<RequestHttpView>();
        group.MapPost("/requests/{id}/revise", RequestEndpoints.Revise).Accepts<RequestCommand>("application/json").Produces<RequestHttpView>();
        group.MapPost("/requests/{id}/withdraw", RequestEndpoints.Withdraw).Accepts<RequestCommand>("application/json").Produces<RequestHttpView>();
        group.MapPost("/requests/{id}/copy", RequestEndpoints.Copy).Accepts<RequestCommand>("application/json").Produces<RequestHttpView>(201);
        return endpoints;
    }
}

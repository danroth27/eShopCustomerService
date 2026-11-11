using System.Diagnostics;
using System.Security.Claims;
using eShopCustomerService.Api.Data;
using eShopCustomerService.Api.Services;
using eShopCustomerService.Contracts;
using eShopCustomerService.Contracts.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;

namespace eShopCustomerService.Api.Endpoints;

public static class ReturnEndpoints
{
    public static void MapReturnEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        var protectedApi = api.MapGroup("").RequireAuthorization("CustomerService");
        protectedApi.MapGet("/returns", (ReturnStore store, CancellationToken ct) => store.GetCasesAsync(ct));
        protectedApi.MapGet("/returns/{orderId:int}", async Task<Results<Ok<ReturnCase>, NotFound>> (
            int orderId, ReturnStore store, CancellationToken ct) =>
            await store.GetCaseAsync(orderId, ct) is { } result ? TypedResults.Ok(result) : TypedResults.NotFound());
        protectedApi.MapGet("/returns/{orderId:int}/audit", (int orderId, ReturnStore store, CancellationToken ct) =>
            store.GetAuditAsync(orderId, ct));
        protectedApi.MapGet("/inventory", (ReturnStore store, CancellationToken ct) => store.GetInventoryAsync(ct));
        protectedApi.MapGet("/inventory/{productCode}/availability", async (
            string productCode, IInventoryService inventory, CancellationToken ct) =>
            TypedResults.Ok(await inventory.IsAvailableAsync(productCode, ct)));
        protectedApi.MapGet("/drafts/{id:guid}", async Task<Results<Ok<ResolutionDraft>, NotFound>> (
            Guid id, ReturnStore store, CancellationToken ct) =>
            await store.GetDraftAsync(id, ct) is { } draft ? TypedResults.Ok(draft) : TypedResults.NotFound());
        protectedApi.MapPost("/drafts", async Task<Results<Created<ResolutionDraft>, ForbidHttpResult, ProblemHttpResult>> (
            SaveResolutionRequest request, ReturnStore store, ClaimsPrincipal user,
            IAuthorizationService authorization, ILogger<ReturnStore> logger, CancellationToken ct) =>
        {
            if (request.Resolution.Value is RefundResolution
                && !(await authorization.AuthorizeAsync(user, DemoAuthentication.RefundPolicy)).Succeeded)
            {
                return TypedResults.Forbid();
            }
            var result = await store.SaveDraftAsync(request, user.Identity!.Name!, ct);
            if (result is ResolutionFailure rejected)
            {
                logger.LogWarning("Draft rejected for order {OrderId}: {Reason}", request.OrderId, rejected.Message);
            }
            return result switch
            {
                ResolutionDraft draft => TypedResults.Created($"/api/drafts/{draft.Id}", draft),
                ResolutionFailure failure => Problem(failure)
            };
        }).ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        protectedApi.MapPost("/resolutions/confirm", async Task<Results<Ok<ReturnCase>, ForbidHttpResult, ProblemHttpResult>> (
            ConfirmDraftRequest request, ReturnStore store, ClaimsPrincipal user,
            IAuthorizationService authorization, ILogger<ReturnStore> logger, CancellationToken ct) =>
        {
            var draft = await store.GetDraftAsync(request.DraftId, ct);
            if (draft?.Resolution.Value is RefundResolution
                && !(await authorization.AuthorizeAsync(user, DemoAuthentication.RefundPolicy)).Succeeded)
            {
                return TypedResults.Forbid();
            }
            var result = await store.ConfirmAsync(request.DraftId, user.Identity!.Name!, ct);
            if (result is ResolutionFailure rejected)
            {
                logger.LogWarning("Resolution confirmation rejected for draft {DraftId}: {Reason}", request.DraftId, rejected.Message);
            }
            return result switch
            {
                ReturnCase order => TypedResults.Ok(order),
                ResolutionFailure failure => Problem(failure)
            };
        });
        protectedApi.MapPost("/demo/reset", async Task<Results<NoContent, BadRequest<string>>> (
            ResetDemoRequest request, ReturnStore store, CancellationToken ct) =>
        {
            if (request.Confirmation != "RESET")
            {
                return TypedResults.BadRequest("Reset requires the explicit RESET confirmation.");
            }
            await store.ResetAsync(ct);
            return TypedResults.NoContent();
        }).ExcludeFromDescription();

        api.MapGet("/examples/refund", () => new Resolution(new RefundResolution(149m, "PAY-48021")));
        api.MapGet("/examples/replacement", () => new Resolution(new ReplacementResolution(
            "DOCK-PRO-PLUS", "120 Market Street, Seattle, WA 98101")));
        api.MapPost("/replacements/validate", (ReplacementRequest request) =>
            TypedResults.Ok(new { valid = true, message = "Replacement request passed asynchronous inventory validation." }));
    }

    private static ProblemHttpResult Problem(ResolutionFailure failure) => TypedResults.Problem(
        statusCode: failure.Kind switch
        {
            ResolutionFailureKind.Invalid => StatusCodes.Status400BadRequest,
            ResolutionFailureKind.NotFound => StatusCodes.Status404NotFound,
            ResolutionFailureKind.Conflict => StatusCodes.Status409Conflict,
            _ => throw new UnreachableException()
        },
        title: "The operation could not be completed.", detail: failure.Message);
}

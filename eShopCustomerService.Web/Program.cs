using System.Security.Claims;
using System.Diagnostics;
using eShopCustomerService.Web.Components;
using eShopCustomerService.Web.Data;
using eShopCustomerService.Web.Domain;
using eShopCustomerService.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("This sample uses a local demo sign-in and must only run in Development.");
}

builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents(options =>
{
    // Shorten the default three-minute retention to make disconnected recovery visible in the demo.
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromSeconds(10);
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<DemoAuthentication>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/sign-in";
    options.Cookie.Name = "eShop.CustomerService.Demo";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnValidatePrincipal = context =>
        context.HttpContext.RequestServices.GetRequiredService<DemoAuthentication>().ValidatePrincipalAsync(context);
    options.Events.OnRedirectToLogin = context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("CustomerService", policy => policy.RequireAuthenticatedUser().RequireRole("Representative"));
    options.AddPolicy(DemoAuthentication.RefundPolicy, policy =>
        policy.RequireAuthenticatedUser().RequireRole("Representative").RequireRole("RefundApprover"));
    options.AddPolicy(DemoAuthentication.PermissionControlsPolicy, policy =>
        policy.RequireAuthenticatedUser().RequireRole("Representative").RequireClaim(DemoAuthentication.SessionClaim));
});
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<ReturnStore>();
builder.Services.AddSingleton<InventoryService>();
builder.Services.AddSingleton<ResolutionContextLoader>();
builder.Services.AddSingleton<CircuitMonitor>();
builder.Services.AddScoped<CircuitHandler, CustomerServiceCircuitHandler>();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "eShop.CustomerCare.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.IdleTimeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddOutputCache(options => options.AddPolicy("PublicHelp",
    policy => policy.Expire(TimeSpan.FromMinutes(1))));
builder.Services.AddDataProtection().SetApplicationName("eShopCustomerService.Web");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseSession();
app.UseOutputCache();
app.MapStaticAssets();
app.MapDefaultEndpoints();

app.MapPost("/demo/sign-in", async ([FromForm] string returnUrl, HttpContext context, DemoAuthentication authentication) =>
{
    await authentication.SignInAsync(context);
    var destination = returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
        ? returnUrl : "/";
    return TypedResults.LocalRedirect(destination);
}).AllowAnonymous().ExcludeFromDescription();

app.MapPost("/demo/auth-refresh/permission", async (
    [FromForm] bool refundApproval, HttpContext context, DemoAuthentication authentication) =>
{
    await authentication.SetRefundApprovalAsync(context, refundApproval);
    return TypedResults.LocalRedirect("/auth-refresh");
}).RequireAuthorization(DemoAuthentication.PermissionControlsPolicy).ExcludeFromDescription();

app.MapPost("/demo/sign-out", async ([FromForm] string confirmation, HttpContext context, DemoAuthentication authentication) =>
{
    await authentication.SignOutAsync(context);
    return TypedResults.LocalRedirect("/sign-in");
}).RequireAuthorization("CustomerService").ExcludeFromDescription();

app.MapPost("/demo/reset", async ([FromForm] string confirmation, ReturnStore store, CancellationToken ct) =>
{
    if (confirmation != "RESET")
    {
        return Results.BadRequest("Reset requires the explicit RESET confirmation.");
    }
    await store.ResetAsync(ct);
    return Results.LocalRedirect("/");
}).RequireAuthorization("CustomerService").ExcludeFromDescription();

app.MapPost("/returns/confirm", async (
    [FromForm] Guid draftId, ReturnStore store, ClaimsPrincipal user, IAuthorizationService authorization,
    ILogger<Program> logger, CancellationToken ct) =>
{
    var draft = await store.GetDraftAsync(draftId, ct);
    if (draft?.Resolution.Value is RefundResolution
        && !(await authorization.AuthorizeAsync(user, DemoAuthentication.RefundPolicy)).Succeeded)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Refund approval is required.");
    }
    var result = await store.ConfirmAsync(draftId, user.Identity!.Name!, ct);
    if (result is ResolutionFailure rejected)
    {
        logger.LogWarning("Resolution confirmation rejected for draft {DraftId}: {Reason}", draftId, rejected.Message);
    }
    return result switch
    {
        ReturnCase order => Results.LocalRedirect($"/returns/{order.OrderId}?completed=true"),
        ResolutionFailure failure => Results.LocalRedirect($"/review/{draftId}?error={Uri.EscapeDataString(failure.Message)}")
    };
}).RequireAuthorization("CustomerService").ExcludeFromDescription();

var api = app.MapGroup("/api");
var protectedApi = api.MapGroup("").RequireAuthorization("CustomerService");
protectedApi.MapGet("/returns", (ReturnStore store, CancellationToken ct) => store.GetCasesAsync(ct));
protectedApi.MapGet("/returns/{orderId:int}", async (int orderId, ReturnStore store, CancellationToken ct) =>
    await store.GetCaseAsync(orderId, ct) is { } result ? Results.Ok(result) : Results.NotFound())
    .Produces<ReturnCase>().Produces(StatusCodes.Status404NotFound);
protectedApi.MapGet("/returns/{orderId:int}/audit", (int orderId, ReturnStore store, CancellationToken ct) =>
    store.GetAuditAsync(orderId, ct));
protectedApi.MapGet("/inventory", (ReturnStore store, CancellationToken ct) => store.GetInventoryAsync(ct));
protectedApi.MapGet("/drafts/{id:guid}", async (Guid id, ReturnStore store, CancellationToken ct) =>
    await store.GetDraftAsync(id, ct) is { } draft ? Results.Ok(draft) : Results.NotFound())
    .Produces<ResolutionDraft>().Produces(StatusCodes.Status404NotFound);
protectedApi.MapPost("/drafts", async Task<Results<Created<ResolutionDraft>, ForbidHttpResult, ProblemHttpResult>> (
    SaveResolutionRequest request, ReturnStore store, ClaimsPrincipal user,
    IAuthorizationService authorization, ILogger<Program> logger, CancellationToken ct) =>
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
        ResolutionFailure failure => TypedResults.Problem(
            statusCode: failure.Kind switch
            {
                ResolutionFailureKind.Invalid => StatusCodes.Status400BadRequest,
                ResolutionFailureKind.NotFound => StatusCodes.Status404NotFound,
                ResolutionFailureKind.Conflict => StatusCodes.Status409Conflict,
                _ => throw new UnreachableException()
            },
            title: "The operation could not be completed.", detail: failure.Message)
    };
}).ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict);

// Minimal APIs + C# unions
api.MapGet("/examples/refund", () => new Resolution(new RefundResolution(149m, "PAY-48021")));
api.MapGet("/examples/replacement", () => new Resolution(new ReplacementResolution(
    "DOCK-PRO-PLUS", "120 Market Street, Seattle, WA 98101")));

// Minimal APIs + async validation
api.MapPost("/replacements/validate", (ReplacementRequest request) =>
    TypedResults.Ok(new { valid = true, message = "Replacement request passed asynchronous inventory validation." }));

app.MapOpenApi();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options =>
    options.ConfigureConnection = connection =>
    {
        connection.CloseOnAuthenticationExpiration = true;
        connection.MaximumAuthenticationExpiration = TimeSpan.FromSeconds(20);
    })
    .WithBrowserOptions(options =>
        options.AddAutoPause(pause => pause.HiddenDelay = TimeSpan.FromSeconds(8)));

await app.Services.GetRequiredService<ReturnStore>().InitializeAsync();
app.Run();

public partial class Program;

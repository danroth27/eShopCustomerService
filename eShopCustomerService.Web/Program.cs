using System.Security.Claims;
using eShopCustomerService.Web.Components;
using eShopCustomerService.Web.Data;
using eShopCustomerService.Web.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("This sample uses a local demo sign-in and must only run in Development.");
}

builder.AddServiceDefaults();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/sign-in";
    options.Cookie.Name = "eShop.CustomerService.Demo";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
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
    options.AddPolicy("CustomerService", policy => policy.RequireAuthenticatedUser().RequireRole("Representative")));
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddSingleton<ReturnStore>();
builder.Services.AddSingleton<InventoryService>();

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapDefaultEndpoints();

app.MapPost("/demo/sign-in", async ([FromForm] string returnUrl, HttpContext context) =>
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, "demo-representative"),
        new Claim(ClaimTypes.Name, "Taylor Brooks"),
        new Claim(ClaimTypes.Role, "Representative")
    };
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    var destination = returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
        ? returnUrl : "/";
    return TypedResults.LocalRedirect(destination);
}).AllowAnonymous().ExcludeFromDescription();

app.MapPost("/demo/sign-out", async ([FromForm] string confirmation, HttpContext context) =>
{
    await context.SignOutAsync();
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
    [FromForm] Guid draftId, ReturnStore store, ClaimsPrincipal user, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        var result = await store.ConfirmAsync(draftId, user.Identity!.Name!, ct);
        return Results.LocalRedirect($"/returns/{result.OrderId}?completed=true");
    }
    catch (DomainException exception)
    {
        logger.LogWarning(exception, "Resolution confirmation rejected for draft {DraftId}", draftId);
        return Results.LocalRedirect($"/review/{draftId}?error={Uri.EscapeDataString(exception.Message)}");
    }
}).RequireAuthorization("CustomerService").ExcludeFromDescription();

var api = app.MapGroup("/api").RequireAuthorization("CustomerService");
api.MapGet("/returns", (ReturnStore store, CancellationToken ct) => store.GetCasesAsync(ct));
api.MapGet("/returns/{orderId:int}", async (int orderId, ReturnStore store, CancellationToken ct) =>
    await store.GetCaseAsync(orderId, ct) is { } result ? Results.Ok(result) : Results.NotFound())
    .Produces<ReturnCase>().Produces(StatusCodes.Status404NotFound);
api.MapGet("/returns/{orderId:int}/audit", (int orderId, ReturnStore store, CancellationToken ct) =>
    store.GetAuditAsync(orderId, ct));
api.MapGet("/inventory", (ReturnStore store, CancellationToken ct) => store.GetInventoryAsync(ct));
api.MapGet("/drafts/{id:guid}", async (Guid id, ReturnStore store, CancellationToken ct) =>
    await store.GetDraftAsync(id, ct) is { } draft ? Results.Ok(draft) : Results.NotFound())
    .Produces<ResolutionDraft>().Produces(StatusCodes.Status404NotFound);

api.MapGet("/examples/refund", () => new Resolution(new RefundResolution(149m, "PAY-48021")));
api.MapGet("/examples/replacement", () => new Resolution(new ReplacementResolution(
    "DOCK-PRO-PLUS", "120 Market Street, Seattle, WA 98101")));
api.MapPost("/replacements/validate", (ReplacementRequest request) =>
    TypedResults.Ok(new { valid = true, message = "Replacement request passed asynchronous inventory validation." }));
api.MapPost("/drafts", async (
    SaveResolutionRequest request, ReturnStore store, ClaimsPrincipal user, CancellationToken ct) =>
{
    var draft = await store.SaveDraftAsync(request, user.Identity!.Name!, ct);
    return TypedResults.Created($"/api/drafts/{draft.Id}", draft);
});

app.MapOpenApi().RequireAuthorization("CustomerService");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.Services.GetRequiredService<ReturnStore>().InitializeAsync();
app.Run();

public partial class Program;

internal sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DomainException domain)
        {
            return false;
        }
        logger.LogWarning(domain, "Business operation rejected");
        context.Response.StatusCode = domain.StatusCode;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "The operation could not be completed.",
            Detail = domain.Message,
            Status = domain.StatusCode
        }, cancellationToken);
        return true;
    }
}

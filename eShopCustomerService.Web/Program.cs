using System.Security.Claims;
using eShopCustomerService.Web.Components;
using eShopCustomerService.Contracts.Domain;
using eShopCustomerService.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using eShopCustomerService.Web.Agentic;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("This sample uses a local demo sign-in and must only run in Development.");
}

builder.AddServiceDefaults();
builder.Services.AddDataProtection().SetApplicationName("eShop.CustomerService.Agentic");
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<DemoAuthentication>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.LoginPath = "/sign-in";
    options.Cookie.Name = builder.Configuration["Demo:CookieName"] ?? "eShop.CustomerService.Demo";
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
builder.Services.AddCustomerServiceValidation();
builder.Services.AddProblemDetails();
CustomerServiceClient.AddServices(builder.Services, builder.Configuration);
builder.Services.AddCustomerServiceAgent(builder.Configuration);

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapApiForwarding();
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

app.MapPost("/demo/reset", async (
    [FromForm] string confirmation, CustomerServiceClient api, HttpContext context, CancellationToken ct) =>
{
    if (confirmation != "RESET")
    {
        return Results.BadRequest("Reset requires the explicit RESET confirmation.");
    }
    await api.ResetAsync(context.User, ct);
    return Results.LocalRedirect("/");
}).RequireAuthorization("CustomerService").ExcludeFromDescription();

app.MapPost("/returns/confirm", async (
    [FromForm] Guid draftId, CustomerServiceClient api, ClaimsPrincipal user, IAuthorizationService authorization,
    ILogger<Program> logger, CancellationToken ct) =>
{
    var draft = await api.GetDraftAsync(draftId, user, ct);
    if (draft?.Resolution.Value is RefundResolution
        && !(await authorization.AuthorizeAsync(user, DemoAuthentication.RefundPolicy)).Succeeded)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Refund approval is required.");
    }
    var result = await api.ConfirmAsync(draftId, user, ct);
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

app.MapRazorComponents<App>().AddInteractiveServerRenderMode(options =>
    options.ConfigureConnection = connection =>
    {
        connection.CloseOnAuthenticationExpiration = true;
        connection.MaximumAuthenticationExpiration = TimeSpan.FromSeconds(20);
    });

app.Run();

public partial class Program;

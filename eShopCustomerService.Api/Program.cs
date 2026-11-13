using eShopCustomerService.Api.Agentic;
using eShopCustomerService.Api.Data;
using eShopCustomerService.Api.Services;
using eShopCustomerService.Api.Endpoints;
using eShopCustomerService.Contracts.Domain;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;

namespace eShopCustomerService.Api;

public sealed class ApiProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException("This sample uses a local demo sign-in and must only run in Development.");
        }
        builder.AddServiceDefaults();
        builder.Services.AddDataProtection().SetApplicationName("eShop.CustomerService.Agentic");
        builder.Services.AddSingleton<DemoAuthentication>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
        {
            options.Cookie.Name = builder.Configuration["Demo:CookieName"] ?? "eShop.CustomerService.Demo";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Events.OnValidatePrincipal = context =>
                context.HttpContext.RequestServices.GetRequiredService<DemoAuthentication>().ValidatePrincipalAsync(context);
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
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
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddSingleton<ReturnStore>();
        builder.Services.AddSingleton<IInventoryService, InventoryService>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<CaseTools>();

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAgentToolEndpoints();
        app.MapReturnEndpoints();
        app.MapDemoSessionEndpoints();
        app.MapOpenApi();
        app.MapDefaultEndpoints();
        await app.Services.GetRequiredService<ReturnStore>().InitializeAsync();
        app.Run();
    }
}

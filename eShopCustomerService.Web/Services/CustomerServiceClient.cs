using System.Net;
using System.Security.Claims;
using eShopCustomerService.Contracts;
using eShopCustomerService.Contracts.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Http.Resilience;

namespace eShopCustomerService.Web.Services;

public sealed class CustomerServiceClient(
    IHttpClientFactory clients, IOptionsMonitor<CookieAuthenticationOptions> cookieOptions)
{
    public static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<CustomerServiceClient>();
        services.AddScoped<WorkspaceService>();
        services.AddScoped<IInventoryService>(provider => provider.GetRequiredService<WorkspaceService>());
        services.AddHttpClient("application-api", client =>
        {
            var endpoint = new Uri(configuration["CustomerService:ApiUrl"]
                ?? throw new InvalidOperationException("The application API URL is required. Start the Aspire AppHost."));
            if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("The local application API requires a loopback HTTPS URL.");
            }
            client.BaseAddress = endpoint;
        }).RemoveAllResilienceHandlers()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
    }

    public HttpRequestMessage CreateRequest(HttpMethod method, string path, ClaimsPrincipal user, object? body = null)
    {
        var message = new HttpRequestMessage(method, path);
        if (user.Identity?.IsAuthenticated == true)
        {
            var options = cookieOptions.Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket = new AuthenticationTicket(user,
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30) },
                CookieAuthenticationDefaults.AuthenticationScheme);
            message.Headers.Add("Cookie", $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}");
        }
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }
        return message;
    }

    public async Task<AgentIdentityResponse> CreateSessionAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/demo/sessions", user, new CreateDemoSessionRequest(), ct);
        return await ReadAsync<AgentIdentityResponse>(response, ct);
    }

    public async Task<AgentIdentityResponse?> GetIdentityAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/agent-tools/identity", user, null, ct);
        return response.StatusCode == HttpStatusCode.Unauthorized ? null : await ReadAsync<AgentIdentityResponse>(response, ct);
    }

    public async Task<AgentIdentityResponse> SetRefundApprovalAsync(ClaimsPrincipal user, bool allowed, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Put, "/api/demo/session/permission", user,
            new DemoPermissionRequest { RefundApproval = allowed }, ct);
        return await ReadAsync<AgentIdentityResponse>(response, ct);
    }

    public async Task SignOutAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Delete, "/api/demo/session", user, null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/demo/reset", user,
            new ResetDemoRequest { Confirmation = "RESET" }, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ReturnCase>> GetCasesAsync(ClaimsPrincipal user, CancellationToken ct) =>
        await GetAsync<List<ReturnCase>>("/api/returns", user, ct);

    public async Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(ClaimsPrincipal user, CancellationToken ct) =>
        await GetAsync<List<InventoryItem>>("/api/inventory", user, ct);

    public Task<bool> IsAvailableAsync(string productCode, ClaimsPrincipal user, CancellationToken ct) =>
        GetAsync<bool>($"/api/inventory/{Uri.EscapeDataString(productCode)}/availability", user, ct);

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int orderId, ClaimsPrincipal user, CancellationToken ct) =>
        await GetAsync<List<AuditEntry>>($"/api/returns/{orderId}/audit", user, ct);

    public async Task<ReturnCase?> GetCaseAsync(int orderId, ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/returns/{orderId}", user, null, ct);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<ReturnCase>(response, ct);
    }

    public async Task<ResolutionDraft?> GetDraftAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/drafts/{id}", user, null, ct);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await ReadAsync<ResolutionDraft>(response, ct);
    }

    public async Task<ResolutionResult<ResolutionDraft>> SaveDraftAsync(
        SaveResolutionRequest request, ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/drafts", user, request, ct);
        return await ReadOutcomeAsync<ResolutionDraft>(response, ct);
    }

    public async Task<ResolutionResult<ReturnCase>> ConfirmAsync(Guid id, ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/resolutions/confirm", user, new ConfirmDraftRequest(id), ct);
        return await ReadOutcomeAsync<ReturnCase>(response, ct);
    }

    private async Task<T> GetAsync<T>(string path, ClaimsPrincipal user, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, user, null, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, ClaimsPrincipal user, object? body, CancellationToken ct)
    {
        using var client = clients.CreateClient("application-api");
        using var message = CreateRequest(method, path, user, body);
        return await client.SendAsync(message, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<T>(ct);
        return result is not null ? result : throw new InvalidOperationException("The application API returned an empty response.");
    }

    private static async Task<ResolutionResult<T>> ReadOutcomeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return new(await ReadAsync<T>(response, ct));
        }
        var kind = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => ResolutionFailureKind.Invalid,
            HttpStatusCode.NotFound => ResolutionFailureKind.NotFound,
            HttpStatusCode.Conflict => ResolutionFailureKind.Conflict,
            _ => throw new HttpRequestException("The application API rejected the operation.", null, response.StatusCode)
        };
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(ct)
            ?? throw new InvalidOperationException("The application API returned an empty failure response.");
        return new(new ResolutionFailure(problem.Detail
            ?? throw new InvalidOperationException("The application API omitted the failure details."), kind));
    }
}

using System.Globalization;
using System.Net;
using eShopCustomerService.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace eShopCustomerService.AgentServer.Services;

public interface ICustomerServiceClient
{
    Task<AgentIdentityResponse?> GetIdentityAsync(CancellationToken cancellationToken);
    Task<CaseContext> GetCaseContextAsync(CancellationToken cancellationToken);
    Task<CaseWorkspaceState> ConfirmResolutionAsync(
        string productCode, string deliveryAddress, CancellationToken cancellationToken,
        string resolutionKind = "replacement", decimal? refundAmount = null, string? paymentReference = null);
}

public sealed class CustomerServiceClient(
    IHttpClientFactory clients, IHttpContextAccessor accessor, ILogger<CustomerServiceClient> logger) : ICustomerServiceClient
{
    private HttpContext Request => accessor.HttpContext
        ?? throw new InvalidOperationException("An agent HTTP request is required.");

    private int OrderId => int.Parse(Request.Request.RouteValues["orderId"]?.ToString()
        ?? throw new InvalidOperationException("An order route is required."), CultureInfo.InvariantCulture);

    public async Task<AgentIdentityResponse?> GetIdentityAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/agent-tools/identity", null, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return null;
        }
        return await ReadAsync<AgentIdentityResponse>(response, cancellationToken);
    }

    public async Task<CaseContext> GetCaseContextAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/agent-tools/returns/{OrderId}", null, cancellationToken);
        return await ReadAsync<CaseContext>(response, cancellationToken);
    }

    public async Task<CaseWorkspaceState> ConfirmResolutionAsync(
        string productCode, string deliveryAddress, CancellationToken cancellationToken,
        string resolutionKind = "replacement", decimal? refundAmount = null, string? paymentReference = null)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/agent-tools/returns/{OrderId}/confirm",
            new ConfirmResolutionRequest
            {
                ProductCode = productCode,
                DeliveryAddress = deliveryAddress,
                ResolutionKind = resolutionKind,
                RefundAmount = refundAmount,
                PaymentReference = paymentReference
            }, cancellationToken);
        return await ReadAsync<CaseWorkspaceState>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var client = clients.CreateClient("customerservice");
        using var message = new HttpRequestMessage(method, path);
        message.Headers.Add("Cookie", Request.Request.Headers.Cookie.ToString());
        if (body is not null)
        {
            message.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(message, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                ? await response.Content.ReadFromJsonAsync<ProblemDetails>(cancellationToken) : null;
            logger.LogWarning("Customer-service API rejected an agent operation with status {StatusCode}", response.StatusCode);
            throw new HttpRequestException(problem?.Detail ?? "The customer-service application rejected this operation.",
                null, response.StatusCode);
        }
        var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        if (result is null)
        {
            logger.LogError("Customer-service API returned an empty agent response");
            throw new InvalidOperationException("The customer-service application returned an empty response.");
        }
        return result;
    }
}

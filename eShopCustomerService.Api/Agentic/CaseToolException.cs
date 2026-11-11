namespace eShopCustomerService.Api.Agentic;

public sealed class CaseToolException(string message, int statusCode) : InvalidOperationException(message)
{
    public int StatusCode { get; } = statusCode;
}

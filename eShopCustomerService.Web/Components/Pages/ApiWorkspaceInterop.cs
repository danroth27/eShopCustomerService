using Microsoft.JSInterop;

namespace eShopCustomerService.Web.Components.Pages;

public sealed record ApiWorkspaceResponse(int Status, string Body);

internal sealed class ApiRequestException(string message, Exception innerException) : Exception(message, innerException);

internal sealed class ApiWorkspaceInterop(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? module;

    public async Task<ApiWorkspaceResponse> SendAsync(string path, string method, string? body)
    {
        try
        {
            module ??= await js.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Contracts.razor.js");
            return await module.InvokeAsync<ApiWorkspaceResponse>("send", path, method, body);
        }
        catch (JSException exception)
        {
            throw new ApiRequestException("The API workspace could not complete its browser request.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (module is not null)
            {
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // A disconnected Blazor Server circuit has already released its browser context.
        }
    }
}

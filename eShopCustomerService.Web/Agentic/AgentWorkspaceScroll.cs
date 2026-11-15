using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using eShopCustomerService.Web.Components.Shared;

namespace eShopCustomerService.Web.Agentic;

public sealed class AgentWorkspaceScroll(IJSRuntime js, ResourceAssetCollection assets) : IAsyncDisposable
{
    private const string ModulePath = "Components/Shared/ResolutionAgentPanel.razor.js";
    private const string InitializeMethod = "initialize";
    private const string DisposeMethod = "dispose";
    private IJSObjectReference? _module;
    private IJSObjectReference? _tracker;

    public async ValueTask InitializeAsync(ElementReference panel, DotNetObjectReference<ResolutionAgentPanel> conversation)
    {
        try
        {
            _module = await js.InvokeAsync<IJSObjectReference>("import", $"./{assets[ModulePath]}");
            _tracker = await _module.InvokeAsync<IJSObjectReference>(InitializeMethod, panel, conversation);
        }
        catch (JSDisconnectedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_tracker is not null)
            {
                await _tracker.InvokeVoidAsync(DisposeMethod);
                await _tracker.DisposeAsync();
            }
            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
    }
}

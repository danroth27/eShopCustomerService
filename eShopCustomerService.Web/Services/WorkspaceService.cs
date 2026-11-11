using eShopCustomerService.Contracts.Domain;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace eShopCustomerService.Web.Services;

public sealed class WorkspaceService : IInventoryService, IDisposable
{
    private readonly CustomerServiceClient api;
    private readonly AuthenticationStateProvider authentication;
    private Task<AuthenticationState>? currentAuthentication;

    public WorkspaceService(CustomerServiceClient api, AuthenticationStateProvider authentication)
    {
        this.api = api;
        this.authentication = authentication;
        authentication.AuthenticationStateChanged += AuthenticationChanged;
    }

    private async Task<ClaimsPrincipal> UserAsync() =>
        (await (currentAuthentication ??= authentication.GetAuthenticationStateAsync())).User;

    private void AuthenticationChanged(Task<AuthenticationState> state) => currentAuthentication = state;

    public void Dispose() => authentication.AuthenticationStateChanged -= AuthenticationChanged;

    public async Task<IReadOnlyList<ReturnCase>> GetCasesAsync(CancellationToken ct = default) =>
        await api.GetCasesAsync(await UserAsync(), ct);

    public async Task<ReturnCase?> GetCaseAsync(int orderId, CancellationToken ct = default) =>
        await api.GetCaseAsync(orderId, await UserAsync(), ct);

    public async Task<IReadOnlyList<InventoryItem>> GetInventoryAsync(CancellationToken ct = default) =>
        await api.GetInventoryAsync(await UserAsync(), ct);

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int orderId, CancellationToken ct = default) =>
        await api.GetAuditAsync(orderId, await UserAsync(), ct);

    public async Task<ResolutionDraft?> GetDraftAsync(Guid id, CancellationToken ct = default) =>
        await api.GetDraftAsync(id, await UserAsync(), ct);

    public async Task<ResolutionResult<ResolutionDraft>> SaveDraftAsync(
        SaveResolutionRequest request, CancellationToken ct = default) =>
        await api.SaveDraftAsync(request, await UserAsync(), ct);

    public async Task<bool> IsAvailableAsync(string productCode, CancellationToken cancellationToken) =>
        await api.IsAvailableAsync(productCode, await UserAsync(), cancellationToken);
}

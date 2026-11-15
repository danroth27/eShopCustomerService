# eShop Customer Service

A customer-care and returns sample for **.NET 11**, demonstrating modern web development, Blazor Server resilience and scalability, and agentic UI with the experimental Blazor AI components.

This is a standalone companion scenario for [dotnet/eShop](https://github.com/dotnet/eShop), not a connected eShop deployment. Orders, customers, and carrier activity are fictional. Inventory allocation and resolution records use local SQLite; no real payments, shipments, or customer messages are sent.

## Run locally

Install the **.NET 11 SDK** (`11.0.100` or later) and **[Aspire CLI](https://aspire.dev/get-started/install-cli/) 13.6**. The non-AI workflows require no containers or cloud configuration.

From the repository root:

```powershell
aspire start
aspire wait api
aspire wait customerservice
aspire wait crossorigin
aspire describe
```

Open the HTTPS **customerservice** URL shown by Aspire and select **Enter demo workspace**. Use **Reset demo data** on the overview to restore the seeded cases and stock. Stop the app with `aspire stop`.

Alternatively, open `eShopCustomerService.sln` in Visual Studio with .NET 11 support and the ASP.NET and web development workload. Set **eShopCustomerService.AppHost** as the startup project and run its **https** profile.

The database is `eShopCustomerService.Api\App_Data\returns.db` and survives restarts. When running multiple instances, use `aspire start --isolated` and separate browser profiles to avoid localhost cookie collisions.

## Enable the agent (optional)

Use an existing Microsoft Foundry model deployment and an Azure identity with inference access to that resource. Configure the resource's root endpoint, not a project URL or a URL ending in `openai/v1/`:

```powershell
dotnet user-secrets set "Parameters:agentic-foundry-endpoint" "https://<resource>.services.ai.azure.com/" --project eShopCustomerService.AppHost
dotnet user-secrets set "Parameters:agentic-foundry-model" "<deployed-model-name>" --project eShopCustomerService.AppHost
az login
```

Start the app as above, then run `aspire wait agentserver`. Setting the endpoint automatically enables the agent; without it, the ordinary editor remains available. To run without AI even when configured, set `$env:Agentic__Enabled = "false"` in the launch terminal. Remove that override with `Remove-Item Env:Agentic__Enabled` before starting an AI-enabled session.

The agent uses `DefaultAzureCredential`; no API key is required. Model calls may incur charges. Keep real settings and credentials outside source control; user secrets are for local development, not a production vault.

## Explore the sample

| Feature | Start here | Key source |
|---|---|---|
| Static SSR, enhanced navigation/forms, QuickGrid, caching, validation, TempData, and HTTP session | `/care` while signed out | [Care pages](eShopCustomerService.Web/Components/Pages/Care), [CustomerHelpLibrary](eShopCustomerService.Web/Components/Shared/CustomerHelpLibrary.razor) |
| Blazor Server prerendering, state recovery, Auto Pause, and graceful shutdown | `/returns/48021`, `/operations` | [ReturnDetails](eShopCustomerService.Web/Components/Pages/ReturnDetails.razor), [ResolutionWorkspace](eShopCustomerService.Web/Components/Shared/ResolutionWorkspace.razor), [CircuitMonitor](eShopCustomerService.Web/Services/CircuitMonitor.cs) |
| C# unions, OpenAPI 3.2, async validation, and CSRF protection | [Demo.http](eShopCustomerService.Web/Demo.http); **Review replacement → Open cross-origin test** | [Resolution contract](eShopCustomerService.Contracts/Domain/Resolution.cs), [API endpoints](eShopCustomerService.Api/Endpoints/ReturnEndpoints.cs) |
| Authentication refresh without reconnecting | **Open permission controls** in an unresolved return editor | [ResolutionEditor](eShopCustomerService.Web/Components/Shared/ResolutionEditor.razor), [AuthRefresh](eShopCustomerService.Web/Components/Pages/AuthRefresh.razor) |
| Variable-height virtualization and scroll anchoring | **Service queue** | [ServiceQueue](eShopCustomerService.Web/Components/Pages/ServiceQueue.razor) |
| Agent investigation, shared editor state, predictive reply edits, and human approval | `/returns/48024` with AI enabled | [ResolutionAgentPanel](eShopCustomerService.Web/Components/Shared/ResolutionAgentPanel.razor), [tool renderers](eShopCustomerService.Web/Components/Shared/ResolutionToolRenderers.razor), [AgentHosting](eShopCustomerService.AgentServer/Agentic/AgentHosting.cs) |

For `Demo.http`, set `@host` to the current HTTPS **customerservice** URL. Its five requests show the two union cases, OpenAPI, and unavailable/available replacement validation; they require no sign-in and do not allocate stock. Use an unresolved case such as **48023** for UI validation after confirming **48021**.

**Architecture:** the Blazor Web frontend calls AgentServer over AG-UI. Both call the application API, which owns SQLite, authorization rules, validation, and transactional confirmation. Contracts holds shared models; AppHost orchestrates the resources. Foundry credentials belong only to AgentServer.

The original agent demo used `ChatPage`; the persistent UI now composes the SDK's `MessageList` and `MessageInput` around one restored `AgentContext`.

## Important limitations

- **Local demonstration only.** All hosts require Development. Demo sign-in is not production authentication; do not expose the app publicly or use real customer data. Confirmation is authorized and revalidated by the API, not merely by the approval UI.
- **Recovery is not durable conversation storage.** Completed exchanges and accepted editor state participate in circuit recovery. Unfinished exchanges, pending approvals, and unaccepted previews are not resumed. Full reloads start fresh conversations; recovery can fail or expire. Transcript size/retention limits need further hardening for production.
- **Timings are accelerated for demos:** eight-second hidden-tab Auto Pause, ten-second disconnected-circuit retention, and roughly ten-second authentication refresh. Keep the editor visible beside permission controls or source when demonstrating an unchanged circuit. Graceful process recovery requires compatible state and the same Data Protection keys.
- **Current AG-UI limitations:** complete **Show review** before **Request approval** in the same conversation ([#2393](https://github.com/ag-ui-protocol/ag-ui/issues/2393)). End the conversation after an approval decision; reload to start fresh before another agent action ([#3364](https://github.com/ag-ui-protocol/ag-ui/issues/3364)). The sample does not patch the SDK.

## Development checks

Stop Aspire before building or formatting to release resource assemblies:

```powershell
dotnet test eShopCustomerService.Tests
dotnet build eShopCustomerService.AppHost
dotnet format eShopCustomerService.sln --verify-no-changes
```

Integration tests use isolated temporary databases. Experimental API warnings remain visible.

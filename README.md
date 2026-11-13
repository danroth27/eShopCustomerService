# eShop Customer Service

A cohesive customer-care and returns workspace showing web development features in **.NET 11**: static SSR, enhanced navigation and forms, client-side validation, caching, component persistence and circuit pause/resume, C# unions with JSON and OpenAPI, shared asynchronous validation, automatic CSRF protection, authentication refresh without reconnecting, and an optional agent integrated into the resolution editor using the experimental Blazor AI components.

This is a self-contained companion scenario for [dotnet/eShop](https://github.com/dotnet/eShop), not an upstream extension or a connected deployment. Its fictional orders and inventory are stored locally. It does not call eShop services, issue payments, or create shipments.

## Run locally

Use the stable .NET 11 SDK (`11.0.100` or later) and the [Aspire CLI](https://aspire.dev/get-started/install-cli/) 13.6. No containers or cloud configuration are required for the non-AI workflows. To include the agent, configure an existing Microsoft Foundry model deployment using the [agentic demo setup](#agentic-customer-service-demo).

```powershell
aspire start
aspire wait api
aspire wait customerservice
aspire wait crossorigin
aspire describe
```

Open the **HTTPS** `customerservice` URL shown by Aspire and select **Enter demo workspace**. This creates a local representative session, not a verified identity. All hosts refuse to start outside Development.

Use `aspire stop` to stop the application. The database in `eShopCustomerService.Api\App_Data\returns.db` survives restarts; **Reset demo data** on the overview restores the seeded scenario. When upgrading an older checkout, stop the app and copy its existing `eShopCustomerService.Web\App_Data\returns.db` to that API location before the first start; do not overwrite an existing API database. Never use real customer data or expose these local demonstration hosts to a network.

When running from a git worktree alongside another instance, use `aspire start --isolated`.

The interface follows the system light/dark preference. A full page load with `?scoutTheme=light` or `?scoutTheme=dark` overrides it for that document.

## Visual Studio HTTP requests

Open `eShopCustomerService.sln` in Visual Studio with .NET 11 support and the ASP.NET and web development workload. Open `eShopCustomerService.Web\Demo.http` and use **Send Request** above each numbered request. Start the app with Aspire first; do not start a second copy from Visual Studio. Set `@host` to the HTTPS `customerservice` URL if it differs from `https://localhost:7153`.

The file shows refund JSON, replacement JSON, the generated OpenAPI document, an unavailable replacement returning HTTP 400, and an available replacement returning HTTP 200. In the OpenAPI response, show `"openapi": "3.2.0"` and search for `"Resolution":` to locate `components.schemas.Resolution.anyOf`.

These non-mutating examples and the OpenAPI document intentionally need no sign-in, cookies, or copied credentials. The validation requests do not create drafts or allocate inventory. Authentication is not part of the keynote walkthrough; enter the browser's demo workspace before presenting so the final form submission still demonstrates a real cookie-bearing request.

## Walkthrough

1. Open featured return **48021** for Alex Morgan's USB-C Dock Pro. Compare the refund and replacement options.
2. In Visual Studio, show `eShopCustomerService.Contracts\Domain\Resolution.cs` and the example Minimal APIs in `eShopCustomerService.Api\Endpoints\ReturnEndpoints.cs`. Send requests **1-3** in `Demo.http`: the two union cases serialize without a wrapper, and the generated OpenAPI document describes them with `anyOf`.
3. Back on the return, choose **USB-C Dock Pro**, which is unavailable. The field shows an asynchronous availability check, then an inline error. Switch rapidly between models to see older field validation superseded.
4. Show `eShopCustomerService.Contracts\Domain\ReplacementRequest.cs`, the form's `DataAnnotationsValidator`, and the validation endpoint in `eShopCustomerService.Api\Endpoints\ReturnEndpoints.cs`. Send requests **4-5** in `Demo.http`: automatic validation returns HTTP 400 for the unavailable model before the handler runs, then HTTP 200 for the available model. The same model attribute is used by the Blazor form, whose inventory check calls the API.
5. Choose **USB-C Dock Pro+** on the return, wait for availability, and select **Review replacement**. A draft does not reserve stock.
6. From review, **Open cross-origin test** in its separate tab. Keep browser Network tools open and attempt confirmation. The actual POST returns HTTP 400. Its `Sec-Fetch-Site` is `same-site`, but its origin differs. The authentication cookie is still sent because both HTTPS localhost endpoints belong to the same site; protection is not simulated by missing authentication.
7. Return to the original review tab and confirm. One unit is allocated and the typed resolution and audit entry are committed together. Reposting the same confirmation cannot allocate another unit.
8. Try a refund on **48022**, then reset from the overview for another walkthrough.

For presentation, start the resources, reset the demo data, and warm the interactive pages. Send all five HTTP requests once, reopen return 48021, and bookmark the source locations. Open the cross-origin test from the newly created review page so it contains the current draft ID; close stale test tabs between rehearsals. No startup, live code edits, or authentication setup is needed during the segment. The app's API workspace remains available for exploration but is not part of the keynote flow.

## Authentication refresh demo

Authentication refresh is part of every workspace session. There is no separate Start/End mode. The app keeps its normal long-lived authentication cookie and sets `MaximumAuthenticationExpiration` to 20 seconds on the Blazor connection. The built-in client normally reauthenticates about every 10 seconds. This short connection-authentication window makes the feature visible during a presentation; it is not the cookie lifetime or a production revocation recommendation.

1. Enter the workspace and open an unresolved return: normally 48021, or 48022 after the CSRF walkthrough resolves 48021. Open browser Network tools before loading the editor and retain the log. Use **Open permission controls**, then move the controls tab to a second window beside the editor. Keep the editor visible throughout this demo so the eight-second Auto Pause policy doesn't replace its circuit.
2. Select the available **USB-C Dock Pro+** and change the delivery address to `200 Demo Way`. Leave this replacement unsaved. Note the **Editor instance** value above the form.
3. In the permission-controls window, select **Remove refund approval**. This issues an updated cookie in the same browser. Watch the original editor without reloading; the next scheduled refresh normally changes **Refund approval: allowed** to **removed** within about 10 seconds. The editor instance, selected product, and unsaved address remain unchanged.
4. Select **Refund**. The review action is replaced by a permission warning. This is not just hidden UI: the interactive save handler, draft API, and HTTP confirmation endpoint each independently enforce the refund policy. Replacement preparation remains available.
5. In the controls window, select **Restore refund approval**. Wait for the original editor's badge and **Review refund** action to return. Switch back to **Replacement** to show the unsaved address and product are still present. No draft or resolution has been committed.
6. Leave refund approval restored and close the controls window before continuing other demos. No session restart or extra sign-in is needed.

For a technical close-up, filter Network for `_blazor`: the existing WebSocket remains open while automatic `POST /_blazor/refresh` requests return HTTP 200 with the browser's current cookie. No new WebSocket is created. The visible editor instance is a component-instance marker, not a SignalR connection ID; use the actual Network trace to substantiate the unchanged connection.

**What is real:** the controls update a per-session server-side demo record and issue a new encrypted authentication cookie through ordinary HTTP. The cookie handler rebuilds the principal from that record during HTTP reauthentication. SignalR's refresh endpoint authenticates a separate HTTP request and applies its principal to the existing connection. Blazor Server propagates that user to the circuit's authentication state. The component never polls permissions, calls `SetAuthenticationState`, or fakes a reconnect.

**What this does not promise:** a role change in an external database does not automatically rewrite a cookie or reach every session promptly. The application must obtain updated credentials or revalidate authorization data. This walkthrough demonstrates applying those updated credentials without losing the connection or unsaved work, not automatic role synchronization or instant revocation.

Static pages retain the normal cookie lifetime. Live connections still use `CloseOnAuthenticationExpiration`: browser or OS suspension can interrupt a circuit if refresh cannot run, even while its sign-in cookie remains valid. The existing demo permission records are stored in SQLite, so restarting the service does not invalidate otherwise valid sign-in cookies. Sign-out or a new sign-in deletes the prior record, and expired records cannot be renewed.

ASP.NET Core still issues, protects, authenticates, renews, and expires the cookies. `DemoPermissions` contains only the existing session identifier, refund-approval flag, and expiration aligned with the cookie lifetime. It is demo-specific authorization data, not a hand-written cookie handler, HTTP session provider, account system, or replacement for Identity. Keep the SQLite database and Data Protection keys/application name unchanged for the local restart walkthrough.

This is **cookie authentication**, not ASP.NET Core Identity or an OAuth access-token acquisition demo. There is no Identity security-stamp check or claim that Identity templates automatically configure permission revalidation.

### SignalR-first explanation

> SignalR connections can outlive the credentials that created them. In .NET 11, authentication refresh lets the client present current credentials and the server update the existing connection's principal without disconnecting. Your application still obtains updated credentials or revalidates the user. For direct SignalR applications, enable authentication refresh on the hub and configure the client to refresh before expiration. Blazor Server builds on that same machinery and propagates the refreshed principal to its components. Here we'll see updated cookie credentials reach an existing editor while the representative's unsaved work stays intact.

For direct hubs, enable `EnableAuthenticationRefresh`, consider `CloseOnAuthenticationExpiration`, and supply current credentials through the normal cookie or access-token configuration. The JavaScript client opts into automatic scheduling with `withAuthenticationRefresh`; the .NET client supports automatic refresh and `WithAuthenticationRefresh` configuration. Automatic scheduling needs a usable authentication lifetime. Configure expiration/reauthentication limits appropriately for the authentication handler; these accelerated demo timings are not production recommendations.

Do not imply that token expiration previously disconnected every connection by default, that refresh instantly detects all revocations, or that it reruns already executing hub calls. Ordinary hubs normally restrict changes to the routed user; Blazor Server has a specialized identity-transition policy. Refresh does not automatically recalculate SignalR groups or `Clients.User` routing. Permission data must be revalidated by the authentication handler, and authorized UI is not a substitute for server-side enforcement.

Sources: [SignalR authentication refresh](https://learn.microsoft.com/aspnet/core/signalr/authn-and-authz?view=aspnetcore-11.0#authentication-refresh), [Blazor circuit integration](https://github.com/dotnet/aspnetcore/pull/68221), and [Identity-template adoption](https://github.com/dotnet/aspnetcore/pull/68663).

## Virtualization demo

Open **Service queue** from the navigation or **Virtualized service queue** in the overview's presenter controls. It renders 10,000 simulated queue entries linked to the real seeded returns, using the built-in .NET 11 `Virtualize` component. Changes are page-local and never update orders, stock, or audit history.

1. Jump to row **5000**. Note the highlighted **Jump target** and the mixture of compact and expanded rows.
2. Select **Expand preceding row**. Earlier context grows above the target; the target should remain at the same viewport offset. Toggle **Show details** on a visible row to demonstrate variable heights.
3. Select **Add at start** while reading in the middle. The same case remains in view despite its new list index.
4. On a freshly loaded queue, **Start of queue** is already selected. Select **Add at start** to show the newest entry at the top.
5. Select **Go to end**, then choose **End of queue** and **Add at end**. The viewport follows the new entry. Scroll upward with the mouse wheel and add again: following disengages while the representative reads older cases.
6. **Neither edge** disables edge pinning; **Both edges** combines the start/end policies. Reload the page to restore the original queue.

Show `Components\Pages\ServiceQueue.razor`: `Items`, `AnchorMode`, and `ScrollToItemAsync` are framework APIs. Row-height measurement and scroll restoration are not implemented with custom JavaScript. `ItemSize` supplies an initial estimate, not a fixed row height. Programmatic jumps disengage active following in this SDK; select the desired edge policy after jumping, changing through **Neither edge** if that policy was already selected. For the live talk, use the jump, preceding-row expansion, middle-of-list prepend, and end-follow steps; no database reset is needed.

## Agentic customer-service demo

The existing **resolution editor** includes a service agent beside its normal controls. Use delayed-delivery case **48024**, separate from the other talks' primary return. It uses a live Microsoft Foundry model through Microsoft Agent Framework, the AG-UI HTTP/SSE protocol, and the experimental Blazor AI components. `/returns/48024` is the primary route; `/agentic/48024` remains an alias for the same page, not a separate workspace.

The representative and agent share one `CaseWorkspaceState`: resolution type, selected replacement product, delivery address, unsent reply, refund amount/payment reference, current refund permission, product options, validation messages, and resolution status. The current editor state is the draft; there is no separate save step, saved-draft ID, or `prepare_resolution` tool in the agent workflow. Every AG-UI request carries the current editor state. The normal frontend tool `update_resolution_editor` changes the same controls, and `show_resolution_review` displays those current values without persistence. Refund amount and original payment reference are read-only order details. Only the approval-protected `confirm_resolution` tool records a resolution, and the API revalidates its exact values, current permission, and stock.

The agent uses `get_case_context` for initial investigation, explicit evidence refreshes, or missing case facts. It is instructed to reuse evidence for ordinary editor changes, reply previews, review, and confirmation rather than retrieving the same case on every turn. Current editor state still travels with each request, and the API revalidates business rules.

The Blazor **Web** frontend calls **AgentServer** over AG-UI. Both call the same application **Api**, which owns SQLite, case data, inventory, drafts, confirmation rules, and demo-session authority. Neither backend calls the frontend. **Contracts** shares the JSON models without sharing database access. Foundry configuration and model credentials belong only to AgentServer.

The web host retains thin forwarding routes for the existing API-workspace page and `Demo.http` URLs, plus its ordinary browser form handlers. They forward to the API; they contain no persistence or business implementation. Blazor components and agent tools call the API directly, not those forwarding routes. Web and Api share the local development user's ASP.NET Core data-protection key ring and application name; AgentServer only forwards the opaque demo cookie.

Configure an existing Foundry resource endpoint and deployed model in the AppHost's user secrets. Use the resource's root URL, not a project URL or a URL ending in `openai/v1/`; that suffix is added by the application. Authenticate using an Azure identity with inference access to that resource (`DefaultAzureCredential` is used; no API key is needed). Requests may incur model charges.

User secrets are stored outside the repository, not in `launchSettings.json` or `appsettings.json`; never commit real resource settings or credentials. The AppHost retains the agentic branch's user-secrets ID so existing model configuration still works after merging. The Foundry endpoint is exposed to Aspire as a secret parameter. User secrets are a local development store, not an encrypted production vault.

```powershell
dotnet user-secrets set "Parameters:agentic-foundry-endpoint" "https://<resource>.services.ai.azure.com/" --project eShopCustomerService.AppHost
dotnet user-secrets set "Parameters:agentic-foundry-model" "<deployed-model-name>" --project eShopCustomerService.AppHost
az login
aspire start --isolated --non-interactive
aspire wait api --non-interactive
aspire wait agentserver --non-interactive
aspire wait customerservice --non-interactive
aspire describe --non-interactive
```

Configuring `Parameters:agentic-foundry-endpoint` enables the agent resources automatically. With no endpoint configured, Aspire starts only **api**, **customerservice**, and **crossorigin**, and the ordinary editor remains available. Set `Agentic:Enabled` to `false` in AppHost configuration (or `$env:Agentic__Enabled = "false"` before starting) to run without AI even when endpoint secrets exist. Explicitly enabling the agent requires a Foundry endpoint.

Use isolated mode when other eShop checkouts are running. Discover this instance's HTTPS address through Aspire, enter its demo workspace, and use **Reset demo data** in its overview before rehearsing. Each worktree has its own database. The AppHost uses the `eShop.CustomerService.Agentic` cookie, distinct from older checkouts; two instances of this merged app still share that cookie name on localhost, so use separate browser profiles when running them simultaneously. Stop only this worktree's AppHost before rebuilding.

For a Visual Studio launch, configure the user secrets once, set `eShopCustomerService.AppHost` as the startup project, select the **https** profile, and press **F5**. The dashboard should show **customerservice**, **agentserver**, **api**, **crossorigin**, and the **foundry** external service. Use the HTTPS `customerservice` link from that dashboard rather than a URL from a previous CLI run.

1. Open the existing resolution editor at `/returns/48024`. Select **Investigate** and show incremental rendering, the formatted Markdown recommendation, and the backend evidence card: the original model has no stock; the compatible upgrade has 12 units.
2. Choose **Refund** or **Replacement**, select a replacement model if appropriate, edit the address, and type directly into **Customer reply**. Alternatively ask the agent to change these fields. Both use the same editor state; nothing needs saving.
3. Ask: `Preview a warmer customer reply that mentions Taylor's workshop and includes the current delivery address. Change only the reply text; do not confirm a resolution.` The reply editor shows a predictive preview using your latest address. Select **Discard reply edits** to restore the previous text. Alternatively, **Accept reply edits** keeps the revised text in this page's workspace. Neither choice sends a message or allocates stock.
4. Select **Show review**, even if you have not used the agent before. The ordinary frontend tool `show_resolution_review` automatically opens a read-only review of the current editor values and returns its result through AG-UI. A generated `ResolutionReviewBlock : FunctionInvocationContentBlock` renders the frontend tool call and result; no extra user action is required to execute it. Close the review and wait for **Ready**. Reviewing does not authorize confirmation. The editor's separate **Review replacement** / **Review refund** buttons retain the traditional saved-draft review page for the HTTP/form and CSRF walkthrough; they do not call the agent.
5. Select **Request approval**, inspect the exact current resolution details, and select **Approve resolution** once. The server revalidates the authenticated representative, selected case, approved values, refund permission if applicable, and stock for replacements, then atomically records the resolution and audit without creating a saved draft. Show the confirmed resolution and refreshed audit history on the same page. A replacement allocates exactly one unit locally; a refund allocates no stock.
6. End the agent conversation after approval. The reply remains visible and unsent. Do not demonstrate reject-then-retry or send another agent turn after an approval decision while the AG-UI SDK [approval-history issue #3364](https://github.com/ag-ui-protocol/ag-ui/issues/3364) remains unresolved.

Complete the frontend review before requesting approval in the same conversation, without reloading. AGUI.Server 1.0.1 can suppress backend approval interrupts when a frontend tool is declared but has not completed ([#2393](https://github.com/ag-ui-protocol/ag-ui/issues/2393)). This demo sequence avoids the observed path; it is not an SDK fix or a general protocol requirement. The sample does not add a protocol shim.

After a rejected confirmation, the agent's continuation disables further tool calls so it acknowledges the decision without retrying or changing the editor. This does not fix the separate SDK approval-history issue: start a fresh conversation for any later action.

Agent-enabled return pages disable hidden-tab Auto Pause through `ConfigureBrowser`, because the agent conversation and pending approval are not persisted with the editor. Switching to source or slides must not silently replace that conversation. Non-AI return pages and the service queue retain the app-wide Auto Pause policy. Explicit server-requested pause and process replacement still end an agent conversation; start a fresh conversation afterward.

Rich text uses Markdig with a small mapper into the components' `RichTextContent` nodes. Original Markdown stays in history; paragraphs, headings, lists, emphasis, and code are formatted. Unsupported syntax stays literal, including raw HTML. Reply previews use the components' `SetPredictiveState`, `AcceptPredictiveState`, and `RejectPredictiveState` APIs, not a custom rollback system. Committed editor values, including accepted reply text, participate in component persistence; an unaccepted predictive preview is not persisted as an accepted edit. Reply text remains unsent. Frontend review and reply editing need no JavaScript; the small collocated JS module only follows transcript output while the representative is reading at the bottom.

**Boundaries:** carrier activity and all customer data are seeded; there are no real carrier calls, shipments, emails, or payments. Confirmation persistence and inventory allocation are real local operations. The independent traditional HTTP/form demos retain their existing saved-draft APIs; the agent workflow does not use them. The approval UI is not authorization: each AG-UI request revalidates the current representative, and tools derive their actor and selected case from the authenticated HTTP request. MAF's in-memory session store retains the genuine pending approval for continuation, partitioned by the authenticated demo session and selected case; do not replace it with the no-op store or disable approval binding. Workspace state synchronizes at request boundaries, not between browsers. Conversation and pending approvals are page-local and do not survive reloads or restarts. Foundry supplies model inference; this demo does not deploy a Foundry Hosted Agent or implement an AG-UI-to-Invocations bridge. Experimental warnings remain visible.

## Code landmarks

| Story | File |
|---|---|
| Live JSON, OpenAPI, and async validation requests | `eShopCustomerService.Web\Demo.http` |
| Refund-or-replacement contract and structural JSON classifier | `eShopCustomerService.Contracts\Domain\Resolution.cs` |
| Shared asynchronous validation attribute | `eShopCustomerService.Contracts\Domain\ReplacementRequest.cs` |
| Database-backed availability check | `eShopCustomerService.Api\Services\InventoryService.cs` |
| Blazor validation, pending state, and typed union construction | `eShopCustomerService.Web\Components\Shared\ResolutionEditor.razor` |
| Typed union rendering | `eShopCustomerService.Web\Components\Shared\ResolutionSummary.razor` |
| Business Minimal APIs and `AddValidation` | `eShopCustomerService.Api\Endpoints\ReturnEndpoints.cs`, `eShopCustomerService.Api\Program.cs` |
| Protected browser form confirmation and app-wide Auto Pause | `eShopCustomerService.Web\Program.cs` |
| Atomic confirmation and audit history | `eShopCustomerService.Api\Data\ReturnStore.cs` |
| Independent-origin browser form | `eShopCustomerService.CrossOrigin\Program.cs` |
| Demo-session authority and permission revalidation | `eShopCustomerService.Api\Services\DemoAuthentication.cs` |
| HTTP permission controls, outside the live circuit | `eShopCustomerService.Web\Components\Pages\AuthRefresh.razor` |
| Resource orchestration and endpoint discovery | `eShopCustomerService.AppHost\AppHost.cs` |
| Variable-height queue, anchor policies, and jump-to-row | `eShopCustomerService.Web\Components\Pages\ServiceQueue.razor` |
| Shared resolution editor state, frontend edits, and AG-UI requests | `eShopCustomerService.Web\Components\Shared\ResolutionWorkspace.razor` |
| Agent chat panel and typed tool/approval rendering | `eShopCustomerService.Web\Components\Shared\ResolutionAgentPanel.razor`, `eShopCustomerService.Web\Components\Shared\ResolutionToolRenderers.razor` |
| Generated ordinary frontend review tool block | `eShopCustomerService.Web\Agentic\ResolutionReviewBlock.cs` |
| Direct customer-reply editing and predictive preview display | `eShopCustomerService.Web\Components\Shared\ResolutionReplyEditor.razor` |
| State snapshot mapping and predictive reply acceptance/rollback | `eShopCustomerService.Web\Agentic\CaseWorkspaceUpdates.cs` |
| Markdown-to-rich-text mapping at the streaming boundary | `eShopCustomerService.Web\Agentic\MarkdownChatClient.cs` |
| Foundry-backed MAF agent and authenticated AG-UI endpoint | `eShopCustomerService.AgentServer\Agentic\AgentHosting.cs` |
| Agent backend tools calling the application API | `eShopCustomerService.AgentServer\Services\CustomerServiceClient.cs` |
| Case-bound operations and existing draft/confirmation rules | `eShopCustomerService.Api\Agentic\CaseTools.cs` |
| Current workspace state carried into each agent request | `eShopCustomerService.AgentServer\Agentic\CaseStateAgent.cs` |
| Authenticated server-to-server AG-UI transport | `eShopCustomerService.Web\Agentic\AgentConnection.cs` |
| Blazor data access through the shared application API | `eShopCustomerService.Web\Services\WorkspaceService.cs`, `eShopCustomerService.Web\Services\CustomerServiceClient.cs` |
| Static customer care, validation, TempData, and HTTP session | `eShopCustomerService.Web\Components\Pages\Care` |
| Public cached QuickGrid region | `eShopCustomerService.Web\Components\Shared\CustomerHelpLibrary.razor` |
| Persistent customer context and conditional loading | `eShopCustomerService.Web\Components\Pages\ReturnDetails.razor` |
| Persistent unsaved resolution state | `eShopCustomerService.Web\Components\Shared\ResolutionWorkspace.razor`, `eShopCustomerService.Contracts\CaseWorkspaceState.cs` |
| Connected-circuit tracking, server-requested pause, and bounded shutdown | `eShopCustomerService.Web\Services\CircuitMonitor.cs` |

Inventory lookup includes a configurable 650 ms presentation delay (`Demo:ValidationDelayMilliseconds`). Availability still comes from SQLite. Validation faults do not count as successful checks. Confirmation always rechecks the business invariants inside its transaction.

## Server-rendering walkthrough

The customer journey and representative workflow are part of the same `customerservice` resource in the existing AppHost. No additional demo web application is needed.

Run this walkthrough without AI (`Agentic:Enabled=false`) so return pages use the accelerated hidden-tab Auto Pause policy.

1. Enter the representative workspace and reset demo data. Close interactive return tabs, select **Sign out**, and open `/care` in the same browser window. Stay signed out throughout the customer-care walkthrough, because the default output-cache policy bypasses authenticated responses. A separate browser profile or InPrivate window isn't needed.
2. Explore `/care/help`. Enhanced navigation preserves the document; QuickGrid sorting and pagination use query-string links without a circuit. The public help region is cached for two minutes, while the outer request timestamp changes. `/care/policy` uses a one-minute public output-cache policy.
3. On `/care/request`, submit Alex Morgan's request for the featured fictional order 48021. Client-side validation rejects invalid input without a POST; the server independently validates accepted submissions. The request is stored in SQLite and updates the very same return's reason, contact details, and audit history.
4. On `/care/received`, refresh once: the TempData thank-you message disappears, while HTTP session retains the receipt reference. The session cache is process-local; the durable request itself remains in SQLite.
5. In the same tab, select **Sign in**, then **Enter demo workspace**, and open the existing case page, `/returns/48021`. Briefly point out Alex's submitted reason and email. Show `[PersistentState]` on `Context` and the conditional load in `ReturnDetails.razor`. Place Aspire's resource logs beside the visible workspace, clear the displayed logs, filter to `Resolution context load`, and fully reload the same URL. Wait for the editor to become interactive: one context load serves prerendering and activation. Without persistence, the separate component instances would each load the context. Explain that baseline; no live code edit, rebuild, or restart is needed.
6. Demonstrate resilience on the same `/returns/48021` page. Select **Replacement**, choose USB-C Dock Pro+, enter `200 Demo Way`, and move focus out of the address field so the edits reach the server. Don't select **Review replacement**. Keep the workspace visible and watch Aspire's built-in connected/active circuit metrics in a side-by-side window, so the hidden-tab Auto Pause timer does not run. In the workspace tab's DevTools Network throttling dropdown, select **Offline**. Observe disconnected, then evicted. Once active circuits reaches zero, restore **No throttling** on the workspace tab. Allow recovery, or select **Retry** if automatic retries have ended. The unsaved selection and address remain without refreshing or restarting the process.
7. Demonstrate graceful deployment with the same unsaved replacement. Show the confirmed static form and `Response.OnCompleted` shutdown handler in `Operations.razor`, then the shutdown callback and bounded pause loop in `CircuitMonitor.cs`. Keep source beside the visible workspace. Immediately switch to `/operations`, select the confirmation checkbox, and select **Stop this service gracefully** before the eight-second Auto Pause timer fires. Show **Requested 1 pauses; 0 connections remain** in Aspire logs and the service stopping. Restart only **customerservice** in Aspire, or run `aspire resource customerservice restart`, leaving the AppHost running. Refresh Operations to show the new process ID, then resume the original workspace tab without refreshing it. The unsaved values and sign-in survive.
8. For proactive resource management, stay on `/returns/48021`. Confirm USB-C Dock Pro+ and `200 Demo Way`, click the editor heading to leave the address field, and wait for validation. Show `[PersistentState]` on `Draft` in `ResolutionWorkspace.razor`, the shared state owner. Its stable holder persists the latest immutable editor state in `Value`, not an unaccepted reply preview. Leave the unsaved form open; don't select **Review replacement**, which saves a database draft.
9. Switch to Aspire Metrics in another tab of the same window, hiding the editor. The app-wide eight-second hidden-tab policy releases the live circuit; allow for telemetry refresh. Returning restores the editable model and address automatically. No manual pause/resume commands are needed.

For presentation pacing, `Program.cs` sets disconnected-circuit retention to ten seconds instead of the framework's default three minutes. Network-loss detection takes additional time: wait for Aspire's active-circuit metric to reach zero rather than timing from the Offline selection. A short interruption reconnects to the retained circuit. Before eviction after a longer interruption, Blazor persists selected application state on the server and can restore it into a new circuit. This capability is available in .NET 10. The default recovery cache is process-local; this does not protect against a server crash or capture edits that haven't reached the server. The later cooperative pause demos use a protected browser-held snapshot instead.

`Program.cs` enables Auto Pause app-wide with `WithBrowserOptions(options => options.AddAutoPause(...))`; no query parameter is needed. The hidden-tab delay is shortened from the two-minute default to eight seconds for demonstration pacing. Keep the workspace visible while checking logs, permissions, or metrics in the other demonstrations; a side-by-side window prevents an unintended hidden-tab pause. This can also affect the authentication-refresh and virtualization demonstrations: transient UI state may reset when their interactive workspace pauses. Operations is a static Blazor page with a confirmed, server-handled shutdown form and requires representative authorization; it does not open a circuit or poll metrics. Its process ID is rendered on each page request. Before demonstrating circuit counts, start with a fresh workspace and verify one connected and one active circuit in Aspire; don't count retained circuits from earlier navigations as part of the demonstration. Close other live tabs before the process-replacement walkthrough, because shutdown requests pause for all connected circuits in this process.

The shutdown callback waits for cooperative pause with a bounded budget before the web server tears down connections. This is a local application-side demonstration, not a production traffic-draining deployment orchestrator. A crash, expired/incompatible recovery state, or full refresh can lose unsaved browser-held work. Durable saving, shared Data Protection, and multi-instance storage design are separate concerns.

## Verification

```powershell
dotnet test eShopCustomerService.Tests
dotnet build eShopCustomerService.AppHost
dotnet format eShopCustomerService.sln --verify-no-changes
```

Stop Aspire before building or formatting to release its resource assemblies. Integration tests use isolated temporary databases and cover union contracts, live OpenAPI generation, asynchronous API validation, authentication, rejected cross-origin forms, idempotent confirmation, stock races, rollback, persistence, reset, and permission enforcement with stale demo cookies.

For browser verification, rehearse the walkthrough above with no other live demo circuits. Check the customer handoff, one-load prerendering, disconnected-circuit recovery, graceful service replacement, and hidden-tab pause/resume. Restore **No throttling** after the interruption demo. Reset the fictional demo data and use a fresh browser window before presenting.

## Deliberate boundaries

The local seeded sign-in is **not production authentication**. Order and draft APIs, confirmation, and reset retain the representative policy and derive audit actors from that principal. The fixed union examples, validation-only endpoint, OpenAPI document, customer-care pages, and sample customer-request endpoint are anonymous. Customer requests concern only the featured fictional purchase, not an unrestricted public customer/order lookup. All hosts remain Development-only. The sample does not implement a production identity provider, per-customer authorization, real payment processing, or fulfillment.

The form endpoint relies on .NET 11's automatic CSRF protection without opting out or layering an antiforgery-token requirement over this demonstration. It binds a scalar draft ID, not a union-valued form field. Automatic form protection is not a security guarantee for JSON-only APIs. Workflow APIs still require authentication, authorization, validation, and authoritative business rules; the anonymous validation example only checks a request and cannot commit a business operation.

SQLite transactions protect this local workflow. Connecting it to eShop's independent ordering, payment, and inventory services would require service contracts, distributed-workflow design, and operational hardening beyond this sample.

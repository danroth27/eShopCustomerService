# eShop Customer Service

A focused returns-and-exchanges workspace showing web development features in **.NET 11**: C# unions with JSON and OpenAPI, shared asynchronous validation in Blazor and Minimal APIs, automatic CSRF protection for form submissions, and authentication refresh without reconnecting.

This is a self-contained companion scenario for [dotnet/eShop](https://github.com/dotnet/eShop), not an upstream extension or a connected deployment. Its fictional orders and inventory are stored locally. It does not call eShop services, issue payments, or create shipments. See [SPEC.md](SPEC.md) for the application specification.

## Run locally

Use the stable .NET 11 SDK (`11.0.100` or later) and the [Aspire CLI](https://aspire.dev/get-started/install-cli/) 13.6. No containers or cloud services are required.

```powershell
aspire start
aspire wait customerservice
aspire wait crossorigin
aspire describe
```

Open the **HTTPS** `customerservice` URL shown by Aspire and select **Enter demo workspace**. This creates a local representative session, not a verified identity. Both hosts refuse to start outside Development.

Use `aspire stop` to stop the application. The database in `eShopCustomerService.Web\App_Data\returns.db` survives restarts; **Reset demo data** on the overview restores the seeded scenario. Never use real customer data or expose these local demonstration hosts to a network.

The interface follows the system light/dark preference. A full page load with `?scoutTheme=light` or `?scoutTheme=dark` overrides it for that document.

## Visual Studio HTTP requests

Open `eShopCustomerService.sln` in Visual Studio with .NET 11 support and the ASP.NET and web development workload. Open `eShopCustomerService.Web\Demo.http` and use **Send Request** above each numbered request. Start the app with Aspire first; do not start a second copy from Visual Studio. Set `@host` to the HTTPS `customerservice` URL if it differs from `https://localhost:7153`.

The file shows refund JSON, replacement JSON, the generated OpenAPI document, an unavailable replacement returning HTTP 400, and an available replacement returning HTTP 200. In the OpenAPI response, show `"openapi": "3.2.0"` and search for `"Resolution":` to locate `components.schemas.Resolution.anyOf`.

These non-mutating examples and the OpenAPI document intentionally need no sign-in, cookies, or copied credentials. The validation requests do not create drafts or allocate inventory. Authentication is not part of the keynote walkthrough; enter the browser's demo workspace before presenting so the final form submission still demonstrates a real cookie-bearing request.

## Walkthrough

1. Open featured return **48021** for Alex Morgan's USB-C Dock Pro. Compare the refund and replacement options.
2. In Visual Studio, show `Domain\Resolution.cs` and the example Minimal APIs in `Program.cs`. Send requests **1-3** in `Demo.http`: the two union cases serialize without a wrapper, and the generated OpenAPI document describes them with `anyOf`.
3. Back on the return, choose **USB-C Dock Pro**, which is unavailable. The field shows an asynchronous availability check, then an inline error. Switch rapidly between models to see older field validation superseded.
4. Show `Domain\ReplacementRequest.cs`, the form's `DataAnnotationsValidator`, and the validation endpoint in `Program.cs`. Send requests **4-5** in `Demo.http`: automatic validation returns HTTP 400 for the unavailable model before the handler runs, then HTTP 200 for the available model. This is the same model attribute used by the Blazor form, not a second copy of the rule.
5. Choose **USB-C Dock Pro+** on the return, wait for availability, and select **Review replacement**. A draft does not reserve stock.
6. From review, **Open cross-origin test** in its separate tab. Keep browser Network tools open and attempt confirmation. The actual POST returns HTTP 400. Its `Sec-Fetch-Site` is `same-site`, but its origin differs. The authentication cookie is still sent because both HTTPS localhost endpoints belong to the same site; protection is not simulated by missing authentication.
7. Return to the original review tab and confirm. One unit is allocated and the typed resolution and audit entry are committed together. Reposting the same confirmation cannot allocate another unit.
8. Try a refund on **48022**, then reset from the overview for another walkthrough.

For presentation, start both resources, reset the demo data, and warm the interactive pages. Send all five HTTP requests once, reopen return 48021, and bookmark the source locations. Open the cross-origin test from the newly created review page so it contains the current draft ID; close stale test tabs between rehearsals. No startup, live code edits, or authentication setup is needed during the segment. The app's API workspace remains available for exploration but is not part of the keynote flow.

## Authentication refresh demo

Authentication refresh is part of every workspace session. There is no separate Start/End mode. The app keeps its normal long-lived authentication cookie and sets `MaximumAuthenticationExpiration` to 20 seconds on the Blazor connection. The built-in client normally reauthenticates about every 10 seconds. This short connection-authentication window makes the feature visible during a presentation; it is not the cookie lifetime or a production revocation recommendation.

1. Enter the workspace and open an unresolved return: normally 48021, or 48022 after the CSRF walkthrough resolves 48021. Open browser Network tools before loading the editor and retain the log.
2. Select the available **USB-C Dock Pro+** and change the delivery address to `200 Demo Way`. Leave this replacement unsaved. Note the **Editor instance** value above the form.
3. Use **Open permission controls** to open a second tab. Select **Remove refund approval**. This issues an updated cookie in the same browser. Return to the original editor without reloading; the next scheduled refresh normally changes **Refund approval: allowed** to **removed** within about 10 seconds. The editor instance, selected product, and unsaved address remain unchanged.
4. Select **Refund**. The review action is replaced by a permission warning. This is not just hidden UI: the interactive save handler, draft API, and HTTP confirmation endpoint each independently enforce the refund policy. Replacement preparation remains available.
5. In the controls tab, select **Restore refund approval**. Return to the original editor and wait for the badge and **Review refund** action to return. Switch back to **Replacement** to show the unsaved address and product are still present. No draft or resolution has been committed.
6. Leave refund approval restored and close the controls tab before continuing other demos. No session restart or extra sign-in is needed.

For a technical close-up, filter Network for `_blazor`: the existing WebSocket remains open while automatic `POST /_blazor/refresh` requests return HTTP 200 with the browser's current cookie. No new WebSocket is created. The visible editor instance is a component-instance marker, not a SignalR connection ID; use the actual Network trace to substantiate the unchanged connection.

**What is real:** the controls update a per-session server-side demo record and issue a new encrypted authentication cookie through ordinary HTTP. The cookie handler rebuilds the principal from that record during HTTP reauthentication. SignalR's refresh endpoint authenticates a separate HTTP request and applies its principal to the existing connection. Blazor Server propagates that user to the circuit's authentication state. The component never polls permissions, calls `SetAuthenticationState`, or fakes a reconnect.

**What this does not promise:** a role change in an external database does not automatically rewrite a cookie or reach every session promptly. The application must obtain updated credentials or revalidate authorization data. This walkthrough demonstrates applying those updated credentials without losing the connection or unsaved work, not automatic role synchronization or instant revocation.

Static pages retain the normal cookie lifetime. Live connections still use `CloseOnAuthenticationExpiration`: browser or OS suspension can interrupt a circuit if refresh cannot run, even while its sign-in cookie remains valid. Session records live only in this process and are removed on sign-out or replacement; after restarting the app, sign in normally again.

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

## Razor tooling demo

The `demo/razor-tooling` branch is the runnable starting point; `main` contains the completed edits. Both branches retain the same refund handling, authorization, and validation. The starting branch commits the unused using directive and formatting issues, so no missing handlers, unsaved-buffer staging, or method restoration is needed.

Before starting the app for a rehearsal:

```powershell
git switch demo/razor-tooling
```

For the Hot Reload segment, open the solution in Visual Studio, set **eShopCustomerService.AppHost** as the startup project, and run it with Hot Reload enabled. Do not leave a separate Aspire CLI instance of this app running. Start on this branch before the earlier security demos and stay on it throughout the talk; do not switch branches or restart the app while demonstrating preserved editor state.

The root `.editorconfig` already sets `csharp_space_after_keywords_in_control_flow_statements = true` for C# and Razor. Keep format-on-save off for this walkthrough so the deliberate cleanup examples remain staged. Disable Copilot inline suggestions for the Emmet step.

1. In `eShopCustomerService.Web\Components\Pages\Home.razor`, below the paragraph under **Designed for a clear decision**, type `ul>li*3` and press **Tab**. Fill the three items with **Confirm the customer's symptoms**, **Check replacement availability**, and **Explain the next step**. Leave this document unsaved until the Hot Reload demonstration is complete.
2. In `Components\Shared\ResolutionEditor.razor`, invoke **Rename** on the refund button's `SaveRefundAsync` reference and rename it to `ReviewRefundAsync`. Show that both the Razor binding and C# method update, without changing the method body or disabling the refund workflow.
3. Invoke **Remove and Sort Usings**, then **Format Document**. Show `@using System.Text` removed, the refund button's attributes aligned, and `if(` becoming `if (` according to `.editorconfig`.
4. Change **Resolve this return** to **Choose the right resolution**. Save only `ResolutionEditor.razor`. Show the already-open return editor update through Hot Reload without refreshing, restarting, or navigating.
5. After showing Hot Reload, save the completed checklist in `Home.razor`. These two files should now match `main`; compare with `git diff main -- eShopCustomerService.Web\Components\Pages\Home.razor eShopCustomerService.Web\Components\Shared\ResolutionEditor.razor`.

To repeat, stop the demo app, close these two documents, and discard any unsaved rehearsal edits. While still on `demo/razor-tooling`, restore the committed starting point:

```powershell
git restore --source=demo/razor-tooling --staged --worktree -- eShopCustomerService.Web\Components\Pages\Home.razor eShopCustomerService.Web\Components\Shared\ResolutionEditor.razor
```

This discards rehearsal changes in **only those two files**. Do not use it for edits you intend to keep. Reopen the documents, start the app, and prepare the normal browser scenario. No database reset is needed specifically for the Razor edits. To inspect the completed version instead, switch to `main` after restoring the rehearsal files.

## Code landmarks

| Story | File |
|---|---|
| Live JSON, OpenAPI, and async validation requests | `eShopCustomerService.Web\Demo.http` |
| Refund-or-replacement contract and structural JSON classifier | `eShopCustomerService.Web\Domain\Resolution.cs` |
| Shared asynchronous validation attribute | `eShopCustomerService.Web\Domain\ReplacementRequest.cs` |
| Database-backed availability check | `eShopCustomerService.Web\Services\InventoryService.cs` |
| Blazor validation, pending state, and typed union construction | `eShopCustomerService.Web\Components\Shared\ResolutionEditor.razor` |
| Typed union rendering | `eShopCustomerService.Web\Components\Shared\ResolutionSummary.razor` |
| Minimal APIs, `AddValidation`, and protected form confirmation | `eShopCustomerService.Web\Program.cs` |
| Atomic confirmation and audit history | `eShopCustomerService.Web\Data\ReturnStore.cs` |
| Independent-origin browser form | `eShopCustomerService.CrossOrigin\Program.cs` |
| Opt-in demo cookies and per-session permission revalidation | `eShopCustomerService.Web\Services\DemoAuthentication.cs` |
| HTTP permission controls, outside the live circuit | `eShopCustomerService.Web\Components\Pages\AuthRefresh.razor` |
| Resource orchestration and endpoint discovery | `eShopCustomerService.AppHost\AppHost.cs` |
| Variable-height queue, anchor policies, and jump-to-row | `eShopCustomerService.Web\Components\Pages\ServiceQueue.razor` |

Inventory lookup includes a configurable 650 ms presentation delay (`Demo:ValidationDelayMilliseconds`). Availability still comes from SQLite. Validation faults do not count as successful checks. Confirmation always rechecks the business invariants inside its transaction.

## Verification

```powershell
dotnet test eShopCustomerService.Tests
dotnet build eShopCustomerService.AppHost
dotnet format eShopCustomerService.sln --verify-no-changes
```

Stop Aspire before building or formatting to release its resource assemblies. Integration tests use isolated temporary databases and cover union contracts, live OpenAPI generation, asynchronous API validation, authentication, rejected cross-origin forms, idempotent confirmation, stock races, rollback, persistence, reset, and permission enforcement with stale demo cookies.

## Deliberate boundaries

The local seeded sign-in is **not production authentication**. Order and draft APIs, confirmation, and reset retain the representative policy and derive audit actors from that principal. Only the two fixed union examples, the validation-only endpoint, and the OpenAPI document are anonymous for the HTTP-file demonstration. Both hosts remain Development-only. The sample does not implement a production identity provider, per-customer authorization, real payment processing, or fulfillment.

The form endpoint relies on .NET 11's automatic CSRF protection without opting out or layering an antiforgery-token requirement over this demonstration. It binds a scalar draft ID, not a union-valued form field. Automatic form protection is not a security guarantee for JSON-only APIs. Workflow APIs still require authentication, authorization, validation, and authoritative business rules; the anonymous validation example only checks a request and cannot commit a business operation.

SQLite transactions protect this local workflow. Connecting it to eShop's independent ordering, payment, and inventory services would require service contracts, distributed-workflow design, and operational hardening beyond this sample.

# eShop Customer Service

A cohesive customer-care and returns workspace showing web development features in **.NET 11**: static SSR, enhanced navigation and forms, client-side validation, caching, component persistence and circuit pause/resume, C# unions with JSON and OpenAPI, shared asynchronous validation, automatic CSRF protection, and authentication refresh without reconnecting.

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

When running from a git worktree alongside another instance, use `aspire start --isolated`.

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
| Static customer care, validation, TempData, and HTTP session | `eShopCustomerService.Web\Components\Pages\Care` |
| Public cached QuickGrid region | `eShopCustomerService.Web\Components\Shared\CustomerHelpLibrary.razor` |
| Persistent customer context and opt-in inactivity pause | `eShopCustomerService.Web\Components\Pages\ReturnDetails.razor` |
| Selected unsaved resolution state | `eShopCustomerService.Web\Domain\ResolutionEditorState.cs` |
| Circuit metrics, server-requested pause, and bounded shutdown | `eShopCustomerService.Web\Services\CircuitMonitor.cs` |

Inventory lookup includes a configurable 650 ms presentation delay (`Demo:ValidationDelayMilliseconds`). Availability still comes from SQLite. Validation faults do not count as successful checks. Confirmation always rechecks the business invariants inside its transaction.

## Server-rendering walkthrough

The customer journey and representative workflow are part of the same `customerservice` resource in the existing AppHost. No additional demo web application is needed.

1. Enter the representative workspace and reset demo data. Open `/care` in a separate anonymous browser context, such as InPrivate; leave interactive return tabs closed for the initial no-circuit demonstration. Keep the customer anonymous for the whole-response cache example, because the default output-cache policy bypasses authenticated responses.
2. Explore `/care/help`. Enhanced navigation preserves the document; QuickGrid sorting and pagination use query-string links without a circuit. The public help region is cached for two minutes, while the outer request timestamp changes. `/care/policy` uses a one-minute public output-cache policy.
3. On `/care/request`, submit Alex Morgan's request for the featured fictional order 48021. Client-side validation rejects invalid input without a POST; the server independently validates accepted submissions. The request is stored in SQLite and updates the very same return's reason, contact details, and audit history.
4. On `/care/received`, refresh once: the TempData thank-you message disappears, while HTTP session retains the receipt reference. The session cache is process-local; the durable request itself remains in SQLite.
5. Compare `/rendering-baseline/48021` with `/returns/48021?continuity=true`. The baseline deliberately repeats its context load during prerendering and activation. The normal return view uses `[PersistentState]` to reuse its loaded context.
6. Select USB-C Dock Pro+, enter `200 Demo Way`, and select **Preview without saving**. This preview and its selected values are component recovery state. Unlike **Review replacement**, preview does not save a database draft.
7. Pause/resume manually with `await Blazor.pauseCircuit()` and `await Blazor.resumeCircuit()` in the browser console. Then hide the preview tab for about ten seconds while observing `/operations`. The opt-in eight-second inactivity policy releases the live circuit; returning restores a new editor instance with the same selected data.
8. With the preview active, immediately switch to `/operations` and select **Stop this service gracefully**. Confirm the stop before the inactivity timer fires. Restart **customerservice** in the Aspire dashboard, or run `aspire resource customerservice restart`. Resume the original workspace tab without refreshing it; the process and editor instance change, but the unsaved preview survives.

Only the `continuity=true` view enables the short inactivity timer, so the separate authentication-refresh and virtualization demos retain their existing behavior. Operations uses HTTP polling and requires representative authorization; it does not open a circuit. Close other live tabs before the process-replacement walkthrough, because shutdown requests pause for all connected circuits in this process.

The shutdown callback waits for cooperative pause with a bounded budget before the web server tears down connections. This is a local application-side demonstration, not a production traffic-draining deployment orchestrator. A crash, expired/incompatible recovery state, or full refresh can lose unsaved browser-held work. Durable saving, shared Data Protection, and multi-instance storage design are separate concerns.

## Verification

```powershell
dotnet test eShopCustomerService.Tests
dotnet build eShopCustomerService.AppHost
dotnet format eShopCustomerService.sln --verify-no-changes
```

Stop Aspire before building or formatting to release its resource assemblies. Integration tests use isolated temporary databases and cover union contracts, live OpenAPI generation, asynchronous API validation, authentication, rejected cross-origin forms, idempotent confirmation, stock races, rollback, persistence, reset, and permission enforcement with stale demo cookies.

For single-user browser checks, start an isolated AppHost first, then:

```powershell
Set-Location server-rendering-tools
npm ci
$env:DEMO_URL = '<HTTPS customerservice URL shown by Aspire>'
npm run verify
npm run verify:auto-pause
```

The browser check resets fictional demo data and exercises customer handoff, caching, forms, component persistence, pause/resume, authentication refresh, and real cross-origin rejection. The native Edge check verifies actual tab visibility, rather than synthesizing a visibility event. Both create and close only their own browsers. Run sequentially without other live circuits.

To include actual service replacement in `npm run verify`, set `VERIFY_RESTART=1` and `DEMO_APPHOST` to the full path of the owned isolated AppHost project. That check deliberately stops and restarts only `customerservice`; it does not stop the AppHost or the independent test resource. Azure/load experiments are separate and remain deferred.

## Deliberate boundaries

The local seeded sign-in is **not production authentication**. Order and draft APIs, confirmation, and reset retain the representative policy and derive audit actors from that principal. The fixed union examples, validation-only endpoint, OpenAPI document, and customer-care pages are anonymous. Customer requests concern only the featured fictional purchase, not an unrestricted public customer/order lookup. Both hosts remain Development-only. The sample does not implement a production identity provider, per-customer authorization, real payment processing, or fulfillment.

The form endpoint relies on .NET 11's automatic CSRF protection without opting out or layering an antiforgery-token requirement over this demonstration. It binds a scalar draft ID, not a union-valued form field. Automatic form protection is not a security guarantee for JSON-only APIs. Workflow APIs still require authentication, authorization, validation, and authoritative business rules; the anonymous validation example only checks a request and cannot commit a business operation.

SQLite transactions protect this local workflow. Connecting it to eShop's independent ordering, payment, and inventory services would require service contracts, distributed-workflow design, and operational hardening beyond this sample.

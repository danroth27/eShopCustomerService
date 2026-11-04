# eShop Customer Service

A focused returns-and-exchanges workspace showing web development features in **.NET 11**: C# unions with JSON and OpenAPI, shared asynchronous validation in Blazor and Minimal APIs, and automatic CSRF protection for form submissions.

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
| Resource orchestration and endpoint discovery | `eShopCustomerService.AppHost\AppHost.cs` |

Inventory lookup includes a configurable 650 ms presentation delay (`Demo:ValidationDelayMilliseconds`). Availability still comes from SQLite. Validation faults do not count as successful checks. Confirmation always rechecks the business invariants inside its transaction.

## Verification

```powershell
dotnet test eShopCustomerService.Tests
dotnet build eShopCustomerService.AppHost
dotnet format eShopCustomerService.sln --verify-no-changes
```

Stop Aspire before building or formatting to release its resource assemblies. Integration tests use isolated temporary databases and cover union contracts, live OpenAPI generation, asynchronous API validation, authentication, rejected cross-origin forms, idempotent confirmation, stock races, rollback, persistence, and reset.

## Deliberate boundaries

The local seeded sign-in is **not production authentication**. Order and draft APIs, confirmation, and reset retain the representative policy and derive audit actors from that principal. Only the two fixed union examples, the validation-only endpoint, and the OpenAPI document are anonymous for the HTTP-file demonstration. Both hosts remain Development-only. The sample does not implement a production identity provider, per-customer authorization, real payment processing, or fulfillment.

The form endpoint relies on .NET 11's automatic CSRF protection without opting out or layering an antiforgery-token requirement over this demonstration. It binds a scalar draft ID, not a union-valued form field. Automatic form protection is not a security guarantee for JSON-only APIs. Workflow APIs still require authentication, authorization, validation, and authoritative business rules; the anonymous validation example only checks a request and cannot commit a business operation.

SQLite transactions protect this local workflow. Connecting it to eShop's independent ordering, payment, and inventory services would require service contracts, distributed-workflow design, and operational hardening beyond this sample.

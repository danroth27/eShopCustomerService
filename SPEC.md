# eShop Customer Service

A self-contained customer-service companion demo for [dotnet/eShop](https://github.com/dotnet/eShop). It explores a returns capability that could sit alongside eShop's ordering and catalog services. It does not currently call those services, consume their events, or change the upstream application.

## Experience

A customer-service representative opens order 48021, reviews its return, and chooses a refund or replacement. The workspace presents the order, customer context, activity history, and resolution editor without navigating away. A separate review screen makes the consequential action explicit. Confirmation produces a persistent resolution and audit entry.

Use a restrained enterprise visual design: warm neutral surfaces, a narrow navigation rail, clear typography, high-contrast hierarchy, a single rose accent, responsive panels, and no external fonts or image dependencies. The interface should read well at presentation scale and remain usable on a laptop. Use fictional customers and original product illustrations.

## Application model

Aspire orchestrates two local resources:

- `customerservice`: a .NET 11 Blazor Web App with per-page Blazor Server interactivity, Minimal APIs, and SQLite persistence. The editor is interactive; sign-in, review, confirmation, and API responses use ordinary HTTP. Keeping API and UI in one host makes cookie authentication and business-rule reuse explicit without inventing a distributed transaction.
- `crossorigin`: an independent ASP.NET Core test site that posts a real browser form to the customer-service confirmation endpoint. Aspire supplies the target endpoint. It never simulates rejection or disables protection.

Both use service defaults and Aspire telemetry. No containers, paid services, external identity provider, or cloud deployment are needed.

## Contracts and validation

`Resolution` is a C# union of `RefundResolution` and `ReplacementResolution`. Configure structural JSON classification for the distinctly shaped cases. Use the union in draft JSON requests, stored resolutions, API responses, and the typed Blazor summary component. Generated OpenAPI must describe its alternatives with `anyOf`.

A shared `ReplacementRequest` declares an async inventory validation attribute. Its validator performs a cancellable inventory lookup. Blazor's `DataAnnotationsValidator` and a Minimal API endpoint must execute the same rule. A configurable local presentation delay makes pending state visible; it is not the source of the availability decision. Service exceptions must surface as faults rather than passing validation.

The editor uses typed union construction, not union-valued form binding. Confirmation posts a scalar draft identifier. Unions are not used in route, query, header, or form binding.

## State and invariants

A separate, authenticated service queue demonstrates .NET 11 virtualization in the same workspace. Generate 10,000 explicitly simulated entries linked to the seeded returns, without adding them to SQLite. Use built-in variable-height measurement, `VirtualizeAnchorMode` edge policies, and `ScrollToItemAsync`. Expanding context above a selected case and prepending entries must preserve the visible case and its offset. End anchoring follows appended entries only while already at the bottom. Queue changes remain page-local and must not alter the keynote or authentication-refresh workflows.

SQLite stores orders, inventory, drafts, completed resolutions, and audit events. Seed one primary return and several supporting cases. Inventory includes one unavailable replacement and one available alternative.

Saving a draft never allocates inventory or issues a refund. Confirmation rechecks eligibility and atomically updates the return, allocates stock if applicable, and writes the audit record in one database transaction. Concurrent or repeated confirmations must not allocate twice. A failed commit leaves stock, order state, and audit history unchanged. Refund confirmation records a simulated refund; no real payment gateway or shipment is involved.

Demo reset requires an authenticated, same-origin form submission and resets only this application's seeded database content. State survives application restarts.

## Authentication and security boundaries

Use an explicitly labeled, Development-only local representative sign-in, a protected cookie, and a customer-service authorization policy on order and draft reads and writes. This is not a production identity system. Derive audit actors from the authenticated principal, never request JSON. The two fixed union examples, the validation-only endpoint, and the generated OpenAPI document are anonymous so Visual Studio's `Demo.http` needs no authentication setup. These examples must not mutate orders, inventory, or audit history.

The confirmation endpoint binds a form and relies on .NET 11's automatic CSRF protection. Do not opt it out or add a competing token requirement that obscures the demonstration. JSON workflow endpoints are independently authenticated, authorized, and validated; do not claim automatic form protection enforces JSON requests. The anonymous validation-only endpoint runs the shared rule without saving a draft or allocating inventory.

The cross-origin test uses a top-level form navigation. Its test cookie must be available to the browser on that request so a 400 can be attributed to form protection rather than a sign-in redirect. Any demonstration-only cookie settings must be documented. Do not expose the demo to a network or deploy it as production software.

Every workspace sign-in creates a session with refund approval and the normal long-lived cookie. A separate static SSR controls page changes the session's permission and issues updated cookie credentials. `MaximumAuthenticationExpiration` caps connection authentication at 20 seconds for presentation, independent of the cookie lifetime. Scheduled HTTP reauthentication applies the current principal to the existing Blazor Server circuit without discarding editor state. Refund draft creation and confirmation enforce the policy on the server, not just in `AuthorizeView`. Restore approval before continuing other demos; there is no separate Start/End mode. This is not automatic detection of external role changes, instant revocation, ASP.NET Core Identity, or an OAuth renewal implementation.

## Presenter flow

1. Before presenting, enter the browser's seeded demo workspace, reset its data, and open return 48021. Authentication is not a talk-track topic.
2. In Visual Studio, show the union and Minimal APIs, then use requests 1-3 in `eShopCustomerService.Web\Demo.http` to show real refund and replacement JSON and the generated union schema.
3. Select the unavailable docking station. Show pending validation and its inline error.
4. Show the shared attribute and Blazor validator. Use requests 4-5 in `Demo.http` to observe automatic HTTP 400 validation for the unavailable model and HTTP 200 for the available alternative.
5. Select the available alternative and save a draft.
6. From the review screen, open the cross-origin test using its link, which supplies the current draft ID. Show the browser's actual rejected POST; the attempt must not mutate application state.
7. Confirm from the legitimate review screen. Show the typed completed summary and audit history.
8. Reset for rehearsal using the authenticated reset control.

## Verification

Cover JSON round trips and schema generation, async rule enforcement at the API boundary, unauthorized callers, invalid draft payloads, transaction rollback, stock races, idempotent confirmation, persistence, and authenticated reset. Exercise the real browser flow for pending validation, successful resolution, responsive layout, and cross-origin rejection with no state change.

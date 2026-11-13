# eShopCustomerService.Web

| Setting | Value |
|---------|-------|
| **Interactivity Mode** | Server |
| **Interactivity Scope** | Per-page |

## Rendering configuration

This project uses per-page Interactive Server with prerendering. Pages are static SSR by default. Only components that explicitly add `@rendermode InteractiveServer` become interactive.

## Adding new components

Create routable pages in `Components/Pages` and shared components in `Components`. Only add an interactive render mode when the page requires it. Sign-in and confirmation use ordinary HTTP and remain static. Use inline `@code` blocks for component logic.

## Data access

The application API owns SQLite and the business rules. Blazor components use `WorkspaceService`/`CustomerServiceClient`; agent tools call that same API. Shared contracts and the async validation rule live in `eShopCustomerService.Contracts`. The inventory availability check never substitutes for transactional allocation at commit time.

`ResolutionWorkspace` owns the shared editor state and persists accepted editor values with `[PersistentState]`. Keep the persistent holder stable and update its `Value`; cascading persistence updates can otherwise reapply an older immutable record. Do not add a second draft-state model in the child editor or persist unaccepted predictive replies. Ordinary Review buttons use the saved-draft/form workflow; the agent's Show review tool displays current values without saving.

## Environment constraints

Interactive components run on the server via SignalR. Do not use `HttpContext` inside a circuit; obtain the user from cascading authentication state. Static pages and HTTP handlers can use `HttpContext`.

## Don'ts

- Do not put an interactive render mode on `Routes` or `HeadOutlet`.
- Do not make sign-in or confirmation interactive.
- Do not suppress experimental warnings.
- Do not bind unions from form, route, query, or header values.
- Do not claim the local demo sign-in is production authentication.
- Do not use external fonts, CDNs, or borrowed eShop artwork.

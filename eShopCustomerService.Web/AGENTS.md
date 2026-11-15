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

Persist conversation history separately through `ResolutionConversation` and `IConversationThread`. Commit exchanges only when the context becomes Idle, not when an intermediate tool round completes. Restore the context before constructing `MessageList`, suppress historical state mapping, and restore completed approval display status without sending a continuation. The pause initializer cancels unfinished turns; it must share the fingerprinted collocated module with the component and release its references when a circuit closes. Recheck current application permissions and committed resolution after recovery.

The simple conference UI used `ChatPage`. The persistent layout composes the SDK's `MessageList` and `MessageInput` around one restored `AgentContext`, because the installed `ChatPage` owns a context without a restoration hook. Keep recovered and live turns together. Reply review returns a typed `ReplyReviewDecision`; render its message, not its serialized payload. The server disables tools only for the continuation immediately after discard, not for later requests containing that history.

## Environment constraints

Interactive components run on the server via SignalR. Do not use `HttpContext` inside a circuit; obtain the user from cascading authentication state. Static pages and HTTP handlers can use `HttpContext`.

## Don'ts

- Do not put an interactive render mode on `Routes` or `HeadOutlet`.
- Do not make sign-in or confirmation interactive.
- Do not suppress experimental warnings.
- Do not bind unions from form, route, query, or header values.
- Do not claim the local demo sign-in is production authentication.
- Do not use external fonts, CDNs, or borrowed eShop artwork.

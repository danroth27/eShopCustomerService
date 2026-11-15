import { prepareConversationsForPause, disposeConversations } from "./Components/Shared/ResolutionAgentPanel.razor.js";

export function beforeServerStart(options) {
    options.circuitHandlers ??= [];
    options.circuitHandlers.push({
        onCircuitPausing: prepareConversationsForPause,
        onCircuitClosed: disposeConversations
    });
}

export function afterWebStarted(blazor) {
    const theme = document.documentElement.getAttribute("data-theme");

    blazor.addEventListener("enhancedload", () => {
        document.documentElement.setAttribute("data-theme", theme);
    });
}

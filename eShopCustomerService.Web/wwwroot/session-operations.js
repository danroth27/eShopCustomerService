let controller;

async function initialize() {
    controller?.abort();
    controller = new AbortController();
    const signal = controller.signal;
    const root = document.querySelector("[data-operations]");
    if (!root) return;
    const status = root.querySelector("[data-operation-status]");
    for (const button of root.querySelectorAll("[data-presenter-action]")) {
        button.addEventListener("click", async () => {
            const action = button.dataset.presenterAction;
            if (action === "shutdown" && !window.confirm("Pause connected workspaces and stop the customer-service process?")) return;
            button.disabled = true;
            status.textContent = action === "shutdown" ? "Stopping this service. Restart customerservice in Aspire." : "Requesting circuit pause...";
            try {
                const response = await fetch(`/presenter/${action}`, {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify({ confirmation: action === "shutdown" ? "STOP" : "PAUSE" }),
                    signal
                });
                if (!response.ok) throw new Error(`Operation failed (${response.status}): ${await response.text()}`);
                if (action === "drain") {
                    const result = await response.json();
                    status.textContent = result.completed
                        ? `${result.requested} pause requests completed.`
                        : `Pause requested; ${result.remaining} connections still remain.`;
                }
            } catch (error) {
                if (signal.aborted) return;
                status.textContent = error.message;
            } finally {
                button.disabled = false;
            }
        }, { signal });
    }
    while (!signal.aborted) {
        try {
            const response = await fetch("/presenter/metrics", { signal });
            if (!response.ok) throw new Error(`Metrics unavailable (${response.status}).`);
            const metrics = await response.json();
            for (const element of root.querySelectorAll("[data-metric]")) {
                element.textContent = metrics[element.dataset.metric];
            }
            if (status.textContent === "Connecting to session metrics..." || status.textContent.startsWith("Metrics unavailable")) {
                status.textContent = "Observing the customer-service process.";
            }
        } catch (error) {
            if (signal.aborted) return;
            status.textContent = `Metrics unavailable: ${error.message}`;
        }
        await new Promise(resolve => {
            const finish = () => {
                clearTimeout(timer);
                signal.removeEventListener("abort", finish);
                resolve();
            };
            const timer = setTimeout(finish, 1000);
            signal.addEventListener("abort", finish, { once: true });
        });
    }
}

initialize();
Blazor.addEventListener("enhancedload", initialize);

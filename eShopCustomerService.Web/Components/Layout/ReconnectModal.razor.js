const modal = document.getElementById("components-reconnect-modal");
modal.addEventListener("components-reconnect-state-changed", async event => {
    if (event.detail.state === "show") modal.showModal();
    else if (event.detail.state === "hide") modal.close();
    else if (event.detail.state === "failed") document.addEventListener("visibilitychange", retryWhenVisible);
    else if (event.detail.state === "rejected") await resume();
});
document.getElementById("components-reconnect-button").addEventListener("click", retry);
document.getElementById("components-resume-button").addEventListener("click", resume);

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenVisible);
    try {
        if (await Blazor.reconnect()) modal.close();
        else await resume();
    } catch (error) {
        console.warn("The customer-service process is not yet available.", error);
        document.addEventListener("visibilitychange", retryWhenVisible);
    }
}

async function resume() {
    try {
        if (await Blazor.resumeCircuit()) {
            modal.close();
            return;
        }
        console.error("The server rejected the workspace recovery state.");
    } catch (error) {
        console.warn("The customer-service workspace could not resume.", error);
    }
    modal.classList.remove("components-reconnect-paused", "components-reconnect-failed", "components-reconnect-retrying");
    modal.classList.add("components-reconnect-resume-failed");
    if (!modal.open) modal.showModal();
}

async function retryWhenVisible() {
    if (document.visibilityState === "visible") await retry();
}

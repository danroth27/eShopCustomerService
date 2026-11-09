import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { randomUUID } from "node:crypto";
import { mkdir, rm, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

// Playwright emulates focus on its own CDP session, making hidden tabs report visible.
// This check drives a fresh native Edge instance directly; it never synthesizes visibility.
const root = dirname(fileURLToPath(import.meta.url));
const base = process.env.DEMO_URL || "https://localhost:7153";
const profile = resolve(root, "artifacts", `auto-pause-profile-${randomUUID()}`);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
await mkdir(profile, { recursive: true });
const edge = spawn("C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
    ["--remote-debugging-port=0", `--user-data-dir=${profile}`, "--no-first-run",
        "--no-default-browser-check", "--disable-extensions", "--ignore-certificate-errors", "--new-window", "about:blank"]);
let launchError;
edge.on("error", error => { launchError = error; });
const { readFile } = await import("node:fs/promises");
let socket;
let workspaceSession;
let authSession;
let id = 0;
const pending = new Map();

async function command(method, params = {}, sessionId) {
    return new Promise((resolve, reject) => {
        const requestId = ++id;
        const timer = setTimeout(() => {
            pending.delete(requestId);
            reject(new Error(`CDP command timed out: ${method}`));
        }, 15000);
        pending.set(requestId, { method, resolve, reject, timer });
        socket.send(JSON.stringify({ id: requestId, method, params, ...(sessionId ? { sessionId } : {}) }));
    });
}

async function evaluate(sessionId, expression) {
    const result = await command("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }, sessionId);
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
}

async function until(action, description, timeout = 20000) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
        if (await action()) return;
        await delay(100);
    }
    throw new Error(`Timed out: ${description}`);
}

async function metrics() {
    return evaluate(authSession, `(async () => {
        const response = await fetch("/presenter/metrics");
        if (!response.ok) throw new Error("Metrics failed: " + response.status);
        return response.json();
    })()`);
}

async function click(session, selector) {
    const point = await evaluate(session, `(() => {
        const element = document.querySelector(${JSON.stringify(selector)});
        element.scrollIntoView({ block: "center" });
        const rect = element.getBoundingClientRect();
        return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
    })()`);
    await command("Input.dispatchMouseEvent", { type: "mousePressed", button: "left", clickCount: 1, ...point }, session);
    await command("Input.dispatchMouseEvent", { type: "mouseReleased", button: "left", clickCount: 1, ...point }, session);
}

try {
    let endpoint;
    await until(async () => {
        if (launchError) throw launchError;
        try {
            const lines = (await readFile(resolve(profile, "DevToolsActivePort"), "utf8")).trim().split(/\r?\n/);
            endpoint = `ws://127.0.0.1:${lines[0]}${lines[1]}`;
            return true;
        } catch (error) {
            if (error.code !== "ENOENT" && error.code !== "EBUSY") throw error;
            return false;
        }
    }, "native Edge debugging endpoint");
    socket = new WebSocket(endpoint);
    await new Promise((resolve, reject) => {
        socket.addEventListener("open", resolve, { once: true });
        socket.addEventListener("error", reject, { once: true });
    });
    socket.addEventListener("message", ({ data }) => {
        const message = JSON.parse(data);
        const request = pending.get(message.id);
        if (!request) return;
        pending.delete(message.id);
        clearTimeout(request.timer);
        message.error ? request.reject(new Error(JSON.stringify(message.error))) : request.resolve(message.result);
    });
    socket.addEventListener("close", () => {
        for (const request of pending.values()) {
            clearTimeout(request.timer);
            request.method === "Browser.close" ? request.resolve({}) : request.reject(new Error(`Browser closed during ${request.method}`));
        }
        pending.clear();
    });
    const signIn = await command("Target.createTarget", { url: `${base}/sign-in` });
    authSession = (await command("Target.attachToTarget", { targetId: signIn.targetId, flatten: true })).sessionId;
    await until(() => evaluate(authSession, 'document.querySelector(\'form[action="/demo/sign-in"]\') !== null'), "demo sign-in");
    await click(authSession, 'form[action="/demo/sign-in"] button');
    await until(() => evaluate(authSession, 'location.pathname === "/" && document.querySelector(".page-heading") !== null'), "authenticated static overview");
    assert.equal((await metrics()).live, 0, "Run with no other live demo circuits");
    const target = await command("Target.createTarget", { url: `${base}/returns/48021?continuity=true` });
    const { sessionId } = await command("Target.attachToTarget", { targetId: target.targetId, flatten: true });
    workspaceSession = sessionId;
    await command("Target.activateTarget", { targetId: target.targetId });
    const window = await command("Browser.getWindowForTarget", { targetId: target.targetId });
    await command("Browser.setWindowBounds", { windowId: window.windowId, bounds: { width: 1600, height: 1100 } });
    await until(() => evaluate(sessionId, 'document.querySelector(\'[data-interactive="true"]\') !== null'), "interactive workspace");
    await evaluate(sessionId, '(() => { const select = document.querySelector("#replacement-product"); select.value = "DOCK-PRO-PLUS"; select.dispatchEvent(new Event("change", { bubbles: true })); })()');
    await until(() => evaluate(sessionId, 'document.querySelector(".field-status.available") !== null'), "replacement availability");
    await click(sessionId, "#delivery-address");
    await evaluate(sessionId, 'document.querySelector("#delivery-address").select()');
    await command("Input.insertText", { text: "Automatic pause, 200 Demo Way" }, sessionId);
    await click(sessionId, ".editor-panel button.secondary");
    await until(() => evaluate(sessionId, 'document.querySelector(".resolution-summary p") !== null'), "unsaved preview");
    const instance = await evaluate(sessionId, 'document.querySelector(\'[data-testid="component-instance"]\').textContent');
    assert.equal((await metrics()).live, 1);
    const observer = await command("Target.createTarget", { url: `${base}/operations` });
    await command("Target.activateTarget", { targetId: observer.targetId });
    await until(() => evaluate(sessionId, 'document.visibilityState === "hidden"'), "real hidden tab", 10000);
    await until(async () => (await metrics()).live === 0, "automatic circuit eviction");
    await command("Target.activateTarget", { targetId: target.targetId });
    await until(() => evaluate(sessionId, 'document.visibilityState === "visible"'), "real visible tab");
    await until(async () => await evaluate(sessionId, 'document.querySelector(\'[data-testid="component-instance"]\')?.textContent') !== instance, "new restored component");
    assert.equal(await evaluate(sessionId, 'document.querySelector(".resolution-summary p").textContent.trim()'), "Automatic pause, 200 Demo Way");
    assert.equal((await metrics()).live, 1);
    await until(() => evaluate(sessionId, 'document.querySelector("dialog[open]") === null'), "reconnect UI dismissal");
    await until(() => evaluate(sessionId,
        'document.querySelector("#components-reconnect-modal").getAnimations({ subtree: true }).every(animation => animation.playState !== "running")'),
        "reconnect UI transition completion");
    const screenshot = await command("Page.captureScreenshot", { format: "png" }, sessionId);
    await writeFile(resolve(root, "artifacts", "auto-pause-restored.png"), Buffer.from(screenshot.data, "base64"));
    await writeFile(resolve(root, "artifacts", "auto-pause-verification.json"),
        JSON.stringify({ passed: true, nativeVisibility: true, liveCircuits: [1, 0, 1], timestamp: new Date().toISOString() }, null, 2));
    await evaluate(sessionId, "Blazor.pauseCircuit()");
    await until(async () => (await metrics()).live === 0, "final circuit cleanup");
    console.log("Verified native hidden-tab auto-pause, circuit eviction, and visible-tab restoration.");
} finally {
    if (socket?.readyState === WebSocket.OPEN) {
        if (workspaceSession) {
            await evaluate(workspaceSession, "Blazor.pauseCircuit()");
            await until(async () => (await metrics()).live === 0, "owned circuit cleanup");
        }
        await command("Browser.close");
        await until(async () => socket.readyState === WebSocket.CLOSED, "owned Edge shutdown", 10000);
    } else if (edge.exitCode === null) {
        edge.kill();
    }
    await delay(500);
    await rm(profile, { recursive: true });
}

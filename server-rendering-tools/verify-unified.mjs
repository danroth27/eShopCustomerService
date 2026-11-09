import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { chromium } from "playwright";

const root = dirname(fileURLToPath(import.meta.url));
const base = process.env.DEMO_URL || "https://localhost:7153";
const output = process.env.DEMO_ARTIFACTS || resolve(root, "artifacts", "unified");
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: "msedge", headless: true });
const context = await browser.newContext({
    viewport: { width: 1600, height: 1100 }, colorScheme: "light", ignoreHTTPSErrors: true
});
const page = await context.newPage();
const errors = [];
page.on("pageerror", error => errors.push(error.message));
let sockets = 0;
page.on("websocket", () => sockets++);
const metrics = async () => {
    const response = await context.request.get(`${base}/presenter/metrics`);
    assert.equal(response.status(), 200);
    return response.json();
};
const postForm = (path, form) => context.request.post(`${base}${path}`, {
    form, headers: { "Sec-Fetch-Site": "same-origin" }
});
async function until(action, description, timeout = 20000) {
    const deadline = Date.now() + timeout;
    while (Date.now() < deadline) {
        if (await action()) return;
        await page.waitForTimeout(100);
    }
    throw new Error(`Timed out: ${description}`);
}
async function preparePreview() {
    await page.locator('[data-interactive="true"]').waitFor();
    await page.getByLabel("Replacement model").selectOption("DOCK-PRO-PLUS");
    await page.getByLabel("Delivery address").fill("200 Demo Way");
    await page.getByRole("button", { name: "Preview without saving", exact: true }).click();
    await page.getByRole("heading", { name: "Your unsaved resolution" }).waitFor();
    assert.equal((await page.locator(".resolution-summary p").textContent()).trim(), "200 Demo Way");
}
async function pause() {
    await page.evaluate(async () => { await Blazor.pauseCircuit(); });
    await until(async () => (await metrics()).live === 0, "owned circuit eviction");
}
const results = {};
try {
    assert.ok((await postForm("/demo/sign-in", { returnUrl: "/" })).ok());
    assert.equal((await metrics()).live, 0, "Use an isolated AppHost without other demo circuits");
    assert.ok((await postForm("/demo/reset", { confirmation: "RESET" })).ok());
    assert.ok((await postForm("/demo/sign-out", { confirmation: "SIGNOUT" })).ok());
    await page.goto(`${base}/care`);
    await page.screenshot({ path: resolve(output, "care.png"), fullPage: true });
    await page.evaluate(() => { window.demoDocumentMarker = "same-document"; });
    await page.locator('a[href="/care/help"]').first().click();
    await page.waitForURL("**/care/help");
    assert.equal(await page.evaluate(() => window.demoDocumentMarker), "same-document");
    const cacheTime = await page.getByTestId("cache-time").textContent();
    const requestTime = await page.getByTestId("request-time").textContent();
    await page.waitForTimeout(1100);
    await page.reload();
    assert.equal(await page.getByTestId("cache-time").textContent(), cacheTime);
    assert.notEqual(await page.getByTestId("request-time").textContent(), requestTime);
    await page.getByRole("link", { name: "Category", exact: true }).click();
    await page.waitForURL("**/*sort=Category*");
    await page.locator('.paginator a[href*="page="]').last().click();
    assert.ok(new URL(page.url()).searchParams.has("page"));
    await page.screenshot({ path: resolve(output, "help.png"), fullPage: true });
    await page.goto(`${base}/care/policy`);
    const policyTime = await page.getByTestId("policy-time").textContent();
    await page.waitForTimeout(1100);
    await page.reload();
    assert.equal(await page.getByTestId("policy-time").textContent(), policyTime);
    await page.goto(`${base}/care/request`);
    let posts = 0;
    page.on("request", request => {
        if (request.method() === "POST" && new URL(request.url()).pathname === "/care/request") posts++;
    });
    await page.getByLabel("Your name").fill("Alex Morgan");
    await page.getByLabel("Email address").fill("not-an-email");
    await page.getByLabel("How can we help?").fill("My dock stopped connecting. I have a presentation tomorrow.");
    await page.getByRole("button", { name: "Send return request" }).click();
    await page.waitForTimeout(500);
    assert.equal(posts, 0, "Client validation must prevent an invalid HTTP form POST");
    assert.match((await page.locator(".validation-message").allTextContents()).join(" "), /email/i);
    await page.screenshot({ path: resolve(output, "validation.png"), fullPage: true });
    await page.getByLabel("Email address").fill("alex@example.test");
    await page.getByRole("button", { name: "Send return request" }).click();
    await page.waitForURL("**/care/received");
    assert.equal(await page.getByTestId("flash-message").count(), 1);
    const reference = await page.getByTestId("request-reference").textContent();
    await page.screenshot({ path: resolve(output, "received.png"), fullPage: true });
    await page.reload();
    assert.equal(await page.getByTestId("flash-message").count(), 0);
    assert.equal(await page.getByTestId("request-reference").textContent(), reference);
    assert.equal(sockets, 0, "The entire static customer-care journey must have no circuit");
    results.staticCare = true;

    assert.ok((await postForm("/demo/sign-in", { returnUrl: "/" })).ok());
    const actualCase = await (await context.request.get(`${base}/api/returns/48021`)).json();
    assert.equal(actualCase.email, "alex@example.test");
    assert.equal(actualCase.reason, "My dock stopped connecting. I have a presentation tomorrow.");
    const beforeBaseline = await metrics();
    await page.goto(`${base}/rendering-baseline/48021`);
    await until(async () => Number(await page.getByTestId("baseline-load").textContent()) === beforeBaseline.contextLoads + 2,
        "prerender and interactive baseline loads");
    await pause();
    const beforePersistent = await metrics();
    await page.goto(`${base}/returns/48021?continuity=true`);
    await page.locator('[data-interactive="true"]').waitFor();
    assert.equal((await metrics()).contextLoads - beforePersistent.contextLoads, 1);
    assert.ok((await page.locator(".customer-note").textContent()).includes(actualCase.reason));
    const auditBefore = await (await context.request.get(`${base}/api/returns/48021/audit`)).json();
    await page.getByLabel("Replacement model").selectOption("DOCK-PRO");
    await page.getByLabel("Delivery address").fill("200 Demo Way");
    await until(async () => (await page.locator(".validation-message").allTextContents()).join(" ").includes("unavailable"),
        "shared asynchronous inventory validation");
    await preparePreview();
    const instance = await page.getByTestId("component-instance").textContent();
    await page.screenshot({ path: resolve(output, "preview.png"), fullPage: true });
    await pause();
    assert.equal(await page.evaluate(async () => Blazor.resumeCircuit()), true);
    await until(async () => await page.getByTestId("component-instance").textContent() !== instance, "new restored editor");
    assert.equal((await page.locator(".resolution-summary p").textContent()).trim(), "200 Demo Way");
    const drainInstance = await page.getByTestId("component-instance").textContent();
    const drainResponse = await context.request.post(`${base}/presenter/drain`, { data: { confirmation: "PAUSE" } });
    assert.equal(drainResponse.status(), 200);
    const drained = await drainResponse.json();
    assert.equal(drained.requested, 1);
    assert.equal(drained.completed, true);
    await page.getByRole("button", { name: "Resume", exact: true }).click();
    await until(async () => await page.getByTestId("component-instance").textContent() !== drainInstance, "server-paused editor restoration");
    assert.equal((await page.locator(".resolution-summary p").textContent()).trim(), "200 Demo Way");
    assert.deepEqual(await (await context.request.get(`${base}/api/returns/48021/audit`)).json(), auditBefore,
        "Preview and circuit recovery must not save a business draft");
    results.contextHandoffAndPause = true;

    await pause();
    await page.goto(`${base}/returns/48022`);
    await page.locator('[data-interactive="true"]').waitFor();
    await page.getByLabel("Replacement model").selectOption("DOCK-PRO-PLUS");
    await page.getByLabel("Delivery address").fill("200 Demo Way");
    const authInstance = await page.getByTestId("component-instance").textContent();
    const authSockets = sockets;
    const refreshed = [];
    page.on("response", response => {
        if (new URL(response.url()).pathname === "/_blazor/refresh") refreshed.push(response.status());
    });
    const controls = await context.newPage();
    await controls.goto(`${base}/auth-refresh`);
    await controls.getByRole("button", { name: "Remove refund approval" }).click();
    await page.getByText("Refund approval: removed", { exact: true }).waitFor({ timeout: 30000 });
    assert.equal(await page.getByTestId("component-instance").textContent(), authInstance);
    assert.equal(await page.getByLabel("Delivery address").inputValue(), "200 Demo Way");
    await controls.getByRole("button", { name: "Restore refund approval" }).click();
    await page.getByText("Refund approval: allowed", { exact: true }).waitFor({ timeout: 30000 });
    assert.equal(sockets, authSockets, "Authentication refresh must retain the original WebSocket");
    assert.ok(refreshed.includes(200));
    await controls.close();
    await page.getByRole("button", { name: "Review replacement", exact: true }).click();
    await page.waitForURL("**/review/*");
    const [crossOrigin] = await Promise.all([
        context.waitForEvent("page"),
        page.getByRole("link", { name: /Open cross-origin test/ }).click()
    ]);
    await crossOrigin.waitForLoadState();
    const [rejected] = await Promise.all([
        crossOrigin.waitForResponse(response =>
            new URL(response.url()).pathname === "/returns/confirm" && response.request().method() === "POST"),
        crossOrigin.getByRole("button", { name: "Attempt cross-origin confirmation" }).click()
    ]);
    assert.equal(rejected.status(), 400);
    const crossHeaders = await rejected.request().allHeaders();
    assert.match(crossHeaders.cookie, /eShop\.CustomerService\.Demo=/);
    assert.equal(crossHeaders["sec-fetch-site"], "same-site");
    await crossOrigin.close();
    const [confirmed] = await Promise.all([
        page.waitForResponse(response =>
            new URL(response.url()).pathname === "/returns/confirm" && response.request().method() === "POST"),
        page.getByRole("button", { name: /Confirm/ }).click()
    ]);
    assert.equal(confirmed.status(), 302);
    await page.waitForURL("**/returns/48022?completed=true");
    results.authenticationRefreshAndCsrf = true;

    await page.goto(`${base}/returns/48021?continuity=true`);
    await preparePreview();
    if (process.env.VERIFY_RESTART === "1") {
        const apphost = process.env.DEMO_APPHOST;
        assert.ok(apphost, "Explicitly provide the owned isolated AppHost before permitting process replacement");
        const before = await metrics();
        const original = await page.getByTestId("component-instance").textContent();
        try {
            const response = await context.request.post(`${base}/presenter/shutdown`, { data: { confirmation: "STOP" } });
            assert.equal(response.status(), 202);
        } catch (error) {
            if (!/ECONNRESET|socket hang up|connection closed/i.test(error.message)) throw error;
        }
        await page.getByRole("button", { name: "Resume", exact: true }).waitFor();
        const run = promisify(execFile);
        await run("aspire", ["wait", "customerservice", "--status", "down", "--timeout", "30",
            "--apphost", apphost, "--non-interactive"], { timeout: 40000 });
        await run("aspire", ["resource", "customerservice", "restart", "--apphost", apphost, "--non-interactive"], { timeout: 60000 });
        await run("aspire", ["wait", "customerservice", "--timeout", "60", "--apphost", apphost, "--non-interactive"], { timeout: 70000 });
        const replacement = await metrics();
        assert.notEqual(replacement.processId, before.processId);
        await page.getByRole("button", { name: "Resume", exact: true }).click();
        await until(async () => await page.getByTestId("component-instance").textContent() !== original, "process-replacement restoration");
        await page.getByRole("heading", { name: "Your unsaved resolution" }).waitFor();
        assert.equal((await page.locator(".resolution-summary p").textContent()).trim(), "200 Demo Way");
        assert.equal((await metrics()).contextLoads, 0, "Restored context must not reload in the replacement process");
        results.processReplacement = { previousPid: before.processId, replacementPid: replacement.processId };
    }
    await pause();
    await page.goto(`${base}/operations`);
    await page.screenshot({ path: resolve(output, "operations.png"), fullPage: true });
    assert.deepEqual(errors, []);
    await writeFile(resolve(output, "verification.json"), JSON.stringify({ base, results, passed: true }, null, 2));
    console.log("Verified the unified customer journey, context handoff, pause/resume, auth refresh, and CSRF workflows.", results);
} catch (error) {
    await page.screenshot({ path: resolve(output, "failure.png"), fullPage: true });
    console.error("Browser errors:", errors);
    throw error;
} finally {
    await browser.close();
}

import assert from "node:assert/strict";
import { mkdir, writeFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium, request } from "playwright";

const root = dirname(fileURLToPath(import.meta.url));
const base = process.env.DEMO_URL || "https://localhost:7153";
const output = process.env.DEMO_ARTIFACTS || resolve(root, "artifacts", "keynote");
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ channel: "msedge", headless: true });
const context = await browser.newContext({
    viewport: { width: 1920, height: 1080 }, colorScheme: "light", ignoreHTTPSErrors: true
});
const anonymous = await request.newContext({ ignoreHTTPSErrors: true });
const page = await context.newPage();
const errors = [];
const stages = [];
page.on("pageerror", error => errors.push(error.message));
let sockets = 0;
page.on("websocket", () => sockets++);
const form = (path, values) => context.request.post(`${base}${path}`, {
    form: values, headers: { "Sec-Fetch-Site": "same-origin" }
});
async function get(path) {
    const response = await context.request.get(`${base}${path}`);
    assert.equal(response.status(), 200);
    return response.json();
}
const stock = async () => (await get("/api/inventory")).find(item => item.productCode === "DOCK-PRO-PLUS").available;
const audit = () => get("/api/returns/48021/audit");
const replacement = code => ({ orderId: 48021, productCode: code, deliveryAddress: "120 Market Street, Seattle, WA 98101" });
const examplePaths = ["/api/examples/refund", "/api/examples/replacement", "/openapi/v1.json"];
try {
    assert.ok((await form("/demo/sign-in", { returnUrl: "/" })).ok());
    assert.equal((await get("/presenter/metrics")).live, 0, "Run on an isolated AppHost without other demo circuits");

    // The keynote warms all five HTTP-file requests before resetting and staging the browser.
    const warm = [];
    for (const path of examplePaths) warm.push((await anonymous.get(`${base}${path}`)).status());
    for (const code of ["DOCK-PRO", "DOCK-PRO-PLUS"]) {
        warm.push((await anonymous.post(`${base}/api/replacements/validate`, { data: replacement(code) })).status());
    }
    assert.deepEqual(warm, [200, 200, 200, 400, 200]);
    assert.ok((await form("/demo/reset", { confirmation: "RESET" })).ok());
    const initialStock = await stock();
    const initialAudit = await audit();
    await page.goto(`${base}/returns/48021`);
    await page.locator('[data-interactive="true"]').waitFor();
    await page.getByRole("button", { name: /^Refund/ }).click();
    await page.getByRole("button", { name: "Review refund", exact: true }).waitFor();
    await page.getByRole("button", { name: /^Replacement/ }).click();
    await page.getByLabel("Replacement model").waitFor();
    const editor = await page.getByTestId("component-instance").textContent();
    const originalSockets = sockets;
    await page.screenshot({ path: resolve(output, "01-business-choices.png"), fullPage: true });
    stages.push("Setup and refund/replacement choices");

    const refund = await anonymous.get(`${base}${examplePaths[0]}`);
    assert.equal(refund.status(), 200);
    assert.deepEqual(await refund.json(), { amount: 149, paymentReference: "PAY-48021" });
    const exchange = await anonymous.get(`${base}${examplePaths[1]}`);
    assert.equal(exchange.status(), 200);
    assert.deepEqual(await exchange.json(), {
        productCode: "DOCK-PRO-PLUS", deliveryAddress: "120 Market Street, Seattle, WA 98101"
    });
    const contractResponse = await anonymous.get(`${base}${examplePaths[2]}`);
    assert.equal(contractResponse.status(), 200);
    const contract = await contractResponse.json();
    assert.equal(contract.openapi, "3.2.0");
    const alternatives = contract.components.schemas.Resolution.anyOf.map(item => item.$ref);
    assert.equal(alternatives.length, 2);
    assert.ok(alternatives.some(value => value.endsWith("/RefundResolution")));
    assert.ok(alternatives.some(value => value.endsWith("/ReplacementResolution")));
    stages.push("HTTP requests1-3: plain union cases and OpenAPI3.2 anyOf");

    await page.getByLabel("Replacement model").selectOption("DOCK-PRO");
    await page.getByText("Checking inventory availability...", { exact: true }).waitFor();
    await page.getByText("This model is currently unavailable. Choose an available alternative.", { exact: true }).waitFor();
    await page.screenshot({ path: resolve(output, "02-unavailable-replacement.png"), fullPage: true });
    const invalid = await anonymous.post(`${base}/api/replacements/validate`, { data: replacement("DOCK-PRO") });
    assert.equal(invalid.status(), 400);
    assert.ok((await invalid.json()).errors.ProductCode.some(value => value.includes("unavailable")));
    const valid = await anonymous.post(`${base}/api/replacements/validate`, { data: replacement("DOCK-PRO-PLUS") });
    assert.equal(valid.status(), 200);
    assert.equal((await valid.json()).valid, true);
    assert.equal(await stock(), initialStock, "Validation must not reserve inventory");
    assert.deepEqual(await audit(), initialAudit, "Validation must not save a draft");
    assert.equal(await page.getByTestId("component-instance").textContent(), editor);
    assert.equal(sockets, originalSockets);
    stages.push("Pending/unavailable Blazor validation, then HTTP requests4-5 without business mutation");

    await page.getByLabel("Replacement model").selectOption("DOCK-PRO-PLUS");
    await page.getByText("Available for replacement", { exact: true }).waitFor();
    await page.getByRole("button", { name: "Review replacement", exact: true }).click();
    await page.waitForURL("**/review/*");
    const draftId = await page.locator("#draft-id").inputValue();
    const draft = await get(`/api/drafts/${draftId}`);
    assert.equal(draft.orderId, 48021);
    assert.equal(draft.confirmedAt, null);
    assert.equal(draft.resolution.productCode, "DOCK-PRO-PLUS");
    assert.ok((await page.locator(".resolution-summary").textContent()).includes("USB-C Dock Pro+"));
    assert.equal(await page.locator('form[action="/returns/confirm"] input[name="__RequestVerificationToken"]').count(), 0);
    assert.equal(await stock(), initialStock, "Saving a draft must not reserve inventory");
    await page.screenshot({ path: resolve(output, "03-review-draft.png"), fullPage: true });
    stages.push("Available replacement and typed review, with stock still unallocated");

    const [crossOrigin] = await Promise.all([
        context.waitForEvent("page"),
        page.getByRole("link", { name: /Open cross-origin test/ }).click()
    ]);
    await crossOrigin.waitForLoadState();
    const origin = new URL(crossOrigin.url()).origin;
    assert.notEqual(origin, new URL(base).origin);
    const [rejection] = await Promise.all([
        crossOrigin.waitForResponse(response =>
            new URL(response.url()).pathname === "/returns/confirm" && response.request().method() === "POST"),
        crossOrigin.getByRole("button", { name: "Attempt cross-origin confirmation" }).click()
    ]);
    assert.equal(rejection.status(), 400);
    const untrustedHeaders = await rejection.request().allHeaders();
    assert.equal(untrustedHeaders["sec-fetch-site"], "same-site");
    assert.equal(untrustedHeaders.origin, origin);
    assert.match(untrustedHeaders.cookie, /eShop\.CustomerService\.Demo=/);
    assert.equal((await get(`/api/drafts/${draftId}`)).confirmedAt, null);
    assert.equal(await stock(), initialStock);
    assert.equal((await get("/api/returns/48021")).status, "Open");
    await crossOrigin.close();
    await page.reload();
    await page.getByRole("button", { name: "Confirm replacement", exact: true }).waitFor();
    stages.push("Real cross-origin400 with same-site cookie; draft, return, and inventory unchanged");

    const [confirmation] = await Promise.all([
        page.waitForResponse(response =>
            new URL(response.url()).pathname === "/returns/confirm" && response.request().method() === "POST"),
        page.getByRole("button", { name: "Confirm replacement", exact: true }).click()
    ]);
    assert.equal(confirmation.status(), 302);
    const trustedHeaders = await confirmation.request().allHeaders();
    assert.equal(trustedHeaders["sec-fetch-site"], "same-origin");
    await page.waitForURL("**/returns/48021?completed=true");
    await page.getByRole("heading", { name: "Confirmed resolution", exact: true }).waitFor();
    const resolved = await get("/api/returns/48021");
    assert.equal(resolved.status, "Resolved");
    assert.equal(resolved.resolution.productCode, "DOCK-PRO-PLUS");
    assert.equal(await stock(), initialStock - 1);
    assert.notEqual((await get(`/api/drafts/${draftId}`)).confirmedAt, null);
    assert.ok((await audit()).some(entry => entry.action.toLowerCase().includes("confirm")));
    await page.screenshot({ path: resolve(output, "04-confirmed-resolution.png"), fullPage: true });
    stages.push("Same-origin confirmation, completed typed resolution, audit entry, one stock allocation");
    await page.locator(".completed-panel").waitFor();
    await page.evaluate(async () => { await Blazor.pauseCircuit(); });
    assert.deepEqual(errors, []);
    await writeFile(resolve(output, "verification.json"), JSON.stringify({
        passed: true, sequence: "Keynote/script.md", warmStatusCodes: warm,
        orderId: 48021, initialStock, finalStock: initialStock - 1, stages
    }, null, 2));
    console.log("Verified exact five-minute keynote sequence.", stages);
} catch (error) {
    await page.screenshot({ path: resolve(output, "failure.png"), fullPage: true });
    await writeFile(resolve(output, "failure.txt"), `${page.url()}\n${await page.locator("body").innerText()}`);
    console.error("Completed stages:", stages, "Browser errors:", errors);
    throw error;
} finally {
    await anonymous.dispose();
    await browser.close();
}

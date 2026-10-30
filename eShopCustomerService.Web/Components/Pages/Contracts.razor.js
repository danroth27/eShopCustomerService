export async function send(path, method, body) {
    const response = await fetch(path, {
        method,
        credentials: "same-origin",
        headers: body === null ? {} : { "Content-Type": "application/json" },
        body
    });
    const text = await response.text();
    if (!response.headers.get("content-type")?.includes("json")) {
        return { status: response.status, body: text };
    }
    let data = JSON.parse(text);
    if (path === "/openapi/v1.json" && response.ok) {
        const schemas = Object.fromEntries(
            Object.entries(data.components?.schemas ?? {})
                .filter(([name]) => name.includes("Resolution"))
        );
        data = { openapi: data.openapi, components: { schemas } };
    }
    return { status: response.status, body: JSON.stringify(data, null, 2) };
}

using System.Net;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("The cross-origin test host is a local Development-only tool.");
}
builder.AddServiceDefaults();
var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", (HttpRequest request, IConfiguration configuration) =>
{
    var target = configuration["CustomerServiceUrl"]
        ?? throw new InvalidOperationException("Start with Aspire so CustomerServiceUrl is configured.");
    var encodedTarget = WebUtility.HtmlEncode(target.TrimEnd('/'));
    var draft = Guid.TryParse(request.Query["draftId"], out var id) ? id.ToString() : "";
    return Results.Content($$"""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <script>
        (() => {
            const param = new URLSearchParams(window.location.search).get("scoutTheme");
            const theme = param || (window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
            document.documentElement.setAttribute("data-theme", theme);
        })();
        </script>
        <link rel="stylesheet" href="{{encodedTarget}}/app.css"><title>Cross-origin form test | eShop</title></head>
        <body><main class="probe-container"><section class="panel probe-panel">
        <span class="badge warning">INDEPENDENT TEST ORIGIN</span>
        <h1>A valid draft.<br>A different origin.</h1>
        <p>This page submits a real browser form to the customer-service application's confirmation endpoint.</p>
        <dl class="facts"><div><dt>Source origin</dt><dd>{{WebUtility.HtmlEncode($"{request.Scheme}://{request.Host}")}}</dd></div>
        <div><dt>Target origin</dt><dd>{{encodedTarget}}</dd></div></dl>
        <form method="post" action="{{encodedTarget}}/returns/confirm">
            <div class="field"><label for="draftId">Resolution draft ID</label>
            <input id="draftId" name="draftId" value="{{draft}}" required pattern="[0-9a-fA-F-]{36}"></div>
            <button class="button full-width" type="submit">Attempt cross-origin confirmation</button>
        </form>
        <div class="notice subtle"><span>The expected result is an actual HTTP 400 from the protected form endpoint.
        Keep the browser Network panel open to inspect the request and its Sec-Fetch-Site header.</span></div>
        <p class="fine-print">Sign in to customer service first. Both Aspire resources use HTTPS on localhost with different ports:
        different origins, but the same site, so the normal SameSite=Lax authentication cookie is included.
        The rejection must come from form protection, not from missing authentication.</p>
        <a class="back-link" href="{{encodedTarget}}/">Return to customer service</a>
        </section></main></body></html>
        """, "text/html");
});
app.Run();

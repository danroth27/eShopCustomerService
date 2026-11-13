namespace eShopCustomerService.Web.Services;

public static class ApiForwarding
{
    public static void MapApiForwarding(this WebApplication app)
    {
        app.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "DELETE"], ForwardAsync).ExcludeFromDescription();
        app.MapGet("/openapi/{**path}", ForwardAsync).ExcludeFromDescription();
    }

    private static async Task ForwardAsync(HttpContext context, CustomerServiceClient api, IHttpClientFactory clients)
    {
        using var client = clients.CreateClient("application-api");
        using var request = api.CreateRequest(new(context.Request.Method),
            $"{context.Request.Path}{context.Request.QueryString}", context.User);
        if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
            {
                request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            }
        }
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
        if (response.Headers.Location is { } location)
        {
            context.Response.Headers.Location = location.ToString();
        }
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}

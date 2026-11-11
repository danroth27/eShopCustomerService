using eShopCustomerService.AgentServer.Agentic;

namespace eShopCustomerService.AgentServer;

public static class AgentServerProgram
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException("This sample uses a local demo session and must only run in Development.");
        }
        builder.AddServiceDefaults();
        builder.Services.AddProblemDetails();
        builder.Services.AddAgentService(builder.Configuration);

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCustomerServiceAgent();
        app.MapDefaultEndpoints();
        app.Run();
    }
}

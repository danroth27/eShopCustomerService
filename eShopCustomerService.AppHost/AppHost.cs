var builder = DistributedApplication.CreateBuilder(args);

var api = builder.AddProject<Projects.eShopCustomerService_Api>("api")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Demo:CookieName", "eShop.CustomerService.Agentic");

var customerService = builder.AddProject<Projects.eShopCustomerService_Web>("customerservice")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithEnvironment("CustomerService:ApiUrl", api.GetEndpoint("https"))
    .WaitFor(api);

var crossOrigin = builder.AddProject<Projects.eShopCustomerService_CrossOrigin>("crossorigin")
    .WithExternalHttpEndpoints()
    .WithEnvironment("CustomerServiceUrl", customerService.GetEndpoint("https"))
    .WaitFor(customerService);

customerService.WithEnvironment("CrossOriginUrl", crossOrigin.GetEndpoint("https"));

var foundryEndpoint = builder.AddParameter("agentic-foundry-endpoint", secret: true);
var foundryModel = builder.AddParameter("agentic-foundry-model",
    value: builder.Configuration["Parameters:agentic-foundry-model"] ?? "gpt-5-mini");
var foundry = builder.AddExternalService("foundry", foundryEndpoint);
var agentServer = builder.AddProject<Projects.eShopCustomerService_AgentServer>("agentserver")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("Foundry:Endpoint", foundry)
    .WithEnvironment("Foundry:Model", foundryModel)
    .WithEnvironment("CustomerService:ApiUrl", api.GetEndpoint("https"))
    .WaitFor(api);

customerService
    .WithEnvironment("Demo:CookieName", "eShop.CustomerService.Agentic")
    .WithEnvironment("Agentic:ServiceUrl", agentServer.GetEndpoint("https"));

builder.Build().Run();

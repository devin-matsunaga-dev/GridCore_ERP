using GridCore.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var infrastructure = builder.AddGridCoreInfrastructure();
var webHost = builder.AddGridCoreWebHost(infrastructure);

// Returns null until WP-0.6 creates `web/`.
builder.AddGridCoreWebApp(webHost);

builder.Build().Run();

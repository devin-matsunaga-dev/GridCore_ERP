using GridCore.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

var infrastructure = builder.AddGridCoreInfrastructure();
var webHost = builder.AddGridCoreWebHost(infrastructure);

builder.AddGridCoreWebApp(webHost, infrastructure);

builder.Build().Run();

using GridCore.Modules.Assets;
using GridCore.Modules.Billing;
using GridCore.Modules.Customers;
using GridCore.Modules.Finance;
using GridCore.Modules.Inventory;
using GridCore.Modules.Metering;
using GridCore.Modules.Payments;
using GridCore.Modules.WorkOrders;
using GridCore.Platform.Modules;

var builder = WebApplication.CreateBuilder(args);

// Telemetry, resilience, service discovery and the "self" health check.
builder.AddServiceDefaults();

// Backing services are supplied by the Aspire AppHost; each client integration also
// registers a health check, which is what /health aggregates. Names match the AppHost
// resource names. Keycloak (WP-0.3) and MinIO (later) are wired by their own packages.
builder.AddNpgsqlDataSource("gridcore");
builder.AddRedisClient("redis");
builder.AddRabbitMQClient("rabbitmq");

var modules = builder.Services.AddModules(
    builder.Configuration,
    new CustomersModule(),
    new MeteringModule(),
    new BillingModule(),
    new PaymentsModule(),
    new AssetsModule(),
    new WorkOrdersModule(),
    new InventoryModule(),
    new FinanceModule());

var app = builder.Build();

// /health (aggregate) and /alive (liveness).
app.MapDefaultEndpoints();

app.MapModules(modules);

app.Run();

/// <summary>Exposed so the gate-tier integration suite can boot the host with WebApplicationFactory.</summary>
public partial class Program;

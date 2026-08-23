using GridCore.Modules.Assets;
using GridCore.Modules.Billing;
using GridCore.Modules.Customers;
using GridCore.Modules.Finance;
using GridCore.Modules.Inventory;
using GridCore.Modules.Metering;
using GridCore.Modules.Payments;
using GridCore.Modules.WorkOrders;
using GridCore.Platform;
using GridCore.Platform.Messaging;
using GridCore.Platform.Modules;
using GridCore.Platform.Security;

var builder = WebApplication.CreateBuilder(args);

// Telemetry, resilience, service discovery and the "self" health check.
builder.AddServiceDefaults();

// Backing services are supplied by the Aspire AppHost; each client integration also
// registers a health check, which is what /health aggregates. Names match the AppHost
// resource names. MinIO (later) is wired by its own package.
builder.AddNpgsqlDataSource("gridcore");
builder.AddRedisClient("redis");
builder.AddRabbitMQClient("rabbitmq");

// OIDC bearer auth + permission-based authorization. Authority/audience come from the
// Authentication section, which the AppHost points at the Keycloak realm.
builder.Services.AddGridCoreSecurity(builder.Configuration);

// Audit, approvals, notifications and the scheduler, over the platform schema.
builder.Services.AddGridCorePlatform(builder.Configuration, builder.Environment);

// RFC 7807 bodies for the framework's own 401/403/404 responses, per CONVENTIONS.md.
builder.Services.AddProblemDetails();

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

// The bus is configured after the modules on purpose: AddGridCoreMessaging reads back the
// consumers they registered, so the composition stays explicit and nothing is assembly-scanned.
builder.Services.AddGridCoreMessaging(builder.Configuration);

var app = builder.Build();

app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

// /health (aggregate) and /alive (liveness). Anonymous — Aspire probes them without a token.
app.MapDefaultEndpoints();

app.MapPlatformEndpoints();
app.MapModules(modules);

app.Run();

/// <summary>Exposed so the gate-tier integration suite can boot the host with WebApplicationFactory.</summary>
public partial class Program;

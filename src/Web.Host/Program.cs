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

// Infrastructure (Postgres, Redis, RabbitMQ, Keycloak, MinIO) is wired in WP-0.2.
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

app.MapModules(modules);

app.Run();

/// <summary>Exposed so the gate-tier integration suite can boot the host with WebApplicationFactory.</summary>
public partial class Program;

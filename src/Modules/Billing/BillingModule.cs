using GridCore.Modules.Billing.Data;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing;

/// <summary>Composition root for the Billing module. Slices live under <c>Features/</c>.</summary>
public sealed class BillingModule : IModule
{
    public string Name => "billing";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The billing schema, on the scope's shared connection so a bill, its audit entry and the
        // BillIssued outbox row commit together. It carries the shipped tariffs (WP-0.8); the rate
        // engine and the bills it produces are WP-2.3's.
        services.AddGridCoreDbContext<BillingDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(BillingDbContext.SchemaName)));
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoints are mapped per feature slice from WP-2.3 onwards.
    }
}

using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Platform.Messaging;
using GridCore.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GridCore.Modules.Finance;

/// <summary>Composition root for the Finance module. Slices live under <c>Features/</c>.</summary>
public sealed class FinanceModule : IModule
{
    public string Name => "finance";

    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The event seam: Finance is downstream of Billing, Payments and Inventory and reacts to
        // their facts. The ledger behind the seam is WP-2.6's; the wiring is proven here.
        services.TryAddScoped<IJournalPostingSeam, LoggingJournalPostingSeam>();

        services.AddEventConsumer<BillIssuedConsumer>();
        services.AddEventConsumer<PaymentApprovedConsumer>();
        services.AddEventConsumer<GoodsReceivedConsumer>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Endpoints are mapped per feature slice from WP-2.6 onwards.
    }
}

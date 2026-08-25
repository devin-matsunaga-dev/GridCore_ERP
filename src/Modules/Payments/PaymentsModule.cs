using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Data;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Features.Shared;
using GridCore.Modules.Payments.Simulation;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Payments;

/// <summary>Composition root for the Payments module. Slices live under <c>Features/</c>.</summary>
public sealed class PaymentsModule : IModule
{
    /// <inheritdoc />
    public string Name => PaymentsDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The payments schema, on the scope's shared connection so a payment, its audit entry and
        // the PaymentApproved outbox row commit together.
        services.AddGridCoreDbContext<PaymentsDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(PaymentsDbContext.SchemaName)));

        services.AddScoped<IPaymentNumberGenerator, SequentialPaymentNumberGenerator>();
        services.AddScoped<IPaymentService, PaymentService>();

        // The simulation seam. Payments owns the payment sandbox (ARCHITECTURE.md's module table),
        // so it IS registered here — but only ever against the Contracts interface, which is what
        // lets a production deployment swap in a real gateway by changing this line and nothing
        // else (invariant 6). The same shape WP-2.2 established for the meter reading provider, and
        // the one IVendorProvider and ICrewProvider will copy.
        services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();

        // Note what is NOT here: IBillDirectory and IServiceAccountDirectory. This module consumes
        // both and Billing and Customers register them, which is the whole point of putting the
        // interfaces in Contracts — a module never registers another module's implementation, and
        // never references the assembly that holds one.

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<TakePaymentRequest, TakePaymentRequestValidator>();

        // No demo seeder. A seeded payment would either publish PaymentApproved — making the demo
        // world's bills depend on broker timing, which no other seeder does — or not publish, and
        // leave settled payments beside bills that still say they are owed. Neither is a demo world
        // that reconciles, and WP-2.7's end-to-end walk of the revenue cycle is where paid bills
        // belong. See STATUS.md.
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPaymentEndpoints();
    }
}

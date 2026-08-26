using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Documents;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.Seeding;
using GridCore.Platform;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Modules;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Billing;

/// <summary>Composition root for the Billing module. Slices live under <c>Features/</c>.</summary>
public sealed class BillingModule : IModule
{
    /// <inheritdoc />
    public string Name => BillingDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The billing schema, on the scope's shared connection so a bill, its audit entry and the
        // BillIssued outbox row commit together.
        services.AddGridCoreDbContext<BillingDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(BillingDbContext.SchemaName)));

        services.AddScoped<IBillNumberGenerator, SequentialBillNumberGenerator>();
        services.AddScoped<IRatePlanService, RatePlanService>();
        services.AddScoped<IBillService, BillService>();

        // The bill reprint (WP-2.14). Its own service rather than another method on IBillService:
        // that interface is where a bill is raised, issued, corrected and paid, and a read that
        // produces a document for a customer has different rules — it refuses a draft, it is gated on
        // customers.documents, and it audits a read.
        services.AddScoped<IBillDocumentService, BillDocumentService>();

        // The billing register as the rest of GridCore reads it (WP-2.5). Payments takes money
        // against bills and may not touch this schema, so it takes IBillDirectory from Contracts
        // and this module — the only one that knows both halves — registers the implementation.
        services.AddScoped<IBillDirectory, BillDirectory>();

        // Billing's first consumer. It published BillIssued from WP-2.3 and BillAdjusted from
        // WP-2.4; this is the other direction — Payments states that money arrived, and reducing
        // what the document is owed is this module's own work.
        services.AddEventConsumer<PaymentApprovedConsumer>();

        // WP-2.12: a customer's deposit put against a bill settles it the same way a payment does.
        // The event is Customers', and Finance claims it too — under its own consumer name.
        services.AddEventConsumer<CustomerDepositAppliedConsumer>();

        // Note what is NOT here: IMeterReadingDirectory and IServiceAccountDirectory. This module
        // consumes both and Metering and Customers register them, which is the whole point of
        // putting the interfaces in Contracts — a module never registers another module's
        // implementation, and never references the assembly that holds one.

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<RunBillingRequest, RunBillingRequestValidator>();
        services.AddGridCoreValidator<IssueBillRequest, IssueBillRequestValidator>();
        services.AddGridCoreValidator<CancelBillRequest, CancelBillRequestValidator>();
        services.AddGridCoreValidator<AdjustBillRequest, AdjustBillRequestValidator>();
        services.AddGridCoreValidator<OverdueReviewRequest, OverdueReviewRequestValidator>();
        services.AddGridCoreValidator<AssignRatePlanRequest, AssignRatePlanRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<BillsDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapRatePlanEndpoints();
        endpoints.MapBillEndpoints();
        endpoints.MapBillDocumentEndpoints();
    }
}

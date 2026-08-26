using GridCore.Modules.Finance.Data;
using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.EventSeam;
using GridCore.Modules.Finance.Features.Journal;
using GridCore.Modules.Finance.Features.Reports;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Modules;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GridCore.Modules.Finance;

/// <summary>Composition root for the Finance module. Slices live under <c>Features/</c>.</summary>
public sealed class FinanceModule : IModule
{
    /// <inheritdoc />
    public string Name => FinanceDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The finance schema, on the scope's shared connection so a posting commits in the same
        // transaction as its audit entry and as the dedupe claim that says the event was handled.
        services.AddGridCoreDbContext<FinanceDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(FinanceDbContext.SchemaName)));

        // The event seam: Finance is downstream of Billing, Payments and Inventory and reacts to
        // their facts. WP-0.5 proved the wiring with a seam that logged the entry it would post;
        // this is the general ledger behind it, swapped by DI with nothing upstream changed.
        services.TryAddScoped<IJournalPostingSeam, JournalPostingSeam>();
        services.AddScoped<IJournalEntryNumberGenerator, SequentialJournalEntryNumberGenerator>();

        services.AddScoped<IChartOfAccountsService, ChartOfAccountsService>();
        services.AddScoped<IJournalService, JournalService>();
        services.AddScoped<IFinanceReportService, FinanceReportService>();

        services.AddEventConsumer<BillIssuedConsumer>();
        services.AddEventConsumer<BillAdjustedConsumer>();
        services.AddEventConsumer<PaymentApprovedConsumer>();
        services.AddEventConsumer<GoodsReceivedConsumer>();

        // WP-2.12's deposit lifecycle. Three facts, three postings: money held is a liability, money
        // returned reverses it, and money applied to a bill turns it into a settled receivable.
        services.AddEventConsumer<CustomerDepositCollectedConsumer>();
        services.AddEventConsumer<CustomerDepositAppliedConsumer>();
        services.AddEventConsumer<CustomerDepositRefundedConsumer>();

        // Note what is NOT here.
        //
        // No edge validators: nothing is posted from the wire, so there is no request body to
        // validate. No directories, in either direction — Finance registers none because it reads
        // no other module's data, and consumes none because everything an entry needs is on the
        // event that caused it. That is what "Finance is downstream of everyone" means in a
        // composition root.
        //
        // No demo seeder either, and that is a decision rather than an omission. Seeded journal
        // entries would be Finance's own account of a demo world it never actually heard about:
        // the demo bills are written straight to Billing's tables by BillsDemoSeeder, which
        // publishes nothing (a seeder adds entities and never publishes), so the ledger has no
        // events behind it to post. Inventing entries to match would put figures in the trial
        // balance that no upstream fact explains — which is the one thing a ledger must never do.
        // WP-2.7's end-to-end walk of the revenue cycle raises real events, and the ledger fills
        // itself from them. See STATUS.md.
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapJournalEndpoints();
    }
}

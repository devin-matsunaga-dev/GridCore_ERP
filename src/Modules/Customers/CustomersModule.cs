using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Applications;
using GridCore.Modules.Customers.Features.Contacts;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Delinquency;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Documents;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Features.Profile;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Search;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Modules.Customers.Seeding;
using GridCore.Platform;
using GridCore.Platform.Data;
using GridCore.Platform.Modules;
using GridCore.Platform.Validation;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Customers;

/// <summary>Composition root for the Customers module. Slices live under <c>Features/</c>.</summary>
public sealed class CustomersModule : IModule
{
    /// <inheritdoc />
    public string Name => CustomersDbContext.SchemaName;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The customers schema, on the scope's shared connection so a registration, its audit entry
        // and its outbox row commit together.
        services.AddGridCoreDbContext<CustomersDbContext>((builder, connection) =>
            builder.UseNpgsql(connection, GridCoreDbContexts.InSchema(CustomersDbContext.SchemaName)));

        services.AddScoped<IRegistryNumberGenerator, SequentialRegistryNumberGenerator>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IServiceLocationService, ServiceLocationService>();
        services.AddScoped<IServiceAccountService, ServiceAccountService>();

        // Intake (WP-2.8). The deposit schedule is read-only reference data; the registration
        // service composes the three registries above inside one unit of work.
        services.AddScoped<IDepositRuleService, DepositRuleService>();
        services.AddScoped<ICustomerRegistrationService, CustomerRegistrationService>();

        // The deposit lifecycle (WP-2.12): collect, hold, apply to a bill, refund. It consumes
        // IBillDirectory — registered by Billing — to ask what a bill still has outstanding before
        // any of the deposit is put against it, which is the second cross-module seam this module
        // reads (IMeterDirectory was the first, for WP-2.9's search).
        services.AddScoped<ICustomerDepositService, CustomerDepositService>();

        // The deposit re-assessment (WP-2.17): what is held against what the schedule now asks across
        // every open account. A read and only a read — it moves nothing, which is why it is a service
        // of its own gated on customers.read while the lifecycle above gates on customers.deposit. It
        // consumes IUsageDirectory, registered by Metering, because a usage-based deposit needs the
        // authoritative average and this module may not read metering.meter_readings.
        services.AddScoped<IDepositReassessmentService, DepositReassessmentService>();

        // The note log (WP-2.13): free-text notes and logged interactions, append-only. It consumes
        // TWO cross-module read seams — IBillDirectory, registered by Billing, and IPaymentDirectory,
        // registered by Payments — to confirm that a note filed against a bill or a payment names a
        // real one of this customer's. A work-order link is stored unverified until WP-3.1 builds
        // that register; see CustomerNoteLinkKinds.IsVerifiable, which is the one method that changes.
        services.AddScoped<ICustomerNoteService, CustomerNoteService>();

        // Customer documents (WP-2.14): the account statement and the payment-history export, both
        // read-side and both audited because they leave the building. It is the heaviest cross-module
        // read in the phase — IBillDirectory and IPaymentDirectory were both widened for it, each
        // having said in as many words that widening was a work package rather than a field — and it
        // still reads nobody's tables but this module's. The bill reprint is Billing's, because
        // Billing owns the figures a bill was issued with.
        services.AddScoped<ICustomerDocumentService, CustomerDocumentService>();

        // Account transitions (WP-2.15): class and status changes with a reason code and an effective
        // date, and move-in / move-out / transfer. It composes IServiceAccountService rather than
        // reimplementing WP-1.2's state machine, and consumes IBillDirectory for one question — how
        // far back a class change may be dated. The gate, customers.transition, is inside the service
        // and not on the routes, because the intake-style in-process callers would otherwise skip it.
        services.AddScoped<ICustomerTransitionService, CustomerTransitionService>();

        // Service applications (WP-2.18): the reviewed path to an account. It composes
        // IServiceAccountService rather than reimplementing WP-1.2's opening rules — approval calls
        // OpenAsync — and IDepositReassessmentService to quote the deposit the approved supply now
        // asks for. It is the first user of IDocumentStore, the object-store seam in Contracts that
        // the AppHost's MinIO container has been waiting for since WP-0.2. The decision gate,
        // customers.approve, is inside the service and not only on the routes, for the reason
        // customers.transition is: WP-3.6's connection order will reach it in process.
        services.AddScoped<IServiceApplicationService, ServiceApplicationService>();

        // Delinquency, dunning and the statutory deposit offset (WP-2.19). It reads what an account
        // owes through IBillDirectory — Billing owns the register and the ageing bands — and moves a
        // deposit only through ICustomerDepositService, which holds the gate, the audit entry and the
        // event Finance posts from. The evaluation gates on customers.deposit inside the service as
        // well as on the route, because it moves money whether or not there is any to move.
        services.AddScoped<IDelinquencyService, DelinquencyService>();

        // The fourth disconnection test's seam, answered by nobody until WP-2.20 builds payment
        // arrangements. Registered against the null implementation deliberately: writing the test
        // around a hole would mean rewriting it next package, and half an arrangements feature here
        // would be building WP-2.20 badly.
        services.AddScoped<IPaymentArrangementDirectory, NoPaymentArrangements>();

        // Contacts and the customer profile (WP-2.11). Two services rather than one: the contacts a
        // rep may speak to and where the utility posts a bill are different registers with different
        // rules, and only one of them has a permission gate inside it.
        services.AddScoped<ICustomerContactService, CustomerContactService>();
        services.AddScoped<ICustomerProfileService, CustomerProfileService>();

        // CSR search (WP-2.9). Read-only, and the one service in this module that consumes another
        // module's seam: IMeterDirectory is registered by Metering, so a meter number can be
        // resolved to a premise without this module knowing a metering schema exists.
        services.AddScoped<ICustomerSearchService, CustomerSearchService>();

        // The premise registry as the rest of GridCore reads it (WP-2.1). Registered against the
        // Contracts interface rather than the concrete type: this is the one place that knows both
        // halves, and a consumer never learns a customers schema exists.
        services.AddScoped<IServiceLocationDirectory, ServiceLocationDirectory>();

        // The service account registry as the rest of GridCore reads it (WP-2.3). Billing raises a
        // bill against "the account open at the premise this meter is on" — a derivation WP-2.1
        // named and this is what answers it, without Billing learning that a customers schema
        // exists. Registered here for the same reason the premise directory is.
        services.AddScoped<IServiceAccountDirectory, ServiceAccountDirectory>();

        // Edge validation. Registered one by one rather than by scanning, so the composition stays
        // greppable — the same reason Program.cs lists the modules.
        services.AddGridCoreValidator<CreateCustomerRequest, CreateCustomerRequestValidator>();
        services.AddGridCoreValidator<UpdateCustomerRequest, UpdateCustomerRequestValidator>();
        services.AddGridCoreValidator<ChangeCustomerClassRequest, ChangeCustomerClassRequestValidator>();
        services.AddGridCoreValidator<ChangeCustomerStatusRequest, ChangeCustomerStatusRequestValidator>();
        services.AddGridCoreValidator<MoveInRequest, MoveInRequestValidator>();
        services.AddGridCoreValidator<MoveOutRequest, MoveOutRequestValidator>();
        services.AddGridCoreValidator<TransferServiceRequest, TransferServiceRequestValidator>();
        services.AddGridCoreValidator<ServiceLocationRequest, ServiceLocationRequestValidator>();
        services.AddGridCoreValidator<OpenServiceAccountRequest, OpenServiceAccountRequestValidator>();
        services.AddGridCoreValidator<ServiceAccountTransitionRequest, ServiceAccountTransitionRequestValidator>();
        services.AddGridCoreValidator<RegisterCustomerIntakeRequest, RegisterCustomerIntakeRequestValidator>();
        services.AddGridCoreValidator<CreateContactRequest, CreateContactRequestValidator>();
        services.AddGridCoreValidator<UpdateContactRequest, UpdateContactRequestValidator>();
        services.AddGridCoreValidator<ContactMethodRequest, ContactMethodRequestValidator>();
        services.AddGridCoreValidator<UpdateContactMethodRequest, UpdateContactMethodRequestValidator>();
        services.AddGridCoreValidator<UpdateCustomerProfileRequest, UpdateCustomerProfileRequestValidator>();
        services.AddGridCoreValidator<CollectDepositRequest, CollectDepositRequestValidator>();
        services.AddGridCoreValidator<ApplyDepositRequest, ApplyDepositRequestValidator>();
        services.AddGridCoreValidator<RefundDepositRequest, RefundDepositRequestValidator>();
        services.AddGridCoreValidator<SubmitApplicationRequest, SubmitApplicationRequestValidator>();
        services.AddGridCoreValidator<DecideApplicationRequest, DecideApplicationRequestValidator>();
        services.AddGridCoreValidator<ResubmitApplicationRequest, ResubmitApplicationRequestValidator>();
        services.AddGridCoreValidator<LogNoteRequest, LogNoteRequestValidator>();
        services.AddGridCoreValidator<CorrectNoteRequest, CorrectNoteRequestValidator>();
        services.AddGridCoreValidator<PinNoteRequest, PinNoteRequestValidator>();
        services.AddGridCoreValidator<ServeNoticeRequest, ServeNoticeRequestValidator>();

        // Registering a seeder does not make it run: DemoSeedRunner is only registered where the
        // environment allows it, so this line is unconditional and the guard stays in one place.
        services.AddDemoSeeder<CustomersDemoSeeder>();
        services.AddDemoSeeder<ServiceAccountsDemoSeeder>();
        services.AddDemoSeeder<AccountTransitionsDemoSeeder>();
        services.AddDemoSeeder<CustomerNotesDemoSeeder>();
        services.AddDemoSeeder<ServiceApplicationsDemoSeeder>();
    }

    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapCustomerEndpoints();
        endpoints.MapServiceLocationEndpoints();
        endpoints.MapServiceAccountEndpoints();
        endpoints.MapRegistrationEndpoints();
        endpoints.MapCustomerSearchEndpoints();
        endpoints.MapContactEndpoints();
        endpoints.MapProfileEndpoints();
        endpoints.MapDepositEndpoints();
        endpoints.MapNoteEndpoints();
        endpoints.MapDocumentEndpoints();
        endpoints.MapTransitionEndpoints();
        endpoints.MapApplicationEndpoints();
        endpoints.MapDelinquencyEndpoints();
    }
}

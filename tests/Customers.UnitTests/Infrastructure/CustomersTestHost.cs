using GridCore.Contracts.Directories;
using GridCore.Contracts.Providers;
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
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Messaging;
using GridCore.Platform.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GridCore.Modules.Customers.UnitTests.Infrastructure;

/// <summary>
/// The customers schema and the platform schema on one SQLite in-memory connection — the fast-tier
/// equivalent of the shared Postgres connection the host gives a request. That is what lets these
/// tests assert the thing that actually matters about a registry write: the customer row, its audit
/// entry and its event all belong to one transaction (CONVENTIONS.md rule C).
/// </summary>
public sealed class CustomersTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    /// <param name="clock">The clock the host uses; a <see cref="FakeClock"/> keeps tests off wall time.</param>
    /// <param name="currentUser">Who is acting. Defaults to the system, as background work is.</param>
    public CustomersTestHost(TimeProvider? clock = null, ICurrentUser? currentUser = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(currentUser ?? SystemUser.Instance);
        services.AddSingleton<IEventPublisher>(Events);

        // Metering registers the real IMeterDirectory; a Customers test may not resolve it, because
        // a metering schema is the thing this module must never know about. WP-2.9's search consumes
        // the seam, so the fast tier stands a double in front of it.
        services.AddSingleton<IMeterDirectory>(Meters);

        // Billing registers the real IBillDirectory; a Customers test may not resolve it, for the
        // same reason. WP-2.12's deposit lifecycle asks it what a bill still has outstanding before
        // any of a deposit is applied, so the fast tier stands a double in front of it too.
        services.AddSingleton<IBillDirectory>(Bills);

        // Payments registers the real IPaymentDirectory; a Customers test may not resolve it, for the
        // same reason again. WP-2.13's note log asks it whether a payment a note is filed against is
        // a real payment of that customer's, so the fast tier stands a double in front of it too.
        services.AddSingleton<IPaymentDirectory>(Payments);

        // Metering registers the real IUsageDirectory; a Customers test may not resolve it, for the
        // same reason again. WP-2.17's deposit re-assessment asks it what a premise averages a month
        // before it prices a usage-based rule, so the fast tier stands a double in front of it too.
        services.AddSingleton<IUsageDirectory>(Usage);

        // The Platform registers the real MinIO-backed IDocumentStore; a fast-tier test may not
        // resolve it, because a container is exactly what CONVENTIONS.md rule C forbids here.
        // WP-2.18's application documents are the seam's first user, so the fast tier stands a
        // dictionary in front of it and the round trip against real MinIO is one gate-tier test.
        services.AddSingleton<IDocumentStore>(Documents);

        // ownsConnection: false — the in-memory database lives only as long as this connection, and
        // each scope disposing it would delete the database mid-test.
        services.AddGridCoreDataAccess(_ => new GridCoreDbConnection(_connection, ownsConnection: false));
        services.AddGridCoreDbContext<PlatformDbContext>((builder, connection) => builder.UseSqlite(connection));
        services.AddGridCoreDbContext<CustomersDbContext>((builder, connection) => builder.UseSqlite(connection));

        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IRegistryNumberGenerator, SequentialRegistryNumberGenerator>();
        services.AddScoped<ICustomerService, CustomerService>();
        services.AddScoped<IServiceLocationService, ServiceLocationService>();
        services.AddScoped<IServiceAccountService, ServiceAccountService>();
        services.AddScoped<IDepositRuleService, DepositRuleService>();
        services.AddScoped<ICustomerRegistrationService, CustomerRegistrationService>();
        services.AddScoped<ICustomerSearchService, CustomerSearchService>();
        services.AddScoped<ICustomerContactService, CustomerContactService>();
        services.AddScoped<ICustomerProfileService, CustomerProfileService>();
        services.AddScoped<IDepositReassessmentService, DepositReassessmentService>();
        services.AddScoped<ICustomerDepositService, CustomerDepositService>();
        services.AddScoped<ICustomerNoteService, CustomerNoteService>();
        services.AddScoped<ICustomerDocumentService, CustomerDocumentService>();
        services.AddScoped<ICustomerTransitionService, CustomerTransitionService>();
        services.AddScoped<IServiceApplicationService, ServiceApplicationService>();

        // Delinquency, dunning and the statutory deposit offset (WP-2.19). The arrangement seam is
        // the fake below rather than NoPaymentArrangements: WP-2.20 has not been built, and a test
        // that wants to prove an arrangement suppresses disconnection has to be able to say there is
        // one. CustomersModuleTests is what pins the real composition to the null implementation.
        services.AddSingleton<IPaymentArrangementDirectory>(Arrangements);
        services.AddScoped<IDelinquencyService, DelinquencyService>();
        services.AddScoped<IServiceLocationDirectory, ServiceLocationDirectory>();
        services.AddScoped<IServiceAccountDirectory, ServiceAccountDirectory>();

        _provider = services.BuildServiceProvider();

        CreateTables();
    }

    /// <summary>Everything the registry published while the test ran.</summary>
    public RecordingEventPublisher Events { get; } = new();

    /// <summary>The meter register the search box resolves a meter number through.</summary>
    public FakeMeterDirectory Meters { get; } = new();

    /// <summary>The billing register the deposit ledger asks what a bill still has outstanding.</summary>
    public FakeBillDirectory Bills { get; } = new();

    /// <summary>The payment register the note log asks whether a linked payment is this customer's.</summary>
    public FakePaymentDirectory Payments { get; } = new();

    /// <summary>The usage register a usage-based deposit is assessed against (WP-2.17).</summary>
    public FakeUsageDirectory Usage { get; } = new();

    /// <summary>The object store an application's documents are filed in (WP-2.18).</summary>
    public FakeDocumentStore Documents { get; } = new();

    /// <summary>The payment arrangements the fourth disconnection test asks about (WP-2.19).</summary>
    public FakePaymentArrangementDirectory Arrangements { get; } = new();

    /// <summary>Runs <paramref name="work"/> in its own DI scope, as a request would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();

        return await work(scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="work"/> against the customer registry, in its own scope.</summary>
    public Task<TResult> WithCustomersAsync<TResult>(Func<ICustomerService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the location registry, in its own scope.</summary>
    public Task<TResult> WithLocationsAsync<TResult>(Func<IServiceLocationService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceLocationService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the service account registry, in its own scope.</summary>
    public Task<TResult> WithAccountsAsync<TResult>(Func<IServiceAccountService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceAccountService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the intake wizard's one commit, in its own scope — the
    /// same scope every registry it composes resolves from, which is what makes the nested units of
    /// work one transaction.
    /// </summary>
    public Task<TResult> WithIntakeAsync<TResult>(Func<ICustomerRegistrationService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerRegistrationService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the CSR search box, in its own scope.</summary>
    public Task<TResult> WithSearchAsync<TResult>(Func<ICustomerSearchService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerSearchService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the contact register, in its own scope.</summary>
    public Task<TResult> WithContactsAsync<TResult>(Func<ICustomerContactService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerContactService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the customer profile, in its own scope.</summary>
    public Task<TResult> WithProfileAsync<TResult>(Func<ICustomerProfileService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerProfileService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the deposit lifecycle, in its own scope.</summary>
    public Task<TResult> WithDepositsAsync<TResult>(Func<ICustomerDepositService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerDepositService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the deposit lifecycle as <paramref name="caller"/>,
    /// over this host's database.
    /// </summary>
    /// <remarks>
    /// The one place a test needs two identities against one dataset: proving a movement is refused
    /// means somebody who <i>may</i> move money has to have put some there first. Composed by hand
    /// rather than through the container, because the caller is the single dependency being swapped.
    /// </remarks>
    public Task<TResult> AsAsync<TResult>(ICurrentUser caller, Func<ICustomerDepositService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(new CustomerDepositService(
            services.GetRequiredService<CustomersDbContext>(),
            services.GetRequiredService<IDepositReassessmentService>(),
            Bills,
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IAuditLog>(),
            Events,
            caller,
            services.GetRequiredService<TimeProvider>())));
    }

    /// <summary>Runs <paramref name="work"/> against the customer's note log, in its own scope.</summary>
    public Task<TResult> WithNotesAsync<TResult>(Func<ICustomerNoteService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerNoteService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the note log as <paramref name="caller"/>, over this
    /// host's database.
    /// </summary>
    /// <remarks>
    /// The same shape <see cref="AsAsync{TResult}(ICurrentUser, Func{ICustomerDepositService, Task{TResult}})"/>
    /// takes, and needed for the same reason: proving that a note records <i>who</i> logged it means
    /// two identities writing against one dataset. Composed by hand rather than through the
    /// container, because the caller is the single dependency being swapped.
    /// </remarks>
    public Task<TResult> AsAsync<TResult>(ICurrentUser caller, Func<ICustomerNoteService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(new CustomerNoteService(
            services.GetRequiredService<CustomersDbContext>(),
            Bills,
            Payments,
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IAuditLog>(),
            caller,
            services.GetRequiredService<TimeProvider>())));
    }

    /// <summary>Runs <paramref name="work"/> against the customer's documents, in its own scope.</summary>
    public Task<TResult> WithDocumentsAsync<TResult>(Func<ICustomerDocumentService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerDocumentService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the customer's documents as <paramref name="caller"/>,
    /// over this host's database.
    /// </summary>
    /// <remarks>
    /// The same shape the deposit ledger and the note log take, and needed for the same reason: a
    /// document that leaves the building is gated on <c>customers.documents</c> (WP-2.14), and
    /// proving the refusal means a caller who does not hold it reading an account somebody who does
    /// has already put activity on.
    /// </remarks>
    public Task<TResult> AsAsync<TResult>(ICurrentUser caller, Func<ICustomerDocumentService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(new CustomerDocumentService(
            services.GetRequiredService<CustomersDbContext>(),
            services.GetRequiredService<ICustomerProfileService>(),
            Bills,
            Payments,
            services.GetRequiredService<IAuditLog>(),
            caller,
            services.GetRequiredService<TimeProvider>())));
    }

    /// <summary>Runs <paramref name="work"/> against the transition register, in its own scope.</summary>
    public Task<TResult> WithTransitionsAsync<TResult>(Func<ICustomerTransitionService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<ICustomerTransitionService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the transition register as <paramref name="caller"/>,
    /// over this host's database.
    /// </summary>
    /// <remarks>
    /// The same shape the deposit ledger, the note log and the documents take, and needed for the
    /// same reason: WP-2.15's transitions are gated on <c>customers.transition</c> inside the service,
    /// and proving the refusal means a caller who does not hold it acting on a customer somebody who
    /// does has already set up.
    /// </remarks>
    public Task<TResult> AsAsync<TResult>(ICurrentUser caller, Func<ICustomerTransitionService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(new CustomerTransitionService(
            services.GetRequiredService<CustomersDbContext>(),
            services.GetRequiredService<IServiceAccountService>(),
            services.GetRequiredService<IDepositRuleService>(),
            Bills,
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IAuditLog>(),
            Events,
            caller,
            services.GetRequiredService<TimeProvider>())));
    }

    /// <summary>Runs <paramref name="work"/> against the application register, in its own scope (WP-2.18).</summary>
    public Task<TResult> WithApplicationsAsync<TResult>(Func<IServiceApplicationService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceApplicationService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the application register as <paramref name="caller"/>,
    /// over this host's database.
    /// </summary>
    /// <remarks>
    /// The same shape the deposit ledger, the note log, the documents and the transitions take, and
    /// needed for the same reason twice over: deciding an application is gated on
    /// <c>customers.approve</c> and reading an uploaded document on <c>customers.documents</c>, both
    /// inside the service — so proving either refusal means a caller who does not hold it acting on
    /// an application somebody who does has already set up.
    /// </remarks>
    public Task<TResult> AsAsync<TResult>(ICurrentUser caller, Func<IServiceApplicationService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(new ServiceApplicationService(
            services.GetRequiredService<CustomersDbContext>(),
            services.GetRequiredService<IServiceAccountService>(),
            services.GetRequiredService<IDepositReassessmentService>(),
            Documents,
            services.GetRequiredService<IRegistryNumberGenerator>(),
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IAuditLog>(),
            caller,
            services.GetRequiredService<TimeProvider>())));
    }

    /// <summary>Runs <paramref name="work"/> against the delinquency register, in its own scope (WP-2.19).</summary>
    public Task<TResult> WithDelinquencyAsync<TResult>(Func<IDelinquencyService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IDelinquencyService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the delinquency register as <paramref name="caller"/>,
    /// over this host's database.
    /// </summary>
    /// <remarks>
    /// The same shape every other refusal in this module takes, and needed for the sharpest reason
    /// yet: evaluating an account for disconnection sets a customer's deposit against what they owe,
    /// so it is gated on <c>customers.deposit</c> inside the service — and proving that refusal means
    /// a caller who does not hold it judging an account somebody who does has already put in arrears.
    /// </remarks>
    public Task<TResult> AsAsync<TResult>(ICurrentUser caller, Func<IDelinquencyService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(new DelinquencyService(
            services.GetRequiredService<CustomersDbContext>(),
            Bills,
            new CustomerDepositService(
                services.GetRequiredService<CustomersDbContext>(),
                services.GetRequiredService<IDepositReassessmentService>(),
                Bills,
                services.GetRequiredService<IUnitOfWork>(),
                services.GetRequiredService<IAuditLog>(),
                Events,
                caller,
                services.GetRequiredService<TimeProvider>()),
            Arrangements,
            services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IAuditLog>(),
            caller,
            services.GetRequiredService<TimeProvider>())));
    }

    /// <summary>Runs <paramref name="work"/> against the deposit schedule, in its own scope.</summary>
    public Task<TResult> WithDepositRulesAsync<TResult>(Func<IDepositRuleService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IDepositRuleService>()));
    }

    /// <summary>Runs <paramref name="work"/> against the deposit re-assessment, in its own scope (WP-2.17).</summary>
    public Task<TResult> WithReassessmentAsync<TResult>(Func<IDepositReassessmentService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IDepositReassessmentService>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the premise registry <i>as another module sees it</i> —
    /// the cross-module read seam, resolved from the container exactly as Metering resolves it.
    /// </summary>
    public Task<TResult> WithDirectoryAsync<TResult>(Func<IServiceLocationDirectory, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceLocationDirectory>()));
    }

    /// <summary>
    /// Runs <paramref name="work"/> against the service account registry <i>as another module sees
    /// it</i> — the seam Billing (WP-2.3) raises every bill through, resolved from the container
    /// exactly as Billing resolves it.
    /// </summary>
    public Task<TResult> WithAccountDirectoryAsync<TResult>(Func<IServiceAccountDirectory, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IServiceAccountDirectory>()));
    }

    /// <summary>Reads back what a test wrote, on a context outside any unit of work.</summary>
    public CustomersDbContext NewCustomersContext() =>
        new(new DbContextOptionsBuilder<CustomersDbContext>().UseSqlite(_connection).Options);

    /// <summary>Reads back the audit trail a registry write produced.</summary>
    public PlatformDbContext NewPlatformContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Creates both schemas. <c>EnsureCreated</c> cannot do this: it returns false once the
    /// database exists, so the second context's tables would silently never be created. It also
    /// emits the configurations' <c>HasData</c> inserts, which is how the shipped deposit schedule
    /// (WP-2.8) reaches the fast tier without a migration — an intake cannot assess without it.
    /// </summary>
    private void CreateTables()
    {
        using var scope = _provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();

        scope.ServiceProvider.GetRequiredService<CustomersDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();
    }
}

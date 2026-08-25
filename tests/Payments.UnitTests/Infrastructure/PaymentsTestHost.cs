using GridCore.Contracts.Directories;
using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Data;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.Features.Shared;
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

namespace GridCore.Modules.Payments.UnitTests.Infrastructure;

/// <summary>
/// The payments schema and the platform schema on one SQLite in-memory connection — the fast-tier
/// equivalent of the shared Postgres connection the host gives a request. That is what lets these
/// tests assert the thing that actually matters about taking money: the payment row, its audit
/// entry and its <c>PaymentApproved</c> event all belong to one transaction (CONVENTIONS.md rule
/// C).
/// </summary>
/// <remarks>
/// The billing and customers schemas are deliberately <b>absent</b>. Payments reads bills through
/// <see cref="IBillDirectory"/> and accounts through <see cref="IServiceAccountDirectory"/>, so the
/// two fakes stand in for two whole modules and their databases — which is the point of the seams,
/// and the reason a payment can be tested in milliseconds.
/// </remarks>
public sealed class PaymentsTestHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    /// <param name="clock">The clock the host uses; a <see cref="FakeClock"/> keeps tests off wall time.</param>
    /// <param name="currentUser">Who is acting. Defaults to the system, as background work is.</param>
    public PaymentsTestHost(TimeProvider? clock = null, ICurrentUser? currentUser = null)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var services = new ServiceCollection();

        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton(currentUser ?? SystemUser.Instance);
        services.AddSingleton<IEventPublisher>(Events);
        services.AddSingleton<IPaymentProvider>(Provider);
        services.AddSingleton<IBillDirectory>(Bills);
        services.AddSingleton<IServiceAccountDirectory>(Accounts);

        // ownsConnection: false — the in-memory database lives only as long as this connection, and
        // each scope disposing it would delete the database mid-test.
        services.AddGridCoreDataAccess(_ => new GridCoreDbConnection(_connection, ownsConnection: false));
        services.AddGridCoreDbContext<PlatformDbContext>((builder, connection) => builder.UseSqlite(connection));
        services.AddGridCoreDbContext<PaymentsDbContext>((builder, connection) => builder.UseSqlite(connection));

        services.AddScoped<IAuditLog, AuditLog>();
        services.AddScoped<IPaymentNumberGenerator, SequentialPaymentNumberGenerator>();
        services.AddScoped<IPaymentService, PaymentService>();

        _provider = services.BuildServiceProvider();

        CreateTables();
    }

    /// <summary>Everything the register published while the test ran.</summary>
    public RecordingEventPublisher Events { get; } = new();

    /// <summary>The provider whose answers the test chooses.</summary>
    public StubPaymentProvider Provider { get; } = new();

    /// <summary>The bills the register is allowed to see.</summary>
    public FakeBillDirectory Bills { get; } = new();

    /// <summary>The service accounts the register is allowed to see.</summary>
    public FakeServiceAccountDirectory Accounts { get; } = new();

    /// <summary>Runs <paramref name="work"/> in its own DI scope, as a request would.</summary>
    public async Task<TResult> InScopeAsync<TResult>(Func<IServiceProvider, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        await using var scope = _provider.CreateAsyncScope();

        return await work(scope.ServiceProvider);
    }

    /// <summary>Runs <paramref name="work"/> against the payments register, in its own scope.</summary>
    public Task<TResult> WithPaymentsAsync<TResult>(Func<IPaymentService, Task<TResult>> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        return InScopeAsync(services => work(services.GetRequiredService<IPaymentService>()));
    }

    /// <summary>
    /// Adds an account and an outstanding bill on it, which is what nearly every test needs first.
    /// </summary>
    public (ServiceAccountSummary Account, BillSummary Bill) AnOutstandingBill(
        decimal amountDue = 120.00m,
        decimal amountPaid = 0m,
        string status = "Issued")
    {
        var account = Accounts.Add();
        var bill = Bills.Add(account.Id, account.CustomerId, amountDue, amountPaid, status);

        return (account, bill);
    }

    /// <summary>Reads back what a test wrote, on a context outside any unit of work.</summary>
    public PaymentsDbContext NewPaymentsContext() =>
        new(new DbContextOptionsBuilder<PaymentsDbContext>().UseSqlite(_connection).Options);

    /// <summary>Reads back the audit trail a register write produced.</summary>
    public PlatformDbContext NewPlatformContext() =>
        new(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// Creates both schemas. <c>EnsureCreated</c> cannot do this: it returns false once the
    /// database exists, so the second context's tables would silently never be created.
    /// </summary>
    private void CreateTables()
    {
        using var scope = _provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();

        scope.ServiceProvider.GetRequiredService<PaymentsDbContext>()
            .Database.GetService<IRelationalDatabaseCreator>().CreateTables();
    }
}

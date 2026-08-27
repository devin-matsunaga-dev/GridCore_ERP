using GridCore.Modules.Customers.Data;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Shared;

/// <summary>
/// Issues the next customer account number and service location code. A seam, so the numbering
/// scheme is one registration away from changing — a utility migrating from a legacy system
/// usually has to keep its own.
/// </summary>
public interface IRegistryNumberGenerator
{
    /// <summary>The next unused customer account number.</summary>
    Task<string> NextCustomerAccountNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>The next unused service location code.</summary>
    Task<string> NextServiceLocationCodeAsync(CancellationToken cancellationToken = default);

    /// <summary>The next unused service account number.</summary>
    Task<string> NextServiceAccountNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>The next unused service application number (WP-2.18).</summary>
    Task<string> NextServiceApplicationNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>The next unused payment arrangement number (WP-2.20).</summary>
    Task<string> NextPaymentArrangementNumberAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Continues each series from the highest number already issued, inside the caller's transaction.
/// </summary>
/// <remarks>
/// Each series is one <see cref="RegistryNumberSeries.NextAsync"/> over this module's own column;
/// the race and the ordering trade are documented there, because every registry shares them.
/// </remarks>
public sealed class SequentialRegistryNumberGenerator(CustomersDbContext database) : IRegistryNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextCustomerAccountNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            CustomerNumbers.CustomerPrefix,
            database.Customers
                .Where(customer => customer.AccountNumber.StartsWith(CustomerNumbers.CustomerPrefix))
                .OrderByDescending(customer => customer.AccountNumber)
                .Select(customer => customer.AccountNumber),
            cancellationToken);

    /// <inheritdoc />
    public Task<string> NextServiceLocationCodeAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            CustomerNumbers.ServiceLocationPrefix,
            database.ServiceLocations
                .Where(location => location.LocationCode.StartsWith(CustomerNumbers.ServiceLocationPrefix))
                .OrderByDescending(location => location.LocationCode)
                .Select(location => location.LocationCode),
            cancellationToken);

    /// <inheritdoc />
    public Task<string> NextServiceAccountNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            CustomerNumbers.ServiceAccountPrefix,
            database.ServiceAccounts
                .Where(account => account.AccountNumber.StartsWith(CustomerNumbers.ServiceAccountPrefix))
                .OrderByDescending(account => account.AccountNumber)
                .Select(account => account.AccountNumber),
            cancellationToken);

    /// <inheritdoc />
    public Task<string> NextServiceApplicationNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            CustomerNumbers.ServiceApplicationPrefix,
            database.ServiceApplications
                .Where(application => application.ApplicationNumber.StartsWith(CustomerNumbers.ServiceApplicationPrefix))
                .OrderByDescending(application => application.ApplicationNumber)
                .Select(application => application.ApplicationNumber),
            cancellationToken);

    /// <inheritdoc />
    public Task<string> NextPaymentArrangementNumberAsync(CancellationToken cancellationToken = default) =>
        RegistryNumberSeries.NextAsync(
            CustomerNumbers.PaymentArrangementPrefix,
            database.PaymentArrangements
                .Where(arrangement => arrangement.ArrangementNumber.StartsWith(CustomerNumbers.PaymentArrangementPrefix))
                .OrderByDescending(arrangement => arrangement.ArrangementNumber)
                .Select(arrangement => arrangement.ArrangementNumber),
            cancellationToken);
}

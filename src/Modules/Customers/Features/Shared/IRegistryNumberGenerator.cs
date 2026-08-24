using GridCore.Modules.Customers.Data;
using Microsoft.EntityFrameworkCore;

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
}

/// <summary>
/// Continues each series from the highest number already issued, inside the caller's transaction.
/// </summary>
/// <remarks>
/// <para>
/// Two registrations racing would read the same highest number and try to issue it twice. The
/// unique index is what makes that safe: the loser's transaction is rejected and its caller gets a
/// 409, so a duplicate account number is impossible rather than merely unlikely. That is the right
/// trade for an MVP whose registrations are typed in by hand — a Postgres sequence would serialise
/// the issue, at the cost of SQL the fast tier's SQLite cannot run, and swapping this
/// implementation for one is a DI change with no domain code touched.
/// </para>
/// <para>
/// The lookup is an <c>ORDER BY … DESC LIMIT 1</c> over the unique index rather than a
/// <c>MAX</c> over a parsed substring, which works because <see cref="RegistryNumbers"/> pads to a
/// fixed width: the lexical maximum and the numeric maximum are the same string.
/// </para>
/// </remarks>
public sealed class SequentialRegistryNumberGenerator(CustomersDbContext database) : IRegistryNumberGenerator
{
    /// <inheritdoc />
    public Task<string> NextCustomerAccountNumberAsync(CancellationToken cancellationToken = default) =>
        NextAsync(
            RegistryNumbers.CustomerPrefix,
            database.Customers
                .Where(customer => customer.AccountNumber.StartsWith(RegistryNumbers.CustomerPrefix))
                .OrderByDescending(customer => customer.AccountNumber)
                .Select(customer => customer.AccountNumber),
            cancellationToken);

    /// <inheritdoc />
    public Task<string> NextServiceLocationCodeAsync(CancellationToken cancellationToken = default) =>
        NextAsync(
            RegistryNumbers.ServiceLocationPrefix,
            database.ServiceLocations
                .Where(location => location.LocationCode.StartsWith(RegistryNumbers.ServiceLocationPrefix))
                .OrderByDescending(location => location.LocationCode)
                .Select(location => location.LocationCode),
            cancellationToken);

    private static async Task<string> NextAsync(string prefix, IQueryable<string> issued, CancellationToken cancellationToken)
    {
        var highest = await issued.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return RegistryNumbers.Format(prefix, (RegistryNumbers.OrdinalOf(prefix, highest) ?? 0) + 1);
    }
}

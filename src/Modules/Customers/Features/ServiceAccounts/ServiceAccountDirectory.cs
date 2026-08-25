using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>
/// Customers' answer to <see cref="IServiceAccountDirectory"/>: the service account registry as the
/// rest of GridCore is allowed to see it.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="ServiceLocations.ServiceLocationDirectory"/>, registered by
/// <see cref="CustomersModule"/> — the only place that knows both halves. Billing (WP-2.3) has to
/// name the account and the customer on a bill and may neither reference this module nor read
/// <c>customers.service_accounts</c>, so it takes the interface from <c>Contracts</c> and this
/// answers it.
/// </para>
/// <para>
/// Every query is <c>AsNoTracking</c> and every one projects to
/// <see cref="ServiceAccountSummary"/>. The customer's name is joined here rather than left to the
/// caller: it is one query either way, and a caller that had to fetch it separately would be a
/// caller holding two ids and a reason to ask for a customer directory it does not need.
/// </para>
/// </remarks>
public sealed class ServiceAccountDirectory(CustomersDbContext database) : IServiceAccountDirectory
{
    /// <summary>The largest batch a lookup will answer, whatever the caller asks for.</summary>
    public const int MaxBatchSize = ServiceAccountService.MaxPageSize;

    /// <inheritdoc />
    public async Task<ServiceAccountSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var found = await Summaries(Accounts().Where(account => account.Id == id))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : Summarise(found);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindManyAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // Distinct before the query, as the premise directory does: a page of bills for one account
        // would otherwise send the same id a dozen times, and the answer is keyed by id anyway.
        var wanted = ids.Distinct().Take(MaxBatchSize).ToArray();

        if (wanted.Length is 0)
        {
            return new Dictionary<Guid, ServiceAccountSummary>();
        }

        var found = await Summaries(Accounts().Where(account => wanted.Contains(account.Id)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return found.ToDictionary(row => row.Account.Id, Summarise);
    }

    /// <inheritdoc />
    public async Task<ServiceAccountSummary?> FindOpenAtLocationAsync(
        Guid serviceLocationId,
        CancellationToken cancellationToken = default)
    {
        var found = await Summaries(Open().Where(account => account.ServiceLocationId == serviceLocationId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return found is null ? null : Summarise(found);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ServiceAccountSummary>> FindOpenAtLocationsAsync(
        IReadOnlyCollection<Guid> serviceLocationIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceLocationIds);

        var wanted = serviceLocationIds.Distinct().Take(MaxBatchSize).ToArray();

        if (wanted.Length is 0)
        {
            return new Dictionary<Guid, ServiceAccountSummary>();
        }

        var found = await Summaries(Open().Where(account => wanted.Contains(account.ServiceLocationId)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Keyed by PREMISE, not by account: the caller is holding a meter's premise and asking who
        // is being served there. At most one row per premise arrives —
        // ux_service_accounts_open_location is what guarantees it — so a plain dictionary is safe
        // and a second one there would be a database fault worth failing on.
        return found.ToDictionary(row => row.Account.ServiceLocationId, Summarise);
    }

    /// <summary>Every account, untracked. A caller outside this module has no business holding one.</summary>
    private IQueryable<ServiceAccount> Accounts() => database.ServiceAccounts.AsNoTracking();

    /// <summary>
    /// The accounts still holding their premise. Expressed as "not Closed" rather than as a list of
    /// the three open statuses, so a status added later joins the set without this line being
    /// remembered — and it is the same predicate <c>ux_service_accounts_open_location</c> filters on,
    /// which is what makes at most one row per premise a database fact.
    /// </summary>
    private IQueryable<ServiceAccount> Open() =>
        Accounts().Where(account => account.Status != ServiceAccountStatus.Closed);

    /// <summary>
    /// Joins <paramref name="accounts"/> to the customers that hold them. A join rather than a
    /// navigation, because WP-1.2 deliberately gave the account a plain <c>CustomerId</c> column
    /// with a foreign key and no navigation property — the database refuses an orphan, and nothing
    /// invites a list query to walk into the customer registry by accident.
    /// </summary>
    /// <remarks>
    /// Takes the filtered accounts rather than filtering afterwards: EF cannot translate a
    /// <c>Where</c> applied to a projection into a record, so a query that projected first would
    /// build fine and throw at run time.
    /// </remarks>
    private IQueryable<AccountRow> Summaries(IQueryable<ServiceAccount> accounts) =>
        from account in accounts
        join customer in database.Customers.AsNoTracking() on account.CustomerId equals customer.Id
        orderby account.Id descending
        select new AccountRow(account, customer);

    private static ServiceAccountSummary Summarise(AccountRow row) =>
        new(
            row.Account.Id,
            row.Account.AccountNumber,
            row.Account.CustomerId,
            row.Customer.Name,

            row.Account.ServiceLocationId,

            // By name, never the enum: Contracts takes no dependency on this module's types.
            row.Account.Status.ToString(),
            row.Account.HoldsPremise,
            row.Account.ServiceStartedAt);

    /// <summary>One account and the customer it belongs to, as the join hands them over.</summary>
    private sealed record AccountRow(ServiceAccount Account, Customer Customer);
}

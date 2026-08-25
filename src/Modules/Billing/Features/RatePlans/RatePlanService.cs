using GridCore.Contracts.Directories;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Data;
using GridCore.Platform.Registry;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>
/// The tariff an account is billed on, and where it came from.
/// </summary>
/// <param name="ServiceAccountId">The account asked about.</param>
/// <param name="RatePlanCode">The code it bills on.</param>
/// <param name="IsDefault">
/// Whether that is the fallback rather than a tariff somebody chose. The distinction matters on a
/// screen: "on the residential tariff because nobody said otherwise" and "on the residential tariff
/// because a billing officer put them there" look the same on a bill and are different facts.
/// </param>
/// <param name="AssignedAt">When it was chosen, if it was.</param>
/// <param name="ChangedAt">When it was last changed, if it ever was.</param>
public sealed record AccountTariff(
    Guid ServiceAccountId,
    string RatePlanCode,
    bool IsDefault,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? ChangedAt);

/// <summary>The published tariffs and who is billed on them.</summary>
public interface IRatePlanService
{
    /// <summary>
    /// Every published tariff version, oldest first, with its tiers loaded.
    /// </summary>
    /// <param name="code">Only versions of this tariff, if given.</param>
    Task<IReadOnlyList<RatePlan>> ListAsync(string? code = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The version of <paramref name="code"/> in force on <paramref name="on"/>, tiers loaded.
    /// </summary>
    /// <exception cref="RatePlanNotFoundException">No such tariff, or none in force by then.</exception>
    Task<RatePlan> InForceAsync(string code, DateOnly on, CancellationToken cancellationToken = default);

    /// <summary>What <paramref name="serviceAccountId"/> is billed on, fallback included.</summary>
    Task<AccountTariff> ForAccountAsync(Guid serviceAccountId, CancellationToken cancellationToken = default);

    /// <summary>Puts an account on a tariff, or moves it to another one.</summary>
    /// <exception cref="ServiceAccountNotFoundException">There is no such service account.</exception>
    /// <exception cref="RatePlanNotFoundException">The utility publishes no tariff with that code.</exception>
    Task<AccountTariff> AssignAsync(
        Guid serviceAccountId,
        string ratePlanCode,
        CancellationToken cancellationToken = default);
}

/// <summary>The tariff catalogue over the billing schema.</summary>
/// <remarks>
/// <para>
/// The tariffs themselves are reference data and are never written here — adding or repricing one is
/// a migration (invariant 7), so this service reads them and nothing more. What it <i>does</i> write
/// is the assignment: which account bills on which tariff, which is Billing's own row about somebody
/// else's account (see <see cref="AccountRatePlan"/>).
/// </para>
/// <para>
/// Effective dating is not decided here either. <see cref="RatePlanSelector"/> is pure and holds the
/// whole rule; this service's job is to load the versions and hand them over, which is what lets
/// every effective-dating case be proven with no database.
/// </para>
/// </remarks>
public sealed class RatePlanService(
    BillingDbContext database,
    IServiceAccountDirectory accounts,
    IUnitOfWork unitOfWork,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : IRatePlanService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RatePlan>> ListAsync(string? code = null, CancellationToken cancellationToken = default)
    {
        var plans = database.RatePlans.AsNoTracking().Include(plan => plan.Tiers).AsQueryable();

        if (!string.IsNullOrWhiteSpace(code))
        {
            var wanted = code.Trim();

            plans = plans.Where(plan => plan.Code == wanted);
        }

        // Oldest first: a tariff's versions read as a history, and a screen showing "then this,
        // then this" wants them in the order they were published.
        return await plans
            .OrderBy(plan => plan.Code)
            .ThenBy(plan => plan.EffectiveFrom)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RatePlan> InForceAsync(string code, DateOnly on, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var versions = await ListAsync(code, cancellationToken).ConfigureAwait(false);

        if (versions.Count is 0)
        {
            throw new RatePlanNotFoundException(
                $"'{code}' is not a rate plan GridCore publishes. Plans are reference data; adding one is a migration.");
        }

        return RatePlanSelector.InForceOn(versions, on)
            ?? throw new RatePlanNotFoundException(
                $"Rate plan '{code}' was not in force on {on:yyyy-MM-dd}; its earliest version takes effect on "
                + $"{versions.Min(plan => plan.EffectiveFrom):yyyy-MM-dd}.");
    }

    /// <inheritdoc />
    public async Task<AccountTariff> ForAccountAsync(Guid serviceAccountId, CancellationToken cancellationToken = default)
    {
        var assignment = await database.AccountRatePlans
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.ServiceAccountId == serviceAccountId, cancellationToken)
            .ConfigureAwait(false);

        return Describe(serviceAccountId, assignment);
    }

    /// <inheritdoc />
    public Task<AccountTariff> AssignAsync(
        Guid serviceAccountId,
        string ratePlanCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ratePlanCode);

        return unitOfWork.ExecuteAsync(
            async ct =>
            {
                var now = clock.GetUtcNow();
                var code = ratePlanCode.Trim();

                // Both ends are checked before anything is written. The account is another module's
                // row, reached through the directory rather than a query — this module may not read
                // the customers schema.
                var account = await accounts.FindAsync(serviceAccountId, ct).ConfigureAwait(false)
                    ?? throw new ServiceAccountNotFoundException(serviceAccountId);

                if (!await database.RatePlans.AnyAsync(plan => plan.Code == code, ct).ConfigureAwait(false))
                {
                    throw new RatePlanNotFoundException(
                        $"'{code}' is not a rate plan GridCore publishes. Plans are reference data; adding one is a migration.");
                }

                var actor = RegistryActor.Of(currentUser);

                var existing = await database.AccountRatePlans
                    .FirstOrDefaultAsync(row => row.ServiceAccountId == serviceAccountId, ct)
                    .ConfigureAwait(false);

                var before = existing is null ? null : Snapshot.Of(existing, account.AccountNumber);

                if (existing is null)
                {
                    existing = AccountRatePlan.Assign(serviceAccountId, code, actor, now);

                    database.AccountRatePlans.Add(existing);
                }
                else if (!existing.ChangeTo(code, actor, now))
                {
                    // Already on that tariff. Nothing was written, so nothing is audited: an audit
                    // trail of writes that did not happen is a trail nobody reads. The caller still
                    // gets the tariff back, so the request reads as successful — it asked for a
                    // state that already holds.
                    return Describe(serviceAccountId, existing);
                }

                // Invariant 5: changing what a customer is charged is permission-gated at the edge
                // and audited here, with the tariff it moved from and the one it moved to.
                audit.Record(
                    AuditActions.AccountRatePlanAssigned,
                    AuditEntityTypes.AccountRatePlan,
                    serviceAccountId.ToString(),
                    before,
                    Snapshot.Of(existing, account.AccountNumber));

                return Describe(serviceAccountId, existing);
            },
            cancellationToken);
    }

    private static AccountTariff Describe(Guid serviceAccountId, AccountRatePlan? assignment) =>
        assignment is null

            // No row is an answer, not an omission: an account nobody has assigned bills on the
            // default tariff, which is what lets a migrated database bill with no setup at all.
            ? new AccountTariff(serviceAccountId, DefaultRatePlans.DefaultCode, IsDefault: true, null, null)
            : new AccountTariff(
                serviceAccountId,
                assignment.RatePlanCode,
                IsDefault: false,
                assignment.AssignedAt,
                assignment.ChangedAt);

    /// <summary>
    /// The shape a tariff assignment is audited as. A dedicated record rather than the entity, so
    /// changing the entity later cannot silently change the meaning of historic entries.
    /// </summary>
    /// <param name="ServiceAccountId">Which account.</param>
    /// <param name="AccountNumber">Its number, so the entry is readable without a second lookup.</param>
    /// <param name="RatePlanCode">The tariff it bills on.</param>
    private sealed record Snapshot(Guid ServiceAccountId, string AccountNumber, string RatePlanCode)
    {
        public static Snapshot Of(AccountRatePlan assignment, string accountNumber) =>
            new(assignment.ServiceAccountId, accountNumber, assignment.RatePlanCode);
    }
}

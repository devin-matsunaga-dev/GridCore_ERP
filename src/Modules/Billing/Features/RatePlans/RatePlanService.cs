using GridCore.Contracts.Directories;
using GridCore.Contracts.Services;
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
    /// <remarks>
    /// <b>The fallback is per service since WP-2.17.</b> An account nobody has assigned is billed on
    /// the default tariff <i>for the supply it takes</i>, and the shipped schedule publishes one only
    /// for electricity — so an account on any other service, and every unmetered account, has no
    /// tariff to fall back to and this says so rather than inventing one.
    /// </remarks>
    /// <exception cref="ServiceAccountNotFoundException">There is no such service account.</exception>
    /// <exception cref="RatePlanNotFoundException">
    /// The utility publishes no default tariff for the service that account takes — see
    /// <see cref="RatePlanService.UnmeteredBillingStub"/>.
    /// </exception>
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

    /// <summary>
    /// What GridCore says when asked to bill a service it cannot price yet — the seam WP-2.17 leaves
    /// for the billing-deepening pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A refusal that names the package, rather than a silent fallback.</b> Wastewater is a flat
    /// charge with no meter and no reading behind it, and the rate engine only knows how to turn
    /// consumption into money — so there is nothing here that could bill one correctly. Falling back
    /// to the electric default would produce a bill: a wrong one, on a tariff for a supply the
    /// customer does not take, with a service charge nobody published for them.
    /// </para>
    /// <para>
    /// The same message covers water and gas, which are metered but have no tariff shipped. Both
    /// cases are "this deployment publishes no default tariff for that supply", and the fix for both
    /// is a migration that ships one — plus, for wastewater, a flat-charge shape the rate engine does
    /// not have.
    /// </para>
    /// </remarks>
    public static string UnmeteredBillingStub(ServiceType serviceType) =>
        $"GridCore publishes no default tariff for {serviceType}, so an account taking it cannot be billed. "
        + (ServiceTypes.IsMetered(serviceType)
            ? "Ship a default tariff for that service in a migration."
            : "An unmetered service is billed a flat charge, which the rate engine does not yet raise — "
              + "the billing-deepening pass owns that shape.");

    /// <inheritdoc />
    public async Task<AccountTariff> ForAccountAsync(Guid serviceAccountId, CancellationToken cancellationToken = default)
    {
        var assignment = await database.AccountRatePlans
            .AsNoTracking()
            .FirstOrDefaultAsync(row => row.ServiceAccountId == serviceAccountId, cancellationToken)
            .ConfigureAwait(false);

        if (assignment is not null)
        {
            // Somebody chose this tariff for this account. The service it belongs to is not consulted
            // and deliberately so: a billing officer who puts an account on a named plan has made a
            // decision, and second-guessing it here would override a person with a lookup table.
            return Describe(serviceAccountId, assignment);
        }

        // Only the fallback needs the account, so only the fallback pays for the boundary call.
        var account = await accounts.FindAsync(serviceAccountId, cancellationToken).ConfigureAwait(false)
            ?? throw new ServiceAccountNotFoundException(serviceAccountId);

        var code = DefaultRatePlans.DefaultCodeFor(account.ServiceType)
            ?? throw new RatePlanNotFoundException(UnmeteredBillingStub(account.ServiceType));

        return new AccountTariff(serviceAccountId, code, IsDefault: true, null, null);
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

    /// <summary>
    /// Describes an assignment, or the electricity default where there is none.
    /// </summary>
    /// <remarks>
    /// The unqualified default is right for every caller left here: <see cref="AssignAsync"/> only
    /// reaches it holding a row it has just written or found. <see cref="ForAccountAsync"/> is the
    /// one that resolves the fallback per service, and it does so without going through this.
    /// </remarks>
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

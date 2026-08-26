using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Seeding;

/// <summary>
/// Writes the transition register the demo world's accounts would actually have: the move-out
/// behind the one account <see cref="ServiceAccountsDemoSeeder"/> closes.
/// </summary>
/// <remarks>
/// <para>
/// Without this the transitions tab is empty on a freshly seeded database, which makes a feature
/// that exists look like a feature that does not — the call WP-2.12 made about seeding deposit
/// <i>entries</i> rather than balances, and WP-2.13 made about the note log.
/// </para>
/// <para>
/// <b>ONE row, and that is the honest number.</b> The demo world's Suspended customer and its
/// Commercial customers were <i>registered</i> that way rather than moved there, so a register row
/// claiming "Active → Suspended" or "Residential → Commercial" would be inventing a change nobody
/// made — the opposite of what a register is for. The one thing the seeded world genuinely did was
/// close an account when a tenant moved out, so that is the one thing recorded. A demo that wants a
/// fuller register gets it by working the screen, which is what a demo is.
/// </para>
/// <para>
/// A seeder of its own rather than more rows in <see cref="ServiceAccountsDemoSeeder"/>: a seeder's
/// <see cref="Name"/> is its dedupe key, so extending one that has already run on a developer's
/// database would seed nothing. Running after it also lets this one query the accounts it
/// committed — inside one transaction those rows are not yet visible to a query.
/// </para>
/// </remarks>
public sealed class AccountTransitionsDemoSeeder(CustomersDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>The account the demo world closes, and the one transition there is to record.</summary>
    private const string ClosedAccountNumber = "A-000007";

    /// <summary>Who the seeded transitions are attributed to — the same stand-in colleague the accounts carry.</summary>
    private static RegistryActor Attribution { get; } = RegistryActor.Of(ServiceAccountsDemoSeeder.Agent);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second set of transitions.</remarks>
    public string Name => "customers.account-transitions";

    /// <inheritdoc />
    /// <remarks>After <see cref="ServiceAccountsDemoSeeder"/> (300), whose accounts these describe.</remarks>
    public int Order => 350;

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var account = await database.ServiceAccounts
            .FirstOrDefaultAsync(candidate => candidate.AccountNumber == ClosedAccountNumber, cancellationToken)
            .ConfigureAwait(false);

        // A demo world that quietly skips its one transition because a pairing was edited is worse
        // than one that refuses to start and says which row is missing.
        if (account is null)
        {
            throw new InvalidOperationException(
                $"Demo service account '{ClosedAccountNumber}' was not seeded; "
                + $"{nameof(ServiceAccountsDemoSeeder)} and this seeder have drifted apart.");
        }

        if (account.Status is not ServiceAccountStatus.Closed)
        {
            throw new InvalidOperationException(
                $"Demo service account '{ClosedAccountNumber}' is {account.Status}, not closed; "
                + "a move-out recorded against an open account would be a register entry that is not true.");
        }

        var customer = await database.Customers
            .FirstOrDefaultAsync(candidate => candidate.Id == account.CustomerId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Demo service account '{ClosedAccountNumber}' names a customer that was not seeded.");

        var now = clock.GetUtcNow();

        database.AccountTransitions.Add(AccountTransition.MovedOut(
            customer,
            account,
            TransitionReasonCode.EndOfTenancy,
            "Tenant moved out; final reading taken at the meter.",

            // Dated a week back, so the demo world opens with an effective date that is visibly not
            // the day it was recorded — which is the distinction the tab exists to make readable.
            DateOnly.FromDateTime(now.UtcDateTime).AddDays(-7),
            Attribution,
            now));

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
    }
}

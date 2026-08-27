using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Delinquency;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>
/// The fourth disconnection test, answered at last (WP-2.20).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what replaces <c>NoPaymentArrangements</c>.</b> WP-2.19 declared
/// <see cref="IPaymentArrangementDirectory"/> and registered it against an implementation that
/// answered "there are none", because building half an arrangements feature there would have been
/// building this package badly. <c>CustomersModuleTests</c> pins the composition, so the swap is a
/// deliberate act — which is exactly what that test was written to force.
/// </para>
/// <para>
/// <b>It answers from the COMPUTED standing, not from the stored status.</b> An account whose
/// instalment fell due yesterday and was not paid stops being protected today, whether or not the
/// review run has come round to write the break down. A directory that read the column would have an
/// account defaulting on a Friday protected from disconnection all weekend because a job had not
/// run; and because the run persists exactly what <see cref="PaymentArrangement.StandingOn"/>
/// answers, the two can never disagree.
/// </para>
/// <para>
/// <b>Still inside Customers rather than in <c>Contracts</c>.</b> "What Customer Service does instead
/// of disconnecting" turned out to be this module's own business after all, so the seam stayed where
/// WP-2.19 put it. Nothing outside Customers reads an arrangement.
/// </para>
/// </remarks>
public sealed class PaymentArrangementDirectory(CustomersDbContext database, TimeProvider clock)
    : IPaymentArrangementDirectory
{
    /// <inheritdoc />
    public async Task<PaymentArrangementStanding?> StandingForAccountAsync(
        Guid serviceAccountId,
        CancellationToken cancellationToken = default)
    {
        var asOf = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // The most recent one that is not already closed. A kept arrangement is history and a broken
        // one is the case disconnection exists for, so neither is worth loading a schedule for —
        // what the test needs to know is whether a promise is standing NOW.
        var arrangement = await database.PaymentArrangements
            .AsNoTracking()
            .Include(candidate => candidate.Instalments)
            .Where(candidate =>
                candidate.ServiceAccountId == serviceAccountId
                && (candidate.Status == PaymentArrangementStatus.Proposed
                    || candidate.Status == PaymentArrangementStatus.Active))
            .OrderByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (arrangement is null)
        {
            // NULL, not "a standing that suppresses nothing" — the distinction WP-2.19's null
            // implementation drew and this one keeps. Null is "this account has no arrangement", and
            // it is what the screen renders as such rather than as a test the customer failed.
            return null;
        }

        return new PaymentArrangementStanding(
            serviceAccountId,
            arrangement.StandingOn(asOf).ToString(),
            arrangement.SuppressesDisconnectionOn(asOf));
    }
}

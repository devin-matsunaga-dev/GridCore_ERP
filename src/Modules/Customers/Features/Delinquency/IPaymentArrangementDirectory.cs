namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>
/// Where an account's payment arrangement stands, as the disconnection test needs to see it.
/// </summary>
/// <param name="ServiceAccountId">The account it is against.</param>
/// <param name="Status">Where it stands, by name — <c>Active</c>, <c>Kept</c>, <c>Broken</c>.</param>
/// <param name="SuppressesDisconnection">
/// Whether it stops the supply being cut off. The one thing this test needs to know, decided by
/// whoever owns arrangements rather than inferred here from a status string.
/// </param>
public sealed record PaymentArrangementStanding(Guid ServiceAccountId, string Status, bool SuppressesDisconnection);

/// <summary>
/// Whether an account is protected by a payment arrangement — the fourth of the four tests
/// <see cref="DisconnectionRules"/> applies.
/// </summary>
/// <remarks>
/// <para>
/// <b>A seam WP-2.19 declares and WP-2.20 fills in.</b> Payment arrangements are the next work
/// package; disconnection eligibility is this one, and WORK_PACKAGES.md makes "no kept payment
/// arrangement" part of the test. Writing the test around a hole would mean rewriting it next
/// package; writing half an arrangements feature here would be building WP-2.20 badly. So the
/// question is asked through an interface, and today's answer is always "none".
/// </para>
/// <para>
/// <b>Inside Customers rather than in <c>Contracts</c>, deliberately.</b> A seam in Contracts is a
/// promise that some <i>other</i> module implements it, and nothing does — an interface there with
/// a stub registered here would misrepresent the module map. "What Customer Service does instead of
/// disconnecting" is this module's own business, so the seam is a module-internal one and moving it
/// to Contracts, if WP-2.20 ever puts arrangements elsewhere, is a namespace change.
/// </para>
/// </remarks>
public interface IPaymentArrangementDirectory
{
    /// <summary>
    /// The arrangement standing against <paramref name="serviceAccountId"/>, or
    /// <see langword="null"/> where there is none.
    /// </summary>
    Task<PaymentArrangementStanding?> StandingForAccountAsync(
        Guid serviceAccountId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The answer until WP-2.20 builds arrangements: there are none.
/// </summary>
/// <remarks>
/// <b>It answers <see langword="null"/> rather than "an arrangement that does not suppress".</b> The
/// two read the same in the test and differently to a person: null is "this utility has no
/// arrangements", and a standing that suppresses nothing is "this customer has one and it does not
/// help them". Saying the first is the honest description of GridCore today, and it is what the
/// screen renders as "payment arrangements are not recorded yet" rather than as a test the customer
/// failed.
/// </remarks>
public sealed class NoPaymentArrangements : IPaymentArrangementDirectory
{
    /// <inheritdoc />
    public Task<PaymentArrangementStanding?> StandingForAccountAsync(
        Guid serviceAccountId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<PaymentArrangementStanding?>(null);
}

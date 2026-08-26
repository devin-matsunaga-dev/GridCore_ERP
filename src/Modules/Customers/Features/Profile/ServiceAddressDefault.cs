using GridCore.Modules.Customers.Features.ServiceAccounts;

namespace GridCore.Modules.Customers.Features.Profile;

/// <summary>
/// Which of a customer's service accounts the mailing address falls back to.
/// </summary>
/// <remarks>
/// <para>
/// Pure and static so the rule can be argued with in milliseconds. A customer may hold several
/// accounts — a house, a shop, a meter at a relative's premise — and "the service address" is not a
/// fact until somebody picks one, so this is where the picking is written down rather than being
/// implied by whatever order a query happened to return.
/// </para>
/// <para>
/// <b>The order is: still holds its premise, then most recently live, then newest.</b> An account
/// that has been closed is a place the customer has left, so it loses to any account that has not;
/// among the rest, the one whose supply was most recently switched on is where they are now.
/// <c>OpenedAt</c> stands in for an account that has been opened but never energised — asking for
/// service is enough of a claim on a premise to post a bill there. The final tie-break is the id,
/// which is Guid v7, so the order is <b>total</b>: two accounts opened in the same millisecond
/// cannot make the answer depend on the query plan.
/// </para>
/// </remarks>
public static class ServiceAddressDefault
{
    /// <summary>
    /// The account whose premise post goes to, or <see langword="null"/> when the customer holds no
    /// accounts at all — a prospect registered this morning, whose only address is one they typed.
    /// </summary>
    public static ServiceAccount? MostRecentlyActive(IEnumerable<ServiceAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        return accounts
            .OrderByDescending(account => account.HoldsPremise)
            .ThenByDescending(account => account.ServiceStartedAt ?? account.OpenedAt)
            .ThenByDescending(account => account.Id)
            .FirstOrDefault();
    }
}

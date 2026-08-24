namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>
/// Where a service account stands. This is the <i>account's</i> status — whether this premise is
/// connected for this customer — not the customer record's: a customer may be Active while one of
/// their accounts is Disconnected, and closing an account does not close the customer.
/// </summary>
public enum ServiceAccountStatus
{
    /// <summary>Opened but not yet energised — awaiting a connection visit or an inspection. Where every account starts.</summary>
    Pending = 1,

    /// <summary>Connected and consuming. Meters read against it and bills are raised from it.</summary>
    Active = 2,

    /// <summary>Supply cut, account still open. Reconnectable without a new registration, and any balance still stands.</summary>
    Disconnected = 3,

    /// <summary>
    /// Finished. Terminal: the premise is released for another account, and a returning customer
    /// gets a new account rather than this one back — so the closed record keeps meaning what it
    /// said, the same reason a ledger correction is a new entry.
    /// </summary>
    Closed = 4,
}

/// <summary>
/// The service account state machine, in one place. Kept out of <see cref="ServiceAccount"/> so a
/// UI can ask what is legal without holding an entity, matching <c>CustomerTransitions</c>.
/// </summary>
public static class ServiceAccountTransitions
{
    private static readonly Dictionary<ServiceAccountStatus, ServiceAccountStatus[]> Allowed = new()
    {
        // No Pending -> Disconnected: nothing was ever connected, so an account that never starts is
        // closed rather than disconnected — otherwise "disconnected" stops meaning supply was cut.
        [ServiceAccountStatus.Pending] = [ServiceAccountStatus.Active, ServiceAccountStatus.Closed],
        [ServiceAccountStatus.Active] = [ServiceAccountStatus.Disconnected, ServiceAccountStatus.Closed],

        // Reconnection is the ordinary path back: a customer who settles their balance gets their
        // supply restored on the same account, keeping one history for the premise.
        [ServiceAccountStatus.Disconnected] = [ServiceAccountStatus.Active, ServiceAccountStatus.Closed],
        [ServiceAccountStatus.Closed] = [],
    };

    /// <summary>The statuses an account in <paramref name="status"/> may move to.</summary>
    public static IReadOnlyList<ServiceAccountStatus> AllowedFrom(ServiceAccountStatus status) =>
        Allowed.TryGetValue(status, out var next) ? next : [];

    /// <summary>Whether <paramref name="from"/> → <paramref name="to"/> is a legal move.</summary>
    public static bool IsAllowed(ServiceAccountStatus from, ServiceAccountStatus to) =>
        AllowedFrom(from).Contains(to);

    /// <summary>
    /// Whether an account in <paramref name="status"/> still holds its premise. A closed account
    /// does not, which is what lets the next occupant of the premise be given an account.
    /// </summary>
    public static bool HoldsPremise(ServiceAccountStatus status) => status is not ServiceAccountStatus.Closed;
}

using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.ServiceAccounts;

/// <summary>
/// The join WP-1.1 deliberately left out: a customer taking service at a premise, with its own
/// lifecycle and its own history. Neither registry owns the other — a premise outlives the
/// customers served at it and a customer may hold several accounts — so this is the thing every
/// later module actually hangs off: a meter is fitted against an account, a bill is raised against
/// one, and a work order is sent to the premise it names.
/// </summary>
public sealed class ServiceAccount
{
    /// <summary>Longest stored form of a status name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest reason recorded against a transition.</summary>
    public const int ReasonLength = ServiceAccountHistoryEntry.ReasonLength;

    private readonly List<ServiceAccountHistoryEntry> _history = [];

    private ServiceAccount()
    {
        // EF materialisation.
        AccountNumber = string.Empty;
    }

    /// <summary>Identifier of this account. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The number quoted for this supply, e.g. <c>A-000001</c>. Unique across accounts.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>Who is being served. Fixed at opening — serving somebody else is a new account.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Where they are being served. Also fixed: an account is the pairing, not a customer with a movable address.</summary>
    public Guid ServiceLocationId { get; private init; }

    /// <summary>
    /// Which utility service this account takes (WP-2.17). Fixed at opening, exactly as the customer
    /// and the premise are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The account is the customer, the premise AND the service.</b> Before this, a service
    /// account was a customer↔location link with no notion of what was being supplied, so a premise
    /// taking electricity and water had to be two customers or one account that meant both. Now one
    /// premise holds one account per service — up to three — and
    /// <c>ux_service_accounts_open_location</c> is keyed on the pair.
    /// </para>
    /// <para>
    /// <b>Not settable, for the reason the other two are not.</b> Changing what a customer is
    /// supplied with is not an edit to a row: it is closing one account and opening another, which
    /// is what leaves the bills raised under the old supply attached to the account that was
    /// actually billed. WP-2.15's transition register is where that pair of acts belongs.
    /// </para>
    /// </remarks>
    public ServiceType ServiceType { get; private init; }

    /// <summary>
    /// Whether a device at the premise measures what this account consumes — <see cref="ServiceType"/>
    /// read through <see cref="ServiceTypes.IsMetered"/>.
    /// </summary>
    /// <remarks>
    /// Derived rather than stored: it is a fact about the service and not about the account, and a
    /// column would be a second place for it to be wrong. An unmetered account has no meter, takes
    /// no reading, and is billed a flat charge — see the type's remarks for what refuses what.
    /// </remarks>
    public bool IsMetered => ServiceTypes.IsMetered(ServiceType);

    /// <summary>Where the account stands.</summary>
    public ServiceAccountStatus Status { get; private set; }

    /// <summary>When the account was opened.</summary>
    public DateTimeOffset OpenedAt { get; private init; }

    /// <summary>
    /// When supply was most recently energised, or <see langword="null"/> if it never has been. A
    /// reconnection moves it: the full sequence of starts and stops lives in <see cref="History"/>,
    /// so this column can answer "since when has this been live" without reading it.
    /// </summary>
    public DateTimeOffset? ServiceStartedAt { get; private set; }

    /// <summary>When supply was most recently cut, or <see langword="null"/> while it is on.</summary>
    public DateTimeOffset? ServiceEndedAt { get; private set; }

    /// <summary>When the status last moved.</summary>
    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>Why it last moved.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>Every transition this account has been through, oldest first.</summary>
    public IReadOnlyList<ServiceAccountHistoryEntry> History => _history;

    /// <summary>The statuses this account may move to, for rendering transition buttons.</summary>
    public IReadOnlyList<ServiceAccountStatus> AllowedTransitions => ServiceAccountTransitions.AllowedFrom(Status);

    /// <summary>
    /// Whether this account still holds its premise <i>for its own service</i>, so no other account
    /// may take that supply there. Since WP-2.17 a premise can be held three times over — once per
    /// service — and each holding is independent of the others.
    /// </summary>
    public bool HoldsPremise => ServiceAccountTransitions.HoldsPremise(Status);

    /// <summary>
    /// Opens an account under a number the caller has already reserved — see
    /// <see cref="IRegistryNumberGenerator"/>. It starts <see cref="ServiceAccountStatus.Pending"/>:
    /// asking for service and getting it are two different days, and the gap is the work order.
    /// </summary>
    /// <exception cref="RegistryValidationException">
    /// The number is missing, either id is empty, or the service is not one GridCore declares.
    /// </exception>
    public static ServiceAccount Open(
        string accountNumber,
        Guid customerId,
        Guid serviceLocationId,
        ServiceType serviceType,
        RegistryActor actor,
        DateTimeOffset now,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (string.IsNullOrWhiteSpace(accountNumber))
        {
            throw new RegistryValidationException("'accountNumber' is required to open a service account.");
        }

        RequireId(customerId, nameof(customerId));
        RequireId(serviceLocationId, nameof(serviceLocationId));

        // Checked here rather than only at the edge: the service is what the deposit schedule, the
        // tariff and the meter guard all key on, so an account carrying a value nobody declared
        // would be an account none of the three can answer for. A 400, because the caller sent it.
        if (!ServiceTypes.IsDeclared(serviceType))
        {
            throw new RegistryValidationException($"'{serviceType}' is not a service GridCore declares.");
        }

        var account = new ServiceAccount
        {
            Id = Guid.CreateVersion7(now),
            AccountNumber = accountNumber.Trim(),
            CustomerId = customerId,
            ServiceLocationId = serviceLocationId,
            ServiceType = serviceType,
            Status = ServiceAccountStatus.Pending,
            OpenedAt = now,
            StatusChangedAt = now,
            StatusReason = RegistryText.Clean(reason, ReasonLength),
        };

        // The opening line, so the history is complete from the first day rather than starting at
        // the first transition and leaving "where did this account come from" unanswerable.
        account._history.Add(ServiceAccountHistoryEntry.For(account.Id, from: null, ServiceAccountStatus.Pending, reason, actor, now));

        return account;
    }

    /// <summary>Energises supply — the first connection, or a reconnection after a disconnection.</summary>
    /// <exception cref="RegistryWorkflowException">The account is already Active, or is Closed.</exception>
    public void StartService(RegistryActor actor, DateTimeOffset now, string? reason = null) =>
        Transition(ServiceAccountStatus.Active, actor, now, reason);

    /// <summary>Cuts supply, leaving the account open so it can be reconnected.</summary>
    /// <exception cref="RegistryWorkflowException">The account is not Active.</exception>
    public void StopService(RegistryActor actor, DateTimeOffset now, string? reason = null) =>
        Transition(ServiceAccountStatus.Disconnected, actor, now, reason);

    /// <summary>Closes the account for good, releasing the premise for another one.</summary>
    /// <exception cref="RegistryWorkflowException">The account is already Closed.</exception>
    public void Close(RegistryActor actor, DateTimeOffset now, string? reason = null) =>
        Transition(ServiceAccountStatus.Closed, actor, now, reason);

    private void Transition(ServiceAccountStatus to, RegistryActor actor, DateTimeOffset now, string? reason)
    {
        ArgumentNullException.ThrowIfNull(actor);

        if (!ServiceAccountTransitions.IsAllowed(Status, to))
        {
            // A 409, never a 400: whether this move is legal depends on where the account is now,
            // which edge validation cannot see.
            throw new RegistryWorkflowException(
                Status == to
                    ? $"Service account {AccountNumber} is already {Status}."
                    : $"Service account {AccountNumber} cannot go from {Status} to {to}.");
        }

        var from = Status;

        Status = to;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(reason, ReasonLength);

        if (to is ServiceAccountStatus.Active)
        {
            ServiceStartedAt = now;
            ServiceEndedAt = null;
        }
        else if (from is ServiceAccountStatus.Active)
        {
            // Only from Active: an account closed while it was still Pending never carried supply,
            // and stamping an end date on it would invent a service period that never existed.
            ServiceEndedAt = now;
        }

        _history.Add(ServiceAccountHistoryEntry.For(Id, from, to, reason, actor, now));
    }

    private static void RequireId(Guid id, string field)
    {
        if (id == Guid.Empty)
        {
            throw new RegistryValidationException($"'{field}' is required to open a service account.");
        }
    }
}

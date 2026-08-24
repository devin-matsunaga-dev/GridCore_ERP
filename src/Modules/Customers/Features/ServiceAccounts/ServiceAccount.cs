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

    /// <summary>Whether this account still holds its premise, so no other account may be opened there.</summary>
    public bool HoldsPremise => ServiceAccountTransitions.HoldsPremise(Status);

    /// <summary>
    /// Opens an account under a number the caller has already reserved — see
    /// <see cref="IRegistryNumberGenerator"/>. It starts <see cref="ServiceAccountStatus.Pending"/>:
    /// asking for service and getting it are two different days, and the gap is the work order.
    /// </summary>
    /// <exception cref="RegistryValidationException">The number is missing, or either id is empty.</exception>
    public static ServiceAccount Open(
        string accountNumber,
        Guid customerId,
        Guid serviceLocationId,
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

        var account = new ServiceAccount
        {
            Id = Guid.CreateVersion7(now),
            AccountNumber = accountNumber.Trim(),
            CustomerId = customerId,
            ServiceLocationId = serviceLocationId,
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

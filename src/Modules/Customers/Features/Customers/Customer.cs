using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Customers;

/// <summary>
/// Somebody the utility serves — a household or an organisation — and the account number they
/// quote when they call. Deliberately not a premise: a customer may be served at several service
/// locations, and a location outlives the customers served there, so the two are separate
/// registries joined by WP-1.2's service accounts.
/// </summary>
public sealed class Customer
{
    /// <summary>Longest customer or contact name stored.</summary>
    public const int NameLength = 256;

    /// <summary>Longest email address stored. 320 is the RFC 5321 maximum.</summary>
    public const int EmailLength = 320;

    /// <summary>Longest telephone number stored, with room for an international prefix and extension.</summary>
    public const int PhoneLength = 32;

    /// <summary>Longest stored form of a class or status name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Longest reason recorded against a status change.</summary>
    public const int ReasonLength = 1024;

    private Customer()
    {
        // EF materialisation.
        AccountNumber = string.Empty;
        Name = string.Empty;
    }

    /// <summary>Identifier of this customer. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The number the customer quotes, e.g. <c>C-000001</c>. Unique across customers.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>Who they are — a person's name or an organisation's.</summary>
    public string Name { get; private set; }

    /// <summary>Who to ask for, where the customer is an organisation.</summary>
    public string? ContactName { get; private set; }

    /// <summary>Where to email them.</summary>
    public string? Email { get; private set; }

    /// <summary>Where to call them.</summary>
    public string? Phone { get; private set; }

    /// <summary>Residential or commercial.</summary>
    public CustomerClass Class { get; private set; }

    /// <summary>Where they stand with the utility.</summary>
    public CustomerStatus Status { get; private set; }

    /// <summary>
    /// Security deposit the utility is holding. <see langword="decimal"/>, in whole cents — money
    /// is never a float, and a deposit that quietly lost a fraction would surface months later as
    /// a refund that does not reconcile.
    /// </summary>
    /// <remarks>
    /// <b>A projection of <c>customers.deposit_entries</c>, not a field (WP-2.12).</b> It is here so
    /// the registry list, the search hit and the 360's header can show what is held without loading
    /// a ledger — but nothing outside <see cref="Deposits.DepositEntry"/> may move it, and no
    /// request body can set it. It was an editable amount up to WP-2.11, which is exactly what
    /// WP-2.12 removed: a balance a form could type over is a balance that disagrees with the
    /// general ledger the first time somebody does.
    /// </remarks>
    public decimal DepositHeld { get; private set; }

    /// <summary>When the customer was registered.</summary>
    public DateTimeOffset RegisteredAt { get; private init; }

    /// <summary>When the status last moved.</summary>
    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>Why it last moved.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>The statuses this customer may move to, for rendering transition buttons.</summary>
    public IReadOnlyList<CustomerStatus> AllowedTransitions => CustomerTransitions.AllowedFrom(Status);

    /// <summary>
    /// Registers a customer under an account number the caller has already reserved — see
    /// <see cref="IRegistryNumberGenerator"/>.
    /// </summary>
    /// <remarks>
    /// <b>No deposit.</b> A customer is registered holding nothing, and money is taken by the
    /// deposit lifecycle (WP-2.12) so that every cent held has an entry explaining it. An intake
    /// that collects one does so as a second act inside the same transaction.
    /// </remarks>
    /// <exception cref="RegistryValidationException">A required field is missing.</exception>
    public static Customer Register(
        string accountNumber,
        string name,
        CustomerClass customerClass,
        DateTimeOffset now,
        string? contactName = null,
        string? email = null,
        string? phone = null,
        CustomerStatus status = CustomerStatus.Prospect)
    {
        Require(accountNumber, nameof(accountNumber));
        Require(name, nameof(name));
        RequireDeclared(customerClass);
        RequireDeclared(status);

        return new Customer
        {
            Id = Guid.CreateVersion7(now),
            AccountNumber = RegistryText.Clean(accountNumber, RegistryNumbers.MaxLength)!,
            Name = RegistryText.Clean(name, NameLength)!,
            ContactName = RegistryText.Clean(contactName, NameLength),
            Email = RegistryText.Clean(email, EmailLength),
            Phone = RegistryText.Clean(phone, PhoneLength),
            Class = customerClass,
            Status = status,
            DepositHeld = Money.Zero,
            RegisteredAt = now,
        };
    }

    /// <summary>
    /// Changes the details a customer may correct at any time. The account number is not among
    /// them: it is quoted on bills and referred to by every other module, so it is fixed at
    /// registration.
    /// </summary>
    /// <remarks>
    /// <b>The deposit is not among them either (WP-2.12).</b> It is a balance made of immutable
    /// entries, so it moves by collecting, applying or refunding — never by a correction to a
    /// customer record. See <see cref="RecordDepositMovement"/>.
    /// </remarks>
    /// <exception cref="RegistryValidationException">A required field is missing.</exception>
    public void UpdateDetails(
        string name,
        CustomerClass customerClass,
        string? contactName,
        string? email,
        string? phone)
    {
        Require(name, nameof(name));
        RequireDeclared(customerClass);

        Name = RegistryText.Clean(name, NameLength)!;
        Class = customerClass;
        ContactName = RegistryText.Clean(contactName, NameLength);
        Email = RegistryText.Clean(email, EmailLength);
        Phone = RegistryText.Clean(phone, PhoneLength);
    }

    /// <summary>Moves the customer to <paramref name="status"/>.</summary>
    /// <exception cref="RegistryWorkflowException">The move is not one <see cref="CustomerTransitions"/> allows.</exception>
    public void ChangeStatus(CustomerStatus status, string? reason, DateTimeOffset now)
    {
        RequireDeclared(status);

        if (!CustomerTransitions.IsAllowed(Status, status))
        {
            throw new RegistryWorkflowException(
                Status == status
                    ? $"Customer {AccountNumber} is already {Status}."
                    : $"Customer {AccountNumber} cannot go from {Status} to {status}.");
        }

        Status = status;
        StatusChangedAt = now;
        StatusReason = RegistryText.Clean(reason, ReasonLength);
    }

    /// <summary>
    /// Moves the held deposit by <paramref name="signedAmount"/> — positive for money taken,
    /// negative for money applied or given back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Deposits.DepositEntry"/> is the only caller, and that is the invariant.</b> It
    /// is <see langword="internal"/> so nothing outside this module can reach it at all, and inside
    /// the module every path goes through the entry factories — which is what makes "a balance
    /// change without a ledger row" unreachable rather than merely unwritten. A second writer takes
    /// the invariant with it: the same warning WP-2.11 left on <c>CustomerContact</c> and the
    /// one-primary-per-kind rule, for the same reason, that no database constraint expresses this.
    /// </para>
    /// <para>
    /// <b>The balance may not go below zero</b>, which is where "a refund cannot exceed the held
    /// balance" is actually enforced — one guard covering refunds and bill applications alike,
    /// rather than a rule per kind that a fourth kind could be added without.
    /// </para>
    /// </remarks>
    /// <exception cref="RegistryWorkflowException">The movement would leave the utility holding less than nothing.</exception>
    internal void RecordDepositMovement(decimal signedAmount, Deposits.DepositEntryKind kind)
    {
        var moved = DepositHeld + signedAmount;

        if (moved < Money.Zero)
        {
            throw new RegistryWorkflowException(
                $"Customer {AccountNumber} holds a deposit of {DepositHeld:0.00}; "
                + $"{Math.Abs(signedAmount):0.00} cannot be {kind.ToString().ToLowerInvariant()} against it. "
                + "The utility cannot hand over money it is not holding.");
        }

        DepositHeld = moved;
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RegistryValidationException($"'{field}' is required to register a customer.");
        }
    }

    private static void RequireDeclared<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        // A value cast from an unmapped integer would be stored by name as a number and read back
        // as nothing anyone can act on.
        if (!Enum.IsDefined(value))
        {
            throw new RegistryValidationException($"'{value}' is not a {typeof(TEnum).Name} GridCore declares.");
        }
    }
}

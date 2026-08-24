using GridCore.Modules.Customers.Features.Shared;
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
    /// <exception cref="RegistryValidationException">A required field is missing, or the deposit is negative or finer than a cent.</exception>
    public static Customer Register(
        string accountNumber,
        string name,
        CustomerClass customerClass,
        DateTimeOffset now,
        string? contactName = null,
        string? email = null,
        string? phone = null,
        decimal depositHeld = 0m,
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
            DepositHeld = Deposit(depositHeld),
            RegisteredAt = now,
        };
    }

    /// <summary>
    /// Changes the details a customer may correct at any time. The account number is not among
    /// them: it is quoted on bills and referred to by every other module, so it is fixed at
    /// registration.
    /// </summary>
    /// <exception cref="RegistryValidationException">A required field is missing, or the deposit is negative or finer than a cent.</exception>
    public void UpdateDetails(
        string name,
        CustomerClass customerClass,
        string? contactName,
        string? email,
        string? phone,
        decimal depositHeld)
    {
        Require(name, nameof(name));
        RequireDeclared(customerClass);

        // Every guard runs before the first assignment, so a rejected correction leaves the entity
        // exactly as it was rather than half-applied.
        var deposit = Deposit(depositHeld);

        Name = RegistryText.Clean(name, NameLength)!;
        Class = customerClass;
        ContactName = RegistryText.Clean(contactName, NameLength);
        Email = RegistryText.Clean(email, EmailLength);
        Phone = RegistryText.Clean(phone, PhoneLength);
        DepositHeld = deposit;
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

    private static decimal Deposit(decimal amount)
    {
        if (amount < 0m)
        {
            throw new RegistryValidationException("A deposit held cannot be negative; a refund owed is a Finance entry, not a negative deposit.");
        }

        // Rounding is not this type's to invent — CONVENTIONS.md centralises it, and the helper
        // arrives with the rate engine (WP-2.3). Until then a value finer than a cent is refused
        // rather than silently truncated by the column's scale.
        if (decimal.Round(amount, 2) != amount)
        {
            throw new RegistryValidationException($"A deposit must be a whole number of cents; '{amount}' is not.");
        }

        return amount;
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

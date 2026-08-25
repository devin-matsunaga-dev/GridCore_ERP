using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>
/// What a customer of a given class is asked for as a security deposit — reference data, not a
/// constant in the domain.
/// </summary>
/// <remarks>
/// <para>
/// ARCHITECTURE.md invariant 8 puts reference data in a migration and demo data in a seeder, and a
/// deposit schedule is squarely the first kind: a working application must be able to assess a
/// deposit in every environment, and the figure is a business decision that changes without the
/// code changing. So the amount lives in <c>customers.deposit_rules</c> and the assessment reads
/// the table — <see cref="DepositRules"/> exists only to seed it, exactly as
/// <c>ChartOfAccounts</c> seeds <c>finance.accounts</c>.
/// </para>
/// <para>
/// This is the assessment and nothing more. WP-2.12 owns the deposit's lifecycle — holding it,
/// applying it to a bill, refunding it on close — and the ledger entries behind all three.
/// </para>
/// </remarks>
public sealed class DepositRule
{
    /// <summary>Longest stored form of the class name.</summary>
    public const int ClassNameLength = 32;

    /// <summary>Longest description stored against a rule.</summary>
    public const int DescriptionLength = 256;

    private DepositRule()
    {
        // EF materialisation.
        Description = string.Empty;
    }

    /// <summary>Identifier of this rule. Derived from the class — see <see cref="ReferenceId"/>.</summary>
    public Guid Id { get; private init; }

    /// <summary>The class of customer the rule applies to. Unique across the schedule.</summary>
    public CustomerClass CustomerClass { get; private init; }

    /// <summary>What a customer of that class is asked for, in whole cents.</summary>
    public decimal Amount { get; private init; }

    /// <summary>Why the figure is what it is, for the clerk who has to explain it.</summary>
    public string Description { get; private init; }

    /// <summary>
    /// Builds a reference rule. The id is derived from the class name so the migration seeds the
    /// same rows every time it is generated.
    /// </summary>
    /// <exception cref="ArgumentException">The description is missing or too long.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative or finer than a cent.</exception>
    public static DepositRule Reference(CustomerClass customerClass, decimal amount, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(description.Length, DescriptionLength);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        if (!Money.IsRounded(amount))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A deposit rule must be a whole number of cents.");
        }

        if (!Enum.IsDefined(customerClass))
        {
            throw new ArgumentOutOfRangeException(nameof(customerClass), customerClass, "Not a customer class GridCore declares.");
        }

        return new DepositRule
        {
            Id = ReferenceId.For(DepositRules.AuthoredAt, customerClass.ToString()),
            CustomerClass = customerClass,
            Amount = amount,
            Description = description,
        };
    }
}

/// <summary>
/// The deposit schedule as shipped: one rule per customer class, seeded by
/// <see cref="DepositRuleConfiguration"/> so the list a test asserts against and the list the
/// migration writes are the same list.
/// </summary>
/// <remarks>
/// Changing an amount is a migration, and adding a customer class means adding a rule in the same
/// one — <see cref="RequireComplete"/> is what stops a class shipping without one. Never change a
/// class name: it is the rule's identity and <see cref="ReferenceId"/> derives the key from it.
/// </remarks>
public static class DepositRules
{
    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every rule id.
    /// Fixed forever: changing it changes every id, which to the database is a different schedule.
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Every rule, in class order.</summary>
    public static IReadOnlyList<DepositRule> All { get; } =
    [
        DepositRule.Reference(
            CustomerClass.Residential,
            75.00m,
            "One residential connection: two months of a typical household bill, refundable on close."),
        DepositRule.Reference(
            CustomerClass.Commercial,
            450.00m,
            "One commercial connection: two months of a small-premises bill, refundable on close."),
    ];

    /// <summary>
    /// Fails if a declared customer class has no rule. Called where the schedule is built, so the
    /// gap is found at startup rather than by the first customer of the new class.
    /// </summary>
    /// <exception cref="RegistryValidationException">A class has no rule, or two rules claim one class.</exception>
    public static void RequireComplete(IEnumerable<DepositRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var byClass = rules.ToLookup(rule => rule.CustomerClass);

        foreach (var customerClass in Enum.GetValues<CustomerClass>())
        {
            var count = byClass[customerClass].Count();

            if (count is not 1)
            {
                throw new RegistryValidationException(
                    count is 0
                        ? $"No deposit rule is declared for {customerClass}. A deposit schedule is reference data: add the rule in a migration."
                        : $"{count} deposit rules claim {customerClass}. A class has exactly one rule.");
            }
        }
    }
}

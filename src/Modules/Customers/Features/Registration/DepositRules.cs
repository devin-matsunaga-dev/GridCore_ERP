using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>
/// The deposit schedule as shipped: one rule per (customer class × service), seeded by
/// <see cref="DepositRuleConfiguration"/> so the list a test asserts against and the list the
/// migration writes are the same list.
/// </summary>
/// <remarks>
/// <para>
/// Changing an amount is a migration, and adding a customer class or a service means adding its
/// rules in the same one — <see cref="RequireComplete"/> is what stops either shipping without
/// them. Never change a class or service name: the pair is the rule's identity and
/// <see cref="DepositRule.KeyFor"/> derives the key from it.
/// </para>
/// <para>
/// <b>Every figure here is a demonstration figure and says so in its own description.</b> The call
/// WP-2.16's fee schedule made, for the same reason: CUC's published deposit amounts disagree with
/// each other and change without notice, so a reader must not take a number out of this file and
/// quote it at a customer. What is real is the <i>shape</i> — a published floor, and for a metered
/// service a multiple of measured usage — which is what the reference actually describes.
/// </para>
/// <para>
/// <b>The electricity minimums are WP-2.8's figures, unchanged.</b> $75 residential and $450
/// commercial are what the class-keyed schedule asked before this package, and they carry over to
/// the electric rules untouched: re-keying the schedule must not silently reprice a customer who has
/// already been assessed. What is new is that those rules now have a usage basis above the floor,
/// which no existing customer had been assessed against because there was nothing to assess it with.
/// </para>
/// </remarks>
public static class DepositRules
{
    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every rule id.
    /// Fixed forever: changing it changes every id, which to the database is a different schedule.
    /// </summary>
    /// <remarks>
    /// Unchanged from WP-2.8 even though every id moved in WP-2.17. The ids moved because the
    /// <i>key</i> gained the service, which is what a re-key means; re-dating this as well would
    /// have been a second reason for the same change and left nothing to point at.
    /// </remarks>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 25, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The currency the shipped schedule is in. The demo utility bills in US dollars, as the rate
    /// plans do; a multi-currency utility is not in scope and would start by making this per-rule
    /// data somebody maintains rather than a value this list repeats.
    /// </summary>
    public const string Currency = "USD";

    /// <summary>
    /// Months of average usage a metered deposit is worth. Two, which is what the reference
    /// describes and roughly what a utility is exposed to between a bill going unpaid and supply
    /// being cut.
    /// </summary>
    public const int UsageMonths = 2;

    /// <summary>Every rule, in class then service order.</summary>
    public static IReadOnlyList<DepositRule> All { get; } =
    [
        DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Electricity,
            minimumAmount: 75.00m,
            Currency,
            "Residential electricity: the greater of $75 and two months of average usage at $0.3200/kWh. "
            + "Demonstration figures — CUC's published residential deposit and its energy rate both move, "
            + "and neither is quoted here as authoritative.",
            UsageMonths,
            usageRate: 0.3200m),
        DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Water,
            minimumAmount: 50.00m,
            Currency,
            "Residential water: the greater of $50 and two months of average usage at $2.5000 per cubic metre. "
            + "Demonstration figures — the utility in this MVP distributes electricity only, and the water "
            + "schedule exists so the module can express a service it does not yet supply.",
            UsageMonths,
            usageRate: 2.5000m),
        DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Gas,
            minimumAmount: 50.00m,
            Currency,
            "Residential gas: the greater of $50 and two months of average usage at $1.5000 per therm. "
            + "Demonstration figures — GridCore declares gas as a service type and the demonstration utility "
            + "does not distribute it; the rule exists so the schedule is complete.",
            UsageMonths,
            usageRate: 1.5000m),
        DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Wastewater,
            minimumAmount: 30.00m,
            Currency,
            "Residential wastewater: a flat $30. Unmetered — there is no wastewater meter, so there is "
            + "nothing to average and no usage basis to apply. Demonstration figure."),
        DepositRule.Reference(
            CustomerClass.Commercial,
            ServiceType.Electricity,
            minimumAmount: 450.00m,
            Currency,
            "Commercial electricity: the greater of $450 and two months of average usage at $0.3200/kWh. "
            + "Demonstration figures — CUC's published commercial deposit and its energy rate both move, "
            + "and neither is quoted here as authoritative.",
            UsageMonths,
            usageRate: 0.3200m),
        DepositRule.Reference(
            CustomerClass.Commercial,
            ServiceType.Water,
            minimumAmount: 250.00m,
            Currency,
            "Commercial water: the greater of $250 and two months of average usage at $2.5000 per cubic metre. "
            + "Demonstration figures — see the residential water rule.",
            UsageMonths,
            usageRate: 2.5000m),
        DepositRule.Reference(
            CustomerClass.Commercial,
            ServiceType.Gas,
            minimumAmount: 250.00m,
            Currency,
            "Commercial gas: the greater of $250 and two months of average usage at $1.5000 per therm. "
            + "Demonstration figures — see the residential gas rule.",
            UsageMonths,
            usageRate: 1.5000m),
        DepositRule.Reference(
            CustomerClass.Commercial,
            ServiceType.Wastewater,
            minimumAmount: 150.00m,
            Currency,
            "Commercial wastewater: a flat $150. Unmetered — there is no wastewater meter, so there is "
            + "nothing to average and no usage basis to apply. Demonstration figure."),
    ];

    /// <summary>
    /// Fails if a declared (class × service) pair has no rule, or two rules claim one pair. Called
    /// where the schedule is built, so the gap is found at startup rather than by the first customer
    /// who asks for a service nobody priced.
    /// </summary>
    /// <remarks>
    /// The cross product, not the classes alone. A class added later needs a rule per service and a
    /// service added later needs a rule per class, and the failure has to name which pair is missing
    /// or the message sends somebody reading eight rows to find the one that is not there.
    /// </remarks>
    /// <exception cref="RegistryValidationException">A pair has no rule, or two rules claim one pair.</exception>
    public static void RequireComplete(IEnumerable<DepositRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var byPair = rules.ToLookup(rule => rule.RuleKey, StringComparer.Ordinal);

        foreach (var customerClass in Enum.GetValues<CustomerClass>())
        {
            foreach (var serviceType in ServiceTypes.All)
            {
                var key = DepositRule.KeyFor(customerClass, serviceType);
                var count = byPair[key].Count();

                if (count is not 1)
                {
                    throw new RegistryValidationException(
                        count is 0
                            ? $"No deposit rule is declared for {customerClass} {serviceType}. A deposit schedule is "
                              + "reference data: add the rule in a migration, in the same one that declared the class or service."
                            : $"{count} deposit rules claim {customerClass} {serviceType}. A pair has exactly one rule.");
                }
            }
        }
    }
}

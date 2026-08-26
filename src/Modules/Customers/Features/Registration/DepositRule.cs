using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>
/// What a customer of a given class is asked for as a security deposit on a given service —
/// reference data, not a constant in the domain.
/// </summary>
/// <remarks>
/// <para>
/// ARCHITECTURE.md invariant 8 puts reference data in a migration and demo data in a seeder, and a
/// deposit schedule is squarely the first kind: a working application must be able to assess a
/// deposit in every environment, and the figure is a business decision that changes without the
/// code changing. So the amounts live in <c>customers.deposit_rules</c> and the assessment reads
/// the table — <see cref="DepositRules"/> exists only to seed it, exactly as
/// <c>ChartOfAccounts</c> seeds <c>finance.accounts</c>.
/// </para>
/// <para>
/// <b>Keyed on the class AND the service since WP-2.17.</b> A class alone could not express what the
/// reference actually publishes — three deposits, electric, water and wastewater, each with its own
/// figure — and while a service account had no notion of what service it took, there was nowhere for
/// the second half of the key to come from. Now there is, and the schedule is the cross product:
/// every declared pair has exactly one rule, and <see cref="DepositRules.RequireComplete"/> fails
/// the build's first startup if one is missing.
/// </para>
/// <para>
/// <b>A minimum and, optionally, a usage basis.</b> The reference describes the electric deposit as
/// the greater of a published floor and a multiple of what the premise actually uses, which is two
/// numbers and not one: <see cref="MinimumAmount"/> is the floor and
/// <see cref="UsageMonths"/> × <see cref="UsageRate"/> is what turns measured consumption into
/// money. A rule with no usage basis is a flat deposit, which is what an unmetered service can only
/// ever be — there is nothing to measure.
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

    /// <summary>Longest stored form of the service name.</summary>
    public const int ServiceTypeNameLength = 32;

    /// <summary>Longest description stored against a rule.</summary>
    public const int DescriptionLength = 512;

    /// <summary>Longest ISO 4217 code stored. Three letters, with room to spare.</summary>
    public const int CurrencyLength = 8;

    /// <summary>Decimal places a usage rate carries — finer than a cent, because it prices one unit.</summary>
    /// <remarks>
    /// Four, matching <c>RatePlanTier.RatePerUnit</c>. A per-kWh figure rounded to the cent would be
    /// a rate of 0.19 or 0.20 and nothing between, which is a 5% error in every deposit assessed
    /// from it.
    /// </remarks>
    public const int RateDecimalPlaces = 4;

    /// <summary>Total digits stored for a usage rate.</summary>
    public const int RatePrecision = 18;

    /// <summary>Separates the class from the service in the rule's natural key.</summary>
    private const char KeySeparator = '|';

    private DepositRule()
    {
        // EF materialisation.
        Description = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>Identifier of this rule. Derived from the class and the service — see <see cref="ReferenceId"/>.</summary>
    public Guid Id { get; private init; }

    /// <summary>The class of customer the rule applies to.</summary>
    public CustomerClass CustomerClass { get; private init; }

    /// <summary>The service it applies to. Unique across the schedule together with the class.</summary>
    public ServiceType ServiceType { get; private init; }

    /// <summary>
    /// The least a customer of that class is asked for on that service, in whole cents. Also the
    /// whole assessment where there is no usage basis, and the floor where there is one.
    /// </summary>
    public decimal MinimumAmount { get; private init; }

    /// <summary>
    /// How many months of average usage the deposit is worth, or <see langword="null"/> where the
    /// deposit is a flat figure.
    /// </summary>
    /// <remarks>
    /// Null and <see cref="UsageRate"/> null travel together — a count of months with nothing to
    /// price it at is not half a rule, it is an unusable one, and <see cref="Reference"/> refuses
    /// the pair where only one is given.
    /// </remarks>
    public int? UsageMonths { get; private init; }

    /// <summary>
    /// What one unit of average monthly usage is worth for deposit purposes, or
    /// <see langword="null"/> where the deposit is flat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A published deposit-basis rate, deliberately not the tariff.</b> Metering measures units
    /// and knows nothing about money; the tariff that prices them is Billing's, is tiered, and is
    /// effective-dated — reaching across two module boundaries to re-derive a bill would make a
    /// deposit quote depend on which tier a customer happened to land in that month. The reference
    /// publishes the deposit as a simple multiple, so this is a simple rate, and the row's
    /// description is where it says which.
    /// </para>
    /// <para>
    /// Finer than a cent — see <see cref="RateDecimalPlaces"/> — because it prices a single unit.
    /// </para>
    /// </remarks>
    public decimal? UsageRate { get; private init; }

    /// <summary>
    /// ISO 4217 code the amounts are expressed in.
    /// </summary>
    /// <remarks>
    /// On the reference row rather than in a constant, the call <c>DefaultRatePlans</c> already made
    /// for a tariff: a Finance posting has to name a currency, and one traceable to the row that set
    /// the figure beats a literal three letters deep in an event mapping. WP-2.12's collections and
    /// refunds read it from here; an application to a bill takes the bill's instead, because that is
    /// the currency the receivable is denominated in.
    /// </remarks>
    public string Currency { get; private init; }

    /// <summary>Why the figure is what it is, for the clerk who has to explain it.</summary>
    public string Description { get; private init; }

    /// <summary>Whether this rule prices measured usage at all, or is simply a flat figure.</summary>
    public bool HasUsageBasis => UsageMonths is not null && UsageRate is not null;

    /// <summary>This rule's natural key — its class and its service, e.g. <c>Residential|Electricity</c>.</summary>
    public string RuleKey => KeyFor(CustomerClass, ServiceType);

    /// <summary>The natural key of the rule for <paramref name="customerClass"/> on <paramref name="serviceType"/>.</summary>
    /// <remarks>
    /// <b>The class alone is not a rule's identity any more; a class and a service are.</b> Keying on
    /// the class (which is what WP-2.8 did, when a deposit was one figure) would give the residential
    /// electric rule and the residential water rule the same <see cref="ReferenceId"/> and so the
    /// same primary key.
    /// </remarks>
    public static string KeyFor(CustomerClass customerClass, ServiceType serviceType) =>
        customerClass.ToString() + KeySeparator + serviceType.ToString();

    /// <summary>
    /// What this rule asks of a customer whose premise averages <paramref name="averageMonthlyUsage"/>
    /// a month — the greater of the minimum and the usage basis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The minimum is a floor and never a ceiling.</b> A heavy user is asked for what their usage
    /// says; a light one, or one nobody has measured yet, is asked for the published minimum. That
    /// asymmetry is the rule the reference states, and it is why this returns a <c>max</c> rather
    /// than picking one of the two.
    /// </para>
    /// <para>
    /// <b>No history is not zero usage.</b> A <see langword="null"/> average — a brand-new
    /// connection, a premise whose reads are all still on the exception worklist — falls back to the
    /// minimum. Treating it as zero would assess every new customer at nothing, which is precisely
    /// the case a deposit exists for.
    /// </para>
    /// <para>
    /// Pure, so every case above is provable in the fast tier with no database anywhere near it.
    /// </para>
    /// </remarks>
    /// <param name="averageMonthlyUsage">
    /// Units the premise consumes in an average month, or <see langword="null"/> where nothing has
    /// been measured. Negative is treated as no history: a register cannot run backwards, and a
    /// deposit is not the place to discover that one did.
    /// </param>
    public DepositAssessmentBasis Assess(decimal? averageMonthlyUsage)
    {
        if (!HasUsageBasis || averageMonthlyUsage is not { } average || average <= 0m)
        {
            return DepositAssessmentBasis.Minimum(MinimumAmount);
        }

        // Rounded here, at the figure, and not at some later total: this IS the amount a customer is
        // asked for, and Money's rule is that each computed charge is rounded as it is computed.
        var usageAmount = Money.Round(average * UsageMonths!.Value * UsageRate!.Value);

        return usageAmount > MinimumAmount
            ? DepositAssessmentBasis.Usage(usageAmount, average, UsageMonths.Value, UsageRate.Value)
            : DepositAssessmentBasis.Minimum(MinimumAmount);
    }

    /// <summary>
    /// Builds a reference rule. The id is derived from the class <i>and</i> the service so the
    /// migration seeds the same rows every time it is generated — see <see cref="ReferenceId"/> and
    /// <see cref="KeyFor"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The description or currency is missing, too long, or an enum value is undeclared.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An amount is negative or finer than its column.</exception>
    public static DepositRule Reference(
        CustomerClass customerClass,
        ServiceType serviceType,
        decimal minimumAmount,
        string currency,
        string description,
        int? usageMonths = null,
        decimal? usageRate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(currency.Length, CurrencyLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(description.Length, DescriptionLength);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumAmount);

        if (!Money.IsRounded(minimumAmount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumAmount), minimumAmount, "A deposit rule's minimum must be a whole number of cents.");
        }

        if (!Enum.IsDefined(customerClass))
        {
            throw new ArgumentOutOfRangeException(nameof(customerClass), customerClass, "Not a customer class GridCore declares.");
        }

        if (!ServiceTypes.IsDeclared(serviceType))
        {
            throw new ArgumentOutOfRangeException(nameof(serviceType), serviceType, "Not a service GridCore declares.");
        }

        // Half a usage basis is not half a rule. A count of months with no rate to price it at, or a
        // rate with no count of months, would assess to the minimum forever and read on the screen
        // as though usage had been considered — the worst of both, and silent.
        if (usageMonths is null != usageRate is null)
        {
            throw new ArgumentException(
                $"The deposit rule for {KeyFor(customerClass, serviceType)} gives "
                + (usageMonths is null ? "a usage rate with no number of months." : "a number of months with no usage rate.")
                + " A usage basis is both or neither.",
                usageMonths is null ? nameof(usageMonths) : nameof(usageRate));
        }

        if (usageMonths is { } months)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(months, nameof(usageMonths));
        }

        if (usageRate is { } rate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rate, nameof(usageRate));

            if (decimal.Round(rate, RateDecimalPlaces) != rate)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(usageRate), rate, $"A deposit usage rate carries at most {RateDecimalPlaces} decimal places.");
            }
        }

        // An unmetered service has nothing to average, so a usage basis on one is a figure that can
        // never apply. Refused rather than ignored: a schedule row that reads as usage-based and
        // silently is not is exactly the kind of reference data nobody notices is wrong.
        if (usageMonths is not null && !ServiceTypes.IsMetered(serviceType))
        {
            throw new ArgumentException(
                $"{serviceType} is unmetered, so its deposit cannot be assessed on usage. Give it a minimum alone.",
                nameof(serviceType));
        }

        return new DepositRule
        {
            Id = ReferenceId.For(DepositRules.AuthoredAt, KeyFor(customerClass, serviceType)),
            CustomerClass = customerClass,
            ServiceType = serviceType,
            MinimumAmount = minimumAmount,
            UsageMonths = usageMonths,
            UsageRate = usageRate,
            Currency = currency,
            Description = description,
        };
    }
}

/// <summary>
/// What a rule worked out to, and which half of it answered.
/// </summary>
/// <remarks>
/// The distinction is the whole point of a two-part rule and it is what a rep reads out: "the
/// minimum, because we have never metered you" and "two months of your usage" are different
/// conversations that happen to produce a number each. A screen that showed only the figure would
/// make the second indefensible.
/// </remarks>
/// <param name="Amount">What the customer is asked for.</param>
/// <param name="IsUsageBased">Whether measured usage decided it, rather than the published floor.</param>
/// <param name="AverageMonthlyUsage">The average that priced it, where one did.</param>
/// <param name="UsageMonths">How many months of it were taken, where any were.</param>
/// <param name="UsageRate">What each unit was priced at, where it was.</param>
public readonly record struct DepositAssessmentBasis(
    decimal Amount,
    bool IsUsageBased,
    decimal? AverageMonthlyUsage,
    int? UsageMonths,
    decimal? UsageRate)
{
    /// <summary>The published floor answered.</summary>
    public static DepositAssessmentBasis Minimum(decimal amount) => new(amount, false, null, null, null);

    /// <summary>Measured usage answered, and this is the working.</summary>
    public static DepositAssessmentBasis Usage(decimal amount, decimal average, int months, decimal rate) =>
        new(amount, true, average, months, rate);
}

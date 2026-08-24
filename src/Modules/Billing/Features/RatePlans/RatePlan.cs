using GridCore.Platform.Data;

namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>What a rate plan charges for.</summary>
public enum ServiceType
{
    /// <summary>Electricity, metered in kWh.</summary>
    Electricity,

    /// <summary>Water, metered in cubic metres.</summary>
    Water,

    /// <summary>Gas, metered in therms.</summary>
    Gas,
}

/// <summary>
/// A published tariff: a fixed monthly service charge plus tiered consumption rates, effective from
/// a date. Reference data — the utility cannot bill without one, so it ships by migration.
/// </summary>
/// <remarks>
/// This is the tariff, not the calculation. WP-2.3 owns the rate engine that reads a plan and turns
/// consumption into bill lines; what WP-0.8 owes is that a plan exists and is well-formed.
/// </remarks>
public sealed class RatePlan
{
    /// <summary>Longest plan code stored.</summary>
    public const int CodeLength = 32;

    /// <summary>Longest plan name stored.</summary>
    public const int NameLength = 128;

    /// <summary>Length of an ISO 4217 currency code.</summary>
    public const int CurrencyLength = 3;

    /// <summary>Longest unit-of-measure symbol stored.</summary>
    public const int UnitLength = 16;

    private readonly List<RatePlanTier> _tiers = [];

    private RatePlan()
    {
        // EF materialisation.
        Code = string.Empty;
        Name = string.Empty;
        Currency = string.Empty;
        UnitOfMeasure = string.Empty;
    }

    /// <summary>Identifier of this plan.</summary>
    public Guid Id { get; private init; }

    /// <summary>The code a person quotes, e.g. <c>RES-STD</c>. Unique across plans.</summary>
    public string Code { get; private init; }

    /// <summary>What the plan is called on a bill.</summary>
    public string Name { get; private init; }

    /// <summary>What it charges for.</summary>
    public ServiceType ServiceType { get; private init; }

    /// <summary>ISO 4217 code the charges are expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>The unit consumption is measured in, e.g. <c>kWh</c>.</summary>
    public string UnitOfMeasure { get; private init; }

    /// <summary>The fixed charge levied every billing period regardless of consumption.</summary>
    public decimal MonthlyServiceCharge { get; private init; }

    /// <summary>The first day this tariff applies.</summary>
    public DateOnly EffectiveFrom { get; private init; }

    /// <summary>
    /// Whether a service account with no plan of its own is billed on this one. Exactly one plan
    /// carries it — a unique index enforces that, because "the default" cannot be two things.
    /// </summary>
    public bool IsDefault { get; private init; }

    /// <summary>The consumption tiers, in order. Loaded on demand; empty until included.</summary>
    public IReadOnlyList<RatePlanTier> Tiers => _tiers;

    /// <summary>
    /// Builds a reference plan. The id is derived from the code so the migration seeds the same row
    /// every time it is generated — see <see cref="ReferenceId"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A required value is missing, wrong length, or negative.</exception>
    public static RatePlan Reference(
        string code,
        string name,
        ServiceType serviceType,
        string currency,
        string unitOfMeasure,
        decimal monthlyServiceCharge,
        DateOnly effectiveFrom,
        bool isDefault)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(unitOfMeasure);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(code.Length, CodeLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(unitOfMeasure.Length, UnitLength);

        if (currency.Length != CurrencyLength)
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
        }

        // A negative fixed charge is a credit dressed up as a tariff. Adjustments are how money goes
        // back to a customer (WP-2.4), and they are audited; a rate plan is not.
        if (monthlyServiceCharge < 0m)
        {
            throw new ArgumentException(
                $"Rate plan '{code}' has a negative monthly service charge ({monthlyServiceCharge}).",
                nameof(monthlyServiceCharge));
        }

        return new RatePlan
        {
            Id = ReferenceId.For(DefaultRatePlans.AuthoredAt, code),
            Code = code,
            Name = name,
            ServiceType = serviceType,
            Currency = currency,
            UnitOfMeasure = unitOfMeasure,
            MonthlyServiceCharge = monthlyServiceCharge,
            EffectiveFrom = effectiveFrom,
            IsDefault = isDefault,
        };
    }
}

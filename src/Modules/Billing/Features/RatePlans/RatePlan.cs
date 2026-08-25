using System.Globalization;
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

    /// <summary>Separates a plan's code from its effective date in the version key.</summary>
    private const char VersionSeparator = '@';

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

    /// <summary>
    /// The code a person quotes, e.g. <c>RES-STD</c>. Shared by every version of the same tariff —
    /// what is unique is the code <i>and</i> the date it takes effect.
    /// </summary>
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

    /// <summary>
    /// The first day this version applies. There is no end date: a version runs until the next
    /// version of the same code starts, which is one fact rather than two that can disagree.
    /// </summary>
    public DateOnly EffectiveFrom { get; private init; }

    /// <summary>
    /// Whether a service account with no plan of its own is billed on this one. Exactly one plan
    /// carries it <i>on any given effective date</i> — a unique index enforces that, because "the
    /// default" cannot be two things at once. Every version of the default tariff carries it, so
    /// republishing the default's prices does not silently leave the utility without one.
    /// </summary>
    public bool IsDefault { get; private init; }

    /// <summary>The consumption tiers, in order. Loaded on demand; empty until included.</summary>
    public IReadOnlyList<RatePlanTier> Tiers => _tiers;

    /// <summary>
    /// This version's natural key — its code and the date it takes effect, e.g.
    /// <c>RES-STD@2026-07-01</c>. What the row's id is derived from, and what a tier's id is
    /// derived from in turn.
    /// </summary>
    public string VersionKey => KeyFor(Code, EffectiveFrom);

    /// <summary>
    /// The natural key of the <paramref name="code"/> version taking effect on
    /// <paramref name="effectiveFrom"/>.
    /// </summary>
    /// <remarks>
    /// <b>The code alone is not a plan's identity; a code and a date are.</b> A tariff is republished
    /// whenever its prices change, and the versions have to coexist — last July's bill is still
    /// billed on last July's rates, whatever the plan charges now. Keying on the code alone (which is
    /// what WP-0.8 did, when there was one version of each) would give two versions of RES-STD the
    /// same <see cref="ReferenceId"/> and so the same primary key.
    /// </remarks>
    public static string KeyFor(string code, DateOnly effectiveFrom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return code + VersionSeparator + effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a reference plan. The id is derived from the code <i>and the effective date</i> so the
    /// migration seeds the same row every time it is generated — see <see cref="ReferenceId"/> and
    /// <see cref="KeyFor"/>.
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
            Id = ReferenceId.For(DefaultRatePlans.AuthoredAt, KeyFor(code, effectiveFrom)),
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

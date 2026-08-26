using System.Globalization;
using GridCore.Contracts.Services;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>
/// The non-rate charges the utility publishes. A fixed vocabulary rather than free text: a fee is a
/// published charge, and one raised under a code nobody declared is a figure with nothing behind it.
/// </summary>
/// <remarks>
/// <para>
/// Stored by name on every schedule row and every charge, so a charge read back years from now does
/// not depend on today's enum ordering — the rule every stored enum in GridCore follows.
/// </para>
/// <para>
/// <b>Adding a member without adding its row is a startup failure</b>, not a counter failure —
/// see <see cref="FeeSchedules.RequireComplete"/>. That is the whole reason this is an enum and not
/// a string: the compiler and the migration can be held to the same list.
/// </para>
/// </remarks>
public enum FeeCode
{
    /// <summary>Establishing supply at a premise: the meter and service charge levied once, at connection.</summary>
    ServiceConnection = 1,

    /// <summary>Restoring supply after it was cut for non-payment (WP-2.21 is what will raise it).</summary>
    Reconnection = 2,

    /// <summary>A payment that settled and then came back — the returned-cheque charge (WP-2.22).</summary>
    ReturnedPayment = 3,

    /// <summary>Testing a meter at the customer's request (WP-3.8), refundable where the meter is found faulty.</summary>
    MeterTest = 4,

    /// <summary>Inspecting an installation before supply is established (WP-3.6).</summary>
    Inspection = 5,

    /// <summary>The penalty for taking supply without an account, or interfering with a meter (WP-3.10).</summary>
    UnauthorizedConnection = 6,

    /// <summary>
    /// The monthly charge for being late (WP-2.19) — the one published fee that is a <i>rate</i>
    /// rather than a figure.
    /// </summary>
    /// <remarks>
    /// It prices a percentage of what is past due, so its schedule row carries a
    /// <see cref="FeeScheduleEntry.Rate"/> and no <see cref="FeeScheduleEntry.Amount"/>, and it
    /// cannot be raised from a screen: there is no basis a rep could type that would not be a rep
    /// inventing one. <c>LateChargeService</c> is the only caller.
    /// </remarks>
    LateCharge = 7,
}

/// <summary>
/// How a published fee arrives at its figure.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two, and there is no third.</b> Every fee GridCore ships is either a published amount — $60 to
/// reconnect, whoever you are and whatever you owe — or a published rate taken on something the
/// register can compute, which today is the 1% per month of a past-due balance that CNMI Public Law
/// 16-17's delinquency regime turns on. A tiered fee, a capped one or one that compounds is a rule
/// with dimensions this enum cannot carry; the day one is published is the day the schedule grows a
/// table of its own rather than a third member here.
/// </para>
/// <para>
/// Stored by name like every other enum in this schema, so a row read years from now does not depend
/// on today's numbering.
/// </para>
/// </remarks>
public enum FeeBasis
{
    /// <summary>A published amount. What the schedule says, whatever the account owes.</summary>
    Flat = 1,

    /// <summary>
    /// A published rate taken on a basis the caller supplies — <see cref="FeeScheduleEntry.Rate"/>
    /// times the figure being charged on. The charge stamps the rate, the basis and the result, so
    /// the arithmetic can be re-read years later without re-running it.
    /// </summary>
    Rate = 2,
}

/// <summary>
/// One version of one published fee: what it is called, what it costs, and the day that figure took
/// effect. Reference data, seeded by migration — never a constant in the domain.
/// </summary>
/// <remarks>
/// <para>
/// <b>Effective-dated exactly as a tariff is</b> (<see cref="RatePlan"/>), and for the same reason:
/// a fee is republished when its amount changes, the versions have to coexist, and a charge raised
/// last July was priced on last July's schedule whatever the schedule says now. A version runs until
/// the next version of the same code starts — one fact rather than an <c>EffectiveTo</c> beside an
/// <c>EffectiveFrom</c> that can disagree with it. <see cref="FeeScheduleSelector"/> is the choosing.
/// </para>
/// <para>
/// <b>A row is never edited in place.</b> Changing $135 to $150 is a new row effective from the day
/// the new figure applies, in a new migration — migrations are append-only (invariant 7) and a
/// schedule that was edited would silently reprice every charge ever raised under it. That is what
/// makes a reprint of an old charge reproduce the old figure without anybody storing the document.
/// </para>
/// <para>
/// <b>The catalogue lives in Billing, beside the tariffs, and not in Customers.</b> A fee is a
/// published charge that becomes a receivable; Customers must not own money that appears on a bill,
/// and reads this over a service interface the way it already reads <c>IBillDirectory</c>.
/// </para>
/// </remarks>
public sealed class FeeScheduleEntry
{
    /// <summary>Longest stored form of a fee code's name.</summary>
    public const int CodeNameLength = 48;

    /// <summary>Longest stored form of a service type's name.</summary>
    public const int ServiceTypeNameLength = 32;

    /// <summary>Longest name stored — what the line says on the bill.</summary>
    public const int NameLength = 128;

    /// <summary>Longest description stored: why the figure is what it is, and where it came from.</summary>
    public const int DescriptionLength = 512;

    /// <summary>Length of an ISO 4217 currency code.</summary>
    public const int CurrencyLength = 3;

    /// <summary>Longest stored form of a basis name.</summary>
    public const int BasisNameLength = 32;

    /// <summary>
    /// Decimal places a published rate carries — four, matching <c>DepositRule.UsageRate</c> and
    /// <c>RatePlanTier.RatePerUnit</c>.
    /// </summary>
    /// <remarks>
    /// A percentage rounded to the cent would be 0.01 or 0.02 and nothing between, which cannot
    /// express one and a half per cent. Four places is what lets the day the legislature moves 1% to
    /// 1.25% be a migration rather than a schema change.
    /// </remarks>
    public const int RateDecimalPlaces = 4;

    /// <summary>Total digits stored for a published rate.</summary>
    public const int RatePrecision = 18;

    /// <summary>Separates a fee's code from its effective date in the version key.</summary>
    private const char VersionSeparator = '@';

    private FeeScheduleEntry()
    {
        // EF materialisation.
        Name = string.Empty;
        Description = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>Identifier of this version. Derived from the code and its effective date.</summary>
    public Guid Id { get; private init; }

    /// <summary>Which published fee this is. Shared by every version of it.</summary>
    public FeeCode Code { get; private init; }

    /// <summary>What the line says when the fee reaches a bill.</summary>
    public string Name { get; private init; }

    /// <summary>
    /// What it covers and where the figure came from — read by the clerk who has to explain it, and
    /// the one place a demo figure says out loud that it is one.
    /// </summary>
    public string Description { get; private init; }

    /// <summary>
    /// The service this fee is published against.
    /// </summary>
    /// <remarks>
    /// Carried on the row rather than left implicit, because the reference publishes different
    /// figures per service — but the utility bills one service today, so every shipped row is
    /// electricity. <b>The code alone is the fee's identity, not the code and the service.</b>
    /// WP-2.17 gave a service account a service and re-keyed the <i>deposit</i> schedule on it, and
    /// deliberately left this one alone: no fee GridCore publishes yet differs by supply, and
    /// re-keying a catalogue in anticipation would move every id in it for nothing. The day a water
    /// reconnection fee differs from the electric one, this index and
    /// <see cref="FeeSchedules.RequireComplete"/> are what change.
    /// </remarks>
    public ServiceType ServiceType { get; private init; }

    /// <summary>
    /// How this fee arrives at its figure: a published amount, or a published rate on a basis.
    /// </summary>
    public FeeBasis Basis { get; private init; }

    /// <summary>
    /// What the fee costs, in whole cents — or <see langword="null"/> on a rate fee, which has no
    /// figure until something is charged on it.
    /// </summary>
    /// <remarks>
    /// <b>Nullable since WP-2.19, and honestly so.</b> A rate row could have carried a zero here and
    /// kept the column non-nullable, at the price of a schedule that reads as publishing a free
    /// service connection. <see cref="Basis"/> is what says which of the two columns is the figure,
    /// and <see cref="Reference"/> and <see cref="ReferenceRate"/> are what stop a row carrying
    /// both or neither.
    /// </remarks>
    public decimal? Amount { get; private init; }

    /// <summary>
    /// The published rate this fee is taken at — <c>0.0100</c> for one per cent — or
    /// <see langword="null"/> on a flat fee.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A fraction, not a percentage.</b> Stored as 0.0100 rather than 1.00 so the arithmetic is
    /// a multiplication with nothing divided by a hundred on the way — the one place a stray factor
    /// of 100 could put a customer's late charge two orders of magnitude out.
    /// </para>
    /// <para>
    /// Finer than a cent (<see cref="RateDecimalPlaces"/>), because it is a rate rather than money.
    /// </para>
    /// </remarks>
    public decimal? Rate { get; private init; }

    /// <summary>ISO 4217 code the amount is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>The first day this version applies. There is no end date — see the type's remarks.</summary>
    public DateOnly EffectiveFrom { get; private init; }

    /// <summary>
    /// This version's natural key — its code and the date it takes effect, e.g.
    /// <c>Reconnection@2026-07-01</c>. What the row's id is derived from.
    /// </summary>
    public string VersionKey => KeyFor(Code, EffectiveFrom);

    /// <summary>
    /// The natural key of the <paramref name="code"/> version taking effect on
    /// <paramref name="effectiveFrom"/>.
    /// </summary>
    public static string KeyFor(FeeCode code, DateOnly effectiveFrom) =>
        code.ToString() + VersionSeparator + effectiveFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a reference row. The id is derived from the code <i>and</i> the effective date, so the
    /// migration seeds the same rows every time it is generated and a repricing is a new row rather
    /// than a collision with the old one.
    /// </summary>
    /// <exception cref="ArgumentException">A required value is missing, too long, or not a code GridCore declares.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative or finer than a cent.</exception>
    public static FeeScheduleEntry Reference(
        FeeCode code,
        string name,
        ServiceType serviceType,
        decimal amount,
        string currency,
        DateOnly effectiveFrom,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(description.Length, DescriptionLength);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);

        if (!Enum.IsDefined(code))
        {
            throw new ArgumentException($"'{code}' is not a fee GridCore declares.", nameof(code));
        }

        if (!Enum.IsDefined(serviceType))
        {
            throw new ArgumentException($"'{serviceType}' is not a service GridCore declares.", nameof(serviceType));
        }

        if (currency.Length != CurrencyLength)
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
        }

        // A published fee is a whole number of cents. Refused rather than rounded, the rule Money
        // states: a schedule figure that is finer than a cent is a typo in reference data, and
        // rounding it here would put a figure on a bill that no published document says.
        if (!Money.IsRounded(amount))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "A published fee is a whole number of cents.");
        }

        return new FeeScheduleEntry
        {
            Id = ReferenceId.For(FeeSchedules.AuthoredAt, KeyFor(code, effectiveFrom)),
            Code = code,
            Name = name,
            ServiceType = serviceType,
            Basis = FeeBasis.Flat,
            Amount = amount,
            Rate = null,
            Currency = currency,
            EffectiveFrom = effectiveFrom,
            Description = description,
        };
    }

    /// <summary>
    /// Builds a reference row for a fee published as a <b>rate</b> rather than as an amount
    /// (WP-2.19) — the late charge, and nothing else today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A separate factory rather than a nullable parameter on <see cref="Reference"/>, so a row
    /// carrying both an amount and a rate, or neither, is not a thing the seed list can express.
    /// The two kinds of published fee are different enough that the call site should say which it
    /// is writing.
    /// </para>
    /// <para>
    /// The id is derived exactly as a flat row's is, from the code and the effective date — so
    /// republishing the rate is a new row and the old charges keep pointing at the old one.
    /// </para>
    /// </remarks>
    /// <param name="code">Which published fee.</param>
    /// <param name="name">What the line says when it reaches a bill.</param>
    /// <param name="serviceType">The service it is published against.</param>
    /// <param name="rate">The rate, as a fraction — <c>0.01m</c> for one per cent.</param>
    /// <param name="currency">ISO 4217 code the charges it produces are expressed in.</param>
    /// <param name="effectiveFrom">The first day this version applies.</param>
    /// <param name="description">What it covers and where the figure came from.</param>
    /// <exception cref="ArgumentException">A required value is missing, too long, or not a code GridCore declares.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive, or is finer than the stored scale.</exception>
    public static FeeScheduleEntry ReferenceRate(
        FeeCode code,
        string name,
        ServiceType serviceType,
        decimal rate,
        string currency,
        DateOnly effectiveFrom,
        string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(description.Length, DescriptionLength);

        if (!Enum.IsDefined(code))
        {
            throw new ArgumentException($"'{code}' is not a fee GridCore declares.", nameof(code));
        }

        if (!Enum.IsDefined(serviceType))
        {
            throw new ArgumentException($"'{serviceType}' is not a service GridCore declares.", nameof(serviceType));
        }

        if (currency.Length != CurrencyLength)
        {
            throw new ArgumentException($"'{currency}' is not an ISO 4217 currency code.", nameof(currency));
        }

        // A published rate of nought is a fee the utility has stopped charging, and the way to stop
        // charging one is to stop raising it — a zero row would put "Late payment charge 0.00" on a
        // bill, which is the argument AccountCharge.Raise already makes about a zero amount.
        if (rate <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), rate, "A published rate must be positive.");
        }

        // Refused rather than rounded, the rule Money states one scale up: a rate finer than the
        // column is a typo in reference data, and rounding it would publish a figure no document says.
        if (decimal.Round(rate, RateDecimalPlaces) != rate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rate),
                rate,
                $"A published rate carries at most {RateDecimalPlaces} decimal places.");
        }

        return new FeeScheduleEntry
        {
            Id = ReferenceId.For(FeeSchedules.AuthoredAt, KeyFor(code, effectiveFrom)),
            Code = code,
            Name = name,
            ServiceType = serviceType,
            Basis = FeeBasis.Rate,
            Amount = null,
            Rate = rate,
            Currency = currency,
            EffectiveFrom = effectiveFrom,
            Description = description,
        };
    }

    /// <summary>
    /// What this row charges on <paramref name="basisAmount"/>, rounded to the cent.
    /// </summary>
    /// <remarks>
    /// <b>Rounded here, at the figure.</b> This IS the amount that lands on a bill, and Money's rule
    /// is that each computed charge is rounded as it is computed rather than at some later total —
    /// the same call <c>DepositRule.Assess</c> makes about a usage-based deposit.
    /// </remarks>
    /// <param name="basisAmount">What the rate is taken on — a past-due balance, for the late charge.</param>
    /// <exception cref="BillingValidationException">This row is a flat fee and has no rate to apply.</exception>
    public decimal PriceOn(decimal basisAmount)
    {
        if (Basis is not FeeBasis.Rate || Rate is not { } rate)
        {
            throw new BillingValidationException(
                $"{Code} is published as a flat fee, so there is nothing to charge on a basis of {basisAmount:0.00}.");
        }

        return Money.Round(rate * basisAmount);
    }
}

/// <summary>
/// Which version of a published fee applies on a given day — the effective-dating rule
/// <see cref="RatePlanSelector"/> states for tariffs, in the one other place GridCore versions
/// reference data by date.
/// </summary>
/// <remarks>
/// Its own function rather than a generic one shared with the tariff selector: the two select over
/// different types with different identities, and the shared abstraction that would unify them
/// (an <c>IEffectiveDated</c> both entities implement) buys a dozen lines and costs the ability to
/// read either rule on its own. If a third one arrives, that is the moment to reconsider.
/// </remarks>
public static class FeeScheduleSelector
{
    /// <summary>
    /// The version of a fee in force on <paramref name="on"/> — the latest one whose
    /// <see cref="FeeScheduleEntry.EffectiveFrom"/> is on or before that day.
    /// </summary>
    /// <param name="versions">
    /// Versions of any number of fees. Versions of other codes are ignored rather than trusted,
    /// which is what lets a caller hand over the whole schedule.
    /// </param>
    /// <param name="code">The fee being priced.</param>
    /// <param name="on">The day it is being raised — never "today" where the two differ.</param>
    /// <returns>
    /// The version in force, or <see langword="null"/> where the fee had not been published yet. A
    /// null is a real answer: a fee raised before it was published has no figure, and saying so
    /// beats charging one nobody had announced.
    /// </returns>
    public static FeeScheduleEntry? InForceOn(IEnumerable<FeeScheduleEntry> versions, FeeCode code, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(versions);

        FeeScheduleEntry? inForce = null;

        foreach (var version in versions)
        {
            if (version.Code != code || version.EffectiveFrom > on)
            {
                continue;
            }

            // Strictly later wins, so a set that somehow held two versions with the same effective
            // date resolves to the first of them rather than to whichever the enumeration yielded
            // last. The database refuses that pair anyway (ux_fee_schedule_code_effective).
            if (inForce is null || version.EffectiveFrom > inForce.EffectiveFrom)
            {
                inForce = version;
            }
        }

        return inForce;
    }
}

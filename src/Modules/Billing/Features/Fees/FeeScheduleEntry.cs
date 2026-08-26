using System.Globalization;
using GridCore.Contracts.Services;
using GridCore.Modules.Billing.Features.RatePlans;
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

    /// <summary>What the fee costs, in whole cents.</summary>
    public decimal Amount { get; private init; }

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
            Amount = amount,
            Currency = currency,
            EffectiveFrom = effectiveFrom,
            Description = description,
        };
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

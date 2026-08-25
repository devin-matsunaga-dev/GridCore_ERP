using GridCore.Modules.Billing.Features.Rating;

namespace GridCore.Modules.Billing.Features.Bills;

/// <summary>
/// One line of a bill, exactly as it was calculated: what it is for, how many units at what rate,
/// and what that came to.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rate is stamped on the line, not looked up from the tariff.</b> WP-1.4 stamps
/// quantity-on-hand against every stock movement and WP-2.2 stamps consumption against every
/// reading, for the same reason this stamps the rate: the tariff will be repriced, and a bill that
/// re-derived its own arithmetic from today's rates would silently change what a customer was
/// charged last July. A bill nobody can reproduce is a bill nobody can defend.
/// </para>
/// <para>
/// Lines are written once, by <see cref="Bill.Calculate"/>, and never edited. A correction is a new
/// bill or an adjustment (WP-2.4) — never a rewritten line.
/// </para>
/// </remarks>
public sealed class BillLine
{
    /// <summary>Longest line description stored.</summary>
    public const int DescriptionLength = 256;

    /// <summary>Longest stored form of a charge-kind name.</summary>
    public const int EnumNameLength = 32;

    /// <summary>Decimal places a unit rate carries, matching the tariff it came from.</summary>
    public const int RateDecimalPlaces = 6;

    /// <summary>Decimal places a unit quantity carries, matching the reading register.</summary>
    public const int UnitsDecimalPlaces = RateEngine.ConsumptionDecimalPlaces;

    private BillLine()
    {
        // EF materialisation.
        Description = string.Empty;
    }

    /// <summary>Identifier of this line. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The bill it belongs to.</summary>
    public Guid BillId { get; private init; }

    /// <summary>Position on the bill, from 1.</summary>
    public int Sequence { get; private init; }

    /// <summary>Whether this is the standing charge or a consumption block.</summary>
    public ChargeKind Kind { get; private init; }

    /// <summary>What the line says on the bill.</summary>
    public string Description { get; private init; }

    /// <summary>Which tier of the tariff produced it, for a consumption line.</summary>
    public int? TierSequence { get; private init; }

    /// <summary>Units charged. Absent on the service charge, which is not per unit.</summary>
    public decimal? Units { get; private init; }

    /// <summary>Price of one unit inside the tier. Absent on the service charge.</summary>
    public decimal? RatePerUnit { get; private init; }

    /// <summary>What the line comes to, rounded to the cent when it was calculated.</summary>
    public decimal Amount { get; private init; }

    /// <summary>Writes <paramref name="charge"/> onto <paramref name="billId"/> as a line.</summary>
    internal static BillLine From(Guid billId, RateCharge charge, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(charge);

        return new BillLine
        {
            Id = Guid.CreateVersion7(now),
            BillId = billId,
            Sequence = charge.Sequence,
            Kind = charge.Kind,
            Description = charge.Description.Length > DescriptionLength
                ? charge.Description[..DescriptionLength]
                : charge.Description,
            TierSequence = charge.TierSequence,
            Units = charge.Units,
            RatePerUnit = charge.RatePerUnit,
            Amount = charge.Amount,
        };
    }
}

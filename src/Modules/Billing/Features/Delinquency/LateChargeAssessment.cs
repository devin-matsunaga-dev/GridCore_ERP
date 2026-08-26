using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Billing.Features.Delinquency;

/// <summary>
/// One late charge assessed against one bill for one month (WP-2.19): what was past due, the rate
/// it was taken at, what that came to, and the charge it produced.
/// </summary>
/// <remarks>
/// <para>
/// <b>This row is the idempotency, and that is its first job.</b> WORK_PACKAGES.md asks for a
/// late-charge run "idempotent per bill per period", and the only way to make a job that raises
/// money idempotent is to write down what it has already raised.
/// <c>ux_late_charge_assessments_bill_period</c> is what makes "running the job twice charges once"
/// a fact about the database rather than a property of the code path that happened to check first —
/// two runs racing each other end with one row and one charge, because the second insert is refused.
/// </para>
/// <para>
/// <b>The period is a month, and it is the month the run was for rather than the month the bill was
/// raised in.</b> A bill three months late is charged three times, once per month it stayed unpaid,
/// which is what "one per cent per month" means. Storing the first day of that month rather than a
/// year-and-month string keeps it comparable, sortable and indexable as a date.
/// </para>
/// <para>
/// <b>It duplicates figures the charge also stamps, deliberately.</b> The <see cref="AccountCharge"/>
/// carries the schedule row, the rate and the basis because a charge has to defend its own figure;
/// this carries them again because the register that answers "was this bill charged for August"
/// must answer it without joining to a charge that may since have been withdrawn. A withdrawn late
/// charge does <b>not</b> free the bill to be charged again for the same month — somebody decided
/// that money was not owed, and a run that quietly re-raised it would overturn them.
/// </para>
/// </remarks>
public sealed class LateChargeAssessment
{
    /// <summary>Longest stored form of a bill or account number.</summary>
    public const int NumberLength = RegistryNumbers.MaxLength;

    private LateChargeAssessment()
    {
        // EF materialisation.
        BillNumber = string.Empty;
        AccountNumber = string.Empty;
        Currency = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this assessment. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The bill that was late.</summary>
    public Guid BillId { get; private init; }

    /// <summary>Its number, as printed. Stamped, so the register reads without a join.</summary>
    public string BillNumber { get; private init; }

    /// <summary>The account it was billed to.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>Its number, as printed.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>The customer who owed it.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>
    /// The first day of the month this assessment covers. Half of the natural key, with
    /// <see cref="BillId"/>.
    /// </summary>
    public DateOnly PeriodStart { get; private init; }

    /// <summary>The day the run judged against — what <see cref="DaysPastDue"/> is measured to.</summary>
    public DateOnly AssessedOn { get; private init; }

    /// <summary>How late the bill was on that day.</summary>
    public int DaysPastDue { get; private init; }

    /// <summary>
    /// What was past due on the bill when the rate was taken — <b>the balance, not the printed
    /// total</b>, which is the distinction WORK_PACKAGES.md asks this package to prove.
    /// </summary>
    public decimal BasisAmount { get; private init; }

    /// <summary>The published rate it was taken at, as a fraction.</summary>
    public decimal Rate { get; private init; }

    /// <summary>What the two came to, rounded to the cent.</summary>
    public decimal Amount { get; private init; }

    /// <summary>ISO 4217 code the amounts are expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>The schedule row that published the rate.</summary>
    public Guid FeeScheduleId { get; private init; }

    /// <summary>The charge this assessment raised. The money itself lives there.</summary>
    public Guid AccountChargeId { get; private init; }

    /// <summary>When the run ran.</summary>
    public DateTimeOffset AssessedAt { get; private init; }

    /// <summary>Subject id of whoever ran it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>The first day of the month <paramref name="on"/> falls in — a period, as this register keys them.</summary>
    public static DateOnly PeriodOf(DateOnly on) => new(on.Year, on.Month, 1);

    /// <summary>
    /// Records that <paramref name="charge"/> was raised because <paramref name="billNumber"/> was
    /// late in <paramref name="periodStart"/>'s month.
    /// </summary>
    /// <param name="charge">The charge that was raised. Its figures are read back rather than re-derived.</param>
    /// <param name="billId">The bill that was late.</param>
    /// <param name="billNumber">Its number.</param>
    /// <param name="periodStart">First day of the month covered.</param>
    /// <param name="assessedOn">The day judged against.</param>
    /// <param name="daysPastDue">How late the bill was on that day.</param>
    /// <param name="actor">Who ran it.</param>
    /// <param name="now">The clock, for the row's identity and timestamp.</param>
    /// <exception cref="BillingValidationException">The charge carries no rate or no basis, or nobody is named.</exception>
    public static LateChargeAssessment For(
        AccountCharge charge,
        Guid billId,
        string billNumber,
        DateOnly periodStart,
        DateOnly assessedOn,
        int daysPastDue,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(charge);
        ArgumentNullException.ThrowIfNull(actor);

        // Read off the charge rather than recomputed. The charge is what the customer will be asked
        // for, so an assessment that arrived at its own figure would be a second opinion nobody
        // asked for — and the first thing to disagree with the bill.
        if (charge.Rate is not { } rate || charge.BasisAmount is not { } basis)
        {
            throw new BillingValidationException(
                $"A late charge is a rate on a past-due balance, and charge {charge.Id} carries "
                + $"{(charge.Rate is null ? "no rate" : "no basis")}. It was not raised by the late-charge run.");
        }

        if (billId == Guid.Empty)
        {
            throw new BillingValidationException("A late charge assessment names the bill that was late.");
        }

        if (daysPastDue <= 0)
        {
            throw new BillingValidationException(
                $"A late charge is assessed on a bill that is late, and {billNumber} was {daysPastDue} days past due.");
        }

        if (charge.Amount <= Money.Zero)
        {
            throw new BillingValidationException(
                $"A late charge assessment records money charged, and {charge.Amount} is not.");
        }

        return new LateChargeAssessment
        {
            Id = Guid.CreateVersion7(now),
            BillId = billId,
            BillNumber = RegistryText.Clean(billNumber, NumberLength)
                ?? throw new BillingValidationException("A late charge assessment names the bill that was late."),
            ServiceAccountId = charge.ServiceAccountId,
            AccountNumber = charge.AccountNumber,
            CustomerId = charge.CustomerId,
            PeriodStart = periodStart,
            AssessedOn = assessedOn,
            DaysPastDue = daysPastDue,
            BasisAmount = basis,
            Rate = rate,
            Amount = charge.Amount,
            Currency = charge.Currency,
            FeeScheduleId = charge.FeeScheduleId,
            AccountChargeId = charge.Id,
            AssessedAt = now,
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new BillingValidationException("A late charge assessment must name who ran it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
        };
    }
}

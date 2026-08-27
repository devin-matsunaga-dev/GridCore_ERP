using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>
/// One dated promise inside a payment arrangement (WP-2.20): what is due, when, and how much of it
/// has arrived.
/// </summary>
/// <remarks>
/// <para>
/// <b>It settles from a real payment and never from a form.</b> <see cref="Settle"/> is reached only
/// by the consumer of <c>PaymentApproved</c> — Payments states that money arrived, and this records
/// what that money was for. Nothing here takes money and nothing here reduces a bill: the customer
/// still owes exactly what the bills say, which is WORK_PACKAGES.md's rule for the whole feature.
/// </para>
/// <para>
/// <b><see cref="PaidAmount"/> moves and nothing else does.</b> The amount and the due date are what
/// was promised, and a promise that could be edited after the fact would make "one missed due date
/// breaks it" unfalsifiable.
/// </para>
/// </remarks>
public sealed class ArrangementInstalment
{
    private ArrangementInstalment()
    {
        // EF materialisation.
    }

    /// <summary>Identifier of this instalment. Guid v7.</summary>
    public Guid Id { get; private init; }

    /// <summary>The arrangement it belongs to.</summary>
    public Guid PaymentArrangementId { get; private init; }

    /// <summary>Where it falls in the schedule, from 1. The down payment, where there is one, is 1.</summary>
    public int Sequence { get; private init; }

    /// <summary>The day it falls due. <b>Missing this is what breaks an arrangement.</b></summary>
    public DateOnly DueDate { get; private init; }

    /// <summary>What was promised on it.</summary>
    public decimal Amount { get; private init; }

    /// <summary>How much of that has arrived.</summary>
    public decimal PaidAmount { get; private set; }

    /// <summary>Whether this is the money taken up front.</summary>
    public bool IsDownPayment { get; private init; }

    /// <summary>When the last payment against it landed, or <see langword="null"/> while none has.</summary>
    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>What is still owed on it. Never negative — an overpayment cascades to the next.</summary>
    public decimal Outstanding => Math.Max(Money.Zero, Amount - PaidAmount);

    /// <summary>Whether it has been paid in full.</summary>
    public bool IsSettled => PaidAmount >= Amount;

    /// <summary>
    /// Whether it was unpaid when <paramref name="asOf"/> came round — the single condition that
    /// breaks an arrangement.
    /// </summary>
    /// <remarks>
    /// Strictly after the due date: a customer paying on the day is a customer who paid on time, and
    /// an arrangement that broke at one minute past midnight on its own due date would break every
    /// arrangement ever made.
    /// </remarks>
    public bool IsMissedBy(DateOnly asOf) => !IsSettled && asOf > DueDate;

    /// <summary>Builds an instalment from a scheduled line.</summary>
    /// <param name="arrangementId">The arrangement it belongs to.</param>
    /// <param name="line">The line, from <see cref="ArrangementSchedule.Build"/>.</param>
    /// <param name="now">The clock, for the row's identity.</param>
    /// <exception cref="RegistryValidationException">The line is not one an instalment can carry.</exception>
    public static ArrangementInstalment From(Guid arrangementId, ScheduledInstalment line, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (line.Amount <= Money.Zero || !Money.IsRounded(line.Amount))
        {
            throw new RegistryValidationException(
                $"An instalment is a whole number of cents worth collecting; '{line.Amount}' is not.");
        }

        return new ArrangementInstalment
        {
            Id = Guid.CreateVersion7(now),
            PaymentArrangementId = arrangementId,
            Sequence = line.Sequence,
            DueDate = line.DueDate,
            Amount = line.Amount,
            PaidAmount = Money.Zero,
            IsDownPayment = line.IsDownPayment,
        };
    }

    /// <summary>
    /// Puts <paramref name="amount"/> against this instalment and answers what is left over.
    /// </summary>
    /// <remarks>
    /// <b>It takes only what it is owed and hands the rest back</b>, which is what lets a payment
    /// larger than one instalment carry on down the schedule instead of sitting as a credit on a
    /// line that has already been kept.
    /// </remarks>
    /// <param name="amount">What is on offer.</param>
    /// <param name="now">When the money landed.</param>
    /// <returns>The part of <paramref name="amount"/> this instalment did not need.</returns>
    internal decimal Settle(decimal amount, DateTimeOffset now)
    {
        var taken = Math.Min(amount, Outstanding);

        if (taken <= Money.Zero)
        {
            return amount;
        }

        PaidAmount += taken;
        SettledAt = now;

        return amount - taken;
    }
}

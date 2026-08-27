using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>One line of a proposed schedule, before it becomes a row.</summary>
/// <param name="Sequence">Where it falls, from 1.</param>
/// <param name="DueDate">The day it falls due.</param>
/// <param name="Amount">What is due on it.</param>
/// <param name="IsDownPayment">Whether this is the money taken up front.</param>
public sealed record ScheduledInstalment(int Sequence, DateOnly DueDate, decimal Amount, bool IsDownPayment);

/// <summary>
/// The arithmetic behind an arrangement: a balance, a down payment and a number of instalments
/// become dated amounts that add up to exactly what was promised (WP-2.20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and that is why every figure in WORK_PACKAGES.md's verify list is a fast test.</b> A
/// hundred dollars over three months, a remainder of a cent, a down payment that swallows all but a
/// dollar — none of them needs a database, a customer or a bill, because a schedule is a function of
/// four numbers and two dates.
/// </para>
/// <para>
/// <b>The lines sum to the balance exactly, in <see langword="decimal"/>, and the remainder lands on
/// the LAST instalment rather than being spread.</b> WORK_PACKAGES.md asks for precisely that, and
/// the reason is that a customer reads the schedule down the telephone: "$33.33, $33.33, $33.34"
/// is a schedule a person can check, and thirty-three thirty-three three times over is a schedule
/// that quietly loses the utility a cent on every arrangement it ever makes. Spreading the remainder
/// would produce neither — it would produce a column of figures that differ for no reason a rep
/// could explain.
/// </para>
/// <para>
/// <b>The down payment is a line of the schedule, not a deduction from it.</b> It is due on the day
/// the arrangement is made rather than a month later, and it is what the customer actually pays, so
/// it belongs where a person reads it — and putting it in the schedule is what keeps "the lines add
/// up to the balance" true of the whole promise rather than of the part after the deposit.
/// </para>
/// </remarks>
public static class ArrangementSchedule
{
    /// <summary>Days between instalments where the caller does not say. A month, near enough for a demo utility.</summary>
    public const int DefaultIntervalDays = 30;

    /// <summary>The longest gap between instalments a schedule may use.</summary>
    /// <remarks>
    /// A promise whose next payment is a year away is not a payment arrangement, whatever it is
    /// called — and the instalment ceiling on <see cref="ArrangementLimit"/> would be trivially
    /// avoidable without this: six instalments at 365-day intervals is a six-year debt inside a
    /// residential rep's authority.
    /// </remarks>
    public const int MaximumIntervalDays = 90;

    /// <summary>
    /// The schedule for <paramref name="balance"/>, taking <paramref name="downPayment"/> up front
    /// and spreading the rest over <paramref name="instalmentCount"/> dated instalments.
    /// </summary>
    /// <param name="balance">What the arrangement promises, in total. The lines add up to this.</param>
    /// <param name="downPayment">What is taken up front. Zero where none is.</param>
    /// <param name="instalmentCount">How many instalments the rest is spread over. At least one.</param>
    /// <param name="downPaymentDue">The day the down payment falls due — the day the arrangement is made.</param>
    /// <param name="firstInstalmentDue">The day the first instalment falls due.</param>
    /// <param name="intervalDays">Days between instalments after the first.</param>
    /// <exception cref="RegistryValidationException">The figures do not describe a schedule anybody could keep.</exception>
    public static IReadOnlyList<ScheduledInstalment> Build(
        decimal balance,
        decimal downPayment,
        int instalmentCount,
        DateOnly downPaymentDue,
        DateOnly firstInstalmentDue,
        int intervalDays = DefaultIntervalDays)
    {
        if (balance <= Money.Zero)
        {
            throw new RegistryValidationException(
                $"An arrangement promises a balance, and {balance:0.00} is not one.");
        }

        if (!Money.IsRounded(balance))
        {
            throw new RegistryValidationException(
                $"The balance an arrangement is made against is a whole number of cents; '{balance}' is not.");
        }

        if (downPayment < Money.Zero)
        {
            throw new RegistryValidationException(
                $"A down payment is money taken up front, and {downPayment:0.00} is not an amount of money.");
        }

        if (!Money.IsRounded(downPayment))
        {
            throw new RegistryValidationException(
                $"A down payment is a whole number of cents; '{downPayment}' is not.");
        }

        // Equal is refused as well as greater: a "down payment" covering the whole balance is a
        // payment, and dressing one up as an arrangement would put an account under the protection
        // of a promise that has nothing left to promise.
        if (downPayment >= balance)
        {
            throw new RegistryValidationException(
                $"A down payment of {downPayment:0.00} covers the whole {balance:0.00} being arranged, which leaves "
                + "nothing to spread. Take the payment instead.");
        }

        if (instalmentCount < 1)
        {
            throw new RegistryValidationException(
                $"An arrangement is spread over at least one instalment, and {instalmentCount} is not a schedule.");
        }

        if (instalmentCount > PaymentArrangement.MaximumInstalments)
        {
            throw new RegistryValidationException(
                $"{instalmentCount} instalments is beyond the {PaymentArrangement.MaximumInstalments} GridCore will "
                + "schedule at all. What a rep may agree without approval is a smaller figure again — see the "
                + "published arrangement limits.");
        }

        if (intervalDays < 1 || intervalDays > MaximumIntervalDays)
        {
            throw new RegistryValidationException(
                $"Instalments fall between 1 and {MaximumIntervalDays} days apart; {intervalDays} is not a schedule "
                + "anybody keeps.");
        }

        if (firstInstalmentDue < downPaymentDue)
        {
            throw new RegistryValidationException(
                $"The first instalment falls due on {firstInstalmentDue:yyyy-MM-dd}, before the arrangement is made "
                + $"on {downPaymentDue:yyyy-MM-dd}.");
        }

        var lines = new List<ScheduledInstalment>(instalmentCount + 1);
        var sequence = 0;

        if (downPayment > Money.Zero)
        {
            lines.Add(new ScheduledInstalment(++sequence, downPaymentDue, downPayment, IsDownPayment: true));
        }

        var spread = balance - downPayment;

        // TRUNCATED, not rounded. Rounding half away from zero could make the equal instalments add
        // up to MORE than the balance, and the last line would then have to be negative to reconcile
        // — a credit in the middle of a promise to pay. Truncating guarantees the remainder is
        // positive and lands, whole, on the last line.
        var each = decimal.Truncate(spread / instalmentCount * 100m) / 100m;

        for (var index = 1; index <= instalmentCount; index++)
        {
            var amount = index == instalmentCount

                // The LAST line is what is left, never the computed figure again. This is the one
                // subtraction that makes the schedule add up to the balance by construction rather
                // than by luck of the division.
                ? spread - (each * (instalmentCount - 1))
                : each;

            lines.Add(new ScheduledInstalment(
                ++sequence,
                firstInstalmentDue.AddDays(intervalDays * (index - 1)),
                amount,
                IsDownPayment: false));
        }

        // Every line has to be worth collecting. A balance of two cents over three instalments would
        // otherwise schedule 0.00, 0.00 and 0.02, and a due date with nothing due on it is a date a
        // customer can miss.
        if (lines.Any(line => line.Amount <= Money.Zero))
        {
            throw new RegistryValidationException(
                $"{balance - downPayment:0.00} over {instalmentCount} instalments leaves at least one of them at "
                + "nothing. Spread it over fewer.");
        }

        return lines;
    }
}

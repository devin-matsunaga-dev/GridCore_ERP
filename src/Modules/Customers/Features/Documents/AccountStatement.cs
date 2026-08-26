using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Documents;

/// <summary>
/// What one line of a statement reports. The order is the order lines of the same date sort in.
/// </summary>
/// <remarks>
/// A bill lands before the correction that reduces it and before the payment that settles it, which
/// is the order they can happen in — a payment cannot precede the bill it pays. Deposit movements
/// sort last on their day: they are the ones that do not move what is owed, so a reader following
/// the balance column down the page is not interrupted by them.
/// </remarks>
public enum StatementEntryKind
{
    /// <summary>A bill went out. What it printed is added to what the customer owes.</summary>
    BillIssued = 1,

    /// <summary>A bill was corrected after it was issued — a credit off it, or a charge on to it.</summary>
    BillCorrected = 2,

    /// <summary>A bill was withdrawn, taking back what was still owed on it.</summary>
    BillWithdrawn = 3,

    /// <summary>Money arrived and settled against a bill.</summary>
    PaymentReceived = 4,

    /// <summary>Held deposit was put against a bill, reducing what is owed and what is held.</summary>
    DepositApplied = 5,

    /// <summary>A deposit was taken. It moves what the utility holds, never what is owed.</summary>
    DepositCollected = 6,

    /// <summary>A deposit was given back. The same, in reverse.</summary>
    DepositRefunded = 7,

    /// <summary>
    /// A held deposit was carried between two of the customer's service accounts on a transfer
    /// (WP-2.15). Moves <b>neither</b> balance — it is on the statement to say the deposit survived
    /// the move, not to change a figure.
    /// </summary>
    /// <remarks>
    /// The one line on a statement that is purely a statement of fact, and the one the composer's
    /// proof was written for: a movement given zero effect on a column has to be carried forward on
    /// both running totals anyway, or the balance printed against the last line stops agreeing with
    /// the closing balance. See <c>AccountStatement.Compose</c>.
    /// </remarks>
    DepositTransferred = 8,
}

/// <summary>
/// One dated fact, before it is placed on a statement — what the three registers hand over.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two signed columns, because a statement tracks two balances.</b> <see cref="Amount"/> is the
/// effect on what the customer <i>owes</i>; <see cref="DepositAmount"/> is the effect on what the
/// utility <i>holds</i> for them. Most movements touch one of the two. An application touches both,
/// in the same direction, which is exactly what applying a deposit to a bill is; a collection
/// touches only the second, which is why putting it in the balance column would make a statement
/// claim that paying a deposit settled a bill.
/// </para>
/// <para>
/// <see cref="OccurredAt"/> is kept beside <see cref="Date"/> so two movements on one day sort in the
/// order they happened rather than by whichever the database returned first. A bill has no time of
/// day — it was issued on a date — so it takes midnight and sorts ahead of everything that has one,
/// which is the truth: the bill existed before anything could be done about it.
/// </para>
/// </remarks>
/// <param name="Date">The day it lands on the statement.</param>
/// <param name="OccurredAt">When it happened, for ordering within the day.</param>
/// <param name="Kind">What kind of movement it is.</param>
/// <param name="Description">What the line says, in the customer's terms.</param>
/// <param name="Reference">The number a customer can quote — a bill number, a payment number.</param>
/// <param name="Amount">Signed effect on what is owed. Zero for a movement that does not touch it.</param>
/// <param name="DepositAmount">Signed effect on the deposit held. Zero for a movement that does not touch it.</param>
/// <param name="Currency">ISO 4217 code the amounts are expressed in.</param>
/// <param name="BillId">The bill this concerns, where there is one.</param>
/// <param name="PaymentId">The payment this concerns, where there is one.</param>
/// <param name="DepositEntryId">The deposit ledger entry this concerns, where there is one.</param>
/// <param name="ServiceAccountId">The account it belongs to, where it belongs to one.</param>
/// <param name="AccountNumber">That account's number, as printed.</param>
public sealed record StatementMovement(
    DateOnly Date,
    DateTimeOffset OccurredAt,
    StatementEntryKind Kind,
    string Description,
    string? Reference,
    decimal Amount,
    decimal DepositAmount,
    string Currency,
    Guid? BillId = null,
    Guid? PaymentId = null,
    Guid? DepositEntryId = null,
    Guid? ServiceAccountId = null,
    string? AccountNumber = null);

/// <summary>One line of a statement: a movement with the two balances it left behind.</summary>
/// <param name="Date">The day it lands on.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="Kind">What kind of movement it is.</param>
/// <param name="Description">What the line says.</param>
/// <param name="Reference">The number a customer can quote.</param>
/// <param name="Amount">Signed effect on what is owed.</param>
/// <param name="DepositAmount">Signed effect on the deposit held.</param>
/// <param name="BalanceAfter">What was owed once this line was applied.</param>
/// <param name="DepositHeldAfter">What was held once it was.</param>
/// <param name="BillId">The bill this concerns, where there is one.</param>
/// <param name="PaymentId">The payment this concerns, where there is one.</param>
/// <param name="DepositEntryId">The deposit ledger entry this concerns, where there is one.</param>
/// <param name="ServiceAccountId">The account it belongs to.</param>
/// <param name="AccountNumber">That account's number.</param>
public sealed record StatementEntry(
    DateOnly Date,
    DateTimeOffset OccurredAt,
    StatementEntryKind Kind,
    string Description,
    string? Reference,
    decimal Amount,
    decimal DepositAmount,
    decimal BalanceAfter,
    decimal DepositHeldAfter,
    Guid? BillId,
    Guid? PaymentId,
    Guid? DepositEntryId,
    Guid? ServiceAccountId,
    string? AccountNumber);

/// <summary>
/// A customer's account statement over a date range: an opening balance, every movement in between,
/// and a closing balance that proves out (WP-2.14).
/// </summary>
/// <remarks>
/// <para>
/// <b>Composed, never stored.</b> A statement is a view of records that already exist in three
/// registers; storing a copy would create a fourth document able to disagree with all of them. What
/// is stored is the audit entry saying one was produced, whose snapshot carries the range and the
/// figures — enough to reproduce it, which is what makes keeping the file unnecessary.
/// </para>
/// <para>
/// <b>The opening balance is derived, not looked up.</b> There is no stored "balance as at" anywhere
/// in GridCore, and there should not be: what a customer owed on a given morning is what every
/// movement before it adds up to. That is why the seams this reads hand over a whole history rather
/// than a window — a statement whose opening balance was built from the recent movements would prove
/// out against itself and still be wrong, which is the worst of both.
/// </para>
/// <para>
/// <b>Two balances, and only one of them is money owed.</b> A deposit collection is a liability the
/// utility takes on, not a payment; folding it into the balance column would make a statement say
/// that paying a deposit settled a bill. So deposits run in their own column, and an application —
/// the one movement that is both — appears in both.
/// </para>
/// </remarks>
/// <param name="CustomerId">Whose statement.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="CustomerName">Their name, as it stands today.</param>
/// <param name="MailingAddress">Where the utility posts to, on one line — the bill-to address WP-2.11 maintains.</param>
/// <param name="From">First day of the range, inclusive.</param>
/// <param name="To">Last day of it, inclusive.</param>
/// <param name="Currency">ISO 4217 code every figure is expressed in.</param>
/// <param name="OpeningBalance">What was owed at the start of the first day.</param>
/// <param name="ClosingBalance">What was owed at the end of the last — the opening balance plus every line.</param>
/// <param name="OpeningDepositHeld">What the utility held at the start.</param>
/// <param name="ClosingDepositHeld">What it held at the end.</param>
/// <param name="Entries">Every movement in the range, oldest first.</param>
/// <param name="Billed">What was billed in the range.</param>
/// <param name="Corrected">The signed sum of corrections made in it.</param>
/// <param name="Paid">What was paid in it — cash, card and transfers that settled.</param>
/// <param name="DepositApplied">How much held deposit was put against bills in it.</param>
/// <param name="IsTruncated">Whether a register answered with as many rows as it was asked for, so the history behind the opening balance may be short.</param>
/// <param name="ProducedAt">When the statement was produced.</param>
/// <param name="ProducedById">Subject id of whoever produced it.</param>
/// <param name="ProducedByName">Their display name at the time.</param>
public sealed record AccountStatement(
    Guid CustomerId,
    string AccountNumber,
    string CustomerName,
    string? MailingAddress,
    DateOnly From,
    DateOnly To,
    string Currency,
    decimal OpeningBalance,
    decimal ClosingBalance,
    decimal OpeningDepositHeld,
    decimal ClosingDepositHeld,
    IReadOnlyList<StatementEntry> Entries,
    decimal Billed,
    decimal Corrected,
    decimal Paid,
    decimal DepositApplied,
    bool IsTruncated,
    DateTimeOffset ProducedAt,
    string ProducedById,
    string? ProducedByName)
{
    /// <summary>
    /// Composes a statement from every movement on the account up to and including <paramref name="to"/>.
    /// </summary>
    /// <param name="header">Who the statement is for and who produced it.</param>
    /// <param name="movements">
    /// Every movement up to <paramref name="to"/> — the ones before <paramref name="from"/> included,
    /// because they are what the opening balance is made of. Order does not matter; this sorts them.
    /// </param>
    /// <param name="from">First day of the range.</param>
    /// <param name="to">Last day of it.</param>
    /// <param name="isTruncated">Whether a register answered with as many rows as it was asked for.</param>
    /// <exception cref="RegistryValidationException">
    /// The range runs backwards, a movement falls after it, a figure is finer than a cent, or two
    /// currencies are mixed.
    /// </exception>
    public static AccountStatement Compose(
        StatementHeader header,
        IReadOnlyList<StatementMovement> movements,
        DateOnly from,
        DateOnly to,
        bool isTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(movements);

        if (to < from)
        {
            throw new RegistryValidationException(
                $"A statement cannot end on {to:yyyy-MM-dd}, before it starts on {from:yyyy-MM-dd}.");
        }

        var currency = Currencies(movements, header.DefaultCurrency);

        foreach (var movement in movements)
        {
            // Refused rather than trimmed to the range. A movement after the last day means the
            // caller fetched a wider window than it asked for, and every figure below — the closing
            // balance most of all — would be a period's worth of activity out.
            if (movement.Date > to)
            {
                throw new RegistryValidationException(
                    $"A statement to {to:yyyy-MM-dd} was handed a movement dated {movement.Date:yyyy-MM-dd}.");
            }

            if (!Money.IsRounded(movement.Amount) || !Money.IsRounded(movement.DepositAmount))
            {
                throw new RegistryValidationException(
                    $"A statement is stated to the cent; '{movement.Description}' carries {movement.Amount} / {movement.DepositAmount}.");
            }
        }

        // Sorted once, and read twice: everything before the range makes the opening balance, and
        // everything in it makes the lines. Sorting the whole set rather than the range means the two
        // running balances carry forward through movements the customer never sees, which is what an
        // opening balance IS.
        var ordered = movements
            .OrderBy(movement => movement.Date)
            .ThenBy(movement => movement.OccurredAt)
            .ThenBy(movement => movement.Kind)
            .ThenBy(movement => movement.Reference, StringComparer.Ordinal)
            .ToList();

        var balance = Money.Zero;
        var deposit = Money.Zero;
        var opening = Money.Zero;
        var openingDeposit = Money.Zero;
        var entries = new List<StatementEntry>();

        foreach (var movement in ordered)
        {
            if (movement.Date < from)
            {
                balance += movement.Amount;
                deposit += movement.DepositAmount;
                opening = balance;
                openingDeposit = deposit;

                continue;
            }

            balance += movement.Amount;
            deposit += movement.DepositAmount;

            entries.Add(new StatementEntry(
                movement.Date,
                movement.OccurredAt,
                movement.Kind,
                movement.Description,
                movement.Reference,
                movement.Amount,
                movement.DepositAmount,
                balance,
                deposit,
                movement.BillId,
                movement.PaymentId,
                movement.DepositEntryId,
                movement.ServiceAccountId,
                movement.AccountNumber));
        }

        // THE PROOF, and it is not the tautology it looks like. The closing balance is the running
        // total; the right-hand side is the balance printed against the last line a customer can
        // see. They come apart the moment a movement is given a zero effect on one column and a real
        // one on the other and the running totals are not both carried forward — which is exactly
        // the shape of a deposit collection, the movement this statement most easily gets wrong.
        if (entries.Count > 0 && (entries[^1].BalanceAfter != balance || entries[^1].DepositHeldAfter != deposit))
        {
            throw new RegistryValidationException(
                $"A statement for {header.AccountNumber} closes at {balance} but its last line reads {entries[^1].BalanceAfter}. "
                + "A statement whose printed balance disagrees with its own arithmetic is not produced.");
        }

        return new AccountStatement(
            header.CustomerId,
            header.AccountNumber,
            header.CustomerName,
            header.MailingAddress,
            from,
            to,
            currency,
            opening,
            balance,
            openingDeposit,
            deposit,
            entries,
            Money.Total(entries.Where(entry => entry.Kind is StatementEntryKind.BillIssued).Select(entry => entry.Amount)),
            Money.Total(entries.Where(entry => entry.Kind is StatementEntryKind.BillCorrected).Select(entry => entry.Amount)),

            // Positive, though the entries are negative: "paid in this period" is a figure a customer
            // reads as an amount, not as a movement, and a summary line reading -240.00 beside
            // "Payments" is one somebody has to stop and think about.
            -Money.Total(entries.Where(entry => entry.Kind is StatementEntryKind.PaymentReceived).Select(entry => entry.Amount)),
            -Money.Total(entries.Where(entry => entry.Kind is StatementEntryKind.DepositApplied).Select(entry => entry.Amount)),
            isTruncated,
            header.ProducedAt,
            header.ProducedById,
            header.ProducedByName);
    }

    /// <summary>
    /// The one currency every movement is in, or the fallback when there are none.
    /// </summary>
    /// <exception cref="RegistryValidationException">
    /// Two currencies are mixed. A statement adds its lines up, and adding dollars to euros produces
    /// a closing balance that means nothing — refused rather than reported in whichever currency
    /// happened to come first.
    /// </exception>
    private static string Currencies(IReadOnlyList<StatementMovement> movements, string fallback)
    {
        var currencies = movements
            .Select(movement => movement.Currency)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return currencies.Count switch
        {
            0 => fallback,
            1 => currencies[0],
            _ => throw new RegistryValidationException(
                $"A statement cannot be produced across {string.Join(" and ", currencies)}: its lines would not add up."),
        };
    }
}

/// <summary>
/// Who a statement is for and who produced it — everything on it that is not a movement.
/// </summary>
/// <param name="CustomerId">Whose statement.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="CustomerName">Their name as it stands today.</param>
/// <param name="MailingAddress">Where the utility posts to, on one line.</param>
/// <param name="DefaultCurrency">What to report in when there is no activity to read one from.</param>
/// <param name="ProducedAt">When the statement was produced.</param>
/// <param name="ProducedById">Subject id of whoever produced it.</param>
/// <param name="ProducedByName">Their display name at the time.</param>
public sealed record StatementHeader(
    Guid CustomerId,
    string AccountNumber,
    string CustomerName,
    string? MailingAddress,
    string DefaultCurrency,
    DateTimeOffset ProducedAt,
    string ProducedById,
    string? ProducedByName);

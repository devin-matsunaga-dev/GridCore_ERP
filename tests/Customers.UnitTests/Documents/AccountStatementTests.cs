using GridCore.Modules.Customers.Features.Documents;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Documents;

/// <summary>
/// The statement's arithmetic (WP-2.14), with no database anywhere near it.
/// </summary>
/// <remarks>
/// The claim under test is the one the whole document rests on: <b>opening balance plus everything
/// in the range equals the closing balance</b>, and a deposit collection does not move it. Both are
/// pure functions of a list of movements, which is why they are argued with in milliseconds.
/// </remarks>
public class AccountStatementTests
{
    private static readonly DateOnly From = new(2026, 7, 1);
    private static readonly DateOnly To = new(2026, 7, 31);

    private static readonly StatementHeader Header = new(
        Guid.CreateVersion7(),
        "C-000001",
        "Ana Cruz",
        "12 Beach Road, Songsong, Rota",
        "USD",
        new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero),
        "auth0|clerk",
        "Bea Santos");

    private static DateTimeOffset At(DateOnly day, int hour = 9) =>
        new(day.ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero);

    private static StatementMovement Bill(DateOnly day, decimal amount, string number = "BIL-000001") =>
        new(day, At(day, 0), StatementEntryKind.BillIssued, $"Bill {number}", number, amount, 0m, "USD");

    private static StatementMovement Payment(DateOnly day, decimal amount, string number = "PAY-000001") =>
        new(day, At(day), StatementEntryKind.PaymentReceived, $"Payment {number}", number, -amount, 0m, "USD");

    private static StatementMovement Correction(DateOnly day, decimal signed, string number = "BIL-000001") =>
        new(day, At(day, 10), StatementEntryKind.BillCorrected, "Credit", number, signed, 0m, "USD");

    private static StatementMovement DepositTaken(DateOnly day, decimal amount) =>
        new(day, At(day, 11), StatementEntryKind.DepositCollected, "Security deposit received", null, 0m, amount, "USD");

    private static StatementMovement DepositUsed(DateOnly day, decimal amount, string number = "BIL-000001") =>
        new(day, At(day, 12), StatementEntryKind.DepositApplied, $"Deposit applied to bill {number}", number, -amount, -amount, "USD");

    [Fact]
    public void Opening_plus_activity_equals_closing()
    {
        // THE PACKAGE'S HEADLINE CLAIM. Two bills and a payment before the range make the opening
        // balance; the range's own movements take it from there.
        var statement = AccountStatement.Compose(
            Header,
            [
                Bill(new DateOnly(2026, 5, 5), 100.00m, "BIL-000001"),
                Bill(new DateOnly(2026, 6, 5), 120.00m, "BIL-000002"),
                Payment(new DateOnly(2026, 6, 20), 100.00m, "PAY-000001"),
                Bill(new DateOnly(2026, 7, 5), 130.00m, "BIL-000003"),
                Payment(new DateOnly(2026, 7, 20), 120.00m, "PAY-000002"),
            ],
            From,
            To);

        Assert.Equal(120.00m, statement.OpeningBalance);
        Assert.Equal(130.00m, statement.ClosingBalance);
        Assert.Equal(statement.OpeningBalance + statement.Entries.Sum(entry => entry.Amount), statement.ClosingBalance);

        Assert.Equal(130.00m, statement.Billed);
        Assert.Equal(120.00m, statement.Paid);
    }

    [Fact]
    public void A_range_with_no_activity_is_a_valid_statement_that_carries_the_balance_across()
    {
        // Not an error and not an empty document. "You owed 120.00 at the start of July, nothing
        // happened, you owe 120.00 now" is a complete answer — and it is the one a customer chasing
        // a missing bill is ringing about.
        var statement = AccountStatement.Compose(Header, [Bill(new DateOnly(2026, 6, 5), 120.00m)], From, To);

        Assert.Empty(statement.Entries);
        Assert.Equal(120.00m, statement.OpeningBalance);
        Assert.Equal(120.00m, statement.ClosingBalance);
        Assert.Equal(0m, statement.Billed);
        Assert.Equal(0m, statement.Paid);
    }

    [Fact]
    public void An_account_that_has_never_moved_opens_and_closes_at_nothing()
    {
        var statement = AccountStatement.Compose(Header, [], From, To);

        Assert.Empty(statement.Entries);
        Assert.Equal(0m, statement.OpeningBalance);
        Assert.Equal(0m, statement.ClosingBalance);

        // The header's fallback, because there is no activity to read a currency off.
        Assert.Equal("USD", statement.Currency);
    }

    [Fact]
    public void A_DEPOSIT_COLLECTION_moves_what_is_held_and_NOT_what_is_owed()
    {
        // The movement this statement most easily gets wrong. A deposit is a liability the utility
        // takes on, not a payment; putting it in the balance column would tell a customer their
        // deposit had settled a bill.
        var statement = AccountStatement.Compose(
            Header,
            [Bill(new DateOnly(2026, 7, 5), 120.00m), DepositTaken(new DateOnly(2026, 7, 6), 75.00m)],
            From,
            To);

        Assert.Equal(120.00m, statement.ClosingBalance);
        Assert.Equal(75.00m, statement.ClosingDepositHeld);

        var deposit = statement.Entries.Single(entry => entry.Kind is StatementEntryKind.DepositCollected);

        Assert.Equal(0m, deposit.Amount);
        Assert.Equal(75.00m, deposit.DepositAmount);

        // And the balance column carries straight through it rather than resetting.
        Assert.Equal(120.00m, deposit.BalanceAfter);
    }

    [Fact]
    public void A_DEPOSIT_APPLICATION_moves_BOTH_columns()
    {
        // The one movement that is both: the utility spends money it holds to settle money it is
        // owed. Anything else would make the two columns disagree about where the money went.
        var statement = AccountStatement.Compose(
            Header,
            [
                DepositTaken(new DateOnly(2026, 6, 1), 75.00m),
                Bill(new DateOnly(2026, 7, 5), 120.00m),
                DepositUsed(new DateOnly(2026, 7, 10), 75.00m),
            ],
            From,
            To);

        Assert.Equal(45.00m, statement.ClosingBalance);
        Assert.Equal(0m, statement.ClosingDepositHeld);
        Assert.Equal(75.00m, statement.OpeningDepositHeld);
        Assert.Equal(75.00m, statement.DepositApplied);
    }

    [Fact]
    public void A_bill_sorts_ahead_of_everything_that_happened_to_it_on_the_same_day()
    {
        // Nothing can be done about a bill before it exists, so a bill takes midnight and sorts
        // first. A statement that showed a payment above the bill it settled would be one a customer
        // reads twice and then rings about.
        var day = new DateOnly(2026, 7, 15);

        var statement = AccountStatement.Compose(
            Header,
            [Payment(day, 50.00m), DepositTaken(day, 75.00m), Bill(day, 120.00m), Correction(day, -10.00m)],
            From,
            To);

        Assert.Equal(
            [
                StatementEntryKind.BillIssued,
                StatementEntryKind.PaymentReceived,
                StatementEntryKind.BillCorrected,
                StatementEntryKind.DepositCollected,
            ],
            statement.Entries.Select(entry => entry.Kind));

        // And the running balance follows the same order down the page: 120 - 50 - 10.
        Assert.Equal([120.00m, 70.00m, 60.00m, 60.00m], statement.Entries.Select(entry => entry.BalanceAfter));
    }

    [Fact]
    public void A_correction_lands_on_the_day_it_was_MADE_not_the_day_the_bill_went_out()
    {
        // WP-2.4's rule read forwards: a credit is its own dated entry, never a rewrite of the
        // charge. A statement for July shows a June bill's July credit and nothing of the bill.
        var statement = AccountStatement.Compose(
            Header,
            [Bill(new DateOnly(2026, 6, 5), 120.00m), Correction(new DateOnly(2026, 7, 8), -20.00m)],
            From,
            To);

        var entry = Assert.Single(statement.Entries);

        Assert.Equal(StatementEntryKind.BillCorrected, entry.Kind);
        Assert.Equal(120.00m, statement.OpeningBalance);
        Assert.Equal(100.00m, statement.ClosingBalance);
        Assert.Equal(-20.00m, statement.Corrected);
    }

    [Fact]
    public void A_range_that_runs_backwards_is_refused()
    {
        var error = Assert.Throws<RegistryValidationException>(() =>
            AccountStatement.Compose(Header, [], To, From));

        Assert.Contains("before it starts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_movement_after_the_last_day_is_refused()
    {
        // THE FAILURE PATH FOR AN OVER-FETCHING CALLER. Trimming it silently would leave every figure
        // below — the closing balance most of all — a period's worth of activity out.
        var error = Assert.Throws<RegistryValidationException>(() =>
            AccountStatement.Compose(Header, [Bill(new DateOnly(2026, 8, 5), 120.00m)], From, To));

        Assert.Contains("2026-08-05", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_currencies_are_refused_rather_than_added_together()
    {
        var euros = Bill(new DateOnly(2026, 7, 5), 120.00m) with { Currency = "EUR" };

        var error = Assert.Throws<RegistryValidationException>(() =>
            AccountStatement.Compose(Header, [Bill(new DateOnly(2026, 7, 6), 120.00m), euros], From, To));

        Assert.Contains("would not add up", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_figure_finer_than_a_cent_is_refused()
    {
        var error = Assert.Throws<RegistryValidationException>(() =>
            AccountStatement.Compose(Header, [Bill(new DateOnly(2026, 7, 5), 120.005m)], From, To));

        Assert.Contains("to the cent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_statement_carries_who_it_is_for_and_who_produced_it()
    {
        var statement = AccountStatement.Compose(Header, [], From, To);

        Assert.Equal("C-000001", statement.AccountNumber);
        Assert.Equal("Ana Cruz", statement.CustomerName);
        Assert.Equal("12 Beach Road, Songsong, Rota", statement.MailingAddress);
        Assert.Equal("auth0|clerk", statement.ProducedById);
        Assert.Equal("Bea Santos", statement.ProducedByName);
        Assert.False(statement.IsTruncated);
    }
}

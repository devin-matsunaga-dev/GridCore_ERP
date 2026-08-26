using GridCore.Contracts.Directories;
using GridCore.Contracts.Providers;
using GridCore.Modules.Payments.Features.Payments;
using GridCore.Modules.Payments.UnitTests.Infrastructure;

namespace GridCore.Modules.Payments.UnitTests.Payments;

/// <summary>
/// The payment register as another module sees it — WP-2.13's seam, over a real EF model on SQLite
/// in-memory.
/// </summary>
/// <remarks>
/// <para>
/// What these pin is the pair of things a double cannot: the projection is translated to SQL rather
/// than materialising a <see cref="Payment"/> first, and <c>IsSettled</c> means the same here as it
/// means inside the module. The second is the one that would rot quietly —
/// <c>PaymentTransitions.IsSettled</c> is a method call no provider can turn into SQL, so the
/// directory restates the rule as a status comparison, and two statements of one rule is exactly the
/// shape that drifts.
/// </para>
/// <para>
/// <c>FakePaymentDirectory</c> in the Customers fast tier answers the same questions the same way. A
/// double that behaved differently would let a rule pass there and fail against this.
/// </para>
/// </remarks>
public sealed class PaymentDirectoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly PaymentsTestHost _host;

    public PaymentDirectoryTests() => _host = new PaymentsTestHost(_clock, new FakeCurrentUser("clerk-1", "Ana Reyes"));

    public void Dispose() => _host.Dispose();

    /// <summary>
    /// Takes a payment, a minute after the last one.
    /// </summary>
    /// <remarks>
    /// The clock advance is load-bearing: a payment's id is a Guid v7 stamped from the clock, so two
    /// taken inside one frozen millisecond have no defined order, and this register is ordered by
    /// key. The trap STATUS.md has warned about since WP-0.5.
    /// </remarks>
    private async Task<Payment> TakeAsync(BillSummary bill, decimal amount = 50.00m, PaymentOutcome? outcome = null)
    {
        _clock.Advance(TimeSpan.FromMinutes(1));

        if (outcome is { } answer)
        {
            _host.Provider.WillAnswer(answer);
        }

        var result = await _host.WithPaymentsAsync(register =>
            register.TakeAsync(new TakePaymentInput(bill.Id, amount, PaymentMethods.Card, "•••• 4242")));

        return result.Payment;
    }

    [Fact]
    public async Task One_payment_comes_back_as_the_facts_another_module_is_allowed_to_see()
    {
        var (account, bill) = _host.AnOutstandingBill(amountDue: 120.00m);
        var taken = await TakeAsync(bill, 50.00m);

        var found = await _host.WithDirectoryAsync(directory => directory.FindAsync(taken.Id));

        var summary = Assert.IsType<PaymentSummary>(found);

        Assert.Equal(taken.Id, summary.Id);
        Assert.Equal(taken.PaymentNumber, summary.PaymentNumber);
        Assert.Equal(account.CustomerId, summary.CustomerId);
        Assert.Equal(account.Id, summary.ServiceAccountId);
        Assert.Equal(bill.Id, summary.BillId);
        Assert.Equal(50.00m, summary.Amount);
        Assert.Equal(bill.Currency, summary.Currency);
        Assert.Equal(taken.RequestedAt, summary.RequestedAt);
    }

    [Fact]
    public async Task The_status_crosses_the_boundary_BY_NAME()
    {
        var (_, bill) = _host.AnOutstandingBill();
        var taken = await TakeAsync(bill);

        var summary = await _host.WithDirectoryAsync(directory => directory.FindAsync(taken.Id));

        // Contracts takes no dependency on this module's enum, so what crosses is the name. A
        // consumer comparing against "Approved" must keep working when a member is added here.
        Assert.Equal(taken.Status.ToString(), summary!.Status);
    }

    [Theory]
    [InlineData(PaymentOutcome.Approved, true)]
    [InlineData(PaymentOutcome.Declined, false)]
    [InlineData(PaymentOutcome.InsufficientFunds, false)]
    [InlineData(PaymentOutcome.Timeout, false)]
    public async Task IsSettled_says_the_same_thing_the_module_says(PaymentOutcome outcome, bool isSettled)
    {
        // The assertion that keeps the two statements of one rule from drifting: the directory has to
        // express "settled" as SQL, so it cannot call PaymentTransitions.IsSettled and has to restate
        // it. Both are checked against each other here, over every outcome the sandbox can give.
        var (_, bill) = _host.AnOutstandingBill();
        var taken = await TakeAsync(bill, outcome: outcome);

        var summary = await _host.WithDirectoryAsync(directory => directory.FindAsync(taken.Id));

        Assert.Equal(isSettled, summary!.IsSettled);
        Assert.Equal(taken.IsSettled, summary.IsSettled);
    }

    [Fact]
    public async Task An_id_that_matches_nothing_is_null_rather_than_an_error() =>
        // A note being written names a payment that may not exist; the caller turns that into its own
        // 400 with its own words, which it cannot do if this throws.
        Assert.Null(await _host.WithDirectoryAsync(directory => directory.FindAsync(Guid.CreateVersion7())));

    [Fact]
    public async Task A_batch_comes_back_keyed_by_id_with_the_misses_simply_absent()
    {
        var (_, bill) = _host.AnOutstandingBill(amountDue: 500.00m);

        var first = await TakeAsync(bill, 10.00m);
        var second = await TakeAsync(bill, 20.00m);
        var missing = Guid.CreateVersion7();

        var found = await _host.WithDirectoryAsync(directory =>
            directory.FindManyAsync([first.Id, second.Id, missing]));

        // Ids that match nothing are absent rather than null-valued: a caller rendering a list has to
        // cope with one it cannot resolve anyway, which is the rule every directory in Contracts
        // follows.
        Assert.Equal([first.Id, second.Id], found.Keys.Order());
        Assert.Equal(first.PaymentNumber, found[first.Id].PaymentNumber);
    }

    [Fact]
    public async Task A_batch_asking_for_the_same_id_twice_answers_once()
    {
        var (_, bill) = _host.AnOutstandingBill();
        var taken = await TakeAsync(bill);

        var found = await _host.WithDirectoryAsync(directory => directory.FindManyAsync([taken.Id, taken.Id, taken.Id]));

        // Distinct before the query, as every other directory does — the answer is keyed by id, so a
        // page of notes about one payment must not send it a dozen times.
        Assert.Equal(taken.Id, Assert.Single(found).Key);
    }

    [Fact]
    public async Task An_empty_batch_asks_the_database_nothing() =>
        Assert.Empty(await _host.WithDirectoryAsync(directory => directory.FindManyAsync([])));
}

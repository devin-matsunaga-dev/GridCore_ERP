using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Deposits;

/// <summary>
/// The deposit ledger's arithmetic, with no database and no service in the way.
/// </summary>
/// <remarks>
/// This is where WP-2.12's central rule is pinned: the balance is the entries, and neither can move
/// without the other. The exactness assertions are in <see langword="decimal"/> on purpose — a
/// deposit collected, part-applied and refunded is the sequence a <see langword="double"/> would
/// leave a fraction of a cent behind on, and it would first be noticed in a trial balance.
/// </remarks>
public class DepositEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Cashier = new("auth0|cs-agent", "Ana Cruz");
    private static readonly Guid ABill = Guid.CreateVersion7(Now);
    private static readonly Guid AnAccount = Guid.CreateVersion7(Now);

    private static Customer ARegisteredCustomer() =>
        Customer.Register("C-000001", "Sablan Family Residence", CustomerClass.Residential, Now);

    private static DepositEntry Collect(Customer customer, decimal amount, bool interestBearing = false) =>
        DepositEntry.Collect(customer, amount, DepositRules.Currency, interestBearing, null, Cashier, Now);

    private static DepositEntry Apply(Customer customer, decimal amount) =>
        DepositEntry.Apply(customer, ABill, "BIL-000001", AnAccount, amount, DepositRules.Currency, null, Cashier, Now);

    private static DepositEntry Refund(Customer customer, decimal amount, string? reason = null) =>
        DepositEntry.Refund(customer, amount, DepositRules.Currency, reason, Cashier, Now);

    [Fact]
    public void Collecting_a_deposit_holds_it_against_the_customer()
    {
        var customer = ARegisteredCustomer();

        var entry = Collect(customer, 75.00m);

        Assert.Equal(DepositEntryKind.Collected, entry.Kind);
        Assert.Equal(75.00m, entry.Amount);
        Assert.Equal(75.00m, entry.BalanceAfter);
        Assert.Equal(75.00m, customer.DepositHeld);

        // The entry and the projection are moved in one act, which is the invariant this type
        // exists for: neither can be written without the other.
        Assert.Equal(entry.BalanceAfter, customer.DepositHeld);
    }

    [Fact]
    public void Collect_then_apply_then_refund_is_exact_to_the_cent()
    {
        // WORK_PACKAGES.md: "collect → hold → apply → refund arithmetic is exact in decimal".
        // The figures are deliberately awkward — a third of a bill, an odd remainder — because the
        // round numbers are the ones a float would also get right.
        var customer = ARegisteredCustomer();

        Assert.Equal(120.55m, Collect(customer, 120.55m).BalanceAfter);
        Assert.Equal(80.22m, Apply(customer, 40.33m).BalanceAfter);
        Assert.Equal(30.15m, Apply(customer, 50.07m).BalanceAfter);
        Assert.Equal(0m, Refund(customer, 30.15m).BalanceAfter);

        Assert.Equal(0m, customer.DepositHeld);
    }

    [Fact]
    public void A_kind_carries_the_direction_so_an_amount_is_never_signed()
    {
        var customer = ARegisteredCustomer();

        var collected = Collect(customer, 100.00m);
        var applied = Apply(customer, 30.00m);
        var refunded = Refund(customer, 20.00m);

        // Every stored amount is a positive magnitude; the sign lives in the kind.
        Assert.All([collected, applied, refunded], entry => Assert.True(entry.Amount > 0m));

        Assert.Equal(100.00m, collected.SignedAmount);
        Assert.Equal(-30.00m, applied.SignedAmount);
        Assert.Equal(-20.00m, refunded.SignedAmount);

        // And the balance is exactly those signed movements added up.
        Assert.Equal(
            collected.SignedAmount + applied.SignedAmount + refunded.SignedAmount,
            customer.DepositHeld);
    }

    [Fact]
    public void A_refund_larger_than_the_held_balance_is_refused()
    {
        // Failure path, and the WP's own words: "a refund cannot exceed the held balance". The
        // utility cannot hand over money it is not holding.
        var customer = ARegisteredCustomer();

        Collect(customer, 75.00m);

        var refused = Assert.Throws<RegistryWorkflowException>(() => Refund(customer, 75.01m));

        Assert.Contains("75.00", refused.Message, StringComparison.Ordinal);
        Assert.Equal(75.00m, customer.DepositHeld);
    }

    [Fact]
    public void An_application_larger_than_the_held_balance_is_refused_by_the_same_guard()
    {
        // One guard covering every kind that takes money out, rather than a rule per kind that a
        // fourth kind could be added without.
        var customer = ARegisteredCustomer();

        Collect(customer, 40.00m);

        Assert.Throws<RegistryWorkflowException>(() => Apply(customer, 40.01m));
        Assert.Equal(40.00m, customer.DepositHeld);
    }

    [Fact]
    public void A_refused_movement_leaves_the_customer_exactly_as_it_was()
    {
        // The guards run before either mutation, so a rejected movement cannot leave a balance
        // moved with no entry behind it — which is the one state this ledger must never reach.
        var customer = ARegisteredCustomer();

        Collect(customer, 75.00m);

        Assert.Throws<RegistryValidationException>(() => Collect(customer, 0m));
        Assert.Throws<RegistryValidationException>(() => Collect(customer, 10.005m));
        Assert.Throws<RegistryWorkflowException>(() => Refund(customer, 1_000m));

        Assert.Equal(75.00m, customer.DepositHeld);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25)]
    public void A_movement_of_nothing_or_less_is_refused(decimal amount) =>
        Assert.Throws<RegistryValidationException>(() => Collect(ARegisteredCustomer(), amount));

    [Fact]
    public void A_movement_finer_than_a_cent_is_refused_rather_than_rounded()
    {
        // The column is numeric(18,2). Accepting this would round it away in the database, where
        // nobody would ever see which value was stored — the call WP-1.1 made for the deposit
        // field this ledger replaced.
        var refused = Assert.Throws<RegistryValidationException>(() => Collect(ARegisteredCustomer(), 75.125m));

        Assert.Contains("cents", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refunding_the_whole_balance_leaves_nothing_held_and_is_allowed()
    {
        var customer = ARegisteredCustomer();

        Collect(customer, 75.00m);

        Assert.Equal(0m, Refund(customer, 75.00m, "Account closed.").BalanceAfter);
        Assert.Equal(0m, customer.DepositHeld);
    }

    [Fact]
    public void An_application_records_the_bill_it_settled()
    {
        var customer = ARegisteredCustomer();

        Collect(customer, 75.00m);

        var applied = Apply(customer, 25.00m);

        // The bill number is kept on the row so the ledger reads without a cross-module lookup —
        // a deposit tab must not have to ask Billing what "BIL-000001" was to render a line.
        Assert.Equal(ABill, applied.BillId);
        Assert.Equal("BIL-000001", applied.BillNumber);
        Assert.Equal(AnAccount, applied.ServiceAccountId);
    }

    [Fact]
    public void Only_a_collection_carries_the_interest_bearing_terms()
    {
        var customer = ARegisteredCustomer();

        Assert.True(Collect(customer, 75.00m, interestBearing: true).IsInterestBearing);

        // Applying or refunding is not a holding, so there are no terms to carry. Stored and never
        // accrued either way — the MVP records the terms and computes nothing from them.
        Assert.False(Apply(customer, 10.00m).IsInterestBearing);
        Assert.False(Refund(customer, 10.00m).IsInterestBearing);
    }

    [Fact]
    public void An_application_that_names_no_bill_is_refused() =>
        // A deposit is applied TO A BILL or it is not applied at all — an entry reducing a balance
        // with nothing to point at is money that has gone somewhere unrecorded.
        Assert.Throws<RegistryValidationException>(() =>
            DepositEntry.Apply(
                ARegisteredCustomer(),
                Guid.Empty,
                "BIL-000001",
                AnAccount,
                10.00m,
                DepositRules.Currency,
                null,
                Cashier,
                Now));

    [Fact]
    public void An_entry_names_who_moved_the_money()
    {
        var entry = Collect(ARegisteredCustomer(), 75.00m);

        Assert.Equal("auth0|cs-agent", entry.ActorId);
        Assert.Equal("Ana Cruz", entry.ActorName);
        Assert.Equal(Now, entry.RecordedAt);
    }

    [Fact]
    public void An_entry_without_an_actor_is_refused() =>
        // Failure path: money moving with nobody's name on it is not a record anybody can act on.
        Assert.Throws<RegistryValidationException>(() =>
            DepositEntry.Collect(
                ARegisteredCustomer(),
                75.00m,
                DepositRules.Currency,
                false,
                null,
                new RegistryActor("  ", null),
                Now));

    [Fact]
    public void Every_declared_kind_has_a_direction() =>
        // A kind added without one would silently take the sign of whichever branch was written
        // first, and the balance would be wrong in a way no test asked about.
        Assert.All(
            Enum.GetValues<DepositEntryKind>(),
            kind => Assert.Contains(DepositEntryKinds.DirectionOf(kind), new[] { -1, 1 }));

    [Fact]
    public void A_kind_GridCore_does_not_declare_has_no_direction() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DepositEntryKinds.DirectionOf((DepositEntryKind)99));
}

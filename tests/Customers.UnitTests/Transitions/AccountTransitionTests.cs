using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Transitions;

/// <summary>
/// The transition register's own guards, with no database and no service in the way: that the reason
/// code fits the kind, that the escape hatch explains itself, and that a transfer names two
/// different accounts.
/// </summary>
/// <remarks>
/// These rules live in the aggregate rather than only in a validator so that a seeder and a later
/// in-process caller meet them too — the call <c>DepositEntry</c> already makes about a movement's
/// sign and <c>CustomerNote</c> about a follow-up date.
/// </remarks>
public class AccountTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    private static readonly RegistryActor Agent = new("auth0|cs-agent", "Ana Cruz");

    private static Customer ACustomer() =>
        Customer.Register("C-000001", "Sablan Family Residence", CustomerClass.Residential, Now);

    private static ServiceAccount AnAccount(Customer customer, string number = "A-000001") =>
        ServiceAccount.Open(number, customer.Id, Guid.CreateVersion7(Now), ServiceType.Electricity, Agent, Now);

    [Fact]
    public void A_class_change_records_both_sides_by_name()
    {
        var customer = ACustomer();

        var transition = AccountTransition.ClassChanged(
            customer,
            CustomerClass.Residential,
            CustomerClass.Commercial,
            TransitionReasonCode.PremiseNowTrading,
            "Bakery opened in the front room.",
            new DateOnly(2026, 9, 1),
            Agent,
            Now);

        Assert.Equal(AccountTransitionKind.ClassChanged, transition.Kind);
        Assert.Equal(nameof(CustomerClass.Residential), transition.FromValue);
        Assert.Equal(nameof(CustomerClass.Commercial), transition.ToValue);

        // Two different dates, and the difference is the whole point: RecordedAt is when a rep typed
        // it, EffectiveOn is when the utility says it happened.
        Assert.Equal(new DateOnly(2026, 9, 1), transition.EffectiveOn);
        Assert.Equal(Now, transition.RecordedAt);

        // No money involved, so no currency stamped: a code on a row with no figure reads as a claim.
        Assert.Equal(0m, transition.DepositCarried);
        Assert.Null(transition.Currency);
        Assert.Null(transition.DepositEntryId);
    }

    [Fact]
    public void A_move_in_has_no_before_and_a_move_out_has_no_after()
    {
        var customer = ACustomer();
        var account = AnAccount(customer);

        var movedIn = AccountTransition.MovedIn(customer, account, TransitionReasonCode.NewOccupancy, null, Today, Agent, Now);
        var movedOut = AccountTransition.MovedOut(customer, account, TransitionReasonCode.EndOfTenancy, null, Today, Agent, Now);

        // Which is what the two acts ARE: nothing was being served here, and now nothing is.
        Assert.Null(movedIn.FromValue);
        Assert.Equal("A-000001", movedIn.ToValue);
        Assert.Equal(account.Id, movedIn.ToServiceAccountId);
        Assert.Null(movedIn.FromServiceAccountId);

        Assert.Equal("A-000001", movedOut.FromValue);
        Assert.Null(movedOut.ToValue);
        Assert.Equal(account.Id, movedOut.FromServiceAccountId);
        Assert.Null(movedOut.ToServiceAccountId);
    }

    [Fact]
    public void A_transfer_names_both_accounts_on_one_row()
    {
        // ONE row, not two linked ones. "Linked" is precisely the property a pair of rows can lose —
        // one written and the other not, one read without the other — and a single row cannot half
        // exist, so the linkage needs no consistency rule to hold it together.
        var customer = ACustomer();
        var closed = AnAccount(customer, "A-000001");
        var opened = AnAccount(customer, "A-000002");

        var transition = AccountTransition.Transferred(
            customer,
            closed,
            opened,
            250.00m,
            "USD",
            Guid.CreateVersion7(Now),
            TransitionReasonCode.Relocation,
            null,
            Today,
            Agent,
            Now);

        Assert.Equal("A-000001", transition.FromValue);
        Assert.Equal("A-000002", transition.ToValue);
        Assert.Equal(closed.Id, transition.FromServiceAccountId);
        Assert.Equal(opened.Id, transition.ToServiceAccountId);
        Assert.Equal(250.00m, transition.DepositCarried);
        Assert.Equal("USD", transition.Currency);
        Assert.NotNull(transition.DepositEntryId);
    }

    [Fact]
    public void A_reason_code_that_does_not_fit_the_kind_is_refused()
    {
        // Failure path. UnpaidBalance suspends somebody; recorded against a move-in it would put a
        // sentence in the register that nobody could act on.
        var customer = ACustomer();

        var refused = Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.MovedIn(customer, AnAccount(customer), TransitionReasonCode.UnpaidBalance, null, Today, Agent, Now));

        // The message names what WOULD have been allowed, because the caller is a rep at a counter
        // and "that is not a reason" without the list is a dead end.
        Assert.Contains(nameof(TransitionReasonCode.NewOccupancy), refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reason_code_GridCore_does_not_declare_is_refused() =>
        // Failure path: a value cast from an unmapped integer would be stored by name as a number and
        // read back as nothing anyone can act on.
        Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.StatusChanged(
                ACustomer(),
                CustomerStatus.Prospect,
                CustomerStatus.Active,
                (TransitionReasonCode)99,
                null,
                Today,
                Agent,
                Now));

    [Fact]
    public void The_escape_hatch_without_a_sentence_is_refused()
    {
        // Failure path, and the rule the fixed list depends on: a list whose escape hatch may be
        // silent is a fixed list in name only.
        var customer = ACustomer();

        Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.MovedOut(customer, AnAccount(customer), TransitionReasonCode.Other, null, Today, Agent, Now));

        // Whitespace is not a sentence either — RegistryText.Clean is what decides.
        Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.MovedOut(customer, AnAccount(customer), TransitionReasonCode.Other, "   ", Today, Agent, Now));
    }

    [Fact]
    public void The_escape_hatch_with_a_sentence_is_recorded()
    {
        var customer = ACustomer();

        var transition = AccountTransition.MovedOut(
            customer,
            AnAccount(customer),
            TransitionReasonCode.Other,
            "Premises condemned after the storm; nobody to transfer to.",
            Today,
            Agent,
            Now);

        Assert.Equal("Premises condemned after the storm; nobody to transfer to.", transition.Notes);
    }

    [Fact]
    public void A_transfer_to_the_same_account_is_refused()
    {
        // Failure path: a transfer moves service between two premises, and an account transferred to
        // itself is a row that says nothing happened while claiming something did.
        var customer = ACustomer();
        var account = AnAccount(customer);

        Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.Transferred(
                customer, account, account, 0m, "USD", null, TransitionReasonCode.Relocation, null, Today, Agent, Now));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-0.01)]
    public void A_transfer_carrying_a_negative_deposit_is_refused(decimal carried)
    {
        // A carry is a magnitude, not a movement — nothing leaves the utility on a transfer, so a
        // negative figure here is not a direction, it is a balance nobody can hold.
        var customer = ACustomer();

        Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.Transferred(
                customer,
                AnAccount(customer, "A-000001"),
                AnAccount(customer, "A-000002"),
                carried,
                "USD",
                null,
                TransitionReasonCode.Relocation,
                null,
                Today,
                Agent,
                Now));
    }

    [Fact]
    public void A_carried_deposit_finer_than_a_cent_is_refused_rather_than_rounded()
    {
        // The column is numeric(18,2). Accepting it would round it away in the database, where nobody
        // would ever see which figure was stored — the call the deposit ledger already makes.
        var customer = ACustomer();

        Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.Transferred(
                customer,
                AnAccount(customer, "A-000001"),
                AnAccount(customer, "A-000002"),
                250.125m,
                "USD",
                null,
                TransitionReasonCode.Relocation,
                null,
                Today,
                Agent,
                Now));
    }

    [Fact]
    public void A_transition_without_an_actor_is_refused() =>
        // Failure path: a change to what somebody is billed, with nobody's name on it, is not a
        // record anybody can answer for.
        Assert.Throws<RegistryValidationException>(() =>
            AccountTransition.ClassChanged(
                ACustomer(),
                CustomerClass.Residential,
                CustomerClass.Commercial,
                TransitionReasonCode.PremiseNowTrading,
                null,
                Today,
                new RegistryActor("  ", null),
                Now));

    [Fact]
    public void A_transfer_of_a_customer_holding_nothing_carries_nothing_and_names_no_ledger_entry()
    {
        // Ordinary, not an error: a customer who has never paid a deposit transfers perfectly well,
        // and no ledger row is written for them — a movement of zero is a row nobody can reconcile.
        var customer = ACustomer();

        var transition = AccountTransition.Transferred(
            customer,
            AnAccount(customer, "A-000001"),
            AnAccount(customer, "A-000002"),
            0m,
            "USD",
            depositEntryId: null,
            TransitionReasonCode.Relocation,
            null,
            Today,
            Agent,
            Now);

        Assert.Equal(0m, transition.DepositCarried);
        Assert.Null(transition.DepositEntryId);
    }
}

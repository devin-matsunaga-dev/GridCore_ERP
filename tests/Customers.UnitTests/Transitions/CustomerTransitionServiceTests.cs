using GridCore.Contracts.Services;
using GridCore.Contracts.Events;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Transitions;

/// <summary>
/// WP-2.15's two changes over the schema: what reaches the database, what the audit trail says about
/// it, what goes on the outbox, and — for a transfer — that the deposit rides along without a cent
/// being created or destroyed.
/// </summary>
/// <remarks>
/// <para>
/// The billing seam is a double, as it is everywhere else in this module: how far back a class change
/// may be dated depends on when the customer was last billed, and a Customers test may not resolve
/// the real <c>IBillDirectory</c>.
/// </para>
/// <para>
/// The state machines themselves are NOT re-tested here — <c>CustomerTests</c> and
/// <c>ServiceAccountTests</c> own them, and WORK_PACKAGES.md is explicit that this phase does not
/// loosen them. What is tested is that the register goes through them: an illegal move is still the
/// 409 it always was, and a transfer to an occupied premise is still refused by
/// <c>ServiceAccountService.OpenAsync</c>.
/// </para>
/// </remarks>
public class CustomerTransitionServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 10, 15, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static CustomersTestHost NewHost(TimeProvider? clock = null) =>
        new(clock ?? new FakeClock(Now), new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ACustomer(CustomersTestHost host, string name = "Sablan Family Residence") =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(new RegisterCustomerInput(name, CustomerClass.Residential)));

    private static Task<ServiceLocation> APremise(CustomersTestHost host, string line1 = "1 Songsong Road") =>
        host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(Address.Create(line1, "Songsong", "Rota", "MP", postalCode: "96951"), "House")));

    /// <summary>A customer with an energised account, which is where a move-out or a transfer starts.</summary>
    private static async Task<(Customer Customer, ServiceAccount Account, ServiceLocation Premise)> AServedCustomer(
        CustomersTestHost host)
    {
        var customer = await ACustomer(host);
        var premise = await APremise(host);

        var account = await host.WithAccountsAsync(accounts => accounts.OpenAsync(
            new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity)));

        await host.WithAccountsAsync(accounts => accounts.StartServiceAsync(account.Id, "Connected."));

        return (customer, account, premise);
    }

    private static Task<DepositEntry> Collect(CustomersTestHost host, Guid customerId, decimal amount) =>
        host.WithDepositsAsync(deposits => deposits.CollectAsync(customerId, new CollectDepositInput(amount)));

    // ---------------------------------------------------------------- class

    [Fact]
    public async Task A_class_change_moves_the_customer_records_the_reason_and_dates_it()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        var transition = await host.WithTransitionsAsync(transitions => transitions.ChangeClassAsync(
            customer.Id,
            new ChangeCustomerClassInput(
                CustomerClass.Commercial,
                TransitionReasonCode.PremiseNowTrading,
                new DateOnly(2026, 9, 1),
                "Bakery opened in the front room.")));

        await using var database = host.NewCustomersContext();
        var stored = await database.Customers.SingleAsync();

        Assert.Equal(CustomerClass.Commercial, stored.Class);
        Assert.Equal(Now, stored.ClassChangedAt);

        // The projection billing will price from, kept on the customer so a rate lookup never has to
        // read the register to answer "from when is this one commercial".
        Assert.Equal(new DateOnly(2026, 9, 1), stored.ClassEffectiveOn);

        Assert.Equal(AccountTransitionKind.ClassChanged, transition.Kind);
        Assert.Equal(TransitionReasonCode.PremiseNowTrading, transition.ReasonCode);
        Assert.Equal("Bakery opened in the front room.", transition.Notes);
    }

    [Fact]
    public async Task A_transition_with_no_effective_date_is_dated_today_by_the_host()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        var transition = await host.WithTransitionsAsync(transitions => transitions.ChangeClassAsync(
            customer.Id,
            new ChangeCustomerClassInput(CustomerClass.Commercial, TransitionReasonCode.MisclassifiedAtIntake)));

        Assert.Equal(Today, transition.EffectiveOn);
    }

    [Fact]
    public async Task A_class_change_cannot_be_dated_behind_a_bill_that_has_already_gone_out()
    {
        // WORK_PACKAGES.md: "class change is effective-dated and does not retro-date past an issued
        // bill". A bill that went out was priced on the class the customer held that day, and
        // re-classifying behind it would make the utility's own document wrong without saying so.
        using var host = NewHost();
        var customer = await ACustomer(host);

        host.Bills.Issued(customer.Id, new DateOnly(2026, 8, 20));

        var refused = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithTransitionsAsync(transitions => transitions.ChangeClassAsync(
                customer.Id,
                new ChangeCustomerClassInput(
                    CustomerClass.Commercial,
                    TransitionReasonCode.PremiseNowTrading,
                    new DateOnly(2026, 8, 19)))));

        Assert.Contains("2026-08-20", refused.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        Assert.Equal(CustomerClass.Residential, (await database.Customers.SingleAsync()).Class);
        Assert.Empty(await database.AccountTransitions.ToListAsync());
    }

    [Fact]
    public async Task A_class_change_dated_the_day_the_last_bill_went_out_is_allowed()
    {
        // On or after, not strictly after: a bill issued that morning covers a period that had
        // already closed, so a class taking effect the same day changes nothing that has been printed.
        using var host = NewHost();
        var customer = await ACustomer(host);

        host.Bills.Issued(customer.Id, new DateOnly(2026, 8, 20));

        var transition = await host.WithTransitionsAsync(transitions => transitions.ChangeClassAsync(
            customer.Id,
            new ChangeCustomerClassInput(
                CustomerClass.Commercial,
                TransitionReasonCode.PremiseNowTrading,
                new DateOnly(2026, 8, 20))));

        Assert.Equal(new DateOnly(2026, 8, 20), transition.EffectiveOn);
    }

    [Fact]
    public async Task A_customer_who_has_never_been_billed_may_be_re_classified_from_any_date()
    {
        // There is no floor to measure against, and inventing one would refuse a correction to a
        // customer registered last week with the wrong class on their form.
        using var host = NewHost();
        var customer = await ACustomer(host);

        var transition = await host.WithTransitionsAsync(transitions => transitions.ChangeClassAsync(
            customer.Id,
            new ChangeCustomerClassInput(
                CustomerClass.Commercial,
                TransitionReasonCode.MisclassifiedAtIntake,
                new DateOnly(2020, 1, 1))));

        Assert.Equal(new DateOnly(2020, 1, 1), transition.EffectiveOn);
    }

    [Fact]
    public async Task A_class_change_publishes_the_fact_billing_will_price_from()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        await host.WithTransitionsAsync(transitions => transitions.ChangeClassAsync(
            customer.Id,
            new ChangeCustomerClassInput(
                CustomerClass.Commercial,
                TransitionReasonCode.PremiseNowTrading,
                new DateOnly(2026, 9, 1))));

        var published = Assert.Single(host.Events.Published.OfType<CustomerClassChanged>());

        Assert.Equal(nameof(CustomerClass.Residential), published.FromClass);
        Assert.Equal(nameof(CustomerClass.Commercial), published.ToClass);

        // The effective date, not the instant it was typed. A consumer pricing from OccurredAt would
        // bill a fortnight of business use at the household rate.
        Assert.Equal(new DateOnly(2026, 9, 1), published.EffectiveOn);
        Assert.NotEqual(published.EffectiveOn, DateOnly.FromDateTime(published.OccurredAt.UtcDateTime));
    }

    // --------------------------------------------------------------- status

    [Fact]
    public async Task A_status_change_moves_the_customer_and_records_both_dates()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        await host.WithTransitionsAsync(transitions => transitions.ChangeStatusAsync(
            customer.Id,
            new ChangeCustomerStatusInput(
                CustomerStatus.Active,
                TransitionReasonCode.CustomerRequest,
                new DateOnly(2026, 9, 2),
                "Deposit received, service starts Monday.")));

        await using var database = host.NewCustomersContext();
        var stored = await database.Customers.SingleAsync();

        Assert.Equal(CustomerStatus.Active, stored.Status);
        Assert.Equal(Now, stored.StatusChangedAt);
        Assert.Equal(new DateOnly(2026, 9, 2), stored.StatusEffectiveOn);
        Assert.Equal("Deposit received, service starts Monday.", stored.StatusReason);
    }

    [Fact]
    public async Task A_status_change_may_be_back_dated_behind_a_bill_where_a_class_change_may_not()
    {
        // The asymmetry is the point: a class decides which tariff a bill is priced on, so dating one
        // behind an issued bill would say the utility charged the wrong rate. A status decides whether
        // the customer may take on new service; back-dating a suspension re-prices nothing.
        using var host = NewHost();
        var customer = await ACustomer(host);

        host.Bills.Issued(customer.Id, new DateOnly(2026, 8, 20));

        var transition = await host.WithTransitionsAsync(transitions => transitions.ChangeStatusAsync(
            customer.Id,
            new ChangeCustomerStatusInput(
                CustomerStatus.Active,
                TransitionReasonCode.CustomerRequest,
                new DateOnly(2026, 8, 1))));

        Assert.Equal(new DateOnly(2026, 8, 1), transition.EffectiveOn);
    }

    [Fact]
    public async Task An_illegal_status_move_is_still_blocked_by_the_WP12_machine()
    {
        // Failure path, and the rule WORK_PACKAGES.md states in as many words: "illegal state moves
        // still blocked by the WP-1.2 machine". A reason code does not buy a move the machine refuses.
        using var host = NewHost();
        var customer = await ACustomer(host);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithTransitionsAsync(transitions => transitions.ChangeStatusAsync(
                customer.Id,
                new ChangeCustomerStatusInput(CustomerStatus.Suspended, TransitionReasonCode.UnpaidBalance))));

        await using var database = host.NewCustomersContext();

        Assert.Equal(CustomerStatus.Prospect, (await database.Customers.SingleAsync()).Status);

        // And nothing was written to the register either: a refused move is not a transition.
        Assert.Empty(await database.AccountTransitions.ToListAsync());
    }

    [Fact]
    public async Task A_status_change_puts_nothing_on_the_outbox()
    {
        // Deliberately. Nothing downstream prices off a customer's status, and publishing an event
        // nobody consumes would be an instruction rather than a fact.
        using var host = NewHost();
        var customer = await ACustomer(host);

        await host.WithTransitionsAsync(transitions => transitions.ChangeStatusAsync(
            customer.Id,
            new ChangeCustomerStatusInput(CustomerStatus.Active, TransitionReasonCode.CustomerRequest)));

        Assert.Empty(host.Events.Published.OfType<CustomerClassChanged>());
        Assert.Empty(host.Events.Published.OfType<ServiceMovedOut>());
        Assert.Empty(host.Events.Published.OfType<ServiceTransferred>());
    }

    // -------------------------------------------------------------- move in

    [Fact]
    public async Task A_move_in_opens_an_account_through_WP12s_own_service()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);
        var premise = await APremise(host);

        var transition = await host.WithTransitionsAsync(transitions => transitions.MoveInAsync(
            customer.Id,
            new MoveInInput(premise.Id, TransitionReasonCode.NewOccupancy, ServiceType.Electricity, Today, "Keys collected this morning.")));

        await using var database = host.NewCustomersContext();
        var account = await database.ServiceAccounts.SingleAsync();

        Assert.Equal(premise.Id, account.ServiceLocationId);

        // Pending, not Active: energising is a connection visit, and a transition register does not
        // put a technician at a premise. WP-1.2's start endpoint is unchanged and is what does.
        Assert.Equal(ServiceAccountStatus.Pending, account.Status);
        Assert.Equal(account.Id, transition.ToServiceAccountId);

        // The account's own opening is audited by the service that opened it — the register adds a
        // second entry carrying the reason code and the date, and does not replace the first.
        await using var platform = host.NewPlatformContext();

        Assert.Single(await platform.AuditEntries.Where(entry => entry.Action == AuditActions.ServiceAccountOpened).ToListAsync());
        Assert.Single(await platform.AuditEntries.Where(entry => entry.Action == AuditActions.ServiceMovedIn).ToListAsync());
    }

    [Fact]
    public async Task A_move_in_to_an_occupied_premise_is_refused_and_writes_nothing()
    {
        // Failure path. The rule is WP-1.2's and lives in ServiceAccountService.OpenAsync; what is
        // asserted here is that the transition register goes through it rather than around it.
        using var host = NewHost();
        var (_, account, premise) = await AServedCustomer(host);
        var newcomer = await ACustomer(host, "Taisacan Household");

        var refused = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithTransitionsAsync(transitions => transitions.MoveInAsync(
                newcomer.Id,
                new MoveInInput(premise.Id, TransitionReasonCode.NewOccupancy))));

        Assert.Contains(account.AccountNumber, refused.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        Assert.Single(await database.ServiceAccounts.ToListAsync());
        Assert.Empty(await database.AccountTransitions.ToListAsync());
    }

    // ------------------------------------------------------------- move out

    [Fact]
    public async Task A_move_out_closes_the_account_and_publishes_the_day_service_ended()
    {
        using var host = NewHost();
        var (customer, account, premise) = await AServedCustomer(host);

        var transition = await host.WithTransitionsAsync(transitions => transitions.MoveOutAsync(
            customer.Id,
            new MoveOutInput(account.Id, TransitionReasonCode.EndOfTenancy, new DateOnly(2026, 8, 31))));

        await using var database = host.NewCustomersContext();
        var stored = await database.ServiceAccounts.SingleAsync();

        Assert.Equal(ServiceAccountStatus.Closed, stored.Status);

        // The premise is released, which is what makes the next occupant's move-in possible.
        Assert.False(stored.HoldsPremise);
        Assert.Equal(account.Id, transition.FromServiceAccountId);

        var published = Assert.Single(host.Events.Published.OfType<ServiceMovedOut>());

        Assert.Equal(new DateOnly(2026, 8, 31), published.EffectiveOn);
        Assert.Equal(premise.Id, published.ServiceLocationId);
        Assert.Equal(nameof(TransitionReasonCode.EndOfTenancy), published.ReasonCode);

        // ServiceAccountClosed fires alongside it — WP-1.2's own fact, unchanged.
        Assert.Single(host.Events.Published.OfType<ServiceAccountClosed>());
    }

    [Fact]
    public async Task A_move_out_dated_before_the_account_was_opened_is_refused()
    {
        // Failure path: a service period cannot close before it opened, and a final bill cut to that
        // date would cover a period the utility never supplied.
        using var host = NewHost();
        var (customer, account, _) = await AServedCustomer(host);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithTransitionsAsync(transitions => transitions.MoveOutAsync(
                customer.Id,
                new MoveOutInput(account.Id, TransitionReasonCode.EndOfTenancy, Today.AddDays(-1)))));

        await using var database = host.NewCustomersContext();

        Assert.Equal(ServiceAccountStatus.Active, (await database.ServiceAccounts.SingleAsync()).Status);
    }

    [Fact]
    public async Task Another_customers_account_cannot_be_moved_out()
    {
        // Failure path, and a 400 rather than a 404: answering "no such account" would tell a caller
        // that somebody else's account number does not exist, which is a different claim and untrue.
        using var host = NewHost();
        var (_, account, _) = await AServedCustomer(host);
        var stranger = await ACustomer(host, "Taisacan Household");

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithTransitionsAsync(transitions => transitions.MoveOutAsync(
                stranger.Id,
                new MoveOutInput(account.Id, TransitionReasonCode.EndOfTenancy))));
    }

    [Fact]
    public async Task An_account_that_does_not_exist_is_a_404() =>
        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(async () =>
        {
            using var host = NewHost();
            var customer = await ACustomer(host);

            await host.WithTransitionsAsync(transitions => transitions.MoveOutAsync(
                customer.Id,
                new MoveOutInput(Guid.CreateVersion7(Now), TransitionReasonCode.EndOfTenancy)));
        });

    // ------------------------------------------------------------- transfer

    [Fact]
    public async Task A_transfer_links_both_accounts_and_carries_the_deposit_exactly()
    {
        // WORK_PACKAGES.md: "move-out then move-in links both accounts and carries the deposit exactly
        // (no net money created)".
        using var host = NewHost();
        var (customer, account, premise) = await AServedCustomer(host);
        var destination = await APremise(host, "9 As Nieves Road");

        await Collect(host, customer.Id, 250.00m);

        var transition = await host.WithTransitionsAsync(transitions => transitions.TransferAsync(
            customer.Id,
            new TransferServiceInput(account.Id, destination.Id, TransitionReasonCode.Relocation, new DateOnly(2026, 9, 1))));

        await using var database = host.NewCustomersContext();

        // Ordered by account NUMBER, not by id: both accounts are minted from the same frozen clock,
        // so their Guid v7 ids share a timestamp and order at random. The number generator is
        // sequential, which makes A-000001 before A-000002 a fact rather than a coin flip.
        var accounts = await database.ServiceAccounts
            .OrderBy(candidate => candidate.AccountNumber)
            .ToListAsync();
        var stored = await database.Customers.SingleAsync();

        Assert.Equal(2, accounts.Count);
        Assert.Equal(ServiceAccountStatus.Closed, accounts[0].Status);
        Assert.Equal(ServiceAccountStatus.Pending, accounts[1].Status);
        Assert.Equal(destination.Id, accounts[1].ServiceLocationId);
        Assert.Equal(premise.Id, accounts[0].ServiceLocationId);

        // One row naming both, which is what "one linked transfer" means: a pair of rows could lose
        // the linkage, and a single row cannot half exist.
        Assert.Equal(AccountTransitionKind.Transferred, transition.Kind);
        Assert.Equal(accounts[0].Id, transition.FromServiceAccountId);
        Assert.Equal(accounts[1].Id, transition.ToServiceAccountId);

        // NO NET MONEY CREATED. The balance the utility holds is exactly what it held before.
        Assert.Equal(250.00m, stored.DepositHeld);
        Assert.Equal(250.00m, transition.DepositCarried);
    }

    [Fact]
    public async Task The_carry_is_a_ledger_entry_that_moves_nothing_rather_than_a_refund_and_a_re_collection()
    {
        // The owner's call, and the reason DepositEntryKind.Transferred has a direction of zero:
        // synthesising a refund and a collection would balance to the same figure and lie twice —
        // a customer's statement would show money going out and coming back.
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);
        var (customer, account, _) = await AServedCustomer(host);
        var destination = await APremise(host, "9 As Nieves Road");

        await Collect(host, customer.Id, 250.00m);

        // The clock is advanced between the collection and the carry so their Guid v7 ids carry
        // different timestamps. Two entries minted at one frozen instant share a 48-bit prefix and
        // differ only in a random tail, which makes OrderBy(Id) a coin flip — the trap STATUS.md has
        // warned about since WP-0.5, and the reason PaymentServiceTests.TakeAsync does the same.
        clock.Advance(TimeSpan.FromMinutes(5));

        await host.WithTransitionsAsync(transitions => transitions.TransferAsync(
            customer.Id,
            new TransferServiceInput(account.Id, destination.Id, TransitionReasonCode.Relocation)));

        await using var database = host.NewCustomersContext();

        var entries = await database.DepositEntries.OrderBy(entry => entry.Id).ToListAsync();

        Assert.Equal([DepositEntryKind.Collected, DepositEntryKind.Transferred], entries.Select(entry => entry.Kind));

        var carry = entries[^1];

        Assert.Equal(250.00m, carry.Amount);
        Assert.Equal(0m, carry.SignedAmount);
        Assert.Equal(250.00m, carry.BalanceAfter);

        // No refund and no collection anywhere in the ledger, which is the claim.
        Assert.DoesNotContain(entries, entry => entry.Kind is DepositEntryKind.Refunded);
        Assert.Single(entries, entry => entry.Kind is DepositEntryKind.Collected);

        // The account numbers live in the reason, because a deposit entry stores neither account —
        // which two accounts a deposit moved between is the transition register's business.
        Assert.Contains(account.AccountNumber, carry.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transfer_of_a_customer_holding_no_deposit_writes_no_ledger_entry()
    {
        // An entry saying nothing was carried is a row nobody can reconcile — the same argument
        // DepositEntryKind makes for having no Held member.
        using var host = NewHost();
        var (customer, account, _) = await AServedCustomer(host);
        var destination = await APremise(host, "9 As Nieves Road");

        var transition = await host.WithTransitionsAsync(transitions => transitions.TransferAsync(
            customer.Id,
            new TransferServiceInput(account.Id, destination.Id, TransitionReasonCode.Relocation)));

        await using var database = host.NewCustomersContext();

        Assert.Empty(await database.DepositEntries.ToListAsync());
        Assert.Equal(0m, transition.DepositCarried);
        Assert.Null(transition.DepositEntryId);
    }

    [Fact]
    public async Task A_transfer_to_an_occupied_premise_is_refused_and_the_old_account_stays_open()
    {
        // WORK_PACKAGES.md: "a transfer to an occupied location is refused". The rollback is the half
        // that matters — the close and the open are one transaction, so a refused open must not leave
        // the customer disconnected at the premise they still live in.
        using var host = NewHost();
        var (customer, account, _) = await AServedCustomer(host);
        var (_, occupier, occupied) = await AServedCustomerAt(host, "9 As Nieves Road");

        var refused = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithTransitionsAsync(transitions => transitions.TransferAsync(
                customer.Id,
                new TransferServiceInput(account.Id, occupied.Id, TransitionReasonCode.Relocation))));

        Assert.Contains(occupier.AccountNumber, refused.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        var stored = await database.ServiceAccounts.SingleAsync(candidate => candidate.Id == account.Id);

        Assert.Equal(ServiceAccountStatus.Active, stored.Status);
        Assert.Empty(await database.AccountTransitions.ToListAsync());
    }

    [Fact]
    public async Task A_transfer_to_the_premise_the_customer_is_already_served_at_is_refused()
    {
        // Failure path: there is nothing to move. Refused before anything is closed, so the message
        // says what is wrong rather than reporting the premise as occupied by the customer's own
        // account a moment after closing it.
        using var host = NewHost();
        var (customer, account, premise) = await AServedCustomer(host);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithTransitionsAsync(transitions => transitions.TransferAsync(
                customer.Id,
                new TransferServiceInput(account.Id, premise.Id, TransitionReasonCode.Relocation))));
    }

    [Fact]
    public async Task A_transfer_publishes_one_event_carrying_both_halves_and_what_rode_along()
    {
        using var host = NewHost();
        var (customer, account, premise) = await AServedCustomer(host);
        var destination = await APremise(host, "9 As Nieves Road");

        await Collect(host, customer.Id, 250.00m);

        await host.WithTransitionsAsync(transitions => transitions.TransferAsync(
            customer.Id,
            new TransferServiceInput(account.Id, destination.Id, TransitionReasonCode.Relocation, new DateOnly(2026, 9, 1))));

        var published = Assert.Single(host.Events.Published.OfType<ServiceTransferred>());

        Assert.Equal(account.Id, published.FromServiceAccountId);
        Assert.Equal(premise.Id, published.FromServiceLocationId);
        Assert.Equal(destination.Id, published.ToServiceLocationId);
        Assert.Equal(new DateOnly(2026, 9, 1), published.EffectiveOn);
        Assert.Equal(250.00m, published.DepositCarried);

        // NOT a move-out. A consumer that saw the closure alone would raise a final bill and release
        // a deposit for a customer who has not left.
        Assert.Empty(host.Events.Published.OfType<ServiceMovedOut>());
    }

    // ---------------------------------------------------------- audit + gate

    [Theory]
    [InlineData(AccountTransitionKind.ClassChanged, AuditActions.CustomerClassChanged)]
    [InlineData(AccountTransitionKind.StatusChanged, AuditActions.CustomerStatusChanged)]
    [InlineData(AccountTransitionKind.MovedIn, AuditActions.ServiceMovedIn)]
    [InlineData(AccountTransitionKind.MovedOut, AuditActions.ServiceMovedOut)]
    [InlineData(AccountTransitionKind.Transferred, AuditActions.ServiceTransferred)]
    public async Task Every_kind_is_audited_against_the_register_row(AccountTransitionKind kind, string action)
    {
        // WORK_PACKAGES.md: "each transition audited with before/after". Uniformly against the row
        // rather than against whichever thing moved, so "show me every transition this customer has
        // been through" does not have to know the kind before it can find the entry.
        using var host = NewHost();
        var transition = await ATransitionOfKind(host, kind);

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == action);

        Assert.Equal(AuditEntityTypes.AccountTransition, entry.EntityType);
        Assert.Equal(transition.Id.ToString(), entry.EntityId);
        Assert.NotNull(entry.BeforeJson);
        Assert.NotNull(entry.AfterJson);
    }

    [Fact]
    public async Task A_class_changes_audit_shows_the_class_moving()
    {
        using var host = NewHost();

        await ATransitionOfKind(host, AccountTransitionKind.ClassChanged);

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.CustomerClassChanged);

        Assert.Contains(nameof(CustomerClass.Residential), entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains(nameof(CustomerClass.Commercial), entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transfers_audit_shows_one_account_giving_way_to_the_other()
    {
        // The before snapshot is taken while the old account is still open, which is the whole value
        // of a before: the pair reads A-000001 Active, then A-000002 Pending.
        using var host = NewHost();

        await ATransitionOfKind(host, AccountTransitionKind.Transferred);

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries.SingleAsync(candidate => candidate.Action == AuditActions.ServiceTransferred);

        Assert.Contains("A-000001", entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains(nameof(ServiceAccountStatus.Active), entry.BeforeJson!, StringComparison.Ordinal);
        Assert.Contains("A-000002", entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_without_the_transition_permission_is_refused()
    {
        // Failure path, invariant 5, and WORK_PACKAGES.md's [SENSITIVE] tag. Demanded in the SERVICE
        // as well as on the route, because a later module completing a disconnection work order will
        // call MoveOutAsync and not a URL.
        using var host = NewHost();
        var (customer, account, _) = await AServedCustomer(host);

        var clerk = new FakeCurrentUser(
            "auth0|clerk",
            "Junior Clerk",
            new HashSet<string>(StringComparer.Ordinal) { Permissions.Customers.Read, Permissions.Customers.Write });

        var refused = await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, transitions => transitions.MoveOutAsync(
                customer.Id,
                new MoveOutInput(account.Id, TransitionReasonCode.EndOfTenancy))));

        Assert.Contains(Permissions.Customers.Transition, refused.Message, StringComparison.Ordinal);

        await using var database = host.NewCustomersContext();

        Assert.Equal(ServiceAccountStatus.Active, (await database.ServiceAccounts.SingleAsync()).Status);
        Assert.Empty(await database.AccountTransitions.ToListAsync());
    }

    [Fact]
    public async Task The_permission_is_checked_before_anything_is_read_so_a_missing_customer_is_still_a_403()
    {
        // Order matters: answering 404 first would let somebody without the grant probe which
        // customer ids exist.
        using var host = NewHost();

        var clerk = new FakeCurrentUser("auth0|clerk", "Junior Clerk", new HashSet<string>(StringComparer.Ordinal));

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, transitions => transitions.ChangeStatusAsync(
                Guid.CreateVersion7(Now),
                new ChangeCustomerStatusInput(CustomerStatus.Active, TransitionReasonCode.CustomerRequest))));
    }

    // -------------------------------------------------------------- reading

    [Fact]
    public async Task The_register_reads_back_newest_first_and_can_be_narrowed_to_one_account()
    {
        var clock = new FakeClock(Now);
        using var host = NewHost(clock);
        var (customer, account, _) = await AServedCustomer(host);
        var destination = await APremise(host, "9 As Nieves Road");

        // The clock is advanced between the two so their Guid v7 ids carry different timestamps —
        // otherwise the ordering is a coin flip, which is the trap STATUS.md has warned about since
        // WP-0.5.
        await host.WithTransitionsAsync(transitions => transitions.ChangeStatusAsync(
            customer.Id,
            new ChangeCustomerStatusInput(CustomerStatus.Active, TransitionReasonCode.CustomerRequest)));

        clock.Advance(TimeSpan.FromMinutes(5));

        await host.WithTransitionsAsync(transitions => transitions.TransferAsync(
            customer.Id,
            new TransferServiceInput(account.Id, destination.Id, TransitionReasonCode.Relocation)));

        var all = await host.WithTransitionsAsync(transitions => transitions.ListAsync(customer.Id, new TransitionQuery()));

        Assert.Equal(
            [AccountTransitionKind.Transferred, AccountTransitionKind.StatusChanged],
            all.Select(transition => transition.Kind));

        // Narrowed to the account released: a transfer names it on the FROM side, and "what happened
        // to this account" has to find it there as readily as on the TO side.
        var forAccount = await host.WithTransitionsAsync(transitions =>
            transitions.ListAsync(customer.Id, new TransitionQuery(ServiceAccountId: account.Id)));

        Assert.Equal([AccountTransitionKind.Transferred], forAccount.Select(transition => transition.Kind));

        var byKind = await host.WithTransitionsAsync(transitions =>
            transitions.ListAsync(customer.Id, new TransitionQuery(Kind: AccountTransitionKind.StatusChanged)));

        Assert.Equal([AccountTransitionKind.StatusChanged], byKind.Select(transition => transition.Kind));
    }

    [Fact]
    public async Task A_customer_with_no_transitions_reads_back_empty_and_one_that_does_not_exist_is_a_404()
    {
        using var host = NewHost();
        var customer = await ACustomer(host);

        Assert.Empty(await host.WithTransitionsAsync(transitions => transitions.ListAsync(customer.Id, new TransitionQuery())));

        // Distinguished, because an empty list for a missing id would say the customer had none.
        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            host.WithTransitionsAsync(transitions => transitions.ListAsync(Guid.CreateVersion7(Now), new TransitionQuery())));
    }

    [Fact]
    public async Task The_register_only_ever_answers_for_the_customer_asked_about()
    {
        using var host = NewHost();
        var mine = await ACustomer(host);
        var theirs = await ACustomer(host, "Taisacan Household");

        await host.WithTransitionsAsync(transitions => transitions.ChangeStatusAsync(
            theirs.Id,
            new ChangeCustomerStatusInput(CustomerStatus.Active, TransitionReasonCode.CustomerRequest)));

        Assert.Empty(await host.WithTransitionsAsync(transitions => transitions.ListAsync(mine.Id, new TransitionQuery())));
    }

    // ------------------------------------------------------------- fixtures

    private static async Task<(Customer Customer, ServiceAccount Account, ServiceLocation Premise)> AServedCustomerAt(
        CustomersTestHost host,
        string line1)
    {
        var customer = await ACustomer(host, $"Occupier of {line1}");
        var premise = await APremise(host, line1);

        var account = await host.WithAccountsAsync(accounts => accounts.OpenAsync(
            new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity)));

        return (customer, account, premise);
    }

    /// <summary>One transition of each kind, so the audit assertions can run over all five.</summary>
    private static async Task<AccountTransition> ATransitionOfKind(CustomersTestHost host, AccountTransitionKind kind)
    {
        switch (kind)
        {
            case AccountTransitionKind.ClassChanged:
            {
                var customer = await ACustomer(host);

                return await host.WithTransitionsAsync(transitions => transitions.ChangeClassAsync(
                    customer.Id,
                    new ChangeCustomerClassInput(CustomerClass.Commercial, TransitionReasonCode.PremiseNowTrading)));
            }

            case AccountTransitionKind.StatusChanged:
            {
                var customer = await ACustomer(host);

                return await host.WithTransitionsAsync(transitions => transitions.ChangeStatusAsync(
                    customer.Id,
                    new ChangeCustomerStatusInput(CustomerStatus.Active, TransitionReasonCode.CustomerRequest)));
            }

            case AccountTransitionKind.MovedIn:
            {
                var customer = await ACustomer(host);
                var premise = await APremise(host);

                return await host.WithTransitionsAsync(transitions => transitions.MoveInAsync(
                    customer.Id,
                    new MoveInInput(premise.Id, TransitionReasonCode.NewOccupancy)));
            }

            case AccountTransitionKind.MovedOut:
            {
                var (customer, account, _) = await AServedCustomer(host);

                return await host.WithTransitionsAsync(transitions => transitions.MoveOutAsync(
                    customer.Id,
                    new MoveOutInput(account.Id, TransitionReasonCode.EndOfTenancy)));
            }

            default:
            {
                var (customer, account, _) = await AServedCustomer(host);
                var destination = await APremise(host, "9 As Nieves Road");

                return await host.WithTransitionsAsync(transitions => transitions.TransferAsync(
                    customer.Id,
                    new TransferServiceInput(account.Id, destination.Id, TransitionReasonCode.Relocation)));
            }
        }
    }
}

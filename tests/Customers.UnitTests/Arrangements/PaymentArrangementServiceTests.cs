using System.Text.Json;
using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Arrangements;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Approvals;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Arrangements;

/// <summary>
/// Payment arrangements over the customers schema (WP-2.20).
/// </summary>
/// <remarks>
/// SQLite in memory with the platform schema on the same connection, so these assert the thing that
/// matters about a proposal: the arrangement, its schedule, the approval request it may need and its
/// audit entry are all one transaction. <c>IBillDirectory</c> is a double — what an account owes is
/// Billing's register, and this module may never read it.
/// </remarks>
public class PaymentArrangementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    /// <summary>A rep who may arrange but may not decide an over-limit one.</summary>
    private static ICurrentUser Rep => Holding("auth0|cs-agent", "Ana Cruz", Permissions.Customers.Arrange);

    /// <summary>A manager who may decide one — <c>platform.approve</c> as well as the grant.</summary>
    private static ICurrentUser Manager =>
        Holding("auth0|manager", "Jo Taitano", Permissions.Customers.Arrange, Permissions.Platform.Approve);

    /// <summary>A caller holding exactly <paramref name="permissions"/> and nothing else.</summary>
    private static FakeCurrentUser Holding(string userId, string userName, params string[] permissions) =>
        new(userId, userName, permissions.ToHashSet(StringComparer.Ordinal));

    private static CustomersTestHost NewHost(ICurrentUser? user = null) =>
        new(new FakeClock(Now), user ?? new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    /// <summary>A customer with one open electricity account, and the account.</summary>
    private static async Task<(Customer Customer, ServiceAccount Account)> ACustomerAsync(
        CustomersTestHost host,
        CustomerClass customerClass = CustomerClass.Residential)
    {
        var customer = await host.WithCustomersAsync(customers =>
            customers.RegisterAsync(new RegisterCustomerInput("Sablan Family Residence", customerClass)));

        var premise = await host.WithLocationsAsync(locations => locations.RegisterAsync(
            new ServiceLocationInput(
                Address.Create("14 Sablan Street", "Songsong", "Rota", "MP"),
                "Meter on the north wall")));

        var account = await host.WithAccountsAsync(accounts =>
            accounts.OpenAsync(new OpenServiceAccountInput(customer.Id, premise.Id, ServiceType.Electricity)));

        return (customer, account);
    }

    /// <summary>Puts <paramref name="balance"/> past due on the account, through Billing's seam.</summary>
    private static void ArrearsOf(CustomersTestHost host, Customer customer, ServiceAccount account, decimal balance) =>
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-40), balance);

    [Fact]
    public async Task A_proposal_schedules_the_arrears_and_starts_out_of_force()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var arrangement = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        Assert.Equal(PaymentArrangementStatus.Proposed, arrangement.Status);
        Assert.StartsWith("PA-", arrangement.ArrangementNumber, StringComparison.Ordinal);
        Assert.Equal(300.00m, arrangement.ScheduledAmount);
        Assert.Equal(3, arrangement.Instalments.Count);
        Assert.Equal(account.AccountNumber, arrangement.AccountNumber);
        Assert.Equal(customer.Id, arrangement.CustomerId);
    }

    [Fact]
    public async Task An_arrangement_for_more_than_the_arrears_is_refused()
    {
        // WORK_PACKAGES.md's verify item. An arrangement records how an EXISTING debt will be paid;
        // promising more than exists would be the utility inventing one.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 200.00m);

        var failure = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithArrangementsAsync(arrangements =>
                arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(250.00m))));

        Assert.Contains("never creates one", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bill_not_yet_due_is_not_arrears_and_cannot_be_arranged()
    {
        // The distinction the whole of Phase 2.6 turns on: a bill issued last week and due next
        // month is money the utility is owed and is NOT money the customer is late with.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(30), 400.00m);

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithArrangementsAsync(arrangements =>
                arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(400.00m))));
    }

    [Fact]
    public async Task Arranging_without_permission_is_refused()
    {
        // THE FAILURE PATH CONVENTIONS.MD ASKS FOR, and demanded before anything is read: an
        // arrangement suppresses a disconnection, so the refusal must not depend on whether the
        // account happened to be in arrears.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var clerk = Holding("auth0|clerk", "Pat Reyes", Permissions.Customers.Write);

        var failure = await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, arrangements =>
                arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m))));

        Assert.Contains(Permissions.Customers.Arrange, failure.Message, StringComparison.Ordinal);

        Assert.Empty(await host.NewCustomersContext().PaymentArrangements.ToListAsync());
    }

    [Fact]
    public async Task Arranging_for_an_account_that_holds_no_deposit_and_no_arrears_is_still_refused_without_permission()
    {
        // The gate is demanded BEFORE the arrears are read, so an account with nothing owing is
        // refused too — otherwise the 403 would depend on there being a debt to arrange.
        using var host = NewHost();
        var (_, account) = await ACustomerAsync(host);

        var clerk = Holding("auth0|clerk", "Pat Reyes", Permissions.Customers.Read);

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, arrangements =>
                arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(100.00m))));
    }

    [Fact]
    public async Task A_proposal_is_audited_with_the_whole_schedule()
    {
        // INVARIANT 1, and more: "what did this customer actually agree to" is the question somebody
        // asks when it is disputed, by which time the instalment rows have been paid against.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var arrangement = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        var entry = Assert.Single(
            await host.NewPlatformContext().AuditEntries
                .Where(candidate => candidate.Action == AuditActions.PaymentArrangementProposed)
                .ToListAsync());

        Assert.Equal(AuditEntityTypes.PaymentArrangement, entry.EntityType);
        Assert.Equal(arrangement.Id.ToString(), entry.EntityId);

        using var after = JsonDocument.Parse(entry.AfterJson!);
        Assert.Equal(3, after.RootElement.GetProperty("instalments").GetArrayLength());
    }

    [Fact]
    public async Task Activation_brings_it_into_force_and_it_then_suppresses_disconnection()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m)));

        var active = await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        Assert.Equal(PaymentArrangementStatus.Active, active.Status);
        Assert.Equal(Today, active.ActivatedOn);

        // THE SEAM WP-2.19 LEFT, now answered by a real register.
        var standing = await host.WithArrangementDirectoryAsync(directory =>
            directory.StandingForAccountAsync(account.Id));

        Assert.NotNull(standing);
        Assert.Equal("Active", standing.Status);
        Assert.True(standing.SuppressesDisconnection);
    }

    [Fact]
    public async Task An_account_with_no_arrangement_reads_as_having_none_rather_than_one_that_helps_nobody()
    {
        // The distinction WP-2.19's null implementation drew and the real one keeps: null is "this
        // account has no arrangement", not "the customer has one and it does not help them".
        using var host = NewHost();
        var (_, account) = await ACustomerAsync(host);

        Assert.Null(await host.WithArrangementDirectoryAsync(directory =>
            directory.StandingForAccountAsync(account.Id)));
    }

    [Fact]
    public async Task A_proposal_protects_nobody_until_it_is_brought_into_force()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m)));

        var standing = await host.WithArrangementDirectoryAsync(directory =>
            directory.StandingForAccountAsync(account.Id));

        Assert.Equal("Proposed", standing!.Status);
        Assert.False(standing.SuppressesDisconnection);
    }

    [Fact]
    public async Task A_second_arrangement_beside_a_standing_one_is_refused()
    {
        // Two answers to "what has this customer agreed to pay" would make the account's protection
        // depend on which was read.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var first = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(150.00m)));

        var failure = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithArrangementsAsync(arrangements =>
                arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(150.00m))));

        Assert.Contains(first.ArrangementNumber, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_arrangement_over_the_rep_limit_raises_an_approval_and_cannot_be_activated_until_it_is_granted()
    {
        // WORK_PACKAGES.md's verify item, in full: beyond the limit the arrangement uses WP-0.4's
        // approval primitive rather than a second bespoke workflow, and it does not become active
        // before the decision.
        using var host = NewHost(Rep);
        var (customer, account) = await ACustomerAsync(host);

        var limit = ArrangementLimits.For(CustomerClass.Residential)!;
        var balance = limit.MaximumBalance + 500.00m;
        ArrearsOf(host, customer, account, balance);

        var arrangement = await host.AsAsync(Rep, arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(balance, InstalmentCount: 6)));

        Assert.True(arrangement.RequiresApproval);
        Assert.NotNull(arrangement.ApprovalRequestId);

        var pending = await host.WithApprovalsAsAsync(Rep, approvals => approvals.FindAsync(arrangement.ApprovalRequestId!.Value));

        Assert.Equal(ApprovalStatus.Pending, pending!.Status);
        Assert.Equal(PaymentArrangementService.ApprovalRequestType, pending.RequestType);
        Assert.Equal(Permissions.Customers.Arrange, pending.RequiredPermission);

        var refused = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.AsAsync(Rep, arrangements => arrangements.ActivateAsync(arrangement.Id)));

        Assert.Contains("Pending", refused.Message, StringComparison.Ordinal);

        // And once a manager has decided it, the same activation goes through.
        await host.WithApprovalsAsAsync(Manager, approvals =>
            approvals.ApproveAsync(arrangement.ApprovalRequestId!.Value, "Long-standing customer."));

        var active = await host.AsAsync(Rep, arrangements => arrangements.ActivateAsync(arrangement.Id));

        Assert.Equal(PaymentArrangementStatus.Active, active.Status);
    }

    [Fact]
    public async Task A_rejected_approval_leaves_the_arrangement_out_of_force()
    {
        using var host = NewHost(Rep);
        var (customer, account) = await ACustomerAsync(host);

        var limit = ArrangementLimits.For(CustomerClass.Residential)!;
        var balance = limit.MaximumBalance + 500.00m;
        ArrearsOf(host, customer, account, balance);

        var arrangement = await host.AsAsync(Rep, arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(balance, InstalmentCount: 6)));

        await host.WithApprovalsAsAsync(Manager, approvals =>
            approvals.RejectAsync(arrangement.ApprovalRequestId!.Value, "Third arrangement this year."));

        var refused = await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.AsAsync(Rep, arrangements => arrangements.ActivateAsync(arrangement.Id)));

        Assert.Contains("Rejected", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_arrangement_within_the_limit_needs_no_approval_at_all()
    {
        using var host = NewHost(Rep);
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var arrangement = await host.AsAsync(Rep, arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        Assert.False(arrangement.RequiresApproval);
        Assert.Null(arrangement.ApprovalRequestId);
        Assert.Empty(await host.NewPlatformContext().ApprovalRequests.ToListAsync());
    }

    [Fact]
    public async Task Too_many_instalments_needs_approval_even_where_the_balance_does_not()
    {
        // Two ceilings rather than one, because either on its own is trivially avoided by moving the
        // other: a small debt spread over three years is a write-off wearing a schedule's clothes.
        using var host = NewHost(Rep);
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var limit = ArrangementLimits.For(CustomerClass.Residential)!;

        var arrangement = await host.AsAsync(Rep, arrangements => arrangements.ProposeAsync(
            account.Id,
            new ProposeArrangementInput(300.00m, InstalmentCount: limit.MaximumInstalments + 1)));

        Assert.True(arrangement.RequiresApproval);
        Assert.NotNull(arrangement.ApprovalRequestId);
    }

    [Fact]
    public async Task A_commercial_customer_is_judged_against_the_commercial_ceiling()
    {
        // Keyed on class, the call DepositRule made: a business owing four thousand dollars over six
        // months is ordinary and a household owing the same is not.
        using var host = NewHost(Rep);
        var (customer, account) = await ACustomerAsync(host, CustomerClass.Commercial);

        var residential = ArrangementLimits.For(CustomerClass.Residential)!;
        var balance = residential.MaximumBalance + 500.00m;
        ArrearsOf(host, customer, account, balance);

        var arrangement = await host.AsAsync(Rep, arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(balance, InstalmentCount: 6)));

        Assert.False(arrangement.RequiresApproval);
        Assert.Equal(ArrangementLimits.For(CustomerClass.Commercial)!.MaximumBalance, arrangement.LimitMaximumBalance);
    }

    [Fact]
    public async Task A_payment_settles_the_earliest_unpaid_instalment_and_leaves_every_bill_alone()
    {
        // WORK_PACKAGES.md's last verify item: "the arrangement leaves every bill's status
        // untouched". Nothing published, and the balance the bill directory reports is exactly what
        // it was — an arrangement records how an existing debt will be paid, and nothing more.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        var bill = host.Bills.Outstanding(customer.Id, account.Id, Today.AddDays(-40), 300.00m);

        // Everything the intake itself published, before a single arrangement exists.
        var publishedBySetup = host.Events.Published.Count;

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        var settlement = await host.WithArrangementsAsync(arrangements =>
            arrangements.RecordPaymentAsync(account.Id, 100.00m, Guid.CreateVersion7(), "sandbox-1"));

        Assert.NotNull(settlement);
        Assert.Equal(100.00m, settlement.AppliedAmount);
        Assert.Equal([1], settlement.SettledSequences);
        Assert.False(settlement.IsKept);

        // NOTHING PUBLISHED. An arrangement changes nothing anybody downstream is entitled to act
        // on, and an event nobody consumes is an instruction rather than a fact.
        Assert.Equal(publishedBySetup, host.Events.Published.Count);
        Assert.Equal(300.00m, (await host.Bills.FindAsync(bill.Id))!.Balance);
        Assert.Equal("Issued", (await host.Bills.FindAsync(bill.Id))!.Status);
    }

    [Fact]
    public async Task The_payment_that_finishes_the_schedule_records_the_arrangement_as_kept()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        var settlement = await host.WithArrangementsAsync(arrangements =>
            arrangements.RecordPaymentAsync(account.Id, 300.00m, Guid.CreateVersion7(), "sandbox-2"));

        Assert.True(settlement!.IsKept);

        var stored = await host.NewCustomersContext().PaymentArrangements.SingleAsync();

        Assert.Equal(PaymentArrangementStatus.Kept, stored.Status);
        Assert.Equal(Today, stored.ClosedOn);

        // And a kept arrangement protects nobody: the promise is over.
        var standing = await host.WithArrangementDirectoryAsync(directory =>
            directory.StandingForAccountAsync(account.Id));

        Assert.Null(standing);
    }

    [Fact]
    public async Task Money_arriving_against_a_proposal_nobody_activated_settles_nothing()
    {
        // Crediting a schedule the customer never agreed to would show a promise being kept that was
        // never made.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m)));

        Assert.Null(await host.WithArrangementsAsync(arrangements =>
            arrangements.RecordPaymentAsync(account.Id, 100.00m, Guid.CreateVersion7(), "sandbox-3")));
    }

    [Fact]
    public async Task Money_arriving_on_an_account_with_no_arrangement_settles_nothing()
    {
        // Most payments. The consumer answers null and the bus is not troubled with a fault.
        using var host = NewHost();
        var (_, account) = await ACustomerAsync(host);

        Assert.Null(await host.WithArrangementsAsync(arrangements =>
            arrangements.RecordPaymentAsync(account.Id, 100.00m, Guid.CreateVersion7(), "sandbox-4")));
    }

    [Fact]
    public async Task An_applied_payment_is_audited_against_the_system_with_the_provider_reference()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));
        await host.WithArrangementsAsync(arrangements =>
            arrangements.RecordPaymentAsync(account.Id, 100.00m, Guid.CreateVersion7(), "sandbox-5"));

        var entry = Assert.Single(
            await host.NewPlatformContext().AuditEntries
                .Where(candidate => candidate.Action == AuditActions.PaymentArrangementPaymentApplied)
                .ToListAsync());

        Assert.Contains("sandbox-5", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_review_run_breaks_an_arrangement_that_missed_an_instalment()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        var missed = proposed.Instalments.First().DueDate.AddDays(1);

        var result = await host.WithArrangementsAsync(arrangements =>
            arrangements.ReviewAsync(new ReviewArrangementsInput(missed)));

        Assert.Equal(1, result.Reviewed);
        Assert.Equal(1, result.BrokenCount);
        Assert.Equal(0, result.KeptCount);

        var stored = await host.NewCustomersContext().PaymentArrangements.SingleAsync();

        Assert.Equal(PaymentArrangementStatus.Broken, stored.Status);
        Assert.Equal(missed, stored.ClosedOn);
    }

    [Fact]
    public async Task A_defaulting_arrangement_stops_protecting_the_account_before_any_review_has_run()
    {
        // THE REASON THE STANDING IS COMPUTED. An account defaulting on a Friday must not stay
        // protected from disconnection all weekend because a job has not run — and because the run
        // persists exactly what the directory reads, the two can never disagree.
        using var lateHost = new CustomersTestHost(
            new FakeClock(Now),
            new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

        var (customer, account) = await ACustomerAsync(lateHost);
        ArrearsOf(lateHost, customer, account, 300.00m);

        var proposed = await lateHost.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        await lateHost.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        var stored = await lateHost.NewCustomersContext().PaymentArrangements
            .Include(arrangement => arrangement.Instalments)
            .SingleAsync();

        // Still Active in the column, and already broken in fact.
        Assert.Equal(PaymentArrangementStatus.Active, stored.Status);

        var missed = stored.Instalments.OrderBy(instalment => instalment.Sequence).First().DueDate.AddDays(1);

        Assert.Equal(PaymentArrangementStatus.Broken, stored.StandingOn(missed));
        Assert.False(stored.SuppressesDisconnectionOn(missed));
    }

    [Fact]
    public async Task A_broken_arrangement_is_replaced_rather_than_resumed()
    {
        // WORK_PACKAGES.md: "a broken arrangement cannot be resumed, only replaced". Both halves —
        // activation is refused, and a fresh proposal is allowed.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var first = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(first.Id));

        var missed = first.Instalments.First().DueDate.AddDays(1);

        await host.WithArrangementsAsync(arrangements =>
            arrangements.ReviewAsync(new ReviewArrangementsInput(missed)));

        await Assert.ThrowsAsync<RegistryWorkflowException>(() =>
            host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(first.Id)));

        var replacement = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, ArrangedOn: missed)));

        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Equal(PaymentArrangementStatus.Proposed, replacement.Status);
    }

    [Fact]
    public async Task The_review_run_records_a_paid_off_arrangement_as_kept()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        // Paid in full, but recorded through a path that leaves the status alone: the domain's own
        // Apply, so the run has something to write down.
        await using (var context = host.NewCustomersContext())
        {
            var stored = await context.PaymentArrangements
                .Include(arrangement => arrangement.Instalments)
                .SingleAsync();

            foreach (var instalment in stored.Instalments)
            {
                instalment.Settle(instalment.Amount, Now);
            }

            await context.SaveChangesAsync();
        }

        var result = await host.WithArrangementsAsync(arrangements =>
            arrangements.ReviewAsync(new ReviewArrangementsInput(Today)));

        Assert.Equal(1, result.KeptCount);
        Assert.Equal(0, result.BrokenCount);
    }

    [Fact]
    public async Task The_review_run_is_audited_and_refused_without_permission()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        var clerk = Holding("auth0|clerk", "Pat Reyes", Permissions.Customers.Read);

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, arrangements => arrangements.ReviewAsync(new ReviewArrangementsInput())));

        await host.WithArrangementsAsync(arrangements =>
            arrangements.ReviewAsync(new ReviewArrangementsInput(Today)));

        Assert.Single(
            await host.NewPlatformContext().AuditEntries
                .Where(candidate => candidate.Action == AuditActions.PaymentArrangementReviewRun)
                .ToListAsync());
    }

    [Fact]
    public async Task Breaking_an_arrangement_is_audited_against_that_account_and_not_only_in_the_run()
    {
        // A broken arrangement RESTORES disconnection eligibility, so "when did this account stop
        // being protected" has to be answerable about the account rather than only from a run that
        // touched four hundred others.
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        await host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(proposed.Id));

        await host.WithArrangementsAsync(arrangements =>
            arrangements.ReviewAsync(new ReviewArrangementsInput(proposed.Instalments.First().DueDate.AddDays(1))));

        var entry = Assert.Single(
            await host.NewPlatformContext().AuditEntries
                .Where(candidate => candidate.Action == AuditActions.PaymentArrangementBroken)
                .ToListAsync());

        Assert.Equal(proposed.Id.ToString(), entry.EntityId);
        Assert.Contains("Active", entry.BeforeJson, StringComparison.Ordinal);
        Assert.Contains("Broken", entry.AfterJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_published_ceilings_are_read_from_the_table_rather_than_the_static_list()
    {
        // The same call every other reference register in this module makes: the static list is how
        // the rows are SEEDED, and what is in force is whatever the database holds.
        using var host = NewHost();

        var limits = await host.WithArrangementsAsync(arrangements => arrangements.LimitsAsync());

        Assert.Equal(Enum.GetValues<CustomerClass>().Length, limits.Count);
        Assert.All(limits, limit => Assert.Contains("Demo figures", limit.Notes, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listing_an_account_returns_its_arrangements_newest_first_with_their_schedules()
    {
        using var host = NewHost();
        var (customer, account) = await ACustomerAsync(host);
        ArrearsOf(host, customer, account, 300.00m);

        var proposed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ProposeAsync(account.Id, new ProposeArrangementInput(300.00m, InstalmentCount: 3)));

        var listed = await host.WithArrangementsAsync(arrangements =>
            arrangements.ListForAccountAsync(account.Id, 10));

        var only = Assert.Single(listed);

        Assert.Equal(proposed.Id, only.Id);
        Assert.Equal(3, only.Instalments.Count);
        Assert.Equal([1, 2, 3], only.Instalments.Select(instalment => instalment.Sequence));
    }

    [Fact]
    public async Task Activating_an_arrangement_that_does_not_exist_is_a_not_found()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<PaymentArrangementNotFoundException>(() =>
            host.WithArrangementsAsync(arrangements => arrangements.ActivateAsync(Guid.CreateVersion7())));
    }

    [Fact]
    public async Task Arranging_against_an_account_that_does_not_exist_is_a_not_found()
    {
        using var host = NewHost();

        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(() =>
            host.WithArrangementsAsync(arrangements =>
                arrangements.ProposeAsync(Guid.CreateVersion7(), new ProposeArrangementInput(100.00m))));
    }
}

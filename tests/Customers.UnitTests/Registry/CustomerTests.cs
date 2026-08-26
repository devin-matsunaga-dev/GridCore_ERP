using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Transitions;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The customer aggregate on its own: no database, no host. Everything the registry refuses, it
/// refuses here, which is why these run in milliseconds.
/// </summary>
public class CustomerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The day <see cref="Now"/> falls on — what a transition dated "today" carries.</summary>
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static Customer ARegisteredCustomer(CustomerStatus status = CustomerStatus.Prospect) =>
        Customer.Register("C-000001", "Sablan Family Residence", CustomerClass.Residential, Now, status: status);

    [Fact]
    public void A_registered_customer_starts_as_a_prospect_with_a_chronological_id()
    {
        var customer = ARegisteredCustomer();

        Assert.Equal(CustomerStatus.Prospect, customer.Status);
        Assert.Equal(Now, customer.RegisteredAt);
        Assert.Equal(7, customer.Id.Version);
        Assert.Null(customer.StatusChangedAt);
    }

    [Fact]
    public void Registering_trims_the_details_and_drops_the_blank_ones()
    {
        var customer = Customer.Register(
            "  C-000001  ",
            "  Songsong Village Market  ",
            CustomerClass.Commercial,
            Now,
            contactName: "   ",
            email: " accounts@songsongmarket.example.com ");

        Assert.Equal("C-000001", customer.AccountNumber);
        Assert.Equal("Songsong Village Market", customer.Name);
        Assert.Equal("accounts@songsongmarket.example.com", customer.Email);

        // Whitespace is not a contact name. Storing it would put an empty line on every screen that
        // renders "contact" and make "has a contact" untestable.
        Assert.Null(customer.ContactName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_customer_without_a_name_is_refused(string name) =>
        Assert.Throws<RegistryValidationException>(() =>
            Customer.Register("C-000001", name, CustomerClass.Residential, Now));

    [Fact]
    public void A_customer_without_an_account_number_is_refused() =>
        Assert.Throws<RegistryValidationException>(() =>
            Customer.Register(" ", "Sablan Family Residence", CustomerClass.Residential, Now));

    [Fact]
    public void A_customer_is_registered_holding_no_deposit()
    {
        // WP-2.12: a registration takes no money. Every cent held has a ledger entry explaining it,
        // so a customer starts at zero and the deposit lifecycle is what moves them off it. The
        // guards that used to live here — negative, finer than a cent — moved to DepositEntry with
        // the money.
        Assert.Equal(0m, ARegisteredCustomer().DepositHeld);
    }

    [Fact]
    public void A_class_that_is_not_declared_is_refused() =>
        Assert.Throws<RegistryValidationException>(() =>
            Customer.Register("C-000001", "Sablan Family Residence", (CustomerClass)99, Now));

    [Fact]
    public void Updating_details_changes_everything_except_the_account_number_and_the_class()
    {
        var customer = ARegisteredCustomer();

        customer.UpdateDetails("Sablan Family Trust", "Maria Sablan", "maria@example.com", "+1-670-532-0114");

        Assert.Equal("C-000001", customer.AccountNumber);
        Assert.Equal("Sablan Family Trust", customer.Name);

        // The class is no longer correctable (WP-2.15) — it decides the tariff, so it moves through
        // ChangeClass with a reason code and an effective date. Untouched here, as the account number
        // and the status are, and for the same kind of reason: other things depend on it.
        Assert.Equal(CustomerClass.Residential, customer.Class);
        Assert.Equal(CustomerStatus.Prospect, customer.Status);
    }

    [Fact]
    public void A_rejected_correction_leaves_the_customer_exactly_as_it_was()
    {
        // Failure path: the guards run before the first assignment, so an empty name cannot take the
        // rest of the record with it. The transaction would roll the database back either way — this
        // is about the entity a caller still holds on the error path.
        var customer = ARegisteredCustomer();

        Assert.Throws<RegistryValidationException>(() =>
            customer.UpdateDetails("   ", "Maria Sablan", "maria@example.com", null));

        Assert.Equal("Sablan Family Residence", customer.Name);
        Assert.Null(customer.ContactName);
    }

    [Fact]
    public void Changing_status_records_when_it_moved_why_and_from_when()
    {
        var customer = ARegisteredCustomer();
        var later = Now.AddDays(3);
        var effectiveOn = new DateOnly(2026, 9, 1);

        customer.ChangeStatus(
            CustomerStatus.Active,
            TransitionReasonCode.CustomerRequest,
            effectiveOn,
            "Deposit received, service starts Monday.",
            later);

        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.Equal(later, customer.StatusChangedAt);

        // Two different dates on purpose (WP-2.15): StatusChangedAt is when a rep typed it,
        // StatusEffectiveOn is when the utility says it happened.
        Assert.Equal(effectiveOn, customer.StatusEffectiveOn);
        Assert.Equal("Deposit received, service starts Monday.", customer.StatusReason);
    }

    [Fact]
    public void A_status_change_under_a_reason_code_that_does_not_fit_it_is_refused()
    {
        // Failure path, and the rule is in the AGGREGATE rather than only at the edge — so a seeder
        // and a later in-process caller meet it too. PremiseNowTrading explains a class change; it
        // says nothing about why somebody was suspended.
        var customer = ARegisteredCustomer();

        Assert.Throws<RegistryValidationException>(() =>
            customer.ChangeStatus(CustomerStatus.Active, TransitionReasonCode.PremiseNowTrading, Today, null, Now));

        Assert.Equal(CustomerStatus.Prospect, customer.Status);
        Assert.Null(customer.StatusEffectiveOn);
    }

    [Fact]
    public void Changing_class_records_when_it_moved_and_from_when()
    {
        var customer = ARegisteredCustomer();
        var later = Now.AddDays(3);
        var effectiveOn = new DateOnly(2026, 10, 1);

        customer.ChangeClass(CustomerClass.Commercial, TransitionReasonCode.PremiseNowTrading, effectiveOn, later);

        Assert.Equal(CustomerClass.Commercial, customer.Class);
        Assert.Equal(later, customer.ClassChangedAt);
        Assert.Equal(effectiveOn, customer.ClassEffectiveOn);
    }

    [Fact]
    public void Changing_class_to_the_one_already_held_is_refused()
    {
        // Failure path, and a 409 rather than a 400: whether this is a move at all depends on where
        // the customer is now, which edge validation cannot see. The call CustomerTransitions makes
        // about a status already held.
        var customer = ARegisteredCustomer();

        Assert.Throws<RegistryWorkflowException>(() =>
            customer.ChangeClass(CustomerClass.Residential, TransitionReasonCode.MisclassifiedAtIntake, Today, Now));

        Assert.Null(customer.ClassChangedAt);
    }

    [Fact]
    public void A_class_change_under_a_reason_code_that_does_not_fit_it_is_refused()
    {
        // The mirror of the status rule above. UnpaidBalance suspends somebody; it is not a statement
        // about what their premise is used for, and a class change recorded under it would put a
        // sentence in the register that nobody could act on.
        var customer = ARegisteredCustomer();

        Assert.Throws<RegistryValidationException>(() =>
            customer.ChangeClass(CustomerClass.Commercial, TransitionReasonCode.UnpaidBalance, Today, Now));

        Assert.Equal(CustomerClass.Residential, customer.Class);
    }

    [Theory]
    [InlineData(CustomerStatus.Prospect, CustomerStatus.Active)]
    [InlineData(CustomerStatus.Prospect, CustomerStatus.Closed)]
    [InlineData(CustomerStatus.Active, CustomerStatus.Suspended)]
    [InlineData(CustomerStatus.Suspended, CustomerStatus.Active)]
    [InlineData(CustomerStatus.Active, CustomerStatus.Closed)]
    public void The_legal_moves_are_allowed(CustomerStatus from, CustomerStatus to)
    {
        var customer = ARegisteredCustomer(from);

        customer.ChangeStatus(to, TransitionReasonCode.CustomerRequest, Today, reason: null, Now);

        Assert.Equal(to, customer.Status);
    }

    [Theory]
    [InlineData(CustomerStatus.Prospect, CustomerStatus.Suspended)]
    [InlineData(CustomerStatus.Closed, CustomerStatus.Active)]
    [InlineData(CustomerStatus.Active, CustomerStatus.Prospect)]
    [InlineData(CustomerStatus.Active, CustomerStatus.Active)]
    public void An_illegal_move_is_refused(CustomerStatus from, CustomerStatus to)
    {
        // Failure path: an illegal transition is a workflow conflict (409), not a validation error.
        // A closed customer reopening as active would quietly resurrect a record whose history says
        // it ended — the same rule the ledger follows, where a correction is a new entry.
        var customer = ARegisteredCustomer(from);

        Assert.Throws<RegistryWorkflowException>(() =>
            customer.ChangeStatus(to, TransitionReasonCode.CustomerRequest, Today, reason: null, Now));
        Assert.Equal(from, customer.Status);
    }

    [Fact]
    public void A_closed_customer_has_nowhere_left_to_go() =>
        Assert.Empty(ARegisteredCustomer(CustomerStatus.Closed).AllowedTransitions);

    [Fact]
    public void The_allowed_transitions_are_what_a_UI_would_offer() =>
        Assert.Equal(
            [CustomerStatus.Suspended, CustomerStatus.Closed],
            ARegisteredCustomer(CustomerStatus.Active).AllowedTransitions);

    [Fact]
    public void Every_declared_status_is_reachable_from_where_a_registration_starts()
    {
        // Guards the state machine against a status that exists in the enum but that no sequence of
        // legal moves can ever produce — which would be a pill no screen could ever show.
        var reached = new HashSet<CustomerStatus> { CustomerStatus.Prospect };
        var frontier = new Queue<CustomerStatus>([CustomerStatus.Prospect]);

        while (frontier.TryDequeue(out var status))
        {
            foreach (var next in CustomerTransitions.AllowedFrom(status).Where(next => reached.Add(next)))
            {
                frontier.Enqueue(next);
            }
        }

        Assert.Equal(Enum.GetValues<CustomerStatus>().ToHashSet(), reached);
    }
}

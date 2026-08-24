using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The customer aggregate on its own: no database, no host. Everything the registry refuses, it
/// refuses here, which is why these run in milliseconds.
/// </summary>
public class CustomerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

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
    public void A_negative_deposit_is_refused()
    {
        // Failure path: money owed back to a customer is a Finance entry, not a deposit stored as a
        // negative — which would net silently against every other deposit in a total.
        var refused = Assert.Throws<RegistryValidationException>(() =>
            Customer.Register("C-000001", "Sablan Family Residence", CustomerClass.Residential, Now, depositHeld: -1m));

        Assert.Contains("negative", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_deposit_finer_than_a_cent_is_refused_rather_than_silently_rounded()
    {
        // The column is numeric(18,2). Accepting this would round it away in the database, where
        // nobody would ever see which value was stored.
        var refused = Assert.Throws<RegistryValidationException>(() =>
            Customer.Register("C-000001", "Sablan Family Residence", CustomerClass.Residential, Now, depositHeld: 75.125m));

        Assert.Contains("cents", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_class_that_is_not_declared_is_refused() =>
        Assert.Throws<RegistryValidationException>(() =>
            Customer.Register("C-000001", "Sablan Family Residence", (CustomerClass)99, Now));

    [Fact]
    public void Updating_details_changes_everything_except_the_account_number()
    {
        var customer = ARegisteredCustomer();

        customer.UpdateDetails("Sablan Family Trust", CustomerClass.Commercial, "Maria Sablan", "maria@example.com", "+1-670-532-0114", 150.00m);

        Assert.Equal("C-000001", customer.AccountNumber);
        Assert.Equal("Sablan Family Trust", customer.Name);
        Assert.Equal(CustomerClass.Commercial, customer.Class);
        Assert.Equal(150.00m, customer.DepositHeld);

        // Untouched by an update: it is quoted on bills and referred to by every other module.
        Assert.Equal(CustomerStatus.Prospect, customer.Status);
    }

    [Fact]
    public void A_rejected_correction_leaves_the_customer_exactly_as_it_was()
    {
        // Failure path: the guards run before the first assignment, so a bad deposit cannot take
        // the name and class with it. The transaction would roll the database back either way —
        // this is about the entity a caller still holds on the error path.
        var customer = ARegisteredCustomer();

        Assert.Throws<RegistryValidationException>(() =>
            customer.UpdateDetails("Sablan Family Trust", CustomerClass.Commercial, null, null, null, -5m));

        Assert.Equal("Sablan Family Residence", customer.Name);
        Assert.Equal(CustomerClass.Residential, customer.Class);
    }

    [Fact]
    public void Changing_status_records_when_and_why()
    {
        var customer = ARegisteredCustomer();
        var later = Now.AddDays(3);

        customer.ChangeStatus(CustomerStatus.Active, "Deposit received, service starts Monday.", later);

        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.Equal(later, customer.StatusChangedAt);
        Assert.Equal("Deposit received, service starts Monday.", customer.StatusReason);
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

        customer.ChangeStatus(to, reason: null, Now);

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

        Assert.Throws<RegistryWorkflowException>(() => customer.ChangeStatus(to, reason: null, Now));
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

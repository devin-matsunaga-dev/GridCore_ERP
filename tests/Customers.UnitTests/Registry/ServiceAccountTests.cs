using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Registry;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Registry;

/// <summary>
/// The service account aggregate and its state machine, with no infrastructure at all. This is
/// where the lifecycle rules are proved: what a legal move is, what each one does to the account's
/// dates, and that nothing moves without a history line to say so.
/// </summary>
public class ServiceAccountTests
{
    private static readonly DateTimeOffset Opened = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly RegistryActor Agent = new("auth0|cs-agent", "Ana Cruz");
    private static readonly Guid ACustomer = Guid.CreateVersion7(Opened);
    private static readonly Guid APremise = Guid.CreateVersion7(Opened);

    private static ServiceAccount AnAccount(string? reason = null) =>
        ServiceAccount.Open("A-000001", ACustomer, APremise, Agent, Opened, reason);

    [Fact]
    public void An_account_opens_pending_and_not_yet_energised()
    {
        var account = AnAccount("Requested at the counter");

        Assert.Equal(ServiceAccountStatus.Pending, account.Status);
        Assert.Equal("A-000001", account.AccountNumber);
        Assert.Equal(Opened, account.OpenedAt);

        // Asking for service and getting it are two different days.
        Assert.Null(account.ServiceStartedAt);
        Assert.Null(account.ServiceEndedAt);
        Assert.Equal("Requested at the counter", account.StatusReason);
    }

    [Fact]
    public void Opening_records_the_first_line_of_the_history()
    {
        var account = AnAccount("Requested at the counter");

        var entry = Assert.Single(account.History);

        Assert.Null(entry.FromStatus);
        Assert.Equal(ServiceAccountStatus.Pending, entry.ToStatus);
        Assert.Equal("Requested at the counter", entry.Reason);
        Assert.Equal("auth0|cs-agent", entry.ActorId);
        Assert.Equal("Ana Cruz", entry.ActorName);
        Assert.Equal(Opened, entry.RecordedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_account_cannot_be_opened_without_a_number(string accountNumber) =>
        Assert.Throws<RegistryValidationException>(() =>
            ServiceAccount.Open(accountNumber, ACustomer, APremise, Agent, Opened));

    [Fact]
    public void An_account_cannot_be_opened_without_a_customer() =>
        Assert.Throws<RegistryValidationException>(() =>
            ServiceAccount.Open("A-000001", Guid.Empty, APremise, Agent, Opened));

    [Fact]
    public void An_account_cannot_be_opened_without_a_premise() =>
        Assert.Throws<RegistryValidationException>(() =>
            ServiceAccount.Open("A-000001", ACustomer, Guid.Empty, Agent, Opened));

    [Fact]
    public void Starting_service_energises_it_and_stamps_the_start()
    {
        var account = AnAccount();
        var started = Opened.AddDays(3);

        account.StartService(Agent, started, "Connection completed");

        Assert.Equal(ServiceAccountStatus.Active, account.Status);
        Assert.Equal(started, account.ServiceStartedAt);
        Assert.Null(account.ServiceEndedAt);
        Assert.Equal(started, account.StatusChangedAt);
    }

    [Fact]
    public void Stopping_service_cuts_it_but_leaves_the_account_open()
    {
        var account = AnAccount();
        var started = Opened.AddDays(3);
        var stopped = started.AddDays(90);

        account.StartService(Agent, started);
        account.StopService(Agent, stopped, "Disconnected for non-payment");

        Assert.Equal(ServiceAccountStatus.Disconnected, account.Status);
        Assert.Equal(stopped, account.ServiceEndedAt);

        // The premise is still held: a disconnection is reconnectable, so nobody else may be
        // connected there while this account exists.
        Assert.True(account.HoldsPremise);
        Assert.Contains(ServiceAccountStatus.Active, account.AllowedTransitions);
    }

    [Fact]
    public void Reconnecting_restamps_the_start_and_clears_the_end()
    {
        var account = AnAccount();
        var started = Opened.AddDays(3);
        var stopped = started.AddDays(90);
        var reconnected = stopped.AddDays(10);

        account.StartService(Agent, started);
        account.StopService(Agent, stopped, "Disconnected for non-payment");
        account.StartService(Agent, reconnected, "Balance settled");

        Assert.Equal(ServiceAccountStatus.Active, account.Status);
        Assert.Equal(reconnected, account.ServiceStartedAt);
        Assert.Null(account.ServiceEndedAt);

        // The columns answer "since when is this live"; the full sequence is the history's job.
        Assert.Equal(4, account.History.Count);
    }

    [Fact]
    public void Closing_releases_the_premise()
    {
        var account = AnAccount();

        account.StartService(Agent, Opened.AddDays(3));
        account.Close(Agent, Opened.AddDays(100), "Customer moved out");

        Assert.Equal(ServiceAccountStatus.Closed, account.Status);
        Assert.False(account.HoldsPremise);
        Assert.Empty(account.AllowedTransitions);
    }

    [Fact]
    public void Closing_an_account_that_never_started_invents_no_service_period()
    {
        var account = AnAccount();

        account.Close(Agent, Opened.AddDays(30), "Applicant withdrew");

        Assert.Equal(ServiceAccountStatus.Closed, account.Status);
        Assert.Null(account.ServiceStartedAt);
        Assert.Null(account.ServiceEndedAt);
    }

    [Fact]
    public void Every_transition_appends_a_history_line_naming_where_it_came_from()
    {
        var account = AnAccount("Requested at the counter");

        account.StartService(Agent, Opened.AddDays(3), "Connection completed");
        account.StopService(Agent, Opened.AddDays(93), "Disconnected for non-payment");
        account.Close(Agent, Opened.AddDays(120), "Customer moved out");

        Assert.Equal(
            [
                (null, ServiceAccountStatus.Pending),
                (ServiceAccountStatus.Pending, ServiceAccountStatus.Active),
                (ServiceAccountStatus.Active, ServiceAccountStatus.Disconnected),
                (ServiceAccountStatus.Disconnected, ServiceAccountStatus.Closed),
            ],
            account.History.Select(entry => (entry.FromStatus, entry.ToStatus)).ToArray());
    }

    [Fact]
    public void Service_cannot_be_stopped_on_an_account_that_was_never_started()
    {
        // The failure path: nothing was ever connected, so there is nothing to cut. An account that
        // never starts is closed, which is why Pending has no route to Disconnected.
        var account = AnAccount();

        var failure = Assert.Throws<RegistryWorkflowException>(() => account.StopService(Agent, Opened.AddDays(1)));

        Assert.Contains("A-000001", failure.Message, StringComparison.Ordinal);
        Assert.Contains("Pending", failure.Message, StringComparison.Ordinal);

        // And it left nothing behind: the status is where it was and no line was written.
        Assert.Equal(ServiceAccountStatus.Pending, account.Status);
        Assert.Single(account.History);
    }

    [Fact]
    public void A_closed_account_cannot_be_reopened()
    {
        var account = AnAccount();

        account.Close(Agent, Opened.AddDays(1), "Applicant withdrew");

        Assert.Throws<RegistryWorkflowException>(() => account.StartService(Agent, Opened.AddDays(2)));
        Assert.Throws<RegistryWorkflowException>(() => account.StopService(Agent, Opened.AddDays(2)));
        Assert.Throws<RegistryWorkflowException>(() => account.Close(Agent, Opened.AddDays(2)));
    }

    [Fact]
    public void Starting_an_account_that_is_already_active_says_so()
    {
        var account = AnAccount();

        account.StartService(Agent, Opened.AddDays(3));

        var failure = Assert.Throws<RegistryWorkflowException>(() => account.StartService(Agent, Opened.AddDays(4)));

        Assert.Contains("already Active", failure.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The state machine on its own, so a UI can be trusted to render exactly the buttons that work.
/// </summary>
public class ServiceAccountTransitionsTests
{
    [Theory]
    [InlineData(ServiceAccountStatus.Pending, ServiceAccountStatus.Active)]
    [InlineData(ServiceAccountStatus.Pending, ServiceAccountStatus.Closed)]
    [InlineData(ServiceAccountStatus.Active, ServiceAccountStatus.Disconnected)]
    [InlineData(ServiceAccountStatus.Active, ServiceAccountStatus.Closed)]
    [InlineData(ServiceAccountStatus.Disconnected, ServiceAccountStatus.Active)]
    [InlineData(ServiceAccountStatus.Disconnected, ServiceAccountStatus.Closed)]
    public void The_legal_moves_are_allowed(ServiceAccountStatus from, ServiceAccountStatus to) =>
        Assert.True(ServiceAccountTransitions.IsAllowed(from, to));

    [Theory]
    [InlineData(ServiceAccountStatus.Pending, ServiceAccountStatus.Pending)]
    [InlineData(ServiceAccountStatus.Pending, ServiceAccountStatus.Disconnected)]
    [InlineData(ServiceAccountStatus.Active, ServiceAccountStatus.Pending)]
    [InlineData(ServiceAccountStatus.Disconnected, ServiceAccountStatus.Pending)]
    [InlineData(ServiceAccountStatus.Closed, ServiceAccountStatus.Active)]
    [InlineData(ServiceAccountStatus.Closed, ServiceAccountStatus.Pending)]
    [InlineData(ServiceAccountStatus.Closed, ServiceAccountStatus.Disconnected)]
    [InlineData(ServiceAccountStatus.Closed, ServiceAccountStatus.Closed)]
    public void The_illegal_moves_are_refused(ServiceAccountStatus from, ServiceAccountStatus to) =>
        Assert.False(ServiceAccountTransitions.IsAllowed(from, to));

    [Fact]
    public void Closed_is_terminal() =>
        Assert.Empty(ServiceAccountTransitions.AllowedFrom(ServiceAccountStatus.Closed));

    [Fact]
    public void Every_status_is_reachable_from_where_an_account_starts()
    {
        // A status nothing can reach is a status the machine cannot produce, and a filter, a pill
        // and a report would all be written for a state that never happens.
        var reached = new HashSet<ServiceAccountStatus> { ServiceAccountStatus.Pending };
        var frontier = new Queue<ServiceAccountStatus>([ServiceAccountStatus.Pending]);

        while (frontier.TryDequeue(out var status))
        {
            foreach (var next in ServiceAccountTransitions.AllowedFrom(status).Where(next => reached.Add(next)))
            {
                frontier.Enqueue(next);
            }
        }

        Assert.Equal(Enum.GetValues<ServiceAccountStatus>().Order().ToArray(), reached.Order().ToArray());
    }

    [Fact]
    public void Only_a_closed_account_releases_its_premise() =>
        Assert.All(
            Enum.GetValues<ServiceAccountStatus>(),
            status => Assert.Equal(status is not ServiceAccountStatus.Closed, ServiceAccountTransitions.HoldsPremise(status)));
}

/// <summary>
/// The one rule that lives in SQL rather than in C#, checked against the enum it names.
/// </summary>
public class ServiceAccountIndexTests
{
    [Fact]
    public void The_one_account_per_premise_filter_names_the_status_it_means() =>
        // A filter is a string the compiler never reads: rename the enum member and the index
        // silently starts covering closed accounts too, which is the one case it exists to exclude.
        // The database would then refuse to reissue a premise, and only the demo would find out.
        Assert.Equal(
            $"\"status\" <> '{nameof(ServiceAccountStatus.Closed)}'",
            ServiceAccountConfiguration.OnePremiseFilter);

    [Fact]
    public void The_filtered_column_is_the_one_the_status_is_stored_in()
    {
        // And the column, for the same reason — the filter is written against the stored column
        // name, not the property, so renaming the column would leave it pointing at nothing.
        using var host = new CustomersTestHost();

        using var database = host.NewCustomersContext();

        var column = database.Model
            .FindEntityType(typeof(ServiceAccount))!
            .FindProperty(nameof(ServiceAccount.Status))!
            .GetColumnName();

        Assert.Contains($"\"{column}\"", ServiceAccountConfiguration.OnePremiseFilter, StringComparison.Ordinal);
    }
}

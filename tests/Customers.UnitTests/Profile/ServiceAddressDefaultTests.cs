using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Profile;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Profile;

/// <summary>
/// Which service account the mailing address falls back to — pure, so the rule can be argued with
/// without a database. A customer holds several accounts and "the service address" is not a fact
/// until somebody picks one; this is where the picking is written down.
/// </summary>
public class ServiceAddressDefaultTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static readonly RegistryActor Actor = new("auth0|cs-agent", "Ana Cruz");

    private static ServiceAccount AnAccount(string number, DateTimeOffset openedAt) =>
        ServiceAccount.Open(number, Guid.CreateVersion7(Now), Guid.CreateVersion7(openedAt), ServiceType.Electricity, Actor, openedAt);

    [Fact]
    public void No_accounts_means_no_service_address() =>
        // A prospect registered this morning. The honest answer is "nowhere", not the first row of
        // an empty list.
        Assert.Null(ServiceAddressDefault.MostRecentlyActive([]));

    [Fact]
    public void An_account_that_still_holds_its_premise_beats_one_that_does_not()
    {
        var closed = AnAccount("A-000001", Now.AddYears(-1));
        var open = AnAccount("A-000002", Now.AddYears(-3));

        closed.StartService(Actor, Now.AddYears(-1));
        closed.Close(Actor, Now.AddMonths(-1), "Moved out");

        // Closed loses however recently it was live: it is a place the customer has left, and posting
        // a bill to it is posting it to whoever lives there now.
        Assert.Equal(open.Id, ServiceAddressDefault.MostRecentlyActive([closed, open])!.Id);
    }

    [Fact]
    public void The_most_recently_energised_open_account_wins()
    {
        var older = AnAccount("A-000001", Now.AddYears(-3));
        var newer = AnAccount("A-000002", Now.AddYears(-2));

        older.StartService(Actor, Now.AddYears(-3));
        newer.StartService(Actor, Now.AddMonths(-2));

        Assert.Equal(newer.Id, ServiceAddressDefault.MostRecentlyActive([older, newer])!.Id);
    }

    [Fact]
    public void An_account_never_energised_falls_back_to_when_it_was_opened()
    {
        var live = AnAccount("A-000001", Now.AddYears(-5));
        var pending = AnAccount("A-000002", Now.AddDays(-1));

        live.StartService(Actor, Now.AddYears(-5));

        // Asking for service is enough of a claim on a premise to post a bill there, so a Pending
        // account competes on its opening date rather than being ignored.
        Assert.Equal(pending.Id, ServiceAddressDefault.MostRecentlyActive([live, pending])!.Id);
    }

    [Fact]
    public void The_order_is_total_when_two_accounts_share_an_instant()
    {
        var first = AnAccount("A-000001", Now);
        var second = AnAccount("A-000002", Now);

        // Same status, same instant. Without the id tie-break the answer would depend on the order
        // the query happened to return, and post would move between two premises at random.
        var chosen = ServiceAddressDefault.MostRecentlyActive([first, second])!.Id;

        Assert.Equal(chosen, ServiceAddressDefault.MostRecentlyActive([second, first])!.Id);
        Assert.Equal(first.Id > second.Id ? first.Id : second.Id, chosen);
    }
}

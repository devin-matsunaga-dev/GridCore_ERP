using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.UnitTests.RatePlans;

/// <summary>
/// The tariff catalogue and the account-to-tariff assignment Billing owns. The tariffs themselves
/// are reference data and are never written here; the assignment is this module's own row about
/// somebody else's account.
/// </summary>
public class RatePlanServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 9, 0, 0, TimeSpan.Zero);

    private static BillingTestHost NewHost() =>
        new(new FakeClock(Now), new FakeCurrentUser("auth0|officer", "A billing officer"));

    [Fact]
    public async Task Every_shipped_tariff_version_is_listed_with_its_tiers()
    {
        using var host = NewHost();

        var plans = await host.WithTariffsAsync(tariffs => tariffs.ListAsync());

        Assert.Equal(DefaultRatePlans.All.Count, plans.Count);
        Assert.All(plans, plan => Assert.NotEmpty(plan.Tiers));
    }

    [Fact]
    public async Task A_tariff_lists_its_versions_oldest_first()
    {
        using var host = NewHost();

        var versions = await host.WithTariffsAsync(tariffs => tariffs.ListAsync(DefaultRatePlans.ResidentialStandard));

        Assert.Equal(2, versions.Count);
        Assert.Equal(
            [DefaultRatePlans.OriginalEffectiveFrom, DefaultRatePlans.ResidentialRevisionFrom],
            versions.Select(plan => plan.EffectiveFrom));
    }

    [Theory]
    [InlineData("2026-06-30", 12.50)]
    [InlineData("2026-07-01", 13.75)]
    [InlineData("2030-01-01", 13.75)]
    public async Task The_version_in_force_is_chosen_by_date(string on, double charge)
    {
        using var host = NewHost();

        var plan = await host.WithTariffsAsync(tariffs =>
            tariffs.InForceAsync(DefaultRatePlans.ResidentialStandard, DateOnly.Parse(on, null)));

        Assert.Equal((decimal)charge, plan.MonthlyServiceCharge);
    }

    [Fact]
    public async Task Asking_for_a_tariff_that_was_not_yet_published_is_a_404()
    {
        // Failure path: a bill for a period before the utility had published a tariff cannot be
        // priced, and saying so beats inventing rates.
        using var host = NewHost();

        var exception = await Assert.ThrowsAsync<RatePlanNotFoundException>(() =>
            host.WithTariffsAsync(tariffs =>
                tariffs.InForceAsync(DefaultRatePlans.ResidentialStandard, DefaultRatePlans.OriginalEffectiveFrom.AddDays(-1))));

        Assert.Contains("was not in force", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Asking_for_a_tariff_that_does_not_exist_is_a_404()
    {
        using var host = NewHost();

        var exception = await Assert.ThrowsAsync<RatePlanNotFoundException>(() =>
            host.WithTariffsAsync(tariffs => tariffs.InForceAsync("NOPE", new DateOnly(2026, 8, 1))));

        Assert.Contains("adding one is a migration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_account_with_no_assignment_falls_back_to_the_default()
    {
        // No row is an answer. The fallback is flagged as such, because "on the residential tariff
        // because nobody said otherwise" and "because a billing officer put them there" look the
        // same on a bill and are different facts.
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        var tariff = await host.WithTariffsAsync(tariffs => tariffs.ForAccountAsync(account.Id));

        Assert.Equal(DefaultRatePlans.DefaultCode, tariff.RatePlanCode);
        Assert.True(tariff.IsDefault);
        Assert.Null(tariff.AssignedAt);
    }

    [Fact]
    public async Task Assigning_a_tariff_records_who_did_it_and_when()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        var tariff = await host.WithTariffsAsync(tariffs =>
            tariffs.AssignAsync(account.Id, DefaultRatePlans.CommercialStandard));

        Assert.Equal(DefaultRatePlans.CommercialStandard, tariff.RatePlanCode);
        Assert.False(tariff.IsDefault);
        Assert.Equal(Now, tariff.AssignedAt);
        Assert.Null(tariff.ChangedAt);

        await using var context = host.NewBillingContext();

        var stored = await context.AccountRatePlans.SingleAsync();

        Assert.Equal("auth0|officer", stored.ActorId);
        Assert.Equal("A billing officer", stored.ActorName);
    }

    [Fact]
    public async Task Moving_an_account_to_another_tariff_keeps_one_row_and_is_audited()
    {
        // ONE tariff per account is a database fact. An account billed on two tariffs is two bills
        // for one period, and which one the customer owes would be decided by the query plan.
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        await host.WithTariffsAsync(tariffs => tariffs.AssignAsync(account.Id, DefaultRatePlans.CommercialStandard));

        var moved = await host.WithTariffsAsync(tariffs =>
            tariffs.AssignAsync(account.Id, DefaultRatePlans.ResidentialStandard));

        Assert.Equal(DefaultRatePlans.ResidentialStandard, moved.RatePlanCode);
        Assert.Equal(Now, moved.ChangedAt);

        // Still "not the default" — it is a tariff somebody chose, which happens to be the same one
        // the fallback would have picked.
        Assert.False(moved.IsDefault);

        await using var billing = host.NewBillingContext();
        await using var platform = host.NewPlatformContext();

        Assert.Equal(1, await billing.AccountRatePlans.CountAsync());

        var entries = await platform.AuditEntries
            .Where(audit => audit.Action == AuditActions.AccountRatePlanAssigned)
            .ToListAsync();

        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry => Assert.Equal(account.Id.ToString(), entry.EntityId));

        // Invariant 5: the entry says what it moved from as well as what it moved to.
        Assert.Contains(DefaultRatePlans.CommercialStandard, entries[1].BeforeJson!, StringComparison.Ordinal);
        Assert.Contains(DefaultRatePlans.ResidentialStandard, entries[1].AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Assigning_the_tariff_an_account_is_already_on_writes_nothing()
    {
        // A no-op rather than a conflict, deliberately unlike WP-1.4's stock adjustment that agrees
        // with the system: there the ledger would gain a line explaining nothing, here there is
        // nothing to write at all. The caller still gets the tariff back — they asked for a state
        // that already holds.
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        await host.WithTariffsAsync(tariffs => tariffs.AssignAsync(account.Id, DefaultRatePlans.CommercialStandard));

        var again = await host.WithTariffsAsync(tariffs =>
            tariffs.AssignAsync(account.Id, DefaultRatePlans.CommercialStandard));

        Assert.Equal(DefaultRatePlans.CommercialStandard, again.RatePlanCode);
        Assert.Null(again.ChangedAt);

        await using var platform = host.NewPlatformContext();

        Assert.Equal(
            1,
            await platform.AuditEntries.CountAsync(audit => audit.Action == AuditActions.AccountRatePlanAssigned));
    }

    [Fact]
    public async Task Assigning_a_tariff_to_an_account_that_does_not_exist_is_a_404()
    {
        // Failure path across the module boundary: the answer depends on the Customers registry,
        // which no validator at this edge can see.
        using var host = NewHost();

        await Assert.ThrowsAsync<ServiceAccountNotFoundException>(() =>
            host.WithTariffsAsync(tariffs =>
                tariffs.AssignAsync(Guid.CreateVersion7(), DefaultRatePlans.CommercialStandard)));

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.AccountRatePlans.CountAsync());
    }

    [Fact]
    public async Task Assigning_a_tariff_the_utility_does_not_publish_is_a_404()
    {
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        await Assert.ThrowsAsync<RatePlanNotFoundException>(() =>
            host.WithTariffsAsync(tariffs => tariffs.AssignAsync(account.Id, "MADE-UP")));

        await using var context = host.NewBillingContext();

        Assert.Equal(0, await context.AccountRatePlans.CountAsync());
    }

    [Fact]
    public async Task An_account_is_assigned_a_code_and_not_a_version()
    {
        // The whole point of effective dating: a repricing reaches every account on the tariff
        // without anybody reassigning them.
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        await host.WithTariffsAsync(tariffs => tariffs.AssignAsync(account.Id, DefaultRatePlans.ResidentialStandard));

        await using var context = host.NewBillingContext();

        var stored = await context.AccountRatePlans.SingleAsync();

        Assert.Equal(DefaultRatePlans.ResidentialStandard, stored.RatePlanCode);
        Assert.DoesNotContain(
            stored.GetType().GetProperties(),
            property => property.Name.Contains("RatePlanId", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_assignment_row_for_one_account_is_refused_by_the_database()
    {
        // Failure path: ux_account_rate_plans_account is what makes "one tariff per account" a fact
        // rather than a convention the service hopes holds.
        using var host = NewHost();

        var account = host.Accounts.Add(Guid.CreateVersion7());

        await host.WithTariffsAsync(tariffs => tariffs.AssignAsync(account.Id, DefaultRatePlans.CommercialStandard));

        await using var context = host.NewBillingContext();

        context.AccountRatePlans.Add(AccountRatePlan.Assign(
            account.Id,
            DefaultRatePlans.ResidentialStandard,
            new Platform.Registry.RegistryActor("auth0|someone", "Someone else"),
            Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}

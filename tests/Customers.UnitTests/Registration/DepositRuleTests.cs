using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.UnitTests.Registration;

/// <summary>
/// The deposit schedule as shipped reference data, keyed on (customer class × service) since
/// WP-2.17. What these prove: every declared pair has exactly one rule, the ids are the stable ones
/// a migration can re-seed, the two-part rule picks the greater of its halves, and the assessment a
/// customer is quoted comes from the table rather than from a constant somebody can edit.
/// </summary>
public class DepositRuleTests
{
    [Fact]
    public void Every_declared_class_and_service_pair_has_a_rule() =>
        // The guard the configuration runs at model-build time, asserted here so the failure is a
        // named test rather than a startup exception in whichever environment migrated first.
        DepositRules.RequireComplete(DepositRules.All);

    [Fact]
    public void The_schedule_is_the_cross_product_of_the_classes_and_the_services() =>
        Assert.Equal(
            Enum.GetValues<CustomerClass>().Length * ServiceTypes.All.Count,
            DepositRules.All.Count);

    [Fact]
    public void A_pair_with_no_rule_is_refused_by_name()
    {
        // One service missing for one class, not a whole class: the failure has to name the PAIR, or
        // it sends somebody reading eight rows to find the one that is not there.
        var missing = DepositRules.All
            .Where(rule => rule.CustomerClass != CustomerClass.Commercial || rule.ServiceType != ServiceType.Water)
            .ToList();

        var exception = Assert.Throws<RegistryValidationException>(() => DepositRules.RequireComplete(missing));

        Assert.Contains("Commercial", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Water", exception.Message, StringComparison.Ordinal);
        Assert.Contains("migration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_rules_for_one_pair_are_refused()
    {
        var duplicated = DepositRules.All.Concat([DepositRules.All[0]]).ToList();

        var exception = Assert.Throws<RegistryValidationException>(() => DepositRules.RequireComplete(duplicated));

        Assert.Contains("exactly one rule", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_minimum_is_a_whole_number_of_cents_and_never_negative() =>
        Assert.All(DepositRules.All, rule =>
        {
            Assert.True(Money.IsRounded(rule.MinimumAmount));
            Assert.True(rule.MinimumAmount >= Money.Zero);
        });

    [Fact]
    public void A_rule_id_is_derived_from_its_class_and_service_so_the_migration_re_seeds_the_same_row() =>
        Assert.All(DepositRules.All, rule =>
            Assert.Equal(ReferenceId.For(DepositRules.AuthoredAt, rule.RuleKey), rule.Id));

    [Fact]
    public void The_electricity_minimums_are_the_figures_the_class_keyed_schedule_asked_for() =>
        // WORK_PACKAGES.md's rule, as an assertion: re-keying the schedule must not silently reprice
        // a customer who has already been assessed. WP-2.8 shipped $75 residential and $450
        // commercial, and every account that existed before WP-2.17 migrates to Electricity.
        Assert.Equal(
            [75.00m, 450.00m],
            DepositRules.All
                .Where(rule => rule.ServiceType == ServiceType.Electricity)
                .OrderBy(rule => rule.CustomerClass)
                .Select(rule => rule.MinimumAmount));

    [Fact]
    public void Every_unmetered_service_ships_a_flat_rule_and_every_metered_one_a_usage_basis() =>
        Assert.All(DepositRules.All, rule =>
            Assert.Equal(ServiceTypes.IsMetered(rule.ServiceType), rule.HasUsageBasis));

    [Fact]
    public void A_minimum_finer_than_a_cent_is_refused_rather_than_rounded() =>
        // The call WP-1.1 made for a deposit somebody types, made again for the figure the schedule
        // quotes: a rule the column would truncate is a rule nobody chose.
        Assert.Throws<ArgumentOutOfRangeException>(() => DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Electricity,
            75.125m,
            DepositRules.Currency,
            "Finer than a cent."));

    [Fact]
    public void A_negative_minimum_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Electricity,
            -1m,
            DepositRules.Currency,
            "Owed back is a refund, not a rule."));

    [Fact]
    public void Half_a_usage_basis_is_refused()
    {
        var exception = Assert.Throws<ArgumentException>(() => DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Electricity,
            75.00m,
            DepositRules.Currency,
            "Months with nothing to price them at.",
            usageMonths: 2));

        Assert.Contains("both or neither", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_usage_basis_on_an_unmetered_service_is_refused()
    {
        var exception = Assert.Throws<ArgumentException>(() => DepositRule.Reference(
            CustomerClass.Residential,
            ServiceType.Wastewater,
            30.00m,
            DepositRules.Currency,
            "There is no wastewater meter.",
            usageMonths: 2,
            usageRate: 1.0000m));

        Assert.Contains("unmetered", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_usage_based_assessment_below_the_minimum_returns_the_minimum()
    {
        // 10 kWh a month at $0.32 over two months is $6.40 — well under the $75 floor. The floor is
        // a floor and never a ceiling, which is the whole of why Assess returns a max.
        var rule = ResidentialElectric();

        var basis = rule.Assess(averageMonthlyUsage: 10m);

        Assert.Equal(rule.MinimumAmount, basis.Amount);
        Assert.False(basis.IsUsageBased);
        Assert.Null(basis.AverageMonthlyUsage);
    }

    [Fact]
    public void A_usage_based_assessment_above_the_minimum_returns_the_usage_figure()
    {
        // 400 kWh a month at $0.32 over two months is $256.00, which clears the $75 floor.
        var rule = ResidentialElectric();

        var basis = rule.Assess(averageMonthlyUsage: 400m);

        Assert.Equal(256.00m, basis.Amount);
        Assert.True(basis.IsUsageBased);
        Assert.Equal(400m, basis.AverageMonthlyUsage);
        Assert.Equal(DepositRules.UsageMonths, basis.UsageMonths);
    }

    [Fact]
    public void No_reading_history_falls_back_to_the_minimum_rather_than_assessing_zero()
    {
        // The distinction the usage seam keeps alive: null is "nobody has measured this premise",
        // and treating it as zero would assess every new connection at nothing at all.
        var rule = ResidentialElectric();

        var basis = rule.Assess(averageMonthlyUsage: null);

        Assert.Equal(rule.MinimumAmount, basis.Amount);
        Assert.False(basis.IsUsageBased);
    }

    [Fact]
    public void An_unmetered_rule_ignores_usage_it_should_never_have_been_given()
    {
        var rule = DepositRules.All.Single(candidate =>
            candidate.CustomerClass == CustomerClass.Residential && candidate.ServiceType == ServiceType.Wastewater);

        var basis = rule.Assess(averageMonthlyUsage: 5_000m);

        Assert.Equal(rule.MinimumAmount, basis.Amount);
        Assert.False(basis.IsUsageBased);
    }

    [Fact]
    public async Task The_schedule_is_read_from_the_table_that_the_migration_seeded()
    {
        using var host = new CustomersTestHost();

        var schedule = await host.WithDepositRulesAsync(deposits => deposits.ListAsync());

        // Ordered by the enums, not by the stored names: residential first as the class list reads,
        // and electricity first as the service list does.
        Assert.Equal(
            DepositRules.All.Select(rule => rule.RuleKey),
            schedule.Select(assessment => DepositRule.KeyFor(assessment.CustomerClass, assessment.ServiceType)));

        Assert.Equal(ResidentialElectric().MinimumAmount, schedule[0].Amount);
    }

    [Theory]
    [InlineData(CustomerClass.Residential, ServiceType.Electricity)]
    [InlineData(CustomerClass.Residential, ServiceType.Wastewater)]
    [InlineData(CustomerClass.Commercial, ServiceType.Electricity)]
    [InlineData(CustomerClass.Commercial, ServiceType.Water)]
    public async Task A_pair_is_assessed_at_what_the_table_says(CustomerClass customerClass, ServiceType serviceType)
    {
        using var host = new CustomersTestHost();

        var assessment = await host.WithDepositRulesAsync(deposits => deposits.AssessAsync(customerClass, serviceType));

        var shipped = DepositRules.All.Single(rule =>
            rule.CustomerClass == customerClass && rule.ServiceType == serviceType);

        // The published floor, with no usage in it: an intake has no premise history to average.
        Assert.Equal(shipped.MinimumAmount, assessment.Amount);
        Assert.False(assessment.IsUsageBased);
        Assert.Equal(shipped.Id, assessment.RuleId);
        Assert.Equal(shipped.Description, assessment.Description);
    }

    private static DepositRule ResidentialElectric() =>
        DepositRules.All.Single(rule =>
            rule.CustomerClass == CustomerClass.Residential && rule.ServiceType == ServiceType.Electricity);
}

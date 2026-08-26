using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.UnitTests.Registration;

/// <summary>
/// The deposit schedule as shipped reference data. What these prove: every class GridCore declares
/// has exactly one rule, the ids are the stable ones a migration can re-seed, and the assessment a
/// customer is quoted comes from the table rather than from a constant somebody can edit.
/// </summary>
public class DepositRuleTests
{
    [Fact]
    public void Every_declared_customer_class_has_a_rule() =>
        // The guard the configuration runs at model-build time, asserted here so the failure is a
        // named test rather than a startup exception in whichever environment migrated first.
        DepositRules.RequireComplete(DepositRules.All);

    [Fact]
    public void A_class_with_no_rule_is_refused_by_name()
    {
        var missingCommercial = DepositRules.All.Where(rule => rule.CustomerClass != CustomerClass.Commercial).ToList();

        var exception = Assert.Throws<RegistryValidationException>(() => DepositRules.RequireComplete(missingCommercial));

        Assert.Contains("Commercial", exception.Message, StringComparison.Ordinal);
        Assert.Contains("migration", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_rules_for_one_class_are_refused()
    {
        var duplicated = DepositRules.All.Concat([DepositRules.All[0]]).ToList();

        Assert.Throws<RegistryValidationException>(() => DepositRules.RequireComplete(duplicated));
    }

    [Fact]
    public void Every_amount_is_a_whole_number_of_cents_and_never_negative() =>
        Assert.All(DepositRules.All, rule =>
        {
            Assert.True(Money.IsRounded(rule.Amount));
            Assert.True(rule.Amount >= Money.Zero);
        });

    [Fact]
    public void A_rule_id_is_derived_from_its_class_so_the_migration_re_seeds_the_same_row() =>
        Assert.All(DepositRules.All, rule =>
            Assert.Equal(ReferenceId.For(DepositRules.AuthoredAt, rule.CustomerClass.ToString()), rule.Id));

    [Fact]
    public void A_rule_finer_than_a_cent_is_refused_rather_than_rounded() =>
        // The call WP-1.1 made for a deposit somebody types, made again for the figure the schedule
        // quotes: a rule the column would truncate is a rule nobody chose.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DepositRule.Reference(CustomerClass.Residential, 75.125m, DepositRules.Currency, "Finer than a cent."));

    [Fact]
    public void A_negative_rule_is_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DepositRule.Reference(CustomerClass.Residential, -1m, DepositRules.Currency, "Owed back is a refund, not a rule."));

    [Fact]
    public async Task The_schedule_is_read_from_the_table_that_the_migration_seeded()
    {
        using var host = new CustomersTestHost();

        var schedule = await host.WithDepositRulesAsync(deposits => deposits.ListAsync());

        // Ordered by the enum, not by the stored name: residential first, as the class list reads.
        Assert.Equal(
            [CustomerClass.Residential, CustomerClass.Commercial],
            schedule.Select(assessment => assessment.CustomerClass));

        Assert.Equal(DepositRules.All.Single(rule => rule.CustomerClass == CustomerClass.Residential).Amount, schedule[0].Amount);
    }

    [Theory]
    [InlineData(CustomerClass.Residential)]
    [InlineData(CustomerClass.Commercial)]
    public async Task A_class_is_assessed_at_what_the_table_says(CustomerClass customerClass)
    {
        using var host = new CustomersTestHost();

        var assessment = await host.WithDepositRulesAsync(deposits => deposits.AssessAsync(customerClass));

        var shipped = DepositRules.All.Single(rule => rule.CustomerClass == customerClass);

        Assert.Equal(shipped.Amount, assessment.Amount);
        Assert.Equal(shipped.Id, assessment.RuleId);
        Assert.Equal(shipped.Description, assessment.Description);
    }
}

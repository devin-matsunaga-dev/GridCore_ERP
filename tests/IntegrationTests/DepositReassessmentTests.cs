using GridCore.Contracts.Directories;
using GridCore.Contracts.Services;
using GridCore.IntegrationTests.Infrastructure;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.ServiceAccounts;
using GridCore.Modules.Customers.Features.ServiceLocations;
using GridCore.Modules.Metering.Features.Meters;
using GridCore.Modules.Metering.Features.Readings;
using GridCore.Platform.Monetary;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.IntegrationTests;

/// <summary>
/// WP-2.17's cross-module read against real Postgres: a deposit assessed in Customers is priced off
/// consumption measured in Metering, over a seam neither module could fake for the other.
/// </summary>
/// <remarks>
/// <para>
/// The fast tier proves everything that does not need infrastructure — the averaging arithmetic, the
/// floor-versus-usage rule, the shortfall never going negative, the unmetered account never asking
/// the register anything — all in milliseconds, with a double standing in for Metering. What only a
/// container can show is the claim the package actually makes: that the double and the real thing
/// agree, and that <c>UsageDirectory</c>'s query translates on Postgres against a schema whose
/// migration has been applied.
/// </para>
/// <para>
/// This is the WP's gate-tier case: meter readings → average → assessed deposit.
/// </para>
/// </remarks>
[Collection(GateCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DepositReassessmentTests(GateFixture fixture) : IAsyncLifetime
{
    /// <inheritdoc />
    public Task InitializeAsync() => fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_metered_premise_is_assessed_off_what_the_reading_register_actually_holds()
    {
        var (customerId, premiseId) = await AServedPremiseAsync("R1");

        // Two reads of 400 units each. The register is Metering's; Customers reads the average
        // across the boundary and has never heard of a metering schema.
        //
        // THE AVERAGE IS NOT A REALISTIC MONTHLY FIGURE and is not asserted as one. This fixture
        // runs on the wall clock, so the two readings are seconds apart and 800 units over a few
        // seconds scales to an enormous month. What that costs is nothing: the realistic cases —
        // a missed cycle, a light user falling back to the floor, no history at all — are proven in
        // the fast tier, where the clock is a FakeClock and can be moved a month at a time. What
        // this proves is the half the fast tier cannot: that the query translates on Postgres, that
        // the seam carries the figure, and that the rule is applied to whatever comes back.
        await ReadAsync(premiseId, "SN-R1", 1_000m, [1_400m, 1_800m]);

        await using var scope = fixture.CreateScope();

        var usage = await scope.ServiceProvider.GetRequiredService<IUsageDirectory>()
            .AverageMonthlyAtLocationAsync(premiseId, ServiceType.Electricity, DepositRules.UsageMonths);

        Assert.True(usage.HasHistory);
        Assert.Equal(2, usage.PeriodsConsidered);

        var requirement = await scope.ServiceProvider.GetRequiredService<IDepositReassessmentService>()
            .ReassessAsync(customerId);

        var line = Assert.Single(requirement.Accounts);

        Assert.True(line.HasUsageHistory);
        Assert.True(line.Assessment.IsUsageBased);
        Assert.Equal(usage.AverageMonthlyUsage, line.Assessment.AverageMonthlyUsage);

        // The identity, not a plausible-looking number: what the customer is asked for IS the rule
        // applied to the average the register handed over.
        var rule = DepositRules.All.Single(candidate =>
            candidate.CustomerClass == CustomerClass.Residential && candidate.ServiceType == ServiceType.Electricity);

        Assert.Equal(
            Money.Round(usage.AverageMonthlyUsage!.Value * rule.UsageMonths!.Value * rule.UsageRate!.Value),
            line.Assessment.Amount);

        Assert.Equal(line.Assessment.Amount, requirement.RequiredAmount);

        // Nothing held yet, so the shortfall is the whole of it.
        Assert.Equal(requirement.RequiredAmount, requirement.ShortfallAmount);
    }

    [Fact]
    public async Task A_premise_nobody_has_read_falls_back_to_the_published_minimum()
    {
        // No history is not zero usage. A brand-new connection is asked for the floor, which is
        // exactly the customer a deposit exists for.
        var (customerId, _) = await AServedPremiseAsync("R2");

        await using var scope = fixture.CreateScope();

        var requirement = await scope.ServiceProvider.GetRequiredService<IDepositReassessmentService>()
            .ReassessAsync(customerId);

        var line = Assert.Single(requirement.Accounts);

        Assert.False(line.HasUsageHistory);
        Assert.False(line.Assessment.IsUsageBased);
        Assert.Equal(line.Assessment.MinimumAmount, line.Assessment.Amount);
    }

    [Fact]
    public async Task A_customer_taking_three_supplies_is_assessed_for_each_and_the_total_is_their_sum()
    {
        var (customerId, premiseId) = await AServedPremiseAsync("R3");

        foreach (var serviceType in (ServiceType[])[ServiceType.Water, ServiceType.Wastewater])
        {
            await using var scope = fixture.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IServiceAccountService>()
                .OpenAsync(new OpenServiceAccountInput(customerId, premiseId, serviceType));
        }

        await using var read = fixture.CreateScope();

        var requirement = await read.ServiceProvider.GetRequiredService<IDepositReassessmentService>()
            .ReassessAsync(customerId);

        Assert.Equal(
            [ServiceType.Electricity, ServiceType.Water, ServiceType.Wastewater],
            requirement.Accounts.Select(line => line.Assessment.ServiceType));

        Assert.Equal(
            requirement.Accounts.Sum(line => line.Assessment.Amount),
            requirement.RequiredAmount);

        // The unmetered line is flat and says so: there is no wastewater meter to average.
        var wastewater = requirement.Accounts.Single(line => line.Assessment.ServiceType == ServiceType.Wastewater);

        Assert.False(wastewater.HasUsageHistory);
        Assert.Null(wastewater.Assessment.UsageMonths);
    }

    /// <summary>Registers a customer, a premise and one energised electricity account at it.</summary>
    private async Task<(Guid CustomerId, Guid PremiseId)> AServedPremiseAsync(string tag)
    {
        Guid customerId;
        Guid premiseId;

        await using (var scope = fixture.CreateScope())
        {
            customerId = (await scope.ServiceProvider.GetRequiredService<ICustomerService>()
                .RegisterAsync(new RegisterCustomerInput($"Reassessment customer {tag}", CustomerClass.Residential)))
                .Id;

            premiseId = (await scope.ServiceProvider.GetRequiredService<IServiceLocationService>()
                .RegisterAsync(new ServiceLocationInput(
                    Address.Create($"{tag} As Nieves Road", "Songsong", "Rota", "MP", postalCode: "96951"),
                    "Meter on the north wall")))
                .Id;
        }

        await using (var scope = fixture.CreateScope())
        {
            var accounts = scope.ServiceProvider.GetRequiredService<IServiceAccountService>();

            var account = await accounts.OpenAsync(
                new OpenServiceAccountInput(customerId, premiseId, ServiceType.Electricity, "Requested at the counter"));

            await accounts.StartServiceAsync(account.Id, "Connected.");
        }

        return (customerId, premiseId);
    }

    /// <summary>Fits a meter at <paramref name="premiseId"/> and records <paramref name="readings"/> off it.</summary>
    private async Task ReadAsync(Guid premiseId, string serial, decimal installationReading, decimal[] readings)
    {
        Guid meterId;

        await using (var scope = fixture.CreateScope())
        {
            var meters = scope.ServiceProvider.GetRequiredService<IMeterService>();
            var meter = await meters.RegisterAsync(new RegisterMeterInput(serial, MeterType.SinglePhase, Manufacturer: "Sensus"));

            meterId = meter.Meter.Id;

            await meters.AssignAsync(meterId, new AssignMeterInput(premiseId, installationReading));
        }

        foreach (var reading in readings)
        {
            // A scope each, so every reading commits on its own request — the shape a real cycle has,
            // and what makes the previous read a row rather than a tracked entity.
            await using var scope = fixture.CreateScope();

            await scope.ServiceProvider.GetRequiredService<IMeterReadingService>()
                .RecordAsync(meterId, new RecordReadingInput(reading, Note: "Read off the card"));
        }
    }
}

using GridCore.Modules.Billing.Features.Fees;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;

namespace GridCore.Modules.Billing.UnitTests.Fees;

/// <summary>
/// The published schedule as the application reads it — off the table, never off the static list.
/// The rows reach this host through the configuration's <c>HasData</c>, which is how a migrated
/// database gets them too.
/// </summary>
public class FeeScheduleServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private static BillingTestHost NewHost() => new(new FakeClock(Now));

    [Fact]
    public async Task A_fee_is_priced_off_the_schedule_in_force_on_the_day()
    {
        using var host = NewHost();

        var today = await host.WithFeesAsync(catalogue =>
            catalogue.AssessAsync(FeeCode.Reconnection, new DateOnly(2026, 8, 26)));

        Assert.Equal(60.00m, today.Amount);
        Assert.Equal(FeeSchedules.ReconnectionRevisionFrom, today.EffectiveFrom);
        Assert.Equal("USD", today.Currency);
    }

    [Fact]
    public async Task A_fee_raised_for_an_earlier_day_prices_off_the_schedule_that_was_in_force_then()
    {
        // THE REASON RaisedOn IS ITS OWN FIELD. A reconnection performed in June and charged in
        // August is a June figure, and the desk is not asked to remember what the schedule said.
        using var host = NewHost();

        var june = await host.WithFeesAsync(catalogue =>
            catalogue.AssessAsync(FeeCode.Reconnection, new DateOnly(2026, 6, 15)));

        Assert.Equal(50.00m, june.Amount);
        Assert.Equal(FeeSchedules.OriginalEffectiveFrom, june.EffectiveFrom);
    }

    [Fact]
    public async Task The_assessment_names_the_row_that_priced_it()
    {
        // What a charge stamps, and the whole reason the assessment carries an id at all: a figure
        // that cannot be traced back to a published row is a figure nobody can defend.
        using var host = NewHost();

        var assessment = await host.WithFeesAsync(catalogue =>
            catalogue.AssessAsync(FeeCode.ServiceConnection, new DateOnly(2026, 8, 26)));

        var row = FeeSchedules.InForceOn(FeeCode.ServiceConnection, new DateOnly(2026, 8, 26))!;

        Assert.Equal(row.Id, assessment.FeeScheduleId);
        Assert.Equal(row.Name, assessment.Name);
    }

    [Fact]
    public async Task A_fee_that_is_not_one_GridCore_declares_is_refused_as_a_bad_request()
    {
        // THE FAILURE PATH WORK_PACKAGES.md NAMES: an unknown fee code is a 400. A value cast in
        // from the wire reaches the service, and it is refused with the list of fees that do exist
        // rather than with a 404 about a resource that was never a resource.
        using var host = NewHost();

        var refusal = await Assert.ThrowsAsync<BillingValidationException>(() =>
            host.WithFeesAsync(catalogue => catalogue.AssessAsync((FeeCode)987, new DateOnly(2026, 8, 26))));

        Assert.Contains(nameof(FeeCode.Reconnection), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fee_asked_for_before_it_was_published_is_refused_and_says_when_it_starts()
    {
        using var host = NewHost();

        var refusal = await Assert.ThrowsAsync<BillingValidationException>(() =>
            host.WithFeesAsync(catalogue =>
                catalogue.AssessAsync(FeeCode.Reconnection, FeeSchedules.OriginalEffectiveFrom.AddDays(-1))));

        Assert.Contains(FeeSchedules.OriginalEffectiveFrom.ToString("yyyy-MM-dd"), refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_schedule_lists_one_row_per_fee_in_force_on_the_day()
    {
        using var host = NewHost();

        var today = await host.WithFeesAsync(catalogue => catalogue.ListAsync(new DateOnly(2026, 8, 26)));

        // One row per published fee, not every version ever published: a schedule screen asks what
        // things cost today.
        Assert.Equal(Enum.GetValues<FeeCode>().Length, today.Count);
        Assert.Distinct(today.Select(fee => fee.Code));
        Assert.Equal(60.00m, today.Single(fee => fee.Code == FeeCode.Reconnection).Amount);
    }

    [Fact]
    public async Task The_schedule_read_for_an_earlier_day_shows_that_days_figures()
    {
        using var host = NewHost();

        var june = await host.WithFeesAsync(catalogue => catalogue.ListAsync(new DateOnly(2026, 6, 1)));

        Assert.Equal(50.00m, june.Single(fee => fee.Code == FeeCode.Reconnection).Amount);
    }

    [Fact]
    public async Task The_schedule_read_before_anything_was_published_is_empty()
    {
        using var host = NewHost();

        // Not an error: a day before the utility published anything genuinely has no schedule, and a
        // list is the honest shape for "nothing yet".
        Assert.Empty(await host.WithFeesAsync(catalogue =>
            catalogue.ListAsync(FeeSchedules.OriginalEffectiveFrom.AddDays(-1))));
    }
}

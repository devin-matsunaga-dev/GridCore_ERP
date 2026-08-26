using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.Features.Fees;

/// <summary>
/// What a published fee costs on a given day, and the schedule row that says so.
/// </summary>
/// <remarks>
/// <see cref="FeeScheduleId"/> is the whole point of this type: an assessment is stamped onto the
/// charge it prices, so the figure can be traced back to the row that produced it after the schedule
/// has moved on. The shape <c>DepositAssessment.RuleId</c> already gives a deposit.
/// </remarks>
/// <param name="Code">Which published fee.</param>
/// <param name="Name">What the line says when it reaches a bill.</param>
/// <param name="Description">What it covers and where the figure came from.</param>
/// <param name="ServiceType">The service it is published against.</param>
/// <param name="Amount">What it costs on the day asked about.</param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="EffectiveFrom">The day that figure took effect — why this one and not another.</param>
/// <param name="FeeScheduleId">Which schedule row answered.</param>
public sealed record FeeAssessment(
    FeeCode Code,
    string Name,
    string Description,
    ServiceType ServiceType,
    decimal Amount,
    string Currency,
    DateOnly EffectiveFrom,
    Guid FeeScheduleId)
{
    /// <summary>Reads an assessment off a schedule row.</summary>
    public static FeeAssessment Of(FeeScheduleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new FeeAssessment(
            entry.Code,
            entry.Name,
            entry.Description,
            entry.ServiceType,
            entry.Amount,
            entry.Currency,
            entry.EffectiveFrom,
            entry.Id);
    }
}

/// <summary>The published fee schedule as the application reads it.</summary>
public interface IFeeScheduleService
{
    /// <summary>
    /// What <paramref name="code"/> costs on <paramref name="on"/>.
    /// </summary>
    /// <exception cref="BillingValidationException">
    /// The code is not one GridCore declares, or the schedule publishes no figure for it on that day.
    /// </exception>
    Task<FeeAssessment> AssessAsync(FeeCode code, DateOnly on, CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole schedule as it stands on <paramref name="on"/> — one row per published fee, in code
    /// order. What a screen shows before a fee is chosen.
    /// </summary>
    Task<IReadOnlyList<FeeAssessment>> ListAsync(DateOnly on, CancellationToken cancellationToken = default);
}

/// <summary>
/// The fee schedule read from <c>billing.fee_schedule</c>.
/// </summary>
/// <remarks>
/// Reads the table rather than <see cref="FeeSchedules.All"/> deliberately, exactly as
/// <c>DepositRuleService</c> does. The static list is how the rows are <i>seeded</i>; the figure in
/// force is whatever the database holds, so changing one is a migration and not a redeploy of the
/// domain. Read-only: a published fee is corrected by migration, never by an endpoint.
/// </remarks>
public sealed class FeeScheduleService(BillingDbContext database) : IFeeScheduleService
{
    /// <inheritdoc />
    public async Task<FeeAssessment> AssessAsync(FeeCode code, DateOnly on, CancellationToken cancellationToken = default)
    {
        // An undefined value cast in from the wire. A 400 rather than a 404: the caller named
        // something that is not a fee at all, which is a malformed request rather than a fee the
        // utility has never published.
        if (!Enum.IsDefined(code))
        {
            throw new BillingValidationException(
                $"'{code}' is not a fee GridCore declares. The published fees are: {string.Join(", ", Enum.GetNames<FeeCode>())}.");
        }

        // Every version of this one fee, and the choosing done in memory. There are a handful of
        // versions of a fee at most, and the alternative — a "latest effective_from on or before"
        // query — is the same rule written a second time in SQL where it can drift from
        // FeeScheduleSelector, which is the rule a charge and a test both read.
        var versions = await database.FeeSchedule
            .AsNoTracking()
            .Where(entry => entry.Code == code)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (FeeScheduleSelector.InForceOn(versions, code, on) is not { } entry)
        {
            // Not a 404, for the reason DepositRuleService gives about a customer class: the caller
            // asked about a fee GridCore declares, and this deployment's schedule failing to cover
            // the day is a configuration problem rather than a missing resource. Named so whoever
            // reads the log knows the fix is a migration.
            throw new BillingValidationException(
                versions.Count is 0
                    ? $"No fee is published for {code}. A fee schedule is reference data: add the row in a migration."
                    : $"{code} was not published on {on:yyyy-MM-dd}; the earliest figure takes effect on "
                      + $"{versions.Min(candidate => candidate.EffectiveFrom):yyyy-MM-dd}.");
        }

        return FeeAssessment.Of(entry);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FeeAssessment>> ListAsync(DateOnly on, CancellationToken cancellationToken = default)
    {
        var published = await database.FeeSchedule
            .AsNoTracking()
            .Where(entry => entry.EffectiveFrom <= on)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // One row per fee — the version in force — rather than every version ever published. A
        // schedule screen asks what things cost today, and the selector is what decides which row
        // that is, here as at the counter.
        return
        [
            .. Enum.GetValues<FeeCode>()
                .Select(code => FeeScheduleSelector.InForceOn(published, code, on))
                .OfType<FeeScheduleEntry>()
                .Select(FeeAssessment.Of),
        ];
    }
}

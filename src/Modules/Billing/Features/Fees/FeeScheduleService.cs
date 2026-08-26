using GridCore.Contracts.Services;
using GridCore.Modules.Billing.Data;
using GridCore.Modules.Billing.Features.RatePlans;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Platform.Monetary;
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
/// <remarks>
/// <b>WP-2.19 made <see cref="Amount"/> nullable, and that is the rate basis arriving.</b> A flat
/// fee is priced the moment it is read — the schedule says $60 and that is the figure. A rate fee
/// has no figure until something is charged on it, so it comes back unpriced and
/// <see cref="PriceOn"/> is what turns it into one. <c>AccountCharge.Raise</c> refuses an unpriced
/// assessment, which is what stops a screen ever putting "Late payment charge —" on a bill with
/// nothing after the dash.
/// </remarks>
/// <param name="Code">Which published fee.</param>
/// <param name="Name">What the line says when it reaches a bill.</param>
/// <param name="Description">What it covers and where the figure came from.</param>
/// <param name="ServiceType">The service it is published against.</param>
/// <param name="Basis">Whether the row publishes an amount or a rate.</param>
/// <param name="Amount">
/// What it costs on the day asked about, or <see langword="null"/> on a rate fee that has not been
/// priced on a basis yet.
/// </param>
/// <param name="Rate">The published rate, as a fraction. <see langword="null"/> on a flat fee.</param>
/// <param name="BasisAmount">
/// What the rate was taken on — the past-due balance, for a late charge. <see langword="null"/>
/// until <see cref="PriceOn"/> has been called, and on every flat fee.
/// </param>
/// <param name="Currency">ISO 4217 code the amount is expressed in.</param>
/// <param name="EffectiveFrom">The day that figure took effect — why this one and not another.</param>
/// <param name="FeeScheduleId">Which schedule row answered.</param>
public sealed record FeeAssessment(
    FeeCode Code,
    string Name,
    string Description,
    ServiceType ServiceType,
    FeeBasis Basis,
    decimal? Amount,
    decimal? Rate,
    decimal? BasisAmount,
    string Currency,
    DateOnly EffectiveFrom,
    Guid FeeScheduleId)
{
    /// <summary>Whether this assessment carries a figure something can actually be charged at.</summary>
    public bool IsPriced => Amount is not null;

    /// <summary>Reads an assessment off a schedule row.</summary>
    /// <remarks>
    /// A flat row comes back priced and a rate row comes back unpriced — the schedule is being read,
    /// not applied, and reading is all a catalogue screen ever does.
    /// </remarks>
    public static FeeAssessment Of(FeeScheduleEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new FeeAssessment(
            entry.Code,
            entry.Name,
            entry.Description,
            entry.ServiceType,
            entry.Basis,
            entry.Amount,
            entry.Rate,
            BasisAmount: null,
            entry.Currency,
            entry.EffectiveFrom,
            entry.Id);
    }

    /// <summary>
    /// Prices a rate fee on <paramref name="basisAmount"/>, carrying the rate and the basis forward
    /// so the arithmetic can be re-read without being re-run.
    /// </summary>
    /// <remarks>
    /// <b>The rate is this assessment's own, never the catalogue's as it stands now.</b> The row was
    /// chosen for the day being charged and its rate travelled with it; going back to the schedule
    /// here would price last month's late charge at this month's rate, which is the whole failure
    /// effective dating exists to prevent.
    /// </remarks>
    /// <param name="basisAmount">What to take the rate on.</param>
    /// <exception cref="BillingValidationException">This is a flat fee, or the basis is not a positive whole number of cents.</exception>
    public FeeAssessment PriceOn(decimal basisAmount)
    {
        if (Basis is not FeeBasis.Rate || Rate is not { } rate)
        {
            throw new BillingValidationException(
                $"{Code} is published as a flat fee, so there is nothing to charge on a basis of {basisAmount:0.00}.");
        }

        if (basisAmount <= Money.Zero)
        {
            throw new BillingValidationException(
                $"{Code} is charged on a balance, and {basisAmount:0.00} is not one.");
        }

        // Refused rather than rounded: the basis is a balance the register computed and already
        // stores to the cent, so one finer than that is a caller having done arithmetic of its own.
        if (!Money.IsRounded(basisAmount))
        {
            throw new BillingValidationException(
                $"A basis for {Code} must be a whole number of cents; '{basisAmount}' is not.");
        }

        return this with { Amount = Money.Round(rate * basisAmount), BasisAmount = basisAmount };
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

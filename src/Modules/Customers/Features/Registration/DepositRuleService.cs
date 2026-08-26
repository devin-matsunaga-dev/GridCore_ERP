using GridCore.Contracts.Services;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>
/// What a customer of a given class is being asked for on a given service, and the rule that says
/// so.
/// </summary>
/// <remarks>
/// <b>The working is on the record, not just the answer.</b> Since WP-2.17 a deposit can be the
/// published floor or a multiple of measured usage, and a rep has to be able to say which — so the
/// minimum, the average that priced it, the months taken and the rate applied all travel beside the
/// figure. A screen holding only <see cref="Amount"/> can render it; one holding all of this can
/// defend it.
/// </remarks>
/// <param name="CustomerClass">The class assessed.</param>
/// <param name="ServiceType">The service assessed.</param>
/// <param name="Amount">What the schedule asks — the greater of the minimum and the usage basis.</param>
/// <param name="MinimumAmount">The published floor, whether or not it is what answered.</param>
/// <param name="IsUsageBased">Whether measured usage decided the figure rather than the floor.</param>
/// <param name="AverageMonthlyUsage">The average that priced it, where usage was considered at all.</param>
/// <param name="UsageMonths">How many months of usage the rule takes, where it takes any.</param>
/// <param name="UsageRate">What one unit is priced at for deposit purposes, where usage applies.</param>
/// <param name="Currency">ISO 4217 code the amounts are expressed in — what a collection is posted in.</param>
/// <param name="Description">Why, in the clerk's words.</param>
/// <param name="RuleId">Which rule row answered — stamped on the audit entry so a figure can be traced.</param>
public sealed record DepositAssessment(
    CustomerClass CustomerClass,
    ServiceType ServiceType,
    decimal Amount,
    decimal MinimumAmount,
    bool IsUsageBased,
    decimal? AverageMonthlyUsage,
    int? UsageMonths,
    decimal? UsageRate,
    string Currency,
    string Description,
    Guid RuleId)
{
    /// <summary>
    /// Reads an assessment off a rule, with <paramref name="averageMonthlyUsage"/> as the measured
    /// input — <see langword="null"/> where nothing has been measured, which falls back to the
    /// minimum rather than assessing zero.
    /// </summary>
    public static DepositAssessment Of(DepositRule rule, decimal? averageMonthlyUsage = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var basis = rule.Assess(averageMonthlyUsage);

        return new DepositAssessment(
            rule.CustomerClass,
            rule.ServiceType,
            basis.Amount,
            rule.MinimumAmount,
            basis.IsUsageBased,
            basis.AverageMonthlyUsage,

            // The rule's months and rate, not the basis's: a screen showing "we would take two
            // months of usage, and the floor still won" is telling the truth about a rule that was
            // applied. The basis says which half answered; these say what the rule is.
            rule.UsageMonths,
            rule.UsageRate,
            rule.Currency,
            rule.Description,
            rule.Id);
    }
}

/// <summary>The deposit schedule as the application reads it.</summary>
public interface IDepositRuleService
{
    /// <summary>
    /// What <paramref name="customerClass"/> is asked for on <paramref name="serviceType"/>, off the
    /// published floor alone.
    /// </summary>
    /// <remarks>
    /// <b>No usage, deliberately.</b> This is the intake answer — a customer being registered has no
    /// premise history to average, and the wizard needs a figure before an account exists to measure
    /// against. Re-assessing an established customer against what they actually use is
    /// <c>IDepositReassessmentService</c>, which is a different question asked at a different time.
    /// </remarks>
    /// <exception cref="RegistryValidationException">The schedule declares no rule for that pair.</exception>
    Task<DepositAssessment> AssessAsync(
        CustomerClass customerClass,
        ServiceType serviceType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The rule for a pair, or <see langword="null"/> where the schedule has none — for a caller that
    /// is assessing several accounts and wants to say which one has no rule rather than throwing on
    /// the first.
    /// </summary>
    Task<DepositRule?> FindAsync(
        CustomerClass customerClass,
        ServiceType serviceType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The whole schedule, in class then service order — what an intake screen shows before a class
    /// and a service are chosen.
    /// </summary>
    Task<IReadOnlyList<DepositAssessment>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The deposit schedule read from <c>customers.deposit_rules</c>.
/// </summary>
/// <remarks>
/// Reads the table rather than <see cref="DepositRules.All"/> deliberately. The static list is how
/// the rows are <i>seeded</i>; the amount in force is whatever the database holds, so changing a
/// figure is a migration and not a redeploy of the domain. Read-only: a rule is corrected by
/// migration, exactly as a chart-of-accounts row is.
/// </remarks>
public sealed class DepositRuleService(CustomersDbContext database) : IDepositRuleService
{
    /// <inheritdoc />
    public async Task<DepositAssessment> AssessAsync(
        CustomerClass customerClass,
        ServiceType serviceType,
        CancellationToken cancellationToken = default)
    {
        var rule = await FindAsync(customerClass, serviceType, cancellationToken).ConfigureAwait(false);

        // Not a 404: the caller asked about a pair GridCore declares, and the schedule failing to
        // cover it is this deployment's problem rather than the request's. Named so whoever reads
        // the log knows the fix is a migration.
        return rule is null
            ? throw new RegistryValidationException(MissingRule(customerClass, serviceType))
            : DepositAssessment.Of(rule);
    }

    /// <inheritdoc />
    public Task<DepositRule?> FindAsync(
        CustomerClass customerClass,
        ServiceType serviceType,
        CancellationToken cancellationToken = default) =>
        database.DepositRules
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.CustomerClass == customerClass && candidate.ServiceType == serviceType,
                cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DepositAssessment>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rules = await database.DepositRules
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered in memory, by the enums rather than by their stored names: the schedule reads
        // residential-then-commercial and electricity-first the way the two lists do, not
        // alphabetically.
        return
        [
            .. rules
                .OrderBy(rule => rule.CustomerClass)
                .ThenBy(rule => rule.ServiceType)
                .Select(rule => DepositAssessment.Of(rule)),
        ];
    }

    /// <summary>What a missing rule reads as. One sentence, in one place, because two callers throw it.</summary>
    internal static string MissingRule(CustomerClass customerClass, ServiceType serviceType) =>
        $"No deposit rule is declared for {customerClass} {serviceType}. A deposit schedule is reference data: "
        + "add the rule in a migration.";
}

using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Shared;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Registration;

/// <summary>
/// What a customer of a given class is being asked for, and the rule that says so.
/// </summary>
/// <param name="CustomerClass">The class assessed.</param>
/// <param name="Amount">What the schedule asks for.</param>
/// <param name="Description">Why, in the clerk's words.</param>
/// <param name="RuleId">Which rule row answered — stamped on the audit entry so a figure can be traced.</param>
public sealed record DepositAssessment(CustomerClass CustomerClass, decimal Amount, string Description, Guid RuleId)
{
    /// <summary>Reads an assessment off a rule.</summary>
    public static DepositAssessment Of(DepositRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new DepositAssessment(rule.CustomerClass, rule.Amount, rule.Description, rule.Id);
    }
}

/// <summary>The deposit schedule as the application reads it.</summary>
public interface IDepositRuleService
{
    /// <summary>What <paramref name="customerClass"/> is asked for.</summary>
    /// <exception cref="RegistryValidationException">The schedule declares no rule for that class.</exception>
    Task<DepositAssessment> AssessAsync(CustomerClass customerClass, CancellationToken cancellationToken = default);

    /// <summary>The whole schedule, in class order — what an intake screen shows before a class is chosen.</summary>
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
    public async Task<DepositAssessment> AssessAsync(CustomerClass customerClass, CancellationToken cancellationToken = default)
    {
        var rule = await database.DepositRules
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.CustomerClass == customerClass, cancellationToken)
            .ConfigureAwait(false);

        // Not a 404: the caller asked about a class GridCore declares, and the schedule failing to
        // cover it is this deployment's problem rather than the request's. Named so whoever reads
        // the log knows the fix is a migration.
        return rule is null
            ? throw new RegistryValidationException(
                $"No deposit rule is declared for {customerClass}. A deposit schedule is reference data: add the rule in a migration.")
            : DepositAssessment.Of(rule);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DepositAssessment>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rules = await database.DepositRules
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ordered in memory, by the enum rather than by its stored name: the schedule reads
        // residential-then-commercial the way the class list does, not alphabetically.
        return [.. rules.OrderBy(rule => rule.CustomerClass).Select(DepositAssessment.Of)];
    }
}

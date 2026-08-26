using GridCore.Contracts.Services;

namespace GridCore.Contracts.Directories;

/// <summary>
/// What a premise has been consuming, averaged to a month — the measured input a deposit assessment
/// needs and cannot work out for itself (WP-2.17).
/// </summary>
/// <remarks>
/// <para>
/// <b>An average and the working behind it, not a bare number.</b> A deposit assessed on usage is a
/// figure a rep has to read out and defend at the counter, so this carries how many measured
/// periods it was drawn from and what span they cover. "Two months of average usage" is only a
/// defensible answer if somebody can see there were readings to average.
/// </para>
/// <para>
/// <b>Averaged over days and scaled to a month, not divided by a count of readings.</b> A reading
/// cycle is nominally monthly and never exactly a month, and a premise whose cycle was missed has
/// fewer readings than it has months. Dividing by the readings would quietly inflate the average of
/// the customer who was hardest to read — who is, reliably, the customer most likely to be assessed.
/// </para>
/// <para>
/// <b>No unit of measure, deliberately.</b> Metering records dial readings and knows nothing about
/// what a dial counts — the unit is a property of the tariff (<c>RatePlan.UnitOfMeasure</c>), not of
/// the register. Inventing one here would mean Metering asserting that its meters read kWh, which
/// is true of every meter the demonstration utility owns and is not a fact this module holds. A
/// caller pricing usage supplies its own basis and names the unit in the reference row that carries
/// it.
/// </para>
/// <para>
/// A DTO, never an entity — the rule every record in this folder follows.
/// </para>
/// </remarks>
/// <param name="ServiceLocationId">The premise asked about.</param>
/// <param name="AverageMonthlyUsage">
/// Units consumed in an average month, or <see langword="null"/> when there was nothing to average.
/// <b>A null is a real answer and not a zero:</b> a premise with no reading history has not been
/// measured consuming nothing, and an assessment that treated the two alike would ask a brand-new
/// connection for a deposit of nothing at all.
/// </param>
/// <param name="PeriodsConsidered">How many measured periods went into it. Zero where there is no history.</param>
/// <param name="DaysCovered">How many days those periods span in total — the divisor, stated.</param>
/// <param name="FirstPeriodStart">The start of the earliest period considered, if there was one.</param>
/// <param name="LastPeriodEnd">The end of the latest period considered, if there was one.</param>
public sealed record PremiseUsage(
    Guid ServiceLocationId,
    decimal? AverageMonthlyUsage,
    int PeriodsConsidered,
    int DaysCovered,
    DateTimeOffset? FirstPeriodStart,
    DateTimeOffset? LastPeriodEnd)
{
    /// <summary>Whether anything was actually measured — what separates "no history" from "used nothing".</summary>
    public bool HasHistory => AverageMonthlyUsage is not null;

    /// <summary>The answer for a premise nothing has ever been read at.</summary>
    public static PremiseUsage None(Guid serviceLocationId) =>
        new(serviceLocationId, null, 0, 0, null, null);
}

/// <summary>
/// Read access to what premises have consumed, for modules that are not Metering — the sixth
/// cross-module read seam in GridCore (WP-2.17).
/// </summary>
/// <remarks>
/// <para>
/// Shaped exactly like <see cref="IMeterReadingDirectory"/>: the interface lives in
/// <c>Contracts</c>, Metering registers the implementation, and the consumer takes the dependency
/// without ever learning that a <c>metering</c> schema exists.
/// </para>
/// <para>
/// <b>Its own seam rather than a fourth method on the reading directory, and that is the point.</b>
/// <see cref="IMeterReadingDirectory"/> hands over readings — the individual measurements, with
/// their exception codes and their previous dials — because its callers bill from them one at a
/// time. This one hands over a <i>derived statistic</i> and never a reading. Customers assesses a
/// deposit against average usage (WP-2.17) and has no business holding a decade of dial readings to
/// do it: a caller given the readings would be a caller that could compute the average its own way,
/// and two modules with two answers to "what does this premise use" is precisely the thing a seam
/// exists to prevent.
/// </para>
/// <para>
/// <b>Read-only, for the reason every directory here is.</b> Recording a reading and running a
/// cycle stay behind <c>IMeterReadingService</c> inside Metering.
/// </para>
/// </remarks>
public interface IUsageDirectory
{
    /// <summary>
    /// What a premise consumed in an average month, over its most recent measured periods.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Periods with no consumption figure — a missed read, a reading still on the exception
    /// worklist, the first reading after an installation — are skipped rather than counted as zero,
    /// which is what stops a gap in the register reading as a month of using nothing.
    /// </para>
    /// <para>
    /// <b>As of now, with no as-of date, deliberately.</b> This answers "what is this premise using",
    /// which is the input to a quote of what a customer is asked for <i>today</i> — there is no
    /// business question of the form "what would we have assessed last March", and a parameter with
    /// one possible value is a parameter that lies about what the seam can do. What makes an answer
    /// reproducible is the record of it: <c>DepositRequirement.AssessedAt</c> stamps the instant, and
    /// the audit trail holds what was actually asked for.
    /// </para>
    /// </remarks>
    /// <param name="serviceLocationId">The premise. A premise, not an account: the meter is at the premise.</param>
    /// <param name="serviceType">
    /// Which supply is being asked about. A premise may take several and they are measured by
    /// different devices, so an average that did not name one would be Metering handing over a kWh
    /// figure to somebody who asked about water. A service this deployment registers no meters for
    /// answers with no history, which is the right answer and not an error — see
    /// <see cref="PremiseUsage.HasHistory"/>.
    /// </param>
    /// <param name="periods">
    /// Most measured periods to average over, newest first. The caller states it because the answer
    /// is only meaningful beside the rule that asked — a deposit worked out over two months and one
    /// worked out over twelve are different assessments of the same customer.
    /// </param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    Task<PremiseUsage> AverageMonthlyAtLocationAsync(
        Guid serviceLocationId,
        ServiceType serviceType,
        int periods,
        CancellationToken cancellationToken = default);
}

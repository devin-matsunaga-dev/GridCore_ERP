namespace GridCore.Modules.Billing.Features.RatePlans;

/// <summary>
/// Which version of a tariff applies on a given day. The whole of GridCore's effective-dating rule,
/// in one pure function.
/// </summary>
/// <remarks>
/// <para>
/// A tariff has a start date and no end date: a version runs until the next version of the same code
/// starts. That is one fact rather than two that can disagree — an <c>EffectiveTo</c> beside an
/// <c>EffectiveFrom</c> is a gap or an overlap waiting to be introduced by the first repricing
/// somebody enters at half past four on a Friday.
/// </para>
/// <para>
/// <b>The date that matters is the end of the period being billed, not today.</b> A bill raised in
/// September for August's consumption is billed on August's rates, whatever the tariff says by the
/// time the run happens — otherwise a late billing run silently reprices everything it touches, and
/// a re-run after clearing the exception worklist would disagree with the bills it ran beside.
/// </para>
/// <para>
/// Pure and static, taking whatever collection of versions the caller has — the shipped set for a
/// test, or the rows loaded from <c>billing.rate_plans</c> for a run. That is what lets every case
/// below be proven with no database (CONVENTIONS.md rule C).
/// </para>
/// </remarks>
public static class RatePlanSelector
{
    /// <summary>
    /// The version of a tariff in force on <paramref name="on"/> — the latest one whose
    /// <see cref="RatePlan.EffectiveFrom"/> is on or before that day.
    /// </summary>
    /// <param name="versions">
    /// Versions of <b>one</b> tariff, in any order. Versions of other codes are ignored rather than
    /// trusted: a caller holding every plan in the database must not accidentally be handed a
    /// commercial tariff because it happened to be published later.
    /// </param>
    /// <param name="on">The day being billed — the end of the billed period, not today.</param>
    /// <returns>
    /// The version in force, or <see langword="null"/> when the tariff had not been published yet.
    /// A null is a real answer: a premise metered before its tariff existed cannot be billed, and
    /// saying so beats billing it on rates that were not published at the time.
    /// </returns>
    public static RatePlan? InForceOn(IEnumerable<RatePlan> versions, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(versions);

        RatePlan? inForce = null;

        foreach (var version in versions)
        {
            if (version.EffectiveFrom > on)
            {
                continue;
            }

            // Strictly later wins, so a set that somehow held two versions with the same effective
            // date resolves to the first of them rather than to whichever the enumeration happened
            // to yield last. The database refuses that pair anyway
            // (ux_rate_plans_code_effective) — this is what the code does if it ever arrives.
            if (inForce is null || version.EffectiveFrom > inForce.EffectiveFrom)
            {
                inForce = version;
            }
        }

        return inForce;
    }

    /// <summary>
    /// The version of <paramref name="code"/> in force on <paramref name="on"/>, out of a set that
    /// may hold several tariffs.
    /// </summary>
    public static RatePlan? InForceOn(IEnumerable<RatePlan> versions, string code, DateOnly on)
    {
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(code);

        return InForceOn(versions.Where(plan => string.Equals(plan.Code, code, StringComparison.Ordinal)), on);
    }
}

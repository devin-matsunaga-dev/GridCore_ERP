using GridCore.Contracts.Directories;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>The statutory authority a deposit offset is made under, quoted on the ledger entry itself.</summary>
/// <remarks>
/// <b>A constant so that every offset says the same thing, and says it in the record rather than in
/// somebody's memory.</b> WORK_PACKAGES.md asks for exactly this: "a legally obliged movement should
/// defend itself from the trail without anyone remembering why it happened". The reason text on the
/// <c>DepositEntry</c> is where a person reads it, and the reason text is what this is.
/// </remarks>
public static class StatutoryBasis
{
    /// <summary>The law the deposit offset is obliged by.</summary>
    public const string PublicLaw1617 = "CNMI Public Law 16-17";

    /// <summary>What an offset entry says about itself, naming the bill it settled.</summary>
    /// <param name="billNumber">The bill the deposit was applied to.</param>
    public static string OffsetReason(string billNumber) =>
        $"Security deposit applied to past-due bill {billNumber} under {PublicLaw1617}, "
        + "which obliges the utility to set a deposit against qualifying past-due amounts before "
        + "service is disconnected for non-payment.";
}

/// <summary>One of the tests an account has to pass before its supply may be cut off.</summary>
/// <param name="Name">What the test is, in the words a rep would use.</param>
/// <param name="IsSatisfied">Whether this account passes it.</param>
/// <param name="Detail">The figures or dates behind the answer, so the screen never has to restate them.</param>
public sealed record EligibilityTest(string Name, bool IsSatisfied, string Detail);

/// <summary>
/// Whether one service account may be disconnected for non-payment, and what the answer turned on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Computed, never typed.</b> WORK_PACKAGES.md's phrase, and the reason this is a record with
/// four tests on it rather than a boolean column somebody sets: a disconnection that can be
/// justified after the event is a disconnection that can be justified wrongly.
/// </para>
/// <para>
/// <b>The deposit offset happens BEFORE the arrears test, which is what makes it statutory rather
/// than clerical.</b> <see cref="ArrearsAfterOffset"/> is the figure every other test reads, so an
/// account whose deposit clears what it owes is not eligible — not "eligible but we chose not to".
/// </para>
/// </remarks>
/// <param name="ServiceAccountId">The account judged.</param>
/// <param name="AsOf">The day judged against.</param>
/// <param name="Currency">ISO 4217 code every figure is expressed in.</param>
/// <param name="ArrearsBeforeOffset">What was past due before the deposit was set against it.</param>
/// <param name="DepositHeldBeforeOffset">What the utility was holding.</param>
/// <param name="OffsetAmount">How much of it qualifies against the arrears — the lesser of the two.</param>
/// <param name="ArrearsAfterOffset">What remains past due once the deposit has been applied.</param>
/// <param name="DepositHeldAfterOffset">What is left on deposit once it has.</param>
/// <param name="Threshold">The published arrears the disconnection step asks for.</param>
/// <param name="DisconnectionNoticeServedOn">The day the disconnection notice went out, or <see langword="null"/>.</param>
/// <param name="WaitingPeriodDays">The published waiting period that notice started.</param>
/// <param name="EligibleFrom">
/// The first day disconnection could be taken on the notice served — <see langword="null"/> where
/// none has been.
/// </param>
/// <param name="Arrangement">The payment arrangement protecting the account, where there is one.</param>
/// <param name="Tests">Every test, in the order a rep reads them.</param>
/// <param name="IsOffsetApplied">
/// Whether the deposit movement described here has actually been made. <see langword="false"/> on
/// the read that a screen opens with — the figures are what <i>would</i> happen — and
/// <see langword="true"/> on the evaluation that made them happen.
/// </param>
public sealed record DisconnectionEligibility(
    Guid ServiceAccountId,
    DateOnly AsOf,
    string Currency,
    decimal ArrearsBeforeOffset,
    decimal DepositHeldBeforeOffset,
    decimal OffsetAmount,
    decimal ArrearsAfterOffset,
    decimal DepositHeldAfterOffset,
    decimal Threshold,
    DateOnly? DisconnectionNoticeServedOn,
    int WaitingPeriodDays,
    DateOnly? EligibleFrom,
    PaymentArrangementStanding? Arrangement,
    IReadOnlyList<EligibilityTest> Tests,
    bool IsOffsetApplied)
{
    /// <summary>Whether the supply may be cut off. True only when every test passes.</summary>
    public bool IsEligible => Tests.All(test => test.IsSatisfied);

    /// <summary>What is standing in the way, in the words the tests use. Empty when nothing is.</summary>
    public IReadOnlyList<string> Blockers => [.. Tests.Where(test => !test.IsSatisfied).Select(test => test.Name)];

    /// <summary>Whether the deposit clears the whole of the arrears — the case the statute exists for.</summary>
    public bool DepositClearsArrears => ArrearsBeforeOffset > Money.Zero && ArrearsAfterOffset <= Money.Zero;
}

/// <summary>
/// The four tests, in one pure function (WP-2.19).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and that is why every case in WORK_PACKAGES.md's verify list is a fast test.</b> A $300
/// deposit against $200 arrears, a $100 deposit against the same $200, a notice never served, a
/// notice served yesterday with ten days to run — none of them needs a database, a bill or a
/// customer, because the decision is a function of five figures and two dates.
/// </para>
/// <para>
/// <b>The offset is computed here rather than passed in.</b> It is the lesser of what is held and
/// what is past due, and computing it beside the test it feeds is what stops a caller applying one
/// figure to the ledger and judging on another.
/// </para>
/// </remarks>
public static class DisconnectionRules
{
    /// <summary>The test that says the account owes enough.</summary>
    public const string ArrearsTest = "Arrears at or over the published threshold";

    /// <summary>The test that says the customer was warned.</summary>
    public const string NoticeTest = "Disconnection notice served";

    /// <summary>The test that says the warning has had time to work.</summary>
    public const string WaitingPeriodTest = "Statutory waiting period elapsed";

    /// <summary>The test that says the customer is not already dealing with it.</summary>
    public const string ArrangementTest = "No payment arrangement in force";

    /// <summary>
    /// How much of <paramref name="depositHeld"/> qualifies against
    /// <paramref name="pastDueAmount"/>: the lesser of the two, floored at nothing.
    /// </summary>
    /// <remarks>
    /// <b>Never more than is owed and never more than is held.</b> The first would leave a credit on
    /// a bill with nowhere to sit — the argument <c>CustomerDepositService</c> already makes about an
    /// overpayment — and the second would have the utility hand over money it is not holding, which
    /// <c>Customer.RecordDepositMovement</c> refuses outright.
    /// </remarks>
    public static decimal QualifyingOffset(decimal depositHeld, decimal pastDueAmount) =>
        Math.Max(Money.Zero, Math.Min(depositHeld, pastDueAmount));

    /// <summary>
    /// Decides whether <paramref name="arrears"/>' account may be disconnected on
    /// <paramref name="arrears"/>'s day.
    /// </summary>
    /// <param name="arrears">What the account owes, aged, from Billing's own register.</param>
    /// <param name="depositHeld">What the utility holds against the customer.</param>
    /// <param name="step">The published disconnection step — its threshold and its waiting period.</param>
    /// <param name="disconnectionNotice">The most recent disconnection notice served, or <see langword="null"/>.</param>
    /// <param name="arrangement">Any payment arrangement standing against the account.</param>
    /// <param name="isOffsetApplied">Whether the deposit movement has actually been made.</param>
    public static DisconnectionEligibility Decide(
        AccountArrears arrears,
        decimal depositHeld,
        DunningStep step,
        DunningNotice? disconnectionNotice,
        PaymentArrangementStanding? arrangement,
        bool isOffsetApplied)
    {
        ArgumentNullException.ThrowIfNull(arrears);
        ArgumentNullException.ThrowIfNull(step);

        var offset = QualifyingOffset(depositHeld, arrears.PastDueAmount);
        var remaining = arrears.PastDueAmount - offset;

        // THE STATUTORY ORDER. Every test below reads the arrears AFTER the offset, which is what
        // "applies the held deposit to qualifying past-due amounts first" means in practice: a
        // customer whose deposit clears their debt fails the first test and is not eligible at all.
        var overThreshold = remaining > Money.Zero && remaining >= step.MinimumArrears;

        var servedOn = disconnectionNotice?.ServedOn;
        var elapsed = disconnectionNotice is not null && disconnectionNotice.HasElapsedBy(arrears.AsOf);

        // An arrangement the owner of arrangements says suppresses disconnection. Null — which is
        // every account until WP-2.20 — protects nobody.
        var protectedByArrangement = arrangement?.SuppressesDisconnection is true;

        return new DisconnectionEligibility(
            arrears.ServiceAccountId,
            arrears.AsOf,
            arrears.Currency,
            arrears.PastDueAmount,
            depositHeld,
            offset,
            remaining,
            depositHeld - offset,
            step.MinimumArrears,
            servedOn,
            step.WaitingPeriodDays,
            disconnectionNotice?.EffectiveFrom,
            arrangement,
            [
                new EligibilityTest(
                    ArrearsTest,
                    overThreshold,
                    offset > Money.Zero
                        ? $"{remaining:0.00} past due after {offset:0.00} of deposit was set against it; "
                          + $"the threshold is {step.MinimumArrears:0.00}."
                        : $"{remaining:0.00} past due against a threshold of {step.MinimumArrears:0.00}."),

                new EligibilityTest(
                    NoticeTest,
                    disconnectionNotice is not null,
                    servedOn is { } served
                        ? $"Served on {served:yyyy-MM-dd}."
                        : "No disconnection notice has been served on this account."),

                new EligibilityTest(
                    WaitingPeriodTest,
                    elapsed,
                    disconnectionNotice?.EffectiveFrom is { } from
                        ? elapsed
                            ? $"Elapsed on {from:yyyy-MM-dd}."
                            : $"Runs until {from:yyyy-MM-dd}; today is {arrears.AsOf:yyyy-MM-dd}."
                        : disconnectionNotice is null
                            ? $"Nothing has started the {step.WaitingPeriodDays}-day period."
                            : "The notice served started no waiting period."),

                new EligibilityTest(
                    ArrangementTest,
                    !protectedByArrangement,
                    arrangement is null
                        ? "No payment arrangement is recorded against this account."
                        : $"An arrangement is {arrangement.Status}."),
            ],
            isOffsetApplied);
    }
}

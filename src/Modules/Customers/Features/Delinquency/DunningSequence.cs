using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Shared;

namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>
/// The dunning sequence the utility ships with: reminder, delinquency notice, disconnection notice.
/// Reference data, not demo data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every figure here is a demo figure and says so in its own message.</b> They follow CUC's
/// published customer-service information and the delinquency regime of CNMI Public Law 16-17, whose
/// own publications disagree with each other and change without notice — so the application reads a
/// table, the row carries the provenance, and nobody can mistake ten days for a statutory certainty.
/// Changing one is a migration, not a redeploy.
/// </para>
/// <para>
/// <b>Three steps and no more.</b> The sequence is what the utility can prove it did before it cut
/// somebody off, and a fourth step nobody serves is a fourth thing a disconnection could be
/// challenged over. Adding one means adding a <see cref="DunningNoticeType"/> member, a row here and
/// a migration — and <see cref="RequireComplete"/> fails the first startup that forgets the row.
/// </para>
/// </remarks>
public static class DunningSequence
{
    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every row id.
    /// Fixed forever: changing it changes every id, which to the database is a different sequence.
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 27, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The currency the shipped thresholds are in. The demo utility bills in US dollars, as the
    /// deposit rules and the fee schedule do.
    /// </summary>
    public const string Currency = DepositRules.Currency;

    /// <summary>Every step, in the order they are served.</summary>
    public static IReadOnlyList<DunningStep> All { get; } =
    [
        DunningStep.Reference(
            DunningNoticeType.Reminder,
            sequence: 1,
            daysPastDue: 10,
            minimumArrears: 10.00m,
            waitingPeriodDays: 0,
            Currency,
            "Payment reminder",
            "Your account is past due. Please pay the outstanding balance to avoid further action. If you have "
            + "already paid, thank you — please disregard this notice. Demo wording and demo timing following CUC's "
            + "published customer-service information; not an authoritative notice."),

        DunningStep.Reference(
            DunningNoticeType.Delinquency,
            sequence: 2,
            daysPastDue: 30,
            minimumArrears: 25.00m,
            waitingPeriodDays: 0,
            Currency,
            "Notice of delinquency",
            "Your account is delinquent. Pay the outstanding balance in full, or contact Customer Service to "
            + "arrange payment, to avoid disconnection of service. Demo wording and demo timing following CUC's "
            + "published customer-service information; not an authoritative notice."),

        DunningStep.Reference(
            DunningNoticeType.Disconnection,
            sequence: 3,
            daysPastDue: 45,
            minimumArrears: 50.00m,

            // THE STATUTORY CLOCK. Serving this notice is what starts it, and no account is eligible
            // for disconnection until it has run out — see DisconnectionRules, where it is one of
            // the four tests.
            waitingPeriodDays: 10,
            Currency,
            "Notice of disconnection",
            "Service at this premise is scheduled for disconnection for non-payment. To avoid disconnection, pay "
            + "the outstanding balance, or contact Customer Service to arrange payment, within ten days of the date "
            + "of this notice. Any security deposit held will be applied to qualifying past-due amounts before "
            + "service is disconnected. Demo wording and demo timing following CUC's published customer-service "
            + "information and CNMI Public Law 16-17; not an authoritative notice."),
    ];

    /// <summary>The step for <paramref name="noticeType"/>, or <see langword="null"/> where none is published.</summary>
    public static DunningStep? For(DunningNoticeType noticeType) =>
        All.FirstOrDefault(step => step.NoticeType == noticeType);

    /// <summary>
    /// The furthest step an account <paramref name="daysPastDue"/> days behind with
    /// <paramref name="pastDueAmount"/> outstanding has reached, or <see langword="null"/> where it
    /// has reached none.
    /// </summary>
    /// <remarks>
    /// <b>The furthest, not the next.</b> An account forty-six days behind is past the reminder and
    /// past the delinquency notice, and a queue that offered the reminder would have the desk send a
    /// courtesy letter to somebody who should be receiving a disconnection notice. What is
    /// <i>outstanding</i> — which of those steps have actually been served — is the served register's
    /// answer, not this list's.
    /// </remarks>
    public static DunningStep? DueOn(IEnumerable<DunningStep> steps, int daysPastDue, decimal pastDueAmount)
    {
        ArgumentNullException.ThrowIfNull(steps);

        return steps
            .Where(step => step.IsDue(daysPastDue, pastDueAmount))
            .OrderByDescending(step => step.Sequence)
            .FirstOrDefault();
    }

    /// <summary>
    /// Fails if a declared notice has no step, if two steps claim one type, or if the sequence
    /// numbers are not 1..n.
    /// </summary>
    /// <remarks>
    /// Called where the model is built (<see cref="DunningStepConfiguration"/>), so a gap is found at
    /// startup rather than by the clerk working a delinquency queue — the shape
    /// <c>DepositRules.RequireComplete</c> and <c>FeeSchedules.RequireComplete</c> established.
    /// </remarks>
    /// <exception cref="RegistryValidationException">A declared notice has no step, or the sequence is malformed.</exception>
    public static void RequireComplete(IEnumerable<DunningStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        var rows = steps.ToList();

        foreach (var noticeType in Enum.GetValues<DunningNoticeType>())
        {
            var published = rows.Count(step => step.NoticeType == noticeType);

            if (published is 0)
            {
                throw new RegistryValidationException(
                    $"No dunning step is published for {noticeType}. The sequence is reference data: add the row in a "
                    + "migration, in the same one that declared the notice.");
            }

            if (published > 1)
            {
                throw new RegistryValidationException(
                    $"{published} dunning steps claim {noticeType}. A notice is served at one point in the sequence.");
            }
        }

        // 1..n with no gaps and no ties. A sequence with two step 2s has no answer to "what comes
        // next", and DueOn would pick whichever the enumeration yielded last.
        var expected = Enumerable.Range(1, rows.Count).ToList();
        var actual = rows.Select(step => step.Sequence).Order().ToList();

        if (!expected.SequenceEqual(actual))
        {
            throw new RegistryValidationException(
                $"The dunning sequence numbers {string.Join(", ", actual)} rather than {string.Join(", ", expected)}. "
                + "The steps are served in order, so the order has to be one to n.");
        }
    }
}

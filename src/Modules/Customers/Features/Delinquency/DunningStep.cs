using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Data;
using GridCore.Platform.Registry;
using GridCore.Platform.Monetary;

namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>
/// One step of the dunning sequence, as reference data: what the notice is called, how far past due
/// it becomes due, how much has to be owed for it to be worth serving, and what it says.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference data shipped by migration, beside <c>DepositRule</c> and the fee schedule</b>
/// (ARCHITECTURE.md invariant 8): a migrated database can work a delinquency queue whether or not
/// anybody ever seeds a demo world, and changing "30 days" to "35 days" is a migration rather than
/// a redeploy.
/// </para>
/// <para>
/// <b>Not effective-dated, and that is a considered difference from the fee schedule.</b> A fee is a
/// figure a customer holds a document about, so a charge raised in June has to keep reading £June's
/// figure for ever — which is what forces versions to coexist. A dunning step is a rule about what
/// the utility does <i>next</i>, and every notice it produces already stamps the arrears, the day
/// and the step it was served under. Nothing re-reads this table to explain an old notice, so there
/// is nothing for a second version to protect. The day a regulator asks "what was the sequence in
/// 2026", <see cref="DunningNotice"/> answers it from the notices themselves; the day the sequence
/// genuinely has to be versioned, <c>FeeScheduleEntry</c> is the pattern to copy.
/// </para>
/// <para>
/// <b>The threshold lives on the step rather than in a constant.</b> "Arrears over the threshold" is
/// one of the four tests <see cref="DisconnectionRules"/> applies, and the threshold that matters is
/// the disconnection step's — so it is published where the rest of that step is published, and the
/// reminder's own threshold stops the utility posting a letter about forty cents.
/// </para>
/// </remarks>
public sealed class DunningStep
{
    /// <summary>Longest stored form of a notice type's name.</summary>
    public const int TypeNameLength = 32;

    /// <summary>Longest name stored — what the notice is called on a screen and in a letterhead.</summary>
    public const int NameLength = 128;

    /// <summary>Longest body stored: what the notice actually says.</summary>
    public const int MessageLength = 1024;

    /// <summary>Longest ISO 4217 code stored. Three letters, with room to spare.</summary>
    public const int CurrencyLength = 8;

    private DunningStep()
    {
        // EF materialisation.
        Name = string.Empty;
        Message = string.Empty;
        Currency = string.Empty;
    }

    /// <summary>Identifier of this step. Derived from the notice type — see <see cref="ReferenceId"/>.</summary>
    public Guid Id { get; private init; }

    /// <summary>Which notice this step serves.</summary>
    public DunningNoticeType NoticeType { get; private init; }

    /// <summary>Where it sits in the sequence, from 1. Unique across the schedule, like the type.</summary>
    public int Sequence { get; private init; }

    /// <summary>
    /// How many days past due the oldest arrears has to be before this notice falls due.
    /// </summary>
    /// <remarks>
    /// Measured from the due date of the <i>oldest past-due bill</i>, which is what
    /// <c>AccountArrears.DaysPastDue</c> reports — a customer who paid last month's bill and not the
    /// one before is as delinquent as the older debt says, not as the newer one does.
    /// </remarks>
    public int DaysPastDue { get; private init; }

    /// <summary>
    /// The least that has to be past due for the notice to be worth serving.
    /// </summary>
    /// <remarks>
    /// A floor, never a ceiling. On the disconnection step it is also the threshold
    /// <see cref="DisconnectionRules"/> tests eligibility against, which is the same figure asked
    /// twice rather than two figures that could disagree.
    /// </remarks>
    public decimal MinimumArrears { get; private init; }

    /// <summary>
    /// How many days must pass after this notice is served before the act it warns of may be taken.
    /// Zero on every step but the disconnection notice.
    /// </summary>
    /// <remarks>
    /// <b>This is the statutory waiting period.</b> It is the difference between a utility that gave
    /// a customer a chance to pay and one that posted a letter on the way to the van, and it is why
    /// a served disconnection notice does not by itself make an account eligible.
    /// </remarks>
    public int WaitingPeriodDays { get; private init; }

    /// <summary>ISO 4217 code <see cref="MinimumArrears"/> is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>What the notice is called.</summary>
    public string Name { get; private init; }

    /// <summary>What it says — the body a rep reads out and a letter prints.</summary>
    public string Message { get; private init; }

    /// <summary>Whether serving this notice starts a clock the utility has to wait out.</summary>
    public bool HasWaitingPeriod => WaitingPeriodDays > 0;

    /// <summary>
    /// Whether an account <paramref name="daysPastDue"/> days behind with
    /// <paramref name="pastDueAmount"/> outstanding has reached this step.
    /// </summary>
    /// <remarks>
    /// Pure, so the whole sequence is provable in the fast tier — and both conditions, because a
    /// notice served on a customer forty days behind with $2 owing is a notice that costs more to
    /// post than it collects.
    /// </remarks>
    public bool IsDue(int daysPastDue, decimal pastDueAmount) =>
        daysPastDue >= DaysPastDue && pastDueAmount >= MinimumArrears && pastDueAmount > Money.Zero;

    /// <summary>
    /// Builds a reference step. The id is derived from the notice type, so the migration seeds the
    /// same rows every time it is generated.
    /// </summary>
    /// <param name="noticeType">Which notice.</param>
    /// <param name="sequence">Where it sits in the sequence, from 1.</param>
    /// <param name="daysPastDue">How far past due it falls due.</param>
    /// <param name="minimumArrears">The least that has to be owed for it to be served.</param>
    /// <param name="waitingPeriodDays">Days that must pass after it is served. Zero where it starts no clock.</param>
    /// <param name="currency">ISO 4217 code the minimum is expressed in.</param>
    /// <param name="name">What it is called.</param>
    /// <param name="message">What it says.</param>
    /// <exception cref="ArgumentException">A required value is missing, too long, or not a type GridCore declares.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A day count is negative, or the minimum is not a whole number of cents.</exception>
    public static DunningStep Reference(
        DunningNoticeType noticeType,
        int sequence,
        int daysPastDue,
        decimal minimumArrears,
        int waitingPeriodDays,
        string currency,
        string name,
        string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, NameLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(message.Length, MessageLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(sequence, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(daysPastDue);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumArrears);
        ArgumentOutOfRangeException.ThrowIfNegative(waitingPeriodDays);

        if (!Enum.IsDefined(noticeType))
        {
            throw new ArgumentException($"'{noticeType}' is not a dunning notice GridCore declares.", nameof(noticeType));
        }

        // Refused rather than rounded, the rule Money states: a threshold finer than a cent is a
        // typo in reference data, and rounding it would publish a figure no regulation says.
        if (!Money.IsRounded(minimumArrears))
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumArrears),
                minimumArrears,
                "A published arrears threshold is a whole number of cents.");
        }

        return new DunningStep
        {
            Id = ReferenceId.For(DunningSequence.AuthoredAt, noticeType.ToString()),
            NoticeType = noticeType,
            Sequence = sequence,
            DaysPastDue = daysPastDue,
            MinimumArrears = minimumArrears,
            WaitingPeriodDays = waitingPeriodDays,
            Currency = RegistryText.Clean(currency, CurrencyLength)
                ?? throw new ArgumentException("A dunning step names the currency its threshold is in.", nameof(currency)),
            Name = name,
            Message = message,
        };
    }
}

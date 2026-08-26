using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Delinquency;

/// <summary>
/// One dunning notice served on one service account, on one day (WP-2.19).
/// </summary>
/// <remarks>
/// <para>
/// <b>This row is the whole of what makes "the customer had an opportunity to pay" provable rather
/// than asserted.</b> WORK_PACKAGES.md says so in as many words, and it is why the record carries
/// what was owed and how late it was at the moment of service rather than pointing at an arrears
/// query that will answer differently tomorrow: the question a regulator asks is what the customer
/// was told, not what they owe now.
/// </para>
/// <para>
/// <b>Append-only.</b> A notice served in error is superseded by the record of what was done about
/// it, never edited — the rule the deposit ledger, the note log and the transition register all
/// follow. There is no <c>Cancel</c>, because a letter that went out went out.
/// </para>
/// <para>
/// <b>It stamps the step that governed it.</b> The sequence is not effective-dated
/// (<see cref="DunningStep"/> says why), and this is what pays for that: the day count, the
/// threshold and the waiting period that applied are all readable from the notice itself, so a
/// change to the published sequence cannot rewrite what an old notice meant.
/// </para>
/// </remarks>
public sealed class DunningNotice
{
    /// <summary>Longest stored form of a notice type's name.</summary>
    public const int TypeNameLength = DunningStep.TypeNameLength;

    /// <summary>Longest stored form of an account or customer number.</summary>
    public const int NumberLength = RegistryNumbers.MaxLength;

    /// <summary>Longest name stored against a customer.</summary>
    public const int NameLength = 200;

    /// <summary>Longest ISO 4217 code stored.</summary>
    public const int CurrencyLength = DunningStep.CurrencyLength;

    /// <summary>Longest note recorded beside a served notice.</summary>
    public const int NotesLength = 1024;

    private DunningNotice()
    {
        // EF materialisation.
        AccountNumber = string.Empty;
        CustomerName = string.Empty;
        Currency = string.Empty;
        ActorId = string.Empty;
    }

    /// <summary>Identifier of this notice. Guid v7, so the key index already orders it chronologically.</summary>
    public Guid Id { get; private init; }

    /// <summary>The account it was served over.</summary>
    public Guid ServiceAccountId { get; private init; }

    /// <summary>Its number, as printed. Stamped, so the notice reads on its own.</summary>
    public string AccountNumber { get; private init; }

    /// <summary>The customer served.</summary>
    public Guid CustomerId { get; private init; }

    /// <summary>Their name at the time it was served.</summary>
    public string CustomerName { get; private init; }

    /// <summary>Which notice this was.</summary>
    public DunningNoticeType NoticeType { get; private init; }

    /// <summary>
    /// The day it was served. <b>The date everything downstream is measured from</b> — the statutory
    /// waiting period runs from here.
    /// </summary>
    public DateOnly ServedOn { get; private init; }

    /// <summary>What was past due when it went out.</summary>
    public decimal ArrearsAmount { get; private init; }

    /// <summary>ISO 4217 code that figure is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>How late the oldest past-due bill was on the day it was served.</summary>
    public int DaysPastDue { get; private init; }

    /// <summary>The published step that governed it.</summary>
    public Guid DunningStepId { get; private init; }

    /// <summary>
    /// The waiting period that applied, in days, copied off the step. Zero on a notice that starts
    /// no clock.
    /// </summary>
    public int WaitingPeriodDays { get; private init; }

    /// <summary>What the desk wrote beside it, if anything.</summary>
    public string? Notes { get; private init; }

    /// <summary>Subject id of whoever served it.</summary>
    public string ActorId { get; private init; }

    /// <summary>Their display name at the time.</summary>
    public string? ActorName { get; private init; }

    /// <summary>When it was recorded — distinct from <see cref="ServedOn"/>, which is when it went out.</summary>
    public DateTimeOffset RecordedAt { get; private init; }

    /// <summary>
    /// The first day the act this notice warns of may be taken, or <see langword="null"/> where it
    /// warns of nothing.
    /// </summary>
    /// <remarks>
    /// Computed rather than stored: it is <see cref="ServedOn"/> plus
    /// <see cref="WaitingPeriodDays"/>, both of which are stored, and a third column could only ever
    /// disagree with them.
    /// </remarks>
    public DateOnly? EffectiveFrom => WaitingPeriodDays > 0 ? ServedOn.AddDays(WaitingPeriodDays) : null;

    /// <summary>Whether the waiting period this notice started has run out by <paramref name="asOf"/>.</summary>
    /// <remarks>
    /// A notice with no waiting period has nothing to elapse, and answers <see langword="true"/> —
    /// there is no clock left running on a reminder.
    /// </remarks>
    public bool HasElapsedBy(DateOnly asOf) => EffectiveFrom is not { } from || asOf >= from;

    /// <summary>Records that <paramref name="step"/>'s notice was served over an account.</summary>
    /// <param name="step">The published step being served.</param>
    /// <param name="serviceAccountId">The account served over.</param>
    /// <param name="accountNumber">Its number.</param>
    /// <param name="customerId">The customer served.</param>
    /// <param name="customerName">Their name at the time.</param>
    /// <param name="servedOn">The day it went out.</param>
    /// <param name="arrearsAmount">What was past due then.</param>
    /// <param name="currency">ISO 4217 code that figure is in.</param>
    /// <param name="daysPastDue">How late the oldest past-due bill was.</param>
    /// <param name="notes">What the desk wrote beside it.</param>
    /// <param name="actor">Who served it.</param>
    /// <param name="now">The clock, for the row's identity and timestamp.</param>
    /// <exception cref="RegistryValidationException">A required value is missing, or a figure is not one a notice can carry.</exception>
    public static DunningNotice Serve(
        DunningStep step,
        Guid serviceAccountId,
        string accountNumber,
        Guid customerId,
        string customerName,
        DateOnly servedOn,
        decimal arrearsAmount,
        string currency,
        int daysPastDue,
        string? notes,
        RegistryActor actor,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(actor);

        // Every guard before the first field is set — WP-1.4's ordering rule.
        if (serviceAccountId == Guid.Empty)
        {
            throw new RegistryValidationException("A dunning notice is served over a service account.");
        }

        // A notice about nothing is a notice nobody can defend. The step's own minimum is checked by
        // the service, which knows what the account owes; this is the floor below which the record
        // itself stops meaning anything.
        if (arrearsAmount <= Money.Zero)
        {
            throw new RegistryValidationException(
                $"A {step.Name.ToLowerInvariant()} says what is owed, and {arrearsAmount:0.00} is not an amount owed.");
        }

        if (!Money.IsRounded(arrearsAmount))
        {
            throw new RegistryValidationException(
                $"The arrears on a dunning notice is a whole number of cents; '{arrearsAmount}' is not.");
        }

        if (daysPastDue < 0)
        {
            throw new RegistryValidationException(
                $"A dunning notice records how late the account was, and {daysPastDue} days is not a lateness.");
        }

        return new DunningNotice
        {
            Id = Guid.CreateVersion7(now),
            ServiceAccountId = serviceAccountId,
            AccountNumber = RegistryText.Clean(accountNumber, NumberLength)
                ?? throw new RegistryValidationException("A dunning notice names the account it was served over."),
            CustomerId = customerId,
            CustomerName = RegistryText.Clean(customerName, NameLength)
                ?? throw new RegistryValidationException("A dunning notice names the customer it was served on."),
            NoticeType = step.NoticeType,
            ServedOn = servedOn,
            ArrearsAmount = arrearsAmount,
            Currency = RegistryText.Clean(currency, CurrencyLength)
                ?? throw new RegistryValidationException("A dunning notice names the currency its arrears is in."),
            DaysPastDue = daysPastDue,
            DunningStepId = step.Id,

            // Copied off the step rather than looked up later, so a change to the published sequence
            // cannot move a clock that has already started running.
            WaitingPeriodDays = step.WaitingPeriodDays,
            Notes = RegistryText.Clean(notes, NotesLength),
            ActorId = RegistryText.Clean(actor.Id, RegistryActor.MaxLength)
                ?? throw new RegistryValidationException("A dunning notice must name who served it."),
            ActorName = RegistryText.Clean(actor.Name, RegistryActor.MaxLength),
            RecordedAt = now,
        };
    }
}

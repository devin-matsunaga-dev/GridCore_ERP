using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Data;
using GridCore.Platform.Monetary;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.Features.Arrangements;

/// <summary>
/// How much a rep may arrange on their own authority, per customer class — reference data (WP-2.20).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reference data shipped by migration, beside <c>DepositRule</c>, the fee schedule and the
/// dunning sequence</b> (ARCHITECTURE.md invariant 8): a migrated database can work a counter
/// whether or not anybody seeds a demo world, and raising the residential ceiling is a migration
/// rather than a redeploy.
/// </para>
/// <para>
/// <b>Two ceilings rather than one, because they refuse different things.</b> A balance ceiling
/// stops a rep promising away a debt the utility should be escalating; an instalment ceiling stops
/// one spreading a small debt over three years, which is a write-off wearing a schedule's clothes.
/// Either on its own is trivially avoided by moving the other.
/// </para>
/// <para>
/// <b>Keyed on customer class, the call <c>DepositRule</c> made.</b> A business owing four thousand
/// dollars over six months is ordinary and a household owing the same is not, so one figure for both
/// would either tie the commercial desk's hands or hand the residential desk an authority nobody
/// meant to give it.
/// </para>
/// <para>
/// <b>Not effective-dated, deliberately — <see cref="Delinquency.DunningStep"/>'s reasoning
/// exactly.</b> A fee is a figure a customer holds a document about, so June's charge has to keep
/// reading June's price for ever; a limit is a rule about what the utility does <i>next</i>, and
/// every arrangement it governs already stamps the two ceilings that applied to it. Nothing
/// re-reads this table to explain an old arrangement, so there is nothing for a second version to
/// protect. The day it genuinely has to be versioned, <c>FeeScheduleEntry</c> is the pattern to copy.
/// </para>
/// </remarks>
public sealed class ArrangementLimit
{
    /// <summary>Longest stored form of a customer class's name.</summary>
    public const int ClassNameLength = 32;

    /// <summary>Longest ISO 4217 code stored. Three letters, with room to spare.</summary>
    public const int CurrencyLength = 8;

    /// <summary>Longest note stored — what the row says about where its figures came from.</summary>
    public const int NotesLength = 512;

    private ArrangementLimit()
    {
        // EF materialisation.
        Currency = string.Empty;
        Notes = string.Empty;
    }

    /// <summary>Identifier of this limit. Derived from the class — see <see cref="ReferenceId"/>.</summary>
    public Guid Id { get; private init; }

    /// <summary>The class of customer it governs.</summary>
    public CustomerClass CustomerClass { get; private init; }

    /// <summary>The most a rep may arrange on their own authority.</summary>
    public decimal MaximumBalance { get; private init; }

    /// <summary>ISO 4217 code <see cref="MaximumBalance"/> is expressed in.</summary>
    public string Currency { get; private init; }

    /// <summary>The most instalments a rep may spread it over on their own authority.</summary>
    public int MaximumInstalments { get; private init; }

    /// <summary>Where these figures came from, in the words a rep reading the refusal needs.</summary>
    public string Notes { get; private init; }

    /// <summary>
    /// Whether an arrangement of <paramref name="balance"/> over <paramref name="instalmentCount"/>
    /// instalments is beyond what a rep may do alone.
    /// </summary>
    /// <remarks>
    /// Pure, so every case in WORK_PACKAGES.md's verify list is a fast test — and <b>either ceiling
    /// on its own</b> is enough, because the two exist to refuse different things.
    /// </remarks>
    public bool RequiresApproval(decimal balance, int instalmentCount) =>
        balance > MaximumBalance || instalmentCount > MaximumInstalments;

    /// <summary>Builds a reference limit. The id is derived from the class, so the migration seeds the same rows every time.</summary>
    /// <param name="customerClass">The class it governs.</param>
    /// <param name="maximumBalance">The most a rep may arrange alone.</param>
    /// <param name="currency">ISO 4217 code that figure is in.</param>
    /// <param name="maximumInstalments">The most instalments a rep may spread it over alone.</param>
    /// <param name="notes">Where the figures came from.</param>
    /// <exception cref="ArgumentException">A required value is missing, too long, or not a class GridCore declares.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A figure is not one a limit can carry.</exception>
    public static ArrangementLimit Reference(
        CustomerClass customerClass,
        decimal maximumBalance,
        string currency,
        int maximumInstalments,
        string notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(notes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(notes.Length, NotesLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBalance);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumInstalments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumInstalments, PaymentArrangement.MaximumInstalments);

        if (!Enum.IsDefined(customerClass))
        {
            throw new ArgumentException($"'{customerClass}' is not a customer class GridCore declares.", nameof(customerClass));
        }

        // Refused rather than rounded, the rule Money states: a published ceiling finer than a cent
        // is a typo in reference data, and rounding it would publish a figure nobody authorised.
        if (!Money.IsRounded(maximumBalance))
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBalance),
                maximumBalance,
                "A published arrangement ceiling is a whole number of cents.");
        }

        return new ArrangementLimit
        {
            Id = ReferenceId.For(ArrangementLimits.AuthoredAt, customerClass.ToString()),
            CustomerClass = customerClass,
            MaximumBalance = maximumBalance,
            Currency = RegistryText.Clean(currency, CurrencyLength)
                ?? throw new ArgumentException("An arrangement limit names the currency its ceiling is in.", nameof(currency)),
            MaximumInstalments = maximumInstalments,
            Notes = notes,
        };
    }
}

/// <summary>
/// The arrangement limits the utility ships with, one per customer class. Reference data, not demo
/// data.
/// </summary>
/// <remarks>
/// <b>Every figure here is a demo figure and says so in its own row.</b> CUC publishes that Customer
/// Service will arrange payment rather than disconnect, and does not publish what a rep may sign
/// alone — so the application reads a table, the row carries the provenance, and nobody can mistake
/// $1,500 for a published authority. Changing one is a migration, not a redeploy.
/// </remarks>
public static class ArrangementLimits
{
    /// <summary>
    /// The instant this reference set was authored, and the timestamp component of every row id.
    /// Fixed forever: changing it changes every id, which to the database is a different set.
    /// </summary>
    public static readonly DateTimeOffset AuthoredAt = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The currency the shipped ceilings are in. The demo utility bills in US dollars, as the
    /// deposit rules, the fee schedule and the dunning sequence do.
    /// </summary>
    public const string Currency = DepositRules.Currency;

    /// <summary>Every published limit, in class order.</summary>
    public static IReadOnlyList<ArrangementLimit> All { get; } =
    [
        ArrangementLimit.Reference(
            CustomerClass.Residential,
            maximumBalance: 1_500.00m,
            Currency,
            maximumInstalments: 6,
            "Demo figures. CUC publishes that Customer Service will arrange payment rather than disconnect, and "
            + "does not publish what a representative may agree without a supervisor; these ceilings are GridCore's "
            + "own and are not an authoritative delegation."),

        ArrangementLimit.Reference(
            CustomerClass.Commercial,
            maximumBalance: 5_000.00m,
            Currency,
            maximumInstalments: 12,
            "Demo figures, set higher than the residential ceiling because a commercial arrears of several thousand "
            + "dollars over a year is ordinary. Not an authoritative delegation."),
    ];

    /// <summary>The limit governing <paramref name="customerClass"/>, or <see langword="null"/> where none is published.</summary>
    public static ArrangementLimit? For(CustomerClass customerClass) =>
        All.FirstOrDefault(limit => limit.CustomerClass == customerClass);

    /// <summary>Fails if a declared class has no limit, or two limits claim one class.</summary>
    /// <remarks>
    /// Called where the model is built (<see cref="ArrangementLimitConfiguration"/>), so a gap is
    /// found at startup rather than by the rep on the telephone — the shape
    /// <c>DepositRules.RequireComplete</c>, <c>FeeSchedules.RequireComplete</c> and
    /// <c>DunningSequence.RequireComplete</c> established.
    /// </remarks>
    /// <exception cref="RegistryValidationException">A declared class has no limit, or two claim one.</exception>
    public static void RequireComplete(IEnumerable<ArrangementLimit> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var rows = limits.ToList();

        foreach (var customerClass in Enum.GetValues<CustomerClass>())
        {
            var published = rows.Count(limit => limit.CustomerClass == customerClass);

            if (published is 0)
            {
                throw new RegistryValidationException(
                    $"No arrangement limit is published for {customerClass}, so no {customerClass} arrangement could "
                    + "be judged against a rep's authority. The limits are reference data: add the row in a migration, "
                    + "in the same one that declared the class.");
            }

            if (published > 1)
            {
                throw new RegistryValidationException(
                    $"{published} arrangement limits claim {customerClass}. A class has one ceiling, or the answer to "
                    + "\"may this rep sign it\" depends on which row was read.");
            }
        }
    }
}

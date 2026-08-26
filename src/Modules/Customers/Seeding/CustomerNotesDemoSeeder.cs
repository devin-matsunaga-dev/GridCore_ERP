using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Platform.Registry;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Seeding;

/// <summary>
/// Writes the note log the demo world's customers would actually have: calls taken, a counter
/// visit, a complaint, a billing dispute, a standing instruction pinned to the top of an account,
/// and one note corrected by a later one.
/// </summary>
/// <remarks>
/// <para>
/// Without this the notes tab and the timeline's fifth source are empty on a freshly seeded
/// database, which makes a feature that exists look like a feature that does not. It is the same
/// call WP-2.12 made about seeding deposit <i>entries</i> rather than balances: the demo world shows
/// the register, not just the shape of it.
/// </para>
/// <para>
/// A seeder of its own rather than more rows in an existing one: a seeder's <see cref="Name"/> is its
/// dedupe key, so extending one that has already run on a developer's database would seed nothing.
/// Running last also lets it query the customers and accounts the earlier seeders committed — inside
/// one transaction those rows are not yet visible to a query.
/// </para>
/// <para>
/// <b>No links.</b> A seeded note filed against a bill would have to name a bill id, and the demo
/// bills are produced by a billing run rather than by a seeder — so the id would either not exist or
/// belong to whatever happened to be raised first. The dispute below says which bill it is about in
/// its own words, which is what a rep would write anyway, and the link is exercised by the tests and
/// by anybody working the screen.
/// </para>
/// </remarks>
public sealed class CustomerNotesDemoSeeder(CustomersDbContext database, TimeProvider clock) : IDemoSeeder
{
    /// <summary>Who the seeded notes are attributed to — the same stand-in colleague the accounts carry.</summary>
    private static RegistryActor Attribution { get; } = RegistryActor.Of(ServiceAccountsDemoSeeder.Agent);

    /// <inheritdoc />
    /// <remarks>The dedupe key. Never renamed — a rename seeds a second set of notes.</remarks>
    public string Name => "customers.notes";

    /// <inheritdoc />
    /// <remarks>After <see cref="ServiceAccountsDemoSeeder"/> (300), whose accounts these notes hang off.</remarks>
    public int Order => 400;

    /// <inheritdoc />
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var customers = await database.Customers
            .ToDictionaryAsync(customer => customer.AccountNumber, customer => customer.Id, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        var accounts = await database.ServiceAccounts
            .ToDictionaryAsync(account => account.AccountNumber, account => account, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);

        // Ids are Guid v7 stamped from the instant they are created, and rows created in the same
        // instant have no defined order. Walking BACKWARDS from now is what gives the log the shape a
        // real one has — the oldest note is oldest — while keeping every id distinct.
        var now = clock.GetUtcNow();
        var age = Entries.Count;

        DateTimeOffset Next() => now.AddDays(-age--);

        // Every note built so far, by its place in Entries, so a correction can name the id of the
        // one it replaces. A list rather than a dictionary because the key IS the position.
        var written = new List<CustomerNote>(Entries.Count);

        foreach (var entry in Entries)
        {
            // A demo world that quietly skips half its notes because an account number was edited is
            // worse than one that refuses to start and says which row is missing.
            if (!customers.TryGetValue(entry.CustomerAccountNumber, out var customerId))
            {
                throw new InvalidOperationException(
                    $"Demo customer '{entry.CustomerAccountNumber}' was not seeded; {nameof(CustomersDemoSeeder)} and this seeder have drifted apart.");
            }

            Guid? serviceAccountId = null;

            if (entry.ServiceAccountNumber is { } accountNumber)
            {
                if (!accounts.TryGetValue(accountNumber, out var account))
                {
                    throw new InvalidOperationException(
                        $"Demo service account '{accountNumber}' was not seeded; {nameof(ServiceAccountsDemoSeeder)} and this seeder have drifted apart.");
                }

                if (account.CustomerId != customerId)
                {
                    throw new InvalidOperationException(
                        $"Demo service account '{accountNumber}' is not {entry.CustomerAccountNumber}'s; the demo pairings and this seeder have drifted apart.");
                }

                serviceAccountId = account.Id;
            }

            // Prefixed, so a seeded note can never be mistaken for one a real agent wrote — the
            // habit every demo attribution in GridCore keeps.
            var body = $"[demo] {entry.Body}";

            // Deliberately null on every seeded note. A follow-up date is refused in the past, so a
            // fixed date would make the demo world un-seedable the day after it was written, and a
            // relative one would drift every time the seeder ran.
            var note = entry.Corrects is { } corrected
                ? CustomerNote.Correct(written[corrected], entry.Kind, body, followUpOn: null, link: null, Attribution, Next())
                : CustomerNote.Log(customerId, serviceAccountId, entry.Kind, body, followUpOn: null, link: null, Attribution, Next());

            note.SetPinned(entry.IsPinned);

            written.Add(note);
            database.CustomerNotes.Add(note);
        }

        // No SaveChanges: the runner's unit of work saves this and the seed record in one
        // transaction, which is what makes a half-seeded demo world impossible.
    }

    /// <summary>
    /// What the demo world's reps wrote down, oldest first.
    /// </summary>
    /// <remarks>
    /// Every kind appears at least once, because a screen that renders seven pills should be seen
    /// rendering seven pills — and so do the three shapes that are not just another row: a note
    /// against the customer rather than an account, a pinned standing instruction, and a note
    /// corrected by a later one.
    /// </remarks>
    private static IReadOnlyList<DemoNote> Entries { get; } =
    [
        new("C-000001", "A-000001", CustomerNoteKind.InboundCall,
            "Rang to ask when the meter would be read. Told them the cycle reads on the 20th."),

        new("C-000002", null, CustomerNoteKind.Note,
            "Dog on the property — reader to sound the horn at the gate rather than walk in.", IsPinned: true),

        new("C-000003", "A-000003", CustomerNoteKind.CounterVisit,
            "Came in to ask about three-phase load for a second freezer. Referred to engineering."),

        new("C-000004", "A-000004", CustomerNoteKind.OutboundCall,
            "Called to confirm the standby generator witness test date. No answer; left a message."),

        new("C-000005", "A-000005", CustomerNoteKind.Complaint,
            "Unhappy about the disconnection notice arriving the same week as the bill. Explained the arrears timeline and the reconnection fee."),

        new("C-000005", "A-000005", CustomerNoteKind.BillingDispute,
            "Disputes the consumption on the most recent bill — says the property was empty for three weeks. Reading to be verified before any adjustment."),

        new("C-000006", "A-000006", CustomerNoteKind.FieldVisit,
            "Attended site to check the service drop after the storm. Drop intact, no work required."),

        // The package's central rule, on screen in the demo world: entry 3 said no answer, and this
        // is what actually happened. The original stays exactly as it was written, above this one in
        // the log, and the correction points at it — which is the whole difference between this
        // register and one somebody could edit.
        new("C-000004", "A-000004", CustomerNoteKind.OutboundCall,
            "Correction: the call was answered. Witness test confirmed for the following Tuesday morning.",
            Corrects: 3),
    ];

    /// <summary>One seeded note, before its customer and account have been resolved to ids.</summary>
    /// <param name="CustomerAccountNumber">The customer it is about, by the number they quote.</param>
    /// <param name="ServiceAccountNumber">The account it is about, or nothing when it is about the person.</param>
    /// <param name="Kind">A written note, or the contact that took place.</param>
    /// <param name="Body">What was said or written.</param>
    /// <param name="IsPinned">Whether it sits at the top of the customer's log.</param>
    /// <param name="Corrects">
    /// The place in this list of the note it corrects, or nothing when it is not a correction. An
    /// index rather than an id because the ids do not exist until the seeder runs, and it can only
    /// point backwards — a correction of a note not yet written would throw here, which is the right
    /// answer to a list edited into that shape.
    /// </param>
    private sealed record DemoNote(
        string CustomerAccountNumber,
        string? ServiceAccountNumber,
        CustomerNoteKind Kind,
        string Body,
        bool IsPinned = false,
        int? Corrects = null);
}

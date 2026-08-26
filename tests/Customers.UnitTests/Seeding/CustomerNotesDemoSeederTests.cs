using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Seeding;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Data;
using GridCore.Platform.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridCore.Modules.Customers.UnitTests.Seeding;

/// <summary>
/// The note log the demo world opens with. Development-only — the guard is the platform's. What
/// matters here is that the dataset is coherent: every customer and account named resolves, every
/// kind a screen renders is present, and the append-only shape is on display rather than only in the
/// tests.
/// </summary>
public class CustomerNotesDemoSeederTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// All three seeders in order, each in its own unit of work — exactly as the runner drives them,
    /// and the reason this one can query rows the others wrote.
    /// </summary>
    private static Task SeedAsync(CustomersTestHost host) =>
        host.InScopeAsync<object?>(async services =>
        {
            var database = services.GetRequiredService<CustomersDbContext>();
            var unitOfWork = services.GetRequiredService<IUnitOfWork>();

            await unitOfWork.ExecuteAsync(new CustomersDemoSeeder(database, new FakeClock(Now)).SeedAsync);
            await unitOfWork.ExecuteAsync(new ServiceAccountsDemoSeeder(database, new FakeClock(Now)).SeedAsync);
            await unitOfWork.ExecuteAsync(new CustomerNotesDemoSeeder(database, new FakeClock(Now)).SeedAsync);

            return null;
        });

    [Fact]
    public void The_seeder_name_is_the_dedupe_key_and_runs_after_the_accounts()
    {
        var seeder = new CustomerNotesDemoSeeder(null!, TimeProvider.System);

        // Renaming this seeds a second set of notes on the next start. It is not a label.
        Assert.Equal("customers.notes", seeder.Name);
        Assert.True(seeder.Order > new ServiceAccountsDemoSeeder(null!, TimeProvider.System).Order);
    }

    [Fact]
    public async Task Every_seeded_note_is_attributed_to_the_demo_colleague_and_marked_as_demo()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();
        var notes = await database.CustomerNotes.ToListAsync();

        Assert.NotEmpty(notes);
        Assert.All(notes, note => Assert.StartsWith(DemoActor.IdPrefix, note.ActorId, StringComparison.Ordinal));

        // A seeded note can never be mistaken for one a real agent wrote — the habit every demo
        // attribution in GridCore keeps.
        Assert.All(notes, note => Assert.StartsWith("[demo] ", note.Body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_kind_a_screen_renders_appears_in_the_demo_world()
    {
        // A screen that renders seven pills should be seen rendering seven pills.
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();
        var seeded = await database.CustomerNotes.Select(note => note.Kind).Distinct().ToListAsync();

        Assert.Equal(CustomerNoteKinds.All.Order(), seeded.Order());
    }

    [Fact]
    public async Task The_demo_world_shows_a_pinned_note_and_a_corrected_one()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        var pinned = await database.CustomerNotes.SingleAsync(note => note.IsPinned);
        var correction = await database.CustomerNotes.SingleAsync(note => note.CorrectsNoteId != null);

        // A note about the person rather than one of their supplies — the shape that would otherwise
        // never be seen on screen.
        Assert.Null(pinned.ServiceAccountId);

        // The corrected note is still there, saying what it originally said. That is the package's
        // central rule, visible in the demo world rather than only in a test.
        var original = await database.CustomerNotes.SingleAsync(note => note.Id == correction.CorrectsNoteId);

        Assert.Contains("No answer", original.Body, StringComparison.Ordinal);
        Assert.Equal(original.CustomerId, correction.CustomerId);
        Assert.Equal(original.ServiceAccountId, correction.ServiceAccountId);
    }

    [Fact]
    public async Task Every_seeded_note_belongs_to_a_seeded_customer_and_an_account_of_theirs()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();
        var notes = await database.CustomerNotes.ToListAsync();
        var accounts = await database.ServiceAccounts.ToDictionaryAsync(account => account.Id);
        var customers = await database.Customers.Select(customer => customer.Id).ToListAsync();

        Assert.All(notes, note => Assert.Contains(note.CustomerId, customers));

        // A note filed under somebody else's account would appear on their 360 — a disclosure the
        // service refuses, and the seeder bypasses the service, so the demo data is checked directly.
        Assert.All(
            notes.Where(note => note.ServiceAccountId is not null),
            note => Assert.Equal(note.CustomerId, accounts[note.ServiceAccountId!.Value].CustomerId));
    }

    [Fact]
    public async Task No_seeded_note_carries_a_follow_up_date()
    {
        // Deliberate. A follow-up is refused in the past, so a fixed date would make the demo world
        // un-seedable the day after it was written and a relative one would drift on every run.
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();

        Assert.All(await database.CustomerNotes.ToListAsync(), note => Assert.Null(note.FollowUpOn));
    }

    [Fact]
    public async Task The_log_reads_oldest_first_by_id_so_a_correction_never_precedes_what_it_corrects()
    {
        using var host = new CustomersTestHost(new FakeClock(Now));
        await SeedAsync(host);

        await using var database = host.NewCustomersContext();
        var notes = await database.CustomerNotes.OrderBy(note => note.Id).ToListAsync();

        // Ids are Guid v7 stamped from the seeder's walk backwards through the calendar, so the key
        // order IS the chronology. A correction that sorted above its original would render as a
        // correction of something that had not happened yet.
        var positions = notes.Select((note, index) => (note, index)).ToDictionary(row => row.note.Id, row => row.index);

        Assert.All(
            notes.Where(note => note.CorrectsNoteId is not null),
            note => Assert.True(positions[note.CorrectsNoteId!.Value] < positions[note.Id]));
    }
}

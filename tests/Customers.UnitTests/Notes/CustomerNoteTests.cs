using GridCore.Modules.Customers.Features.Notes;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Registry;

namespace GridCore.Modules.Customers.UnitTests.Notes;

/// <summary>
/// The note aggregate's own rules, with no database in sight — what a note may say, what a
/// correction does to the note it corrects (nothing), and the follow-up guard.
/// </summary>
public class CustomerNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 10, 15, 0, TimeSpan.Zero);
    private static readonly RegistryActor Rep = new("auth0|cs-agent", "Ana Cruz");
    private static readonly Guid Customer = Guid.CreateVersion7();

    private static CustomerNote Logged(
        CustomerNoteKind kind = CustomerNoteKind.InboundCall,
        string body = "Rang about the meter reading.",
        Guid? serviceAccountId = null,
        DateOnly? followUpOn = null,
        CustomerNoteLink? link = null,
        DateTimeOffset? now = null) =>
        CustomerNote.Log(Customer, serviceAccountId, kind, body, followUpOn, link, Rep, now ?? Now);

    [Fact]
    public void A_logged_note_records_who_wrote_it_and_when()
    {
        var note = Logged();

        Assert.Equal(Customer, note.CustomerId);
        Assert.Equal(CustomerNoteKind.InboundCall, note.Kind);
        Assert.Equal("Rang about the meter reading.", note.Body);
        Assert.Equal(Rep.Id, note.ActorId);
        Assert.Equal(Rep.Name, note.ActorName);
        Assert.Equal(Now, note.RecordedAt);

        // Guid v7 stamped from the clock, so the key index orders the log chronologically without a
        // second column to sort on.
        Assert.Equal(7, note.Id.Version);
    }

    [Fact]
    public void A_new_note_is_never_pinned_and_is_never_a_correction()
    {
        var note = Logged();

        Assert.False(note.IsPinned);
        Assert.False(note.IsCorrection);
        Assert.Null(note.CorrectsNoteId);
        Assert.Null(note.Link);
    }

    [Theory]
    [InlineData(CustomerNoteKind.Note, false)]
    [InlineData(CustomerNoteKind.InboundCall, true)]
    [InlineData(CustomerNoteKind.OutboundCall, true)]
    [InlineData(CustomerNoteKind.CounterVisit, true)]
    [InlineData(CustomerNoteKind.FieldVisit, true)]
    [InlineData(CustomerNoteKind.Complaint, true)]
    [InlineData(CustomerNoteKind.BillingDispute, true)]
    public void Every_kind_but_the_plain_note_records_a_contact_that_took_place(CustomerNoteKind kind, bool isInteraction)
    {
        // Pinned per member rather than asserted from the same expression the production code uses,
        // so a member added on the wrong side of the line fails here rather than being agreed with.
        Assert.Equal(isInteraction, Logged(kind).IsInteraction);
        Assert.Equal(isInteraction, CustomerNoteKinds.IsInteraction(kind));
    }

    [Fact]
    public void The_body_is_trimmed_and_a_blank_one_is_refused()
    {
        Assert.Equal("Rang twice.", Logged(body: "  Rang twice.  ").Body);

        var blank = Assert.Throws<RegistryValidationException>(() => Logged(body: "   "));

        Assert.Contains("records nothing", blank.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_GridCore_does_not_declare_is_refused()
    {
        // A cast from an undeclared integer is a legal expression, so a body off the wire is checked
        // rather than trusted. WORK_PACKAGES.md: "interaction requires a valid type".
        var refused = Assert.Throws<RegistryValidationException>(() => Logged(kind: (CustomerNoteKind)99));

        Assert.Contains("not a note kind GridCore declares", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_must_name_the_customer_it_is_about() =>
        Assert.Throws<RegistryValidationException>(() =>
            CustomerNote.Log(Guid.Empty, null, CustomerNoteKind.Note, "Anything.", null, null, Rep, Now));

    [Fact]
    public void A_follow_up_before_today_is_refused()
    {
        var yesterday = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-1);

        var refused = Assert.Throws<RegistryValidationException>(() => Logged(followUpOn: yesterday));

        Assert.Contains("cannot be set for", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_follow_up_TODAY_is_allowed()
    {
        // The commonest follow-up there is — "ring them back this afternoon" — and a rule that
        // refused it would read as an off-by-one to every rep who hit it.
        var today = DateOnly.FromDateTime(Now.UtcDateTime);

        Assert.Equal(today, Logged(followUpOn: today).FollowUpOn);
    }

    [Fact]
    public void A_follow_up_in_the_future_is_allowed()
    {
        var thursday = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(3);

        Assert.Equal(thursday, Logged(followUpOn: thursday).FollowUpOn);
    }

    [Fact]
    public void A_link_is_read_back_as_one_piece()
    {
        var billId = Guid.CreateVersion7();
        var note = Logged(link: new CustomerNoteLink(CustomerNoteLinkKind.Bill, billId, "BIL-000042"));

        // The three columns put back together. A caller must never find an id without the kind that
        // says which register to look in.
        var link = Assert.IsType<CustomerNoteLink>(note.Link);

        Assert.Equal(CustomerNoteLinkKind.Bill, link.Kind);
        Assert.Equal(billId, link.EntityId);
        Assert.Equal("BIL-000042", link.Reference);
    }

    [Fact]
    public void A_link_naming_no_row_is_refused()
    {
        var refused = Assert.Throws<RegistryValidationException>(() =>
            Logged(link: new CustomerNoteLink(CustomerNoteLinkKind.Payment, Guid.Empty, null)));

        Assert.Contains("must name which one", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_link_to_a_register_GridCore_does_not_have_is_refused() =>
        Assert.Throws<RegistryValidationException>(() =>
            Logged(link: new CustomerNoteLink((CustomerNoteLinkKind)42, Guid.CreateVersion7(), null)));

    [Fact]
    public void A_correction_leaves_the_note_it_corrects_exactly_as_it_was()
    {
        // The package's central rule. Every field of the original is compared afterwards, not just
        // the body, because "append-only" is a claim about the whole row.
        var original = Logged(body: "No answer.", followUpOn: DateOnly.FromDateTime(Now.UtcDateTime).AddDays(1));
        var before = Snapshot(original);

        CustomerNote.Correct(
            original,
            CustomerNoteKind.OutboundCall,
            "Answered — test confirmed for Tuesday.",
            null,
            null,
            new RegistryActor("auth0|supervisor", "Jo Reyes"),
            Now.AddHours(2));

        Assert.Equal(before, Snapshot(original));
    }

    [Fact]
    public void A_correction_is_a_new_note_pointing_at_the_old_one()
    {
        var original = Logged(body: "No answer.");

        var correction = CustomerNote.Correct(
            original,
            CustomerNoteKind.CounterVisit,
            "They came in instead.",
            null,
            null,
            Rep,
            Now.AddHours(2));

        Assert.NotEqual(original.Id, correction.Id);
        Assert.True(correction.IsCorrection);
        Assert.Equal(original.Id, correction.CorrectsNoteId);

        // The kind is supplied afresh: "logged as a call, it was actually a counter visit" is exactly
        // what a correction is for.
        Assert.Equal(CustomerNoteKind.CounterVisit, correction.Kind);
    }

    [Fact]
    public void A_correction_is_filed_where_the_note_it_corrects_was_filed()
    {
        var accountId = Guid.CreateVersion7();
        var original = Logged(serviceAccountId: accountId);

        var correction = CustomerNote.Correct(original, CustomerNoteKind.Note, "Actually.", null, null, Rep, Now);

        // A correction that could re-file a note under a different customer or account is not a
        // correction; it is a second note, and the caller should log one.
        Assert.Equal(original.CustomerId, correction.CustomerId);
        Assert.Equal(accountId, correction.ServiceAccountId);
    }

    [Fact]
    public void A_correction_can_itself_be_corrected()
    {
        // A chain read end to end is the honest record of somebody getting it wrong twice. Collapsing
        // it to the first note would lose the middle version a customer may have been quoted from.
        var original = Logged(body: "First.");
        var second = CustomerNote.Correct(original, CustomerNoteKind.Note, "Second.", null, null, Rep, Now.AddHours(1));
        var third = CustomerNote.Correct(second, CustomerNoteKind.Note, "Third.", null, null, Rep, Now.AddHours(2));

        Assert.Equal(original.Id, second.CorrectsNoteId);
        Assert.Equal(second.Id, third.CorrectsNoteId);
    }

    [Fact]
    public void A_correction_meets_the_same_guards_a_note_does()
    {
        var original = Logged();
        var yesterday = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-1);

        Assert.Throws<RegistryValidationException>(() =>
            CustomerNote.Correct(original, CustomerNoteKind.Note, "   ", null, null, Rep, Now));

        Assert.Throws<RegistryValidationException>(() =>
            CustomerNote.Correct(original, CustomerNoteKind.Note, "Fine.", yesterday, null, Rep, Now));
    }

    [Fact]
    public void Pinning_moves_the_flag_and_says_whether_it_moved()
    {
        var note = Logged();

        Assert.True(note.SetPinned(true));
        Assert.True(note.IsPinned);

        // Idempotent: two reps pinning the same note is not a conflict, and the false is what lets
        // the service skip auditing a no-op.
        Assert.False(note.SetPinned(true));

        Assert.True(note.SetPinned(false));
        Assert.False(note.IsPinned);
    }

    [Fact]
    public void Pinning_changes_nothing_a_reader_would_quote()
    {
        // "Append-only except one column" is the kind of exception that grows a second member, so
        // what pinning may touch is pinned down rather than left to the property setters.
        var note = Logged(body: "Rang about the bill.", followUpOn: DateOnly.FromDateTime(Now.UtcDateTime));
        var before = Snapshot(note) with { IsPinned = true };

        note.SetPinned(true);

        Assert.Equal(before, Snapshot(note));
    }

    /// <summary>Every field of a note, so a test can assert that none of them moved.</summary>
    private static CustomerNoteSnapshot Snapshot(CustomerNote note) => CustomerNoteSnapshot.Of(note);
}

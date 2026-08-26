using FluentValidation;
using GridCore.Modules.Customers.Features.Notes;

namespace GridCore.Modules.Customers.UnitTests.Notes;

/// <summary>
/// Edge validation only — what a body can get wrong on its own, with no clock and no database.
/// </summary>
/// <remarks>
/// The follow-up date is pointedly absent: "cannot be in the past" needs the clock, so it lives in
/// <c>CustomerNote</c> where a seeder meets it too, and <c>CustomerNoteTests</c> is where it is
/// pinned. What is here is the pair of mistakes the validator can see by itself.
/// </remarks>
public class NoteValidatorTests
{
    private static readonly LogNoteRequestValidator Log = new();
    private static readonly CorrectNoteRequestValidator Correct = new();

    private static IReadOnlyList<string> ErrorsFrom<T>(IValidator<T> validator, T request) =>
        [.. validator.Validate(request).Errors.Select(failure => failure.ErrorMessage)];

    [Fact]
    public void An_ordinary_note_passes() =>
        Assert.Empty(ErrorsFrom(Log, new LogNoteRequest(CustomerNoteKind.InboundCall, "Rang about the reading.")));

    [Fact]
    public void An_empty_note_is_refused() =>
        Assert.Contains(
            "A note must say something; an empty note records nothing.",
            ErrorsFrom(Log, new LogNoteRequest(CustomerNoteKind.Note, "   ")));

    [Fact]
    public void A_note_longer_than_the_column_is_refused() =>
        Assert.NotEmpty(ErrorsFrom(Log, new LogNoteRequest(CustomerNoteKind.Note, new string('x', CustomerNote.BodyLength + 1))));

    [Fact]
    public void A_note_exactly_as_long_as_the_column_passes() =>
        // Both sides of the boundary, because an off-by-one here is a 400 on a note a rep spent five
        // minutes typing.
        Assert.Empty(ErrorsFrom(Log, new LogNoteRequest(CustomerNoteKind.Note, new string('x', CustomerNote.BodyLength))));

    [Fact]
    public void A_kind_GridCore_does_not_declare_is_refused() =>
        // WORK_PACKAGES.md: "interaction requires a valid type". A cast from an undeclared integer is
        // a legal expression, so a body off the wire is checked rather than trusted.
        Assert.Contains(
            "A note must say what it is — a note, or the kind of contact that took place.",
            ErrorsFrom(Log, new LogNoteRequest((CustomerNoteKind)99, "Rang.")));

    [Theory]
    [InlineData(CustomerNoteKind.Note)]
    [InlineData(CustomerNoteKind.InboundCall)]
    [InlineData(CustomerNoteKind.OutboundCall)]
    [InlineData(CustomerNoteKind.CounterVisit)]
    [InlineData(CustomerNoteKind.FieldVisit)]
    [InlineData(CustomerNoteKind.Complaint)]
    [InlineData(CustomerNoteKind.BillingDispute)]
    public void Every_kind_the_work_package_names_is_accepted(CustomerNoteKind kind) =>
        // The list from WORK_PACKAGES.md, spelled out, so a kind renamed out of existence fails here
        // rather than in a screen nobody opened.
        Assert.Empty(ErrorsFrom(Log, new LogNoteRequest(kind, "Something happened.")));

    [Fact]
    public void A_note_with_no_link_passes() =>
        Assert.Empty(ErrorsFrom(Log, new LogNoteRequest(CustomerNoteKind.Note, "Standing instruction.", Link: null)));

    [Theory]
    [InlineData(CustomerNoteLinkKind.Bill)]
    [InlineData(CustomerNoteLinkKind.Payment)]
    [InlineData(CustomerNoteLinkKind.WorkOrder)]
    public void A_link_to_any_of_the_three_registers_passes_the_edge(CustomerNoteLinkKind kind) =>
        // The edge only checks shape. Whether the row exists is the service's question, and for a
        // work order there is nobody to ask until WP-3.1.
        Assert.Empty(ErrorsFrom(
            Log,
            new LogNoteRequest(CustomerNoteKind.Note, "About that.", Link: new NoteLinkRequest(kind, Guid.CreateVersion7()))));

    [Fact]
    public void A_link_naming_no_row_is_refused() =>
        Assert.Contains(
            "A link must name which bill, payment or work order it means.",
            ErrorsFrom(Log, new LogNoteRequest(CustomerNoteKind.Note, "About that.", Link: new NoteLinkRequest(CustomerNoteLinkKind.Bill, Guid.Empty))));

    [Fact]
    public void A_link_to_a_register_GridCore_does_not_have_is_refused() =>
        Assert.Contains(
            "A note can be filed against a bill, a payment or a work order.",
            ErrorsFrom(Log, new LogNoteRequest(CustomerNoteKind.Note, "About that.", Link: new NoteLinkRequest((CustomerNoteLinkKind)42, Guid.CreateVersion7()))));

    [Fact]
    public void A_correction_meets_the_same_rules_a_note_does()
    {
        Assert.Empty(ErrorsFrom(Correct, new CorrectNoteRequest(CustomerNoteKind.CounterVisit, "It was a counter visit.")));
        Assert.NotEmpty(ErrorsFrom(Correct, new CorrectNoteRequest(CustomerNoteKind.Note, "  ")));
        Assert.NotEmpty(ErrorsFrom(Correct, new CorrectNoteRequest((CustomerNoteKind)99, "Fine.")));
        Assert.NotEmpty(ErrorsFrom(
            Correct,
            new CorrectNoteRequest(CustomerNoteKind.Note, "Fine.", Link: new NoteLinkRequest(CustomerNoteLinkKind.Bill, Guid.Empty))));
    }

    [Fact]
    public void Pinning_has_nothing_to_validate() =>
        // The validator exists so the filter can find one — it throws when none is registered, which
        // would turn an omission into a 500 on the first pin anybody attempted. A boolean has nothing
        // to be wrong about.
        Assert.True(new PinNoteRequestValidator().Validate(new PinNoteRequest(true)).IsValid);
}

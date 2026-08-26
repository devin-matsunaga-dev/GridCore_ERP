using FluentValidation;

namespace GridCore.Modules.Customers.Features.Notes;

/// <summary>
/// The rules a note's link has to meet before anything is looked up.
/// </summary>
/// <remarks>
/// Shape only: whether the row exists and whether it is this customer's are questions for the
/// service, which can reach the directories. What is here is the pair of mistakes a body can make on
/// its own — naming a register GridCore does not have, and naming one without saying which row.
/// </remarks>
public sealed class NoteLinkRequestValidator : AbstractValidator<NoteLinkRequest>
{
    /// <summary>Builds the rules.</summary>
    public NoteLinkRequestValidator()
    {
        RuleFor(link => link.Kind)
            .Must(CustomerNoteLinkKinds.IsKnown)
            .WithMessage("A note can be filed against a bill, a payment or a work order.");

        RuleFor(link => link.EntityId)
            .NotEmpty()
            .WithMessage("A link must name which bill, payment or work order it means.");
    }
}

/// <summary>
/// Rules for logging a note or an interaction.
/// </summary>
/// <remarks>
/// <para>
/// Field shape only, the split every validator in this module keeps. <b>The follow-up date is not
/// checked here:</b> "cannot be in the past" needs the clock, which a validator does not have and
/// should not be handed — the guard lives in <c>CustomerNote</c>, where a seeder and a later module
/// calling the service directly meet it too. That is the call <c>MeterReading</c> and <c>Asset</c>
/// already made about a date in the future.
/// </para>
/// <para>
/// The kind IS checked here, because WORK_PACKAGES.md asks for it at the edge ("interaction requires
/// a valid type") and because an undeclared enum arriving off the wire is a malformed body rather
/// than a rule about the register. The aggregate checks it again; both are cheap and only one of
/// them protects a caller who is not an HTTP request.
/// </para>
/// </remarks>
public sealed class LogNoteRequestValidator : AbstractValidator<LogNoteRequest>
{
    /// <summary>Builds the rules.</summary>
    public LogNoteRequestValidator()
    {
        RuleFor(request => request.Kind)
            .Must(CustomerNoteKinds.IsKnown)
            .WithMessage("A note must say what it is — a note, or the kind of contact that took place.");

        RuleFor(request => request.Body)
            .NotEmpty()
            .WithMessage("A note must say something; an empty note records nothing.")
            .MaximumLength(CustomerNote.BodyLength)
            .WithMessage($"A note is at most {CustomerNote.BodyLength} characters.");

        RuleFor(request => request.Link!).SetValidator(new NoteLinkRequestValidator()).When(request => request.Link is not null);
    }
}

/// <summary>
/// Rules for correcting a note.
/// </summary>
/// <remarks>
/// The same rules as logging one, because a correction <i>is</i> a note — it is filed where the
/// original was and it says what the original should have said. What it does not carry is a customer
/// or an account: those come from the note being corrected, so there is nothing here to validate
/// about them.
/// </remarks>
public sealed class CorrectNoteRequestValidator : AbstractValidator<CorrectNoteRequest>
{
    /// <summary>Builds the rules.</summary>
    public CorrectNoteRequestValidator()
    {
        RuleFor(request => request.Kind)
            .Must(CustomerNoteKinds.IsKnown)
            .WithMessage("A correction must say what the contact actually was.");

        RuleFor(request => request.Body)
            .NotEmpty()
            .WithMessage("A correction must say what the note should have said.")
            .MaximumLength(CustomerNote.BodyLength)
            .WithMessage($"A note is at most {CustomerNote.BodyLength} characters.");

        RuleFor(request => request.Link!).SetValidator(new NoteLinkRequestValidator()).When(request => request.Link is not null);
    }
}

/// <summary>
/// Rules for pinning a note.
/// </summary>
/// <remarks>
/// There are none, and the validator exists anyway. Every request body the endpoints accept has one
/// registered — <c>CustomersModuleTests</c> asserts it, because the validation filter throws when one
/// is missing and that would turn an omission here into a 500 on the first pin anybody attempts. A
/// boolean has nothing to be wrong about; whether the note exists is the service's 404.
/// </remarks>
public sealed class PinNoteRequestValidator : AbstractValidator<PinNoteRequest>;

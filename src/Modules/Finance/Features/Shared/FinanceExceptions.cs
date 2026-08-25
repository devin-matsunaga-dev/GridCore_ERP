namespace GridCore.Modules.Finance.Features.Shared;

/// <summary>
/// Base of the failures the Finance module's endpoints translate into ProblemDetails responses.
/// The ledger throws these rather than returning result objects, so a rule can be enforced in the
/// one place that knows it and still reach the caller as the right status code.
/// </summary>
/// <remarks>
/// Finance's own hierarchy rather than a shared one, for the reason WP-1.3 gave and every module
/// since has repeated: every message in it names an entry or an account, and a platform-wide "not
/// found" would have to be told what it was looking for.
/// </remarks>
public abstract class FinanceException(string message) : Exception(message);

/// <summary>No journal entry with that id. Surfaces as 404.</summary>
public sealed class JournalEntryNotFoundException(Guid id)
    : FinanceException($"Journal entry '{id}' was not found.")
{
    /// <summary>The id that was looked up.</summary>
    public Guid JournalEntryId { get; } = id;
}

/// <summary>
/// A posting the ledger will not accept — an entry that does not balance, a line that is neither a
/// debit nor a credit, an amount finer than a cent, or an account the chart does not declare.
/// Surfaces as 400.
/// </summary>
/// <remarks>
/// <b>Every one of these is a defect, not a user error.</b> Nothing outside this module builds a
/// posting: they arrive from <see cref="EventSeam.FinancePostings"/>, which is pure and unit
/// tested. A consumer that raises one faults its message rather than swallowing it — a fact
/// Finance could not post is precisely the thing that must not be lost quietly, because the
/// alternative is a ledger that silently disagrees with the modules upstream of it.
/// </remarks>
public sealed class FinanceValidationException(string message) : FinanceException(message);

using GridCore.Modules.Finance.Data;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Finance.Features.Journal;

/// <summary>What a caller filters the general ledger by.</summary>
/// <param name="Source">Only entries raised by this upstream fact, e.g. <c>billing.bill_issued</c>.</param>
/// <param name="Reference">Only entries carrying this business reference — a bill number, say.</param>
/// <param name="ServiceAccountId">Only entries about this service account.</param>
/// <param name="CustomerId">Only entries about this customer.</param>
/// <param name="From">Only entries posted on or after this accounting date.</param>
/// <param name="To">Only entries posted on or before it.</param>
/// <param name="Limit">Most rows to return.</param>
public sealed record JournalQuery(
    string? Source = null,
    string? Reference = null,
    Guid? ServiceAccountId = null,
    Guid? CustomerId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    int Limit = 50);

/// <summary>The general ledger, read-only. Endpoints are a thin layer over it.</summary>
/// <remarks>
/// There is no write method here on purpose. The only thing that posts to the ledger is
/// <see cref="EventSeam.JournalPostingSeam"/>, reacting to a fact another module has already
/// stated — so a service with a <c>PostAsync</c> on it would be an invitation to raise an entry
/// from a screen, which is the manual-journal surface <c>finance.post</c> is reserved for and
/// which this work package deliberately does not build.
/// </remarks>
public interface IJournalService
{
    /// <summary>The ledger, newest first.</summary>
    Task<IReadOnlyList<JournalEntry>> ListAsync(JournalQuery query, CancellationToken cancellationToken = default);

    /// <summary>One entry with its lines, or <see langword="null"/> when there is no such id.</summary>
    Task<JournalEntry?> FindAsync(Guid journalEntryId, CancellationToken cancellationToken = default);
}

/// <summary>The general ledger over the finance schema.</summary>
public sealed class JournalService(FinanceDbContext database) : IJournalService
{
    /// <summary>The largest page a list will return, whatever the caller asks for.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<JournalEntry>> ListAsync(
        JournalQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var entries = database.JournalEntries.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Source))
        {
            entries = entries.Where(entry => entry.Source == query.Source);
        }

        if (!string.IsNullOrWhiteSpace(query.Reference))
        {
            entries = entries.Where(entry => entry.Reference == query.Reference);
        }

        if (query.ServiceAccountId is { } serviceAccountId)
        {
            entries = entries.Where(entry => entry.ServiceAccountId == serviceAccountId);
        }

        if (query.CustomerId is { } customerId)
        {
            entries = entries.Where(entry => entry.CustomerId == customerId);
        }

        if (query.From is { } from)
        {
            entries = entries.Where(entry => entry.PostedOn >= from);
        }

        if (query.To is { } to)
        {
            entries = entries.Where(entry => entry.PostedOn <= to);
        }

        // Ordered by key: ids are Guid v7, so the primary-key index already orders chronologically
        // on Postgres and on the fast tier's SQLite alike. Not by PostedOn, which is the accounting
        // date and ties for every entry raised on the same day.
        return await entries
            .Include(entry => entry.Lines.OrderBy(line => line.Sequence))
            .ThenInclude(line => line.Account)
            .OrderByDescending(entry => entry.Id)
            .Take(Math.Clamp(query.Limit, 1, MaxPageSize))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<JournalEntry?> FindAsync(Guid journalEntryId, CancellationToken cancellationToken = default) =>
        database.JournalEntries
            .AsNoTracking()
            .Include(entry => entry.Lines.OrderBy(line => line.Sequence))
            .ThenInclude(line => line.Account)
            .FirstOrDefaultAsync(entry => entry.Id == journalEntryId, cancellationToken);
}

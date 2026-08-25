using GridCore.Modules.Finance.Features.EventSeam;

namespace GridCore.IntegrationTests.Infrastructure;

/// <summary>
/// Lets the gate tier see Finance's event seam fire without polling a broker. Since WP-2.6 the
/// ledger behind the seam is real, so this observes rather than substitutes — a test awaits the
/// posting and then reads the journal entry it caused.
/// </summary>
public sealed class JournalPostingRecorder
{
    private readonly List<JournalPostingIntent> _postings = [];
    private readonly Lock _gate = new();

    private TaskCompletionSource<JournalPostingIntent> _next =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Every posting that has arrived, oldest first.</summary>
    public IReadOnlyList<JournalPostingIntent> Postings
    {
        get
        {
            lock (_gate)
            {
                return [.. _postings];
            }
        }
    }

    /// <summary>
    /// Awaits the next posting to arrive. Taken <b>before</b> the act that should cause it, so a
    /// delivery that beats the assertion is still observed — polling or sleeping for a broker is
    /// what CONVENTIONS.md rule G forbids.
    /// </summary>
    public Task<JournalPostingIntent> NextAsync()
    {
        lock (_gate)
        {
            return _next.Task;
        }
    }

    /// <summary>Records a posting and releases whoever is awaiting the next one.</summary>
    public void Record(JournalPostingIntent posting)
    {
        TaskCompletionSource<JournalPostingIntent> waiting;

        lock (_gate)
        {
            _postings.Add(posting);

            waiting = _next;
            _next = new TaskCompletionSource<JournalPostingIntent>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        waiting.TrySetResult(posting);
    }

    /// <summary>Forgets what has arrived so far, so one test cannot see another's deliveries.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _postings.Clear();
        }
    }
}

/// <summary>
/// The real ledger with a tap on it, so a test can await the seam firing.
/// </summary>
/// <remarks>
/// A decorator, not a substitute. Before WP-2.6 this stood in for a ledger that did not exist; now
/// the posting really is written, and the recorder only says when. Recording happens <b>after</b>
/// the inner post, so a test released by <see cref="JournalPostingRecorder.NextAsync"/> can read
/// the entry back — a tap that fired first would hand the test a race with its own assertion.
/// </remarks>
public sealed class RecordingJournalPostingSeam(JournalPostingSeam ledger, JournalPostingRecorder recorder)
    : IJournalPostingSeam
{
    /// <inheritdoc />
    public async Task PostAsync(JournalPostingIntent posting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(ledger);

        await ledger.PostAsync(posting, cancellationToken).ConfigureAwait(false);

        recorder.Record(posting);
    }
}

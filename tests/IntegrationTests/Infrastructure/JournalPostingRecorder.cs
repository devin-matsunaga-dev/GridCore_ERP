using GridCore.Modules.Finance.Features.EventSeam;

namespace GridCore.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for Finance's ledger so the gate tier can see the event seam fire. Registered over
/// <see cref="IJournalPostingSeam"/> exactly the way production will swap the real ledger in —
/// by DI, with no change to the modules that raise the events.
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

/// <summary>The no-op ledger, wired to the recorder so a test can await the seam firing.</summary>
public sealed class RecordingJournalPostingSeam(JournalPostingRecorder recorder) : IJournalPostingSeam
{
    /// <inheritdoc />
    public Task PostAsync(JournalPostingIntent posting, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        recorder.Record(posting);

        return Task.CompletedTask;
    }
}

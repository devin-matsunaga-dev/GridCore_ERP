using GridCore.Modules.Billing.Features.Bills;
using GridCore.Modules.Billing.Features.Documents;
using GridCore.Modules.Billing.Features.Shared;
using GridCore.Modules.Billing.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Billing.UnitTests.Documents;

/// <summary>
/// The reprint over the register (WP-2.14): what it loads, what it refuses, and the one row it
/// writes — the audit entry that says a document left the building.
/// </summary>
public sealed class BillDocumentServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeClock _clock = new(Now);
    private readonly BillingTestHost _host;

    public BillDocumentServiceTests() => _host = new BillingTestHost(_clock, new FakeCurrentUser("auth0|clerk", "Ana Cruz"));

    public void Dispose() => _host.Dispose();

    /// <summary>Raises and issues a bill on a fresh premise.</summary>
    private async Task<Bill> AnIssuedBillAsync(string cycleCode = "2026-07")
    {
        var location = Guid.CreateVersion7(_clock.GetUtcNow());

        _host.Accounts.Add(location);
        _host.Readings.Add(location, consumption: 400m, cycleCode: cycleCode, readingDate: Now.AddDays(-30));

        var run = await _host.WithBillsAsync(register => register.RunAsync(new RunBillingInput(cycleCode)));

        // A step between writes: Guid v7 takes its timestamp from the clock, so rows minted inside
        // one frozen millisecond have no defined order at all.
        _clock.Advance(TimeSpan.FromMinutes(1));

        var draft = Assert.Single(run.Bills);
        var issued = await _host.WithBillsAsync(register => register.IssueAsync(draft.Id, new IssueBillInput()));

        _clock.Advance(TimeSpan.FromMinutes(1));

        return issued;
    }

    [Fact]
    public async Task A_reprint_loads_the_lines_AND_the_whole_correction_history()
    {
        // The service's own job, as opposed to BillDocument's: fetching enough for the document to
        // be legal. Without the Include of adjustments, BillDocument.Of refuses — which is exactly
        // how this would be caught if somebody trimmed the query.
        var bill = await AnIssuedBillAsync();

        await _host.WithBillsAsync(register =>
            register.AdjustAsync(bill.Id, new AdjustBillInput(BillAdjustmentKind.Credit, 20.00m, "Meter misread")));

        var document = await _host.WithDocumentsAsync(documents => documents.ReprintAsync(bill.Id));

        Assert.Equal(bill.TotalAmount, document.PrintedTotal);
        Assert.NotEmpty(document.Lines);
        Assert.Equal(-20.00m, Assert.Single(document.Corrections).Amount);
        Assert.Equal(bill.TotalAmount - 20.00m, document.AmountDue);
    }

    [Fact]
    public async Task Producing_a_copy_is_AUDITED_though_nothing_changed()
    {
        var bill = await AnIssuedBillAsync();

        await _host.WithDocumentsAsync(documents => documents.ReprintAsync(bill.Id));

        await using var context = _host.NewPlatformContext();

        var entry = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.BillReprinted);

        Assert.Equal(AuditEntityTypes.Bill, entry.EntityType);
        Assert.Equal(bill.Id.ToString(), entry.EntityId);
        Assert.Equal("auth0|clerk", entry.UserId);

        // No before and no after-diff: nothing about the bill moved. What the entry carries is what
        // the document SAID, so it can be read on its own years later without following an id to a
        // row that has been credited twice since.
        Assert.Null(entry.BeforeJson);
        Assert.Contains(bill.BillNumber, entry.AfterJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_without_the_documents_permission_is_refused()
    {
        // THE FAILURE PATH THE PACKAGE ASKS FOR. The caller holds billing.generate and billing.read
        // — they raised the bill — and still may not send a copy of it out, because producing a
        // document is its own act with its own grant.
        var bill = await AnIssuedBillAsync();

        var clerk = FakeCurrentUser.Holding(Permissions.Billing.Read, Permissions.Billing.Generate);

        var error = await Assert.ThrowsAsync<BillingPermissionException>(() =>
            _host.AsAsync(clerk, documents => documents.ReprintAsync(bill.Id)));

        Assert.Contains(Permissions.Customers.Documents, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_reprint_leaves_no_trail_entry_claiming_a_copy_went_out()
    {
        // The order the service does its two jobs in, asserted. An entry written before the document
        // was built would say a customer was sent something they were not.
        var bill = await AnIssuedBillAsync();

        var clerk = FakeCurrentUser.Holding(Permissions.Billing.Read);

        await Assert.ThrowsAsync<BillingPermissionException>(() =>
            _host.AsAsync(clerk, documents => documents.ReprintAsync(bill.Id)));

        await using var context = _host.NewPlatformContext();

        Assert.False(await context.AuditEntries.AnyAsync(audit => audit.Action == AuditActions.BillReprinted));
    }

    [Fact]
    public async Task A_draft_has_no_document_and_leaves_no_entry()
    {
        var location = Guid.CreateVersion7(_clock.GetUtcNow());

        _host.Accounts.Add(location);
        _host.Readings.Add(location, consumption: 400m, cycleCode: "2026-08", readingDate: Now.AddDays(-30));

        var run = await _host.WithBillsAsync(register => register.RunAsync(new RunBillingInput("2026-08")));
        var draft = Assert.Single(run.Bills);

        await Assert.ThrowsAsync<BillingWorkflowException>(() =>
            _host.WithDocumentsAsync(documents => documents.ReprintAsync(draft.Id)));

        await using var context = _host.NewPlatformContext();

        Assert.False(await context.AuditEntries.AnyAsync(audit => audit.Action == AuditActions.BillReprinted));
    }

    [Fact]
    public async Task An_id_that_matches_nothing_is_a_404_rather_than_an_empty_document() =>
        await Assert.ThrowsAsync<BillNotFoundException>(() =>
            _host.WithDocumentsAsync(documents => documents.ReprintAsync(Guid.CreateVersion7())));
}

using System.Text.Json;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Documents;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Modules.Customers.UnitTests.Infrastructure;
using GridCore.Platform.Audit;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.UnitTests.Documents;

/// <summary>
/// The statement and the export over three registers (WP-2.14): what is gathered, what is refused,
/// and the one row each of them writes — the audit entry saying a document left the building.
/// </summary>
/// <remarks>
/// The deposit ledger is this module's own, on SQLite in memory; the bills and the payments arrive
/// through doubles, because a <c>billing</c> or a <c>payments</c> schema is exactly what this module
/// may never know about. That is the whole point of the seams this package widened.
/// </remarks>
public class CustomerDocumentServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly StatementRange July = new(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));

    private static CustomersTestHost NewHost(ICurrentUser? user = null) =>
        new(new FakeClock(Now), user ?? new FakeCurrentUser("auth0|cs-agent", "Ana Cruz"));

    private static Task<Customer> ARegisteredCustomerAsync(CustomersTestHost host, string name = "Sablan Family Residence") =>
        host.WithCustomersAsync(customers => customers.RegisterAsync(new RegisterCustomerInput(name, CustomerClass.Residential)));

    [Fact]
    public async Task A_statement_is_composed_across_bills_payments_and_the_deposit_ledger()
    {
        // The heaviest read of the phase, and it proves out. June's bill makes the opening balance;
        // July's bill, the credit against it, the payment and the deposit are the range.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        var june = host.Bills.Issued(customer.Id, new DateOnly(2026, 6, 5), totalAmount: 100.00m);

        host.Bills.Issued(customer.Id, new DateOnly(2026, 7, 5), totalAmount: 120.00m);
        host.Bills.Correct(customer.Id, june, -20.00m, new DateTimeOffset(2026, 7, 8, 10, 0, 0, TimeSpan.Zero));
        host.Payments.Add(customer.Id, amount: 80.00m, requestedAt: new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero));

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));

        var statement = await host.WithDocumentsAsync(documents => documents.StatementAsync(customer.Id, July));

        Assert.Equal(100.00m, statement.OpeningBalance);
        Assert.Equal(120.00m, statement.ClosingBalance);
        Assert.Equal(statement.OpeningBalance + statement.Entries.Sum(entry => entry.Amount), statement.ClosingBalance);

        Assert.Equal(120.00m, statement.Billed);
        Assert.Equal(-20.00m, statement.Corrected);
        Assert.Equal(80.00m, statement.Paid);

        // The deposit was taken today, which is outside July — so it is not on this statement, and
        // the deposit column reads nothing.
        Assert.Equal(0m, statement.ClosingDepositHeld);
        Assert.Equal(customer.AccountNumber, statement.AccountNumber);
    }

    [Fact]
    public async Task A_deposit_APPLIED_to_a_bill_reduces_what_is_owed_on_the_statement()
    {
        // The one deposit movement in both columns, end to end: the ledger entry is this module's,
        // the bill it settles is Billing's, and the statement is where the two meet.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);
        var bill = host.Bills.Add(customer.Id, amountDue: 120.00m);

        host.Bills.Issued(customer.Id, new DateOnly(2026, 8, 5), totalAmount: 120.00m);

        await host.WithDepositsAsync(deposits => deposits.CollectAsync(customer.Id, new CollectDepositInput(75.00m)));
        await host.WithDepositsAsync(deposits => deposits.ApplyAsync(customer.Id, new ApplyDepositInput(bill.Id, 75.00m)));

        var statement = await host.WithDocumentsAsync(documents =>
            documents.StatementAsync(customer.Id, new StatementRange(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31))));

        Assert.Equal(45.00m, statement.ClosingBalance);
        Assert.Equal(0m, statement.ClosingDepositHeld);
        Assert.Equal(75.00m, statement.DepositApplied);

        var applied = statement.Entries.Single(entry => entry.Kind is StatementEntryKind.DepositApplied);

        Assert.Equal(-75.00m, applied.Amount);
        Assert.Equal(-75.00m, applied.DepositAmount);
    }

    [Fact]
    public async Task A_WITHDRAWN_bill_takes_back_only_what_was_still_owed_on_it()
    {
        // A bill cancelled after part of it was paid only ever owed the remainder. Reversing the
        // whole charge would leave the statement claiming the utility owes the customer money it
        // kept.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        var bill = host.Bills.Issued(customer.Id, new DateOnly(2026, 7, 5), totalAmount: 120.00m, amountPaid: 50.00m);

        host.Payments.Add(customer.Id, amount: 50.00m, requestedAt: new DateTimeOffset(2026, 7, 10, 9, 0, 0, TimeSpan.Zero));
        host.Bills.Withdraw(customer.Id, bill, new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero));

        var statement = await host.WithDocumentsAsync(documents => documents.StatementAsync(customer.Id, July));

        // 120 billed, 50 paid, 70 taken back: nothing is owed and nothing was invented.
        Assert.Equal(0m, statement.ClosingBalance);

        var withdrawn = statement.Entries.Single(entry => entry.Kind is StatementEntryKind.BillWithdrawn);

        Assert.Equal(-70.00m, withdrawn.Amount);
    }

    [Fact]
    public async Task A_DECLINED_payment_is_not_credited_on_a_statement()
    {
        // A refusal moved no money. It belongs on the export, where the run of declines answers "why
        // does this customer still owe money" — and nowhere near a document that has to add up.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        host.Bills.Issued(customer.Id, new DateOnly(2026, 7, 5), totalAmount: 120.00m);
        host.Payments.Add(customer.Id, amount: 120.00m, status: "Declined", requestedAt: new DateTimeOffset(2026, 7, 20, 9, 0, 0, TimeSpan.Zero));

        var statement = await host.WithDocumentsAsync(documents => documents.StatementAsync(customer.Id, July));

        Assert.Equal(120.00m, statement.ClosingBalance);
        Assert.DoesNotContain(statement.Entries, entry => entry.Kind is StatementEntryKind.PaymentReceived);
    }

    [Fact]
    public async Task A_range_with_no_activity_answers_a_statement_rather_than_an_error()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        var statement = await host.WithDocumentsAsync(documents => documents.StatementAsync(customer.Id, July));

        Assert.Empty(statement.Entries);
        Assert.Equal(0m, statement.OpeningBalance);
        Assert.Equal(0m, statement.ClosingBalance);
        Assert.Equal("USD", statement.Currency);
    }

    [Fact]
    public async Task A_statement_says_so_when_a_registers_history_did_not_fit()
    {
        // Reported on the document rather than thrown. The opening balance is what every earlier
        // movement adds up to, so a short history is a short opening balance — and a statement that
        // was quietly short would prove out against itself and still be wrong.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        for (var index = 0; index < CustomerDocumentService.MaxHistory; index++)
        {
            host.Bills.Issued(customer.Id, new DateOnly(2026, 1, 1).AddDays(index % 150), totalAmount: 1.00m);
        }

        var statement = await host.WithDocumentsAsync(documents => documents.StatementAsync(customer.Id, July));

        Assert.True(statement.IsTruncated);
        Assert.Equal(CustomerDocumentService.MaxHistory, host.Bills.LastHistoryLimit);
    }

    [Fact]
    public async Task Producing_a_statement_is_AUDITED_though_nothing_changed()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        host.Bills.Issued(customer.Id, new DateOnly(2026, 7, 5), totalAmount: 120.00m);

        await host.WithDocumentsAsync(documents => documents.StatementAsync(customer.Id, July));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries
            .SingleAsync(candidate => candidate.Action == AuditActions.CustomerStatementProduced);

        Assert.Equal(AuditEntityTypes.CustomerDocument, entry.EntityType);
        Assert.Equal(customer.Id.ToString(), entry.EntityId);
        Assert.Null(entry.BeforeJson);

        var snapshot = JsonSerializer.Deserialize<StatementSnapshot>(entry.AfterJson!, AuditJson.Options);

        Assert.NotNull(snapshot);
        Assert.Equal(July.From, snapshot.From);
        Assert.Equal(July.To, snapshot.To);
        Assert.Equal(120.00m, snapshot.ClosingBalance);
    }

    [Fact]
    public async Task A_statement_without_the_documents_permission_is_refused_and_leaves_no_entry()
    {
        // THE FAILURE PATH THE PACKAGE ASKS FOR. The caller may read the account — they can see the
        // balance on screen — and may not send a statement of it out.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);
        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read, Permissions.Customers.Write);

        var error = await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, documents => documents.StatementAsync(customer.Id, July)));

        Assert.Contains(Permissions.Customers.Documents, error.Message, StringComparison.Ordinal);

        await using var platform = host.NewPlatformContext();

        Assert.False(await platform.AuditEntries
            .AnyAsync(candidate => candidate.Action == AuditActions.CustomerStatementProduced));
    }

    [Fact]
    public async Task A_range_that_runs_backwards_is_refused()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithDocumentsAsync(documents =>
                documents.StatementAsync(customer.Id, new StatementRange(July.To, July.From))));
    }

    [Fact]
    public async Task A_range_longer_than_a_statement_covers_is_refused()
    {
        // Usually a mistyped year. Refused rather than answered with a decade of movements nobody
        // asked for.
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        var error = await Assert.ThrowsAsync<RegistryValidationException>(() =>
            host.WithDocumentsAsync(documents => documents.StatementAsync(
                customer.Id,
                new StatementRange(new DateOnly(1990, 1, 1), new DateOnly(2026, 7, 31)))));

        Assert.Contains("mistyped year", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_statement_for_a_customer_who_does_not_exist_is_a_404() =>
        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            NewHost().WithDocumentsAsync(documents => documents.StatementAsync(Guid.CreateVersion7(), July)));

    [Fact]
    public async Task An_export_carries_every_attempt_and_names_the_bills_through_the_seam()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);
        var bill = host.Bills.Add(customer.Id);

        host.Payments.Add(customer.Id, billId: bill.Id, amount: 120.00m);
        host.Payments.Add(customer.Id, billId: bill.Id, amount: 40.00m, status: "Declined");

        var export = await host.WithDocumentsAsync(documents => documents.ExportPaymentHistoryAsync(customer.Id));

        Assert.Equal(2, export.Rows);
        Assert.Contains(bill.BillNumber, export.Csv, StringComparison.Ordinal);
        Assert.Contains("Declined,No", export.Csv, StringComparison.Ordinal);
        Assert.Equal($"payment-history-{customer.AccountNumber}-2026-08-26.csv", export.FileName);
    }

    [Fact]
    public async Task An_export_escapes_a_customer_name_that_would_otherwise_split_a_row()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host, "Cruz, Ana \"Anita\"");

        host.Payments.Add(customer.Id, amount: 120.00m);

        var export = await host.WithDocumentsAsync(documents => documents.ExportPaymentHistoryAsync(customer.Id));

        Assert.Contains("\"Cruz, Ana \"\"Anita\"\"\"", export.Csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_is_AUDITED()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);

        host.Payments.Add(customer.Id, amount: 120.00m);

        await host.WithDocumentsAsync(documents => documents.ExportPaymentHistoryAsync(customer.Id));

        await using var platform = host.NewPlatformContext();

        var entry = await platform.AuditEntries
            .SingleAsync(candidate => candidate.Action == AuditActions.CustomerPaymentHistoryExported);

        Assert.Equal(AuditEntityTypes.CustomerDocument, entry.EntityType);

        var snapshot = JsonSerializer.Deserialize<PaymentHistorySnapshot>(entry.AfterJson!, AuditJson.Options);

        Assert.NotNull(snapshot);
        Assert.Equal(1, snapshot.Rows);
        Assert.Equal(120.00m, snapshot.SettledTotal);

        // The file's name is in the trail, because it is what a recipient would quote.
        Assert.EndsWith(".csv", snapshot.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_export_without_the_documents_permission_is_refused()
    {
        using var host = NewHost();

        var customer = await ARegisteredCustomerAsync(host);
        var clerk = FakeCurrentUser.Holding(Permissions.Customers.Read);

        await Assert.ThrowsAsync<RegistryPermissionException>(() =>
            host.AsAsync(clerk, documents => documents.ExportPaymentHistoryAsync(customer.Id)));
    }
}

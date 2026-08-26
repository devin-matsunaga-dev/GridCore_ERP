using System.Text;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Documents;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Customers.Features.Documents;

/// <summary>One line of an account statement, as the API returns it.</summary>
/// <param name="Date">The day it lands on.</param>
/// <param name="OccurredAt">When it happened, for a screen that shows a time.</param>
/// <param name="Kind">What kind of movement it is.</param>
/// <param name="Description">What the line says.</param>
/// <param name="Reference">The number a customer can quote.</param>
/// <param name="Amount">Signed effect on what is owed. Zero on a deposit movement that is not an application.</param>
/// <param name="DepositAmount">Signed effect on the deposit held. Zero on everything else.</param>
/// <param name="BalanceAfter">What was owed once this line was applied.</param>
/// <param name="DepositHeldAfter">What was held once it was.</param>
/// <param name="BillId">The bill this concerns, where there is one — what a reprint link is built from.</param>
/// <param name="PaymentId">The payment this concerns, where there is one.</param>
/// <param name="DepositEntryId">The deposit ledger entry this concerns, where there is one.</param>
/// <param name="ServiceAccountId">The account it belongs to.</param>
/// <param name="AccountNumber">That account's number.</param>
public sealed record StatementEntryResponse(
    DateOnly Date,
    DateTimeOffset OccurredAt,
    string Kind,
    string Description,
    string? Reference,
    decimal Amount,
    decimal DepositAmount,
    decimal BalanceAfter,
    decimal DepositHeldAfter,
    Guid? BillId,
    Guid? PaymentId,
    Guid? DepositEntryId,
    Guid? ServiceAccountId,
    string? AccountNumber)
{
    /// <summary>Projects a statement line for the wire.</summary>
    public static StatementEntryResponse From(StatementEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new StatementEntryResponse(
            entry.Date,
            entry.OccurredAt,

            // By name, never the enum's number: a client renders a label from it, and a reordered
            // enum must never silently change what a statement line says.
            entry.Kind.ToString(),
            entry.Description,
            entry.Reference,
            entry.Amount,
            entry.DepositAmount,
            entry.BalanceAfter,
            entry.DepositHeldAfter,
            entry.BillId,
            entry.PaymentId,
            entry.DepositEntryId,
            entry.ServiceAccountId,
            entry.AccountNumber);
    }
}

/// <summary>An account statement, as the API returns it.</summary>
/// <param name="CustomerId">Whose statement.</param>
/// <param name="AccountNumber">The number they quote.</param>
/// <param name="CustomerName">Their name.</param>
/// <param name="MailingAddress">Where the utility posts to, on one line.</param>
/// <param name="From">First day of the range.</param>
/// <param name="To">Last day of it.</param>
/// <param name="Currency">ISO 4217 code every figure is expressed in.</param>
/// <param name="OpeningBalance">What was owed at the start.</param>
/// <param name="ClosingBalance">What was owed at the end.</param>
/// <param name="OpeningDepositHeld">What was held at the start.</param>
/// <param name="ClosingDepositHeld">What was held at the end.</param>
/// <param name="Entries">Every movement in the range, oldest first.</param>
/// <param name="Billed">What was billed in the range.</param>
/// <param name="Corrected">The signed sum of corrections made in it.</param>
/// <param name="Paid">What was paid in it, as a positive figure.</param>
/// <param name="DepositApplied">How much held deposit was put against bills in it, as a positive figure.</param>
/// <param name="IsTruncated">Whether a register's history did not fit, so the opening balance may be short.</param>
/// <param name="ProducedAt">When it was produced.</param>
/// <param name="ProducedById">Subject id of whoever produced it.</param>
/// <param name="ProducedByName">Their display name.</param>
public sealed record AccountStatementResponse(
    Guid CustomerId,
    string AccountNumber,
    string CustomerName,
    string? MailingAddress,
    DateOnly From,
    DateOnly To,
    string Currency,
    decimal OpeningBalance,
    decimal ClosingBalance,
    decimal OpeningDepositHeld,
    decimal ClosingDepositHeld,
    IReadOnlyList<StatementEntryResponse> Entries,
    decimal Billed,
    decimal Corrected,
    decimal Paid,
    decimal DepositApplied,
    bool IsTruncated,
    DateTimeOffset ProducedAt,
    string ProducedById,
    string? ProducedByName)
{
    /// <summary>
    /// Projects a statement for the wire.
    /// </summary>
    /// <remarks>
    /// <c>Of</c> rather than the <c>From</c> every other response in this module uses: the statement
    /// has a <see cref="From"/> of its own — the first day of the range — and a type cannot have both.
    /// The range keeps the name, because <c>from</c> and <c>to</c> are what the query string calls
    /// them and what a reader of the document expects.
    /// </remarks>
    public static AccountStatementResponse Of(AccountStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return new AccountStatementResponse(
            statement.CustomerId,
            statement.AccountNumber,
            statement.CustomerName,
            statement.MailingAddress,
            statement.From,
            statement.To,
            statement.Currency,
            statement.OpeningBalance,
            statement.ClosingBalance,
            statement.OpeningDepositHeld,
            statement.ClosingDepositHeld,
            [.. statement.Entries.Select(StatementEntryResponse.From)],
            statement.Billed,
            statement.Corrected,
            statement.Paid,
            statement.DepositApplied,
            statement.IsTruncated,
            statement.ProducedAt,
            statement.ProducedById,
            statement.ProducedByName);
    }
}

/// <summary>
/// The customer documents' HTTP surface (WP-2.14): a statement to read, and a payment history to
/// download.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both are GETs, though both write an audit entry.</b> The package is read-side: nothing moves,
/// and asking twice gives the same document. A POST would say the utility keeps a register of
/// statements, which it deliberately does not — a statement is composed from records that already
/// exist, and storing a second copy of them would create a document able to disagree with the
/// ledgers behind it.
/// </para>
/// <para>
/// <b>Gated on <c>customers.documents</c>, not <c>customers.read</c>.</b> Reading a balance on
/// screen and handing somebody a statement of it are different acts — see
/// <see cref="Permissions.Customers.Documents"/>. The services demand it too, because CONVENTIONS.md
/// asks a service to enforce its own permissions rather than trust the route that called it.
/// </para>
/// <para>
/// <b>The range is optional and defaults to the last <see cref="DefaultRangeDays"/> days.</b> A rep
/// opening the tab wants a statement, not a date form; the two selects then narrow it. A statement
/// with no range at all would have to mean "everything", which is the one range nobody asks for and
/// the slowest to produce.
/// </para>
/// </remarks>
public static class DocumentEndpoints
{
    /// <summary>Route prefix of one customer's documents.</summary>
    public const string RoutePrefix = "/api/customers/{customerId:guid}/documents";

    /// <summary>Route of the statement, under <see cref="RoutePrefix"/>.</summary>
    public const string StatementRoute = "/statement";

    /// <summary>Route of the payment-history export, under <see cref="RoutePrefix"/>.</summary>
    public const string PaymentHistoryRoute = "/payment-history";

    /// <summary>How far back a statement reaches when the caller names no range. A quarter.</summary>
    public const int DefaultRangeDays = 90;

    /// <summary>Maps the customer document endpoints.</summary>
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(RoutePrefix).WithTags("Customers");

        group
            .MapGet(StatementRoute, (
                    [FromRoute] Guid customerId,
                    DateOnly? from,
                    DateOnly? to,
                    [FromServices] ICustomerDocumentService documents,
                    [FromServices] TimeProvider clock,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var last = to ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
                    var first = from ?? last.AddDays(-DefaultRangeDays);

                    var statement = await documents.StatementAsync(
                        customerId,
                        new StatementRange(first, last),
                        cancellationToken);

                    return Results.Ok(AccountStatementResponse.Of(statement));
                }))
            .RequirePermission(Permissions.Customers.Documents)
            .WithName("GetCustomerStatement");

        // The one endpoint in GridCore that answers with something other than JSON. A file, with the
        // name in a Content-Disposition header — which is what makes a browser save it rather than
        // render it, and what makes the name the utility chose the name on the clerk's desktop.
        group
            .MapGet(PaymentHistoryRoute, (
                    [FromRoute] Guid customerId,
                    [FromServices] ICustomerDocumentService documents,
                    CancellationToken cancellationToken) =>
                RegistryProblems.RunAsync(async () =>
                {
                    var export = await documents.ExportPaymentHistoryAsync(customerId, cancellationToken);

                    // The byte-order mark in front of the text, so a spreadsheet reads it as UTF-8
                    // rather than guessing at the local code page and mangling every accented place
                    // name in the address column.
                    var bytes = new byte[CsvDocument.Preamble.Length + Encoding.UTF8.GetByteCount(export.Csv)];

                    CsvDocument.Preamble.CopyTo(bytes);
                    Encoding.UTF8.GetBytes(export.Csv, bytes.AsSpan(CsvDocument.Preamble.Length));

                    return Results.File(bytes, CsvDocument.ContentType, export.FileName);
                }))
            .RequirePermission(Permissions.Customers.Documents)
            .WithName("ExportCustomerPaymentHistory");

        return endpoints;
    }
}

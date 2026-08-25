using GridCore.Modules.Finance.Features.ChartOfAccounts;
using GridCore.Modules.Finance.Features.Reports;
using GridCore.Modules.Finance.Features.Shared;
using GridCore.Platform.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace GridCore.Modules.Finance.Features.Journal;

/// <summary>One line of a journal entry as the API returns it.</summary>
/// <param name="Sequence">Position within the entry, from 1.</param>
/// <param name="AccountCode">The account posted to.</param>
/// <param name="AccountName">What it is called on a report.</param>
/// <param name="AccountType">Which of the five kinds it is, by name.</param>
/// <param name="Debit">Amount debited. Zero on a credit line.</param>
/// <param name="Credit">Amount credited. Zero on a debit line.</param>
public sealed record JournalLineResponse(
    int Sequence,
    string AccountCode,
    string AccountName,
    string AccountType,
    decimal Debit,
    decimal Credit)
{
    /// <summary>Projects a line for the wire.</summary>
    public static JournalLineResponse From(JournalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return new JournalLineResponse(
            line.Sequence,
            line.Account.Code,
            line.Account.Name,
            line.Account.Type.ToString(),
            line.Debit,
            line.Credit);
    }
}

/// <summary>A journal entry as the API returns it.</summary>
/// <param name="Id">Identifier.</param>
/// <param name="EntryNumber">The number on the entry.</param>
/// <param name="EventId">The event that caused it.</param>
/// <param name="Source">Which upstream fact it came from.</param>
/// <param name="Reference">The business reference a person would recognise.</param>
/// <param name="Description">What the entry is for.</param>
/// <param name="Currency">ISO 4217 code the amounts are expressed in.</param>
/// <param name="PostedOn">The accounting date.</param>
/// <param name="OccurredAt">When the fact became true.</param>
/// <param name="PostedAt">When Finance wrote the entry.</param>
/// <param name="ServiceAccountId">The service account it is about, where it is about one.</param>
/// <param name="CustomerId">The customer it is about, where it is about one.</param>
/// <param name="TotalDebits">Sum of the debits.</param>
/// <param name="TotalCredits">Sum of the credits.</param>
/// <param name="IsBalanced">Whether the two agree. True of every entry that exists.</param>
/// <param name="Lines">The accounts posted to.</param>
/// <param name="ActorId">Subject id of whoever posted it — <c>system</c> for a consumer.</param>
/// <param name="ActorName">Their name at the time.</param>
public sealed record JournalEntryResponse(
    Guid Id,
    string EntryNumber,
    Guid? EventId,
    string Source,
    string Reference,
    string Description,
    string Currency,
    DateOnly PostedOn,
    DateTimeOffset OccurredAt,
    DateTimeOffset PostedAt,
    Guid? ServiceAccountId,
    Guid? CustomerId,
    decimal TotalDebits,
    decimal TotalCredits,
    bool IsBalanced,
    IReadOnlyList<JournalLineResponse> Lines,
    string ActorId,
    string? ActorName)
{
    /// <summary>Projects an entry for the wire.</summary>
    public static JournalEntryResponse From(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return new JournalEntryResponse(
            entry.Id,
            entry.EntryNumber,
            entry.EventId,
            entry.Source,
            entry.Reference,
            entry.Description,
            entry.Currency,
            entry.PostedOn,
            entry.OccurredAt,
            entry.PostedAt,
            entry.ServiceAccountId,
            entry.CustomerId,
            entry.TotalDebits,
            entry.TotalCredits,
            entry.IsBalanced,
            [.. entry.Lines.Select(JournalLineResponse.From)],
            entry.ActorId,
            entry.ActorName);
    }
}

/// <summary>One account in the chart, as the API returns it.</summary>
/// <param name="Code">The code a person quotes.</param>
/// <param name="Name">What it is called on a report.</param>
/// <param name="Type">Which of the five kinds it is, by name.</param>
/// <param name="NormalBalance">Which side it is normally increased on, by name.</param>
public sealed record AccountResponse(string Code, string Name, string Type, string NormalBalance)
{
    /// <summary>Projects an account for the wire.</summary>
    public static AccountResponse From(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new AccountResponse(
            account.Code,
            account.Name,
            account.Type.ToString(),
            account.NormalBalance.ToString());
    }
}

/// <summary>One account's position on a trial balance, as the API returns it.</summary>
/// <param name="AccountCode">The code a person quotes.</param>
/// <param name="AccountName">What it is called.</param>
/// <param name="AccountType">Which of the five kinds it is, by name.</param>
/// <param name="NormalBalance">Which side it is normally increased on, by name.</param>
/// <param name="Debits">Everything debited to it.</param>
/// <param name="Credits">Everything credited to it.</param>
/// <param name="Balance">What it stands at, signed the way the account normally runs.</param>
/// <param name="LineCount">How many ledger lines are behind the figures.</param>
public sealed record TrialBalanceRowResponse(
    string AccountCode,
    string AccountName,
    string AccountType,
    string NormalBalance,
    decimal Debits,
    decimal Credits,
    decimal Balance,
    int LineCount)
{
    /// <summary>Projects a row for the wire.</summary>
    public static TrialBalanceRowResponse From(TrialBalanceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new TrialBalanceRowResponse(
            row.AccountCode,
            row.AccountName,
            row.Type.ToString(),
            row.NormalBalance.ToString(),
            row.Debits,
            row.Credits,
            row.Balance,
            row.LineCount);
    }
}

/// <summary>The trial balance, as the API returns it.</summary>
/// <param name="AsOf">The accounting date read up to, inclusive.</param>
/// <param name="Rows">Every account in the chart, in code order.</param>
/// <param name="TotalDebits">Everything debited across the ledger.</param>
/// <param name="TotalCredits">Everything credited across it.</param>
/// <param name="Difference">How far out of balance the ledger is. Zero, unless something is wrong.</param>
/// <param name="IsBalanced">The one field this report exists to produce.</param>
public sealed record TrialBalanceResponse(
    DateOnly AsOf,
    IReadOnlyList<TrialBalanceRowResponse> Rows,
    decimal TotalDebits,
    decimal TotalCredits,
    decimal Difference,
    bool IsBalanced)
{
    /// <summary>Projects a trial balance for the wire.</summary>
    public static TrialBalanceResponse From(TrialBalance trialBalance)
    {
        ArgumentNullException.ThrowIfNull(trialBalance);

        return new TrialBalanceResponse(
            trialBalance.AsOf,
            [.. trialBalance.Rows.Select(TrialBalanceRowResponse.From)],
            trialBalance.TotalDebits,
            trialBalance.TotalCredits,
            trialBalance.Difference,
            trialBalance.IsBalanced);
    }
}

/// <summary>What one service account owes, as the API returns it.</summary>
/// <param name="ServiceAccountId">The account.</param>
/// <param name="CustomerId">The customer behind it.</param>
/// <param name="Charged">Everything debited to receivables for them.</param>
/// <param name="Settled">Everything credited.</param>
/// <param name="Outstanding">What is still owed. Negative is money held on account.</param>
/// <param name="PostingCount">How many receivables lines are behind the figures.</param>
/// <param name="LastPostedOn">The accounting date of the most recent of them.</param>
public sealed record ReceivableRowResponse(
    Guid? ServiceAccountId,
    Guid? CustomerId,
    decimal Charged,
    decimal Settled,
    decimal Outstanding,
    int PostingCount,
    DateOnly LastPostedOn)
{
    /// <summary>Projects a row for the wire.</summary>
    public static ReceivableRowResponse From(ReceivableRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new ReceivableRowResponse(
            row.ServiceAccountId,
            row.CustomerId,
            row.Charged,
            row.Settled,
            row.Outstanding,
            row.PostingCount,
            row.LastPostedOn);
    }
}

/// <summary>The receivables subsidiary ledger, as the API returns it.</summary>
/// <param name="AsOf">The accounting date read up to, inclusive.</param>
/// <param name="ControlAccountCode">The receivables account these lines were read from.</param>
/// <param name="Rows">One row per service account, most owed first.</param>
/// <param name="TotalCharged">Everything charged to receivables.</param>
/// <param name="TotalSettled">Everything settled against it.</param>
/// <param name="TotalOutstanding">What the utility is owed — the control account's balance.</param>
/// <param name="Unallocated">What is owed by nobody in particular. Zero, today.</param>
public sealed record ReceivablesResponse(
    DateOnly AsOf,
    string ControlAccountCode,
    IReadOnlyList<ReceivableRowResponse> Rows,
    decimal TotalCharged,
    decimal TotalSettled,
    decimal TotalOutstanding,
    decimal Unallocated)
{
    /// <summary>Projects a receivables ledger for the wire.</summary>
    public static ReceivablesResponse From(Receivables receivables)
    {
        ArgumentNullException.ThrowIfNull(receivables);

        return new ReceivablesResponse(
            receivables.AsOf,
            receivables.ControlAccountCode,
            [.. receivables.Rows.Select(ReceivableRowResponse.From)],
            receivables.TotalCharged,
            receivables.TotalSettled,
            receivables.TotalOutstanding,
            receivables.Unallocated);
    }
}

/// <summary>The general ledger's HTTP surface. Read-only, in this work package deliberately.</summary>
public static class JournalEndpoints
{
    /// <summary>Route prefix of the Finance module.</summary>
    public const string RoutePrefix = "/api/finance";

    /// <summary>Default page size for a ledger listing.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>Maps the Finance endpoints.</summary>
    public static IEndpointRouteBuilder MapJournalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var finance = endpoints.MapGroup(RoutePrefix).WithTags("Finance");

        // The chart, so a caller can render an entry's accounts without knowing the codes. Read-only
        // and always will be: accounts are reference data shipped by migration, and adding one is a
        // migration rather than a POST (invariant 7 and 8 between them).
        finance
            .MapGet("/accounts", async (
                    [FromServices] IChartOfAccountsService chart,
                    CancellationToken cancellationToken) =>
                Results.Ok((await chart.ListAsync(cancellationToken))
                    .Select(AccountResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Finance.Read)
            .WithName("ListAccounts");

        finance
            .MapGet("/journal-entries", async (
                    string? source,
                    string? reference,
                    Guid? serviceAccountId,
                    Guid? customerId,
                    DateOnly? from,
                    DateOnly? to,
                    int? limit,
                    [FromServices] IJournalService journal,
                    CancellationToken cancellationToken) =>
                Results.Ok((await journal.ListAsync(
                        new JournalQuery(source, reference, serviceAccountId, customerId, from, to, limit ?? DefaultPageSize),
                        cancellationToken))
                    .Select(JournalEntryResponse.From)
                    .ToList()))
            .RequirePermission(Permissions.Finance.Read)
            .WithName("ListJournalEntries");

        finance
            .MapGet("/journal-entries/{id:guid}", async (
                    [FromRoute] Guid id,
                    [FromServices] IJournalService journal,
                    CancellationToken cancellationToken) =>
                await journal.FindAsync(id, cancellationToken) is { } entry
                    ? Results.Ok(JournalEntryResponse.From(entry))
                    : FinanceProblems.JournalEntryNotFound(id))
            .RequirePermission(Permissions.Finance.Read)
            .WithName("GetJournalEntry");

        finance
            .MapGet("/trial-balance", async (
                    DateOnly? asOf,
                    [FromServices] IFinanceReportService reports,
                    CancellationToken cancellationToken) =>
                Results.Ok(TrialBalanceResponse.From(await reports.TrialBalanceAsync(asOf, cancellationToken))))
            .RequirePermission(Permissions.Finance.Read)
            .WithName("GetTrialBalance");

        // The receivables subsidiary ledger. Kebab-case and plural, like every other route; the
        // accounting name rather than "debtors", because the chart calls the control account
        // Accounts receivable and a report should agree with the account it reads.
        finance
            .MapGet("/accounts-receivable", (
                    DateOnly? asOf,
                    Guid? serviceAccountId,
                    Guid? customerId,
                    bool? outstandingOnly,
                    [FromServices] IFinanceReportService reports,
                    CancellationToken cancellationToken) =>
                FinanceProblems.RunAsync(async () =>
                    Results.Ok(ReceivablesResponse.From(await reports.ReceivablesAsync(
                        new ReceivablesQuery(asOf, serviceAccountId, customerId, outstandingOnly ?? false),
                        cancellationToken)))))
            .RequirePermission(Permissions.Finance.Read)
            .WithName("GetAccountsReceivable");

        // Note what is NOT here: anything that writes. finance.post is declared, granted to the
        // Finance role and to Administrator, and opens no route at all — a manual journal entry is
        // not something SPEC.md asks for, and a ledger whose only author is the event seam is a
        // ledger that cannot disagree with the modules upstream of it. Nor is there a refund: WP-2.5
        // left payments.refund unclaimed because a refund needs a ledger to post the reversal into,
        // and building that ledger is not the same act as performing one. JournalEndpointsTests
        // asserts both permissions are still unclaimed, so the day a route demands one, that is a
        // deliberate act.
        return endpoints;
    }
}

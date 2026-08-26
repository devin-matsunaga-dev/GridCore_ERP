using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Data;
using GridCore.Modules.Customers.Features.Customers;
using GridCore.Modules.Customers.Features.Deposits;
using GridCore.Modules.Customers.Features.Profile;
using GridCore.Modules.Customers.Features.Registration;
using GridCore.Modules.Customers.Features.Shared;
using GridCore.Platform.Audit;
using GridCore.Platform.Documents;
using GridCore.Platform.Monetary;
using GridCore.Platform.Security;
using Microsoft.EntityFrameworkCore;

namespace GridCore.Modules.Customers.Features.Documents;

/// <summary>The range a caller asks a statement for. Both days are included.</summary>
/// <param name="From">First day.</param>
/// <param name="To">Last day.</param>
public sealed record StatementRange(DateOnly From, DateOnly To);

/// <summary>
/// A payment history as a file — the CSV itself, and what a browser should call it.
/// </summary>
/// <param name="FileName">What to save it as, built from the account number and the day it was run.</param>
/// <param name="Csv">The file's text, headers included.</param>
/// <param name="Rows">How many payments it holds. Zero is a valid export: a file with columns and no rows.</param>
/// <param name="ProducedAt">When it was produced.</param>
/// <param name="ProducedById">Subject id of whoever produced it.</param>
/// <param name="ProducedByName">Their display name at the time.</param>
public sealed record PaymentHistoryExport(
    string FileName,
    string Csv,
    int Rows,
    DateTimeOffset ProducedAt,
    string ProducedById,
    string? ProducedByName);

/// <summary>
/// The documents a rep hands or sends a customer (WP-2.14): an account statement, and a payment
/// history export. The bill reprint lives in Billing, which owns the figures a bill was issued with.
/// </summary>
public interface ICustomerDocumentService
{
    /// <summary>An account statement over <paramref name="range"/>, proving out from an opening balance.</summary>
    /// <exception cref="CustomerNotFoundException">There is no such customer.</exception>
    /// <exception cref="RegistryPermissionException">The caller may not produce customer documents.</exception>
    /// <exception cref="RegistryValidationException">The range runs backwards or is longer than a statement is produced for.</exception>
    Task<AccountStatement> StatementAsync(Guid customerId, StatementRange range, CancellationToken cancellationToken = default);

    /// <summary>Every payment on the account as a CSV file.</summary>
    /// <exception cref="CustomerNotFoundException">There is no such customer.</exception>
    /// <exception cref="RegistryPermissionException">The caller may not produce customer documents.</exception>
    Task<PaymentHistoryExport> ExportPaymentHistoryAsync(Guid customerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The statement and the export, composed over three registers.
/// </summary>
/// <remarks>
/// <para>
/// <b>The heaviest cross-module read in the phase, and it still touches nobody's tables.</b> Bills
/// and their corrections arrive through <see cref="IBillDirectory"/>, payments through
/// <see cref="IPaymentDirectory"/>, and the deposit ledger is this module's own. The two seams were
/// widened by this work package — each of them said in as many words that widening was a work
/// package rather than a field, and this is it.
/// </para>
/// <para>
/// <b>Reads that write one row: the audit entry.</b> Both documents leave the building, so both are
/// recorded through <see cref="IAuditLog.RecordAsync"/> — after the document is built, never before,
/// so a refused range leaves no entry claiming a statement went out. WP-2.9 is the other half of
/// this rule: a search is <i>not</i> audited, because a log of every screen somebody opened is
/// surveillance rather than a trail.
/// </para>
/// <para>
/// <b>The gate is demanded here as well as on the route</b>, per CONVENTIONS.md — a service enforces
/// its own permissions rather than trusting the endpoint that called it.
/// </para>
/// </remarks>
public sealed class CustomerDocumentService(
    CustomersDbContext database,
    ICustomerProfileService profiles,
    IBillDirectory bills,
    IPaymentDirectory payments,
    IAuditLog audit,
    ICurrentUser currentUser,
    TimeProvider clock) : ICustomerDocumentService
{
    /// <summary>
    /// The most bills, payments or deposit entries one document will read.
    /// </summary>
    /// <remarks>
    /// A whole history rather than a page, for the reason the seams give: an opening balance built
    /// from a truncated history proves out against itself and is still wrong. Reaching this cap is
    /// what <see cref="AccountStatement.IsTruncated"/> reports, so a short statement says so on its
    /// face instead of being quietly short.
    /// </remarks>
    public const int MaxHistory = 1_000;

    /// <summary>
    /// The longest range a statement is produced for. Ten years — long enough for "everything you
    /// have ever billed me", short enough that a mistyped year is refused rather than answered.
    /// </summary>
    public const int MaxRangeDays = 3_653;

    /// <inheritdoc />
    public async Task<AccountStatement> StatementAsync(
        Guid customerId,
        StatementRange range,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(range);

        RequireDocumentPermission("Producing an account statement");

        if (range.To < range.From)
        {
            throw new RegistryValidationException(
                $"A statement cannot end on {range.To:yyyy-MM-dd}, before it starts on {range.From:yyyy-MM-dd}.");
        }

        if (range.To.DayNumber - range.From.DayNumber > MaxRangeDays)
        {
            throw new RegistryValidationException(
                $"A statement covers at most {MaxRangeDays} days; {range.From:yyyy-MM-dd} to {range.To:yyyy-MM-dd} is longer. "
                + "A range that long is usually a mistyped year.");
        }

        var customer = await RequireCustomerAsync(customerId, cancellationToken).ConfigureAwait(false);

        var billed = await bills
            .ActivityForCustomerAsync(customerId, range.To, MaxHistory, cancellationToken)
            .ConfigureAwait(false);

        var taken = await payments
            .ForCustomerAsync(customerId, MaxHistory, cancellationToken)
            .ConfigureAwait(false);

        var held = await database.DepositEntries
            .AsNoTracking()
            .Where(entry => entry.CustomerId == customerId)
            .OrderBy(entry => entry.Id)
            .Take(MaxHistory)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var profile = await profiles.GetAsync(customerId, cancellationToken).ConfigureAwait(false);

        var movements = new List<StatementMovement>();

        movements.AddRange(BillMovements(billed));
        movements.AddRange(PaymentMovements(taken, range.To));
        movements.AddRange(DepositMovements(held, range.To));

        var header = new StatementHeader(
            customer.Id,
            customer.AccountNumber,
            customer.Name,
            profile.MailingAddress?.OneLine,

            // What to report in when the account has never moved. The schedule's currency rather
            // than a constant here: it is the one piece of reference data this module owns that
            // states what the utility deals in.
            DepositRules.Currency,
            clock.GetUtcNow(),
            currentUser.UserId,
            currentUser.UserName);

        var statement = AccountStatement.Compose(
            header,
            movements,
            range.From,
            range.To,

            // Any one register answering with exactly as many rows as it was asked for means its
            // history did not fit, and the opening balance may therefore be short. Reported on the
            // document rather than thrown: a statement of the last ten years is still the statement
            // the customer asked for, and refusing it outright helps nobody.
            billed.Count >= MaxHistory || taken.Count >= MaxHistory || held.Count >= MaxHistory);

        await audit
            .RecordAsync(
                AuditActions.CustomerStatementProduced,
                AuditEntityTypes.CustomerDocument,
                customer.Id.ToString(),
                before: null,
                after: StatementSnapshot.Of(statement),
                cancellationToken)
            .ConfigureAwait(false);

        return statement;
    }

    /// <inheritdoc />
    public async Task<PaymentHistoryExport> ExportPaymentHistoryAsync(
        Guid customerId,
        CancellationToken cancellationToken = default)
    {
        RequireDocumentPermission("Exporting a payment history");

        var customer = await RequireCustomerAsync(customerId, cancellationToken).ConfigureAwait(false);

        var taken = await payments.ForCustomerAsync(customerId, MaxHistory, cancellationToken).ConfigureAwait(false);

        // The premise each payment's account supplies, so a customer with three connections can tell
        // which one a payment was for. Two queries over this module's own tables rather than a join:
        // a customer holds a handful of accounts, and the alternative is a payment carrying an
        // address across a module boundary it has no business crossing.
        var accounts = await database.ServiceAccounts
            .AsNoTracking()
            .Where(account => account.CustomerId == customerId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var locationIds = accounts.ConvertAll(account => account.ServiceLocationId);

        var locations = await database.ServiceLocations
            .AsNoTracking()
            .Where(location => locationIds.Contains(location.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var addressByAccountId = accounts
            .Select(account => (
                account.Id,
                Address: locations.FirstOrDefault(location => location.Id == account.ServiceLocationId)?.Address.OneLine))
            .Where(pair => pair.Address is not null)
            .ToDictionary(pair => pair.Id, pair => pair.Address!);

        var accountNumberById = accounts.ToDictionary(account => account.Id, account => account.AccountNumber);

        // The bill numbers, through the seam, in one batched call. A row that named a Guid would be
        // a row nobody can match against the bill in their filing cabinet.
        var billed = await bills
            .FindManyAsync([.. taken.Select(payment => payment.BillId).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        var export = PaymentHistoryCsv.Write(
            customer.AccountNumber,
            customer.Name,
            taken,
            accountNumberById,
            addressByAccountId,
            billed.ToDictionary(pair => pair.Key, pair => pair.Value.BillNumber),
            clock.GetUtcNow(),
            currentUser.UserId,
            currentUser.UserName);

        await audit
            .RecordAsync(
                AuditActions.CustomerPaymentHistoryExported,
                AuditEntityTypes.CustomerDocument,
                customer.Id.ToString(),
                before: null,
                after: new PaymentHistorySnapshot(
                    customer.AccountNumber,
                    customer.Name,
                    export.FileName,
                    export.Rows,
                    Money.Total(taken.Where(payment => payment.IsSettled).Select(payment => payment.Amount))),
                cancellationToken)
            .ConfigureAwait(false);

        return export;
    }

    /// <summary>
    /// A bill's own movements: it was issued, it may have been corrected, it may have been withdrawn.
    /// </summary>
    /// <remarks>
    /// <b>What a withdrawal takes back is the balance, not the printed total.</b> A bill cancelled
    /// after part of it was paid only ever owed the remainder, and reversing the whole charge would
    /// leave the statement claiming the utility owes the customer money it kept. Payments cannot
    /// arrive after a cancellation — a cancelled bill is not outstanding — so the amount paid on it
    /// today is the amount paid on it then.
    /// </remarks>
    private static IEnumerable<StatementMovement> BillMovements(IReadOnlyList<BillActivity> billed)
    {
        foreach (var bill in billed)
        {
            yield return new StatementMovement(
                bill.IssuedOn,

                // Midnight, because a bill has an issue date and not a time. It sorts ahead of
                // everything else that day, which is the truth: nothing can be done about a bill
                // before it exists.
                new DateTimeOffset(bill.IssuedOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                StatementEntryKind.BillIssued,
                $"Bill {bill.BillNumber} for {bill.PeriodStart:d MMM yyyy} to {bill.PeriodEnd:d MMM yyyy}",
                bill.BillNumber,
                bill.TotalAmount,
                Money.Zero,
                bill.Currency,
                BillId: bill.Id,
                ServiceAccountId: bill.ServiceAccountId,
                AccountNumber: bill.AccountNumber);

            foreach (var correction in bill.Corrections)
            {
                yield return new StatementMovement(
                    DateOnly.FromDateTime(correction.RecordedAt.UtcDateTime),
                    correction.RecordedAt,
                    StatementEntryKind.BillCorrected,
                    $"{correction.Kind} on bill {bill.BillNumber}: {correction.Reason}",
                    bill.BillNumber,
                    correction.Amount,
                    Money.Zero,
                    bill.Currency,
                    BillId: bill.Id,
                    ServiceAccountId: bill.ServiceAccountId,
                    AccountNumber: bill.AccountNumber);
            }

            if (bill.WithdrawnAt is { } withdrawnAt)
            {
                yield return new StatementMovement(
                    DateOnly.FromDateTime(withdrawnAt.UtcDateTime),
                    withdrawnAt,
                    StatementEntryKind.BillWithdrawn,
                    $"Bill {bill.BillNumber} withdrawn",
                    bill.BillNumber,
                    -(bill.TotalAmount + bill.AdjustmentTotal - bill.AmountPaid),
                    Money.Zero,
                    bill.Currency,
                    BillId: bill.Id,
                    ServiceAccountId: bill.ServiceAccountId,
                    AccountNumber: bill.AccountNumber);
            }
        }
    }

    /// <summary>
    /// The payments that moved money, on the day they moved it.
    /// </summary>
    /// <remarks>
    /// <b>Settled only.</b> A declined card is a real event and it belongs on the payment-history
    /// export, where "why does this customer still owe money" is answered by the run of refusals —
    /// but it moved nothing, and a statement that credited it would not add up. Refunds are not a
    /// case yet: WP-2.5 left <c>Refunded</c> as an outcome the seam carries and nothing more, so no
    /// payment reaches this module in that state.
    /// </remarks>
    private static IEnumerable<StatementMovement> PaymentMovements(IReadOnlyList<PaymentSummary> taken, DateOnly to)
    {
        foreach (var payment in taken.Where(payment => payment.IsSettled))
        {
            // The day the money landed: for a payment that settled, the day the provider approved
            // it. The fallback to the request date is what keeps a statement dated rather than
            // dateless if an approval ever arrives without an answer time.
            var settled = payment.AnsweredAt ?? payment.RequestedAt;
            var date = DateOnly.FromDateTime(settled.UtcDateTime);

            // The bill window is cut off at `to` by the query; the payment history is not, because
            // it has no date to cut on that a database index would use. Cut here instead — a
            // statement handed a movement after its last day refuses to compose.
            if (date > to)
            {
                continue;
            }

            yield return new StatementMovement(
                date,
                settled,
                StatementEntryKind.PaymentReceived,
                $"Payment {payment.PaymentNumber} received",
                payment.PaymentNumber,
                -payment.Amount,
                Money.Zero,
                payment.Currency,
                PaymentId: payment.Id,
                BillId: payment.BillId,
                ServiceAccountId: payment.ServiceAccountId);
        }
    }

    /// <summary>
    /// The deposit ledger's movements — in the deposit column, and only an application in both.
    /// </summary>
    private static IEnumerable<StatementMovement> DepositMovements(IReadOnlyList<DepositEntry> held, DateOnly to)
    {
        foreach (var entry in held)
        {
            var date = DateOnly.FromDateTime(entry.RecordedAt.UtcDateTime);

            if (date > to)
            {
                continue;
            }

            var kind = entry.Kind switch
            {
                DepositEntryKind.Collected => StatementEntryKind.DepositCollected,
                DepositEntryKind.Applied => StatementEntryKind.DepositApplied,
                DepositEntryKind.Refunded => StatementEntryKind.DepositRefunded,
                _ => throw new RegistryValidationException(
                    $"A statement does not know what a '{entry.Kind}' deposit movement does to a balance."),
            };

            yield return new StatementMovement(
                date,
                entry.RecordedAt,
                kind,
                Describe(entry),
                entry.BillNumber,

                // ONLY an application moves what is owed. A collection is a liability the utility
                // takes on and a refund is that liability discharged; neither settles a bill, and a
                // statement that put them in the balance column would tell a customer their deposit
                // had paid for something.
                kind is StatementEntryKind.DepositApplied ? entry.SignedAmount : Money.Zero,
                entry.SignedAmount,
                entry.Currency,
                BillId: entry.BillId,
                DepositEntryId: entry.Id,
                ServiceAccountId: entry.ServiceAccountId);
        }
    }

    private static string Describe(DepositEntry entry) =>
        entry.Kind switch
        {
            DepositEntryKind.Collected => "Security deposit received",
            DepositEntryKind.Applied when entry.BillNumber is { } number => $"Deposit applied to bill {number}",
            DepositEntryKind.Applied => "Deposit applied to a bill",
            _ => "Security deposit refunded",
        };

    private async Task<Customer> RequireCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        await database.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == customerId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new CustomerNotFoundException(customerId);

    private void RequireDocumentPermission(string act)
    {
        if (currentUser.HasPermission(Permissions.Customers.Documents))
        {
            return;
        }

        throw new RegistryPermissionException(
            $"{act} requires the '{Permissions.Customers.Documents}' permission. "
            + "Reading an account on screen and sending a document out under the utility's name are different acts.");
    }
}

/// <summary>
/// The shape a statement is audited as: which account, over what range, and what it said.
/// </summary>
/// <remarks>
/// The figures are here so the entry answers on its own, years later, what the customer was told —
/// following the customer id to a balance that has moved a hundred times since does not.
/// </remarks>
/// <param name="AccountNumber">The account the statement is for.</param>
/// <param name="CustomerName">Their name at the time.</param>
/// <param name="From">First day of the range.</param>
/// <param name="To">Last day of it.</param>
/// <param name="Currency">What the figures are expressed in.</param>
/// <param name="OpeningBalance">What it opened at.</param>
/// <param name="ClosingBalance">What it closed at.</param>
/// <param name="Lines">How many movements it showed.</param>
/// <param name="IsTruncated">Whether a register's history did not fit, so the opening balance may be short.</param>
public sealed record StatementSnapshot(
    string AccountNumber,
    string CustomerName,
    DateOnly From,
    DateOnly To,
    string Currency,
    decimal OpeningBalance,
    decimal ClosingBalance,
    int Lines,
    bool IsTruncated)
{
    /// <summary>Takes a snapshot of the statement that was produced.</summary>
    public static StatementSnapshot Of(AccountStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return new StatementSnapshot(
            statement.AccountNumber,
            statement.CustomerName,
            statement.From,
            statement.To,
            statement.Currency,
            statement.OpeningBalance,
            statement.ClosingBalance,
            statement.Entries.Count,
            statement.IsTruncated);
    }
}

/// <summary>The shape a payment-history export is audited as.</summary>
/// <param name="AccountNumber">The account exported.</param>
/// <param name="CustomerName">Their name at the time.</param>
/// <param name="FileName">What the file was called — what a recipient would quote.</param>
/// <param name="Rows">How many payments it held.</param>
/// <param name="SettledTotal">What the settled ones come to.</param>
public sealed record PaymentHistorySnapshot(
    string AccountNumber,
    string CustomerName,
    string FileName,
    int Rows,
    decimal SettledTotal);

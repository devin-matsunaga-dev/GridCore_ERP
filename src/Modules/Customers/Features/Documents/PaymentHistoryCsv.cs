using System.Globalization;
using GridCore.Contracts.Directories;
using GridCore.Platform.Documents;

namespace GridCore.Modules.Customers.Features.Documents;

/// <summary>
/// A customer's payment history as a CSV file (WP-2.14).
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure, and that is why the escaping is testable.</b> Nothing here reads a database or a clock;
/// it takes what the registers answered and returns text. The rules worth arguing with — what a
/// column means, how a refused attempt is shown, what a file is called — are argued with in
/// milliseconds.
/// </para>
/// <para>
/// <b>Refused attempts are in the file.</b> A statement credits only what settled, because it has to
/// add up; an export is a history, and the run of declines on a card is exactly what somebody opens
/// it to find. "Money received" is the column that tells the two apart, and it is Payments' own rule
/// carried across the seam rather than one restated here.
/// </para>
/// <para>
/// <b>Dates are ISO, amounts are invariant.</b> A CSV crosses machines with different locales, and
/// <c>03/04/2026</c> means two different days on two desks. Amounts are written with a point and no
/// thousands separator for the same reason — a spreadsheet has to be able to add the column up.
/// </para>
/// </remarks>
public static class PaymentHistoryCsv
{
    /// <summary>The columns, in order. The header row of every export.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "Payment number",
        "Date",
        "Status",
        "Money received",
        "Method",
        "Amount",
        "Currency",
        "Bill number",
        "Account number",
        "Service address",
        "Customer",
    ];

    /// <summary>
    /// Writes <paramref name="payments"/> as a file.
    /// </summary>
    /// <param name="accountNumber">The customer's own number, which names the file.</param>
    /// <param name="customerName">Their name, as it stands today.</param>
    /// <param name="payments">Every payment on the account, oldest first — attempts included.</param>
    /// <param name="accountNumberByAccountId">Each service account's number, for the account column.</param>
    /// <param name="serviceAddressByAccountId">Each service account's premise, on one line.</param>
    /// <param name="billNumberByBillId">Each bill's number, so a row names the document it settled.</param>
    /// <param name="producedAt">When the export was produced. Dates the file and names it.</param>
    /// <param name="producedById">Subject id of whoever produced it.</param>
    /// <param name="producedByName">Their display name at the time.</param>
    public static PaymentHistoryExport Write(
        string accountNumber,
        string customerName,
        IReadOnlyList<PaymentSummary> payments,
        IReadOnlyDictionary<Guid, string> accountNumberByAccountId,
        IReadOnlyDictionary<Guid, string> serviceAddressByAccountId,
        IReadOnlyDictionary<Guid, string> billNumberByBillId,
        DateTimeOffset producedAt,
        string producedById,
        string? producedByName)
    {
        ArgumentNullException.ThrowIfNull(payments);
        ArgumentNullException.ThrowIfNull(accountNumberByAccountId);
        ArgumentNullException.ThrowIfNull(serviceAddressByAccountId);
        ArgumentNullException.ThrowIfNull(billNumberByBillId);

        var rows = payments
            .Select(payment => (IReadOnlyList<string?>)
            [
                payment.PaymentNumber,
                Date(payment),
                payment.Status,
                payment.IsSettled ? "Yes" : "No",
                payment.Method,
                payment.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                payment.Currency,

                // Absent rather than a raw id where a number cannot be resolved. A customer reading
                // the file has no use for a Guid, and an empty cell says "not known here" without
                // pretending otherwise.
                billNumberByBillId.GetValueOrDefault(payment.BillId),
                accountNumberByAccountId.GetValueOrDefault(payment.ServiceAccountId),
                serviceAddressByAccountId.GetValueOrDefault(payment.ServiceAccountId),
                customerName,
            ])
            .ToList();

        return new PaymentHistoryExport(
            FileNameFor(accountNumber, DateOnly.FromDateTime(producedAt.UtcDateTime)),
            CsvDocument.Write(Columns, rows),
            rows.Count,
            producedAt,
            producedById,
            producedByName);
    }

    /// <summary>
    /// What the file is called: the account number and the day it was run.
    /// </summary>
    /// <remarks>
    /// Every character outside letters, digits and a dash is replaced, because this ends up in a
    /// <c>Content-Disposition</c> header and in a folder on somebody's desktop — an account number
    /// is <c>C-000123</c> today, and a utility that later puts a slash in one should not be able to
    /// break a download by doing so.
    /// </remarks>
    public static string FileNameFor(string accountNumber, DateOnly producedOn)
    {
        var safe = new string([.. (accountNumber ?? string.Empty)
            .Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' ? character : '-')]);

        if (safe.Length is 0)
        {
            safe = "account";
        }

        return $"payment-history-{safe}-{producedOn:yyyy-MM-dd}.csv";
    }

    /// <summary>
    /// The day the row is dated: when the provider answered, or when it was attempted if it never
    /// answered at all.
    /// </summary>
    /// <remarks>
    /// The answer date whatever the answer, which is what a customer looking for "the day my card
    /// was refused" is after. It is the day the money landed only where <c>Money received</c> says
    /// Yes — the column beside it is what keeps the two from being read as one.
    /// </remarks>
    private static string Date(PaymentSummary payment) =>
        (payment.AnsweredAt ?? payment.RequestedAt).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

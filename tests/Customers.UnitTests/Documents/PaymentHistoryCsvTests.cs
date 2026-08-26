using GridCore.Contracts.Directories;
using GridCore.Modules.Customers.Features.Documents;

namespace GridCore.Modules.Customers.UnitTests.Documents;

/// <summary>
/// The payment-history export (WP-2.14), pure and without a database.
/// </summary>
/// <remarks>
/// What is under test is what the file <i>says</i>: refused attempts are in it, every row is dated,
/// and a name or an address with a comma in it stays in its own column. The escaping itself belongs
/// to <c>CsvDocument</c> and is pinned there; this is about what reaches it.
/// </remarks>
public class PaymentHistoryCsvTests
{
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly Guid Account = Guid.CreateVersion7();
    private static readonly Guid BillId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Produced = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    private static PaymentSummary APayment(
        string number = "PAY-000001",
        decimal amount = 120.00m,
        string status = "Approved",
        string method = "card",
        DateTimeOffset? requestedAt = null,
        DateTimeOffset? answeredAt = null) =>
        new(
            Guid.CreateVersion7(),
            number,
            Customer,
            Account,
            BillId,
            amount,
            "USD",
            status,
            IsSettled: status is "Approved",
            method,
            requestedAt ?? new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero),
            answeredAt);

    private static PaymentHistoryExport Write(
        IReadOnlyList<PaymentSummary> payments,
        string customerName = "Ana Cruz",
        string address = "12 Beach Road, Songsong, Rota") =>
        PaymentHistoryCsv.Write(
            "C-000001",
            customerName,
            payments,
            new Dictionary<Guid, string> { [Account] = "A-000001" },
            new Dictionary<Guid, string> { [Account] = address },
            new Dictionary<Guid, string> { [BillId] = "BIL-000001" },
            Produced,
            "auth0|clerk",
            "Bea Santos");

    private static string[] Rows(string csv) =>
        [.. csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)];

    [Fact]
    public void The_file_opens_with_the_columns()
    {
        var export = Write([]);

        Assert.Equal(string.Join(',', PaymentHistoryCsv.Columns), Rows(export.Csv)[0]);
    }

    [Fact]
    public void A_customer_who_has_never_paid_gets_a_file_with_columns_and_no_rows()
    {
        // An answer, not a failure. A zero-byte file reads as a broken download; a header row reads
        // as "there is nothing here", which is the truth.
        var export = Write([]);

        Assert.Equal(0, export.Rows);
        Assert.Single(Rows(export.Csv));
    }

    [Fact]
    public void A_settled_payment_is_written_with_everything_a_customer_can_check()
    {
        var answered = new DateTimeOffset(2026, 7, 21, 8, 15, 0, TimeSpan.Zero);

        var export = Write([APayment(answeredAt: answered)]);

        var row = Rows(export.Csv)[1];

        Assert.StartsWith("PAY-000001,2026-07-21,Approved,Yes,card,120.00,USD,BIL-000001,A-000001,", row, StringComparison.Ordinal);
        Assert.EndsWith("Ana Cruz", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_REFUSED_attempt_is_in_the_file_dated_by_when_it_was_TRIED()
    {
        // The export and the statement part company here, deliberately. A statement credits only
        // what settled, because it has to add up; an export is a history, and the run of declines on
        // a card is exactly what somebody opens the file to find.
        var export = Write([APayment(status: "Declined", requestedAt: new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeSpan.Zero))]);

        var row = Rows(export.Csv)[1];

        Assert.Contains("2026-07-20", row, StringComparison.Ordinal);
        Assert.Contains("Declined,No", row, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_with_a_comma_and_a_quote_in_it_stays_in_one_column()
    {
        // WP-2.14's escaping requirement, in the shape it actually arrives in. Getting this wrong
        // does not throw — it silently shifts every column after the name.
        var export = Write([APayment()], customerName: "Cruz, Ana \"Anita\"");

        var row = Rows(export.Csv)[1];

        Assert.EndsWith("\"Cruz, Ana \"\"Anita\"\"\"", row, StringComparison.Ordinal);

        // Eleven columns, still: the quoting is what keeps the count right.
        Assert.Equal(PaymentHistoryCsv.Columns.Count, CountFields(row));
    }

    [Fact]
    public void An_address_with_commas_in_it_stays_in_one_column()
    {
        var export = Write([APayment()], address: "Unit 4, 12 Beach Road, Songsong, Rota");

        var row = Rows(export.Csv)[1];

        Assert.Contains("\"Unit 4, 12 Beach Road, Songsong, Rota\"", row, StringComparison.Ordinal);
        Assert.Equal(PaymentHistoryCsv.Columns.Count, CountFields(row));
    }

    [Fact]
    public void An_address_a_spreadsheet_would_run_as_a_formula_is_guarded()
    {
        var export = Write([APayment()], address: "=HYPERLINK(\"http://elsewhere\")");

        Assert.Contains("'=HYPERLINK", export.Csv, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_is_named_after_the_account_and_the_day_it_was_run()
    {
        var export = Write([]);

        Assert.Equal("payment-history-C-000001-2026-08-26.csv", export.FileName);
    }

    [Theory]
    [InlineData("C/000123", "payment-history-C-000123-2026-08-26.csv")]
    [InlineData("../etc", "payment-history----etc-2026-08-26.csv")]
    [InlineData("", "payment-history-account-2026-08-26.csv")]
    public void A_file_name_carries_nothing_a_path_could_read(string accountNumber, string expected) =>
        // This ends up in a Content-Disposition header and in a folder on somebody's desktop. An
        // account number is C-000123 today; a utility that later puts a slash in one must not be
        // able to break a download by doing so.
        Assert.Equal(expected, PaymentHistoryCsv.FileNameFor(accountNumber, new DateOnly(2026, 8, 26)));

    /// <summary>Counts the fields of one CSV row, respecting quotes.</summary>
    private static int CountFields(string row)
    {
        var fields = 1;
        var quoted = false;

        for (var index = 0; index < row.Length; index++)
        {
            if (row[index] is '"')
            {
                quoted = !quoted;
            }
            else if (row[index] is ',' && !quoted)
            {
                fields++;
            }
        }

        return fields;
    }
}

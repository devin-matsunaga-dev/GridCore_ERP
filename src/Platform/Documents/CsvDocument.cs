using System.Globalization;
using System.Text;

namespace GridCore.Platform.Documents;

/// <summary>
/// The one place GridCore writes CSV. WP-2.14's payment-history export is the first caller; every
/// later export goes through here rather than joining strings with commas.
/// </summary>
/// <remarks>
/// <para>
/// <b>RFC 4180, and the rule that matters is quoting.</b> A field containing a comma, a quote, a
/// newline or leading whitespace is wrapped in quotes and its own quotes are doubled. A customer
/// called <c>Cruz, Ana "Anita"</c> is not an exotic case — it is Tuesday — and an export that split
/// her into two columns is one somebody reconciles by hand and then stops trusting.
/// </para>
/// <para>
/// <b>The formula guard is the other half, and it is the half nobody remembers.</b> A spreadsheet
/// treats a cell beginning <c>=</c>, <c>+</c>, <c>@</c> or a tab as a formula, so a field a customer
/// typed can become code the moment a clerk opens the file. Those are prefixed with an apostrophe,
/// which every spreadsheet reads as "this is text". A leading <c>-</c> is guarded too <i>unless the
/// field is a number</i>: a credit of <c>-45.00</c> must stay a number a spreadsheet can add up, and
/// mangling it to protect against <c>-2+3</c> would break every arithmetic column in the file.
/// </para>
/// <para>
/// <b>CRLF line endings and a UTF-8 BOM are deliberate.</b> Both are what a spreadsheet on a
/// clerk's desk expects; a LF-terminated file without a BOM is the one that opens with accented
/// place names mangled and every row on one line. <see cref="Write"/> returns the text and
/// <see cref="Preamble"/> is the BOM an endpoint puts in front of it.
/// </para>
/// </remarks>
public static class CsvDocument
{
    /// <summary>The media type an export is served as.</summary>
    public const string ContentType = "text/csv";

    /// <summary>Row separator. CRLF, as RFC 4180 asks and as a spreadsheet expects.</summary>
    public const string LineBreak = "\r\n";

    /// <summary>
    /// The byte-order mark to put in front of the text, so a spreadsheet reads it as UTF-8 rather
    /// than guessing at the local code page.
    /// </summary>
    public static ReadOnlySpan<byte> Preamble => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Writes <paramref name="rows"/> under <paramref name="headers"/>, escaping every field.
    /// </summary>
    /// <param name="headers">The header row. Written even when there are no rows — an empty export is a file with columns, not an empty file.</param>
    /// <param name="rows">The data rows, each of which must have as many fields as there are headers.</param>
    /// <exception cref="ArgumentException">A row has a different number of fields from the header.</exception>
    public static string Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        var text = new StringBuilder();

        AppendRow(text, headers);

        foreach (var row in rows)
        {
            // Refused rather than padded. A short row is a caller that added a column to the header
            // and forgot one of the projections, and a file that silently shifted every later field
            // one column left is the kind of wrong that is found weeks later in a reconciliation.
            if (row.Count != headers.Count)
            {
                throw new ArgumentException(
                    $"A CSV row has {row.Count} fields but the header has {headers.Count}.",
                    nameof(rows));
            }

            AppendRow(text, row);
        }

        return text.ToString();
    }

    /// <summary>
    /// One field, escaped and guarded — what <see cref="Write"/> puts through every cell.
    /// </summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var guarded = NeedsFormulaGuard(value) ? "'" + value : value;

        return NeedsQuoting(guarded)
            ? '"' + guarded.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : guarded;
    }

    private static void AppendRow(StringBuilder text, IReadOnlyList<string?> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                text.Append(',');
            }

            text.Append(Field(fields[index]));
        }

        text.Append(LineBreak);
    }

    private static bool NeedsQuoting(string value) =>
        value.AsSpan().IndexOfAny(",\"\r\n") >= 0
        || char.IsWhiteSpace(value[0])
        || char.IsWhiteSpace(value[^1]);

    /// <summary>
    /// Whether a spreadsheet would read <paramref name="value"/> as a formula rather than as text.
    /// </summary>
    /// <remarks>
    /// A leading minus is the exception: <c>-45.00</c> is a credit and has to stay a number, so it is
    /// guarded only when it is not one. Everything else on this list is a character no legitimate
    /// name, address or reference in GridCore starts with.
    /// </remarks>
    private static bool NeedsFormulaGuard(string value) =>
        value[0] switch
        {
            '=' or '+' or '@' or '\t' or '\r' => true,
            '-' => !decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _),
            _ => false,
        };
}

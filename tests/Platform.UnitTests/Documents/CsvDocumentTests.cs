using GridCore.Platform.Documents;

namespace GridCore.Platform.UnitTests.Documents;

/// <summary>
/// The one place GridCore writes CSV (WP-2.14). What is under test is the escaping: a field that
/// splits a row in two, and a field a spreadsheet would run as code.
/// </summary>
public class CsvDocumentTests
{
    [Fact]
    public void A_plain_field_is_written_as_it_stands() =>
        Assert.Equal("Ana Cruz", CsvDocument.Field("Ana Cruz"));

    [Fact]
    public void Nothing_is_written_for_a_missing_field() =>
        // Not "null", not a space. An empty cell is what a spreadsheet reads as "not known".
        Assert.Equal(string.Empty, CsvDocument.Field(null));

    [Theory]
    [InlineData("Cruz, Ana", "\"Cruz, Ana\"")]
    [InlineData("Ana \"Anita\" Cruz", "\"Ana \"\"Anita\"\" Cruz\"")]
    [InlineData("12 Beach Road\nRota", "\"12 Beach Road\nRota\"")]
    [InlineData(" leading space", "\" leading space\"")]
    [InlineData("trailing space ", "\"trailing space \"")]
    public void A_field_that_would_break_a_row_is_quoted(string value, string expected) =>
        // The four cases that split one row into two or one column into two. A name with a comma in
        // it is not exotic — it is how half the world writes a name.
        Assert.Equal(expected, CsvDocument.Field(value));

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+34 555 1234")]
    [InlineData("@handle")]
    public void A_field_a_spreadsheet_would_run_is_guarded(string value)
    {
        var written = CsvDocument.Field(value);

        // Prefixed with an apostrophe, which every spreadsheet reads as "this cell is text". Without
        // it, a field a customer typed becomes a formula the moment a clerk opens the file.
        Assert.StartsWith("'", written, StringComparison.Ordinal);
        Assert.Contains(value, written, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-45.00")]
    [InlineData("-1")]
    [InlineData("-0.01")]
    public void A_negative_NUMBER_is_left_alone(string value) =>
        // The exception that makes the guard usable. A credit has to stay a number a spreadsheet can
        // add up; guarding every leading minus would break every arithmetic column in the file.
        Assert.Equal(value, CsvDocument.Field(value));

    [Fact]
    public void A_leading_minus_that_is_not_a_number_is_still_guarded() =>
        Assert.Equal("'-2+3", CsvDocument.Field("-2+3"));

    [Fact]
    public void An_export_with_no_rows_is_a_file_with_columns() =>
        // Not an empty file, and not a 204. A customer who has never paid gets a header row, which
        // is an answer; a zero-byte file reads as a failed download.
        Assert.Equal("Number,Amount\r\n", CsvDocument.Write(["Number", "Amount"], []));

    [Fact]
    public void Rows_are_written_under_the_header_and_separated_by_CRLF()
    {
        var csv = CsvDocument.Write(
            ["Number", "Customer"],
            [["PAY-000001", "Cruz, Ana"], ["PAY-000002", "Ana \"Anita\" Cruz"]]);

        Assert.Equal(
            "Number,Customer\r\n"
            + "PAY-000001,\"Cruz, Ana\"\r\n"
            + "PAY-000002,\"Ana \"\"Anita\"\" Cruz\"\r\n",
            csv);
    }

    [Fact]
    public void A_row_that_does_not_fit_the_header_is_refused()
    {
        // THE FAILURE PATH. A short row is a caller that added a column and forgot a projection, and
        // a file that quietly shifted every later field one column left is found weeks later in a
        // reconciliation — if at all.
        var error = Assert.Throws<ArgumentException>(() =>
            CsvDocument.Write(["Number", "Amount", "Currency"], [["PAY-000001", "12.00"]]));

        Assert.Contains("2 fields", error.Message, StringComparison.Ordinal);
        Assert.Contains("header has 3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preamble_is_the_UTF8_byte_order_mark() =>
        // What makes a spreadsheet read the file as UTF-8 rather than guessing at the local code
        // page and mangling every accented place name in the address column.
        Assert.Equal<byte[]>([0xEF, 0xBB, 0xBF], CsvDocument.Preamble.ToArray());
}

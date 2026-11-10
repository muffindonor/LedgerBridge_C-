using LedgerBridge.Cobol;
using LedgerBridge.Core.Batch;

namespace LedgerBridge.Tests;

public class CopybookParserTests
{
    [Fact]
    public void Real_copybooks_have_their_documented_lengths()
    {
        Assert.Equal(59, BatchLayouts.Master.Layout.Length);
        Assert.Equal(52, BatchLayouts.PackedMaster.Layout.Length);
        Assert.Equal(38, BatchLayouts.Transaction.Layout.Length);
        Assert.Equal(72, BatchLayouts.Reject.Layout.Length);
    }

    [Fact]
    public void Master_fields_land_where_COBOL_puts_them()
    {
        MasterFields master = BatchLayouts.Master;
        Assert.Equal(37, master.Balance.Offset);
        Assert.Equal(14, master.Balance.Length);
        Assert.Equal(FieldKind.DisplayLeadingSeparate, master.Balance.Kind);
        Assert.Equal(2, master.Balance.Scale);
        Assert.Equal(51, master.LastTxnDate.Offset);
    }

    [Fact]
    public void Group_field_spans_its_children()
    {
        TransactionFields txn = BatchLayouts.Transaction;
        Assert.Equal(0, txn.Key.Offset);
        Assert.Equal(16, txn.Key.Length);
        Assert.Equal(16, txn.Code.Offset);
    }

    [Fact]
    public void Packed_field_size_comes_from_its_digit_count()
    {
        Field balance = BatchLayouts.PackedMaster.Balance;
        Assert.Equal(FieldKind.Packed, balance.Kind);
        Assert.Equal(13, balance.Digits);
        Assert.Equal(7, balance.Length);
    }

    [Fact]
    public void Comments_sequence_numbers_and_the_identification_area_are_ignored()
    {
        string source = string.Join(
            '\n',
            "000100* A COMMENT WITH A PERIOD.",
            "000200 01  R.".PadRight(72) + "IGNOREME",
            "000300     05  A   PIC X(02).".PadRight(72) + "IGNOREME",
            "000400     05  FILLER PIC X(03).",
            "000500     05  B   PIC 9(02).");
        RecordLayout layout = CopybookParser.Parse(source);
        Assert.Equal(7, layout.Length);
        Assert.Equal(5, layout["B"].Offset);
        Assert.Equal(2, layout.Fields.Count);
    }

    [Fact]
    public void A_statement_can_span_lines_and_88_levels_take_no_space()
    {
        RecordLayout layout = CopybookParser.Parse(
            """
                   01  R.
                       05  A   PIC S9(3)V99
                               SIGN IS TRAILING SEPARATE CHARACTER.
                           88  A-VALID VALUE "1.5".
                       05  B   PIC X.
            """);
        Assert.Equal(7, layout.Length);
        Assert.Equal(FieldKind.DisplayTrailingSeparate, layout["A"].Kind);
        Assert.Equal(6, layout["B"].Offset);
    }

    [Theory]
    [InlineData("05  A   PIC X(2) REDEFINES B.", "not supported")]
    [InlineData("05  A   PIC X(2) OCCURS 3 TIMES.", "not supported")]
    [InlineData("05  A   PIC 9(19).", "more than 18")]
    [InlineData("05  A   PIC ZZ9.", "not supported")]
    [InlineData("05  A   PIC X(2) COMP-3.", "numeric picture")]
    [InlineData("05  A   PIC S9(2) SIGN IS LEADING.", "not supported")]
    [InlineData("05  A   PIC 9(2) SIGN IS LEADING SEPARATE.", "unsigned")]
    public void Unsupported_definitions_fail_loudly(string item, string message)
    {
        string source = "       01  R.\n           " + item;
        CopybookException error = Assert.Throws<CopybookException>(() => CopybookParser.Parse(source));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void An_elementary_item_with_children_is_an_error()
    {
        Assert.Throws<CopybookException>(() => CopybookParser.Parse(
            "       01  R.\n           05  A   PIC X.\n               10  B   PIC X."));
    }

    [Fact]
    public void Missing_terminator_is_an_error()
    {
        Assert.Throws<CopybookException>(() => CopybookParser.Parse("       01  R.\n           05 A PIC X"));
    }

    [Fact]
    public void Unknown_field_lookup_names_the_record()
    {
        KeyNotFoundException error = Assert.Throws<KeyNotFoundException>(() => BatchLayouts.Master.Layout["NOPE"]);
        Assert.Contains("ACCT-REC", error.Message, StringComparison.Ordinal);
    }
}

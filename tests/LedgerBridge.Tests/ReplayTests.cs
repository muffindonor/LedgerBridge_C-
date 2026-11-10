using System.Text;
using LedgerBridge.Core.Batch;

namespace LedgerBridge.Tests;

/// <summary>The replay engine alone, against real COBOL output, with no database.</summary>
public class ReplayTests
{
    private sealed class Recorder : IReplaySink
    {
        public List<BatchBreak> Breaks { get; } = [];

        public List<string> Accounts { get; } = [];

        public List<(EntryKind Kind, string Key, long Amount)> Entries { get; } = [];

        public List<string> Rejects { get; } = [];

        public List<string> Groups { get; } = [];

        public void Entry(EntryKind kind, ReadOnlySpan<byte> accountId, ReadOnlySpan<byte> entryKey, long amountCents, int txnDate) =>
            Entries.Add((kind, Encoding.ASCII.GetString(entryKey), amountCents));

        public void Reject(ReadOnlySpan<byte> rejectRecord) => Rejects.Add(Encoding.ASCII.GetString(rejectRecord));

        public void Account(in AccountResult account) => Accounts.Add(Encoding.ASCII.GetString(account.Id));

        public void GroupEnd(ReadOnlySpan<byte> key) => Groups.Add(Encoding.ASCII.GetString(key));

        public void Break(BatchBreak failure) => Breaks.Add(failure);
    }

    [Theory]
    [InlineData("day1")]
    [InlineData("day2")]
    [InlineData("day2-packed")]
    [InlineData("medium")]
    public void Replay_of_a_real_COBOL_run_agrees_with_its_control_totals(string fixture)
    {
        var files = new BatchFiles(TestPaths.Batch(fixture));
        var recorder = new Recorder();
        BatchFigures figures = new BatchReplay(files, recorder).Run(null, CancellationToken.None);

        Assert.Empty(recorder.Breaks);
        Assert.Empty(ReportCheck.Compare(figures, ControlReport.Parse(files.Report)));
    }

    [Fact]
    public void Day2_interest_is_recomputed_exactly()
    {
        var recorder = new Recorder();
        new BatchReplay(new BatchFiles(TestPaths.Batch("day2")), recorder).Run(null, CancellationToken.None);

        // 9,030.00 * 0.0025 = 22.575, which rounds up to 22.58. 8,020.50 * 0.0025 = 20.05125.
        Assert.Contains(recorder.Entries, e => e is { Kind: EntryKind.Interest, Key: "1000000002000001", Amount: 2258 });
        Assert.Contains(recorder.Entries, e => e is { Kind: EntryKind.Interest, Key: "1000000005000001", Amount: 2005 });
    }

    [Fact]
    public void Resuming_skips_output_but_still_totals_the_whole_batch()
    {
        var files = new BatchFiles(TestPaths.Batch("day1"));
        var full = new Recorder();
        BatchFigures whole = new BatchReplay(files, full).Run(null, CancellationToken.None);

        var resumed = new Recorder();
        BatchFigures partial = new BatchReplay(files, resumed).Run("1000000002", CancellationToken.None);

        Assert.Equal(whole, partial);
        Assert.DoesNotContain("1000000001", resumed.Accounts);
        Assert.DoesNotContain("1000000002", resumed.Accounts);
        Assert.Contains("1000000003", resumed.Accounts);
        Assert.Equal(full.Accounts.Count - 2, resumed.Accounts.Count);
        Assert.All(resumed.Entries, e => Assert.True(string.CompareOrdinal(e.Key, "1000000003") >= 0));
    }

    [Fact]
    public void One_cent_off_in_the_new_master_is_caught()
    {
        using var batch = new TempBatch("day1");
        batch.Replace("ACCTMAST.NEW", "+0000000280000", "+0000000280001");
        var recorder = new Recorder();
        new BatchReplay(new BatchFiles(batch.Directory), recorder).Run(null, CancellationToken.None);

        BatchBreak failure = Assert.Single(recorder.Breaks);
        Assert.Equal("BALANCE_MISMATCH", failure.Check);
        Assert.Contains("1000000001", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_transaction_the_job_applied_but_the_file_changed_is_caught()
    {
        using var batch = new TempBatch("day1");
        batch.Replace("TRANS.DAT", "1000000001000001DEP00000050000", "1000000001000001DEP00000050001");
        var recorder = new Recorder();
        new BatchReplay(new BatchFiles(batch.Directory), recorder).Run(null, CancellationToken.None);

        Assert.Contains(recorder.Breaks, b => b.Check == "BALANCE_MISMATCH");
    }

    [Fact]
    public void A_reject_with_no_matching_transaction_is_caught()
    {
        using var batch = new TempBatch("day2");
        File.AppendAllText(batch.PathOf("REJECTS.DAT"), "1000000999000001DEP0000000010020261109 R1 UNKNOWN ACCOUNT\n");
        var recorder = new Recorder();
        new BatchReplay(new BatchFiles(batch.Directory), recorder).Run(null, CancellationToken.None);

        Assert.Contains(recorder.Breaks, b => b.Check == "REJECT_NOT_IN_TRANSACTIONS");
    }

    [Fact]
    public void A_missing_reject_for_an_unknown_account_is_caught()
    {
        using var batch = new TempBatch("day2");
        string[] rejects = File.ReadAllLines(batch.PathOf("REJECTS.DAT"));
        File.WriteAllLines(batch.PathOf("REJECTS.DAT"), rejects.Where(r => !r.StartsWith("1000000099", StringComparison.Ordinal)));
        var recorder = new Recorder();
        new BatchReplay(new BatchFiles(batch.Directory), recorder).Run(null, CancellationToken.None);

        Assert.Contains(recorder.Breaks, b => b.Check == "APPLIED_TO_UNKNOWN_ACCOUNT");
    }

    [Fact]
    public void An_altered_reject_is_caught()
    {
        using var batch = new TempBatch("day2");
        batch.Replace("REJECTS.DAT", "1000000099000001DEP00000000100", "1000000099000001DEP00000000200");
        var recorder = new Recorder();
        new BatchReplay(new BatchFiles(batch.Directory), recorder).Run(null, CancellationToken.None);

        Assert.Contains(recorder.Breaks, b => b.Check == "REJECT_ALTERED");
    }

    [Fact]
    public void A_reject_reason_that_contradicts_the_master_is_caught()
    {
        using var batch = new TempBatch("day2");
        batch.Replace("REJECTS.DAT", " R1 ", " R4 ");
        var recorder = new Recorder();
        new BatchReplay(new BatchFiles(batch.Directory), recorder).Run(null, CancellationToken.None);

        Assert.Contains(recorder.Breaks, b => b.Check == "REJECT_CODE_MISMATCH");
    }

    [Fact]
    public void Out_of_sequence_transactions_stop_the_replay()
    {
        using var batch = new TempBatch("day2");
        string[] lines = File.ReadAllLines(batch.PathOf("TRANS.DAT"));
        (lines[2], lines[3]) = (lines[3], lines[2]);
        File.WriteAllLines(batch.PathOf("TRANS.DAT"), lines);

        BatchRejectedException error = Assert.Throws<BatchRejectedException>(
            () => new BatchReplay(new BatchFiles(batch.Directory), new Recorder()).Run(null, CancellationToken.None));
        Assert.Equal("SEQUENCE", error.Check);
    }

    [Fact]
    public void Corrupt_packed_data_stops_the_replay_and_names_the_field()
    {
        using var batch = new TempBatch("day2-packed");
        byte[] packed = File.ReadAllBytes(batch.PathOf("ACCTMAST.PCK"));
        packed[37] = 0xAB; // the first byte of PACK-BALANCE in record one
        File.WriteAllBytes(batch.PathOf("ACCTMAST.PCK"), packed);

        BatchRejectedException error = Assert.Throws<BatchRejectedException>(
            () => new BatchReplay(new BatchFiles(batch.Directory), new Recorder()).Run(null, CancellationToken.None));
        Assert.Equal("RECORD_FORMAT", error.Check);
        Assert.Contains("PACK-BALANCE", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_parser_reads_every_control_total()
    {
        ControlReport report = ControlReport.Parse(Path.Combine(TestPaths.Batch("day1"), "REPORT.TXT"));
        Assert.Equal(new ControlReport(2755050, 62550, 5000, 320000, 2502600, 2502600, 7, 7, 15, 7, 8, true), report);
    }

    [Fact]
    public void A_report_that_is_out_of_balance_fails_the_check()
    {
        using var batch = new TempBatch("day1");
        batch.Replace("REPORT.TXT", "IN BALANCE", "*** OUT OF BALANCE ***");
        var figures = new BatchReplay(new BatchFiles(batch.Directory), new Recorder()).Run(null, CancellationToken.None);
        ControlReport report = ControlReport.Parse(batch.PathOf("REPORT.TXT"));

        Assert.Contains(ReportCheck.Compare(figures, report), b => b.Check == "REPORT_NOT_IN_BALANCE");
    }

    [Theory]
    [InlineData(20261108, true)]
    [InlineData(20240229, true)]
    [InlineData(20260229, false)]
    [InlineData(20261301, false)]
    [InlineData(0, false)]
    public void Dates_are_validated(long value, bool valid)
    {
        Assert.Equal(valid, LedgerDates.ToDate(value) is not null);
    }
}

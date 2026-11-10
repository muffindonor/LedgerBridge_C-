using System.Globalization;
using LedgerBridge.Core.Ledger;
using Npgsql;

namespace LedgerBridge.Tests;

[Collection(PostgresCollection.Name)]
public class BatchProcessorTests(PostgresFixture postgres)
{
    private sealed class SimulatedCrash : Exception;

    private static Dictionary<string, long> ExpectedClosing(string fixture) =>
        File.ReadLines(Path.Combine(TestPaths.Batch(fixture), "ACCTMAST.NEW")).ToDictionary(
            line => line[..10],
            line => long.Parse(line.AsSpan(37, 14), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));

    [Fact]
    public async Task A_valid_batch_is_posted_and_reconciled()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();

        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day1")));

        Assert.Equal(ExpectedClosing("day1"), await ledger.BalancesAsync());
        Assert.Equal(7, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry WHERE kind <> 'OPEN'"));
        Assert.Equal(7, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry WHERE kind = 'OPEN'"));
        Assert.Equal(8, await ledger.ScalarAsync("SELECT count(*) FROM rejected_transaction"));
        Assert.Equal("RECONCILED", await ledger.TextAsync("SELECT status FROM batch"));
    }

    [Fact]
    public async Task A_negative_opening_balance_debits_the_customer()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));

        Assert.Equal("1000000007", await ledger.TextAsync("SELECT debit_account FROM ledger_entry WHERE entry_key = 'OPEN:1000000007'"));
        Assert.Equal(5000, await ledger.ScalarAsync("SELECT amount_cents FROM ledger_entry WHERE entry_key = 'OPEN:1000000007'"));
    }

    [Fact]
    public async Task The_same_batch_is_never_posted_twice_even_under_another_name()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));
        long entries = await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry");

        Assert.Equal(BatchOutcome.AlreadyProcessed, await ledger.ProcessAsync(TestPaths.Batch("day1")));
        using var renamed = new TempBatch("day1", "redelivered");
        Assert.Equal(BatchOutcome.AlreadyProcessed, await ledger.ProcessAsync(renamed.Directory));

        Assert.Equal(entries, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry"));
        Assert.Equal(1, await ledger.ScalarAsync("SELECT count(*) FROM batch"));
    }

    [Fact]
    public async Task Consecutive_nights_chain_together()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day1")));
        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day2")));

        Assert.Equal(ExpectedClosing("day2"), await ledger.BalancesAsync());
        Assert.Equal(7, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry WHERE kind = 'OPEN'"));
    }

    [Fact]
    public async Task A_night_replayed_out_of_order_is_rejected_without_touching_the_ledger()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));
        await ledger.ProcessAsync(TestPaths.Batch("day2"));
        Dictionary<string, long> before = await ledger.BalancesAsync();

        using var late = new TempBatch("day1");
        File.AppendAllText(late.PathOf("_READY"), "changes the directory only");
        File.AppendAllText(late.PathOf("REPORT.TXT"), "\n");

        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(late.Directory));
        Assert.Equal("OPENING_BALANCE_CONTINUITY", await ledger.TextAsync("SELECT check_name FROM batch_break LIMIT 1"));
        Assert.Equal(before, await ledger.BalancesAsync());
    }

    [Fact]
    public async Task A_batch_missing_known_accounts_is_rejected()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));

        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(TestPaths.Batch("medium")));
        Assert.Equal(1, await ledger.ScalarAsync("SELECT count(DISTINCT check_name) FROM batch_break WHERE check_name = 'ACCOUNT_MISSING_FROM_MASTER'"));
    }

    [Fact]
    public async Task A_tampered_batch_posts_nothing_and_says_why()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        using var batch = new TempBatch("day1");
        batch.Replace("ACCTMAST.NEW", "+0000000280000", "+0000000280001");

        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(batch.Directory));

        Assert.Equal(0, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry"));
        Assert.Equal(0, await ledger.ScalarAsync("SELECT count(*) FROM account"));
        Assert.Equal("REJECTED", await ledger.TextAsync("SELECT status FROM batch"));
        Assert.Equal(1, await ledger.ScalarAsync("SELECT count(*) FROM batch_break WHERE check_name = 'BALANCE_MISMATCH'"));
    }

    [Fact]
    public async Task A_report_that_disagrees_with_the_files_is_rejected()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        using var batch = new TempBatch("day1");
        batch.Replace("REPORT.TXT", "625.50", "625.51");

        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(batch.Directory));

        Assert.Equal("DEPOSIT_TOTAL", await ledger.TextAsync("SELECT check_name FROM batch_break LIMIT 1"));
        Assert.Equal(0, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry"));
    }

    [Fact]
    public async Task A_missing_file_rejects_the_batch()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        using var batch = new TempBatch("day1");
        File.Delete(batch.PathOf("REJECTS.DAT"));

        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(batch.Directory));
        Assert.Equal("MISSING_FILE", await ledger.TextAsync("SELECT check_name FROM batch_break"));
    }

    [Fact]
    public async Task A_corrected_batch_can_follow_a_rejected_one()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        using var bad = new TempBatch("day1");
        bad.Replace("ACCTMAST.NEW", "+0000000280000", "+0000000280001");
        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(bad.Directory));

        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day1")));
    }

    [Fact]
    public async Task A_rejected_night_can_be_retried_once_its_cause_is_fixed()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));
        await ledger.ExecuteAsync("UPDATE account SET balance_cents = balance_cents + 1 WHERE account_id = '1000000001'");
        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(TestPaths.Batch("day2")));

        await ledger.ExecuteAsync("UPDATE account SET balance_cents = balance_cents - 1 WHERE account_id = '1000000001'");

        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day2")));
        Assert.Equal(0, await ledger.ScalarAsync("SELECT count(*) FROM batch_break"));
        Assert.Equal(2, await ledger.ScalarAsync("SELECT count(*) FROM batch"));
    }

    [Fact]
    public async Task A_batch_with_too_many_breaks_says_how_many_went_unrecorded()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        using var batch = new TempBatch("medium");
        string[] lines = File.ReadAllLines(batch.PathOf("ACCTMAST.NEW"));
        File.WriteAllLines(
            batch.PathOf("ACCTMAST.NEW"),
            lines.Select(line => line[..50] + (line[50] == '0' ? '1' : '0') + line[51..]),
            new System.Text.UTF8Encoding(false));

        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(batch.Directory));

        Assert.Equal(101, await ledger.ScalarAsync("SELECT count(*) FROM batch_break"));
        Assert.Equal(1, await ledger.ScalarAsync("SELECT count(*) FROM batch_break WHERE check_name = 'MORE_BREAKS'"));
    }

    [Fact]
    public async Task A_crash_at_every_chunk_boundary_still_lands_every_record_once()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        var crashing = new ProcessorOptions
        {
            GroupsPerChunk = 20,
            AfterChunkCommitted = _ => throw new SimulatedCrash(),
        };

        int crashes = 0;
        while (true)
        {
            try
            {
                Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("medium"), crashing));
                break;
            }
            catch (SimulatedCrash)
            {
                crashes++;
                Assert.True(crashes < 100, "The batch never finished.");
            }
        }

        Assert.True(crashes >= 15, $"Only {crashes} crashes, so the chunks were larger than intended.");
        Assert.Equal(crashes, await ledger.ScalarAsync("SELECT chunks_committed FROM batch"));
        Assert.Equal(ExpectedClosing("medium"), await ledger.BalancesAsync());
        Assert.Equal(1128, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry WHERE kind <> 'OPEN'"));
        Assert.Equal(81, await ledger.ScalarAsync("SELECT count(*) FROM rejected_transaction"));
    }

    [Fact]
    public async Task A_failed_chunk_commits_nothing_of_itself()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ExecuteAsync(
            """
            CREATE FUNCTION fail_on_key() RETURNS trigger AS $$
            BEGIN
                IF NEW.account_id = '1000000100' THEN RAISE EXCEPTION 'boom'; END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_on_key BEFORE INSERT ON batch_account
                FOR EACH ROW EXECUTE FUNCTION fail_on_key();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => ledger.ProcessAsync(TestPaths.Batch("medium")));

        // Account 1000000100 is the 50th, so it falls in the third chunk of 20.
        Assert.Equal(2, await ledger.ScalarAsync("SELECT chunks_committed FROM batch"));
        long accounts = await ledger.ScalarAsync("SELECT count(*) FROM account");
        Assert.InRange(accounts, 30, 40); // a chunk is 20 keys, and unknown accounts are keys too
        Assert.Equal(accounts, await ledger.ScalarAsync("SELECT count(*) FROM batch_account"));
        Assert.Equal(0, await ledger.ScalarAsync("SELECT count(*) FROM account WHERE account_id >= '1000000100'"));

        await ledger.ExecuteAsync("DROP TRIGGER fail_on_key ON batch_account");
        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("medium")));
        Assert.Equal(ExpectedClosing("medium"), await ledger.BalancesAsync());
    }

    [Fact]
    public async Task Packed_new_master_gives_the_same_ledger_as_text()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));
        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day2-packed")));
        Assert.Equal(ExpectedClosing("day2-packed"), await ledger.BalancesAsync());
    }

    [Fact]
    public async Task A_second_worker_cannot_post_while_the_first_holds_the_lock()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await using NpgsqlConnection holder = await ledger.Db.OpenConnectionAsync();
        Assert.True(await LedgerStore.TryLockPostingAsync(holder, CancellationToken.None));

        BatchOutcome outcome = await ledger.ProcessAsync(TestPaths.Batch("day1"), new ProcessorOptions { LockWait = TimeSpan.Zero });

        Assert.Equal(BatchOutcome.Blocked, outcome);
        Assert.Equal(0, await ledger.ScalarAsync("SELECT count(*) FROM batch"));
    }

    [Fact]
    public async Task Two_workers_racing_on_one_batch_post_it_once()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        BatchOutcome[] outcomes = await Task.WhenAll(
            ledger.ProcessAsync(TestPaths.Batch("medium")),
            ledger.ProcessAsync(TestPaths.Batch("medium")));

        Assert.Contains(BatchOutcome.Reconciled, outcomes);
        Assert.Contains(BatchOutcome.AlreadyProcessed, outcomes);
        Assert.Equal(1128, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry WHERE kind <> 'OPEN'"));
    }

    [Fact]
    public async Task An_unfinished_batch_blocks_the_next_night()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        var crashing = new ProcessorOptions { GroupsPerChunk = 2, AfterChunkCommitted = _ => throw new SimulatedCrash() };
        await Assert.ThrowsAsync<SimulatedCrash>(() => ledger.ProcessAsync(TestPaths.Batch("day1"), crashing));

        Assert.Equal(BatchOutcome.Blocked, await ledger.ProcessAsync(TestPaths.Batch("day2")));

        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day1")));
        Assert.Equal(BatchOutcome.Reconciled, await ledger.ProcessAsync(TestPaths.Batch("day2")));
    }

    [Fact]
    public async Task Corruption_during_posting_is_flagged_out_of_balance_and_halts_later_nights()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        var corrupting = new ProcessorOptions
        {
            GroupsPerChunk = 2,
            AfterChunkCommitted = _ => ledger.ExecuteAsync(
                "UPDATE ledger_entry SET amount_cents = amount_cents + 1 WHERE entry_key = '1000000001000001'"),
        };

        Assert.Equal(BatchOutcome.OutOfBalance, await ledger.ProcessAsync(TestPaths.Batch("day1"), corrupting));

        Assert.Equal("OUT_OF_BALANCE", await ledger.TextAsync("SELECT status FROM batch"));
        Assert.True(await ledger.ScalarAsync("SELECT count(*) FROM batch_break WHERE check_name = 'LEDGER_DRIFT'") == 1);
        Assert.Equal(BatchOutcome.Blocked, await ledger.ProcessAsync(TestPaths.Batch("day2")));
    }

    [Fact]
    public async Task A_balance_changed_behind_the_ledgers_back_stops_the_next_night()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));
        await ledger.ExecuteAsync("UPDATE account SET balance_cents = balance_cents + 1 WHERE account_id = '1000000001'");

        Assert.Equal(BatchOutcome.Rejected, await ledger.ProcessAsync(TestPaths.Batch("day2")));
        Assert.Equal("OPENING_BALANCE_CONTINUITY", await ledger.TextAsync("SELECT check_name FROM batch_break LIMIT 1"));
    }

    [Fact]
    public async Task A_chunk_for_an_unknown_batch_commits_nothing()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await using NpgsqlConnection connection = await ledger.Db.OpenConnectionAsync();
        await using (var create = new NpgsqlCommand(LedgerStore.CreateChunkTableSql, connection))
        {
            await create.ExecuteNonQueryAsync();
        }

        var chunk = new PostingChunk(
            "1000000001",
            [new EntryRow("1000000001000001", "DEP", LedgerAccounts.Cash, "1000000001", 100, null)],
            [],
            []);

        await Assert.ThrowsAsync<LedgerIntegrityException>(
            () => LedgerStore.CommitChunkAsync(connection, batchId: 999, chunk, expectedCheckpoint: null, CancellationToken.None));

        Assert.Equal(0, await ledger.ScalarAsync("SELECT count(*) FROM ledger_entry"));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_duplicate_entry()
    {
        await using TestLedger ledger = await postgres.CreateLedgerAsync();
        await ledger.ProcessAsync(TestPaths.Batch("day1"));

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => ledger.ExecuteAsync(
            """
            INSERT INTO ledger_entry (batch_id, entry_key, kind, debit_account, credit_account, amount_cents)
            SELECT batch_id, entry_key, kind, debit_account, credit_account, amount_cents
            FROM ledger_entry LIMIT 1
            """));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
    }
}

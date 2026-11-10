using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using LedgerBridge.Core.Batch;
using Npgsql;
using NpgsqlTypes;

namespace LedgerBridge.Tools;

/// <summary>
/// The control experiment. It loads the same batch with the common shortcut: commit data in
/// chunks and save progress to a file every few chunks. The crash test then shows what the
/// real worker's single-transaction checkpoint is protecting against.
/// </summary>
public static class NaiveControl
{
    private const int ChunkEntries = 1000;

    public static async Task<int> RunAsync(Arguments options)
    {
        string batch = Path.GetFullPath(options.Text("batch"));
        string connection = options.Text("connection");
        int rounds = options.Number("rounds", 5);
        int kills = options.Number("kills", 10);
        int checkpointEvery = options.Number("checkpoint-every", 10);
        var random = new Random(options.Number("seed", 7));
        string self = Assembly.GetExecutingAssembly().Location;
        string progress = Path.Combine(Path.GetTempPath(), "ledger-bridge-naive-" + Guid.NewGuid().ToString("N"));

        ExpectedBatch expected = ExpectedBatch.Load(batch);
        await using NpgsqlDataSource db = NpgsqlDataSource.Create(connection);
        string[] childArgs = ["naive-load", "--batch", batch, "--connection", connection, "--progress", progress, "--checkpoint-every", checkpointEvery.ToString(CultureInfo.InvariantCulture)];

        await ResetAsync(db, progress);
        var stopwatch = Stopwatch.StartNew();
        using (Process baseline = ChaosSupport.Start(self, childArgs))
        {
            await baseline.WaitForExitAsync();
        }

        stopwatch.Stop();
        Console.WriteLine($"Clean naive run: {stopwatch.Elapsed.TotalSeconds:F1}s, duplicates {await DuplicatesAsync(db)}");

        int badRounds = 0;
        for (int round = 1; round <= rounds; round++)
        {
            await ResetAsync(db, progress);
            int killCount = 0;
            bool finished = false;
            for (int k = 0; k < kills && !finished; k++)
            {
                using Process child = ChaosSupport.Start(self, childArgs);
                if (await ChaosSupport.KillAfterAsync(child, stopwatch.Elapsed * (0.05 + (random.NextDouble() * 0.35))))
                {
                    killCount++;
                }
                else
                {
                    finished = true;
                }
            }

            // Only finish a job that was left unfinished. Running a completed job again
            // would add duplicates the crashes didn't cause, and flatter the real worker.
            if (!finished)
            {
                using Process finisher = ChaosSupport.Start(self, childArgs);
                await finisher.WaitForExitAsync();
            }

            long duplicates = await DuplicatesAsync(db);
            long wrong = await WrongBalancesAsync(db, expected);
            long excess = await ChaosSupport.ScalarAsync(db, "SELECT count(*) FROM naive_entry") - expected.Applied;
            Console.WriteLine($"Round {round}: {killCount} kills, {duplicates} duplicate entries, {excess} extra entries, {wrong} wrong balances");
            badRounds += duplicates > 0 || wrong > 0 ? 1 : 0;
        }

        File.Delete(progress);
        Console.WriteLine($"Rounds with a wrong ledger: {badRounds} of {rounds}");
        return 0;
    }

    public static async Task<int> LoadAsync(Arguments options)
    {
        var files = new BatchFiles(options.Text("batch"));
        string progress = options.Text("progress");
        int checkpointEvery = options.Number("checkpoint-every", 10);
        await using NpgsqlDataSource db = NpgsqlDataSource.Create(options.Text("connection"));
        string? resume = File.Exists(progress) ? await File.ReadAllTextAsync(progress) : null;
        var sink = new Sink(db, progress, checkpointEvery);
        await Task.Run(() => new BatchReplay(files, sink).Run(resume, CancellationToken.None));
        sink.Flush();
        return 0;
    }

    private static async Task ResetAsync(NpgsqlDataSource db, string progress)
    {
        File.Delete(progress);
        await ChaosSupport.ExecuteAsync(db, "DROP TABLE IF EXISTS naive_entry; CREATE TABLE naive_entry (entry_key text NOT NULL, account text NOT NULL, delta bigint NOT NULL)");
    }

    private static Task<long> DuplicatesAsync(NpgsqlDataSource db) =>
        ChaosSupport.ScalarAsync(db, "SELECT count(*) - count(DISTINCT entry_key) FROM naive_entry");

    private static async Task<long> WrongBalancesAsync(NpgsqlDataSource db, ExpectedBatch expected)
    {
        var movement = new Dictionary<string, long>(StringComparer.Ordinal);
        await using NpgsqlCommand command = db.CreateCommand("SELECT account, sum(delta)::bigint FROM naive_entry GROUP BY account");
        command.CommandTimeout = 0;
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            movement[reader.GetString(0)] = reader.GetInt64(1);
        }

        return expected.Closing.Count(pair =>
            expected.Opening[pair.Key] + movement.GetValueOrDefault(pair.Key.TrimEnd(' ')) != pair.Value);
    }

    private sealed class Sink(NpgsqlDataSource db, string progress, int checkpointEvery) : IReplaySink
    {
        private readonly List<(string Key, string Account, long Delta)> _rows = [];
        private int _chunks;
        private string? _lastKey;

        public void Entry(EntryKind kind, ReadOnlySpan<byte> accountId, ReadOnlySpan<byte> entryKey, long amountCents, int txnDate) =>
            _rows.Add((
                Encoding.Latin1.GetString(entryKey),
                Encoding.Latin1.GetString(accountId).TrimEnd(' '),
                kind == EntryKind.Withdrawal ? -amountCents : amountCents));

        public void Reject(ReadOnlySpan<byte> rejectRecord)
        {
        }

        public void Account(in AccountResult account)
        {
        }

        public void Break(BatchBreak failure)
        {
        }

        public void GroupEnd(ReadOnlySpan<byte> key)
        {
            _lastKey = Encoding.Latin1.GetString(key);
            if (_rows.Count >= ChunkEntries)
            {
                Flush();
            }
        }

        public void Flush()
        {
            if (_rows.Count == 0)
            {
                return;
            }

            using NpgsqlConnection connection = db.OpenConnection();
            using (NpgsqlBinaryImporter writer = connection.BeginBinaryImport("COPY naive_entry (entry_key, account, delta) FROM STDIN (FORMAT BINARY)"))
            {
                foreach ((string key, string account, long delta) in _rows)
                {
                    writer.StartRow();
                    writer.Write(key, NpgsqlDbType.Text);
                    writer.Write(account, NpgsqlDbType.Text);
                    writer.Write(delta, NpgsqlDbType.Bigint);
                }

                writer.Complete();
            }

            _rows.Clear();
            if (++_chunks % checkpointEvery == 0)
            {
                File.WriteAllText(progress, _lastKey);
            }
        }
    }
}

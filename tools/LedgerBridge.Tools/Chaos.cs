using System.Diagnostics;
using Npgsql;

namespace LedgerBridge.Tools;

/// <summary>
/// Hard-kills the real worker at random moments, restarts it until the batch finishes,
/// then checks the ledger to the cent against the COBOL files.
/// </summary>
public static class Chaos
{
    public static async Task<int> RunAsync(Arguments options)
    {
        string batch = Path.GetFullPath(options.Text("batch"));
        string workerDll = Path.GetFullPath(options.Text("worker"));
        string connection = options.Text("connection");
        int rounds = options.Number("rounds", 5);
        int kills = options.Number("kills", 10);
        var random = new Random(options.Number("seed", 7));

        ExpectedBatch expected = ExpectedBatch.Load(batch);
        await using NpgsqlDataSource db = NpgsqlDataSource.Create(connection);
        string sandbox = Path.Combine(Path.GetTempPath(), "ledger-bridge-chaos-" + Guid.NewGuid().ToString("N"));
        string name = Path.GetFileName(batch);

        try
        {
            string inbox = Path.Combine(sandbox, "inbox");
            string archive = Path.Combine(sandbox, "archive");
            string[] workerArgs =
            [
                "--Inbox:Once=true",
                $"--Inbox:Path={inbox}",
                $"--Inbox:ArchivePath={archive}",
                $"--Inbox:RejectedPath={Path.Combine(sandbox, "rejected")}",
                $"--ConnectionStrings:Ledger={connection}",
            ];

            Copy(batch, Path.Combine(inbox, name));
            await ResetAsync(db);
            var stopwatch = Stopwatch.StartNew();
            using (Process baseline = ChaosSupport.Start(workerDll, workerArgs))
            {
                await baseline.WaitForExitAsync();
            }

            stopwatch.Stop();
            Console.WriteLine($"Clean run: {stopwatch.Elapsed.TotalSeconds:F1}s");
            int failures = await VerifyAsync(db, expected, "clean run") ? 0 : 1;
            Directory.Move(Path.Combine(archive, name), Path.Combine(inbox, name));

            int totalKills = 0, midPostKills = 0;
            for (int round = 1; round <= rounds; round++)
            {
                await ResetAsync(db);
                int killsThisRound = 0, midPostThisRound = 0;
                for (int k = 0; k < kills; k++)
                {
                    // Short runs, because each restart resumes and has less left to do.
                    double fraction = 0.05 + (random.NextDouble() * 0.35);
                    using Process worker = ChaosSupport.Start(workerDll, workerArgs);
                    bool killed = await ChaosSupport.KillAfterAsync(worker, stopwatch.Elapsed * fraction);
                    if (!killed)
                    {
                        break;
                    }

                    killsThisRound++;
                    if (await ProgressAsync(db) > 0)
                    {
                        midPostThisRound++;
                    }
                }

                if (Directory.Exists(Path.Combine(inbox, name)))
                {
                    using Process finisher = ChaosSupport.Start(workerDll, workerArgs);
                    await finisher.WaitForExitAsync();
                }

                totalKills += killsThisRound;
                midPostKills += midPostThisRound;
                bool ok = await VerifyAsync(db, expected, $"round {round}: {killsThisRound} kills, {midPostThisRound} during posting");
                failures += ok ? 0 : 1;
                if (Directory.Exists(Path.Combine(archive, name)))
                {
                    Directory.Move(Path.Combine(archive, name), Path.Combine(inbox, name));
                }
            }

            Console.WriteLine($"Rounds: {rounds}, kills: {totalKills}, during posting: {midPostKills}, failed checks: {failures}");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            if (Directory.Exists(sandbox))
            {
                Directory.Delete(sandbox, recursive: true);
            }
        }
    }

    /// <summary>Chunks committed by a batch still being posted, or 0 if none is.</summary>
    private static async Task<long> ProgressAsync(NpgsqlDataSource db)
    {
        try
        {
            return await ChaosSupport.ScalarAsync(db, "SELECT coalesce(max(chunks_committed), 0) FROM batch WHERE status = 'POSTING'");
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return 0; // killed before the worker created the schema
        }
    }

    private static async Task ResetAsync(NpgsqlDataSource db) =>
        await ChaosSupport.ExecuteAsync(db, "DROP SCHEMA public CASCADE; CREATE SCHEMA public");

    private static async Task<bool> VerifyAsync(NpgsqlDataSource db, ExpectedBatch expected, string label)
    {
        long status = await ChaosSupport.ScalarAsync(db, "SELECT count(*) FROM batch WHERE status = 'RECONCILED'");
        long entries = await ChaosSupport.ScalarAsync(db, "SELECT count(*) FROM ledger_entry WHERE kind <> 'OPEN'");
        long rejects = await ChaosSupport.ScalarAsync(db, "SELECT count(*) FROM rejected_transaction");

        long wrong = 0, seen = 0;
        await using (NpgsqlCommand command = db.CreateCommand(
            """
            SELECT account, sum(delta)::bigint FROM (
                SELECT credit_account AS account, amount_cents AS delta FROM ledger_entry
                UNION ALL
                SELECT debit_account, -amount_cents FROM ledger_entry
            ) sides WHERE account NOT LIKE '#%' GROUP BY account
            """))
        {
            command.CommandTimeout = 0;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                seen++;
                if (!expected.Closing.TryGetValue(reader.GetString(0), out long closing) || closing != reader.GetInt64(1))
                {
                    wrong++;
                }
            }
        }

        // An account with no entries at all can only be right if it opened and closed at zero.
        long zeroAccounts = expected.Closing.Values.Count(v => v == 0);
        bool ok = status == 1 && entries == expected.Applied && rejects == expected.Rejected && wrong == 0
            && seen >= expected.Closing.Count - zeroAccounts && seen <= expected.Closing.Count;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}: entries {entries}/{expected.Applied}, rejects {rejects}/{expected.Rejected}, wrong balances {wrong}");
        return ok;
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }
}

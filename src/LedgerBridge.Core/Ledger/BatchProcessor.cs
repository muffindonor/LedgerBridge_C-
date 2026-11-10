using System.Threading.Channels;
using LedgerBridge.Core.Batch;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace LedgerBridge.Core.Ledger;

public enum BatchOutcome
{
    Reconciled,
    AlreadyProcessed,
    Rejected,
    OutOfBalance,

    /// <summary>Another worker holds the lock, or an earlier batch must be dealt with first.</summary>
    Blocked,
}

public sealed class ProcessorOptions
{
    /// <summary>Account keys per database transaction. Smaller means less rework after a crash, and slower runs.</summary>
    public int GroupsPerChunk { get; set; } = 1000;

    /// <summary>How many finished chunks may wait for the database before the reader pauses.</summary>
    public int ChannelCapacity { get; set; } = 4;

    public TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Test seam, called after each committed chunk with the running chunk count.</summary>
    public Func<int, Task>? AfterChunkCommitted { get; set; }
}

/// <summary>
/// Runs one batch from start to finish: identify, validate, post, reconcile.
/// Validation is a separate full pass so that a bad batch leaves the ledger untouched.
/// </summary>
public sealed class BatchProcessor(NpgsqlDataSource dataSource, ProcessorOptions options, ILogger<BatchProcessor> logger)
{
    private const int MaxRecordedBreaks = 100;

    public async Task<BatchOutcome> ProcessAsync(BatchFiles files, CancellationToken cancellationToken)
    {
        byte[] hash = await Task.Run(files.ComputeHash, cancellationToken);

        // This connection stays open for the whole run. If the process dies, Postgres drops
        // the connection and releases the lock, so a crash can never leave a batch locked.
        await using NpgsqlConnection lockConnection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await WaitForLockAsync(lockConnection, cancellationToken))
        {
            logger.LogWarning("Batch {Batch}: another worker holds the posting lock.", files.Name);
            return BatchOutcome.Blocked;
        }

        try
        {
            return await ProcessLockedAsync(lockConnection, files, hash, cancellationToken);
        }
        finally
        {
            // Closing only returns a connection to the pool, where a session lock would live on.
            try
            {
                await LedgerStore.UnlockPostingAsync(lockConnection);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // A broken connection has already lost the lock. Don't hide the original failure.
                logger.LogWarning(ex, "Batch {Batch}: could not release the posting lock.", files.Name);
            }
        }
    }

    private async Task<BatchOutcome> ProcessLockedAsync(
        NpgsqlConnection lockConnection, BatchFiles files, byte[] hash, CancellationToken cancellationToken)
    {
        BatchRow? batch = await LedgerStore.FindBatchAsync(lockConnection, hash, cancellationToken);
        switch (batch?.Status)
        {
            case "RECONCILED":
                logger.LogInformation("Batch {Batch}: already reconciled as {Original}, skipping.", files.Name, batch.Name);
                return BatchOutcome.AlreadyProcessed;
            case "OUT_OF_BALANCE":
                logger.LogCritical("Batch {Batch}: is out of balance and needs investigation.", files.Name);
                return BatchOutcome.OutOfBalance;
        }

        BatchRow? blocker = await LedgerStore.FindBlockingBatchAsync(lockConnection, hash, cancellationToken);
        if (blocker is not null)
        {
            logger.LogCritical("Batch {Batch}: blocked by {Other}, which is {Status}.", files.Name, blocker.Name, blocker.Status);
            return BatchOutcome.Blocked;
        }

        // A rejected batch is validated again if it's delivered again. Its files may be fine and
        // the ledger was what had to change first, such as a night that arrived out of order.
        if (batch is null or { Status: "REJECTED" })
        {
            IReadOnlyList<BatchBreak> breaks = await ValidateAsync(lockConnection, files, cancellationToken);
            if (breaks.Count > 0)
            {
                await LedgerStore.SaveBatchAsync(lockConnection, files.Name, hash, "REJECTED", breaks, cancellationToken);
                logger.LogError("Batch {Batch}: rejected with {Count} breaks. First: {Check}: {Detail}", files.Name, breaks.Count, breaks[0].Check, breaks[0].Detail);
                return BatchOutcome.Rejected;
            }

            batch = await LedgerStore.SaveBatchAsync(lockConnection, files.Name, hash, "POSTING", [], cancellationToken);
            logger.LogInformation("Batch {Batch}: validated, posting.", files.Name);
        }
        else
        {
            logger.LogWarning("Batch {Batch}: resuming after account {Account}.", files.Name, batch.LastAccountId ?? "(none)");
        }

        await PostAsync(batch, files, cancellationToken);
        return await ReconcileAsync(lockConnection, batch, files, cancellationToken);
    }

    private async Task<bool> WaitForLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        // A crashed worker's lock lasts until Postgres notices the dead connection.
        DateTime deadline = DateTime.UtcNow + options.LockWait;
        while (!await LedgerStore.TryLockPostingAsync(connection, cancellationToken))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return true;
    }

    private static async Task<IReadOnlyList<BatchBreak>> ValidateAsync(
        NpgsqlConnection connection, BatchFiles files, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> missing = files.MissingFiles();
        if (missing.Count > 0)
        {
            return [new BatchBreak("MISSING_FILE", string.Join(", ", missing))];
        }

        await using (var create = new NpgsqlCommand(
            "CREATE TEMP TABLE IF NOT EXISTS batch_opening (account_id text PRIMARY KEY, opening_cents bigint NOT NULL); TRUNCATE batch_opening",
            connection))
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var sink = new ValidationSink(MaxRecordedBreaks);
        BatchFigures figures;
        try
        {
            await using NpgsqlBinaryImporter importer = await connection.BeginBinaryImportAsync(
                "COPY batch_opening (account_id, opening_cents) FROM STDIN (FORMAT BINARY)", cancellationToken);
            sink.Openings = importer;
            figures = await Task.Run(() => new BatchReplay(files, sink).Run(null, cancellationToken), cancellationToken);
            await importer.CompleteAsync(cancellationToken);
        }
        catch (BatchRejectedException ex)
        {
            sink.Break(new BatchBreak(ex.Check, ex.Detail));
            return sink.Breaks;
        }

        try
        {
            ControlReport report = ControlReport.Parse(files.Report);
            foreach (BatchBreak failure in ReportCheck.Compare(figures, report))
            {
                sink.Break(failure);
            }
        }
        catch (BatchRejectedException ex)
        {
            sink.Break(new BatchBreak(ex.Check, ex.Detail));
        }

        await CheckContinuityAsync(connection, sink, cancellationToken);
        return sink.Breaks;
    }

    /// <summary>Tonight's opening balances must equal the ledger, or a night was skipped or replayed.</summary>
    private static async Task CheckContinuityAsync(NpgsqlConnection connection, ValidationSink sink, CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand(
            $"""
            SELECT o.account_id, o.opening_cents, a.balance_cents
            FROM batch_opening o JOIN account a USING (account_id)
            WHERE o.opening_cents <> a.balance_cents
            ORDER BY o.account_id LIMIT {MaxRecordedBreaks}
            """,
            connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sink.Break(new BatchBreak(
                    "OPENING_BALANCE_CONTINUITY",
                    $"Account {reader.GetString(0)}: the master opens at {reader.GetInt64(1)}, the ledger holds {reader.GetInt64(2)}."));
            }
        }

        await using (var command = new NpgsqlCommand(
            $"""
            SELECT a.account_id FROM account a
            WHERE NOT EXISTS (SELECT 1 FROM batch_opening o WHERE o.account_id = a.account_id)
            ORDER BY a.account_id LIMIT {MaxRecordedBreaks}
            """,
            connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sink.Break(new BatchBreak("ACCOUNT_MISSING_FROM_MASTER", $"Account {reader.GetString(0)} is in the ledger but not on the master."));
            }
        }
    }

    private async Task PostAsync(BatchRow batch, BatchFiles files, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<PostingChunk> channel = Channel.CreateBounded<PostingChunk>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        Task producer = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    var sink = new PostingSink(options.GroupsPerChunk, channel.Writer, linked.Token);
                    new BatchReplay(files, sink).Run(batch.LastAccountId, linked.Token);
                    sink.Flush();
                    channel.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                    throw;
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(linked.Token);
            await using (var create = new NpgsqlCommand(LedgerStore.CreateChunkTableSql, connection))
            {
                await create.ExecuteNonQueryAsync(linked.Token);
            }

            string? checkpoint = batch.LastAccountId;
            int committed = 0;
            await foreach (PostingChunk chunk in channel.Reader.ReadAllAsync(linked.Token))
            {
                await LedgerStore.CommitChunkAsync(connection, batch.Id, chunk, checkpoint, linked.Token);
                checkpoint = chunk.LastKey;
                committed++;
                if (options.AfterChunkCommitted is { } hook)
                {
                    await hook(committed);
                }
            }
        }
        catch
        {
            await linked.CancelAsync();
            try
            {
                await producer;
            }
            catch
            {
                // The original failure is the one worth reporting.
            }

            throw;
        }

        await producer;
    }

    private async Task<BatchOutcome> ReconcileAsync(
        NpgsqlConnection connection, BatchRow batch, BatchFiles files, CancellationToken cancellationToken)
    {
        ControlReport report = ControlReport.Parse(files.Report);
        BatchFigures stored = await LedgerStore.ReadStoredFiguresAsync(connection, batch.Id, cancellationToken);
        var breaks = new List<BatchBreak>(ReportCheck.Compare(stored, report));

        long drifted = await LedgerStore.CountDriftedAccountsAsync(connection, cancellationToken);
        if (drifted != 0)
        {
            breaks.Add(new BatchBreak("LEDGER_DRIFT", $"{drifted} accounts differ from the sum of their ledger entries."));
        }

        string status = breaks.Count == 0 ? "RECONCILED" : "OUT_OF_BALANCE";
        await LedgerStore.FinishBatchAsync(connection, batch.Id, status, breaks, cancellationToken);

        if (breaks.Count > 0)
        {
            logger.LogCritical("Batch {Batch}: OUT OF BALANCE after posting. First: {Check}: {Detail}", files.Name, breaks[0].Check, breaks[0].Detail);
            return BatchOutcome.OutOfBalance;
        }

        logger.LogInformation(
            "Batch {Batch}: reconciled. {Accounts} accounts, {Applied} applied, {Rejected} rejected.",
            files.Name,
            stored.Accounts,
            stored.Applied,
            stored.Rejected);
        return BatchOutcome.Reconciled;
    }

    private sealed class ValidationSink(int maxBreaks) : IReplaySink
    {
        private readonly List<BatchBreak> _breaks = [];
        private int _notRecorded;

        public NpgsqlBinaryImporter? Openings { get; set; }

        public IReadOnlyList<BatchBreak> Breaks => _notRecorded == 0
            ? _breaks
            : [.. _breaks, new BatchBreak("MORE_BREAKS", $"{_notRecorded} more breaks were found but not recorded.")];

        public void Entry(EntryKind kind, ReadOnlySpan<byte> accountId, ReadOnlySpan<byte> entryKey, long amountCents, int txnDate)
        {
        }

        public void Reject(ReadOnlySpan<byte> rejectRecord)
        {
        }

        public void GroupEnd(ReadOnlySpan<byte> key)
        {
        }

        public void Account(in AccountResult account)
        {
            Openings!.StartRow();
            Openings.Write(System.Text.Encoding.Latin1.GetString(account.Id).TrimEnd(' '), NpgsqlDbType.Text);
            Openings.Write(account.Opening, NpgsqlDbType.Bigint);
        }

        public void Break(BatchBreak failure)
        {
            if (_breaks.Count < maxBreaks)
            {
                _breaks.Add(failure);
            }
            else
            {
                _notRecorded++;
            }
        }
    }
}

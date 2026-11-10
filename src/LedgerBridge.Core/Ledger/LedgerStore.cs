using LedgerBridge.Core.Batch;
using Npgsql;
using NpgsqlTypes;

namespace LedgerBridge.Core.Ledger;

public sealed record BatchRow(long Id, string Name, string Status, string? LastAccountId);

/// <summary>A database-level failure that means the ledger and the batch disagree.</summary>
public sealed class LedgerIntegrityException(string message) : Exception(message);

/// <summary>All SQL lives here. Plain Npgsql, because binary COPY is the whole point of the write path.</summary>
public sealed class LedgerStore(NpgsqlDataSource dataSource)
{
    public const long PostingLockKey = 0x4C45444745525F31; // "LEDGER_1"
    private const long SchemaLockKey = 0x4C45444745525F32; // "LEDGER_2"

    public const string CreateChunkTableSql = """
        CREATE TEMP TABLE IF NOT EXISTS chunk_account (
            account_id text NOT NULL, name text NOT NULL, type text NOT NULL, status text NOT NULL,
            opening_cents bigint NOT NULL, closing_cents bigint NOT NULL,
            last_txn_date date, applied_count integer NOT NULL
        ) ON COMMIT DELETE ROWS
        """;

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        using Stream stream = typeof(LedgerStore).Assembly.GetManifestResourceStream("Schema.sql")
            ?? throw new InvalidOperationException("Embedded Schema.sql is missing.");
        using var reader = new StreamReader(stream);
        string schema = await reader.ReadToEndAsync(cancellationToken);

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue(SchemaLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand(schema, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public static async Task<bool> TryLockPostingAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock($1)", connection);
        command.Parameters.AddWithValue(PostingLockKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task UnlockPostingAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
        command.Parameters.AddWithValue(PostingLockKey);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    public static async Task<BatchRow?> FindBatchAsync(NpgsqlConnection connection, byte[] hash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT id, name, status, last_account_id FROM batch WHERE content_hash = $1", connection);
        command.Parameters.AddWithValue(hash);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BatchRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    /// <summary>Finds an earlier batch that's unfinished or out of balance and so must block new ones.</summary>
    public static async Task<BatchRow?> FindBlockingBatchAsync(NpgsqlConnection connection, byte[] hash, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id, name, status, last_account_id FROM batch
            WHERE status IN ('POSTING', 'OUT_OF_BALANCE') AND content_hash <> $1
            ORDER BY id LIMIT 1
            """,
            connection);
        command.Parameters.AddWithValue(hash);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BatchRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    /// <summary>Records a new batch, or the new result of re-validating one that was rejected.</summary>
    public static async Task<BatchRow> SaveBatchAsync(
        NpgsqlConnection connection, string name, byte[] hash, string status, IReadOnlyList<BatchBreak> breaks, CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO batch (name, content_hash, status, finished_at)
            VALUES ($1, $2, $3, CASE WHEN $3 = 'POSTING' THEN NULL ELSE now() END)
            ON CONFLICT (content_hash) DO UPDATE SET
                name = EXCLUDED.name, status = EXCLUDED.status, finished_at = EXCLUDED.finished_at
            RETURNING id
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(hash);
        command.Parameters.AddWithValue(status);
        long id = (long)(await command.ExecuteScalarAsync(cancellationToken))!;

        await using (var clear = new NpgsqlCommand("DELETE FROM batch_break WHERE batch_id = $1", connection, transaction))
        {
            clear.Parameters.AddWithValue(id);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertBreaksAsync(connection, transaction, id, breaks, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BatchRow(id, name, status, null);
    }

    /// <summary>
    /// Commits a chunk and moves the checkpoint in a single transaction. After a crash,
    /// either both happened or neither did, which is what makes each record land exactly once.
    /// </summary>
    public static async Task CommitChunkAsync(
        NpgsqlConnection connection, long batchId, PostingChunk chunk, string? expectedCheckpoint, CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await CopyEntriesAsync(connection, batchId, chunk.Entries, cancellationToken);
        await CopyRejectsAsync(connection, batchId, chunk.Rejects, cancellationToken);
        await CopyChunkAccountsAsync(connection, chunk.Accounts, cancellationToken);

        await using (var guard = new NpgsqlCommand(
            """
            SELECT count(*) FROM chunk_account c JOIN account a USING (account_id)
            WHERE a.balance_cents <> c.opening_cents
            """,
            connection,
            transaction))
        {
            long drifted = (long)(await guard.ExecuteScalarAsync(cancellationToken))!;
            if (drifted != 0)
            {
                throw new LedgerIntegrityException($"{drifted} accounts no longer match their opening balance.");
            }
        }

        await using (NpgsqlBatch batch = new(connection, transaction))
        {
            batch.BatchCommands.Add(Statement(
                $"""
                INSERT INTO ledger_entry (batch_id, entry_key, kind, debit_account, credit_account, amount_cents)
                SELECT $1, 'OPEN:' || c.account_id, 'OPEN',
                       CASE WHEN c.opening_cents >= 0 THEN '{LedgerAccounts.OpeningEquity}' ELSE c.account_id END,
                       CASE WHEN c.opening_cents >= 0 THEN c.account_id ELSE '{LedgerAccounts.OpeningEquity}' END,
                       abs(c.opening_cents)
                FROM chunk_account c
                WHERE NOT EXISTS (SELECT 1 FROM account a WHERE a.account_id = c.account_id)
                """,
                batchId));
            batch.BatchCommands.Add(Statement(
                """
                INSERT INTO account (account_id, name, type, status, balance_cents, last_txn_date, last_batch_id)
                SELECT account_id, name, type, status, closing_cents, last_txn_date, $1 FROM chunk_account
                ON CONFLICT (account_id) DO UPDATE SET
                    name = EXCLUDED.name, type = EXCLUDED.type, status = EXCLUDED.status,
                    balance_cents = EXCLUDED.balance_cents, last_txn_date = EXCLUDED.last_txn_date,
                    last_batch_id = EXCLUDED.last_batch_id
                """,
                batchId));
            batch.BatchCommands.Add(Statement(
                """
                INSERT INTO batch_account (batch_id, account_id, opening_cents, closing_cents, applied_count)
                SELECT $1, account_id, opening_cents, closing_cents, applied_count FROM chunk_account
                """,
                batchId));
            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var checkpoint = new NpgsqlCommand(
            """
            UPDATE batch SET last_account_id = $2, chunks_committed = chunks_committed + 1
            WHERE id = $1 AND status = 'POSTING' AND last_account_id IS NOT DISTINCT FROM $3
            """,
            connection,
            transaction))
        {
            checkpoint.Parameters.AddWithValue(batchId);
            checkpoint.Parameters.AddWithValue(chunk.LastKey);
            checkpoint.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)expectedCheckpoint ?? DBNull.Value });
            if (await checkpoint.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new LedgerIntegrityException($"Batch {batchId} checkpoint moved unexpectedly. Another writer may be active.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Recomputes the batch's figures from what's actually stored, not from what was sent.</summary>
    public static async Task<BatchFigures> ReadStoredFiguresAsync(NpgsqlConnection connection, long batchId, CancellationToken cancellationToken)
    {
        long opening, closing, accounts;
        await using (var command = new NpgsqlCommand(
            "SELECT coalesce(sum(opening_cents), 0)::bigint, coalesce(sum(closing_cents), 0)::bigint, count(*) FROM batch_account WHERE batch_id = $1",
            connection))
        {
            command.Parameters.AddWithValue(batchId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            opening = reader.GetInt64(0);
            closing = reader.GetInt64(1);
            accounts = reader.GetInt64(2);
        }

        long deposits = 0, interest = 0, withdrawals = 0, applied = 0;
        await using (var command = new NpgsqlCommand(
            "SELECT kind, count(*), sum(amount_cents)::bigint FROM ledger_entry WHERE batch_id = $1 AND kind <> 'OPEN' GROUP BY kind",
            connection))
        {
            command.Parameters.AddWithValue(batchId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applied += reader.GetInt64(1);
                switch (reader.GetString(0))
                {
                    case "DEP":
                        deposits = reader.GetInt64(2);
                        break;
                    case "WDR":
                        withdrawals = reader.GetInt64(2);
                        break;
                    default:
                        interest = reader.GetInt64(2);
                        break;
                }
            }
        }

        await using var rejects = new NpgsqlCommand("SELECT count(*) FROM rejected_transaction WHERE batch_id = $1", connection);
        rejects.Parameters.AddWithValue(batchId);
        long rejected = (long)(await rejects.ExecuteScalarAsync(cancellationToken))!;

        return new BatchFigures(opening, deposits, interest, withdrawals, closing, accounts, applied, rejected);
    }

    /// <summary>Counts accounts whose stored balance differs from the sum of their ledger entries.</summary>
    public static async Task<long> CountDriftedAccountsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH movement AS (
                SELECT account, sum(delta)::bigint AS balance FROM (
                    SELECT credit_account AS account, amount_cents AS delta FROM ledger_entry
                    UNION ALL
                    SELECT debit_account, -amount_cents FROM ledger_entry
                ) sides GROUP BY account
            )
            SELECT count(*) FROM account a LEFT JOIN movement m ON m.account = a.account_id
            WHERE a.balance_cents <> coalesce(m.balance, 0)
            """,
            connection);
        command.CommandTimeout = 0;
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public static async Task FinishBatchAsync(
        NpgsqlConnection connection, long batchId, string status, IReadOnlyList<BatchBreak> breaks, CancellationToken cancellationToken)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertBreaksAsync(connection, transaction, batchId, breaks, cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE batch SET status = $2, finished_at = now() WHERE id = $1 AND status = 'POSTING'", connection, transaction);
        command.Parameters.AddWithValue(batchId);
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static NpgsqlBatchCommand Statement(string sql, long batchId)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue(batchId);
        return command;
    }

    private static async Task InsertBreaksAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long batchId, IReadOnlyList<BatchBreak> breaks, CancellationToken cancellationToken)
    {
        for (int i = 0; i < breaks.Count; i++)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO batch_break (batch_id, seq, check_name, detail) VALUES ($1, $2, $3, $4)", connection, transaction);
            command.Parameters.AddWithValue(batchId);
            command.Parameters.AddWithValue(i + 1);
            command.Parameters.AddWithValue(breaks[i].Check);
            command.Parameters.AddWithValue(breaks[i].Detail);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CopyEntriesAsync(NpgsqlConnection connection, long batchId, List<EntryRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY ledger_entry (batch_id, entry_key, kind, debit_account, credit_account, amount_cents, txn_date) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        foreach (EntryRow row in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(batchId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(row.Key, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Kind, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Debit, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Credit, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.AmountCents, NpgsqlDbType.Bigint, cancellationToken);
            await WriteDateAsync(writer, row.TxnDate, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static async Task CopyRejectsAsync(NpgsqlConnection connection, long batchId, List<RejectRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY rejected_transaction (batch_id, txn_key, reason_code, reason_text, raw_record) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        foreach (RejectRow row in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(batchId, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(row.Key, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Code, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Text, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Raw, NpgsqlDbType.Text, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static async Task CopyChunkAccountsAsync(NpgsqlConnection connection, List<AccountRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        await using NpgsqlBinaryImporter writer = await connection.BeginBinaryImportAsync(
            "COPY chunk_account (account_id, name, type, status, opening_cents, closing_cents, last_txn_date, applied_count) FROM STDIN (FORMAT BINARY)",
            cancellationToken);
        foreach (AccountRow row in rows)
        {
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(row.Id, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Name, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Type, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Status, NpgsqlDbType.Text, cancellationToken);
            await writer.WriteAsync(row.Opening, NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(row.Closing, NpgsqlDbType.Bigint, cancellationToken);
            await WriteDateAsync(writer, row.LastTxnDate, cancellationToken);
            await writer.WriteAsync(row.Applied, NpgsqlDbType.Integer, cancellationToken);
        }

        await writer.CompleteAsync(cancellationToken);
    }

    private static async Task WriteDateAsync(NpgsqlBinaryImporter writer, DateOnly? date, CancellationToken cancellationToken)
    {
        if (date is { } value)
        {
            await writer.WriteAsync(value, NpgsqlDbType.Date, cancellationToken);
        }
        else
        {
            await writer.WriteNullAsync(cancellationToken);
        }
    }
}

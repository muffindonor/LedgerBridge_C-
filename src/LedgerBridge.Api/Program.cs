using LedgerBridge.Core.Ledger;
using Npgsql;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is not set.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<LedgerStore>();

WebApplication app = builder.Build();

await app.Services.GetRequiredService<LedgerStore>().EnsureSchemaAsync(CancellationToken.None);

const int MaxPageSize = 200;

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/batches", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    await using NpgsqlCommand command = db.CreateCommand(
        "SELECT id, name, status, chunks_committed, created_at, finished_at FROM batch ORDER BY id DESC LIMIT 100");
    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
    var batches = new List<object>();
    while (await reader.ReadAsync(ct))
    {
        batches.Add(new
        {
            id = reader.GetInt64(0),
            name = reader.GetString(1),
            status = reader.GetString(2),
            chunksCommitted = reader.GetInt32(3),
            createdAt = reader.GetDateTime(4),
            finishedAt = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
        });
    }

    return Results.Ok(batches);
});

app.MapGet("/batches/{id:long}", async (long id, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using NpgsqlConnection connection = await db.OpenConnectionAsync(ct);
    string name, status;
    await using (var batch = new NpgsqlCommand("SELECT name, status FROM batch WHERE id = $1", connection))
    {
        batch.Parameters.AddWithValue(id);
        await using NpgsqlDataReader reader = await batch.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return Results.NotFound();
        }

        name = reader.GetString(0);
        status = reader.GetString(1);
    }

    var breaks = new List<object>();
    await using (var command = new NpgsqlCommand("SELECT check_name, detail FROM batch_break WHERE batch_id = $1 ORDER BY seq", connection))
    {
        command.Parameters.AddWithValue(id);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            breaks.Add(new { check = reader.GetString(0), detail = reader.GetString(1) });
        }
    }

    var figures = await LedgerStore.ReadStoredFiguresAsync(connection, id, ct);
    return Results.Ok(new { id, name, status, figures, breaks });
});

app.MapGet("/accounts/{id}", async (string id, NpgsqlDataSource db, CancellationToken ct) =>
{
    await using NpgsqlCommand command = db.CreateCommand(
        "SELECT account_id, name, type, status, balance_cents, last_txn_date FROM account WHERE account_id = $1");
    command.Parameters.AddWithValue(id);
    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct))
    {
        return Results.NotFound();
    }

    return Results.Ok(new
    {
        id = reader.GetString(0),
        name = reader.GetString(1),
        type = reader.GetString(2),
        status = reader.GetString(3),
        balanceCents = reader.GetInt64(4),
        lastTxnDate = reader.IsDBNull(5) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(5),
    });
});

// Keyset paging: the cost of a page stays the same however deep the statement goes.
app.MapGet("/accounts/{id}/entries", async (string id, long? afterBatch, string? afterKey, int? limit, NpgsqlDataSource db, CancellationToken ct) =>
{
    int pageSize = Math.Clamp(limit ?? 50, 1, MaxPageSize);
    await using NpgsqlCommand command = db.CreateCommand(
        """
        SELECT batch_id, entry_key, kind, debit_account, credit_account, amount_cents, txn_date
        FROM ledger_entry
        WHERE (debit_account = $1 OR credit_account = $1)
          AND (batch_id, entry_key) > ($2, $3)
        ORDER BY batch_id, entry_key
        LIMIT $4
        """);
    command.Parameters.AddWithValue(id);
    command.Parameters.AddWithValue(afterBatch ?? 0L);
    command.Parameters.AddWithValue(afterKey ?? string.Empty);
    command.Parameters.AddWithValue(pageSize);
    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(ct);
    var entries = new List<object>();
    while (await reader.ReadAsync(ct))
    {
        bool credited = reader.GetString(4) == id;
        long amount = reader.GetInt64(5);
        entries.Add(new
        {
            batchId = reader.GetInt64(0),
            entryKey = reader.GetString(1),
            kind = reader.GetString(2),
            signedAmountCents = credited ? amount : -amount,
            counterAccount = credited ? reader.GetString(3) : reader.GetString(4),
            txnDate = reader.IsDBNull(6) ? (DateOnly?)null : reader.GetFieldValue<DateOnly>(6),
        });
    }

    return Results.Ok(entries);
});

await app.RunAsync();

public partial class Program;

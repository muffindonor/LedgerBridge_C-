using LedgerBridge.Core.Batch;
using LedgerBridge.Core.Ledger;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace LedgerBridge.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}

/// <summary>One real Postgres container, with a new empty database for each test.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<TestLedger> CreateLedgerAsync()
    {
        string name = "t" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", admin);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name };
        var ledger = new TestLedger(builder.ConnectionString);
        await new LedgerStore(ledger.Db).EnsureSchemaAsync(CancellationToken.None);
        return ledger;
    }
}

public sealed class TestLedger(string connectionString) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public NpgsqlDataSource Db { get; } = NpgsqlDataSource.Create(connectionString);

    public Task<BatchOutcome> ProcessAsync(string directory, ProcessorOptions? options = null) =>
        new BatchProcessor(Db, options ?? new ProcessorOptions { GroupsPerChunk = 20 }, NullLogger<BatchProcessor>.Instance)
            .ProcessAsync(new BatchFiles(directory), CancellationToken.None);

    public async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlCommand command = Db.CreateCommand(sql);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string?> TextAsync(string sql)
    {
        await using NpgsqlCommand command = Db.CreateCommand(sql);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using NpgsqlCommand command = Db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, long>> BalancesAsync()
    {
        var balances = new Dictionary<string, long>(StringComparer.Ordinal);
        await using NpgsqlCommand command = Db.CreateCommand("SELECT account_id, balance_cents FROM account");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            balances[reader.GetString(0)] = reader.GetInt64(1);
        }

        return balances;
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

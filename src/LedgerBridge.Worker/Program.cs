using LedgerBridge.Core.Ledger;
using LedgerBridge.Worker;
using Npgsql;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// The 30 second default suits web requests. Totals over a full night's batch can take longer.
var connectionString = new NpgsqlConnectionStringBuilder(
    builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is not set."))
{
    CommandTimeout = 600,
}.ConnectionString;

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<LedgerStore>();
builder.Services.AddSingleton<BatchProcessor>();
builder.Services.Configure<InboxOptions>(builder.Configuration.GetSection("Inbox"));
builder.Services.AddSingleton(_ =>
{
    var options = new ProcessorOptions();
    builder.Configuration.GetSection("Processor").Bind(options);
    return options;
});
builder.Services.AddHostedService<InboxWorker>();

await builder.Build().RunAsync();

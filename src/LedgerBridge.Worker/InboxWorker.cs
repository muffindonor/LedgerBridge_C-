using LedgerBridge.Core.Batch;
using LedgerBridge.Core.Ledger;
using Microsoft.Extensions.Options;

namespace LedgerBridge.Worker;

public sealed class InboxOptions
{
    public string Path { get; set; } = "inbox";

    public string ArchivePath { get; set; } = "archive";

    public string RejectedPath { get; set; } = "rejected";

    public int PollSeconds { get; set; } = 10;

    /// <summary>Process what's ready, then exit. For cron-style runs and the crash test.</summary>
    public bool Once { get; set; }
}

/// <summary>Picks up ready batches in name order, because each night builds on the one before.</summary>
public sealed class InboxWorker(
    BatchProcessor processor,
    LedgerStore store,
    IOptions<InboxOptions> inboxOptions,
    IHostApplicationLifetime lifetime,
    ILogger<InboxWorker> logger) : BackgroundService
{
    private const int ExitFailure = 1;
    private const int ExitHalted = 2;

    private readonly InboxOptions _options = inboxOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool schemaReady = false;
        try
        {
            Directory.CreateDirectory(_options.Path);

            while (!stoppingToken.IsCancellationRequested)
            {
                bool halted = false;
                try
                {
                    if (!schemaReady)
                    {
                        await store.EnsureSchemaAsync(stoppingToken);
                        schemaReady = true;
                    }

                    halted = await DrainInboxAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !_options.Once)
                {
                    // Every step is safe to repeat, so a passing failure is retried on the next poll.
                    logger.LogError(ex, "Inbox scan failed, will retry.");
                }

                if (_options.Once)
                {
                    Environment.ExitCode = halted ? ExitHalted : 0;
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.PollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown. Committed chunks are safe and the next start resumes after them.
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Worker failed.");
            Environment.ExitCode = ExitFailure;
        }
        finally
        {
            // Without this, a fatal error would leave a process that looks alive and does nothing.
            lifetime.StopApplication();
        }
    }

    /// <summary>Returns true when a batch needs attention and later ones mustn't run.</summary>
    private async Task<bool> DrainInboxAsync(CancellationToken cancellationToken)
    {
        IEnumerable<BatchFiles> ready = Directory.GetDirectories(_options.Path)
            .Order(StringComparer.Ordinal)
            .Select(directory => new BatchFiles(directory))
            .Where(files => files.IsReady);

        foreach (BatchFiles files in ready)
        {
            BatchOutcome outcome = await processor.ProcessAsync(files, cancellationToken);
            switch (outcome)
            {
                case BatchOutcome.Reconciled or BatchOutcome.AlreadyProcessed:
                    Move(files, _options.ArchivePath);
                    break;
                case BatchOutcome.Rejected:
                    Move(files, _options.RejectedPath);
                    return true;
                default:
                    return true;
            }
        }

        return false;
    }

    private void Move(BatchFiles files, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        string destination = Path.Combine(destinationRoot, files.Name);
        if (Directory.Exists(destination))
        {
            destination += "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        }

        Directory.Move(files.Directory, destination);
        logger.LogInformation("Batch {Batch}: moved to {Destination}.", files.Name, destination);
    }
}

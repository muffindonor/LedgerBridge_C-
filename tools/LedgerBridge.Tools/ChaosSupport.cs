using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace LedgerBridge.Tools;

/// <summary>
/// The answer key for the crash tests. It reads the COBOL files with plain string slicing,
/// so a bug in the decoder or the replay engine can't also hide itself here.
/// </summary>
public sealed class ExpectedBatch
{
    public Dictionary<string, long> Opening { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, long> Closing { get; } = new(StringComparer.Ordinal);

    public long Applied { get; private set; }

    public long Rejected { get; private set; }

    public static ExpectedBatch Load(string directory)
    {
        var expected = new ExpectedBatch();
        foreach (string line in File.ReadLines(Path.Combine(directory, "ACCTMAST.DAT")))
        {
            expected.Opening[line[..10]] = long.Parse(line.Substring(37, 14), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        foreach (string line in File.ReadLines(Path.Combine(directory, "ACCTMAST.NEW")))
        {
            expected.Closing[line[..10]] = long.Parse(line.Substring(37, 14), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }

        expected.Rejected = File.ReadLines(Path.Combine(directory, "REJECTS.DAT")).LongCount();
        expected.Applied = File.ReadLines(Path.Combine(directory, "TRANS.DAT")).LongCount() - expected.Rejected;
        return expected;
    }
}

public static class ChaosSupport
{
    public static Process Start(string dll, params string[] arguments)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(dll);
        foreach (string argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the process.");
        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Hard-kills the process, as a power cut would. It gets no chance to clean up.</summary>
    public static async Task<bool> KillAfterAsync(Process process, TimeSpan delay)
    {
        using var timeout = new CancellationTokenSource(delay);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return false;
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            return true;
        }
    }

    public static async Task ExecuteAsync(NpgsqlDataSource db, string sql)
    {
        await using NpgsqlCommand command = db.CreateCommand(sql);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<long> ScalarAsync(NpgsqlDataSource db, string sql)
    {
        await using NpgsqlCommand command = db.CreateCommand(sql);
        command.CommandTimeout = 0;
        object? value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}

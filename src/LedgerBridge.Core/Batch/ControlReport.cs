using System.Globalization;
using LedgerBridge.Cobol;

namespace LedgerBridge.Core.Batch;

/// <summary>A failed check. Batches that have any of these are never posted.</summary>
public sealed record BatchBreak(string Check, string Detail);

/// <summary>The batch can't be read at all, for example bad data or files out of sequence.</summary>
public sealed class BatchRejectedException(string check, string detail) : Exception($"{check}: {detail}")
{
    public string Check { get; } = check;

    public string Detail { get; } = detail;
}

/// <summary>The control totals from the end of the COBOL job's REPORT.TXT.</summary>
public sealed record ControlReport(
    long Opening,
    long Deposits,
    long Interest,
    long Withdrawals,
    long ExpectedClosing,
    long ActualClosing,
    long AccountsRead,
    long AccountsWritten,
    long TransactionsRead,
    long Applied,
    long Rejected,
    bool InBalance)
{
    public static ControlReport Parse(string path)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        bool inTotals = false;
        string? lastLine = null;

        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            lastLine = line;
            if (line == "CONTROL TOTALS")
            {
                inTotals = true;
            }
            else if (inTotals && line.LastIndexOf(' ') is var split and > 0 && TryParseValue(line[(split + 1)..], out long parsed))
            {
                values[line[..split].TrimEnd()] = parsed;
            }
        }

        if (!inTotals)
        {
            throw new BatchRejectedException("REPORT_FORMAT", "REPORT.TXT has no CONTROL TOTALS section.");
        }

        return new ControlReport(
            Value("OPENING BALANCE"),
            Value("DEPOSITS"),
            Value("INTEREST"),
            Value("WITHDRAWALS"),
            Value("EXPECTED CLOSING"),
            Value("ACTUAL CLOSING"),
            Value("ACCOUNTS READ"),
            Value("ACCOUNTS WRITTEN"),
            Value("TRANSACTIONS READ"),
            Value("TRANSACTIONS APPLIED"),
            Value("TRANSACTIONS REJECTED"),
            lastLine == "IN BALANCE");

        long Value(string label) => values.TryGetValue(label, out long value)
            ? value
            : throw new BatchRejectedException("REPORT_FORMAT", $"REPORT.TXT has no '{label}' line.");
    }

    /// <summary>Amounts become cents and counts stay whole, so every value is an exact integer.</summary>
    private static bool TryParseValue(string text, out long value)
    {
        string digits = text.Replace(",", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal);
        return long.TryParse(digits, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>The figures a batch produced, however they were computed.</summary>
public sealed record BatchFigures(
    long Opening,
    long Deposits,
    long Interest,
    long Withdrawals,
    long Closing,
    long Accounts,
    long Applied,
    long Rejected);

public static class ReportCheck
{
    /// <summary>Compares computed figures against the control totals COBOL printed.</summary>
    public static IReadOnlyList<BatchBreak> Compare(BatchFigures figures, ControlReport report)
    {
        var breaks = new List<BatchBreak>();
        if (!report.InBalance)
        {
            breaks.Add(new BatchBreak("REPORT_NOT_IN_BALANCE", "The COBOL report does not end with IN BALANCE."));
        }

        Check("OPENING_TOTAL", figures.Opening, report.Opening);
        Check("DEPOSIT_TOTAL", figures.Deposits, report.Deposits);
        Check("INTEREST_TOTAL", figures.Interest, report.Interest);
        Check("WITHDRAWAL_TOTAL", figures.Withdrawals, report.Withdrawals);
        Check("EXPECTED_CLOSING", figures.Opening + figures.Deposits + figures.Interest - figures.Withdrawals, report.ExpectedClosing);
        Check("ACTUAL_CLOSING", figures.Closing, report.ActualClosing);
        Check("ACCOUNTS_READ", figures.Accounts, report.AccountsRead);
        Check("ACCOUNTS_WRITTEN", figures.Accounts, report.AccountsWritten);
        Check("TRANSACTIONS_READ", figures.Applied + figures.Rejected, report.TransactionsRead);
        Check("TRANSACTIONS_APPLIED", figures.Applied, report.Applied);
        Check("TRANSACTIONS_REJECTED", figures.Rejected, report.Rejected);
        return breaks;

        void Check(string name, long actual, long expected)
        {
            if (actual != expected)
            {
                breaks.Add(new BatchBreak(name, $"Computed {actual}, the COBOL report says {expected}."));
            }
        }
    }
}

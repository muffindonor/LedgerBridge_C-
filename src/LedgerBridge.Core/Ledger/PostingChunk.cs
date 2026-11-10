using System.Text;
using System.Threading.Channels;
using LedgerBridge.Core.Batch;

namespace LedgerBridge.Core.Ledger;

public readonly record struct EntryRow(string Key, string Kind, string Debit, string Credit, long AmountCents, DateOnly? TxnDate);

public readonly record struct RejectRow(string Key, string Code, string Text, string Raw);

public readonly record struct AccountRow(string Id, string Name, string Type, string Status, long Opening, long Closing, DateOnly? LastTxnDate, int Applied);

/// <summary>One unit of work: committed all together, along with its checkpoint.</summary>
public sealed class PostingChunk(string lastKey, List<EntryRow> entries, List<RejectRow> rejects, List<AccountRow> accounts)
{
    public string LastKey { get; } = lastKey;

    public List<EntryRow> Entries { get; } = entries;

    public List<RejectRow> Rejects { get; } = rejects;

    public List<AccountRow> Accounts { get; } = accounts;
}

public static class LedgerAccounts
{
    // Longer than any 10-character COBOL account id, so they can't collide with one.
    public const string Cash = "#CASH-CLEARING";
    public const string InterestExpense = "#INTEREST-EXPENSE";
    public const string OpeningEquity = "#OPENING-EQUITY";
}

/// <summary>Collects replay output into chunks, ending each one at an account boundary.</summary>
public sealed class PostingSink(int groupsPerChunk, ChannelWriter<PostingChunk> output, CancellationToken cancellationToken)
    : IReplaySink
{
    private List<EntryRow> _entries = [];
    private List<RejectRow> _rejects = [];
    private List<AccountRow> _accounts = [];
    private int _groups;
    private string? _lastKey;

    public void Entry(EntryKind kind, ReadOnlySpan<byte> accountId, ReadOnlySpan<byte> entryKey, long amountCents, int txnDate)
    {
        string account = Id(accountId);
        (string name, string debit, string credit) = kind switch
        {
            EntryKind.Deposit => ("DEP", LedgerAccounts.Cash, account),
            EntryKind.Withdrawal => ("WDR", account, LedgerAccounts.Cash),
            _ => ("INT", LedgerAccounts.InterestExpense, account),
        };
        _entries.Add(new EntryRow(Encoding.Latin1.GetString(entryKey), name, debit, credit, amountCents, LedgerDates.ToDate(txnDate)));
    }

    public void Reject(ReadOnlySpan<byte> rejectRecord)
    {
        RejectFields fields = BatchLayouts.Reject;
        ReadOnlySpan<byte> txn = fields.Transaction.Bytes(rejectRecord);
        _rejects.Add(new RejectRow(
            Encoding.Latin1.GetString(BatchLayouts.Transaction.Key.Bytes(txn)),
            fields.Code.ReadText(rejectRecord),
            fields.Text.ReadText(rejectRecord),
            Encoding.Latin1.GetString(txn)));
    }

    public void Account(in AccountResult account) =>
        _accounts.Add(new AccountRow(
            Id(account.Id),
            Encoding.Latin1.GetString(account.Name).TrimEnd(' '),
            ((char)account.Type).ToString(),
            ((char)account.Status).ToString(),
            account.Opening,
            account.Closing,
            LedgerDates.ToDate(account.LastTxnDate),
            account.Applied));

    public void GroupEnd(ReadOnlySpan<byte> key)
    {
        _groups++;
        _lastKey = Encoding.Latin1.GetString(key);
        if (_groups >= groupsPerChunk)
        {
            Flush();
        }
    }

    public void Break(BatchBreak failure) =>
        throw new InvalidOperationException($"Validated batch failed during posting. Files changed? {failure.Check}: {failure.Detail}");

    public void Flush()
    {
        if (_groups == 0)
        {
            return;
        }

        var chunk = new PostingChunk(_lastKey!, _entries, _rejects, _accounts);
        _entries = [];
        _rejects = [];
        _accounts = [];
        _groups = 0;

        // This runs on a dedicated thread, so blocking here is how backpressure reaches the file reads.
        output.WriteAsync(chunk, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    private static string Id(ReadOnlySpan<byte> id) => Encoding.Latin1.GetString(id).TrimEnd(' ');
}

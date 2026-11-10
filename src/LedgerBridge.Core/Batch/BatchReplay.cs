using LedgerBridge.Cobol;

namespace LedgerBridge.Core.Batch;

public enum EntryKind
{
    Deposit,
    Withdrawal,
    Interest,
}

/// <summary>Receives the results of a replay. Spans are only valid during the call.</summary>
public interface IReplaySink
{
    void Entry(EntryKind kind, ReadOnlySpan<byte> accountId, ReadOnlySpan<byte> entryKey, long amountCents, int txnDate);

    void Reject(ReadOnlySpan<byte> rejectRecord);

    void Account(in AccountResult account);

    /// <summary>Everything for this key is done, so it's a safe place to commit and to resume from.</summary>
    void GroupEnd(ReadOnlySpan<byte> key);

    void Break(BatchBreak failure);
}

public readonly ref struct AccountResult
{
    public required ReadOnlySpan<byte> Id { get; init; }

    public required ReadOnlySpan<byte> Name { get; init; }

    public required byte Type { get; init; }

    public required byte Status { get; init; }

    public required long Opening { get; init; }

    public required long Closing { get; init; }

    public required long LastTxnDate { get; init; }

    public required int Applied { get; init; }
}

/// <summary>
/// Replays one night's run with the same balanced-line merge the COBOL job used.
/// The files don't record the interest each account earned, so it has to be recomputed.
/// Every account's replayed balance is then proved against the COBOL job's new master.
/// All four files are sorted, so memory use doesn't grow with the size of the batch.
/// </summary>
public sealed class BatchReplay(BatchFiles files, IReplaySink sink)
{
    private const int TxnKeyLength = 16;
    private const int MonthlyRateBasisPoints = 25;
    private const int BasisPointsDivisor = 10_000;

    private static readonly TransactionFields Txn = BatchLayouts.Transaction;
    private static readonly RejectFields Rej = BatchLayouts.Reject;
    private static readonly MasterFields Mast = BatchLayouts.Master;

    /// <summary>
    /// Runs the replay. When <paramref name="resumeAfter"/> is set, every check and total still
    /// covers the whole batch, but the sink hears nothing for keys at or below it.
    /// </summary>
    public BatchFigures Run(string? resumeAfter, CancellationToken cancellationToken)
    {
        using RecordReader oldMaster = files.OpenOldMaster();
        using RecordReader newMaster = files.OpenNewMaster();
        using RecordReader transactions = files.OpenTransactions();
        using RecordReader rejects = files.OpenRejects();

        try
        {
            return Merge(oldMaster, newMaster, transactions, rejects, resumeAfter, cancellationToken);
        }
        catch (RecordFormatException ex)
        {
            throw new BatchRejectedException(
                "RECORD_FORMAT",
                $"{ex.Message} (at master record {oldMaster.RecordNumber}, transaction record {transactions.RecordNumber})");
        }
    }

    private BatchFigures Merge(
        RecordReader oldMaster,
        RecordReader newMaster,
        RecordReader transactions,
        RecordReader rejects,
        string? resumeAfter,
        CancellationToken cancellationToken)
    {
        MasterFields newFields = files.NewMasterFields;
        int keyLength = Mast.Id.Length;
        byte[]? resumeKey = resumeAfter is null ? null : System.Text.Encoding.Latin1.GetBytes(resumeAfter);

        Span<byte> key = stackalloc byte[keyLength];
        Span<byte> previousMaster = stackalloc byte[keyLength];
        Span<byte> previousTxn = stackalloc byte[TxnKeyLength];
        bool firstMaster = true;
        bool firstTxn = true;

        bool hasMaster = oldMaster.Read();
        bool hasNew = newMaster.Read();
        bool hasTxn = transactions.Read();
        bool hasReject = rejects.Read();

        long opening = 0, deposits = 0, interest = 0, withdrawals = 0, closing = 0, accounts = 0, applied = 0, rejected = 0;
        int groupsSinceCancelCheck = 0;

        while (hasMaster || hasTxn)
        {
            if (++groupsSinceCancelCheck == 1024)
            {
                groupsSinceCancelCheck = 0;
                cancellationToken.ThrowIfCancellationRequested();
            }

            int order = !hasMaster ? 1
                : !hasTxn ? -1
                : Mast.Id.Bytes(oldMaster.Current).SequenceCompareTo(Txn.AccountId.Bytes(transactions.Current));
            bool groupHasMaster = order <= 0;

            if (groupHasMaster)
            {
                Mast.Id.Bytes(oldMaster.Current).CopyTo(key);
                if (!firstMaster && key.SequenceCompareTo(previousMaster) <= 0)
                {
                    throw new BatchRejectedException("SEQUENCE", $"Master record {oldMaster.RecordNumber} is out of sequence or a duplicate.");
                }

                key.CopyTo(previousMaster);
                firstMaster = false;
            }
            else
            {
                Txn.AccountId.Bytes(transactions.Current).CopyTo(key);
            }

            bool emit = resumeKey is null || key.SequenceCompareTo(resumeKey) > 0;
            long balance = groupHasMaster ? Mast.Balance.ReadInt64(oldMaster.Current) : 0;
            long accountOpening = balance;
            int accountApplied = 0;

            while (hasTxn && Txn.AccountId.Bytes(transactions.Current).SequenceEqual(key))
            {
                ReadOnlySpan<byte> txn = transactions.Current;
                ReadOnlySpan<byte> txnKey = Txn.Key.Bytes(txn);
                if (!firstTxn && txnKey.SequenceCompareTo(previousTxn) <= 0)
                {
                    throw new BatchRejectedException("SEQUENCE", $"Transaction record {transactions.RecordNumber} is out of sequence or a duplicate.");
                }

                txnKey.CopyTo(previousTxn);
                firstTxn = false;

                // A reject that sorts before this transaction can never match one.
                while (hasReject && Rej.Transaction.Bytes(rejects.Current)[..TxnKeyLength].SequenceCompareTo(txnKey) < 0)
                {
                    sink.Break(new BatchBreak("REJECT_NOT_IN_TRANSACTIONS", $"Reject record {rejects.RecordNumber} matches no transaction."));
                    hasReject = rejects.Read();
                }

                bool sameKey = hasReject && Rej.Transaction.Bytes(rejects.Current)[..TxnKeyLength].SequenceEqual(txnKey);
                if (sameKey)
                {
                    if (!Rej.Transaction.Bytes(rejects.Current).SequenceEqual(txn))
                    {
                        sink.Break(new BatchBreak("REJECT_ALTERED", $"Reject record {rejects.RecordNumber} differs from its transaction."));
                    }

                    bool unknownAccountCode = Rej.Code.Bytes(rejects.Current).SequenceEqual("R1"u8);
                    if (unknownAccountCode == groupHasMaster)
                    {
                        sink.Break(new BatchBreak("REJECT_CODE_MISMATCH", $"Reject record {rejects.RecordNumber} disagrees with the master about whether the account exists."));
                    }

                    rejected++;
                    if (emit)
                    {
                        sink.Reject(rejects.Current);
                    }

                    hasReject = rejects.Read();
                }
                else if (!groupHasMaster)
                {
                    sink.Break(new BatchBreak("APPLIED_TO_UNKNOWN_ACCOUNT", $"Transaction record {transactions.RecordNumber} was not rejected, but its account is not on the master."));
                }
                else
                {
                    ApplyTransaction(txn, transactions.RecordNumber, ref balance, out EntryKind kind, out long amount, out int date);
                    switch (kind)
                    {
                        case EntryKind.Deposit:
                            deposits += amount;
                            break;
                        case EntryKind.Withdrawal:
                            withdrawals += amount;
                            break;
                        default:
                            interest += amount;
                            break;
                    }

                    applied++;
                    accountApplied++;
                    if (emit)
                    {
                        sink.Entry(kind, key, txnKey, amount, date);
                    }
                }

                hasTxn = transactions.Read();
            }

            if (groupHasMaster)
            {
                if (!hasNew || !newFields.Id.Bytes(newMaster.Current).SequenceEqual(key))
                {
                    throw new BatchRejectedException("NEW_MASTER_MISMATCH", $"The new master has no record for old master record {oldMaster.RecordNumber}.");
                }

                long accountClosing = newFields.Balance.ReadInt64(newMaster.Current);
                if (accountClosing != balance)
                {
                    sink.Break(new BatchBreak(
                        "BALANCE_MISMATCH",
                        $"Account {System.Text.Encoding.Latin1.GetString(key)}: replay gives {balance}, the new master says {accountClosing}."));
                }

                accounts++;
                opening += accountOpening;
                closing += accountClosing;

                if (emit)
                {
                    sink.Account(new AccountResult
                    {
                        Id = key,
                        Name = Mast.Name.Bytes(oldMaster.Current),
                        Type = Mast.Type.Bytes(oldMaster.Current)[0],
                        Status = Mast.Status.Bytes(oldMaster.Current)[0],
                        Opening = accountOpening,
                        Closing = accountClosing,
                        LastTxnDate = newFields.LastTxnDate.ReadInt64(newMaster.Current),
                        Applied = accountApplied,
                    });
                }

                hasNew = newMaster.Read();
                hasMaster = oldMaster.Read();
            }

            if (emit)
            {
                sink.GroupEnd(key);
            }
        }

        while (hasReject)
        {
            sink.Break(new BatchBreak("REJECT_NOT_IN_TRANSACTIONS", $"Reject record {rejects.RecordNumber} matches no transaction."));
            hasReject = rejects.Read();
        }

        if (hasNew)
        {
            sink.Break(new BatchBreak("NEW_MASTER_MISMATCH", "The new master has more records than the old master."));
        }

        return new BatchFigures(opening, deposits, interest, withdrawals, closing, accounts, applied, rejected);
    }

    private static void ApplyTransaction(
        ReadOnlySpan<byte> txn, long recordNumber, ref long balance, out EntryKind kind, out long amount, out int date)
    {
        ReadOnlySpan<byte> code = Txn.Code.Bytes(txn);
        if (code.SequenceEqual("DEP"u8))
        {
            kind = EntryKind.Deposit;
            amount = Txn.Amount.ReadInt64(txn);
            balance += amount;
        }
        else if (code.SequenceEqual("WDR"u8))
        {
            kind = EntryKind.Withdrawal;
            amount = Txn.Amount.ReadInt64(txn);
            balance -= amount;
        }
        else if (code.SequenceEqual("INT"u8))
        {
            // Same rule as BANKBTCH: COMPUTE ... ROUNDED, at 0.25% a month.
            kind = EntryKind.Interest;
            amount = ((balance * MonthlyRateBasisPoints) + (BasisPointsDivisor / 2)) / BasisPointsDivisor;
            balance += amount;
        }
        else
        {
            throw new BatchRejectedException("INVALID_APPLIED_TRANSACTION", $"Transaction record {recordNumber} has an invalid code but was not rejected.");
        }

        date = checked((int)Txn.Date.ReadInt64(txn));
        if (LedgerDates.ToDate(date) is null)
        {
            throw new BatchRejectedException("INVALID_APPLIED_TRANSACTION", $"Transaction record {recordNumber} has an invalid date but was not rejected.");
        }
    }
}

public static class LedgerDates
{
    /// <summary>Converts a YYYYMMDD integer, returning null when it isn't a real date.</summary>
    public static DateOnly? ToDate(long yyyymmdd)
    {
        int year = (int)(yyyymmdd / 10_000);
        int month = (int)(yyyymmdd / 100 % 100);
        int day = (int)(yyyymmdd % 100);
        return year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }
}

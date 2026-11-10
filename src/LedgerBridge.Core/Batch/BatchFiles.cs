using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using LedgerBridge.Cobol;

namespace LedgerBridge.Core.Batch;

/// <summary>The files one nightly COBOL run delivers, in one directory.</summary>
public sealed class BatchFiles(string directory)
{
    private const string ReadyMarker = "_READY";

    public string Directory { get; } = directory;

    public string Name => Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory));

    public string OldMaster => Path.Combine(Directory, "ACCTMAST.DAT");

    public string Transactions => Path.Combine(Directory, "TRANS.DAT");

    public string Rejects => Path.Combine(Directory, "REJECTS.DAT");

    public string NewMaster => Path.Combine(Directory, "ACCTMAST.NEW");

    /// <summary>Optional packed form of the new master. Used in place of ACCTMAST.NEW when present.</summary>
    public string PackedNewMaster => Path.Combine(Directory, "ACCTMAST.PCK");

    public string Report => Path.Combine(Directory, "REPORT.TXT");

    /// <summary>The sender creates this marker last, so a half-copied batch is never picked up.</summary>
    public bool IsReady => File.Exists(Path.Combine(Directory, ReadyMarker));

    public bool HasPackedNewMaster => File.Exists(PackedNewMaster);

    private string EffectiveNewMaster => HasPackedNewMaster ? PackedNewMaster : NewMaster;

    public IReadOnlyList<string> MissingFiles() =>
        new[] { OldMaster, Transactions, Rejects, EffectiveNewMaster, Report }
            .Where(path => !File.Exists(path))
            .Select(path => Path.GetFileName(path))
            .ToList();

    /// <summary>Identifies a batch by content, so a re-delivery under another name is still recognized.</summary>
    public byte[] ComputeHash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1 << 20];
        foreach (string path in new[] { OldMaster, Transactions, Rejects, EffectiveNewMaster, Report })
        {
            hash.AppendData(Encoding.ASCII.GetBytes(Path.GetFileName(path) + "\0"));
            if (!File.Exists(path))
            {
                continue;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }

        return hash.GetHashAndReset();
    }

    public RecordReader OpenOldMaster() =>
        RecordReader.OpenLineSequential(OldMaster, BatchLayouts.Master.Layout.Length);

    public RecordReader OpenNewMaster() => HasPackedNewMaster
        ? RecordReader.OpenFixed(PackedNewMaster, BatchLayouts.PackedMaster.Layout.Length)
        : RecordReader.OpenLineSequential(NewMaster, BatchLayouts.Master.Layout.Length);

    public MasterFields NewMasterFields => HasPackedNewMaster ? BatchLayouts.PackedMaster : BatchLayouts.Master;

    public RecordReader OpenTransactions() =>
        RecordReader.OpenLineSequential(Transactions, BatchLayouts.Transaction.Layout.Length);

    public RecordReader OpenRejects() =>
        RecordReader.OpenLineSequential(Rejects, BatchLayouts.Reject.Layout.Length);
}

public sealed record MasterFields(RecordLayout Layout, Field Id, Field Name, Field Type, Field Status, Field Balance, Field LastTxnDate);

public sealed record TransactionFields(RecordLayout Layout, Field Key, Field AccountId, Field Code, Field Amount, Field Date);

public sealed record RejectFields(RecordLayout Layout, Field Transaction, Field Code, Field Text);

/// <summary>Layouts come from the copybooks themselves, so C# never restates a field offset.</summary>
public static class BatchLayouts
{
    public static MasterFields Master { get; } = LoadMaster("ACCTREC.cpy", "ACCT-ID", "ACCT-NAME", "ACCT-TYPE", "ACCT-STATUS", "ACCT-BALANCE", "ACCT-LAST-TXN-DATE");

    public static MasterFields PackedMaster { get; } = LoadMaster("ACCTPACK.cpy", "PACK-ID", "PACK-NAME", "PACK-TYPE", "PACK-STATUS", "PACK-BALANCE", "PACK-LAST-TXN-DATE");

    public static TransactionFields Transaction { get; } = LoadTransaction();

    public static RejectFields Reject { get; } = LoadReject();

    private static MasterFields LoadMaster(string file, string id, string name, string type, string status, string balance, string date)
    {
        RecordLayout layout = Load(file);
        return new MasterFields(layout, layout[id], layout[name], layout[type], layout[status], layout[balance], layout[date]);
    }

    private static TransactionFields LoadTransaction()
    {
        RecordLayout layout = Load("TRANREC.cpy");
        return new TransactionFields(layout, layout["TXN-KEY"], layout["TXN-ACCT-ID"], layout["TXN-CODE"], layout["TXN-AMOUNT"], layout["TXN-DATE"]);
    }

    private static RejectFields LoadReject()
    {
        RecordLayout layout = Load("REJREC.cpy");
        return new RejectFields(layout, layout["REJ-TXN"], layout["REJ-CODE"], layout["REJ-TEXT"]);
    }

    private static RecordLayout Load(string resource)
    {
        using Stream stream = typeof(BatchLayouts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded copybook {resource} is missing.");
        using var reader = new StreamReader(stream);
        return CopybookParser.Parse(reader.ReadToEnd());
    }
}

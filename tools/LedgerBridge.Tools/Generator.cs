using System.Globalization;
using System.Text;

namespace LedgerBridge.Tools;

/// <summary>
/// Writes input files for the real COBOL job: an account master and a sorted transaction file,
/// with a small share of transactions that trigger each of its reject reasons.
/// </summary>
public static class Generator
{
    public static int Run(Arguments options)
    {
        int accounts = options.Number("accounts", 1000);
        int average = options.Number("txns-per-account", 4);
        var random = new Random(options.Number("seed", 1));
        string outDir = options.Text("out");
        string date = options.Optional("date") ?? "20261108";
        string? fromMaster = options.Optional("from-master");
        Directory.CreateDirectory(outDir);

        List<(string Id, char Type)> master = fromMaster is null
            ? WriteNewMaster(Path.Combine(outDir, "ACCTMAST.DAT"), accounts, random)
            : CopyMaster(fromMaster, Path.Combine(outDir, "ACCTMAST.DAT"));

        using var writer = new StreamWriter(Path.Combine(outDir, "TRANS.DAT"), false, Encoding.ASCII) { NewLine = "\n" };
        foreach ((string id, char type) in master)
        {
            // Account ids are even, so the odd id just below one is never on the master.
            if (random.Next(100) < 2)
            {
                string unknown = (long.Parse(id, CultureInfo.InvariantCulture) - 1).ToString(CultureInfo.InvariantCulture);
                writer.WriteLine(Line(unknown, 1, "DEP", Amount(random), date));
            }

            int count = random.Next((average * 2) + 1);
            for (int seq = 1; seq <= count; seq++)
            {
                writer.WriteLine(Transaction(id, type, seq, seq == count, date, random));
            }
        }

        Console.WriteLine($"Wrote {master.Count} accounts to {outDir}.");
        return 0;
    }

    private static string Transaction(string id, char type, int seq, bool last, string date, Random random)
    {
        int roll = random.Next(100);
        return roll switch
        {
            0 => Line(id, seq, "XXX", Amount(random), date),
            1 => Line(id, seq, "DEP", "00000000000", date),
            2 => Line(id, seq, "DEP", Amount(random), "20261399"),
            // Letters sort after digits, so only the last sequence number can be non-numeric.
            3 when last => $"{id}99999ADEP{Amount(random)}{date}",
            < 55 => Line(id, seq, "DEP", Amount(random), date),
            < 90 => Line(id, seq, "WDR", Amount(random), date),
            _ when type == 'S' => Line(id, seq, "INT", "00000000000", date),
            _ => Line(id, seq, "DEP", Amount(random), date),
        };
    }

    private static string Line(string id, int seq, string code, string amount, string date) =>
        string.Create(CultureInfo.InvariantCulture, $"{id}{seq:D6}{code}{amount}{date}");

    private static string Amount(Random random) =>
        random.NextInt64(1, 500_000).ToString("D11", CultureInfo.InvariantCulture);

    private static List<(string Id, char Type)> WriteNewMaster(string path, int accounts, Random random)
    {
        var master = new List<(string, char)>(accounts);
        using var writer = new StreamWriter(path, false, Encoding.ASCII) { NewLine = "\n" };
        for (int i = 1; i <= accounts; i++)
        {
            string id = (1_000_000_000L + (i * 2)).ToString(CultureInfo.InvariantCulture);
            char type = random.Next(2) == 0 ? 'C' : 'S';
            int statusRoll = random.Next(100);
            char status = statusRoll == 0 ? 'F' : statusRoll == 1 ? 'C' : 'A';
            string balance = random.NextInt64(0, 50_000_000).ToString("D13", CultureInfo.InvariantCulture);
            string name = string.Create(CultureInfo.InvariantCulture, $"CUSTOMER {i}").PadRight(25);
            writer.WriteLine($"{id}{name}{type}{status}+{balance}20261101");
            master.Add((id, type));
        }

        return master;
    }

    private static List<(string Id, char Type)> CopyMaster(string from, string to)
    {
        var master = new List<(string, char)>();
        using var writer = new StreamWriter(to, false, Encoding.ASCII) { NewLine = "\n" };
        foreach (string line in File.ReadLines(from))
        {
            writer.WriteLine(line);
            master.Add((line[..10], line[35]));
        }

        return master;
    }
}

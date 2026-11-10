using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LedgerBridge.Cobol;

BenchmarkRunner.Run<DecodeBenchmarks>(args: args);

/// <summary>Reads the same transaction file the fast way and the obvious way.</summary>
[MemoryDiagnoser]
public class DecodeBenchmarks
{
    private const string Copybook = """
               01  TXN-REC.
                   05  TXN-KEY.
                       10  TXN-ACCT-ID         PIC X(10).
                       10  TXN-SEQ             PIC 9(06).
                   05  TXN-CODE                PIC X(03).
                   05  TXN-AMOUNT              PIC 9(09)V99.
                   05  TXN-DATE                PIC 9(08).
        """;

    private string _path = string.Empty;
    private RecordLayout _layout = null!;

    [Params(100_000)]
    public int Records { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _layout = CopybookParser.Parse(Copybook);
        _path = Path.GetTempFileName();
        var random = new Random(1);
        using var writer = new StreamWriter(_path, false, Encoding.ASCII) { NewLine = "\n" };
        for (int i = 0; i < Records; i++)
        {
            long amount = random.NextInt64(1, 500_000);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{1_000_000_000L + i}{i % 999_999:D6}DEP{amount:D11}20261108"));
        }
    }

    [GlobalCleanup]
    public void Cleanup() => File.Delete(_path);

    [Benchmark]
    public long SpanDecoder()
    {
        Field amount = _layout["TXN-AMOUNT"];
        Field date = _layout["TXN-DATE"];
        Field sequence = _layout["TXN-SEQ"];
        long sum = 0;
        using RecordReader reader = RecordReader.OpenLineSequential(_path, _layout.Length);
        while (reader.Read())
        {
            ReadOnlySpan<byte> record = reader.Current;
            sum += amount.ReadInt64(record) + date.ReadInt64(record) + sequence.ReadInt64(record);
        }

        return sum;
    }

    [Benchmark(Baseline = true)]
    public long NaiveStrings()
    {
        long sum = 0;
        foreach (string line in File.ReadLines(_path, Encoding.ASCII))
        {
            decimal amount = decimal.Parse(line.Substring(19, 11), CultureInfo.InvariantCulture) / 100m;
            int date = int.Parse(line.Substring(30, 8), CultureInfo.InvariantCulture);
            int sequence = int.Parse(line.Substring(10, 6), CultureInfo.InvariantCulture);
            sum += (long)(amount * 100m) + date + sequence;
        }

        return sum;
    }
}

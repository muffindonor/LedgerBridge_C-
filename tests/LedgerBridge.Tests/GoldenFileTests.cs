using System.Globalization;
using LedgerBridge.Cobol;

namespace LedgerBridge.Tests;

/// <summary>
/// golden.bin was written by real GnuCOBOL from golden.txt. Expected values come from
/// plain text parsing, so the decoder is checked against the compiler and not against itself.
/// </summary>
public class GoldenFileTests
{
    [Fact]
    public void Every_format_matches_what_GnuCOBOL_wrote()
    {
        RecordLayout layout = CopybookParser.Parse(File.ReadAllText(TestPaths.Golden("GOLDREC.cpy")));
        Assert.Equal(68, layout.Length);

        string[] expected = File.ReadAllLines(TestPaths.Golden("golden.txt"));
        using RecordReader reader = RecordReader.OpenFixed(TestPaths.Golden("golden.bin"), layout.Length);

        int rows = 0;
        foreach (string line in expected)
        {
            Assert.True(reader.Read());
            ReadOnlySpan<byte> record = reader.Current;

            Assert.Equal(Parse(line, 0, 4), layout["G-CASE"].ReadInt64(record));
            Assert.Equal(Parse(line, 4, 14), layout["G-PACK-ODD"].ReadInt64(record));
            Assert.Equal(Parse(line, 18, 13), layout["G-PACK-EVEN"].ReadInt64(record));
            Assert.Equal(Parse(line, 31, 5), layout["G-PACK-UNS"].ReadInt64(record));
            Assert.Equal(Parse(line, 36, 19), layout["G-PACK-MAX"].ReadInt64(record));
            long display = Parse(line, 55, 8);
            Assert.Equal(display, layout["G-DISP-OVER"].ReadInt64(record));
            Assert.Equal(display, layout["G-DISP-LEAD"].ReadInt64(record));
            Assert.Equal(display, layout["G-DISP-TRAIL"].ReadInt64(record));
            Assert.Equal(Parse(line, 63, 5), layout["G-BIN-4"].ReadInt64(record));
            Assert.Equal(Parse(line, 68, 10), layout["G-BIN-9"].ReadInt64(record));
            Assert.Equal(Parse(line, 78, 19), layout["G-BIN-18"].ReadInt64(record));
            rows++;
        }

        Assert.False(reader.Read());
        Assert.Equal(500, rows);
    }

    private static long Parse(string line, int start, int length) =>
        long.Parse(line.AsSpan(start, length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
}

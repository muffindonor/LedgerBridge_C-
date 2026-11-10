using System.Text;
using LedgerBridge.Cobol;

namespace LedgerBridge.Tests;

public sealed class RecordReaderTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Short_lines_are_padded_back_to_record_length()
    {
        File.WriteAllText(_path, "AB\nABCDE\n");
        using RecordReader reader = RecordReader.OpenLineSequential(_path, 5);

        Assert.True(reader.Read());
        Assert.Equal("AB   ", Text(reader.Current));
        Assert.True(reader.Read());
        Assert.Equal("ABCDE", Text(reader.Current));
        Assert.False(reader.Read());
        Assert.Equal(2, reader.RecordNumber);
    }

    [Fact]
    public void Windows_line_endings_are_accepted()
    {
        File.WriteAllText(_path, "ABCDE\r\nFGHIJ\r\n");
        using RecordReader reader = RecordReader.OpenLineSequential(_path, 5);
        Assert.True(reader.Read());
        Assert.Equal("ABCDE", Text(reader.Current));
        Assert.True(reader.Read());
        Assert.Equal("FGHIJ", Text(reader.Current));
    }

    [Fact]
    public void A_last_line_without_a_newline_is_still_read()
    {
        File.WriteAllText(_path, "ABCDE\nFGHIJ");
        using RecordReader reader = RecordReader.OpenLineSequential(_path, 5);
        Assert.True(reader.Read());
        Assert.True(reader.Read());
        Assert.Equal("FGHIJ", Text(reader.Current));
        Assert.False(reader.Read());
    }

    [Fact]
    public void A_line_longer_than_the_layout_is_an_error()
    {
        File.WriteAllText(_path, "ABCDEF\n");
        using RecordReader reader = RecordReader.OpenLineSequential(_path, 5);
        Assert.Throws<RecordFormatException>(() => reader.Read());
    }

    [Fact]
    public void A_NUL_byte_in_a_text_record_is_an_error()
    {
        File.WriteAllBytes(_path, [(byte)'A', 0, (byte)'\n']);
        using RecordReader reader = RecordReader.OpenLineSequential(_path, 5);
        Assert.Throws<RecordFormatException>(() => reader.Read());
    }

    [Fact]
    public void An_empty_file_has_no_records()
    {
        File.WriteAllText(_path, string.Empty);
        using RecordReader lines = RecordReader.OpenLineSequential(_path, 5);
        Assert.False(lines.Read());
        using RecordReader fixedRecords = RecordReader.OpenFixed(_path, 5);
        Assert.False(fixedRecords.Read());
    }

    [Fact]
    public void A_truncated_fixed_record_is_an_error()
    {
        File.WriteAllBytes(_path, [1, 2, 3, 4, 5, 6, 7]);
        using RecordReader reader = RecordReader.OpenFixed(_path, 5);
        Assert.True(reader.Read());
        Assert.Throws<RecordFormatException>(() => reader.Read());
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(13)]
    public void Records_crossing_every_buffer_boundary_stay_intact(int bufferSize)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < 500; i++)
        {
            builder.Append((i % 100).ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).Append(i % 3 == 0 ? "\n" : "XXX\n");
        }

        File.WriteAllText(_path, builder.ToString());
        using RecordReader reader = RecordReader.OpenLineSequential(_path, 5, bufferSize);
        for (int i = 0; i < 500; i++)
        {
            Assert.True(reader.Read());
            string expected = (i % 100).ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + (i % 3 == 0 ? "   " : "XXX");
            Assert.Equal(expected, Text(reader.Current));
        }

        Assert.False(reader.Read());
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public void Fixed_records_crossing_every_buffer_boundary_stay_intact(int bufferSize)
    {
        byte[] data = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(_path, data);
        using RecordReader reader = RecordReader.OpenFixed(_path, 3, bufferSize + 2);
        for (int i = 0; i + 3 <= 498; i += 3)
        {
            Assert.True(reader.Read());
            Assert.Equal(data[i..(i + 3)], reader.Current.ToArray());
        }
    }

    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes);
}

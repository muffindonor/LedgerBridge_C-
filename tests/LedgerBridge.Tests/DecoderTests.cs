using System.Text;
using LedgerBridge.Cobol;

namespace LedgerBridge.Tests;

public class DecoderTests
{
    [Theory]
    [InlineData(new byte[] { 0x12, 0x34, 0x5C }, 5, 12345)]
    [InlineData(new byte[] { 0x12, 0x34, 0x5D }, 5, -12345)]
    [InlineData(new byte[] { 0x12, 0x34, 0x5F }, 5, 12345)]
    [InlineData(new byte[] { 0x01, 0x23, 0x4C }, 4, 1234)]
    [InlineData(new byte[] { 0x0C }, 1, 0)]
    public void Packed_decodes_valid_values(byte[] field, int digits, long expected)
    {
        Assert.True(CobolDecoder.TryDecodePacked(field, digits, out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(new byte[] { 0x1A, 0x34, 0x5C }, 5)] // digit nibble above 9, a S0C7 on a mainframe
    [InlineData(new byte[] { 0x12, 0x34, 0x51 }, 5)] // invalid sign nibble
    [InlineData(new byte[] { 0x12, 0x34, 0xAC }, 5)] // bad last digit
    [InlineData(new byte[] { 0x11, 0x23, 0x4C }, 4)] // even PIC, but the spare nibble is used
    [InlineData(new byte[] { 0x12, 0x3C }, 5)]       // wrong length for the PIC
    public void Packed_rejects_invalid_data(byte[] field, int digits)
    {
        Assert.False(CobolDecoder.TryDecodePacked(field, digits, out _));
    }

    [Fact]
    public void Packed_round_trips_random_values()
    {
        var random = new Random(12345);
        for (int i = 0; i < 20_000; i++)
        {
            int digits = random.Next(1, 19);
            long limit = (long)Math.Pow(10, digits) - 1;
            long expected = random.NextInt64(-limit, limit);
            byte[] packed = Pack(expected, digits);
            Assert.True(CobolDecoder.TryDecodePacked(packed, digits, out long actual));
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData("12345", 12345)]
    [InlineData("1234u", -12345)] // GnuCOBOL: p-y mean negative 0-9
    [InlineData("1234p", -12340)]
    [InlineData("1234{", 12340)]  // EBCDIC converted to ASCII
    [InlineData("1234E", 12345)]
    [InlineData("1234}", -12340)]
    [InlineData("1234N", -12345)]
    [InlineData("7", 7)]
    public void Overpunch_decodes(string field, long expected)
    {
        Assert.True(CobolDecoder.TryDecodeOverpunch(Encoding.ASCII.GetBytes(field), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("12 45")]
    [InlineData("1234 ")]
    [InlineData("A2345")]
    [InlineData("")]
    public void Overpunch_rejects_invalid_data(string field)
    {
        Assert.False(CobolDecoder.TryDecodeOverpunch(Encoding.ASCII.GetBytes(field), out _));
    }

    [Theory]
    [InlineData("+00123", 123)]
    [InlineData("-00123", -123)]
    public void Leading_separate_decodes(string field, long expected)
    {
        Assert.True(CobolDecoder.TryDecodeLeadingSeparate(Encoding.ASCII.GetBytes(field), out long value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("00123")]
    [InlineData(" 00123")]
    [InlineData("+00A23")]
    public void Leading_separate_rejects_invalid_data(string field)
    {
        Assert.False(CobolDecoder.TryDecodeLeadingSeparate(Encoding.ASCII.GetBytes(field), out _));
    }

    [Fact]
    public void Trailing_separate_decodes()
    {
        Assert.True(CobolDecoder.TryDecodeTrailingSeparate("00123-"u8, out long value));
        Assert.Equal(-123, value);
    }

    [Fact]
    public void Digits_reject_a_value_too_long_to_fit()
    {
        Assert.False(CobolDecoder.TryDecodeDigits("1234567890123456789"u8, out _));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE }, true, -2)]
    [InlineData(new byte[] { 0xFF, 0xFE }, false, 65534)]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00 }, true, 256)]
    public void Binary_is_big_endian(byte[] field, bool signed, long expected)
    {
        Assert.True(CobolDecoder.TryDecodeBinary(field, signed, out long value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Binary_rejects_an_unsigned_value_too_large_for_long()
    {
        byte[] max = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        Assert.False(CobolDecoder.TryDecodeBinary(max, signed: false, out _));
    }

    private static byte[] Pack(long value, int digits)
    {
        string text = Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
        if (digits % 2 == 0)
        {
            text = "0" + text;
        }

        text += value < 0 ? "D" : "C";
        return Convert.FromHexString(text);
    }
}

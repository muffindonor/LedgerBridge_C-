using System.Buffers.Binary;

namespace LedgerBridge.Cobol;

/// <summary>
/// Decodes COBOL numeric storage formats from raw bytes without allocating.
/// Every method returns the value as an unscaled integer: 12.34 in a V99 field is 1234.
/// Exact integers keep money away from floating point, as COBOL itself does.
/// </summary>
public static class CobolDecoder
{
    private const int MaxDigits = 18;

    /// <summary>Unsigned zoned decimal, for example PIC 9(5).</summary>
    public static bool TryDecodeDigits(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        if (field.IsEmpty || field.Length > MaxDigits)
        {
            return false;
        }

        long result = 0;
        foreach (byte b in field)
        {
            uint digit = (uint)(b - '0');
            if (digit > 9)
            {
                return false;
            }

            result = (result * 10) + digit;
        }

        value = result;
        return true;
    }

    /// <summary>
    /// Signed zoned decimal with the sign carried in the last digit, the default for PIC S9.
    /// Accepts GnuCOBOL's ASCII form (p-y negative) and the form a mainframe file takes
    /// after EBCDIC to ASCII conversion ({, A-I positive and }, J-R negative).
    /// </summary>
    public static bool TryDecodeOverpunch(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        if (field.IsEmpty || field.Length > MaxDigits)
        {
            return false;
        }

        long head = 0;
        if (field.Length > 1 && !TryDecodeDigits(field[..^1], out head))
        {
            return false;
        }

        byte last = field[^1];
        int digit;
        bool negative;
        switch (last)
        {
            case >= (byte)'0' and <= (byte)'9':
                digit = last - '0';
                negative = false;
                break;
            case >= (byte)'p' and <= (byte)'y':
                digit = last - 'p';
                negative = true;
                break;
            case (byte)'{':
                digit = 0;
                negative = false;
                break;
            case >= (byte)'A' and <= (byte)'I':
                digit = last - 'A' + 1;
                negative = false;
                break;
            case (byte)'}':
                digit = 0;
                negative = true;
                break;
            case >= (byte)'J' and <= (byte)'R':
                digit = last - 'J' + 1;
                negative = true;
                break;
            default:
                return false;
        }

        long magnitude = (head * 10) + digit;
        value = negative ? -magnitude : magnitude;
        return true;
    }

    /// <summary>SIGN IS LEADING SEPARATE: a '+' or '-' byte, then the digits.</summary>
    public static bool TryDecodeLeadingSeparate(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        return !field.IsEmpty && TryApplySign(field[0], field[1..], out value);
    }

    /// <summary>SIGN IS TRAILING SEPARATE: the digits, then a '+' or '-' byte.</summary>
    public static bool TryDecodeTrailingSeparate(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        return !field.IsEmpty && TryApplySign(field[^1], field[..^1], out value);
    }

    /// <summary>
    /// COMP-3 packed decimal: two digits per byte, with the sign in the last nibble.
    /// <paramref name="digits"/> is the PIC digit count. An even count leaves one unused
    /// high nibble, which must be zero or the value wouldn't fit the picture.
    /// An invalid nibble is what a mainframe reports as a S0C7 data exception.
    /// </summary>
    public static bool TryDecodePacked(ReadOnlySpan<byte> field, int digits, out long value)
    {
        value = 0;
        if (field.IsEmpty || digits is < 1 or > MaxDigits || field.Length != (digits / 2) + 1)
        {
            return false;
        }

        if (digits % 2 == 0 && (field[0] & 0xF0) != 0)
        {
            return false;
        }

        long result = 0;
        for (int i = 0; i < field.Length - 1; i++)
        {
            uint high = (uint)field[i] >> 4;
            uint low = (uint)field[i] & 0x0F;
            if (high > 9 || low > 9)
            {
                return false;
            }

            result = (result * 100) + (high * 10) + low;
        }

        uint lastDigit = (uint)field[^1] >> 4;
        uint sign = (uint)field[^1] & 0x0F;
        if (lastDigit > 9)
        {
            return false;
        }

        result = (result * 10) + lastDigit;
        switch (sign)
        {
            case 0xC or 0xA or 0xE or 0xF:
                value = result;
                return true;
            case 0xD or 0xB:
                value = -result;
                return true;
            default:
                return false;
        }
    }

    /// <summary>COMP big-endian binary of 2, 4 or 8 bytes, as written on a mainframe.</summary>
    public static bool TryDecodeBinary(ReadOnlySpan<byte> field, bool signed, out long value)
    {
        value = 0;
        switch (field.Length)
        {
            case 2:
                value = signed ? BinaryPrimitives.ReadInt16BigEndian(field) : BinaryPrimitives.ReadUInt16BigEndian(field);
                return true;
            case 4:
                value = signed ? BinaryPrimitives.ReadInt32BigEndian(field) : BinaryPrimitives.ReadUInt32BigEndian(field);
                return true;
            case 8:
                if (signed)
                {
                    value = BinaryPrimitives.ReadInt64BigEndian(field);
                    return true;
                }

                ulong unsigned = BinaryPrimitives.ReadUInt64BigEndian(field);
                if (unsigned > long.MaxValue)
                {
                    return false;
                }

                value = (long)unsigned;
                return true;
            default:
                return false;
        }
    }

    private static bool TryApplySign(byte sign, ReadOnlySpan<byte> digits, out long value)
    {
        value = 0;
        if ((sign != '+' && sign != '-') || !TryDecodeDigits(digits, out long magnitude))
        {
            return false;
        }

        value = sign == '-' ? -magnitude : magnitude;
        return true;
    }
}

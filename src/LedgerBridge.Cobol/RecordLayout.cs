using System.Text;

namespace LedgerBridge.Cobol;

public enum FieldKind
{
    Text,
    Display,
    DisplayLeadingSeparate,
    DisplayTrailingSeparate,
    Packed,
    Binary,
}

/// <summary>One named field's position and storage format inside a record.</summary>
public sealed class Field
{
    internal Field(string name, int offset, int length, FieldKind kind, int digits, int scale, bool signed)
    {
        Name = name;
        Offset = offset;
        Length = length;
        Kind = kind;
        Digits = digits;
        Scale = scale;
        Signed = signed;
    }

    public string Name { get; }

    public int Offset { get; }

    public int Length { get; }

    public FieldKind Kind { get; }

    public int Digits { get; }

    public int Scale { get; }

    public bool Signed { get; }

    public ReadOnlySpan<byte> Bytes(ReadOnlySpan<byte> record) => record.Slice(Offset, Length);

    public string ReadText(ReadOnlySpan<byte> record) =>
        Encoding.Latin1.GetString(Bytes(record)).TrimEnd(' ');

    /// <summary>Reads a numeric field as an unscaled integer, so 12.34 in a V99 field is 1234.</summary>
    public long ReadInt64(ReadOnlySpan<byte> record)
    {
        ReadOnlySpan<byte> raw = Bytes(record);
        long value;
        bool ok = Kind switch
        {
            FieldKind.Display when Signed => CobolDecoder.TryDecodeOverpunch(raw, out value),
            FieldKind.Display => CobolDecoder.TryDecodeDigits(raw, out value),
            FieldKind.DisplayLeadingSeparate => CobolDecoder.TryDecodeLeadingSeparate(raw, out value),
            FieldKind.DisplayTrailingSeparate => CobolDecoder.TryDecodeTrailingSeparate(raw, out value),
            FieldKind.Packed => CobolDecoder.TryDecodePacked(raw, Digits, out value),
            FieldKind.Binary => CobolDecoder.TryDecodeBinary(raw, Signed, out value),
            _ => throw new InvalidOperationException($"{Name} is a text field and has no numeric value."),
        };

        if (!ok)
        {
            ThrowInvalid(raw);
        }

        return value;
    }

    private void ThrowInvalid(ReadOnlySpan<byte> raw) =>
        throw new RecordFormatException(
            $"Field {Name} at offset {Offset} is not valid {Kind} data: {Convert.ToHexString(raw)}");
}

/// <summary>A parsed copybook: the record's total length and its named fields.</summary>
public sealed class RecordLayout
{
    private readonly Dictionary<string, Field> _byName;

    internal RecordLayout(string name, int length, IReadOnlyList<Field> fields, Dictionary<string, Field> byName)
    {
        Name = name;
        Length = length;
        Fields = fields;
        _byName = byName;
    }

    public string Name { get; }

    public int Length { get; }

    /// <summary>Elementary and group fields in copybook order. FILLER is left out.</summary>
    public IReadOnlyList<Field> Fields { get; }

    public Field this[string name] =>
        _byName.TryGetValue(name, out Field? field)
            ? field
            : throw new KeyNotFoundException($"Record {Name} has no field named {name}.");
}

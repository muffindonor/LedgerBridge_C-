namespace LedgerBridge.Cobol;

/// <summary>A record or field in a data file doesn't match its copybook.</summary>
public sealed class RecordFormatException(string message) : Exception(message);

/// <summary>A copybook is malformed or uses a clause this parser doesn't support.</summary>
public sealed class CopybookException(string message, int line)
    : Exception($"Copybook line {line}: {message}")
{
    public int Line { get; } = line;
}

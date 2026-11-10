using System.Globalization;

namespace LedgerBridge.Tools;

public sealed class Arguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static Arguments Parse(string[] args)
    {
        var parsed = new Arguments();
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            parsed._values[args[i].TrimStart('-')] = args[i + 1];
        }

        return parsed;
    }

    public string Text(string name) => _values.TryGetValue(name, out string? value)
        ? value
        : throw new ArgumentException($"--{name} is required.");

    public string? Optional(string name) => _values.GetValueOrDefault(name);

    public int Number(string name, int fallback) =>
        _values.TryGetValue(name, out string? value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
}

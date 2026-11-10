namespace LedgerBridge.Tests;

public static class TestPaths
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Golden(string file) => Path.Combine(Fixtures, "golden", file);

    public static string Batch(string name) => Path.Combine(Fixtures, "batches", name);
}

/// <summary>A private copy of a fixture batch, safe to tamper with.</summary>
public sealed class TempBatch : IDisposable
{
    private readonly string _root;

    public TempBatch(string fixture, string? name = null)
    {
        _root = Path.Combine(Path.GetTempPath(), "ledger-bridge-tests", Guid.NewGuid().ToString("N"));
        Directory = Path.Combine(_root, name ?? fixture);
        System.IO.Directory.CreateDirectory(Directory);
        foreach (string file in System.IO.Directory.GetFiles(TestPaths.Batch(fixture)))
        {
            File.Copy(file, Path.Combine(Directory, Path.GetFileName(file)));
        }
    }

    public string Directory { get; }

    public string PathOf(string file) => Path.Combine(Directory, file);

    /// <summary>Replaces the first match, and fails the test if there isn't one.</summary>
    public void Replace(string file, string oldText, string newText)
    {
        string content = File.ReadAllText(PathOf(file));
        int index = content.IndexOf(oldText, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{oldText}' not found in {file}");
        File.WriteAllText(PathOf(file), content.Remove(index, oldText.Length).Insert(index, newText));
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_root))
        {
            System.IO.Directory.Delete(_root, recursive: true);
        }
    }
}

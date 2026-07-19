using System;
using System.IO;

namespace DesktopPlus.Tests;

/// <summary>
/// A throwaway directory under the system temp folder, removed on dispose. Used by the
/// filesystem tests so each test gets an isolated sandbox with no shared state.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dp_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name, string content = "x")
    {
        string p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public string Dir(string name)
    {
        string p = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public string Combine(params string[] parts)
    {
        string p = Path;
        foreach (var part in parts) { p = System.IO.Path.Combine(p, part); }
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

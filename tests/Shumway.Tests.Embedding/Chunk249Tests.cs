using System.IO;
using Shumway.Repl;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 249: REPL <see cref="HistoryStore"/> — persistent
/// command history. The interactive <see cref="LineEditor"/>
/// itself isn't tested here (it depends on
/// <see cref="System.Console"/> in interactive mode); the
/// HistoryStore covers the persistence + dedup + capping
/// behaviour the editor relies on.
/// </summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public class Chunk249Tests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(),
            $"shumway-history-{System.Guid.NewGuid():N}.txt");

    [Fact]
    public void EmptyStore_StartsEmpty()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            Assert.Empty(store.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Add_AppendsAndPersists()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            store.Add("between(1, 5, X).");
            store.Add("member(X, [a, b, c]).");
            Assert.Equal(new[]
            {
                "between(1, 5, X).",
                "member(X, [a, b, c]).",
            }, store.Entries);

            // Persisted: a fresh store on the same path sees them.
            var rehydrated = new HistoryStore(path);
            Assert.Equal(store.Entries, rehydrated.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Add_SkipsConsecutiveDuplicates()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            store.Add("foo.");
            store.Add("foo.");
            store.Add("foo.");
            Assert.Single(store.Entries);
            Assert.Equal("foo.", store.Entries[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Add_KeepsNonConsecutiveDuplicates()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            store.Add("foo.");
            store.Add("bar.");
            store.Add("foo.");
            Assert.Equal(new[] { "foo.", "bar.", "foo." }, store.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Add_IgnoresEmptyOrWhitespace()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            store.Add("");
            store.Add("   ");
            store.Add("\t");
            Assert.Empty(store.Entries);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Capacity_TrimsOldestWhenExceeded()
    {
        var path = TempPath();
        try
        {
            var store = new HistoryStore(path);
            // Push past the cap with unique entries.
            for (int i = 0; i < HistoryStore.MaxEntries + 50; i++)
                store.Add($"entry_{i}.");
            Assert.Equal(HistoryStore.MaxEntries, store.Entries.Count);
            Assert.Equal("entry_50.", store.Entries[0]);
            Assert.Equal($"entry_{HistoryStore.MaxEntries + 49}.",
                store.Entries[^1]);

            // Disk file is rewritten on overflow — check it matches.
            var lines = File.ReadAllLines(path);
            Assert.Equal(HistoryStore.MaxEntries, lines.Length);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_TrimsOversizedFile()
    {
        var path = TempPath();
        try
        {
            // Pre-seed a file that's bigger than the cap (could
            // happen if max changed between runs, or another tool
            // wrote it).
            var oversized = new System.Collections.Generic.List<string>();
            for (int i = 0; i < HistoryStore.MaxEntries + 100; i++)
                oversized.Add($"old_{i}.");
            File.WriteAllLines(path, oversized);

            var store = new HistoryStore(path);
            Assert.Equal(HistoryStore.MaxEntries, store.Entries.Count);
            // Trimmed from the front — most recent entries survive.
            Assert.Equal($"old_{HistoryStore.MaxEntries + 99}.",
                store.Entries[^1]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void MissingFile_LoadsEmpty_AddCreates()
    {
        var path = TempPath();
        try
        {
            Assert.False(File.Exists(path));
            var store = new HistoryStore(path);
            Assert.Empty(store.Entries);
            store.Add("hello.");
            Assert.True(File.Exists(path));
            Assert.Equal("hello." + System.Environment.NewLine,
                File.ReadAllText(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void DefaultPath_UsesEnvOverrideWhenSet()
    {
        string? saved = System.Environment.GetEnvironmentVariable("SHUMWAY_HISTORY");
        try
        {
            System.Environment.SetEnvironmentVariable(
                "SHUMWAY_HISTORY", "/tmp/custom-history");
            Assert.Equal("/tmp/custom-history", HistoryStore.DefaultPath());
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SHUMWAY_HISTORY", saved);
        }
    }

    [Fact]
    public void DefaultPath_FallsBackToHomeWhenUnset()
    {
        string? saved = System.Environment.GetEnvironmentVariable("SHUMWAY_HISTORY");
        try
        {
            System.Environment.SetEnvironmentVariable("SHUMWAY_HISTORY", null);
            string p = HistoryStore.DefaultPath();
            Assert.EndsWith(".shumway_history", p);
            // Sits under the user profile folder.
            string profile = System.Environment.GetFolderPath(
                System.Environment.SpecialFolder.UserProfile);
            Assert.StartsWith(profile, p);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("SHUMWAY_HISTORY", saved);
        }
    }
}

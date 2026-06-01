using System.Text;

namespace Shumway.Repl;

/// <summary>
/// Chunk 249 — persistent command history for the interactive
/// top-level. Loads previously-saved entries from a file on
/// construction; appends each new entry both to the in-memory
/// list and to the file (so an unexpected exit doesn't lose
/// recently-typed queries).
///
/// <para>Deduplication: consecutive duplicates aren't saved. A
/// query that's identical to the most-recent entry doesn't grow
/// the history. Long-distance duplicates (the same query a
/// dozen entries back) are kept — useful for navigating with
/// Up/Down without losing intermediate state.</para>
///
/// <para>Size cap: <see cref="MaxEntries"/> trims the oldest
/// entries when the in-memory list overflows. The file gets
/// rewritten on the next entry-add to match.</para>
///
/// <para>File format: one entry per line, UTF-8. Multi-line
/// queries are stored as one line per physical line — the REPL's
/// per-line input model already matches this granularity.</para>
/// </summary>
public sealed class HistoryStore
{
    public const int MaxEntries = 1000;

    private readonly string _path;
    private readonly List<string> _entries;

    public HistoryStore(string path)
    {
        _path = path;
        _entries = LoadFromDisk(path);
    }

    /// <summary>The in-memory list, oldest first. Read-only view —
    /// mutations go through <see cref="Add"/>.</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>Appends <paramref name="entry"/> as the newest entry
    /// (unless it duplicates the current newest), and persists it to
    /// disk. Empty / whitespace-only entries are ignored.</summary>
    public void Add(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;
        if (_entries.Count > 0 && _entries[^1] == entry) return;
        _entries.Add(entry);
        if (_entries.Count > MaxEntries)
        {
            int excess = _entries.Count - MaxEntries;
            _entries.RemoveRange(0, excess);
            // Rewrite the file so it stays bounded too.
            TryWriteAll();
            return;
        }
        TryAppend(entry);
    }

    private static List<string> LoadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return new List<string>();
            var lines = new List<string>(
                File.ReadAllLines(path, Encoding.UTF8));
            // Trim to MaxEntries on load — if the file grew past it
            // (some other process appending, or a previous chunk's
            // overflow that didn't rewrite), we don't carry the bloat
            // into memory.
            if (lines.Count > MaxEntries)
                lines.RemoveRange(0, lines.Count - MaxEntries);
            return lines;
        }
        catch { return new List<string>(); }
    }

    private void TryAppend(string entry)
    {
        try
        {
            EnsureDirectory();
            File.AppendAllText(_path, entry + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* best-effort — history loss is non-fatal */ }
    }

    private void TryWriteAll()
    {
        try
        {
            EnsureDirectory();
            File.WriteAllLines(_path, _entries, Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    private void EnsureDirectory()
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>Default history-file path: <c>~/.shumway_history</c>
    /// on Unix-y systems, the equivalent under the user's profile
    /// folder on Windows. Environment-overridable via
    /// <c>SHUMWAY_HISTORY</c> for ops who want to redirect (e.g.
    /// CI runs into <c>/dev/null</c>, multi-user setups).</summary>
    public static string DefaultPath()
    {
        string? envOverride = Environment.GetEnvironmentVariable("SHUMWAY_HISTORY");
        if (!string.IsNullOrEmpty(envOverride)) return envOverride;
        string home = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".shumway_history");
    }
}

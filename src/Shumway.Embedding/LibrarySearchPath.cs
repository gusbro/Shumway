namespace Shumway.Embedding;

/// <summary>ADR-038: the library search path of the command-line tools.</summary>
public static class LibrarySearchPath
{
    /// <summary>The directories given first (highest precedence), then the
    /// entries of SHUMWAY_LIBRARY_PATH, then the shipped <c>lib/</c> beside the
    /// tool and under the current directory. The last two are what
    /// <see cref="PrologEngine"/> searches by default, and the tools agree with
    /// it: a toolchain unpacked into one directory has <c>lib/</c> next to the
    /// executables, so a program the REPL runs links without being told where
    /// the libraries are. An explicit directory still wins.</summary>
    public static List<string> Of(IEnumerable<string> given)
    {
        var dirs = new List<string>(given);
        string? env = Environment.GetEnvironmentVariable("SHUMWAY_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(env))
            // Manual Trim: net48 has no StringSplitOptions.TrimEntries.
            foreach (string raw in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string d = raw.Trim();
                if (d.Length > 0) dirs.Add(d);
            }
        AddIfExists(dirs, Path.Combine(AppContext.BaseDirectory, "lib"));
        AddIfExists(dirs, Path.Combine(Directory.GetCurrentDirectory(), "lib"));
        return dirs;
    }

    /// <summary>The directory a search path entry names: an entry may carry
    /// a dialect before it (ADR-040, <c>scryer:dir</c>).</summary>
    internal static string DirectoryOf(string entry)
    {
        int colon = entry.IndexOf(':');
        return colon > 0 && DialectRegistry.IsKnownDialect(entry.Substring(0, colon))
            ? entry.Substring(colon + 1)
            : entry;
    }

    /// <summary>Whether <c>library(Name)</c> is on <paramref name="dirs"/>, a
    /// source or a bundle, as the consult resolves it.</summary>
    internal static bool Has(IReadOnlyList<string> dirs, string name)
    {
        foreach (string entry in dirs)
        {
            string dir = DirectoryOf(entry);
            try
            {
                if (File.Exists(Path.Combine(dir, name + ".pl"))
                    || File.Exists(Path.Combine(dir, name + ".shum")))
                    return true;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // An invalid path component: not this directory.
            }
        }
        return false;
    }

    private static void AddIfExists(List<string> dirs, string dir)
    {
        if (Directory.Exists(dir)) dirs.Add(dir);
    }
}

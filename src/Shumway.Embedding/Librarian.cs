namespace Shumway.Embedding;

/// <summary>A librarian-level problem (duplicate module, target is not an
/// archive, …) that the <c>shumway-lib</c> CLI surfaces as a clean error
/// rather than a stack trace.</summary>
public sealed class LibrarianException : Exception
{
    public LibrarianException(string message) : base(message) { }
}

/// <summary>
/// The <c>shumway-lib</c> librarian: assembles a <c>.shum</c> bundle out of
/// chosen <c>.shmo</c> compiled-object files and performs the usual archive
/// operations on it — list, add, remove, extract — <em>without</em> the
/// linker's reachability analysis and dead-module pruning. Every object you
/// put in stays in, verbatim.
///
/// <para>The archive is a normal, directly-runnable <c>.shum</c>: its modules
/// live in <see cref="Bundle.ArchiveMembers"/> (each an unmodified
/// <c>.shmo</c> image), and <see cref="PrologEngine.LoadBundle(Bundle)"/>
/// derives a runnable entry from each member at load. Because the objects are
/// kept unchanged, <see cref="ReadArchive"/> + a file write reproduces the
/// exact input <c>.shmo</c>.</para>
///
/// <para>Members are keyed by module name (an archive cannot hold two modules
/// of the same name — they would collide at load, exactly as consulting two
/// such files would).</para>
/// </summary>
public static class Librarian
{
    /// <summary>Builds a librarian archive (<c>.shum</c> bytes) from the given
    /// members. Each member's bytes must be a valid <c>.shmo</c>; module names
    /// must be unique.</summary>
    public static byte[] CreateArchive(IReadOnlyList<BundleArchiveMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        RequireDistinctModules(members);
        var bundle = new Bundle(
            Array.Empty<BundleEntry>(), foreignAssemblies: null,
            snapshot: null, archiveMembers: members);
        return BundleWriter.ToBytes(bundle);
    }

    /// <summary>Adds members to an existing librarian archive and returns the
    /// rewritten <c>.shum</c> bytes. Throws if <paramref name="existingShum"/>
    /// is a linked bundle (it has no archive to add to) or if any new module
    /// name collides with one already present.</summary>
    public static byte[] AddMembers(
        byte[] existingShum, IReadOnlyList<BundleArchiveMember> toAdd)
    {
        ArgumentNullException.ThrowIfNull(existingShum);
        ArgumentNullException.ThrowIfNull(toAdd);
        var bundle = BundleReader.FromBytes(existingShum);
        RequireArchive(bundle);
        var combined = new List<BundleArchiveMember>(
            bundle.ArchiveMembers.Count + toAdd.Count);
        combined.AddRange(bundle.ArchiveMembers);
        combined.AddRange(toAdd);
        RequireDistinctModules(combined);
        return CreateArchive(combined);
    }

    /// <summary>Removes members by module name and returns the rewritten
    /// archive bytes. <paramref name="removed"/> lists the module names that
    /// were present and dropped; <paramref name="notFound"/> lists requested
    /// names that the archive did not contain.</summary>
    public static byte[] RemoveModules(
        byte[] existingShum, IReadOnlyList<string> moduleNames,
        out IReadOnlyList<string> removed, out IReadOnlyList<string> notFound)
    {
        ArgumentNullException.ThrowIfNull(existingShum);
        ArgumentNullException.ThrowIfNull(moduleNames);
        var bundle = BundleReader.FromBytes(existingShum);
        RequireArchive(bundle);
        var drop = new HashSet<string>(moduleNames, StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<BundleArchiveMember>(bundle.ArchiveMembers.Count);
        var removedList = new List<string>();
        foreach (var m in bundle.ArchiveMembers)
        {
            string module = ModuleNameOf(m);
            present.Add(module);
            if (drop.Contains(module)) removedList.Add(module);
            else kept.Add(m);
        }
        removed = removedList;
        notFound = moduleNames.Where(n => !present.Contains(n)).ToList();
        return CreateArchive(kept);
    }

    /// <summary>Returns the archive members of a <c>.shum</c> — empty for a
    /// linked bundle (its modules are post-link <see cref="Bundle.Entries"/>,
    /// not archived objects).</summary>
    public static IReadOnlyList<BundleArchiveMember> ReadArchive(byte[] shumBytes)
    {
        ArgumentNullException.ThrowIfNull(shumBytes);
        return BundleReader.FromBytes(shumBytes).ArchiveMembers;
    }

    /// <summary>Parses a member's <c>.shmo</c> image.</summary>
    public static ShmoObject Parse(BundleArchiveMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return ShmoReader.FromBytes(member.ShmoBytes);
    }

    /// <summary>The module name a member contributes (its archive key).</summary>
    public static string ModuleNameOf(BundleArchiveMember member)
        => Parse(member).ModuleName;

    private static void RequireArchive(Bundle bundle)
    {
        if (bundle.ArchiveMembers.Count == 0)
            throw new LibrarianException(
                bundle.Entries.Count > 0
                    ? "this .shum is a linked bundle (built by shumway-link), not a "
                      + "librarian archive — it has no .shmo members to operate on."
                    : "this .shum contains no archive members.");
    }

    private static void RequireDistinctModules(IReadOnlyList<BundleArchiveMember> members)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in members)
        {
            string module = ModuleNameOf(m);
            if (seen.TryGetValue(module, out string? firstFile))
                throw new LibrarianException(
                    $"two objects define module '{module}' ('{firstFile}' and "
                    + $"'{m.FileName}'); an archive cannot hold duplicate module names.");
            seen[module] = m.FileName;
        }
    }
}

using System.Reflection;

namespace Shumway.Embedding;

/// <summary>The engine's own Prolog libraries (clpfd, clpr, coroutining, reif)
/// and its prelude, baked at build time into bundles by the Shumway.Libraries
/// assembly. That assembly cannot be referenced from here (its bake runs the
/// compiler and linker defined in this one), so it is loaded by name at first
/// use: a host references it, and roots it when trimming.</summary>
internal static class LibraryBundles
{
    public const string AssemblyName = "Shumway.Libraries";
    public const string Clpfd = "clpfd";
    public const string Clpr = "clpr";
    public const string Coroutining = "coroutining";
    public const string Reif = "reif";

    /// <summary>The library names; each is also its module name.</summary>
    public static readonly string[] Names = { Clpfd, Clpr, Coroutining, Reif };

    public static bool IsEngineLibrary(string name)
        => Array.IndexOf(Names, name) >= 0;

    /// <summary>The library's public predicates: what a program that imports
    /// it may call.</summary>
    public static IReadOnlyCollection<PredicateRef> PublicsOf(string name)
    {
        var publics = new HashSet<PredicateRef>();
        foreach (var entry in Get(name).Entries)
            foreach (var d in entry.Defined)
                if (d.Visibility == PredicateVisibility.Public) publics.Add(d.Indicator);
        return publics;
    }

    private static readonly object Gate = new();
    private static Assembly? _assembly;
    private static readonly Dictionary<string, byte[]> BundleBytes = new();

    // The library bake runs before the assembly exists: a library that
    // imports another (reif, coroutining) reads that one's bundle from the
    // bake's output and its source from the project, in bake order.
    private static string? _bakedBundleDir, _sourceDir;

    /// <summary>For the library bake: read the libraries from these
    /// directories instead of the assembly.</summary>
    internal static void UseBakeDirectories(string bakedBundleDir, string sourceDir)
    {
        lock (Gate) { _bakedBundleDir = bakedBundleDir; _sourceDir = sourceDir; }
    }

    /// <summary>The library's bundle, parsed afresh for each call (a loaded
    /// bundle is the engine's to keep).</summary>
    public static Bundle Get(string name)
    {
        byte[] bytes;
        lock (Gate)
        {
            if (!BundleBytes.TryGetValue(name, out bytes!))
                BundleBytes[name] = bytes = ReadResource(name + ".shum");
        }
        return BundleReader.FromBytes(bytes);
    }

    /// <summary>The library's Prolog source text (the predicate reference
    /// is generated from its doc comments).</summary>
    public static string SourceOf(string name)
        => System.Text.Encoding.UTF8.GetString(ReadResource(name + ".pl"));

    /// <summary>Names the engine build: the baked prelude carries the stamp of
    /// the build that compiled it, and is used only by that build.</summary>
    /// <remarks>Null where the runtime cannot name it: the prelude is then
    /// consulted.</remarks>
    public static string? EngineBuildStamp
    {
        get
        {
            try { return typeof(LibraryBundles).Assembly.ManifestModule.ModuleVersionId.ToString("N"); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException) { return null; }
        }
    }

    private static Bundle? _bakedPrelude;
    private static bool _bakedPreludeRead;

    /// <summary>The prelude the libraries' assembly carries, compiled by this
    /// engine build (with its Tier-1 IL on the desktop): read once, and shared
    /// by every engine, since loading a bundle only reads it. Null when there
    /// is none to take: the assembly is absent, its flavour carries none (the
    /// browser's: the stdlib bundle bakes the prelude there), or another engine
    /// build baked it. The engine then compiles the prelude's text.</summary>
    public static Bundle? BakedPrelude
    {
        get
        {
            lock (Gate)
            {
                if (!_bakedPreludeRead)
                {
                    _bakedPreludeRead = true;
                    _bakedPrelude = AcceptBakedPrelude(
                        TryReadResource(PreludeName + ".stamp", out _),
                        () => TryReadResource(PreludeName + ".shum", out _));
                }
                return _bakedPrelude;
            }
        }
    }

    /// <summary>The baked prelude's bundle, when its stamp names this engine
    /// build; the bundle is not read otherwise.</summary>
    internal static Bundle? AcceptBakedPrelude(byte[]? stamp, Func<byte[]?> bundle)
        => stamp is not null && EngineBuildStamp is { } build
           && System.Text.Encoding.ASCII.GetString(stamp).Trim() == build
           && bundle() is { } bytes
            ? BundleReader.FromBytes(bytes)
            : null;

    /// <summary>Whether the entry is the prelude this process read from the
    /// libraries' assembly.</summary>
    public static bool IsBakedPrelude(BundleEntry entry)
    {
        lock (Gate)
            return _bakedPrelude is { } baked && baked.Entries.Contains(entry);
    }

    private const string PreludeName = "prelude";

    private static byte[] ReadResource(string logicalName)
        => TryReadResource(logicalName, out string? why)
            ?? throw new InvalidOperationException(why);

    private static byte[]? TryReadResource(string logicalName, out string? why)
    {
        why = null;
        Assembly asm;
        lock (Gate)
        {
            string? dir = logicalName.EndsWith(".pl", StringComparison.Ordinal)
                ? _sourceDir : _bakedBundleDir;
            if (dir is not null)
            {
                string path = Path.Combine(dir, logicalName);
                if (File.Exists(path)) return File.ReadAllBytes(path);
                why = $"The library bake needs '{logicalName}' in {dir}: bake the libraries "
                    + "a library imports before it.";
                return null;
            }
            if (_assembly is null)
            {
                try { _assembly = Assembly.Load(new AssemblyName(AssemblyName)); }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException
                                           or BadImageFormatException)
                {
                    why = $"The engine's Prolog libraries live in the {AssemblyName} assembly, "
                        + "which could not be loaded: reference it from the host application "
                        + $"(and root it when trimming). {ex.Message}";
                    return null;
                }
            }
            asm = _assembly;
        }
        using var stream = asm.GetManifestResourceStream(logicalName);
        if (stream is null)
        {
            why = $"{AssemblyName} carries no resource '{logicalName}'.";
            return null;
        }
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

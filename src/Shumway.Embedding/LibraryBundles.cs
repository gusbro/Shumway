using System.Reflection;

namespace Shumway.Embedding;

/// <summary>The engine's own Prolog libraries (clpfd, clpr, coroutining),
/// baked at build time into bundles by the Shumway.Libraries assembly.
/// That assembly cannot be referenced from here (its bake runs the compiler
/// and linker defined in this one), so it is loaded by name at first use:
/// a host references it, and roots it when trimming.</summary>
internal static class LibraryBundles
{
    public const string AssemblyName = "Shumway.Libraries";
    public const string Clpfd = "clpfd";
    public const string Clpr = "clpr";
    public const string Coroutining = "coroutining";

    /// <summary>The library names; each is also its module name.</summary>
    public static readonly string[] Names = { Clpfd, Clpr, Coroutining };

    public static bool IsEngineLibrary(string name)
        => Array.IndexOf(Names, name) >= 0;

    private static readonly object Gate = new();
    private static Assembly? _assembly;
    private static readonly Dictionary<string, byte[]> BundleBytes = new();

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

    private static byte[] ReadResource(string logicalName)
    {
        Assembly asm;
        lock (Gate)
        {
            if (_assembly is null)
            {
                try { _assembly = Assembly.Load(new AssemblyName(AssemblyName)); }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException
                                           or BadImageFormatException)
                {
                    throw new InvalidOperationException(
                        $"The engine's Prolog libraries live in the {AssemblyName} assembly, "
                        + "which could not be loaded: reference it from the host application "
                        + "(and root it when trimming).", ex);
                }
            }
            asm = _assembly;
        }
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"{AssemblyName} carries no resource '{logicalName}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}

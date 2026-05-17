namespace Shumway.Embedding;

/// <summary>
/// In-memory representation of a Shumway bundle. A bundle bundles the source
/// for one or more named modules into a single deployable unit; the engine's
/// <see cref="PrologEngine.LoadBundle"/> consults each entry as if it had
/// been passed through <see cref="PrologEngine.ConsultString"/> directly.
/// </summary>
public sealed class Bundle
{
    public IReadOnlyList<BundleEntry> Entries { get; }

    public Bundle(IReadOnlyList<BundleEntry> entries)
    {
        Entries = entries;
    }
}

/// <summary>One module inside a bundle: its name and the original Prolog
/// source for that module. The source includes its own
/// <c>:- module(name).</c> directive (if any) so loading round-trips
/// through the standard consult path.</summary>
public sealed class BundleEntry
{
    public string ModuleName { get; }
    public string Source { get; }

    public BundleEntry(string moduleName, string source)
    {
        ModuleName = moduleName;
        Source = source;
    }
}

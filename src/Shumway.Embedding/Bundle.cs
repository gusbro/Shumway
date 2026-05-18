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

/// <summary>One module inside a bundle: its name, the original Prolog source,
/// and an optional pre-compiled bytecode payload. The source includes its
/// own <c>:- module(name).</c> directive (if any) so loading round-trips
/// through the standard consult path. The compiled payload is the
/// <see cref="CompiledModuleCodec"/> output for the module's clauses; when
/// present, future runtime paths can use it to skip parser / compiler work
/// at consult time.</summary>
public sealed class BundleEntry
{
    public string ModuleName { get; }
    public string Source { get; }
    public byte[]? CompiledBytecode { get; }

    public BundleEntry(string moduleName, string source, byte[]? compiledBytecode = null)
    {
        ModuleName = moduleName;
        Source = source;
        CompiledBytecode = compiledBytecode;
    }
}

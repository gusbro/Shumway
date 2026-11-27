using System.Text;

namespace Shumway.Embedding;

/// <summary>ADR-076: how a <c>.shum</c> or a <c>.shmo</c> was built, held
/// after the stamp where every format reads it. An engine that accepts the
/// file's rules version (<see cref="Accepts"/>) can build the file again from
/// it when the stamp is not its own.
///
/// <para>Under rules 1 a recipe is a sequence of fields, each a tag byte, a
/// length and that many bytes. A reader skips a tag it does not know, so a
/// field can be added within rules 1; a change to what a field means raises
/// the rules version.</para></summary>
public sealed class BundleRecipe
{
    /// <summary>The rules this engine builds files under.</summary>
    public const int CurrentRules = 1;

    /// <summary>Whether this engine rebuilds a file built under
    /// <paramref name="rules"/>.</summary>
    public static bool Accepts(int rules) => rules == CurrentRules;

    /// <summary>The tool that built the file: <c>shumway-link</c>,
    /// <c>shumway-lib</c>, <c>shumway-compile</c>, <c>page</c> for a library a
    /// page compiled, <c>save_state</c>, or <c>host</c> for a bundle a host
    /// wrote with <see cref="BundleWriter"/>.</summary>
    public string Tool { get; init; } = "host";

    /// <summary>The recipe of a file a host wrote: nothing to build it from.</summary>
    public static BundleRecipe Host { get; } = new();

    /// <summary>The source named <paramref name="name"/> whose bytes this
    /// recipe carries, or one of its inputs' recipes does; null when none
    /// does.</summary>
    public RecipeSource? CarriedSource(string name)
    {
        foreach (RecipeSource source in Sources)
            if (source.Contents is not null && source.Name == name) return source;
        foreach (RecipeInput input in Inputs)
            if (input.Recipe?.CarriedSource(name) is { } found) return found;
        return null;
    }

    /// <summary>The inputs of the build, in the order it took them.</summary>
    public IReadOnlyList<RecipeInput> Inputs { get; init; } = Array.Empty<RecipeInput>();

    /// <summary>The source files the build read, by name.</summary>
    public IReadOnlyList<RecipeSource> Sources { get; init; } = Array.Empty<RecipeSource>();

    /// <summary>For an object of a compile that wrote one object per module it
    /// loaded: which module this object is.</summary>
    public string? Module { get; init; }

    // What the build was asked for: the options that change its product.

    /// <summary>The sources were consulted into one engine (<c>--consult</c>).</summary>
    public bool Consult { get; init; }

    /// <summary>A library link (<c>--library</c>).</summary>
    public bool Library { get; init; }

    /// <summary>The entry points (<c>--entry</c>).</summary>
    public IReadOnlyList<PredicateRef> Entries { get; init; } = Array.Empty<PredicateRef>();

    /// <summary>The startup goal, or a library's load goal (<c>--goal</c>).</summary>
    public string? Goal { get; init; }

    public bool AllowUndefined { get; init; }

    /// <summary>The source left out of the bundle (<c>--strip</c>).</summary>
    public bool Strip { get; init; }

    public bool CompiledIl { get; init; }

    public bool StripWam { get; init; }

    public bool Wasm { get; init; }

    /// <summary>The prelude baked in (<c>--stdlib</c>, implied by an executable).</summary>
    public bool Stdlib { get; init; }

    public bool PrunePrelude { get; init; }

    public bool RegionPrune { get; init; } = true;

    /// <summary>The dynamic libraries the link was against (<c>--dynlib</c>),
    /// by name.</summary>
    public IReadOnlyList<string> DynamicLibraries { get; init; } = Array.Empty<string>();

    /// <summary>The foreign assemblies and native libraries, by file name.</summary>
    public IReadOnlyList<string> ForeignAssemblies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NativeLibraries { get; init; } = Array.Empty<string>();

    /// <summary>A compile in debug mode (<c>--debug</c>).</summary>
    public bool Debug { get; init; }

    /// <summary>A compile that kept the source (<c>--with-source</c>).</summary>
    public bool WithSource { get; init; }

    /// <summary>A compile in Arity compatibility mode (<c>--arity</c>).</summary>
    public bool ArityCompat { get; init; }

    /// <summary>The dialect the sources were read in (ADR-040), or null for
    /// Shumway's own.</summary>
    public string? Dialect { get; init; }

    private enum Field : byte
    {
        Tool = 1,
        Module = 2,
        Input = 3,
        Source = 4,
        Consult = 5,
        Library = 6,
        Entry = 7,
        Goal = 8,
        AllowUndefined = 9,
        Strip = 10,
        CompiledIl = 11,
        StripWam = 12,
        Wasm = 13,
        Stdlib = 14,
        PrunePrelude = 15,
        NoRegionPrune = 16,
        DynamicLibrary = 17,
        ForeignAssembly = 18,
        NativeLibrary = 19,
        Debug = 20,
        WithSource = 21,
        ArityCompat = 22,
        Dialect = 23,
    }

    internal byte[] ToBytes()
    {
        var w = new FieldWriter();
        w.Text((byte)Field.Tool, Tool);
        if (Module is not null) w.Text((byte)Field.Module, Module);
        foreach (RecipeInput input in Inputs) w.Bytes((byte)Field.Input, input.ToBytes());
        foreach (RecipeSource source in Sources) w.Bytes((byte)Field.Source, source.ToBytes());
        if (Consult) w.Flag((byte)Field.Consult);
        if (Library) w.Flag((byte)Field.Library);
        foreach (PredicateRef entry in Entries) w.Text((byte)Field.Entry, $"{entry.Name}/{entry.Arity}");
        if (Goal is not null) w.Text((byte)Field.Goal, Goal);
        if (AllowUndefined) w.Flag((byte)Field.AllowUndefined);
        if (Strip) w.Flag((byte)Field.Strip);
        if (CompiledIl) w.Flag((byte)Field.CompiledIl);
        if (StripWam) w.Flag((byte)Field.StripWam);
        if (Wasm) w.Flag((byte)Field.Wasm);
        if (Stdlib) w.Flag((byte)Field.Stdlib);
        if (PrunePrelude) w.Flag((byte)Field.PrunePrelude);
        if (!RegionPrune) w.Flag((byte)Field.NoRegionPrune);
        foreach (string name in DynamicLibraries) w.Text((byte)Field.DynamicLibrary, name);
        foreach (string name in ForeignAssemblies) w.Text((byte)Field.ForeignAssembly, name);
        foreach (string name in NativeLibraries) w.Text((byte)Field.NativeLibrary, name);
        if (Debug) w.Flag((byte)Field.Debug);
        if (WithSource) w.Flag((byte)Field.WithSource);
        if (ArityCompat) w.Flag((byte)Field.ArityCompat);
        if (Dialect is not null) w.Text((byte)Field.Dialect, Dialect);
        return w.ToArray();
    }

    /// <summary>The recipe in <paramref name="bytes"/>, under rules 1.</summary>
    internal static BundleRecipe FromBytes(ReadOnlySpan<byte> bytes)
    {
        string tool = "host";
        string? module = null, goal = null, dialect = null;
        var inputs = new List<RecipeInput>();
        var sources = new List<RecipeSource>();
        var entries = new List<PredicateRef>();
        var dynlibs = new List<string>();
        var foreign = new List<string>();
        var native = new List<string>();
        bool consult = false, library = false, allowUndefined = false, strip = false, compiledIl = false,
             stripWam = false, wasm = false, stdlib = false, prunePrelude = false, regionPrune = true,
             debug = false, withSource = false, arityCompat = false;
        foreach (var (tag, value) in FieldReader.Fields(bytes))
        {
            switch ((Field)tag)
            {
                case Field.Tool: tool = FieldReader.Text(value); break;
                case Field.Module: module = FieldReader.Text(value); break;
                case Field.Input: inputs.Add(RecipeInput.FromBytes(value)); break;
                case Field.Source: sources.Add(RecipeSource.FromBytes(value)); break;
                case Field.Consult: consult = true; break;
                case Field.Library: library = true; break;
                case Field.Entry: entries.Add(ParseEntry(FieldReader.Text(value))); break;
                case Field.Goal: goal = FieldReader.Text(value); break;
                case Field.AllowUndefined: allowUndefined = true; break;
                case Field.Strip: strip = true; break;
                case Field.CompiledIl: compiledIl = true; break;
                case Field.StripWam: stripWam = true; break;
                case Field.Wasm: wasm = true; break;
                case Field.Stdlib: stdlib = true; break;
                case Field.PrunePrelude: prunePrelude = true; break;
                case Field.NoRegionPrune: regionPrune = false; break;
                case Field.DynamicLibrary: dynlibs.Add(FieldReader.Text(value)); break;
                case Field.ForeignAssembly: foreign.Add(FieldReader.Text(value)); break;
                case Field.NativeLibrary: native.Add(FieldReader.Text(value)); break;
                case Field.Debug: debug = true; break;
                case Field.WithSource: withSource = true; break;
                case Field.ArityCompat: arityCompat = true; break;
                case Field.Dialect: dialect = FieldReader.Text(value); break;
                // A field of a later engine under rules 1: skipped.
            }
        }
        return new BundleRecipe
        {
            Tool = tool, Module = module, Inputs = inputs, Sources = sources,
            Consult = consult, Library = library, Entries = entries, Goal = goal,
            AllowUndefined = allowUndefined, Strip = strip, CompiledIl = compiledIl,
            StripWam = stripWam, Wasm = wasm, Stdlib = stdlib, PrunePrelude = prunePrelude,
            RegionPrune = regionPrune, DynamicLibraries = dynlibs, ForeignAssemblies = foreign,
            NativeLibraries = native, Debug = debug, WithSource = withSource,
            ArityCompat = arityCompat, Dialect = dialect,
        };
    }

    private static PredicateRef ParseEntry(string text)
    {
        int slash = text.LastIndexOf('/');
        if (slash <= 0 || !int.TryParse(text.Substring(slash + 1), out int arity))
            throw new InvalidDataException($"The recipe of a compiled file names an entry '{text}'.");
        return new PredicateRef(text.Substring(0, slash), arity);
    }
}

/// <summary>What an input of a build is.</summary>
public enum RecipeInputKind : byte
{
    /// <summary>A source file, compiled by the build.</summary>
    Source = 0,
    /// <summary>A compiled object (<c>.shmo</c>).</summary>
    Object = 1,
    /// <summary>A librarian archive (<c>.shum</c>) linked against.</summary>
    Archive = 2,
}

/// <summary>An input of a build: a file by its name, and for an object or an
/// archive the recipe it carries itself.</summary>
public sealed class RecipeInput
{
    public RecipeInputKind Kind { get; init; }

    /// <summary>The file name, without the directory the build found it in.</summary>
    public string Name { get; init; } = "";

    /// <summary>The recipe of an object or an archive, as it carried it.</summary>
    public BundleRecipe? Recipe { get; init; }

    private enum Field : byte { Kind = 1, Name = 2, Recipe = 3 }

    internal byte[] ToBytes()
    {
        var w = new FieldWriter();
        w.Bytes((byte)Field.Kind, new[] { (byte)Kind });
        w.Text((byte)Field.Name, Name);
        if (Recipe is not null) w.Bytes((byte)Field.Recipe, Recipe.ToBytes());
        return w.ToArray();
    }

    internal static RecipeInput FromBytes(ReadOnlySpan<byte> bytes)
    {
        var kind = RecipeInputKind.Source;
        string name = "";
        BundleRecipe? recipe = null;
        foreach (var (tag, value) in FieldReader.Fields(bytes))
        {
            switch ((Field)tag)
            {
                case Field.Kind when value.Length == 1: kind = (RecipeInputKind)value[0]; break;
                case Field.Name: name = FieldReader.Text(value); break;
                case Field.Recipe: recipe = BundleRecipe.FromBytes(value); break;
            }
        }
        return new RecipeInput { Kind = kind, Name = name, Recipe = recipe };
    }
}

/// <summary>A source file a build read: its name, the SHA-256 of its bytes,
/// the time it was last written, in UTC, and for a build that carries its
/// sources (<c>--with-source</c>) the bytes.</summary>
public sealed class RecipeSource
{
    public string Name { get; init; } = "";
    public byte[] Digest { get; init; } = Array.Empty<byte>();
    public DateTime WrittenUtc { get; init; }

    /// <summary>The file's bytes, when the build carries them; else null.</summary>
    public byte[]? Contents { get; init; }

    private enum Field : byte { Name = 1, Digest = 2, Written = 3, Contents = 4 }

    // How the bytes of Contents are held: the first byte of the field.
    private const byte Raw = 0, Brotli = 1;

    /// <summary>The source of a file's bytes: its digest and the time given,
    /// and the bytes themselves when <paramref name="carry"/>.</summary>
    public static RecipeSource Of(string name, byte[] contents, DateTime writtenUtc, bool carry = false)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return new RecipeSource
        {
            Name = name,
            Digest = sha.ComputeHash(contents),
            WrittenUtc = DateTime.SpecifyKind(writtenUtc, DateTimeKind.Utc),
            Contents = carry ? contents : null,
        };
    }

    internal byte[] ToBytes()
    {
        var w = new FieldWriter();
        w.Text((byte)Field.Name, Name);
        w.Bytes((byte)Field.Digest, Digest);
        w.Bytes((byte)Field.Written, BitConverter.GetBytes(WrittenUtc.Ticks));
        if (Contents is not null)
        {
            // Compressed where this runtime has an encoder; every one decodes.
            byte[]? packed = BundleFormat.EncodeBrotli(Contents);
            byte[] value = new byte[1 + (packed ?? Contents).Length];
            value[0] = packed is null ? Raw : Brotli;
            Array.Copy(packed ?? Contents, 0, value, 1, value.Length - 1);
            w.Bytes((byte)Field.Contents, value);
        }
        return w.ToArray();
    }

    internal static RecipeSource FromBytes(ReadOnlySpan<byte> bytes)
    {
        string name = "";
        byte[] digest = Array.Empty<byte>();
        DateTime written = default;
        byte[]? contents = null;
        foreach (var (tag, value) in FieldReader.Fields(bytes))
        {
            switch ((Field)tag)
            {
                case Field.Name: name = FieldReader.Text(value); break;
                case Field.Digest: digest = value.ToArray(); break;
                case Field.Written when value.Length == 8:
                    written = new DateTime(BitConverter.ToInt64(value.ToArray(), 0), DateTimeKind.Utc);
                    break;
                case Field.Contents when value.Length >= 1:
                    contents = value[0] switch
                    {
                        Raw => value.AsSpan(1).ToArray(),
                        Brotli => BundleFormat.DecodeBrotli(value, 1, value.Length - 1),
                        _ => throw new InvalidDataException(
                            $"The recipe of a compiled file holds {name} in an unknown form."),
                    };
                    break;
            }
        }
        return new RecipeSource { Name = name, Digest = digest, WrittenUtc = written, Contents = contents };
    }
}

// The field encoding of rules 1: a tag byte, an int32 length, the bytes.
internal sealed class FieldWriter
{
    private readonly MemoryStream _ms = new();

    public void Bytes(byte tag, byte[] value)
    {
        _ms.WriteByte(tag);
        _ms.Write(BitConverter.GetBytes(value.Length), 0, 4);
        _ms.Write(value, 0, value.Length);
    }

    public void Text(byte tag, string text) => Bytes(tag, Encoding.UTF8.GetBytes(text));

    public void Flag(byte tag) => Bytes(tag, Array.Empty<byte>());

    public byte[] ToArray() => _ms.ToArray();
}

internal static class FieldReader
{
    public static List<(byte Tag, byte[] Value)> Fields(ReadOnlySpan<byte> bytes)
    {
        var fields = new List<(byte, byte[])>();
        int at = 0;
        while (at < bytes.Length)
        {
            if (bytes.Length - at < 5) throw Short();
            byte tag = bytes[at];
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at + 1));
            at += 5;
            if (length < 0 || length > bytes.Length - at) throw Short();
            fields.Add((tag, bytes.Slice(at, length).ToArray()));
            at += length;
        }
        return fields;
    }

    public static string Text(byte[] value) => Encoding.UTF8.GetString(value);

    private static InvalidDataException Short()
        => new("The recipe of a compiled file is cut short.");
}

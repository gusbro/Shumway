using System.Text;

namespace Shumway.Embedding;

/// <summary>ADR-076, decision 4: a compiled file of another format built
/// again from its recipe, under the rules it names, with this engine's
/// compiler and linker. Nothing is written: the result is the file's bytes
/// in this engine's format. A file that cannot be rebuilt is refused with the
/// reason (<see cref="RebuildRefusedException"/>, decision 5).</summary>
public static class BundleRebuild
{
    /// <summary>What a rebuild looks things up in.</summary>
    public sealed class Context
    {
        /// <summary>Where a source the file carries is placed, in memory: the
        /// directory the file was found in. Its relative paths resolve there.</summary>
        public string BaseDirectory { get; init; } = ".";

        /// <summary>The path of a source, an object or an archive found by its
        /// file name (beside the file, in a collection, on the library search
        /// path), or null.</summary>
        public Func<string, string?> FindFile { get; init; } = _ => null;

        public IReadOnlyList<string> LibraryDirs { get; init; } = Array.Empty<string>();

        /// <summary>Bakes the WebAssembly of a link that had it, where a host
        /// can; without it the rebuilt bundle carries none.</summary>
        public Func<Bundle, IReadOnlyList<byte[]>>? WasmBaker { get; init; }

        /// <summary>What the rebuild says: a source changed since the build.</summary>
        public Action<string> Warn { get; init; } = _ => { };
    }

    /// <summary>The bytes of <paramref name="image"/> built again in this
    /// engine's format. <paramref name="what"/> names the file in messages.</summary>
    public static byte[] Rebuild(byte[] image, string what, Context context)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(context);
        BundleRecipe recipe = RecipeOf(image, what);
        return recipe.Tool switch
        {
            "shumway-link" => Link(recipe, what, context),
            "shumway-lib" => Archive(recipe, what, context),
            "shumway-compile" => ShmoWriter.ToBytes(Compile(recipe, what, context)),
            _ => throw new RebuildRefusedException(Nothing(recipe.Tool, what)),
        };
    }

    /// <summary>The recipe of <paramref name="image"/>, or the refusal that
    /// says why there is none to rebuild it from.</summary>
    public static BundleRecipe RecipeOf(byte[] image, string what)
    {
        if (!BundleReader.IsBundle(image)
            && !(image.Length >= 4 && image.AsSpan(0, 4).SequenceEqual(ShmoFormat.Magic)))
            throw new RebuildRefusedException($"{what} is not a compiled file.");
        if (BundleFormat.HeaderRecipe(image) is not { } header)
            throw new RebuildRefusedException(
                $"{what} is in another format of compiled code and was built before compiled files "
                + "said how they were built: build it again.");
        return header.Recipe ?? throw new RebuildRefusedException(
            $"{what} was built under rebuild rules {header.Rules}, which this engine does not "
            + "rebuild: link it again with this engine's tools.");
    }

    private static string Nothing(string tool, string what) => tool switch
    {
        "save_state" => $"{what} is a saved state, the state of an engine: nothing builds it again.",
        "page" => $"{what} is a library a page compiled: the page compiles it again from its source.",
        _ => $"{what} was written by a host, with nothing it was built from: build it again.",
    };

    private static byte[] Link(BundleRecipe r, string what, Context c)
    {
        var inputs = new List<LinkInput>();
        foreach (RecipeInput input in r.Inputs)
            inputs.Add(input.Kind switch
            {
                RecipeInputKind.Source => SourceInput(r, input, what, c),
                RecipeInputKind.Object => ObjectInput(input, what, c),
                _ => ArchiveInput(input, what, c),
            });
        var said = new List<string>();
        var plan = new LinkPlan
        {
            Consult = r.Consult,
            Library = r.Library,
            EntryPoints = r.Entries,
            Goal = r.Goal,
            AllowUndefined = r.AllowUndefined,
            StripSource = r.Strip,
            IncludeCompiledIl = r.CompiledIl,
            StripWam = r.StripWam,
            Wasm = r.Wasm && c.WasmBaker is not null,
            BakePrelude = r.Stdlib,
            PrunePrelude = r.PrunePrelude,
            RegionPrune = r.RegionPrune,
            DynamicLibraries = r.DynamicLibraries,
            ForeignAssemblies = r.ForeignAssemblies.Select(n => c.FindFile(n) ?? n).ToList(),
            NativeLibraries = r.NativeLibraries.Select(n => c.FindFile(n) ?? n).ToList(),
            LibraryDirs = c.LibraryDirs,
            WasmBaker = c.WasmBaker,
            Report = said.Add,
        };
        LinkConfig config = plan.Prepare(inputs, out _)
            ?? throw Failed(what, said.FirstOrDefault() ?? "the link could not start");
        LinkResult result = ShmoLinker.Link(config);
        if (!result.Success)
            throw Failed(what, result.Diagnostics.FirstOrDefault(d => d.Severity == LinkSeverity.Error)?.Message
                ?? "the link failed");
        return result.Bytes!;
    }

    private static byte[] Archive(BundleRecipe r, string what, Context c)
    {
        var members = new List<BundleArchiveMember>();
        foreach (RecipeInput input in r.Inputs)
        {
            LinkInput member = ObjectInput(input, what, c);
            members.Add(new BundleArchiveMember(input.Name, ShmoWriter.ToBytes(member.Object!)));
        }
        return Librarian.CreateArchive(members, r.Wasm ? c.WasmBaker : null);
    }

    private static ShmoObject Compile(BundleRecipe r, string what, Context c)
    {
        if (r.Tool != "shumway-compile") throw new RebuildRefusedException(Nothing(r.Tool, what));
        var mode = r.Debug ? ShmoBuildMode.Debuggable : ShmoBuildMode.Release;
        var sources = r.Inputs.Where(i => i.Kind == RecipeInputKind.Source)
            .Select(i => SourceInput(r, i, what, c)).ToList();
        if (sources.Count == 0) throw Failed(what, "its recipe names no source");
        ShmoObject obj;
        if (r.Consult)
        {
            var errors = new List<ShmoCompileError>();
            var texts = sources.Where(s => s.Text is not null).ToDictionary(s => s.Path, s => s.Text!);
            var compiled = ShmoViaConsult.CompileMany(sources.Select(s => s.Path).ToList(), c.LibraryDirs,
                mode, errors, dialect: r.Dialect, withSource: r.WithSource,
                rootTexts: texts.Count > 0 ? texts : null);
            if (errors.Count > 0) throw Failed(what, errors[0].Message);
            obj = compiled.Find(x => x.ModuleName == r.Module).Object
                ?? throw Failed(what, $"consulting its sources brought in no module {r.Module}");
        }
        else
        {
            LinkInput source = sources[0];
            ShmoCompileResult res = source.Text is not null
                ? ShmoCompiler.TryCompileSource(source.Text, Path.GetFileNameWithoutExtension(source.Path),
                    mode, arityCompat: r.ArityCompat,
                    includeBaseDir: Path.GetDirectoryName(Path.GetFullPath(source.Path)), withSource: r.WithSource,
                    libraryDirs: c.LibraryDirs)
                : ShmoCompiler.TryCompileFile(source.Path, mode, arityCompat: r.ArityCompat, withSource: r.WithSource,
                    libraryDirs: c.LibraryDirs);
            obj = res.Object ?? throw Failed(what, res.Errors.FirstOrDefault()?.Message ?? "the compile failed");
        }
        // The recipe of what was compiled: the sources as they were taken.
        return obj.WithRecipe(new BundleRecipe
        {
            Tool = r.Tool,
            Module = r.Module,
            Inputs = r.Inputs,
            Sources = sources.Select(s => RecipeSource.Of(Path.GetFileName(s.Path),
                s.Text is not null ? Encoding.UTF8.GetBytes(s.Text) : File.ReadAllBytes(s.Path),
                s.WrittenUtc ?? File.GetLastWriteTimeUtc(s.Path), carry: r.WithSource)).ToList(),
            Consult = r.Consult,
            Debug = r.Debug,
            WithSource = r.WithSource,
            ArityCompat = r.ArityCompat,
            Dialect = r.Dialect,
        });
    }

    // A source: the file found by its name, or the copy the recipe carries.
    // Whether they differ is the digest's to say; when they do, the newer is
    // compiled, as library(X) takes the newer of a source and its bundle.
    private static LinkInput SourceInput(BundleRecipe r, RecipeInput input, string what, Context c)
    {
        RecipeSource? recorded = r.Sources.FirstOrDefault(s => s.Name == input.Name);
        byte[]? carried = recorded?.Contents;
        if (c.FindFile(input.Name) is { } found)
        {
            byte[] bytes = File.ReadAllBytes(found);
            DateTime written = File.GetLastWriteTimeUtc(found);
            bool same = recorded is not null && SameDigest(bytes, recorded.Digest);
            if (same || carried is null || written > recorded!.WrittenUtc)
            {
                if (!same && recorded is not null)
                    c.Warn($"{input.Name} changed since {what} was built: the rebuild compiles it as it is now.");
                return new LinkInput { Path = found };
            }
            c.Warn($"{input.Name} beside {what} is older than the copy {what} carries: the rebuild compiles the copy.");
        }
        if (carried is not null)
            return new LinkInput
            {
                Path = Path.Combine(c.BaseDirectory, input.Name),
                Text = Encoding.UTF8.GetString(carried),
                WrittenUtc = recorded!.WrittenUtc,
            };
        throw new RebuildRefusedException(
            $"{what} was built from {input.Name}, which is not here, and {what} does not carry it: "
            + "put it beside, or build it again.");
    }

    // An object: the one found by its name in this format, or one compiled
    // again from the recipe it carried.
    private static LinkInput ObjectInput(RecipeInput input, string what, Context c)
    {
        if (c.FindFile(input.Name) is { } found)
        {
            byte[] bytes = File.ReadAllBytes(found);
            if (ObjectIsCurrent(bytes)) return new LinkInput { Path = found, Object = ShmoReader.FromBytes(bytes) };
        }
        BundleRecipe recipe = input.Recipe ?? throw new RebuildRefusedException(
            $"{what} was built from the object {input.Name}, which carries no recipe: build it again.");
        return new LinkInput
        {
            Path = Path.Combine(c.BaseDirectory, input.Name),
            Object = Compile(recipe, input.Name, c),
        };
    }

    private static LinkInput ArchiveInput(RecipeInput input, string what, Context c)
    {
        if (c.FindFile(input.Name) is { } found)
        {
            byte[] bytes = File.ReadAllBytes(found);
            if (BundleReader.IsCurrent(bytes))
                return new LinkInput { Path = found, Archive = BundleReader.FromBytes(bytes) };
        }
        BundleRecipe recipe = input.Recipe ?? throw new RebuildRefusedException(
            $"{what} was built against the archive {input.Name}, which carries no recipe: build it again.");
        return new LinkInput
        {
            Path = Path.Combine(c.BaseDirectory, input.Name),
            Archive = BundleReader.FromBytes(Archive(recipe, input.Name, c)),
        };
    }

    private static bool ObjectIsCurrent(ReadOnlySpan<byte> data)
        => data.Length >= 16
           && data.Slice(0, 4).SequenceEqual(ShmoFormat.Magic)
           && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4)) == ShmoFormat.CurrentVersion
           && System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8)) == BundleFormat.Stamp;

    private static bool SameDigest(byte[] bytes, byte[] digest)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(bytes).AsSpan().SequenceEqual(digest);
    }

    private static RebuildRefusedException Failed(string what, string why)
        => new($"{what} could not be rebuilt: {why}");
}

/// <summary>ADR-076, decision 5: a compiled file of another format that cannot
/// be built again, with the reason.</summary>
public sealed class RebuildRefusedException : Exception
{
    public RebuildRefusedException(string message) : base(message) { }
}

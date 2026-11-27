namespace Shumway.Embedding;

/// <summary>ADR-076: a link from its inputs and the options that make its
/// product, what <c>shumway-link</c> runs and what a rebuild runs again from
/// a recipe. <see cref="Prepare"/> reads or compiles each input in order and
/// gives the link's configuration, with the recipe of the bundle it makes.
/// Inputs route by extension: a <c>.shum</c> is a librarian archive linked
/// against, a <c>.pl</c> a source compiled here, anything else an object.</summary>
public sealed class LinkPlan
{
    // The product: recorded in the recipe.

    /// <summary>The sources are consulted into one engine (<c>--consult</c>).</summary>
    public bool Consult { get; init; }
    public bool Library { get; init; }
    public IReadOnlyList<PredicateRef> EntryPoints { get; init; } = Array.Empty<PredicateRef>();
    public string? Goal { get; init; }

    /// <summary>An executable or a class library is made of the bundle: its
    /// goal is a startup goal, not a library's load goal.</summary>
    public bool Executable { get; init; }

    public bool AllowUndefined { get; init; }
    public bool StripSource { get; init; }
    public bool IncludeCompiledIl { get; init; }
    public bool StripWam { get; init; }
    public bool Wasm { get; init; }
    public bool BakePrelude { get; init; }
    public bool PrunePrelude { get; init; }
    public bool RegionPrune { get; init; } = true;

    /// <summary>The dynamic libraries (<c>--dynlib</c>): a <c>.shum</c>, or a
    /// name found in <see cref="LibraryDirs"/>.</summary>
    public IReadOnlyList<string> DynamicLibraries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ForeignAssemblies { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NativeLibraries { get; init; } = Array.Empty<string>();

    // Where to look and what to report: not in the recipe.

    public IReadOnlyList<string> LibraryDirs { get; init; } = Array.Empty<string>();
    public bool WarnShadow { get; init; }
    public bool RegionPruneReport { get; init; }
    public string? DumpWamPath { get; init; }
    public string? DumpIlPath { get; init; }
    public TextWriter? VerboseOut { get; init; }
    public Func<Bundle, IReadOnlyList<byte[]>>? WasmBaker { get; init; }

    /// <summary>Each line the plan says while it reads and compiles: an
    /// error, a warning, a hint.</summary>
    public Action<string> Report { get; init; } = _ => { };

    /// <summary>The link's configuration for <paramref name="inputPaths"/>, in
    /// order, or null when an input cannot be read or compiled, which
    /// <see cref="Report"/> says. <paramref name="usageError"/> is set when
    /// what failed is the goal given.</summary>
    public LinkConfig? Prepare(IReadOnlyList<string> inputPaths, out bool usageError)
        => Prepare(inputPaths.Select(p => new LinkInput { Path = p }).ToList(), out usageError);

    /// <summary>As <see cref="Prepare(IReadOnlyList{string}, out bool)"/>, for
    /// inputs a rebuild may hold in memory.</summary>
    public LinkConfig? Prepare(IReadOnlyList<LinkInput> inputs, out bool usageError)
    {
        usageError = false;
        var objects = new List<ShmoObject>();
        var libraries = new List<LinkLibrary>();
        var sourceInputs = new List<string>();
        var recipeInputs = new List<RecipeInput>();
        var recipeSources = new List<RecipeSource>();
        // The last input that is a module (an object or a source, not an
        // archive): a library's goal runs in it.
        string? lastInputModule = null;
        bool lastInputIsSource = false;
        var sourceTexts = new Dictionary<string, string>();
        foreach (var input in inputs)
        {
            string path = input.Path;
            string name = Path.GetFileName(path);
            try
            {
                if (path.EndsWith(".shum", StringComparison.OrdinalIgnoreCase))
                {
                    var lib = input.Archive ?? ReadOrRebuild(path, BundleReader.ReadFromFile,
                        bytes => BundleReader.FromBytes(bytes));
                    if (lib.ArchiveMembers.Count == 0)
                    {
                        Report($"shumway-link: library '{path}' is a linked bundle, not a "
                            + "librarian archive — it has no objects to link against. "
                            + "Build a library with shumway-lib.");
                        return null;
                    }
                    var members = new List<ShmoObject>(lib.ArchiveMembers.Count);
                    foreach (var m in lib.ArchiveMembers)
                        members.Add(ShmoReader.FromBytes(m.ShmoBytes));
                    libraries.Add(new LinkLibrary(name, members));
                    recipeInputs.Add(new RecipeInput { Kind = RecipeInputKind.Archive, Name = name, Recipe = lib.Recipe });
                }
                else if (path.EndsWith(".pl", StringComparison.OrdinalIgnoreCase))
                {
                    // Collected and compiled below: all together under
                    // --consult, one at a time otherwise.
                    sourceInputs.Add(path);
                    lastInputIsSource = true;
                    if (input.Text is not null) sourceTexts[path] = input.Text;
                    recipeInputs.Add(new RecipeInput { Kind = RecipeInputKind.Source, Name = name });
                    recipeSources.Add(RecipeSource.Of(name,
                        input.Text is not null ? System.Text.Encoding.UTF8.GetBytes(input.Text) : File.ReadAllBytes(path),
                        input.WrittenUtc ?? File.GetLastWriteTimeUtc(path)));
                }
                else
                {
                    var obj = input.Object ?? ReadOrRebuild(path, ShmoReader.ReadFromFile,
                        bytes => ShmoReader.FromBytes(bytes));
                    objects.Add(obj);
                    lastInputModule = obj.ModuleName;
                    lastInputIsSource = false;
                    recipeInputs.Add(new RecipeInput { Kind = RecipeInputKind.Object, Name = name, Recipe = obj.Recipe });
                }
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                Report($"shumway-link: error reading '{path}': {ex.Message}");
                return null;
            }
        }

        // ADR-075, decision 3: the file the consult attached for each
        // ensure_loaded(bundle(Name)) of the sources, linked against as a
        // --dynlib is.
        var consultAttached = new Dictionary<string, string>(StringComparer.Ordinal);
        if (sourceInputs.Count > 0)
        {
            if (Consult)
            {
                // All sources into one engine (directives and expansion hooks
                // run, use_module dependencies load), and every module the load
                // brought in linked: the shumway-compile --consult pipeline.
                var errors = new List<ShmoCompileError>();
                var roots = new List<string>();
                List<(string ModuleName, ShmoObject Object, DateTime SourceTimeUtc, bool IsRoot)> compiled;
                try
                {
                    compiled = ShmoViaConsult.CompileMany(
                        sourceInputs, LibraryDirs, ShmoBuildMode.Release, errors,
                        attachedLibraries: consultAttached, roots: roots,
                        rootTexts: sourceTexts.Count > 0 ? sourceTexts : null);
                }
                catch (Exception ex)
                {
                    Report($"shumway-link: consult failed: {ex.Message}");
                    return null;
                }
                foreach (var e in errors)
                    Report($"error: {e.Message}");
                if (errors.Count > 0 || compiled.Count == 0)
                {
                    Report($"shumway-link: {Math.Max(errors.Count, 1)} error(s) compiling sources (consult mode).");
                    return null;
                }
                foreach (var (_, obj, _, _) in compiled) objects.Add(obj);
                if (lastInputIsSource && roots.Count > 0) lastInputModule = roots[^1];
            }
            else
            {
                // One source at a time: no load-time hooks, no dependency
                // pull. So a small program need not be compiled to a .shmo first.
                foreach (var path in sourceInputs)
                {
                    var res = sourceTexts.TryGetValue(path, out string? text)
                        ? ShmoCompiler.TryCompileSource(text, Path.GetFileNameWithoutExtension(path),
                            ShmoBuildMode.Release,
                            includeBaseDir: Path.GetDirectoryName(Path.GetFullPath(path)))
                        : ShmoCompiler.TryCompileFile(path, ShmoBuildMode.Release);
                    foreach (var w in res.Warnings)
                        Report($"{w.Where(path)}: warning: {w.Message}");
                    if (res.Object is null)
                    {
                        foreach (var e in res.Errors)
                            Report($"{e.Where(path)}: error: {e.Message}");
                        HintConsult(path);
                        return null;
                    }
                    // The same refusal as shumway-compile's: a hook compiled
                    // file-at-a-time never fires, and the program linked from
                    // it would mean something else than the consulted one.
                    if (ShmoCompiler.WhatFileAtATimeLoses(res.Object) is { } lost)
                    {
                        Report($"shumway-link: {path} {lost} File-at-a-time compilation "
                            + "would produce a program that means something else than the "
                            + "consulted one, so it is refused rather than linked. Link "
                            + "it with:" + Environment.NewLine
                            + $"  shumway-link --consult -L <libdir> ... {path}");
                        return null;
                    }
                    HintConsult(path);
                    objects.Add(res.Object);
                    if (lastInputIsSource) lastInputModule = res.Object.ModuleName;
                }
            }
        }

        // The goal validates and contributes reachability roots. Any query
        // the top level accepts: its call-position predicates may be user,
        // builtin or prelude (`time(main)`), and any user predicate named
        // inside it (a meta-call argument) stays linked. A library's goal
        // without an executable is the goal the bundle runs when it loads, in
        // the module of the last input: the linker makes it that module's
        // last initialization goal, and it is no root.
        bool loadGoal = Library && !Executable && !string.IsNullOrEmpty(Goal);
        var goalCallRefs = new List<PredicateRef>();
        var goalTermRefs = new List<PredicateRef>();
        if (!string.IsNullOrEmpty(Goal))
        {
            if (!ExecutableEmitter.TryCollectGoalRefs(Goal!,
                    out goalCallRefs, out goalTermRefs, out string? goalErr))
            {
                Report($"shumway-link: --goal: {goalErr}");
                usageError = true;
                return null;
            }
            if (loadGoal)
            {
                goalCallRefs.Clear();
                goalTermRefs.Clear();
            }
        }

        var dynamicLibraries = new List<LinkDynamicLibrary>();
        foreach (string spec in DynamicLibraries)
        {
            string? path = LinkDynamicLibrary.Find(spec, LibraryDirs);
            if (path is null)
            {
                Report($"shumway-link: dynamic library '{spec}' not found: give its .shum, or its name "
                    + "with the directory that holds it in -L.");
                return null;
            }
            try
            {
                dynamicLibraries.Add(new LinkDynamicLibrary(
                    Path.GetFileNameWithoutExtension(path), BundleReader.ReadFromFile(path)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Report($"shumway-link: dynamic library {path}: {ex.Message}");
                return null;
            }
        }
        var givenDynamicLibraries = dynamicLibraries.Select(l => l.Name).ToList();
        foreach (var (name, path) in consultAttached)
        {
            if (dynamicLibraries.Exists(l => l.Name == name)) continue;
            try
            {
                dynamicLibraries.Add(new LinkDynamicLibrary(name, BundleReader.ReadFromFile(path)));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                Report($"shumway-link: dynamic library {path}: {ex.Message}");
                return null;
            }
        }

        var recipe = new BundleRecipe
        {
            Tool = "shumway-link",
            Inputs = recipeInputs,
            Sources = recipeSources,
            Consult = Consult,
            Library = Library,
            Entries = EntryPoints.ToList(),
            Goal = string.IsNullOrEmpty(Goal) ? null : Goal,
            AllowUndefined = AllowUndefined,
            Strip = StripSource,
            CompiledIl = IncludeCompiledIl,
            StripWam = StripWam,
            Wasm = Wasm,
            Stdlib = BakePrelude,
            PrunePrelude = PrunePrelude,
            RegionPrune = RegionPrune,
            DynamicLibraries = givenDynamicLibraries,
            ForeignAssemblies = ForeignAssemblies.Select(Path.GetFileName).ToList()!,
            NativeLibraries = NativeLibraries.Select(Path.GetFileName).ToList()!,
        };

        return new LinkConfig
        {
            Objects = objects,
            DynamicLibraries = dynamicLibraries,
            EntryPoints = EntryPoints,
            GoalCallRefs = goalCallRefs,
            GoalTermRefs = goalTermRefs,
            AllowUndefined = AllowUndefined,
            WarnShadow = WarnShadow,
            Libraries = libraries,
            LibraryDirs = LibraryDirs.ToList(),
            BakePrelude = BakePrelude,
            PrunePrelude = PrunePrelude,
            Library = Library,
            LoadGoal = loadGoal ? Goal : null,
            LoadGoalModule = loadGoal ? lastInputModule : null,
            VerboseOut = VerboseOut,
            StripSource = StripSource,
            IncludeCompiledIl = IncludeCompiledIl,
            StripWam = StripWam,
            RegionPruneReport = RegionPruneReport,
            RegionPrune = RegionPrune,
            DumpWamPath = DumpWamPath,
            DumpIlPath = DumpIlPath,
            ForeignAssemblies = ForeignAssemblies.ToList(),
            NativeLibraries = NativeLibraries.ToList(),
            WasmBaker = Wasm ? WasmBaker : null,
            Recipe = recipe,
        };
    }

    // ADR-076: an object or an archive of another format is built again from
    // its recipe, in memory, from the sources beside it or on the library
    // search path; refused, the reason is the read error.
    private T ReadOrRebuild<T>(string path, Func<string, T> read, Func<byte[], T> fromBytes)
    {
        try { return read(path); }
        catch (InvalidDataException) when (IsOfAnotherFormat(path))
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var dirs = new List<string> { dir };
            dirs.AddRange(LibraryDirs);
            byte[] rebuilt;
            try
            {
                rebuilt = BundleRebuild.Rebuild(File.ReadAllBytes(path), Path.GetFileName(path),
                    new BundleRebuild.Context
                    {
                        BaseDirectory = dir,
                        FindFile = file => dirs.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists),
                        LibraryDirs = dirs,
                        WasmBaker = WasmBaker,
                        Warn = message => Report("shumway-link: warning: " + message),
                    });
            }
            catch (RebuildRefusedException ex) { throw new InvalidDataException(ex.Message); }
            Report($"shumway-link: {Path.GetFileName(path)} is in another format of compiled code: "
                + "rebuilt from what it says it was built from.");
            return fromBytes(rebuilt);
        }
    }

    private static bool IsOfAnotherFormat(string path)
    {
        try { return BundleFormat.HeaderRecipe(File.ReadAllBytes(path)) is not null; }
        catch (IOException) { return false; }
    }

    // A source with library dependencies or load-time hooks, compiled one
    // file at a time: the consult pipeline is what loads those.
    private void HintConsult(string path)
    {
        string text;
        try { text = File.Exists(path) ? File.ReadAllText(path) : ""; } catch { return; }
        bool hasLibDeps = text.Contains("use_module(library(");
        bool hasHooks = text.Contains("term_expansion(")
            || text.Contains("goal_expansion(")
            || text.Contains(":- attribute");
        if (hasLibDeps || hasHooks)
            Report($"shumway-link: hint: {path} uses library dependencies or load-time "
                + "hooks that a file-at-a-time compile does not load; link it through "
                + "the consult pipeline instead:\n"
                + $"  shumway-link --consult -L <libdir> ... {path}");
    }
}

/// <summary>An input of a <see cref="LinkPlan"/>: a file by its path, which a
/// rebuild may hold in memory: a source's text and the time it was written,
/// an object or an archive already read.</summary>
public sealed class LinkInput
{
    public string Path { get; init; } = "";
    public string? Text { get; init; }
    public DateTime? WrittenUtc { get; init; }
    public ShmoObject? Object { get; init; }
    public Bundle? Archive { get; init; }
}

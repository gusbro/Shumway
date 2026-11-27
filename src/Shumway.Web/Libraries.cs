using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using System.Collections.Concurrent;
using System.Runtime.InteropServices.JavaScript;

namespace Shumway.Web;

/// <summary>
/// Libraries the user brought in, so <c>:- use_module(library(clpz)).</c> works
/// in a page.
///
/// <para>What is imported is a collection — a directory of Prolog sources on the
/// engine's library search path (ADR-038). Scryer's <c>lib/</c> is one folder
/// and forty-six libraries: every <c>x.pl</c> in it is <c>library(x)</c>. So a
/// collection is named for where it came from, and the libraries are what it
/// contains. It may carry a dialect (ADR-040): a library resolved from a
/// directory tagged <c>scryer</c> loads under Scryer's name resolution and
/// double_quotes, which is what lets Scryer's and SWI's versions of the same
/// library coexist.</para>
///
/// <para>Compilation is per library, not per collection: nobody wants to wait
/// for forty-six when they came for one. A compiled <c>x.shum</c> sits at the
/// collection's root, which the search path reaches before the sources, so it
/// is what <c>library(x)</c> resolves to from then on.</para>
///
/// <para>Libraries are global: they are not part of any workspace, they survive
/// switching between them, and they do not travel in a workspace's zip or
/// share link. Every engine — a fresh one after a workspace switch included —
/// registers them again at startup, which is bookkeeping and costs nothing; a
/// library's clauses arrive when a program imports it.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    internal const string LibrariesRoot = "/libraries";

    /// <summary>Where a library's sources live, under its own directory. Not on
    /// the search path: what resolves is the compiled bundle beside it, so a
    /// source edited here does nothing until the library is compiled again.
    /// That rule is the layout rather than a permission — the alternative was a
    /// read-only flag somebody has to enforce.
    ///
    /// <para>Until a library has been compiled, this directory is searched too,
    /// so an uncompiled library still works — just slowly.</para></summary>
    private const string SourceDir = "src";

    /// <summary>Names the dialect a library loads under. A file rather than a
    /// setting: it belongs to the library, so it survives in the same place the
    /// sources do and cannot drift from them. Hidden from the file listings.</summary>
    private const string DialectMarker = ".dialect";

    /// <summary>What the last compile of one library had to say, kept beside its
    /// bundle as <c>&lt;library&gt;.diag</c>.
    ///
    /// <para>It exists because importing a collection compiles forty-odd
    /// libraries nobody asked for one at a time, and what they have to say is
    /// not the page's output: a foreign-interface library from another system
    /// declares functions this engine has no way to bind, and reporting that
    /// forty times buries whatever the user was doing. So the diagnostics go
    /// here and the library list shows which libraries have one — the user
    /// learns that a library is unusable when they look for a library, not
    /// while they are typing a query.</para>
    ///
    /// <para>First line is <c>failed</c> or <c>warned</c>, second is the
    /// headline, the rest is the detail. The kind is written rather than
    /// inferred: a library that compiled once and then failed still has its old
    /// bundle beside it, so the bundle's presence does not say how the last
    /// compile went.</para></summary>
    private const string DiagnosticSuffix = ".diag";

    /// <summary>Where a collection came from: the URL or the file it was
    /// imported from, so importing it again from there is known to be the
    /// same collection and one from elsewhere under its name is refused.</summary>
    private const string SourceMarker = ".source";

    /// <summary>How many warning lines one compile's diagnostic keeps.</summary>
    private const int DiagnosticLineLimit = 200;

    /// <summary>Puts every imported library on <paramref name="engine"/>'s
    /// search path: the page's engine at each start, a notebook session's at
    /// its start and before each cell. Adding a directory twice adds it once.</summary>
    internal static void RegisterLibraries(PrologEngine engine)
    {
        Directory.CreateDirectory(LibrariesRoot);
        engine.LibrarySourceResolver = (library, source) => CompileOnDemand(engine, library, source);
        engine.LibraryBundleFilter = (_, path, bundle) => TakesStored(path, bundle);
        foreach (string dir in Directory.GetDirectories(LibrariesRoot))
        {
            string marker = Path.Combine(dir, DialectMarker);
            string? dialect = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            AddCollection(engine, dir, dialect);
        }
    }

    /// <summary>The page's own engine, or null in a notebook page, which
    /// never starts one: its engines are its kernels' sessions (ADR-068).</summary>
    private static PrologEngine? PageEngine => _only?.TopLevel?.Engine;

    /// <summary>Where the library exports run: queued on the page's engine
    /// when there is one, since a consult reads these files; on a pool thread
    /// in a notebook page.</summary>
    private static Task<T> OnLibraries<T>(Func<T> work)
        => PageEngine is not null ? S.OnEngine(work) : Task.Run(work);

    /// <summary>The imported libraries, newline-separated, sorted.</summary>
    [JSExport]
    internal static Task<string> LibraryNames()
        => OnLibraries(() =>
        {
            Directory.CreateDirectory(LibrariesRoot);
            var names = Directory.GetDirectories(LibrariesRoot)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n, StringComparer.Ordinal);
            return string.Join('\n', names!);
        });

    /// <summary>The dialect a library loads under, or the empty string.</summary>
    [JSExport]
    internal static Task<string> LibraryDialect(string name)
        => OnLibraries(() =>
        {
            string marker = Path.Combine(ResolveLibrary(name), DialectMarker);
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        });

    /// <summary>Creates (or re-tags) a library and puts it on the search path.
    /// Returns null, or the error text.</summary>
    [JSExport]
    internal static Task<string?> LibraryCreate(string name, string dialect)
        => OnLibraries(() =>
        {
            if (InvalidWorkspaceName(name) is { } bad) return bad;
            try
            {
                string dir = ResolveLibrary(name);
                Directory.CreateDirectory(dir);
                string marker = Path.Combine(dir, DialectMarker);
                if (string.IsNullOrWhiteSpace(dialect))
                {
                    if (File.Exists(marker)) File.Delete(marker);
                    PageEngine?.AddLibraryDirectory(dir);
                }
                else
                {
                    // Tag first: an unknown dialect must not leave a marker
                    // behind that every later boot would trip over. A notebook
                    // page has no engine to ask: its dialects come checked,
                    // and a session registers an unknown one untagged.
                    PageEngine?.AddLibraryDirectory(dir, dialect.Trim());
                    File.WriteAllText(marker, dialect.Trim());
                }
                PageEngine?.AddLibraryDirectory(Path.Combine(dir, SourceDir));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>Where a collection was imported from, or the empty string
    /// when that was not kept: it was imported before the marker existed.</summary>
    [JSExport]
    internal static Task<string> LibrarySource(string name)
        => OnLibraries(() =>
        {
            string marker = Path.Combine(ResolveLibrary(name), SourceMarker);
            return File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        });

    /// <summary>Records where a collection was imported from. Returns null,
    /// or the error text.</summary>
    [JSExport]
    internal static Task<string?> LibrarySetSource(string name, string source)
        => OnLibraries(() =>
        {
            try
            {
                string dir = ResolveLibrary(name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, SourceMarker), source.Trim());
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>What a .zip, .tar.gz or .tar archive of a collection holds,
    /// as a JSON array, or <c>{"error": ...}</c>: <c>{path, text}</c> for a
    /// Prolog source, and <c>{path, bundle}</c> for the bundle of a library
    /// compiled ahead (<c>x.shum</c>), whose bytes stay here until
    /// <see cref="LibraryTakeBundle"/> is given that <c>bundle</c>.</summary>
    [JSExport]
    internal static Task<string> LibraryUnpack([JSMarshalAs<JSType.Array<JSType.Number>>] byte[] archive)
        => Task.Run(() =>
        {
            using var buffer = new MemoryStream();
            using (var json = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                try
                {
                    var contents = Shumway.WebHost.LibraryArchive.Read(archive);
                    json.WriteStartArray();
                    foreach ((string path, string text) in contents.Sources)
                    {
                        json.WriteStartObject();
                        json.WriteString("path", path);
                        json.WriteString("text", text);
                        json.WriteEndObject();
                    }
                    // The bundles of one archive at a time: an import that
                    // was given up leaves the last one's here, and no more.
                    UnpackedBundles.Clear();
                    foreach ((string path, byte[] bytes) in contents.Bundles)
                    {
                        string id = Guid.NewGuid().ToString("N");
                        UnpackedBundles[id] = bytes;
                        json.WriteStartObject();
                        json.WriteString("path", path);
                        json.WriteString("bundle", id);
                        json.WriteBoolean("current", BundleReader.IsCurrent(bytes));
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    json.WriteStartObject();
                    json.WriteString("error", ex.Message);
                    json.WriteEndObject();
                }
            }
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        });

    // What the last archive unpacked carried compiled, until it is taken.
    private static readonly ConcurrentDictionary<string, byte[]> UnpackedBundles = new();

    /// <summary>Puts into a collection the bundle an archive carried of one
    /// of its libraries (<see cref="LibraryUnpack"/>), where
    /// <c>library(X)</c> finds it, as if this engine had compiled it. Answers
    /// null, or why it was not put: one written with another format of
    /// compiled code is left out, and the library compiles from its source
    /// the first time it is used. <paramref name="path"/> is the bundle's
    /// path in the archive, <c>x.shum</c> beside <c>x.pl</c>.</summary>
    [JSExport]
    internal static Task<string?> LibraryTakeBundle(string name, string path, string bundle)
        => OnLibraries(() =>
        {
            try
            {
                if (!UnpackedBundles.TryRemove(bundle, out byte[]? taken))
                    return "its bytes are not here any more";
                string library = path.Substring(0, path.Length - ".shum".Length);
                foreach (string segment in library.Split('/'))
                    if (segment.Length == 0 || segment == "..") return "not a path inside the collection";
                string root = ResolveLibrary(name);
                if (ToStore(name, root, library, taken, imported: true, out string? why) is not { } bytes)
                    return why;
                string target = Path.Combine(root, library + ".shum");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string partial = target + ".partial";
                File.WriteAllBytes(partial, bytes);
                File.Move(partial, target, overwrite: true);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>ADR-076: what to store of a bundle a collection brings, or one
    /// the page kept: one of this format, raw, so it is decoded once here and
    /// not at every load; one of another format built again from its recipe
    /// with the collection's sources. A library the page compiled, and one it
    /// kept from before bundles said how they were built, is compiled again
    /// from its source when it is used. Null, with the reason, for one that
    /// is not stored; one refused is recorded, and is not compiled from its
    /// source in its place.</summary>
    private static byte[]? ToStore(string name, string root, string library, byte[] bytes,
        bool imported, out string? why)
    {
        why = null;
        if (BundleReader.IsCurrent(bytes))
        {
            ClearRefusal(root, library);
            return BundleFormat.Uncompressed(bytes);
        }
        var header = BundleFormat.HeaderRecipe(bytes);
        if (header?.Recipe is { Tool: "page" } || header is null && !imported)
        {
            why = "written with another format of compiled code";
            return null;
        }
        try
        {
            byte[] rebuilt = BundleRebuild.Rebuild(bytes, library + ".shum", RebuildIn(root));
            ClearRefusal(root, library);
            if (!imported) _compiledOnDemand.Enqueue($"{name}\t{library}");
            return BundleFormat.Uncompressed(rebuilt);
        }
        catch (Exception ex) when (ex is RebuildRefusedException or InvalidDataException or IOException)
        {
            why = ex.Message;
            RecordRefusal(root, library, ex.Message);
            return null;
        }
    }

    // What a rebuild in a collection looks things up in: its sources, then
    // the collection itself.
    private static BundleRebuild.Context RebuildIn(string root)
    {
        string sources = Path.Combine(root, SourceDir);
        var dirs = new List<string> { sources, root };
        string marker = Path.Combine(root, DialectMarker);
        string? dialect = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        if (string.IsNullOrEmpty(dialect)) dialect = null;
        return new BundleRebuild.Context
        {
            BaseDirectory = sources,
            FindFile = file => dirs.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists),
            LibraryDirs = dirs,
            WasmBaker = RuntimeCaps.SupportsWasmCodegen && !Tier.Disabled
                ? b => BakeSessionFor(root, dialect).Bake(b, BakeLog)
                : null,
        };
    }

    // A refused bundle reads as a failed library, and its reason is what a
    // load of it says (CompileOnDemand). Kept with the diagnostic, which the
    // page stores.
    private static void RecordRefusal(string root, string library, string reason)
    {
        try
        {
            File.WriteAllText(Path.Combine(root, library + DiagnosticSuffix),
                string.Join("\n", "refused", Flatten(reason), reason));
        }
        catch { /* a diagnostic that cannot be filed is not worth failing over */ }
    }

    private static string? RefusalOf(string root, string library)
    {
        string marker = Path.Combine(root, library + DiagnosticSuffix);
        try
        {
            if (!File.Exists(marker)) return null;
            var lines = File.ReadLines(marker).ToList();
            return lines.Count >= 3 && lines[0] == "refused" ? string.Join("\n", lines.Skip(2)) : null;
        }
        catch (IOException) { return null; }
    }

    private static void ClearRefusal(string root, string library)
    {
        if (RefusalOf(root, library) is null) return;
        try { File.Delete(Path.Combine(root, library + DiagnosticSuffix)); }
        catch (IOException) { }
    }

    /// <summary>Whether the first bytes of a bundle say it is in the format of
    /// compiled code this engine reads.</summary>
    [JSExport]
    internal static Task<bool> LibraryBundleIsCurrent(
        [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] head)
        => Task.FromResult(BundleReader.IsCurrent(head));

    /// <summary>Writes beside a library that came as its bundle alone the
    /// source the bundle carries of it (<see cref="Shumway.WebHost.LibraryArchive.SourceOfBundle"/>),
    /// where it can be read as any library's source, and where the library
    /// compiles from when the bundle cannot be read any more. True when it
    /// wrote one; never over a source that is there.</summary>
    [JSExport]
    internal static Task<bool> LibraryWriteBundleSource(string name, string library)
        => OnLibraries(() =>
        {
            string root = ResolveLibrary(name);
            string bundle = Path.Combine(root, library + ".shum");
            string source = Path.Combine(root, SourceDir, library + ".pl");
            if (!File.Exists(bundle) || File.Exists(source)) return false;
            if (Shumway.WebHost.LibraryArchive.SourceToWrite(File.ReadAllBytes(bundle), library,
                    File.GetLastWriteTimeUtc(bundle)) is not var (text, written))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, text);
            // Not newer than the bundle, which stays what library(X) loads.
            File.SetLastWriteTimeUtc(source, written);
            return true;
        });

    /// <summary>A file the engine sees, base64-encoded: an archive or a source
    /// a notebook imports from its own files.</summary>
    [JSExport]
    internal static Task<string> LibraryReadLocal(string path)
        => Task.Run(() => Convert.ToBase64String(File.ReadAllBytes(path)));

    /// <summary>Compiles a library's sources into a bundle beside them, which is
    /// then what <c>library(X)</c> resolves to.
    ///
    /// <para>Worth the wait it costs once: Scryer's clpz loads about six times
    /// faster from a bundle than from source, because the compiling — the
    /// expensive part — has already happened. Compiled by consulting, the only
    /// way that works for a library which generates clauses as it loads, which
    /// is exactly what clpz and its attributed-variable machinery do.</para>
    ///
    /// <para>Returns null, or the diagnostic. Without <paramref name="force"/>
    /// a library that has a bundle already is left as it is: the batch queued
    /// it before a consult compiled it on demand.</para></summary>
    [JSExport]
    internal static Task<string?> LibraryCompile(string name, string library, bool force)
        // Not on the engine gate. Compiling builds its own ephemeral engine and
        // never touches the session's, so holding the gate for its whole
        // duration bought nothing and cost everything: pressing Consult while a
        // big library compiled meant waiting minutes for it to finish. Two
        // engines at once is the model working as designed — activations are
        // single-threaded internally and thread-agile between them, and the
        // tables they share are thread-safe.
        => Task.Run<string?>(() =>
        {
            string root = ResolveLibrary(name);
            EnterCompileGate(onDemand: false);
            try
            {
                if (!force && HasCurrentBundle(root, library)) return null;
                return CompileLibrary(root, name, library);
            }
            finally { ExitCompileGate(); }
        });

    // One compile at a time, and one a consult is waiting for goes first: the
    // batch finishes the library it is on and starts no other until then. Not
    // the engine gate: compiling builds its own engine (see LibraryCompile).
    private static readonly object CompileGate = new();
    private static bool _compiling;
    private static int _onDemandWaiting;

    private static void EnterCompileGate(bool onDemand)
    {
        lock (CompileGate)
        {
            if (onDemand) _onDemandWaiting++;
            // CA1416: blocking is unsupported on the browser's main thread.
            // Both callers are pool threads: the batch's Task.Run, and a
            // consult or query on the engine's (CompileOnDemand refuses the
            // JavaScript thread).
#pragma warning disable CA1416
            while (_compiling || (!onDemand && _onDemandWaiting > 0))
                Monitor.Wait(CompileGate);
#pragma warning restore CA1416
            if (onDemand) _onDemandWaiting--;
            _compiling = true;
        }
    }

    private static void ExitCompileGate()
    {
        lock (CompileGate)
        {
            _bakeSession = null;
            _bakeSessionRoot = null;
            _compiling = false;
            Monitor.PulseAll(CompileGate);
        }
    }

    // The baking engine of the compile holding the gate, shared by every
    // library it compiles: one engine with the prelude and the collection's
    // libraries loaded once, not one per library.
    private static WasmBakeSession? _bakeSession;
    private static string? _bakeSessionRoot;

    // A measurement switch: false bakes each library in an engine of its
    // own, as before the session, to compare the two in one page.
    private static bool _sharedBake = true;

    // A Task: the threaded runtime refuses a synchronous export.
    [JSExport]
    internal static Task LibraryBakeShared(bool on)
    {
        _sharedBake = on;
        return Task.CompletedTask;
    }

    // The bake's own account of its phases, beside the compile's.
    private static void BakeLog(string line)
    {
        if (!line.StartsWith("wasm: skipping", StringComparison.Ordinal))
            _compileLog?.WriteLine("%   " + line);
    }

    private static WasmBakeSession BakeSessionFor(string root, string? dialect)
    {
        if (!_sharedBake) return new WasmBakeSession(e => AddCollection(e, root, dialect));
        if (_bakeSession is null || _bakeSessionRoot != root)
        {
            _bakeSession = new WasmBakeSession(e => AddCollection(e, root, dialect));
            _bakeSessionRoot = root;
        }
        return _bakeSession;
    }

    // Libraries compiled on demand since the page last asked: it stores them,
    // as it stores what the batch compiles, so a reload does not compile them
    // again.
    private static readonly ConcurrentQueue<string> _compiledOnDemand = new();

    /// <summary>The libraries a consult, a query or the compile of another
    /// library compiled because it needed them, since the last call: one per
    /// line, the collection, a tab and the library.</summary>
    [JSExport]
    internal static Task<string> LibraryTakeCompiledOnDemand() => Answered(TakeCompiledOnDemand());

    /// <summary>What <see cref="LibraryTakeCompiledOnDemand"/> answers.</summary>
    private static string TakeCompiledOnDemand()
    {
        var taken = new List<string>();
        while (_compiledOnDemand.TryDequeue(out string? entry)) taken.Add(entry);
        return string.Join('\n', taken);
    }

    /// <summary>The engine's <see cref="PrologEngine.LibrarySourceResolver"/>:
    /// a library of an imported collection that has no bundle yet is compiled
    /// when a program first needs it, and the bundle is what loads.
    ///
    /// <para>Loading its sources instead mixed them with the libraries the
    /// batch had compiled already, and a source that relies on another
    /// library's expansion hooks has to load the way it compiles: everything
    /// from source, in one engine. Loading from source is also several times
    /// slower, every time; this is one compile, kept from then on.</para></summary>
    private static string? CompileOnDemand(PrologEngine engine, string library, string source)
    {
        string? sources = Path.GetDirectoryName(source);
        string? root = sources is null ? null : Path.GetDirectoryName(sources);
        if (root is null || Path.GetFileName(sources) != SourceDir
            || Path.GetDirectoryName(root) != LibrariesRoot)
            return null;   // not a library of an imported collection
        // Waiting for a compile there would freeze the page: load the source.
        if (SynchronizationContext.Current == _jsThread) return null;
        string name = Path.GetFileName(root);
        string target = Path.Combine(root, library + ".shum");
        // A bundle the collection brought that could not be rebuilt: its
        // source is not compiled in its place (ADR-076, decision 5).
        if (RefusalOf(root, library) is { } refused)
            throw new InvalidDataException($"library({library}) from {name}: {refused}");
        // Its last compile failed: trying again on every consult would only
        // repeat the wait, and the list already says why.
        if (LastCompileFailed(root, library)) return null;

        TextWriter say = engine.Out;
        say.WriteLine($"% library({library}) from {name} is not compiled yet: compiling it now."
            + " This happens once; the compiled library is kept for next time.");
        say.Flush();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? failed;
        EnterCompileGate(onDemand: true);
        try
        {
            _compileLog = say;
            // The batch may have been compiling this very library. A bundle
            // of another format is as none: the compile writes over it.
            failed = HasCurrentBundle(root, library) ? null : CompileLibrary(root, name, library);
        }
        finally
        {
            _compileLog = null;
            ExitCompileGate();
        }

        if (failed is null && HasCurrentBundle(root, library))
        {
            _compiledOnDemand.Enqueue($"{name}\t{library}");
            say.WriteLine($"% library({library}) compiled in {clock.Elapsed.TotalSeconds:0.#} s.");
            say.Flush();
            return target;
        }
        say.WriteLine($"% library({library}) did not compile ({failed}); loading it from source."
            + " The library list has the details.");
        say.Flush();
        return null;
    }

    /// <summary>The engine's <see cref="PrologEngine.LibraryBundleFilter"/>.
    /// A bundle a browser kept may carry a WebAssembly module another build
    /// of the engine baked. Such a module is refused at every install, and
    /// the library would run from what the tier compiles for itself at every
    /// load: the bundle goes, and the resolver, asked next, compiles the
    /// library again. The page stores what that compiles over what it had.
    ///
    /// <para>Asked as the library loads, about the bundle the load has read:
    /// looking when the page puts the stored bundles back read every one of
    /// them a second time, used or not. With the tier off the bundle stays:
    /// compiled now, it would carry no module at all.</para></summary>
    private static bool TakesStored(string path, Bundle bundle)
    {
        if (!RuntimeCaps.SupportsWasmCodegen || Tier.Disabled) return true;
        if (WasmBundleTier.ModulesAreCurrent(bundle)) return true;
        // Only what an imported collection compiled: there is a source to
        // compile it from again.
        if (Path.GetDirectoryName(Path.GetDirectoryName(path)) != LibrariesRoot) return true;
        try { File.Delete(path); }
        catch (IOException) { return true; }
        return false;
    }

    /// <summary>Whether the library has a bundle this engine reads: one of
    /// its format (<see cref="BundleFormat.Stamp"/>), which the first bytes
    /// of the file say.</summary>
    private static bool HasCurrentBundle(string root, string library)
        => BundleReader.IsCurrentFile(Path.Combine(root, library + ".shum"));

    private static bool LastCompileFailed(string root, string library)
    {
        string marker = Path.Combine(root, library + DiagnosticSuffix);
        try { return File.Exists(marker) && File.ReadLines(marker).FirstOrDefault() == "failed"; }
        catch (IOException) { return false; }
    }

    /// <summary>A library the engine replaces with its own (Scryer's builtins,
    /// format, dif, ...): loading it gives the engine's version, so there is
    /// nothing to compile, and a bundle of it would only be loaded in its place.</summary>
    private static bool ProvidedNatively(string root, string library)
        => PrologEngine.ProvidesLibraryNatively(
            library, Path.Combine(root, SourceDir, library + ".pl"));

    /// <summary>Compiles one library into its bundle. Returns null, or the
    /// diagnostic. Callers hold the compile gate.
    ///
    /// <para>ADR-038 §4: the libraries of the collection it imports load from
    /// their own bundles, and one that has none yet is compiled first (see
    /// <see cref="CompileDependency"/>). The bundle holds this library's
    /// modules only; loading it loads those libraries first.
    /// <paramref name="compiling"/> names the libraries on their way up the
    /// chain.</para></summary>
    private static string? CompileLibrary(string root, string name, string library,
                                          HashSet<string>? compiling = null)
    {
        _dependencySeconds.Push(0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double bakeSeconds = 0;
        try
        {
            string? failed = CompileLibrary(root, name, library, compiling,
                                            s => bakeSeconds = s, out double consultSeconds);
            double dependencies = _dependencySeconds.Peek();
            if (failed is null)
                _compileLog?.WriteLine(
                    $"% library({library}) compiled in {clock.Elapsed.TotalSeconds:0.0} s: "
                    + $"consult {consultSeconds - dependencies:0.0} s, wasm module {bakeSeconds:0.0} s"
                    + (dependencies > 0 ? $", the libraries it imports {dependencies:0.0} s." : "."));
            return failed;
        }
        finally
        {
            _dependencySeconds.Pop();
            if (_dependencySeconds.Count > 0)
                _dependencySeconds.Push(_dependencySeconds.Pop() + clock.Elapsed.TotalSeconds);
        }
    }

    // Where an on-demand compile says how long each library took, and the
    // time of the libraries each one on the way up compiled first. The
    // compile gate makes both single-threaded.
    private static TextWriter? _compileLog;
    private static readonly Stack<double> _dependencySeconds = new();

    private static string? CompileLibrary(string root, string name, string library,
                                          HashSet<string>? compiling, Action<double> baked,
                                          out double consultSeconds)
    {
        consultSeconds = 0;
        if (ProvidedNatively(root, library))
            return $"{library} is provided by Shumway itself: there is nothing to compile";
        string sources = Path.Combine(root, SourceDir);
        string entry = Path.Combine(sources, library + ".pl");
        if (!File.Exists(entry)) return $"{name} has no {library}.pl";

        string marker = Path.Combine(root, DialectMarker);
        string? dialect = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        if (string.IsNullOrEmpty(dialect)) dialect = null;
        compiling ??= new HashSet<string>(StringComparer.Ordinal);
        compiling.Add(library);

        // The consult's warnings are caught, not printed. This runs on the
        // page's behalf rather than the user's, and what a library from
        // another system says while it loads is not their output.
        var said = new StringWriter();
        try
        {
            var errors = new List<ShmoCompileError>();
            // The root first, as on the page's engine: a bundle there is what
            // library(X) finds.
            var consultClock = System.Diagnostics.Stopwatch.StartNew();
            var compiled = ShmoViaConsult.CompileMany(
                new[] { entry }, new[] { root, sources }, ShmoBuildMode.Release, errors,
                dialect, said,
                configure: e => e.LibrarySourceResolver =
                    (dep, source) => CompileDependency(root, name, dep, source, compiling));
            consultSeconds = consultClock.Elapsed.TotalSeconds;
            if (errors.Count > 0)
            {
                string text = string.Join(
                    "\n", errors.Select(e => $"{e.Line}:{e.Column}: {e.Message}"));
                return RecordDiagnostic(root, library, text, said, failed: true);
            }
            if (compiled.Count == 0)
                return RecordDiagnostic(root, library, "nothing compiled", said, failed: true);

            // Packed by the librarian, not the linker: a library has no entry
            // point, so there is nothing to compute reachability from — every
            // module it brought in is kept, but for another library of the
            // collection, which has a bundle of its own. One the engine
            // provides has none, so it stays. Under the wasm tier
            // the archive also carries its predicates as a wasm module, baked
            // here once (the batch compile the tier would otherwise run on
            // every visit) and installed when the library loads.
            bool wasm = RuntimeCaps.SupportsWasmCodegen && !Tier.Disabled;
            byte[] bytes = Librarian.CreateArchive(compiled
                .Where(c => c.IsRoot || !HasBundleOfItsOwn(root, c.ModuleName))
                .Select(c => new BundleArchiveMember(
                    c.ModuleName + ".shmo", ShmoWriter.ToBytes(c.Object)))
                .ToList(),
                wasm ? b =>
                     {
                         var bakeClock = System.Diagnostics.Stopwatch.StartNew();
                         try
                         {
                             return BakeSessionFor(root, dialect).Bake(b, BakeLog);
                         }
                         finally { baked(bakeClock.Elapsed.TotalSeconds); }
                     }
                     : null,
                // ADR-076: compiled from its entry by consult, as here.
                new BundleRecipe
                {
                    Tool = "page",
                    Inputs = new[] { new RecipeInput { Kind = RecipeInputKind.Source, Name = library + ".pl" } },
                    Sources = new[] { RecipeSource.Of(library + ".pl", File.ReadAllBytes(entry),
                        File.GetLastWriteTimeUtc(entry)) },
                    Consult = true,
                    Dialect = dialect,
                    Wasm = wasm,
                });
            // Written under a temporary name and moved into place, so what
            // `library(X)` can see is either the old bundle or the new one
            // and never half of one — a consult may look while this runs.
            string target = Path.Combine(root, library + ".shum");
            string partial = target + ".partial";
            File.WriteAllBytes(partial, bytes);
            File.Move(partial, target, overwrite: true);
            RecordDiagnostic(root, library, null, said, failed: false);
            return null;
        }
        catch (Exception ex)
        {
            return RecordDiagnostic(root, library, ex.Message, said, failed: true);
        }
        finally { compiling.Remove(library); }
    }

    /// <summary>The compiling engine's resolver: a library of the same
    /// collection with no bundle yet is compiled into its own, which the
    /// library importing it then loads. Null loads the source: a library
    /// further up the chain (an import cycle), or one that does not compile.
    /// The page stores what this compiled, as it stores the batch's.</summary>
    private static string? CompileDependency(
        string root, string name, string library, string source, HashSet<string> compiling)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(source));
        if (dir != Path.GetFullPath(Path.Combine(root, SourceDir))) return null;
        if (compiling.Contains(library) || LastCompileFailed(root, library)) return null;
        string target = Path.Combine(root, library + ".shum");
        if (!HasCurrentBundle(root, library))
        {
            if (CompileLibrary(root, name, library, compiling) is not null) return null;
            _compiledOnDemand.Enqueue($"{name}\t{library}");
        }
        return HasCurrentBundle(root, library) ? target : null;
    }

    /// <summary>Whether <paramref name="module"/> is a library of the collection
    /// that compiles into a bundle of its own, so another library's bundle
    /// leaves it out.</summary>
    private static bool HasBundleOfItsOwn(string root, string module)
        => File.Exists(Path.Combine(root, SourceDir, module + ".pl"))
           && !ProvidedNatively(root, module);

    /// <summary>Puts a collection on an engine's search path: its root first,
    /// so a compiled bundle there is what <c>library(X)</c> finds, its sources
    /// after, so an uncompiled library still resolves.</summary>
    private static void AddCollection(PrologEngine engine, string root, string? dialect)
    {
        foreach (string searched in new[] { root, Path.Combine(root, SourceDir) })
        {
            try
            {
                if (string.IsNullOrEmpty(dialect)) engine.AddLibraryDirectory(searched);
                else engine.AddLibraryDirectory(searched, dialect);
            }
            catch (ArgumentException)
            {
                // An unknown dialect (a marker written by an older build, or
                // by hand). The library is still usable, just untagged.
                engine.AddLibraryDirectory(searched);
            }
        }
    }

    /// <summary>Files what a compile had to say, and gives back the headline —
    /// which is what <see cref="LibraryCompile"/> returns, so a caller that
    /// compiles one library on purpose still sees why it did not work.
    ///
    /// <para>A clean compile removes the marker: the record describes the last
    /// compile, and a library that has been fixed must stop reading as
    /// broken.</para></summary>
    private static string? RecordDiagnostic(
        string root, string library, string? error, StringWriter said, bool failed)
    {
        string marker = Path.Combine(root, library + DiagnosticSuffix);
        var lines = said.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        if (!failed && lines.Count == 0)
        {
            if (File.Exists(marker)) File.Delete(marker);
            return null;
        }

        string headline = error is { Length: > 0 }
            ? Flatten(error)
            : $"compiled, but {lines.Count} warning{(lines.Count == 1 ? "" : "s")} while loading it";

        var body = new List<string> { failed ? "failed" : "warned", headline };
        if (error is { Length: > 0 }) body.Add(error);
        if (lines.Count > DiagnosticLineLimit)
        {
            body.AddRange(lines.Take(DiagnosticLineLimit));
            body.Add($"… and {lines.Count - DiagnosticLineLimit} more");
        }
        else body.AddRange(lines);

        try { File.WriteAllText(marker, string.Join("\n", body)); }
        catch { /* a diagnostic that cannot be filed is not worth failing over */ }
        return failed ? headline : null;
    }

    /// <summary>One line, short enough to sit in a list row.</summary>
    private static string Flatten(string text)
    {
        string first = text.Split('\n')[0].TrimEnd('\r').Replace('\t', ' ').Trim();
        return first.Length <= 140 ? first : first[..140] + "…";
    }

    /// <summary>The libraries a collection provides: the Prolog files at the top
    /// of it, which are the names <c>library(X)</c> can ask for. Each line is
    /// the name, a tab, the state — <c>compiled</c>, <c>source</c> or
    /// <c>failed</c> — and, when the last compile had something to say, another
    /// tab and its headline.</summary>
    [JSExport]
    internal static Task<string> LibraryEntries(string name)
        => OnLibraries(() =>
        {
            string root = ResolveLibrary(name);
            string sources = Path.Combine(root, SourceDir);
            var withSource = Directory.Exists(sources)
                ? Directory.GetFiles(sources, "*.pl", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Select(n => n!)
                    .ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            // A library that came as its bundle alone: what it loads from is
            // there, and nothing to compile it from.
            var alone = Directory.Exists(root)
                ? Directory.GetFiles(root, "*.shum", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension)
                    .Where(n => !string.IsNullOrEmpty(n) && !withSource.Contains(n!))
                    .Select(n => n!)
                : Enumerable.Empty<string>();
            var entries = withSource.Select(n => DescribeEntry(root, n))
                .Concat(alone.Select(n => $"{n}\tbundle"))
                .OrderBy(line => line, StringComparer.Ordinal);
            return string.Join('\n', entries);
        });

    private static string DescribeEntry(string root, string library)
    {
        if (ProvidedNatively(root, library)) return $"{library}\tnative";
        var (kind, headline) = ReadDiagnostic(root, library);
        string state = kind is "failed" or "refused" ? "failed"
            : HasCurrentBundle(root, library) ? "compiled"
            : "source";
        return headline is null ? $"{library}\t{state}" : $"{library}\t{state}\t{headline}";
    }

    /// <summary>The kind and headline of a library's filed diagnostic, or
    /// nulls when the last compile was clean.</summary>
    private static (string? Kind, string? Headline) ReadDiagnostic(string root, string library)
    {
        string marker = Path.Combine(root, library + DiagnosticSuffix);
        if (!File.Exists(marker)) return (null, null);
        try
        {
            string[] lines = File.ReadAllLines(marker);
            if (lines.Length < 2) return (null, null);
            return (lines[0].Trim(), lines[1]);
        }
        catch { return (null, null); }
    }

    /// <summary>Everything the last compile of one library had to say, or the
    /// empty string. What the library list's <c>details</c> shows.</summary>
    [JSExport]
    internal static Task<string> LibraryDiagnostic(string name, string library)
        => OnLibraries(() =>
        {
            string marker = Path.Combine(ResolveLibrary(name), library + DiagnosticSuffix);
            return File.Exists(marker) ? File.ReadAllText(marker) : "";
        });

    /// <summary>Puts a stored diagnostic back, so a library that could not be
    /// compiled still reads as unusable after a reload — that is when it
    /// matters, since the batch that found out runs once, at import.</summary>
    [JSExport]
    internal static Task<string?> LibraryPutDiagnostic(string name, string library, string text)
        => OnLibraries(() =>
        {
            try
            {
                string root = ResolveLibrary(name);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, library + DiagnosticSuffix), text);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>Removes a library and its files. It stays on the engine's search
    /// path until the next engine — a path that resolves nothing is harmless.</summary>
    [JSExport]
    internal static Task<string?> LibraryRemove(string name)
        => OnLibraries(() =>
        {
            if (InvalidWorkspaceName(name) is { } bad) return bad;
            try
            {
                string dir = ResolveLibrary(name);
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>A library's files, newline-separated, sorted — including those in
    /// subdirectories, as relative paths, since <c>library(dcg/basics)</c> is a
    /// real thing. The dialect marker is not one of them.</summary>
    [JSExport]
    internal static Task<string> LibraryFiles(string name)
        => OnLibraries(() =>
        {
            string dir = Path.Combine(ResolveLibrary(name), SourceDir);
            if (!Directory.Exists(dir)) return "";
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal);
            return string.Join('\n', files);
        });

    /// <summary>One compiled bundle, base64-encoded, or the empty string when
    /// there is none — so the page can put it in storage and not have to compile
    /// the library again on every visit. Base64 because a promise cannot carry
    /// a byte array; the page stores the bytes.</summary>
    [JSExport]
    internal static Task<string> LibraryBundle(string name, string library)
        => OnLibraries(() =>
        {
            string path = Path.Combine(ResolveLibrary(name), library + ".shum");
            return File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : "";
        });

    /// <summary>Puts a stored bundle back, and answers null, or why it was
    /// not put: what the page stored is not a bundle of this engine's format.
    /// A page closed while it stored one leaves an empty file, and a bundle
    /// kept from an engine with another format of compiled code says so in
    /// its first bytes (<see cref="BundleReader.IsCurrent(ReadOnlySpan{byte})"/>).
    /// The page drops from its storage what is not put back, and the library
    /// compiles again when it is used. Bytes, not base64: every page load
    /// restores every stored bundle, and decoding strings of tens of
    /// megabytes held the page up. Also how a folder or files picked on this
    /// computer bring a library compiled ahead: <paramref name="library"/> is
    /// then its path in the collection, which may name a directory.</summary>
    [JSExport]
    internal static Task<string?> LibraryPutBundle(
        string name, string library, [JSMarshalAs<JSType.Array<JSType.Number>>] byte[] given, bool imported)
        => OnLibraries(() =>
        {
            try
            {
                foreach (string segment in library.Split('/'))
                    if (segment.Length == 0 || segment == "..") return "not a path inside the collection";
                string root = ResolveLibrary(name);
                if (ToStore(name, root, library, given, imported, out string? why) is not { } bundle)
                    return why;
                // Moved into place, as a compile's is: a query may be
                // looking for library(X) while this writes.
                string target = Path.Combine(root, library + ".shum");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string partial = target + ".partial";
                File.WriteAllBytes(partial, bundle);
                File.Move(partial, target, overwrite: true);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    /// <summary>A library file's contents, or null when there is no such file.</summary>
    [JSExport]
    internal static Task<string?> LibraryRead(string name, string file)
        => OnLibraries(() =>
        {
            string path = ResolveLibraryFile(name, file);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });

    /// <summary>Creates or replaces a library file. Returns null, or the error.</summary>
    [JSExport]
    internal static Task<string?> LibraryWrite(string name, string file, string content)
        => OnLibraries(() =>
        {
            try
            {
                string path = ResolveLibraryFile(name, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
                return (string?)null;
            }
            catch (Exception ex) { return ex.Message; }
        });

    private static string ResolveLibrary(string name)
    {
        if (InvalidWorkspaceName(name) is { } bad) throw new ArgumentException(bad, nameof(name));
        return LibrariesRoot + "/" + name;
    }

    /// <summary>Rejects a path that would leave the library. Its files arrive
    /// from a folder the user picked, and a relative path out of one is not
    /// something to follow.</summary>
    private static string ResolveLibraryFile(string name, string file)
    {
        ArgumentException.ThrowIfNullOrEmpty(file);
        // A library's files are its sources; the bundle beside them is built,
        // not edited.
        string root = Path.Combine(ResolveLibrary(name), SourceDir);
        string full = Path.GetFullPath(Path.Combine(root, file));
        if (!full.StartsWith(root + "/", StringComparison.Ordinal))
            throw new ArgumentException($"'{file}' is outside the library", nameof(file));
        return full;
    }
}

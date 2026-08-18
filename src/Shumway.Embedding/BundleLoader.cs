using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

/// <summary>
/// The bundle loader (extracted component): .shum loading (source-carrying
/// and precompiled-bytecode entries, archive members, dynamic seeds,
/// operators, native-interop restore), persisted Tier-1 IL (patching,
/// process-wide assembly cache, delegate registration), the shared static
/// link cache, the link-time Call-to-CallIl rewrites, and the Arity
/// save/restore state snapshots. First-stage extraction: back-references
/// the owning engine (E); the seam narrows in a later pass.
/// </summary>
internal sealed class BundleLoader
{
    private readonly PrologEngine E;
    public BundleLoader(PrologEngine engine) => E = engine;

    /// <summary>Loads a Shumway bundle (.shum) from disk and consults every
    /// module inside it. Equivalent to calling <see cref="ConsultString"/>
    /// for each entry in the bundle's manifest, in order. Throws
    /// <see cref="InvalidDataException"/> if the file isn't a valid
    /// bundle.</summary>
    public void LoadBundle(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Bundle bundle = BundleReader.ReadFromFile(path);
        // pass the bundle's directory so the foreign-
        // assembly auto-loader can find sibling DLLs (the typical
        // `myapp.shum` + `MyForeigns.dll` layout).
        LoadBundleCore(bundle, System.IO.Path.GetDirectoryName(
            System.IO.Path.GetFullPath(path)));
    }

    /// <summary>Loads an in-memory <see cref="Bundle"/> into this engine —
    /// useful for tests and for in-process pipelines that prefer not to
    /// round-trip through disk. Entries that carry a pre-compiled
    /// bytecode blob get their IL-eligible
    /// predicates eagerly warmed via <see cref="E.IlPromotion"/>'s
    /// <c>Warm</c> path; the precompiled clause list is cached on
    /// <see cref="PrecompiledClauseCache"/> so subsequent query setups
    /// can skip the WAM compile for those clauses.</summary>
    public void LoadBundle(Bundle bundle) => LoadBundleCore(bundle, bundleDir: null);

    /// <summary>ADR-035 — write a bundle entry's embedded source to a stable file the
    /// debugger can open, and return its full path. Named for the module so
    /// <see cref="Shumway.Core.DebugSiteTable"/>'s base-name file identity matches the
    /// breakpoint a user draws on it. This is the exact text the module was compiled from —
    /// which is the whole point of preferring it to a same-named <c>.pl</c> on disk that may
    /// have drifted.
    ///
    /// <para>All of a program's modules share ONE directory, keyed by the EXECUTABLE (not the
    /// process id): re-running the same binary materialises to the SAME paths, so the debugger
    /// reuses its source windows and the breakpoints bound to those paths survive the new run —
    /// instead of opening a second identical window per module and orphaning every breakpoint
    /// (which is what a per-process directory did). One directory per program, N files for N
    /// modules — not N directories.</para>
    ///
    /// <para>The file is made READ-ONLY. There is no hot relinking — an edit here could not
    /// reach the running code, so it would only diverge silently from what executes. (The day
    /// we can reload edited source, drop the read-only flag.) If the module's source changed
    /// since a prior run (a recompile), the file is rewritten in place at the same path.</para></summary>
    private static string MaterialiseDebugSource(string moduleName, string source)
    {
        // ADR-035 — write CONSISTENT line endings. The embedded source can carry mixed
        // CRLF/LF (a file edited on more than one platform), and the debugger's editor flags
        // that on open. Normalising CRLF -> LF -> CRLF removes the mix without moving any line:
        // every `\n` boundary the compiler counted the stop-site lines against is preserved, so
        // breakpoints and the entry stop still land where they should.
        string normalised = source.Replace("\r\n", "\n").Replace("\n", "\r\n");

        // One directory for the whole program, keyed by the executable path; the FILE keeps its
        // clean "<module>.pl" name — the window title, and the base name the DebugSiteTable
        // matches stop sites against.
        string dir = Path.Combine(Path.GetTempPath(), "shumway-debug", ProgramKey());
        Directory.CreateDirectory(dir);

        var safe = new char[moduleName.Length == 0 ? 1 : moduleName.Length];
        char[] invalid = Path.GetInvalidFileNameChars();
        if (moduleName.Length == 0) safe[0] = '_';
        for (int i = 0; i < moduleName.Length; i++)
            safe[i] = Array.IndexOf(invalid, moduleName[i]) >= 0 ? '_' : moduleName[i];
        string path = Path.Combine(dir, new string(safe) + ".pl");

        try
        {
            bool exists = File.Exists(path);
            // Rewrite only when the text actually changed (a recompile) — a read of a read-only
            // file is fine; the write needs the flag cleared first.
            if (!exists || File.ReadAllText(path) != normalised)
            {
                if (exists)
                {
                    var a = File.GetAttributes(path);
                    if ((a & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
                }
                File.WriteAllText(path, normalised);
            }

            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) == 0)
                File.SetAttributes(path, attrs | FileAttributes.ReadOnly);
        }
        catch (IOException) { /* open in the debugger, same content — the path is what we need */ }
        catch (UnauthorizedAccessException) { /* already read-only from a prior run */ }
        return path;
    }

    /// <summary>A short, STABLE (cross-process) key for the program, so all of its modules share
    /// one materialised-source directory that re-runs of the same binary reuse. The executable
    /// path (SHA-256, not <see cref="string.GetHashCode"/> — that is randomised per process, so
    /// it would give a different directory every run and defeat the whole point).</summary>
    private static string ProgramKey()
    {
        string exe = Environment.ProcessPath ?? AppContext.BaseDirectory ?? "shumway";
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(exe));
        var sb = new System.Text.StringBuilder(16);
        for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    /// <summary>ADR-035 — does this entry's bytecode carry the baked debug side tables (was it
    /// compiled <see cref="ShmoBuildMode.Debuggable"/>)? Decodes the module and looks for any
    /// stop site — a real module compiled debuggable always has some (even a fact gets one).
    /// Only ever called on the debug-session load path, so the decode cost is never on a
    /// release load; the decode re-interns the sites, which is idempotent with the load that
    /// follows.</summary>
    private static bool EntryCarriesDebugWam(BundleEntry entry)
    {
        if (entry.CompiledBytecode is not { Length: > 0 }) return false;
        try
        {
            var module = CompiledModuleCodec.Decode(entry.CompiledBytecode);
            foreach (var pred in module.Predicates)
                if (pred.DebugStops.Count > 0) return true;
        }
        catch (Exception) { /* undecodable → not a debug entry we can use directly */ }
        return false;
    }

    // The RequiresUnreferencedCode call below (RegisterForeignAssembly) is reached
    // only for a bundle that DECLARES foreign assemblies — a deployment that must
    // ship those DLLs beside the bundle anyway, and therefore cannot rely on
    // trimming to reason about them. Propagating the attribute instead would brand
    // every LoadBundle trim-unsafe, including the overwhelming majority of bundles
    // that declare none (a browser bundle can declare none at all).
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Only reached when the bundle declares foreign assemblies, "
        + "which are loaded from disk beside it and are outside the trimmer's view "
        + "by construction.")]
    /// <summary>Arity programs assume unknown=fail (a call to a predicate
    /// nothing defined or asserted just fails). Applied when a loaded
    /// bundle carries the Arity bit — separate compilation must not lose
    /// the CALL semantics the sources were written against. Deliberately
    /// does NOT flip arity_compat itself: that is a consult/parse mode,
    /// and it would leak into unrelated files consulted after the bundle
    /// (their goal directives would be skipped as Arity annotations).</summary>
    private void ApplyArityRuntimeFlags()
    {
        E._flags.Unknown = "fail";
    }

    internal void LoadBundleCore(Bundle bundle, string? bundleDir)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        // A bundle linked from Arity modules expects Arity call semantics:
        // a call to an undefined (or abolished) predicate FAILS. Set the
        // flags before any entry loads; an explicit later
        // set_prolog_flag(unknown, _) still overrides.
        if (bundle.ArityCompat) ApplyArityRuntimeFlags();
        // auto-register every foreign DLL the linker
        // recorded into the bundle. Each entry is a filename only;
        // we look for it adjacent to the bundle file first, then
        // alongside the executable (AppContext.BaseDirectory), then
        // fall back to the runtime's normal Assembly.Load resolution.
        // A missing DLL throws — same loudness as a missing predicate
        // would surface at first call.
        foreach (var asmName in bundle.ForeignAssemblies)
        {
            string? resolved = PrologEngine.ResolveForeignAssemblyPath(asmName, bundleDir);
            if (resolved is null)
                throw new FileNotFoundException(
                    $"Bundle declared a foreign assembly '{asmName}' but no matching file "
                    + "was found next to the bundle, next to the executable, or via the "
                    + "default Assembly.Load probe path.",
                    asmName);
            E.RegisterForeignAssembly(resolved);
        }
        // ADR-024 — native C libraries (--native-dll): load each so a `:- native`
        // function resolves by P/Invoke. Probed like a foreign assembly (next to
        // the bundle / executable), then the OS loader's default search.
        foreach (var libName in bundle.NativeLibraries)
        {
            string probe = PrologEngine.ResolveForeignAssemblyPath(libName, bundleDir) ?? libName;
            E.UseNativeLibrary(probe);
        }
        // A shumway-lib librarian archive stores its modules as verbatim
        // .shmo objects (bundle.ArchiveMembers) rather than post-link
        // Entries. Derive a runnable entry from each — exactly the fields a
        // .shmo carries that the per-entry load path below needs — and run it
        // through the same machinery, so an archive loads identically to
        // consulting each object's source / loading its compiled bytecode.
        // No linking or pruning happens: every member is kept verbatim.
        IReadOnlyList<BundleEntry> effectiveEntries = bundle.Entries;
        if (bundle.ArchiveMembers.Count > 0)
        {
            var combined = new List<BundleEntry>(
                bundle.Entries.Count + bundle.ArchiveMembers.Count);
            combined.AddRange(bundle.Entries);
            foreach (var member in bundle.ArchiveMembers)
            {
                var shmo = ShmoReader.FromBytes(member.ShmoBytes);
                // librarian archives keep the per-object flag — any Arity
                // member switches the runtime to Arity call semantics.
                if (shmo.ArityCompat) ApplyArityRuntimeFlags();
                combined.Add(new BundleEntry(
                    shmo.ModuleName, shmo.Source,
                    compiledBytecode: shmo.Bytecode,
                    compiledIl: null,
                    defined: shmo.Defined,
                    compiledIlPatches: null,
                    compiledIlEntries: null,
                    dynamicSeeds: shmo.DynamicSeeds,
                    nativeBlocks: shmo.NativeBlocks,
                    operators: shmo.Operators,
                    isExportQualified: shmo.IsExportQualified,
                    exports: shmo.Exports,
                    imports: shmo.Imports,
                    dialect: shmo.Dialect));
            }
            effectiveEntries = combined;
        }
        // A bundle may bake a precompiled `$prelude` entry (shumway-link
        // --exe / --stdlib) so a bare engine (FromBundle / the generated
        // --exe) gets the prelude without compiling it. A NORMAL engine
        // already consulted the prelude in its constructor, so that entry is
        // redundant here — drop it to avoid a double install.
        if (E._modules.ContainsKey(Prelude.ModuleName)
            && effectiveEntries.Any(e => e.ModuleName == Prelude.ModuleName))
        {
            effectiveEntries = effectiveEntries
                .Where(e => e.ModuleName != Prelude.ModuleName).ToList();
        }
        foreach (var entry in effectiveEntries)
        {
            // replay the entry's `:- op/3` definitions
            // into the runtime operator table BEFORE loading it. A
            // source-stripped entry otherwise loses its ops entirely (the
            // debug path re-executes them via ConsultString, for which this
            // replay is an idempotent no-op) — and any runtime read/1 /
            // string_term/2 of text using them would mis-parse.
            foreach (var od in entry.Operators)
            {
                var opType = od.Type switch
                {
                    "fx" => Shumway.Compiler.Parsing.OperatorType.Fx,
                    "fy" => Shumway.Compiler.Parsing.OperatorType.Fy,
                    "xf" => Shumway.Compiler.Parsing.OperatorType.Xf,
                    "yf" => Shumway.Compiler.Parsing.OperatorType.Yf,
                    "xfx" => Shumway.Compiler.Parsing.OperatorType.Xfx,
                    "xfy" => Shumway.Compiler.Parsing.OperatorType.Xfy,
                    "yfx" => Shumway.Compiler.Parsing.OperatorType.Yfx,
                    _ => (Shumway.Compiler.Parsing.OperatorType?)null,
                } ;
                if (opType is { } t) E.DefineOperator(od.Name, od.Priority, t);
            }
            // source-less load. When the bundle was built
            // with --strip (or compiled in Release with
            // source omission), Source is empty and we cannot
            // ConsultString. The entry's CompiledBytecode + Defined
            // metadata carry everything we need — the bytecode is
            // already runtime-ready (mangled) and the
            // Defined list tells us which functors are public /
            // dynamic / local. Set up a ModuleManifest from the
            // metadata and queue the precompiled predicates for the
            // static-link region; SetupQueryFromTerm will plug them
            // in next time it rebuilds the link.
            if (string.IsNullOrEmpty(entry.Source)
                && entry.CompiledBytecode is not null
                && entry.Defined.Count > 0)
            {
                LoadEntryFromBytecode(entry);
                continue;
            }
            // ADR-035 — a Debuggable bundle bakes the debug-shape WAM (frames, Y-slots, no
            // trimming/LCO, stop sites + frame/variable maps) straight into its bytecode. Under
            // a debug session we run THAT directly — no re-consult, zero recompile at load — and
            // materialise the embedded source only for the debugger to open. The baked stop
            // sites already reference "<module>.pl" (by base name), and the materialised file
            // has that base name, so interning its full path upgrades the site's file to an
            // openable one; the stop sites then flow into the query-setup breakpoint index
            // (SetupQueryFromTerm) exactly as a fresh debug consult's would.
            if (E._flags.DebugCodegen && entry.CompiledBytecode is { Length: > 0 }
                && entry.Defined.Count > 0 && EntryCarriesDebugWam(entry))
            {
                string dbgFile = MaterialiseDebugSource(entry.ModuleName, entry.Source);
                Shumway.Core.DebugSiteTable.InternFile(dbgFile);   // upgrade to an openable path
                if (E.DebugSession is not null)
                    Debugging.ShumwayDebugHelper.NoteSourceFile(dbgFile);
                LoadEntryFromBytecode(entry);
                continue;
            }

            // Otherwise (a non-Debuggable source-carrying entry): when debugging, show the code
            // FROM that source. The source-stripped entry took the bytecode branch above; there
            // is nothing to show but the module name, and the debugger resolves it the ordinary
            // way (by module name to a `<module>.pl` on disk). But here the exact text the
            // module was compiled from is in hand, so materialise it to a file the debugger can
            // open and stamp this consult's stop sites with that path — a breakpoint in the
            // .shum's code then resolves to the code that is IN the .shum, not a possibly-
            // different .pl someone happens to have on disk. (This re-consult path is the
            // fallback for a Debug — not Debuggable — bundle loaded under a debug session.)
            int prevDebugFile = E._debugFileId;
            try
            {
                if (E._flags.DebugCodegen)
                {
                    string materialised = MaterialiseDebugSource(entry.ModuleName, entry.Source);
                    E._debugFileId = Shumway.Core.DebugSiteTable.InternFile(materialised);
                    if (E.DebugSession is not null)
                        Debugging.ShumwayDebugHelper.NoteSourceFile(materialised);
                }
                // consult under the entry's module name so a
                // module-less file keeps the per-file module identity its
                // .shmo bytecode was compiled (and mangled) with, instead of
                // merging into the rolling "user" module.
                E.ConsultStringInner(entry.Source, recordInHistory: true,
                    moduleNameFallback: entry.ModuleName);
            }
            finally
            {
                E._debugFileId = prevDebugFile;
            }
        }

        // Bind persisted Tier-1 IL. RegisterBoundDelegate is first-wins, so this
        // must precede any Sigil warm — else warm compiles a region root
        // standalone and blocks the persisted delegate. A source-STRIPPED entry
        // warms inside LoadEntryFromBytecode (the entry loop above), which is why
        // that path binds its OWN persisted IL first (BindPersistedIlForEntry at
        // the top of LoadEntryFromBytecode); this loop is the idempotent
        // whole-bundle pass (cached, first-wins) that also covers source-carrying
        // entries, whose warm runs in the loop below.
        foreach (var entry in effectiveEntries)
            BindPersistedIlForEntry(entry);

        // Decode each source-carrying entry's CompiledModule and feed IL warmup
        // (its PrecompiledClauseCache substitution remains active — made the
        // .shmo bytecode byte-identical to what SetupQueryFromTerm would
        // produce, so the warmed IL delegates' call sites resolve correctly).
        foreach (var entry in effectiveEntries)
        {
            if (entry.CompiledBytecode is null) continue;
            // Source-less entries already decoded above (via LoadEntryFromBytecode).
            if (E._precompiledModules.ContainsKey(entry.ModuleName)
                && string.IsNullOrEmpty(entry.Source)) continue;
            // Source-carrying: the source consult is the truth, so the bytecode is an
            // IL-warm / skip-compile cache only — don't register static predicates.
            // (The shared helper also remaps literals, which fixes float value-baking
            // for a warmed-from-bytecode source-carrying predicate under Threshold>0.)
            DecodeAndRegisterPrecompiledModule(entry, registerStaticPredicates: false);
        }
        // A bundle's predicates join the static program — drop the
        // ADR-015 cached static linked region so the next query rebuilds it.
        E._staticLink = null;
        E.InvalidatePersistent();

        // Cross-process functor-id drift diagnostic (see PersistedIlBuilder).
        var dumpFidsEnv = System.Environment.GetEnvironmentVariable("SHUMWAY_PERSIST_DUMP_FIDS");
        if (!string.IsNullOrEmpty(dumpFidsEnv))
        {
            foreach (var ind in dumpFidsEnv.Split(','))
            {
                var slash = ind.IndexOf('/');
                if (slash < 0) continue;
                if (!int.TryParse(ind.AsSpan(slash + 1), out int ar)) continue;
                string nm = ind.Substring(0, slash);
                int aid = Shumway.Core.AtomTable.Intern(nm).Id;
                int fid = Shumway.Core.FunctorTable.Intern(aid, ar);
                System.Console.Error.WriteLine($"[load-fid] {nm}/{ar} atom={aid} functor={fid}");
            }
        }
    }

    /// <summary>overwrites every build-time atom-id / functor-id
    /// / resume-marker constant in <paramref name="ilBytes"/> with the
    /// runtime-process equivalent, in-place. The patch sites carry the
    /// build-process <c>(name, arity)</c> pair plus a recorded absolute
    /// byte offset; for each, we intern the name in the current process,
    /// compute the runtime id (or recompute the resume marker via
    /// <see cref="Shumway.Core.Activation.EncodeResumeMarker"/>), and write
    /// the four little-endian bytes back into <paramref name="ilBytes"/>
    /// at that offset. Runs BEFORE <c>Assembly.Load</c> so the JIT sees
    /// runtime values as inline IL constants — zero per-dispatch
    /// overhead.</summary>
    /// <summary>Stage B.1 — populate the interpreter's
    /// IlByFunctorId table from <see cref="E.IlPromotion"/>, then rewrite
    /// every <see cref="Shumway.Core.Opcode.Call"/> site whose callee
    /// already has a registered IL delegate into the equivalent
    /// <see cref="Shumway.Core.Opcode.CallIl"/>. The two opcodes share
    /// width (9 bytes) and operand layout — the only difference is the
    /// opcode byte and the meaning of the 4-byte target operand
    /// (address → functor id) — so the rewrite is in-place. Idempotent
    /// (skips sites whose opcode is no longer <c>Call</c>, e.g. when
    /// a re-link revisits a previously-rewritten persistent buffer).</summary>
    private int _diagCallIlCount;
    // Per-program-state cache for InstallCallIlRewrites. The persistent
    // buffer's call-site rewrites are IN-PLACE and idempotent — once walked,
    // re-walking every predicate's sites per query is a pure no-op that
    // dominated warm query setup (~2.7 ms per QueryAll on a clpz-sized
    // program). Valid while the persistent buffer, the promotion set (installs
    // via PromotedCount, evicts via EvictionStamp) and the program stamp are
    // unchanged; a warm hit reuses the pending-site map, the fid-keyed
    // predicate view and the IL dispatch-table template, and walks only the
    // QUERY overlay's few predicates.
    private sealed class CallSiteRewriteCache
    {
        public required byte[]? PersistentRef;
        public required int EvictionStamp;
        public required int PromotedCount;
        public required int ProgramStamp;
        public required Dictionary<int, List<(int AbsAddr, bool IsExecute)>>? PromotableCallSites;
        public required Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> PredicateByFid;
        public required Func<Shumway.Core.Activation, int, bool>?[]? IlTableTemplate;
    }
    private CallSiteRewriteCache? _callSiteCache;

    internal void InstallCallIlRewrites(
        Shumway.Interpreter.BytecodeInterpreter interp,
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicatesByAddress,
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> queryPredicatesByAddress,
        byte[] queryBytes)
    {
        _diagCallIlCount = 0;
        _rewriteInterp = interp;
        // ADR-034 cross-activation variant: mutation-time IlByFunctorId slot
        // clearing must reach EVERY live interpreter, not just the current one
        // (a suspended outer activation's table otherwise keeps dispatching an
        // evicted dynamic snapshot — the Logtalk-under-promotion silent
        // failure: '$lgt_current_object_'/11 served pre-assert answers).
        for (int i = E._liveInterps.Count - 1; i >= 0; i--)
            if (!E._liveInterps[i].TryGetTarget(out _)) E._liveInterps.RemoveAt(i);
        E._liveInterps.Add(new WeakReference<Shumway.Interpreter.BytecodeInterpreter>(interp));
        E.IlPromotion.OnPromotionInstalled = OnCalleePromoted;

        var cache = _callSiteCache;
        bool warm = cache is not null
            && ReferenceEquals(cache.PersistentRef, E._persistentProgram)
            && cache.EvictionStamp == E.IlPromotion.EvictionStamp
            && cache.PromotedCount == E.IlPromotion.PromotedCount
            && cache.ProgramStamp == E._programStamp;

        Func<Shumway.Core.Activation, int, bool>?[]? ilTable;
        Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicateByFid;
        if (warm)
        {
            // The template is cloned per interpreter: live tables get slots
            // nulled by dynamic-mutation invalidation and grown by mid-query
            // installs — neither may leak into the shared template.
            var tpl = cache!.IlTableTemplate;
            ilTable = tpl is null ? null : (Func<Shumway.Core.Activation, int, bool>?[])tpl.Clone();
            predicateByFid = cache.PredicateByFid;
            _promotableCallSites = cache.PromotableCallSites;
            interp.IlByFunctorId = ilTable;
            DiagIlTable(ilTable);
            // Only the query overlay's predicates need their sites classified —
            // the persistent buffer's were rewritten in place on the cold walk.
            RewriteCallSites(queryPredicatesByAddress, ilTable, predicateByFid, queryBytes);
            DiagIlRewriteTotal();
            return;
        }

        _promotableCallSites?.Clear();
        // Snapshot every currently-promoted IL delegate, indexed by
        // functor id. The PredicateDelegate -> Func<Activation,int,bool>
        // bridge allocates one wrapper per IL predicate, here at link
        // time — not per dispatch.
        int maxFid = -1;
        foreach (int fid in E.IlPromotion.PromotedFunctorIds())
            if (fid > maxFid) maxFid = fid;
        ilTable = null;
        if (maxFid >= 0)
        {
            ilTable = new Func<Shumway.Core.Activation, int, bool>?[maxFid + 1];
            foreach (int fid in E.IlPromotion.PromotedFunctorIds())
            {
                var del = E.IlPromotion.TryGet(fid);
                if (del is null) continue;
                // Method-group conversion: del.Invoke creates a
                // Func<Activation,int,bool> that calls through to del.
                ilTable[fid] = del.Invoke;
            }
        }
        // The interp gets its own copy; the pristine array is the template.
        interp.IlByFunctorId = ilTable is null
            ? null : (Func<Shumway.Core.Activation, int, bool>?[])ilTable.Clone();
        DiagIlTable(interp.IlByFunctorId);

        // Stage B.2 — build a fid-keyed view of
        // predicatesByAddress so we can look up the callee's
        // CompiledPredicate by functor id when classifying Call sites
        // as bytecode-only. The same predicate may live under
        // multiple addresses (the enter_dynamic trampoline
        // is at the entry address but the chain bodies sit at later
        // addresses); the functor id is unique.
        predicateByFid = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
            predicatesByAddress.Count);
        foreach (var (_, p) in predicatesByAddress)
            predicateByFid[p.FunctorId] = p;

        RewriteCallSites(predicatesByAddress, interp.IlByFunctorId, predicateByFid, queryBytes);
        _callSiteCache = new CallSiteRewriteCache
        {
            PersistentRef = E._persistentProgram,
            EvictionStamp = E.IlPromotion.EvictionStamp,
            PromotedCount = E.IlPromotion.PromotedCount,
            ProgramStamp = E._programStamp,
            PromotableCallSites = _promotableCallSites,
            PredicateByFid = predicateByFid,
            IlTableTemplate = ilTable,
        };
        DiagIlRewriteTotal();
    }

    private void RewriteCallSites(
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicatesByAddress,
        Func<Shumway.Core.Activation, int, bool>?[]? ilTable,
        Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate> predicateByFid,
        byte[] queryBytes)
    {
        // Rewrite every Call whose callee state is decided at link time.
        // Three opcodes today (B.3 will add ExecuteIl / ExecuteBytecode):
        //   - CallIl: callee already has a bundle-IL delegate. Operand
        //     is rewritten from target address to callee functor id.
        //   - CallBytecode: callee will never have IL (Threshold==0,
        //     layout-excluded, oversized, or already rejected). Operand
        //     is the absolute target address, unchanged.
        //   - Call: callee may still earn IL via JIT promotion later.
        // Walks both the persistent buffer (addresses < E._querySplit)
        // and the per-query overlay. Execute is left alone for now.
        foreach (var (predAddr, pred) in predicatesByAddress)
        {
            foreach (var site in pred.CallSites)
            {
                int calleeFid = site.CalleeFunctorId;

                int absAddr = predAddr + site.OpcodeOffset;
                byte[] buf;
                int bufOffset;
                if (absAddr < E._querySplit)
                {
                    buf = E._persistentProgram!;
                    bufOffset = absAddr;
                }
                else
                {
                    buf = queryBytes;
                    bufOffset = absAddr - E._querySplit;
                }
                // Idempotence + safety: only rewrite the original
                // Call/Execute opcode. A previous query may have already
                // rewritten this site in the persistent buffer.
                int width = site.IsExecute ? 5 : 9;
                if (bufOffset < 0 || bufOffset + width > buf.Length) continue;
                byte expected = site.IsExecute
                    ? (byte)Shumway.Core.Opcode.Execute
                    : (byte)Shumway.Core.Opcode.Call;
                if (buf[bufOffset] != expected) continue;

                // Prefer the IL variant when IL is available.
                // ADR-023/034 — but never for a DYNAMIC callee: its delegate
                // (the bundle-baked or runtime-promoted snapshot) is EVICTED
                // on the first assert/retract, while a CallIl/ExecuteIl
                // rewrite persists in the buffer across queries — the
                // hardened site would run the stale snapshot (or crash on
                // the cleared table slot). Dynamic callees stay on the
                // generic Call/Execute path, whose OnDispatch resolves per
                // call and sees the eviction. (The pre-fix symptom:
                // `assertz(f(7)), f(7)` FALSE through a baked-snapshot
                // bundle — the ISO logical update view broken.)
                bool hasIl = !E._dynStore.IsDynamic(calleeFid)
                    && ilTable is not null
                    && (uint)calleeFid < (uint)ilTable.Length
                    && ilTable[calleeFid] is not null;
                if (hasIl)
                {
                    DiagCountCallIlRewrite();
                    buf[bufOffset] = site.IsExecute
                        ? (byte)Shumway.Core.Opcode.ExecuteIl
                        : (byte)Shumway.Core.Opcode.CallIl;
                    // Replace the address operand with the functor id;
                    // for Call the trailing numLivePerms stays put.
                    Shumway.Core.BytecodeIO.WriteInt32(buf, bufOffset + 1, calleeFid);
                    continue;
                }

                // Otherwise classify as bytecode-only when we can prove
                // IL will never come. Falls through (leaves Call /
                // Execute as-is) when:
                //   - the callee is unresolved (no CompiledPredicate at
                //     link time — e.g., an assertz-auto-promoted
                //     functor materialised after the linker ran)
                //   - the callee MAY still be promotable
                //   - the callee is a dynamic predicate. Dynamic
                //     dispatch goes through the JitIndexProfile (chunk
                //     75) counter inside OnDispatch — rewriting to
                //     CallBytecode would bypass RecordCall, breaking
                //     the dynamic-predicate re-index threshold. Static
                //     bytecode-only predicates (oversized, threshold-
                //     disabled, etc.) have no such tracking concern
                //     and are safe to rewrite.
                if (predicateByFid.TryGetValue(calleeFid, out var calleePred)
                    && E.IlPromotion.IsPermanentlyBytecodeOnly(calleeFid, calleePred)
                    && !IsDynamicPredicate(calleePred))
                {
                    buf[bufOffset] = site.IsExecute
                        ? (byte)Shumway.Core.Opcode.ExecuteBytecode
                        : (byte)Shumway.Core.Opcode.CallBytecode;
                    // Operand stays as the absolute target address.
                    continue;
                }

                // the site stays a generic Call/Execute
                // because the callee may still earn IL mid-query. Record it (by
                // callee fid) so the moment the callee's delegate installs, the
                // site is patched to CallIl/ExecuteIl for the rest of the query.
                // Persistent-buffer sites only: the query overlay is rebuilt at
                // the next setup anyway, and its buffer may be replaced mid-query.
                // Skip dynamic callees — their dispatch must keep feeding the
                // JitIndexProfile counter inside OnDispatch.
                if (absAddr < E._querySplit
                    && (calleePred is null || !IsDynamicPredicate(calleePred)))
                {
                    (_promotableCallSites ??= new()).TryGetValue(calleeFid, out var list);
                    if (list is null) _promotableCallSites[calleeFid] = list = new();
                    list.Add((absAddr, site.IsExecute));
                }
            }
        }
    }

    // generic Call/Execute sites whose callee may promote later,
    // indexed by callee fid, in the PERSISTENT buffer. Rebuilt every query setup
    // (see InstallCallIlRewrites); consumed by OnCalleePromoted.
    private Dictionary<int, List<(int AbsAddr, bool IsExecute)>>? _promotableCallSites;
    private Shumway.Interpreter.BytecodeInterpreter? _rewriteInterp;

    /// <summary>Stage B.4: called (on the engine thread, from
    /// <see cref="IlPromotionStore.OnPromotionInstalled"/>) when a delegate is
    /// installed mid-query. Publishes the delegate in the interpreter's direct
    /// <c>IlByFunctorId</c> table and patches the callee's recorded generic call
    /// sites to <c>CallIl</c>/<c>ExecuteIl</c> — the rest of the running query
    /// dispatches directly instead of paying the OnDispatch interface + dict +
    /// wrapper per call (previously that tax lasted until the next query setup,
    /// i.e. the whole run for a single-goal <c>--exe</c>).</summary>
    private void OnCalleePromoted(int calleeFid, Shumway.Compiler.Il.PredicateDelegate del)
    {
        var interp = _rewriteInterp;
        if (interp is null) return;
        // 1. Direct dispatch table (grow if needed; engine thread — no races).
        var table = interp.IlByFunctorId;
        if (table is null || calleeFid >= table.Length)
        {
            var grown = new Func<Shumway.Core.Activation, int, bool>?[calleeFid + 1];
            table?.CopyTo(grown, 0);
            interp.IlByFunctorId = table = grown;
        }
        table[calleeFid] = del.Invoke;
        // 2. Patch the recorded persistent-buffer sites.
        if (_promotableCallSites is null
            || !_promotableCallSites.TryGetValue(calleeFid, out var sites)) return;
        var buf = E._persistentProgram;
        if (buf is not null)
        {
            foreach (var (absAddr, isExecute) in sites)
            {
                int width = isExecute ? 5 : 9;
                if (absAddr + width > buf.Length) continue;
                byte expected = isExecute
                    ? (byte)Shumway.Core.Opcode.Execute
                    : (byte)Shumway.Core.Opcode.Call;
                if (buf[absAddr] != expected) continue;   // already rewritten / stale
                DiagCountCallIlRewrite();
                buf[absAddr] = isExecute
                    ? (byte)Shumway.Core.Opcode.ExecuteIl
                    : (byte)Shumway.Core.Opcode.CallIl;
                Shumway.Core.BytecodeIO.WriteInt32(buf, absAddr + 1, calleeFid);
            }
        }
        _promotableCallSites.Remove(calleeFid);
    }

    /// <summary>diag-build-only (<c>-p:ShumwayDiag=true</c> +
    /// <c>SHUMWAY_IL_DIAG=1</c>): the per-query IL-dispatch
    /// diagnostics. All three hooks are stripped from normal builds.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagIlTable(Func<Shumway.Core.Activation, int, bool>?[]? ilTable)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_DIAG") == "1")
            System.Console.Error.WriteLine(
                $"[il-diag] promoted/registered fids={E.IlPromotion.PromotedFunctorIds().Count()} "
                + $"ilTable.Length={(ilTable?.Length ?? 0)} Threshold={E.IlPromotion.Threshold}");
    }

    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagCountCallIlRewrite() => _diagCallIlCount++;

    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private void DiagIlRewriteTotal()
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_IL_DIAG") == "1")
            System.Console.Error.WriteLine(
                $"[il-diag] CallIl/ExecuteIl rewrites installed this query={_diagCallIlCount}");
    }

    /// <summary>True when the predicate is a dynamic
    /// one (its bytecode begins with <see cref="Shumway.Core.Opcode.EnterDynamic"/>
    ///). Dynamic predicates must keep using the
    /// <see cref="Shumway.Core.Opcode.Call"/> / <see cref="Shumway.Core.Opcode.Execute"/>
    /// path so the OnDispatch hook can bump the JitIndexProfile
    /// counter that drives re-indexing.</summary>
    private static bool IsDynamicPredicate(Shumway.Compiler.Wam.CompiledPredicate pred)
        => pred.Bytecode.Length > 0
            && pred.Bytecode[0] == (byte)Shumway.Core.Opcode.EnterDynamic;

    // ---- process-wide persisted-IL cache ---------------------
    // Loading a bundle entry's persisted IL means: clone + patch the assembly
    // image, Assembly.Load, reflect the P_* methods, CreateDelegate each. All
    // of that output is engine-agnostic — compiled IL takes Activation as a
    // parameter (the ADR-011 invariant), functor ids come from the process-
    // global atom/functor tables, and resume markers are process-global dense
    // ids — and the patch application itself is deterministic within a process
    // (each sentinel resolves by NAME through the global tables). So the load
    // is done ONCE per IL content for the process lifetime and shared across
    // engines, mirroring the _loadedNativeLibraries table. Without this, an
    // EnginePool loading the same bundle N times paid N Assembly.Loads and N
    // JITs of identical code. Entries never evict — like a loaded native
    // library, a loaded assembly can't be unloaded anyway (no collectible
    // AssemblyLoadContext here by design: the delegates are cached globally).
    private sealed class PersistedIlModule
    {
        public required List<(int Slot, int FunctorId,
            Shumway.Compiler.Il.PredicateDelegate Delegate)> Bound;
        public Dictionary<int, byte[]>? IndexGraphs;   // runtime fid → dispatch graph
        public Dictionary<int, int>? RegionAliases;    // member fid → resume marker
    }

    private static readonly Dictionary<string, PersistedIlModule?> _loadedPersistedIl = new();
    private static readonly object _loadedPersistedIlLock = new();

    /// <summary>Test/diagnostic: the number of real <c>Assembly.Load</c> calls
    /// for persisted IL (distinct content loads once for the whole process).</summary>
    internal static int PersistedIlLoadCount;

    /// <summary>Test/diagnostic: whether this entry's persisted IL is already
    /// in the process-wide cache (a later LoadBundle of the same content
    /// reuses the loaded assembly + delegates instead of re-loading).</summary>
    internal static bool IsPersistedIlCached(BundleEntry entry)
    {
        lock (_loadedPersistedIlLock)
            return _loadedPersistedIl.ContainsKey(PersistedIlCacheKey(entry));
    }

    /// <summary>Content key over everything that determines the loaded module:
    /// the IL image plus its patch and entries tables. Same bytes ⇒ same
    /// patched assembly within this process (patches resolve by name against
    /// the global tables), so a hash of the inputs is a sound identity.</summary>
    private static string PersistedIlCacheKey(BundleEntry entry)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var len = new byte[4];
        void Add(byte[]? b)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(len, b?.Length ?? -1);
            sha.AppendData(len);
            if (b is not null) sha.AppendData(b);
        }
        Add(entry.CompiledIl);
        Add(entry.CompiledIlPatches);
        Add(entry.CompiledIlEntries);
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static PersistedIlModule? GetOrLoadPersistedIl(BundleEntry entry)
    {
        string key = PersistedIlCacheKey(entry);
        // The lock is held across Assembly.Load on purpose (same discipline as
        // UseNativeLibrary): the guarantee is load-ONCE, and a racing second
        // loader would otherwise produce a duplicate assembly + JIT.
        lock (_loadedPersistedIlLock)
        {
            if (_loadedPersistedIl.TryGetValue(key, out var cached)) return cached;
            var module = LoadPersistedIl(entry);
            _loadedPersistedIl[key] = module;
            return module;
        }
    }

    private static PersistedIlModule? LoadPersistedIl(BundleEntry entry)
    {
        // overwrite each baked build-time atom/functor id sentinel
        // with the runtime-process id BEFORE handing the bytes to
        // Assembly.Load. Once the assembly is loaded its IL is read-only
        // mapped, so the patch must happen on the byte buffer (a copy so we
        // don't mutate the caller's reusable BundleEntry).
        byte[] ilBytes = entry.CompiledIl!;
        if (entry.CompiledIlPatches is not null && entry.CompiledIlPatches.Length > 0)
        {
            ilBytes = (byte[])entry.CompiledIl!.Clone();
            ApplyIlPatches(ilBytes, entry.CompiledIlPatches);
        }
        var asm = System.Reflection.Assembly.Load(ilBytes);
        System.Threading.Interlocked.Increment(ref PersistedIlLoadCount);
        var type = asm.GetType(Shumway.Compiler.Il.PersistedIlBuilder.TypeName);
        if (type is null) return null;

        // Method-name layout from PersistedIlBuilder:
        //   P_{slot}_{functorId}_{sanitisedName}
        // when CompiledIlEntries is present (V3+ bundles), use the
        // per-method (name, arity) table to intern the name in THIS process
        // and bind the delegate under the runtime functor id. Falls back to
        // parsing the build-time functor id from the method name only for
        // pre-V3 bundles (which never run cross-process correctly anyway).
        Dictionary<string, (string Name, int Arity, int Slot)>? methodInfo = null;
        Dictionary<string, byte[]>? graphByMethod = null;
        Dictionary<string, IReadOnlyList<(string Name, int Arity, int Cursor)>>?
            regionMembersByMethod = null;
        if (entry.CompiledIlEntries is not null && entry.CompiledIlEntries.Length > 0)
        {
            methodInfo = new Dictionary<string, (string, int, int)>();
            foreach (var pe in Shumway.Compiler.Il.IlPersistedEntryCodec.Decode(
                entry.CompiledIlEntries))
            {
                methodInfo[pe.MethodName] = (pe.Name, pe.Arity, pe.Slot);
                if (pe.IndexGraph is { Length: > 0 } g)
                    (graphByMethod ??= new Dictionary<string, byte[]>())[pe.MethodName] = g;
                if (pe.RegionMembers is { Count: > 0 } rm)
                    (regionMembersByMethod ??= new())[pe.MethodName] = rm;
            }
        }
        var bound = new List<(int Slot, int FunctorId,
            Shumway.Compiler.Il.PredicateDelegate Delegate)>();
        Dictionary<int, byte[]>? indexGraphs = null;
        Dictionary<int, int>? regionAliases = null;
        foreach (var method in type.GetMethods(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (!method.Name.StartsWith("P_")) continue;
            int slot;
            int functorId;
            if (methodInfo is not null && methodInfo.TryGetValue(method.Name, out var info))
            {
                int aid = Shumway.Core.AtomTable.Intern(info.Name).Id;
                functorId = Shumway.Core.FunctorTable.Intern(aid, info.Arity);
                slot = info.Slot;
            }
            else
            {
                int u1 = method.Name.IndexOf('_');
                int u2 = method.Name.IndexOf('_', u1 + 1);
                int u3 = method.Name.IndexOf('_', u2 + 1);
                if (u1 < 0 || u2 < 0 || u3 < 0) continue;
                if (!int.TryParse(method.Name.AsSpan(u1 + 1, u2 - u1 - 1), out slot)) continue;
                if (!int.TryParse(method.Name.AsSpan(u2 + 1, u3 - u2 - 1), out functorId)) continue;
            }
            // The cast form rather than CreateDelegate<T>: net48 has only the
            // non-generic method, and an instance member with the right name
            // blocks extension-method fallback for the generic call shape.
            var del = (Shumway.Compiler.Il.PredicateDelegate)method.CreateDelegate(
                typeof(Shumway.Compiler.Il.PredicateDelegate));
            bound.Add((slot, functorId, del));
            if (graphByMethod is not null
                && graphByMethod.TryGetValue(method.Name, out var graphBytes))
                (indexGraphs ??= new())[functorId] = graphBytes;
            // functorId here is the region ROOT's runtime fid; each
            // member's runtime fid maps to a marker at the member's entry cursor.
            if (regionMembersByMethod is not null
                && regionMembersByMethod.TryGetValue(method.Name, out var rMembers))
            {
                foreach (var (mName, mArity, mCursor) in rMembers)
                {
                    int mAid = Shumway.Core.AtomTable.Intern(mName, permanent: true).Id;
                    int mFid = Shumway.Core.FunctorTable.Intern(mAid, mArity);
                    (regionAliases ??= new())[mFid] =
                        Activation.EncodeResumeMarker(functorId, mCursor);
                }
            }
        }

        // Populate the static delegates array (multi-clause
        // self-reference). Once per loaded assembly — the field lives on the
        // loaded type, shared by every engine using this module.
        var dF = type.GetField(
            Shumway.Compiler.Il.PersistedIlBuilder.DelegatesFieldName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (dF is not null && bound.Count > 0)
        {
            int size = bound.Max(b => b.Slot) + 1;
            var arr = new Shumway.Compiler.Il.PredicateDelegate[size];
            foreach (var (slot, _, del) in bound) arr[slot] = del;
            dF.SetValue(null, arr);
        }
        return new PersistedIlModule
        {
            Bound = bound,
            IndexGraphs = indexGraphs,
            RegionAliases = regionAliases,
        };
    }

    // ---- process-wide static-region link cache ---------------
    // The static region links once per ENGINE (caches it in
    // E._staticLink), but an EnginePool loading the same bundle N times still
    // ran N identical full-program links on each engine's first query. The
    // link is a pure function of the ordered predicate list (bytecode +
    // switch tables + call sites — all immutable) and the load offset, so
    // the result is shared process-wide, keyed by a content hash. The hash
    // covers the post-literal-remap bytecode bytes, so an engine whose
    // literal pools were populated in a different order simply misses and
    // links fresh — never a wrong hit. LinkResult is read-only downstream:
    // its bytecode is COPIED into each engine's persistent buffer (per-engine
    // Call→CallIl patches land in the copy), and static switch tables are
    // never mutated (in-place mutation applies to dynamics only).
    private static readonly Dictionary<string, Shumway.Compiler.Wam.Linker.LinkResult>
        _sharedStaticLinks = new();
    private static readonly object _sharedStaticLinksLock = new();

    // Crude growth bound: a long-lived process churning DISTINCT static
    // programs (a test suite, a REPL consulting repeatedly) would otherwise
    // accumulate full program images forever. The pool scenario this cache
    // exists for uses a handful of distinct programs, so wholesale reset on
    // overflow is simpler than LRU and costs one relink per evicted program.
    private const int SharedStaticLinkCapacity = 64;

    /// <summary>Test/diagnostic: the number of real static-region link runs
    /// (identical static programs link once for the whole process).</summary>
    internal static int StaticLinkBuildCount;

    /// <summary>Test/diagnostic (per-engine, so parallel tests can't perturb
    /// it): whether this engine's most recent static-region link came from
    /// the process-wide shared cache instead of a fresh link run.</summary>
    internal bool LastStaticLinkWasSharedHit;

    internal Shumway.Compiler.Wam.Linker.LinkResult GetOrLinkStatic(
        List<Shumway.Compiler.Wam.CompiledPredicate> staticPreds, int loadOffset)
    {
        string key = StaticLinkKey(staticPreds, loadOffset);
        lock (_sharedStaticLinksLock)
        {
            if (_sharedStaticLinks.TryGetValue(key, out var hit))
            {
                LastStaticLinkWasSharedHit = true;
                return hit;
            }
            LastStaticLinkWasSharedHit = false;
            if (_sharedStaticLinks.Count >= SharedStaticLinkCapacity)
                _sharedStaticLinks.Clear();
            var link = new Shumway.Compiler.Wam.Linker().Link(staticPreds, loadOffset: loadOffset);
            System.Threading.Interlocked.Increment(ref StaticLinkBuildCount);
            _sharedStaticLinks[key] = link;
            return link;
        }
    }

    /// <summary>Content fingerprint of the static link inputs: load offset
    /// plus, per predicate in order, functor id, bytecode bytes, and the
    /// switch-table content (keys/values/default live OUTSIDE the bytecode)
    /// and call-site table (drives the linker's resolution).</summary>
    private static string StaticLinkKey(
        List<Shumway.Compiler.Wam.CompiledPredicate> staticPreds, int loadOffset)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        var word = new byte[4];
        void AddInt(int v)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(word, v);
            sha.AppendData(word);
        }
        AddInt(loadOffset);
        AddInt(staticPreds.Count);
        foreach (var p in staticPreds)
        {
            AddInt(p.FunctorId);
            AddInt(p.Bytecode.Length);
            sha.AppendData(p.Bytecode);
            AddInt(p.SwitchTables.Count);
            foreach (var t in p.SwitchTables)
            {
                AddInt(t.DefaultAddress);
                AddInt(t.Count);
                for (int i = 0; i < t.Count; i++) { AddInt(t.Keys[i]); AddInt(t.Values[i]); }
            }
            AddInt(p.CallSites.Count);
            foreach (var c in p.CallSites)
            {
                AddInt(c.OpcodeOffset);
                AddInt(c.CalleeFunctorId);
                AddInt(c.IsExecute ? 1 : 0);
            }
            // ADR-035 — the debug metadata is part of the identity, even though it is
            // not part of the bytecode. Two programs can compile to byte-identical code
            // and be written on entirely different LINES: the stop sites and the frame
            // maps are what tell them apart. Leaving them out of the key let one
            // program's link be handed to another, which then reported its neighbour's
            // source positions — a debugger showing the wrong file, with no error
            // anywhere. Debug programs are also exactly the case this cache was never
            // for (it exists so a pool can load one bundle N times), so the extra
            // hashing costs nothing that matters.
            AddInt(p.DebugStops.Count);
            foreach (var s in p.DebugStops)
            {
                AddInt(s.Offset);
                AddInt(s.SiteId);
            }
            AddInt(p.DebugFrames.Count);
            foreach (var f in p.DebugFrames)
            {
                AddInt(f.Start);
                AddInt(f.End);
                AddInt(f.HasFrame ? 1 : 0);
                AddInt(f.Variables.Count);
                foreach (var v in f.Variables)
                {
                    AddInt(v.Slot);
                    sha.AppendData(System.Text.Encoding.UTF8.GetBytes(v.Name));
                }
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    private static void ApplyIlPatches(byte[] ilBytes, byte[] patchTable)
    {
        var sites = Shumway.Compiler.Il.IlPatchSiteCodec.Decode(patchTable);
        foreach (var s in sites)
        {
            int runtimeValue;
            switch (s.Kind)
            {
                case Shumway.Compiler.Il.IlPatchKind.Atom:
                    runtimeValue = Shumway.Core.AtomTable.Intern(s.Name).Id;
                    break;
                case Shumway.Compiler.Il.IlPatchKind.Functor:
                {
                    int aid = Shumway.Core.AtomTable.Intern(s.Name).Id;
                    runtimeValue = Shumway.Core.FunctorTable.Intern(aid, s.Arity);
                    break;
                }
                case Shumway.Compiler.Il.IlPatchKind.ResumeMarker:
                {
                    int aid = Shumway.Core.AtomTable.Intern(s.Name).Id;
                    int fid = Shumway.Core.FunctorTable.Intern(aid, s.Arity);
                    runtimeValue = Shumway.Core.Activation.EncodeResumeMarker(fid, s.Cursor);
                    break;
                }
                default:
                    throw new InvalidDataException(
                        $"Unknown IL patch kind {(int)s.Kind}.");
            }

            int off = s.AbsoluteByteOffset;
            if (off < 0 || off + 4 > ilBytes.Length)
                throw new InvalidDataException(
                    $"IL patch site at offset 0x{off:X8} is out of range "
                    + $"(IL buffer is {ilBytes.Length} bytes).");
            // Defensive sanity: the four bytes currently at the offset
            // must equal the recorded sentinel. If they don't, the
            // patch table is desynchronised from the .dll — fail loudly
            // rather than silently writing into the wrong instruction.
            int current = ilBytes[off]
                | (ilBytes[off + 1] << 8)
                | (ilBytes[off + 2] << 16)
                | (ilBytes[off + 3] << 24);
            if (current != s.Sentinel)
                throw new InvalidDataException(
                    $"IL patch site for {s.Kind} {s.Name}/{s.Arity} at "
                    + $"offset 0x{off:X8} holds 0x{current:X8}, expected "
                    + $"sentinel 0x{s.Sentinel:X8} — patch table out of "
                    + $"sync with the embedded .dll.");
            ilBytes[off] = (byte)(runtimeValue & 0xFF);
            ilBytes[off + 1] = (byte)((runtimeValue >> 8) & 0xFF);
            ilBytes[off + 2] = (byte)((runtimeValue >> 16) & 0xFF);
            ilBytes[off + 3] = (byte)((runtimeValue >> 24) & 0xFF);
        }
    }

    /// <summary>registers a source-less bundle entry's
    /// predicates with the engine without going through
    /// <see cref="ConsultString"/>. Populates the per-module
    /// <see cref="ModuleManifest"/> from the entry's
    /// <see cref="BundleEntry.Defined"/> list (publics → public set,
    /// dynamics → dynamic set + engine-wide <c>E._dynStore.Functors</c>),
    /// then decodes the entry's <see cref="BundleEntry.CompiledBytecode"/>
    /// and registers each predicate in
    /// <see cref="E._precompiledStaticPredicates"/>. The next
    /// <see cref="SetupQueryFromTerm"/> appends those predicates to
    /// the static-link region so call sites resolve identically to
    /// the source-carrying path. The bytecode is byte-identical to
    /// what <see cref="SetupQueryFromTerm"/> would have produced
    ///.</summary>
    /// <summary>Decodes a bundle entry's <see cref="BundleEntry.CompiledBytecode"/>,
    /// remaps its module-local literal ids into the engine's shared pools (see
    /// <see cref="RemapPrecompiledLiterals"/>), records the module, and warms its IL
    /// when Tier 1 is enabled. The single decode path shared by the source-less
    /// <see cref="LoadEntryFromBytecode"/> and the source-carrying
    /// <see cref="LoadBundleCore"/> loop.
    ///
    /// <para><paramref name="registerStaticPredicates"/> — true only for the
    /// source-less path: there the bytecode IS the definition, so each predicate
    /// goes into <see cref="E._precompiledStaticPredicates"/>. For a source-carrying
    /// entry the source consult is the truth and the bytecode is only an IL-warm /
    /// skip-compile cache, so it is NOT registered there.</para></summary>
    /// <summary>Binds one entry's persisted Tier-1 IL into this engine — clone +
    /// patch + Assembly.Load + delegate binding, done ONCE per IL content for the
    /// whole process (GetOrLoadPersistedIl, cached like the native libraries); a
    /// second call replays only the per-engine registrations. First-wins, so it
    /// is idempotent and safe to call both from LoadEntryFromBytecode (before its
    /// warm) and the whole-bundle pass. No-op for an entry without persisted IL
    /// or under Native AOT (the bytecode is used instead).</summary>
    private void BindPersistedIlForEntry(BundleEntry entry)
    {
        if (entry.CompiledIl is null || entry.CompiledIl.Length == 0
            || !Shumway.Core.RuntimeCaps.SupportsRuntimeCodegen)
            return;
        var module = GetOrLoadPersistedIl(entry);
        if (module is null)
        {
            // The image loaded but its type could not surface — on .NET
            // Framework this is a bundle whose IL was emitted by the .NET 10
            // toolchain (System.Private.CoreLib refs Framework cannot
            // resolve). Correctness survives on the bytecode, but silently
            // losing the persisted tier hides a real deployment mistake.
            if (!E._warnedIlUnbindable.Add(entry.ModuleName)) return;
            E.Warn($"bundle entry '{entry.ModuleName}': persisted IL could not "
                + "be bound on this runtime; using bytecode."
#if NETFRAMEWORK
                + " A bundle for a .NET Framework host must be linked with the"
                + " net48 build of shumway-link."
#endif
                );
            return;
        }
        foreach (var (_, functorId, del) in module.Bound)
            E.IlPromotion.RegisterBoundDelegate(functorId, del);
        // A stripped indexed predicate carries its dispatch graph in the bundle.
        // Stash it by runtime functor id; each query's fresh engine gets it
        // registered at setup. Without a WAM body the delegate would otherwise
        // have nothing to rebuild the switch model from.
        if (module.IndexGraphs is not null)
            foreach (var kv in module.IndexGraphs)
                E._persistedIndexGraphs[kv.Key] = kv.Value;
        // A region method publishes its members' entry cursors; the alias marker
        // dispatches the region delegate at the member's entry. Consulted by the
        // warm below to skip a member the region already covers.
        if (module.RegionAliases is not null)
            foreach (var kv in module.RegionAliases)
                E._regionMemberAliases[kv.Key] = kv.Value;
    }

    private Shumway.Compiler.Wam.CompiledModule DecodeAndRegisterPrecompiledModule(
        BundleEntry entry, bool registerStaticPredicates)
    {
        var module = CompiledModuleCodec.Decode(entry.CompiledBytecode!);
        // Remap COMPILE-TIME, module-local float/string/bigint literal ids into the
        // engine's ONE shared E._literalPools (mutating the freshly-decoded bytecode in
        // place) — else a static literal reads whatever value sits at that id in the
        // merged pool (the two-float bug). Afterward every id indexes the live pool,
        // so IL float value-baking reads E._literalPools.Floats directly.
        E.RemapPrecompiledLiterals(module);
        E._precompiledModules[entry.ModuleName] = module;
        // Register the predicates but do NOT eagerly Sigil-compile them here.
        // A t0 bundle (no persisted IL) has nothing bound, so a load-time
        // warm-all would Sigil-compile every static predicate — ~900 on a clpz
        // bundle, ~1.5 s of load — most of which never run hot. Lazy is the
        // default: a predicate promotes at runtime once its invocation counter
        // crosses the threshold (background compile). A program that wants the
        // whole set hot up front calls compile_all/0 explicitly. (Persisted IL
        // is already bound by BindPersistedIlForEntry, independent of this.)
        if (registerStaticPredicates)
            foreach (var pred in module.Predicates)
            {
                E._precompiledStaticPredicates[pred.FunctorId] = pred;
                // Keep the runtime meta-helper counter above every bundled
                // helper id so a query-setup re-transform of this module's
                // dynamic clauses can't mint a colliding `mod$$disj_N` fid.
                E.ObserveBundleHelperId(pred.FunctorId);
            }
        return module;
    }

    private void LoadEntryFromBytecode(BundleEntry entry)
    {
        if (entry.CompiledBytecode is null)
            throw new InvalidOperationException(
                $"LoadEntryFromBytecode: entry '{entry.ModuleName}' has no compiled bytecode.");

        // Resolve the manifest under the entry's module name. The
        // contract here mirrors ConsultString's "explicit module"
        // path (PrologEngine.cs:2792) — `:- module(name).` would have
        // landed us in the same place. A subsequent source-carrying
        // load of the same module name is allowed to extend it
        // (consistent with the "rolling user module" pattern), but
        // each predicate id is at most once in the precompiled set.
        if (!E._modules.TryGetValue(entry.ModuleName, out var manifest))
        {
            manifest = new ModuleManifest(entry.ModuleName);
            E._modules[entry.ModuleName] = manifest;
        }

        // ADR-040 — restore the module's source dialect so a source-stripped
        // linked library keeps its dialect-sensitive builtin behaviour at runtime.
        if (entry.Dialect is not null)
        {
            manifest.Dialect = entry.Dialect;
            E.NoteDialectedModule();
        }

        // ADR-038 — reconstruct the export-qualification + import table so runtime
        // variable-meta-call resolution matches what the source consult produced.
        if (entry.IsExportQualified) manifest.IsExportQualified = true;
        foreach (var ex in entry.Exports)
            manifest.ExportFunctors.Add(Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(ex.Name, permanent: true).Id, ex.Arity));
        foreach (var imp in entry.Imports)
            manifest.Imports[Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(imp.Pred.Name, permanent: true).Id,
                imp.Pred.Arity)] = imp.Source;

        foreach (var d in entry.Defined)
        {
            int fid = Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(d.Indicator.Name, permanent: true).Id,
                d.Indicator.Arity);
            if (d.Visibility == PredicateVisibility.Public)
                manifest.PublicFunctors.Add(fid);
            else if (d.Visibility == PredicateVisibility.Dynamic)
            {
                manifest.DynamicFunctors.Add(fid);
                E._dynStore.MarkDynamic(fid);
                if (!E._dynStore.HasClauses(fid))
                    E._dynStore[fid] = new List<Clause>();
            }
            else // Local — record the bare fid so query setup can fold
                 // it into the module's locals.
            {
                if (!E._precompiledModuleLocals.TryGetValue(entry.ModuleName, out var localSet))
                {
                    localSet = new HashSet<int>();
                    E._precompiledModuleLocals[entry.ModuleName] = localSet;
                }
                localSet.Add(fid);
            }
        }

        // seed _dynamicClauses with the source-declared
        // clauses of every `:- dynamic foo/N.` predicate. Mirrors what
        // ConsultString does (PrologEngine.cs:3318-3341) — without
        // this, dispatch / clause/2 / retract would see an empty
        // dynamic store and the predicate would behave as if it had
        // no clauses. TermCodec rehydrates the AST so
        // SetupQueryFromTerm's downstream PredicateCompiler builds
        // the dynamic trampoline with check_visible entries pointing
        // at born=current-gen / died=MAX initial clauses.
        foreach (var seed in entry.DynamicSeeds)
        {
            int fid = Shumway.Core.FunctorTable.Intern(
                Shumway.Core.AtomTable.Intern(seed.Indicator.Name, permanent: true).Id,
                seed.Indicator.Arity);
            var slot = E._dynStore.Slot(fid);
            foreach (var encoded in seed.EncodedClauses)
                slot.Add(TermCodec.DecodeClause(encoded));
            // ADR-023 priming — a bundle's `:- dynamic`/`:- visible` predicate
            // shipped WITH clauses runs as its Tier-1 IL snapshot from the first
            // call (evictable on the first mutation).
            if (seed.EncodedClauses.Count > 0)
                E.IlPromotion.MarkPrime(fid);
            // remember which module these clauses came from.
            // The entry's static bytecode was mangled by ShmoCompiler
            // under entry.ModuleName, so the query-setup rewrite of these
            // rehydrated clauses must run under the SAME module context
            // (module name + that module's locals) or a body call to a
            // module-local predicate stays bare while its target is
            // `module$name`-mangled. NOT for multifile seeds: their clauses
            // were pre-mangled at compile time under their origin module,
            // and one fid holds several modules' contributions — a single
            // seed module would rewrite the other contributors' clauses
            // under the wrong locals.
            if (entry.ModuleName != PrologEngine.DefaultModuleName && !seed.Multifile)
                E._dynamicSeedModule[fid] = entry.ModuleName;
        }

        // ADR-022 — repopulate this engine's native-block table so the baked
        // `'$native_run'('$nb$…', Vars)` dispatch (in the entry's bytecode) finds
        // its block at run time. The C statement source is re-parsed here (the C
        // symbol table is only needed for the compile-time inference, already
        // baked into the serialized vars); a malformed block would have failed at
        // compile, so a parse error here is a corrupt bundle — surfaced, not
        // swallowed.
        foreach (var nb in entry.NativeBlocks)
        {
            var stmts = Shumway.Compiler.NativeC.CParser.ParseStatements(nb.RawText);
            E.AddNativeBlock(nb.Name, nb.Vars.ToArray(), stmts.ToArray(), nb.ScalarGlobals.ToArray());
        }

        // ADR-024 — restore the `:- native` indicators + `:- c` prototypes so a
        // source-stripped bundle resolves native calls (the directive/prototypes
        // are not re-applied without re-consulting the source).
        foreach (var pr in entry.NativeFunctions)
        {
            E.MarkNativeFunction(pr.Name, pr.Arity);
        }
        if (!string.IsNullOrEmpty(entry.NativeDecls))
            E.RegisterNativePrototypes(
                Shumway.Compiler.NativeC.CParser.ParseDeclarations(entry.NativeDecls!));

        // Bind this entry's persisted Tier-1 IL BEFORE the warm below. A
        // source-stripped IL bundle warms here, and RegisterBoundDelegate is
        // first-wins: if warm ran first it would Sigil-compile the region roots
        // standalone and block the persisted delegates (measured: 692 of 1644
        // persisted delegates lost on a clpz bundle at threshold 32). Binding
        // here also publishes _regionMemberAliases so the warm skips region
        // members. Idempotent — the whole-bundle pass re-runs it (cached).
        BindPersistedIlForEntry(entry);
        // Decode + literal-remap + record + warm IL (the bytecode IS the definition
        // here, so register the static predicates).
        DecodeAndRegisterPrecompiledModule(entry, registerStaticPredicates: true);

        // The static program just changed shape — drop the cached
        // static link region so the next query rebuild picks up the
        // new predicates.
        E._staticLink = null;
        E._staticPredicateCache.Clear();
        E._skipCompileMergedCache = null;   // static cache cleared
        E.InvalidatePersistent();
    }

    /// <summary>Save-state writes a snapshot of this engine's
    /// state to <paramref name="path"/> as a V6 .shum bundle.
    ///
    /// <para>Full mode (<paramref name="dynamicOnly"/> = false, the
    /// default) captures every source previously passed to
    /// <see cref="ConsultString"/> (in order, excluding the auto-
    /// loaded prelude) plus every currently asserted dynamic clause.
    /// <see cref="RestoreState"/> on a fresh engine reconstitutes
    /// equivalent state by replaying the consults and re-asserting
    /// the dynamic clauses.</para>
    ///
    /// <para>Dynamic-only mode (<paramref name="dynamicOnly"/> = true)
    /// skips the consult history and captures only the dynamic
    /// clauses — useful for persisting an application's facts
    /// without re-shipping the code that operates on them. Loaded
    /// with <see cref="RestoreState"/>, the clauses merge into the
    /// engine's current state via <c>assertz</c>, without resetting
    /// anything.</para></summary>
    public void SaveState(string path, bool dynamicOnly = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        BundleWriter.WriteToFile(BuildSnapshotBundle(dynamicOnly), path);
    }

    /// <summary>Save-state in-memory variant returning the
    /// serialized bundle bytes. Used by tests; the file-path overload
    /// is what user code typically calls.</summary>
    public byte[] SaveStateToBytes(bool dynamicOnly = false)
        => BundleWriter.ToBytes(BuildSnapshotBundle(dynamicOnly));

    private Bundle BuildSnapshotBundle(bool dynamicOnly)
    {
        var consultHistory = dynamicOnly
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : E._consultHistory.ToArray();
        var dynamicSeeds = new List<ShmoDynamicSeed>();
        foreach (var (fid, clauses) in E._dynStore.Slots)
        {
            if (clauses.Count == 0) continue;
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name
                ?? throw new InvalidOperationException(
                    $"SaveState: functor id {fid} has no atom-table entry.");
            var encoded = new byte[clauses.Count][];
            for (int i = 0; i < clauses.Count; i++)
                encoded[i] = TermCodec.EncodeClause(clauses[i]);
            dynamicSeeds.Add(new ShmoDynamicSeed(
                new PredicateRef(name, arity), encoded));
        }
        var snapshot = new BundleSnapshot(dynamicOnly, consultHistory, dynamicSeeds);
        return new Bundle(Array.Empty<BundleEntry>(), foreignAssemblies: null, snapshot);
    }

    /// <summary>Save-state restores a snapshot previously
    /// written by <see cref="SaveState"/>. Full-mode snapshots reset
    /// this engine's state first (clearing every consulted module,
    /// dynamic clause, and operator declaration not in the parser
    /// default) and then replay the saved consults + clauses.
    /// Dynamic-only snapshots merge their clauses into the current
    /// state via <c>assertz</c>, leaving consults and operators
    /// untouched.
    ///
    /// <para>Throws <see cref="InvalidDataException"/> if the file
    /// isn't a Shumway bundle or carries no snapshot trailer (i.e.
    /// was produced by <c>shumway-link</c> / <c>shumway-compile</c>
    /// rather than <see cref="SaveState"/>).</para></summary>
    public void RestoreState(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        RestoreStateFromBundle(BundleReader.ReadFromFile(path));
    }

    /// <summary>Save-state in-memory variant of
    /// <see cref="RestoreState"/>; reads from a bundle byte array.</summary>
    public void RestoreStateFromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        RestoreStateFromBundle(BundleReader.FromBytes(data));
    }

    private void RestoreStateFromBundle(Bundle bundle)
    {
        if (bundle.Snapshot is not { } snap)
            throw new InvalidDataException(
                "RestoreState: bundle has no save-state snapshot trailer "
                + "(was it produced by shumway-link rather than SaveState?).");

        if (!snap.DynamicOnly)
        {
            // Full reset: drop every consulted module (keep only the
            // default 'user' module), every dynamic clause, the
            // static-predicate cache, the cached static link region, the
            // persistent dynamic buffer, and the consult-history log.
            // Then re-consult the prelude (the ctor's first step) and
            // replay the saved history.
            E._modules.Clear();
            E._modules[PrologEngine.DefaultModuleName] = new ModuleManifest(PrologEngine.DefaultModuleName);
            E._dynStore.ClearAllSlots();
            E.ResetDynChains();
            E._staticPredicateCache.Clear();
            E._dynamicPredicateCache.Clear();
            E._skipCompileMergedCache = null;   // both caches cleared
            E._precompiledStaticPredicates.Clear();
            E._staticLink = null;
            E.InvalidatePersistent();
            E._consultHistory.Clear();
            E.ConsultStringInner(Prelude.Source, recordInHistory: false);
            E.MarkModuleNonDebuggable(Prelude.ModuleName);   // ADR-035
            foreach (var src in snap.ConsultHistory)
                E.ConsultString(src);
        }

        // Re-assert the snapshot's dynamic clauses. In full mode this
        // restores the post-snapshot state on top of the replayed
        // consults; in dynamic-only mode it merges into the engine as-is.
        // We bypass the AppendDynamicClauseIncremental in-place path
        // (which needs a live Activation) and just bookkeep via Assertz +
        // invalidate the persistent buffer once at the end. The next
        // query rebuilds dispatch from scratch and sees every restored
        // clause through the normal trampoline path.
        bool anyRestored = false;
        foreach (var seed in snap.DynamicClauses)
        {
            foreach (var encoded in seed.EncodedClauses)
            {
                var clause = TermCodec.DecodeClause(encoded);
                E.Assertz(clause);
                anyRestored = true;
            }
        }
        if (anyRestored) E.InvalidatePersistent();
    }

}

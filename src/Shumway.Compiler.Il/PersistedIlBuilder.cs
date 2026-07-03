using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Chunk 71 — emits a persisted .NET assembly (.dll bytes) holding the
/// IL for every IL-promotable predicate in a set of compiled modules.
/// Counterpart to the runtime
/// <see cref="IlPredicateCompiler"/> which targets
/// <c>DynamicMethod</c>; this one targets <c>MethodBuilder</c> via
/// <see cref="PersistedAssemblyBuilder"/> so the IL lives in a .dll
/// that engines can load with <c>Assembly.Load(bytes)</c>, skipping
/// the Sigil emission step at load time.
///
/// <para>Self-referential IL choice-points (used by multi-clause and
/// non-leaf single-clause predicates) point at a static
/// <c>PredicateDelegate[]</c> field in the emitted type rather than
/// the process-wide
/// <see cref="IlPredicateCompiler.IndexedDelegateHolder"/> table. At
/// load time the engine fills the array with delegates created from
/// each <c>MethodInfo</c>, giving each bundle a self-contained
/// indirection that doesn't collide with other bundles' keys.</para>
/// </summary>
public static class PersistedIlBuilder
{
    /// <summary>The fully qualified name of the type emitted into the
    /// assembly. Load-side code uses
    /// <see cref="Assembly.GetType(string)"/> to find it.</summary>
    public const string TypeName = "Shumway.Compiled.ShumwayCompiledPredicates";

    /// <summary>The name of the static <c>PredicateDelegate[]</c>
    /// field that holds the self-referential delegate slots. Load-side
    /// code accesses it via reflection to populate the entries from
    /// <c>MethodInfo.CreateDelegate</c>.</summary>
    public const string DelegatesFieldName = "_delegates";

    /// <summary>Per-method metadata embedded in the assembly so the
    /// load path can map each emitted method back to its functor /
    /// arity / promotion shape without parsing IL.</summary>
    public sealed class Entry
    {
        public required int FunctorId { get; init; }
        public required string FunctorName { get; init; }
        public required int Arity { get; init; }
        public required string MethodName { get; init; }
        public required int DelegateSlot { get; init; }

        /// <summary>True iff this predicate's WAM body may be dropped
        /// (--strip-wam): its IL is self-contained, OR it is an indexed
        /// predicate whose dispatch graph (<see cref="IndexGraph"/>) is persisted
        /// (so the delegate no longer reads the WAM).</summary>
        public required bool Strippable { get; init; }

        /// <summary>For a chunk-216 indexed predicate, the serialised
        /// WAM-independent dispatch graph (<see cref="IndexGraphCodec"/>); null
        /// for self-contained shapes. Registered at LoadBundle so a stripped
        /// indexed predicate dispatches without its WAM body.</summary>
        public byte[]? IndexGraph { get; init; }

        /// <summary>Chunk 402 — for a REGION method, the non-root members'
        /// external-entry cursor table (name, arity, MemberEntry cursor); null
        /// otherwise. Flows into <see cref="IlPersistedEntry.RegionMembers"/> so
        /// LoadBundle can alias a stripped member to its region entry.</summary>
        public IReadOnlyList<(string Name, int Arity, int Cursor)>? RegionMembers { get; init; }
    }

    /// <summary>Builds an in-memory .dll holding IL for every
    /// predicate in <paramref name="predicates"/> that the runtime IL
    /// compiler can handle. Returns the .dll bytes, the per-method
    /// metadata callers need at load time, and the
    /// <see cref="IlPatchSite"/> table the LoadBundle path uses to
    /// rewrite each baked build-time atom/functor id constant into the
    /// equivalent runtime-process id (Phase 17 — functor/atom ids drift
    /// across processes since they're ordinal in the global AtomTable
    /// /FunctorTable, and the LINK process accumulates interns that
    /// the RUN process doesn't).</summary>
    /// <param name="prunableFids">Stage 9b-3 (dead-region prune): functor ids of
    /// ABSORBED-ONLY predicates — reached only as <c>br</c>-members of some live region
    /// method, never standalone. They are SKIPPED here (no standalone IL method emitted),
    /// since their code is already baked into the region methods that absorb them. They
    /// REMAIN in <paramref name="predicates"/> (the callee map) so those region methods
    /// can still absorb their bodies, and they keep their Tier-0 WAM as a safety fallback
    /// (a later step may strip it). Null = no prune.</param>
    /// <param name="emitOnly">Phase 33 (bundle-wide calleeMap) — when non-null,
    /// only these functor ids get standalone IL methods; everything else in
    /// <paramref name="predicates"/> serves purely as the CALLEE MAP for
    /// resolution. This is how a multi-entry bundle compiles each entry's IL
    /// against the WHOLE program (cross-module calls resolve) while emitting
    /// each predicate exactly once, in its own entry.</param>
    public static (byte[] DllBytes, IReadOnlyList<Entry> Entries,
        IReadOnlyList<IlPatchSite> Patches) Build(
        string assemblyName,
        IReadOnlyDictionary<int, CompiledPredicate> predicates,
        IReadOnlySet<int>? prunableFids = null,
        Func<int, IReadOnlyList<double>?>? floatPoolProvider = null,
        IReadOnlySet<int>? emitOnly = null)
    {
        var psab = new PersistedAssemblyBuilder(
            new AssemblyName(assemblyName), typeof(object).Assembly);
        var module = psab.DefineDynamicModule(assemblyName);
        var typeBuilder = module.DefineType(
            TypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        // The static field's array size must be known at type definition
        // time, but the slot count depends on how many predicates we
        // actually emit. Pre-pass to filter the persistable set.
        var ic = new IlPredicateCompiler();
        var probeCalleeMap = predicates;
        var eligible = new List<(int FunctorId, CompiledPredicate Pred)>();
        foreach (var (functorId, pred) in predicates)
        {
            if (emitOnly is not null && !emitOnly.Contains(functorId)) continue;
            // Stage 9b-3: an absorbed-only predicate gets no standalone IL method — its
            // body lives in the region methods that absorb it (it stays in the callee
            // map so they still find it). It keeps its WAM as a Tier-0 fallback.
            if (prunableFids?.Contains(functorId) == true) continue;
            // Float-literal support: set the predicate's own float pool so
            // get_float/put_float are accepted (and later value-baked).
            var prevPool = IlPredicateCompiler.BeginFloatPool(floatPoolProvider?.Invoke(functorId));
            bool persistable;
            try { persistable = CanPersist(pred, probeCalleeMap); }
            finally { IlPredicateCompiler.EndFloatPool(prevPool); }
            if (persistable) eligible.Add((functorId, pred));
        }

        // Static field _delegates: PredicateDelegate[] populated at load
        // time. Each persisted method references it for self-CP push and
        // for chain-style cross-predicate IL CP targets. Empty bundles
        // still get the field so the load path's reflection lookup
        // doesn't have to special-case "no eligible predicates".
        var delegatesField = typeBuilder.DefineField(
            DelegatesFieldName,
            typeof(PredicateDelegate[]),
            FieldAttributes.Public | FieldAttributes.Static);

        // Diagnostic env-var for bisecting persisted-IL correctness
        // issues on real-world programs: SHUMWAY_PERSIST_RANGE="lo,hi"
        // restricts the persisted set to predicates whose ordinal in
        // the eligible list (sorted by functorId) is in [lo, hi).
        // Predicates outside the range are still EmitPersistedMethod-
        // skipped at link time, so they fall back to runtime
        // bytecode/IL at LoadBundle. Bisect by halving the range
        // until the smallest range that still produces a wrong
        // answer names the divergent predicate(s).
        int rangeLo = 0;
        int rangeHi = int.MaxValue;
        DiagReadPersistRange(ref rangeLo, ref rangeHi);
        // Stable ordering for bisection: sort eligible by functorId.
        eligible.Sort((a, b) => a.FunctorId.CompareTo(b.FunctorId));

        // Phase 17: switch the predicate compiler into persist mode so
        // every atom-id / functor-id / resume-marker constant emit
        // routes through a sentinel + records the IlPatchSite. After
        // psab.Save() below we scan the resulting PE for each
        // sentinel to fill in AbsoluteByteOffset.
        var patches = ic.BeginPersistEmit();
        var entries = new List<Entry>();
        int slot = 0;
        int ordinal = 0;
        DiagDumpBuildFids();
        foreach (var (functorId, pred) in eligible)
        {
            int thisOrdinal = ordinal++;
            DiagDumpOrdinal(thisOrdinal, functorId, pred);
            if (thisOrdinal < rangeLo || thisOrdinal >= rangeHi)
                continue;
            string functorName = ResolveFunctorName(functorId);
            // Method name layout: P_{slot}_{functorId}_{sanitisedName}.
            // The load path's reflection enumeration order isn't
            // guaranteed by the runtime, so slot and functorId both
            // live in the name where they can be parsed unambiguously.
            string methodName = SanitiseMethodName($"P_{slot}_{functorId}_{functorName}");
            // Phase 17: snapshot patch-list size before this predicate's
            // emit so we can roll back partially-recorded patches on a
            // mid-emit failure (otherwise the post-Save scan would look
            // for sentinels that never landed in any method body).
            int patchesBeforeThisPred = patches.Count;
            // Float-literal support: this predicate's get_float/put_float bake
            // values from its own float pool. Set on this (emit) thread for the
            // whole per-predicate emit; restored in the finally below.
            var emitPrevPool = IlPredicateCompiler.BeginFloatPool(floatPoolProvider?.Invoke(functorId));
            try
            {
                ic.EmitPersistedMethod(
                    typeBuilder, methodName, pred,
                    delegatesField: delegatesField,
                    slot: slot,
                    calleeMap: probeCalleeMap);
            }
            catch (Exception ex)
                when (ex is NotSupportedException
                      || ex.GetType().Namespace?.StartsWith("Sigil") == true)
            {
                IlPredicateCompiler.EndFloatPool(emitPrevPool);
                // CanPersist accepted this predicate (CanCompile was happy)
                // but the emit blew up. Most often this is Sigil's verifier
                // flagging dead-code or stack-mismatch issues in a generated
                // sequence the predicate compiler hasn't been hardened
                // against yet (chunk-190 corner cases are a known source).
                // The runtime IL promotion store would have caught the same
                // failure and skipped the predicate; do the same here so a
                // single bad predicate doesn't abort the entire .shum
                // build.
                System.Console.Error.WriteLine(
                    $"shumway-persisted-il: skipped {functorName} "
                    + $"(fid={functorId}): {ex.GetType().Name}: {ex.Message}");
                DiagDumpSkippedPredicate(functorName, functorId, pred, ex);
                if (patches.Count > patchesBeforeThisPred)
                    patches.RemoveRange(patchesBeforeThisPred,
                        patches.Count - patchesBeforeThisPred);
                continue;
            }
            // Persist the dispatch graph for an indexed predicate so its WAM can
            // be stripped (the delegate reads the registered graph, not the WAM).
            byte[]? indexGraph = IlPredicateCompiler.BuildPersistableIndexGraph(
                pred, probeCalleeMap);
            bool indexed = IlPredicateCompiler.UsesWamBackedIndexedDispatch(
                pred, probeCalleeMap);
            entries.Add(new Entry
            {
                FunctorId = functorId,
                FunctorName = functorName,
                Arity = pred.Arity,
                MethodName = methodName,
                DelegateSlot = slot++,
                IndexGraph = indexGraph,
                // Chunk 402: a region method reports its members' external-entry
                // cursors so the load path can alias stripped members into it.
                RegionMembers = ic.LastRegionMemberCursors,
                // Strippable when self-contained (not indexed) OR indexed with a
                // persisted graph. An indexed predicate whose graph build failed
                // keeps its WAM (the delegate would still read it).
                Strippable = !indexed || indexGraph is not null,
            });
            IlPredicateCompiler.EndFloatPool(emitPrevPool);
        }

        typeBuilder.CreateType();
        ic.EndPersistEmit();

        using var stream = new MemoryStream();
        psab.Save(stream);
        byte[] bytes = stream.ToArray();

        // Phase 17 — locate each sentinel in the saved PE and record
        // its absolute byte offset. Each sentinel is unique and is
        // emitted exactly once (via the 5-byte long form of ldc.i4),
        // so a single forward scan over the PE bytes finds them all.
        // We restrict the scan to method-body byte ranges so a stray
        // metadata int that happens to match a sentinel doesn't get
        // mistaken for a patch site.
        LocatePatchSites(bytes, patches);

        return (bytes, entries, patches);
    }

    /// <summary>Scans <paramref name="peBytes"/> within every method
    /// body's IL stream looking for each <see cref="IlPatchSite.Sentinel"/>;
    /// fills <see cref="IlPatchSite.AbsoluteByteOffset"/> with the byte
    /// position of the int operand (i.e. one byte past the
    /// <c>ldc.i4</c> opcode). Verifies every sentinel was found exactly
    /// once — duplicates indicate a metadata collision and the build
    /// fails loudly rather than silently emitting a bundle that will
    /// patch the wrong four bytes at LoadBundle time.</summary>
    private static void LocatePatchSites(byte[] peBytes, List<IlPatchSite> patches)
    {
        if (patches.Count == 0) return;
        var bySentinel = new Dictionary<int, IlPatchSite>(patches.Count);
        foreach (var s in patches)
        {
            if (!bySentinel.TryAdd(s.Sentinel, s))
                throw new InvalidOperationException(
                    $"Duplicate persisted-IL patch sentinel 0x{s.Sentinel:X8}.");
        }

        using var ms = new MemoryStream(peBytes, writable: false);
        using var peReader = new System.Reflection.PortableExecutable.PEReader(ms);
        var mdReader = peReader.GetMetadataReader();

        var siteMethod = new Dictionary<IlPatchSite, string>();
        foreach (var methodHandle in mdReader.MethodDefinitions)
        {
            var methodDef = mdReader.GetMethodDefinition(methodHandle);
            string methodName = mdReader.GetString(methodDef.Name);
            int rva = methodDef.RelativeVirtualAddress;
            if (rva == 0) continue; // abstract / external / no body
            int fileOffset = RvaToFileOffset(peReader.PEHeaders, rva);
            byte headerFirst = peBytes[fileOffset];
            int ilLength;
            int headerSize;
            switch (headerFirst & 0x03)
            {
                case 0x02: // tiny
                    headerSize = 1;
                    ilLength = headerFirst >> 2;
                    break;
                case 0x03: // fat
                    headerSize = 12;
                    // CodeSize @ offset 4 (uint32 LE).
                    ilLength = peBytes[fileOffset + 4]
                        | (peBytes[fileOffset + 5] << 8)
                        | (peBytes[fileOffset + 6] << 16)
                        | (peBytes[fileOffset + 7] << 24);
                    break;
                default:
                    continue;
            }
            int ilStart = fileOffset + headerSize;
            int ilEnd = ilStart + ilLength;
            // Phase 33 — a PROPER opcode walk, not a sliding byte window.
            // The old heuristic ("any int preceded by byte 0x20") matched
            // sentinel-shaped bytes inside a `switch` instruction's jump
            // table: two adjacent 4-byte branch targets like 0x320 / 0x17E
            // lay out as … 20 03 00 00 7E … — indistinguishable from
            // `ldc.i4 0x7E000003` to the window. Surfaced by the Phase-33
            // bundle-wide calleeMap (more resolvable call sites → more resume
            // cursors → bigger cursor-switch tables). Walking real
            // instruction boundaries makes the exactly-once check sound.
            int q = ilStart;
            while (q < ilEnd)
            {
                byte b = peBytes[q];
                short opVal = b == 0xFE && q + 1 < ilEnd
                    ? (short)(0xFE00 | peBytes[q + 1]) : b;
                int opLen = b == 0xFE ? 2 : 1;
                if (!_ilOperandKind.TryGetValue(opVal, out var kind))
                    throw new InvalidOperationException(
                        $"Persisted-IL scan: unknown CIL opcode 0x{opVal:X4} at "
                        + $"0x{q:X8} in {methodName}.");
                int operandSize;
                switch (kind)
                {
                    case System.Reflection.Emit.OperandType.InlineNone:
                        operandSize = 0; break;
                    case System.Reflection.Emit.OperandType.ShortInlineVar:
                    case System.Reflection.Emit.OperandType.ShortInlineI:
                    case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                        operandSize = 1; break;
                    case System.Reflection.Emit.OperandType.InlineVar:
                        operandSize = 2; break;
                    case System.Reflection.Emit.OperandType.InlineI8:
                    case System.Reflection.Emit.OperandType.InlineR:
                        operandSize = 8; break;
                    case System.Reflection.Emit.OperandType.InlineSwitch:
                    {
                        int n = peBytes[q + opLen]
                            | (peBytes[q + opLen + 1] << 8)
                            | (peBytes[q + opLen + 2] << 16)
                            | (peBytes[q + opLen + 3] << 24);
                        operandSize = 4 + 4 * n;
                        break;
                    }
                    default:   // InlineI, tokens, br targets, ShortInlineR — 4 bytes
                        operandSize = 4; break;
                }
                if (opVal == 0x20 && q + opLen + 4 <= ilEnd)   // ldc.i4
                {
                    int p = q + opLen;
                    int v = peBytes[p]
                        | (peBytes[p + 1] << 8)
                        | (peBytes[p + 2] << 16)
                        | (peBytes[p + 3] << 24);
                    if (bySentinel.TryGetValue(v, out var site))
                    {
                        if (site.AbsoluteByteOffset != 0)
                            throw new InvalidOperationException(
                                $"Persisted-IL sentinel 0x{v:X8} found more than once "
                                + $"(first at 0x{site.AbsoluteByteOffset:X8} in "
                                + $"{siteMethod.GetValueOrDefault(site, "?")}, again at "
                                + $"0x{p:X8} in {methodName}); site kind={site.Kind} "
                                + $"name={site.Name}/{site.Arity}.");
                        site.AbsoluteByteOffset = p;
                        siteMethod[site] = methodName;
                    }
                }
                q += opLen + operandSize;
            }
        }

        foreach (var s in patches)
        {
            if (s.AbsoluteByteOffset == 0)
                throw new InvalidOperationException(
                    $"Persisted-IL sentinel 0x{s.Sentinel:X8} for "
                    + $"{s.Kind} {s.Name}/{s.Arity} was not located "
                    + $"in any method body — Sigil may have compacted the "
                    + $"ldc.i4 or emitted no IL for this site.");
        }
    }

    /// <summary>CIL opcode value → operand kind, built from
    /// <see cref="System.Reflection.Emit.OpCodes"/> via reflection (covers
    /// every one-byte and 0xFE-prefixed two-byte opcode). Drives the
    /// instruction walk in <see cref="LocatePatchSites"/>.</summary>
    private static readonly Dictionary<short, System.Reflection.Emit.OperandType>
        _ilOperandKind = BuildIlOperandTable();

    private static Dictionary<short, System.Reflection.Emit.OperandType> BuildIlOperandTable()
    {
        var map = new Dictionary<short, System.Reflection.Emit.OperandType>();
        foreach (var f in typeof(System.Reflection.Emit.OpCodes).GetFields(
            BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is System.Reflection.Emit.OpCode op)
                map[op.Value] = op.OperandType;
        }
        return map;
    }

    private static int RvaToFileOffset(
        System.Reflection.PortableExecutable.PEHeaders headers, int rva)
    {
        foreach (var section in headers.SectionHeaders)
        {
            if (rva >= section.VirtualAddress
                && rva < section.VirtualAddress + section.VirtualSize)
                return section.PointerToRawData + (rva - section.VirtualAddress);
        }
        throw new InvalidOperationException($"RVA 0x{rva:X8} is not in any section.");
    }

    /// <summary>Returns true iff <paramref name="pred"/> falls inside
    /// the IL compiler's promotable subset — exactly the set
    /// <see cref="IlPredicateCompiler.CanCompile"/> accepts. Chunk 71
    /// covers single-clause-leaf, single-clause-with-meta-CP, indexed-
    /// atom dispatch, and try-me-else chains; the runtime path and
    /// the persisted path share the same eligibility check now.</summary>
    public static bool CanPersist(
        CompiledPredicate pred,
        IReadOnlyDictionary<int, CompiledPredicate>? calleeMap = null)
    {
        // Chunk 217 — indexed dispatch is now persistable. Its IL bakes the
        // predicate's functor id via the chunk-197 patching mechanism (so a
        // fresh process resolves the runtime-process id at LoadBundle), and
        // the dispatch model is rebuilt lazily on first call from the
        // engine's linked code + switch tables. No build-time runtime state
        // crosses the process boundary.
        return new IlPredicateCompiler().CanCompile(pred, calleeMap);
    }

    // ----- Chunk 414 — diag-build-only hooks (-p:ShumwayDiag=true), stripped
    // from normal builds via [Conditional("SHUMWAY_DIAG")]. -----

    /// <summary>SHUMWAY_PERSIST_RANGE="lo,hi" — bisection aid restricting the
    /// persisted set to eligible-ordinal range [lo, hi). No-op (defaults stay)
    /// in normal builds.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagReadPersistRange(ref int rangeLo, ref int rangeHi)
    {
        var rangeStr = System.Environment.GetEnvironmentVariable("SHUMWAY_PERSIST_RANGE");
        if (string.IsNullOrEmpty(rangeStr)) return;
        var parts = rangeStr.Split(',');
        if (parts.Length == 2
            && int.TryParse(parts[0], out int rLo)
            && int.TryParse(parts[1], out int rHi))
        {
            rangeLo = rLo;
            rangeHi = rHi;
        }
    }

    /// <summary>SHUMWAY_PERSIST_DUMP_FIDS="name/arity,..." — prints each
    /// indicator's build-time atom/functor id (cross-process drift diagnosis;
    /// pair with the run side).</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagDumpBuildFids()
    {
        var dumpFidsEnv = System.Environment.GetEnvironmentVariable("SHUMWAY_PERSIST_DUMP_FIDS");
        if (string.IsNullOrEmpty(dumpFidsEnv)) return;
        foreach (var ind in dumpFidsEnv.Split(','))
        {
            var slash = ind.IndexOf('/');
            if (slash < 0) continue;
            if (!int.TryParse(ind.AsSpan(slash + 1), out int ar)) continue;
            string nm = ind.Substring(0, slash);
            int aid = AtomTable.Intern(nm).Id;
            int fid = FunctorTable.Intern(aid, ar);
            System.Console.Error.WriteLine($"[build-fid] {nm}/{ar} atom={aid} functor={fid}");
        }
    }

    /// <summary>SHUMWAY_PERSIST_DUMP_ORDINALS=1 — one line per eligible
    /// predicate with its bisection ordinal.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagDumpOrdinal(int ordinal, int functorId, CompiledPredicate pred)
    {
        if (System.Environment.GetEnvironmentVariable("SHUMWAY_PERSIST_DUMP_ORDINALS") == "1")
            System.Console.Error.WriteLine(
                $"[persist] ord={ordinal} fid={functorId} {ResolveFunctorName(functorId)} clauses={pred.ClauseCount}");
    }

    /// <summary>SHUMWAY_PERSIST_SKIP_DUMP=&lt;file&gt; — appends the emit
    /// exception + bytecode of a predicate the persisted-IL build skipped.</summary>
    [System.Diagnostics.Conditional("SHUMWAY_DIAG")]
    private static void DiagDumpSkippedPredicate(
        string functorName, int functorId, CompiledPredicate pred, Exception ex)
    {
        var skipDumpPath = System.Environment.GetEnvironmentVariable("SHUMWAY_PERSIST_SKIP_DUMP");
        if (string.IsNullOrEmpty(skipDumpPath)) return;
        using var w = System.IO.File.AppendText(skipDumpPath);
        w.WriteLine($"=== {functorName} (fid={functorId}) ===");
        w.WriteLine(ex.ToString());
        if (ex.GetType().GetProperty("DebugInstructions") is { } pi
            && pi.GetValue(ex) is string instructions)
            w.WriteLine("---- IL so far ----\n" + instructions);
        w.WriteLine("---- Bytecode ----");
        int q = 0;
        while (q < pred.Bytecode.Length)
        {
            byte op = pred.Bytecode[q];
            var info = Shumway.Core.OpcodeTable.Get(op);
            w.Write($"  {q:D4}: {(Shumway.Core.Opcode)op,-22}");
            if (info.Size > 1)
            {
                for (int j = 1; j + 4 <= info.Size; j += 4)
                    w.Write($" {Shumway.Core.BytecodeIO.ReadInt32(pred.Bytecode, q + j)}");
            }
            w.WriteLine();
            if (info.Size == 0) break;
            q += info.Size;
        }
        w.WriteLine();
    }

    private static string ResolveFunctorName(int functorId)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        string name = AtomTable.GetById(atomId)?.Name ?? "anon";
        return $"{name}/{arity}";
    }

    /// <summary>Maps a Prolog functor indicator (which can include
    /// operator characters, slashes, module-mangled <c>$</c> etc.) onto
    /// a CLR-identifier-safe method name. Reversibility doesn't matter
    /// here — the per-method <see cref="Entry"/> carries the original
    /// functor metadata so the load path doesn't need to parse names.</summary>
    private static string SanitiseMethodName(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }
}

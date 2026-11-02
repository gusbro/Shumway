using Shumway.Compiler.Ast;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>Predicates laid out indexed in place at a query's setup, their
    /// calls having crossed the JIT threshold (<see cref="TryFlipHotnessInPlace"/>).</summary>
    internal long HotnessFlipsInPlace;

    /// <summary>Lays the indexed version of each predicate whose calls crossed
    /// the JIT threshold since its compile into the persistent buffer, in place
    /// of its plain chain, instead of laying the whole program out again: the
    /// version is compiled and linked at a free place, the predicate's entry
    /// becomes <c>execute &lt;version&gt;</c> for whatever baked it, the address
    /// map and the static region's sites name the version, the chain's chunks
    /// go to the free list, and the product and the caches hold the compile.
    /// Returns the reason when any of them cannot be, and the setup lays the
    /// program out again as it did: a predicate with rules (its helpers would
    /// have to be linked with it), one with source clauses in a module, a
    /// flip back to cold, a debug session, an activation on the buffer, the
    /// wasm tier. A predicate with nothing to index, no argument or no clause,
    /// keeps the chain it has, which is what its indexed compile would be.</summary>
    private string? TryFlipHotnessInPlace(List<int> fids)
    {
        if (_persistentProgram is null || _dynamicLink is null || _staticLink is null
            || _programProduct is not { } product
            || _persistentAddressesCache is null || _persistentAddressBaseCache is null
            || _persistentPredsByAddressCache is null)
            return "no buffer";
        if (_debugEvalDepth > 0 || DebugSession is not null || _flags.EmitDebugInfo || _flags.DebugCodegen)
            return "debugging";
        if (DynChains.Attached > 0) return "an activation on the buffer";
        if (IlPromotion.Wasm is not null) return "wasm tier";
        if (_dynamicLink.SwitchTables is not List<SwitchTable> dynTables) return "switch tables";

        // Every predicate is checked before any is compiled, and every version
        // compiled and placed before the buffer's state is touched: a refusal
        // leaves it as the full build finds it, and a version placed by then is
        // bytes nothing reaches, laid out again with the rest.
        byte[] prog = _persistentProgram;
        var pending = new List<(int Fid, int OldAddr, int Index, List<Clause> Clauses)>(fids.Count);
        List<int>? sameEitherWay = null;
        foreach (int fid in fids)
        {
            if (_jitIndexProfile.CompileDecision(fid) != false || !_jitIndexProfile.IsHot(fid))
                return "not cold to hot: " + FunctorDisplayName(fid);
            if (!_dynStore.IsDynamic(fid)) return "not dynamic: " + FunctorDisplayName(fid);
            if (FunctorTable.Lookup(fid).Arity == 0
                || !_dynStore.TryGetClauses(fid, out var clauses) || clauses.Count == 0)
            {
                // Nothing to index, no argument or no clause: the compile is the
                // chain it has (an empty predicate's stub is a plain chain too).
                (sameEitherWay ??= new()).Add(fid);
                continue;
            }
            if (_staticRewriteHeadFids?.Contains(fid) == true)
                return "source clauses in a module: " + FunctorDisplayName(fid);
            if (_dynamicSeedModule.ContainsKey(fid)) return "seed module: " + FunctorDisplayName(fid);
            if (!_dynStore.HoldsOnlyFacts(fid)) return "rules: " + FunctorDisplayName(fid);
            int index = product.DynamicFunctors.Contains(fid)
                ? product.DynamicPreds.FindIndex(p => p.FunctorId == fid) : -1;
            if (index < 0 || !_dynamicLink.Addresses.TryGetValue(fid, out int oldAddr))
                return "not in the product: " + FunctorDisplayName(fid);
            if (oldAddr + 6 > prog.Length
                || prog[oldAddr] != (byte)Opcode.EnterDynamic
                || prog[oldAddr + 1] != (byte)Opcode.Execute)
                return "not a plain chain: " + FunctorDisplayName(fid);
            pending.Add((fid, oldAddr, index, clauses));
        }

        long profT0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long profCompile = 0, profPlace = 0, profChain = 0;
        static double Ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        int failStubAddr = OpcodeTable.Get(Opcode.Call).Size + OpcodeTable.Get(Opcode.Halt).Size;
        var ctx = new ModuleRewrite.Context(
            DefaultModuleName, _staticRewriteUserLocals ?? EmptyLocalsSentinel,
            _dynStore.Functors, ImportsOf(DefaultModuleName));
        var laid = new List<(int Fid, int OldAddr, int Index, int At, CompiledPredicate Pred, Linker.LinkResult Link)>(pending.Count);
        foreach (var (fid, oldAddr, index, clauses) in pending)
        {
            if (!_dynamicRewriteCache.TryGetValue(fid, out var entry)
                || !ReferenceEquals(entry.LocalsRef, ctx.LocalFunctors)
                || entry.ModesVersion != Modes.Version)
                entry = RewriteDynamicPredicate(fid, clauses, ctx, Modes);
            var module = new ModuleCompiler
            {
                DebugFileId = _debugFileId,
                NonDebuggableFunctors = _nonDebuggableFunctors,
            }.Compile(
                entry.Clauses, cache: null, unindexedFunctors: null, _literalPools,
                dynamicFunctors: _dynStore.Functors, failStubAddr: failStubAddr);
            if (module.Predicates.Count != 1 || module.Predicates[0].FunctorId != fid)
                return "helpers: " + FunctorDisplayName(fid);
            var pred = module.Predicates[0];
            long profT1 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (LoadProfEnabled) { profCompile += profT1 - profT0; profT0 = profT1; }
            int at = ReservePersistent(pred.Bytecode.Length);
            Linker.LinkResult link;
            try
            {
                link = new Linker().Link(
                    module.Predicates, at, externalSymbols: _persistentAddressesCache,
                    switchTableIdBase: _staticLink.SwitchTables.Count + dynTables.Count);
            }
            catch (InvalidOperationException)
            {
                return "link: " + FunctorDisplayName(fid);
            }
            if (link.Bytecode.Length != pred.Bytecode.Length || link.Addresses[fid] != at)
                return "link: " + FunctorDisplayName(fid);
            PlacePersistent(link.Bytecode, at);
            dynTables.AddRange(link.SwitchTables);
            laid.Add((fid, oldAddr, index, at, pred, link));
            if (LoadProfEnabled) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); profPlace += t - profT0; profT0 = t; }
        }

        // A placement may have grown the buffer.
        prog = _persistentProgram!;
        foreach (var (fid, oldAddr, index, at, pred, link) in laid)
        {
            // The chain's chunks: nothing runs them (no activation is on the
            // buffer), and nothing will (its entry leads to the version).
            if (DynChains.Chains.TryGetValue(fid, out var old))
            {
                foreach (var e in old.Entries)
                    if (!e.Retired && e.ChunkAddr >= 0 && e.ChunkLength > 0)
                        ChainPatcher.DropChunk(DynChains, e.ChunkAddr, e.ChunkLength, recycle: true);
                foreach (var (addr, length) in old.DeadChunks)
                    ChainPatcher.DropChunk(DynChains, addr, length, recycle: true);
            }
            DynChains.TrampolineFids.Remove(oldAddr);
            // The version's units, as the dynamic region's: its literals as one,
            // its atoms chunk by chunk.
            ChainPatcher.NoteCodeUnit(DynChains, link.Bytecode, System.Array.Empty<int>());
            PopulateDynChainFor(fid, prog, at, pred.Bytecode.Length);
            NoteDynamicPredicateAtoms(prog, pred);
            // Whatever baked the entry (compiled code, a wasm module, a call site
            // in the dynamic region) reaches the version through it.
            prog[oldAddr] = (byte)Opcode.Execute;
            BytecodeIO.WriteInt32(prog, oldAddr + 1, at);
            _persistentAddressesCache[fid] = at;
            _persistentAddressBaseCache[fid] = at;
            _persistentPredsByAddressCache[at] = pred;
            foreach (var (offset, callee) in _staticLink.UnresolvedSites)
                if (callee == fid)
                    BytecodeIO.WriteInt32(prog, _persistentPrefixLength + offset + 1, at);
            // The product holds what the buffer runs, so does the cache the next
            // build compiles from.
            product.DynamicPreds[index] = pred;
            _productStaleDynamicFids.Remove(fid);
            _dynamicPredicateCache[fid] = pred;
            if (_skipCompileMergedCache is { } merged) merged[fid] = pred;
            _jitIndexProfile.RecordCompileDecision(fid, true);
            HotnessFlipsInPlace++;
            if (LoadProfEnabled)
                ProfProductDrop("setup " + ProfSetupCalls + " hot in place: " + FunctorDisplayName(fid)
                    + "#" + fid + " calls=" + _jitIndexProfile.CallCountFor(fid));
        }
        if (sameEitherWay is not null)
            foreach (int fid in sameEitherWay) _jitIndexProfile.RecordCompileDecision(fid, true);
        if (LoadProfEnabled) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); profChain += t - profT0; profT0 = t; }
        if (laid.Count > 0) ChainPatcher.ReleaseUnreached(DynChains);
        if (LoadProfEnabled && laid.Count > 0)
            Console.Error.WriteLine($"[PROF-FLIP] versions={laid.Count} compile={Ms(profCompile):F0} ms place={Ms(profPlace):F0} ms chain={Ms(profChain):F0} ms release={Ms(System.Diagnostics.Stopwatch.GetTimestamp() - profT0):F0} ms");
        return null;
    }
}

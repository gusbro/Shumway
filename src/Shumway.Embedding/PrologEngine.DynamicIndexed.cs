using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    // ====================================================================
    // runtime in-place extension of extensible-indexed
    // dynamic predicates.
    // ====================================================================

    /// <summary>returns <c>true</c> iff the predicate
    /// <paramref name="functorId"/>'s live dispatch is the
    /// extensible-indexed layout (<c>enter_dynamic</c> +
    /// <c>switch_on_term</c> + <c>try_me_else</c>-headed bucket
    /// chains). Walks the predicate's entry to distinguish:
    /// chains begin with <c>try_me_else</c> (patchable
    /// <c>&lt;next&gt;</c> operand) whereas contiguous
    /// indexed layout begins with <c>try</c>.</summary>
    /// <summary>walks through the multi-level cascade
    /// from the predicate's <c>switch_on_term</c> var label,
    /// descending through any number of <c>switch_on_arg</c> level
    /// switches, until it reaches the final chain head (the chain
    /// that enumerates EVERY clause regardless of indexable args).
    /// Returns -1 if the layout doesn't match.</summary>
    private int FindFinalVarChainHead(Activation engine, int predAddr)
    {
        var prog = engine.CurrentProgram;
        if (prog is null || predAddr + 18 > prog.Length) return -1;
        int p = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);
        while (p > 0 && p + 1 <= prog.Length)
        {
            var op = (Shumway.Core.Opcode)prog[p];
            if (op == Shumway.Core.Opcode.TryMeElse) return p;
            if (op == Shumway.Core.Opcode.RetryMeElse
                && p + 6 <= prog.Length
                && prog[p + 5] == (byte)Shumway.Core.Opcode.Nop) return p;
            if (op != Shumway.Core.Opcode.SwitchOnArg) return -1;
            if (p + 9 > prog.Length) return -1;
            p = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 5);
        }
        return -1;
    }

    private bool IsExtensibleIndexedLayout(Activation engine, int functorId)
    {
        var addrMap = engine.CurrentFunctorAddresses;
        if (addrMap is null) return false;
        if (!addrMap.TryGetValue(functorId, out int predAddr)) return false;
        var prog = engine.CurrentProgram;
        if (prog is null || predAddr + 18 > prog.Length) return false;
        if (prog[predAddr] != (byte)Shumway.Core.Opcode.EnterDynamic) return false;
        if (prog[predAddr + 1] != (byte)Shumway.Core.Opcode.SwitchOnTerm) return false;
        // for single-arg the var label points at a
        // try_me_else chain head; for multi-arg it points at the
        // next level's switch_on_arg (or, after layers of cascade,
        // eventually at a chain head). Walk through switch_on_arg
        // nodes until we reach a try_me_else chain head; that's
        // the signature of every indexed layout.
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);
        while (varLbl > 0 && varLbl + 1 <= prog.Length)
        {
            var op = (Shumway.Core.Opcode)prog[varLbl];
            if (op == Shumway.Core.Opcode.TryMeElse) return true;
            if (op == Shumway.Core.Opcode.RetryMeElse
                && varLbl + 6 <= prog.Length
                && prog[varLbl + 5] == (byte)Shumway.Core.Opcode.Nop) return true;
            if (op != Shumway.Core.Opcode.SwitchOnArg) return false;
            // switch_on_arg's var label is at +5 (skip the opcode
            // byte and the 4-byte arg_idx).
            if (varLbl + 9 > prog.Length) return false;
            varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, varLbl + 5);
        }
        return false;
    }

    /// <summary>walks the chain starting at
    /// <paramref name="chainHead"/>, following <c>&lt;next&gt;</c>
    /// operands until the entry whose <c>&lt;next&gt;</c> is the
    /// absolute <see cref="Activation.DynamicFailStubAddr"/>. Returns the
    /// absolute byte offset of that tail entry's <c>&lt;next&gt;</c>
    /// operand (where the assertz extension patches), or <c>-1</c>
    /// on any malformed chain.</summary>
    private static int WalkChainToTailNextOperand(
        byte[] prog, int chainHead, int failStubAddr)
    {
        int cur = chainHead;
        bool isHead = true;
        // A chain instruction is ≥5 bytes, so a well-formed chain has at
        // most prog.Length/5 distinct entries — more steps than that means
        // the <next> operands form a cycle (corrupted buffer). Returning -1
        // sends the caller to the rebuild fallback instead of spinning.
        int stepsLeft = prog.Length / 5 + 1;
        while (true)
        {
            if (cur < 0 || cur + 5 > prog.Length || --stepsLeft < 0) return -1;
            var op = (Shumway.Core.Opcode)prog[cur];
            if (isHead)
            {
                if (op != Shumway.Core.Opcode.TryMeElse) return -1;
            }
            else
            {
                if (op != Shumway.Core.Opcode.RetryMeElse) return -1;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStubAddr) return cur + 1;
            cur = next;
            isHead = false;
        }
    }

    /// <summary>returns <c>true</c> when the chain
    /// entry at <paramref name="entryAddr"/> sits in a 9-byte
    /// chain-instruction slot (the original try_me_else footprint,
    /// possibly demoted to retry_me_else + 4 nops by an asserta).
    /// In that case the entry's check_visible / execute live at the
    /// head offsets (+9 / +26); for a native non-head 5-byte slot
    /// they live at +5 / +22. The distinguisher is the byte at
    /// entry+5: <c>Nop</c> for a demoted-head slot, anything else
    /// (start of check_visible) for a native non-head.</summary>
    private static int ChainEntryHeaderSize(byte[] prog, int entryAddr)
    {
        // try_me_else (9-byte) head: opcode TryMeElse.
        if (entryAddr + 1 <= prog.Length
            && prog[entryAddr] == (byte)Shumway.Core.Opcode.TryMeElse)
            return 9;
        // retry_me_else: 5-byte native OR 9-byte demoted-from-head.
        // Demoted has Nop at offset +5; native has CheckVisible.
        if (entryAddr + 6 <= prog.Length
            && prog[entryAddr + 5] == (byte)Shumway.Core.Opcode.Nop)
            return 9;
        return 5;
    }

    /// <summary>given the predicate entry and the new
    /// clause's arg-0 classification, locate the bucket chain head
    /// the new clause should be appended to. Returns <c>-1</c> when
    /// no bucket exists (new key) or the arg is var (every bucket
    /// would need to extend). Both cases are deferred to the
    /// persistent-rebuild fallback by the caller.</summary>
    private static int FindBucketChainHead(
        Activation engine, int predAddr, Shumway.Compiler.Ast.Clause newClause)
    {
        var prog = engine.CurrentProgram!;
        // The switch_on_term sits at predAddr + 1; its operands at
        // +2 (var), +6 (const), +10 (list), +14 (struct).
        int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 6);
        int listLbl  = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 10);
        int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 14);
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);

        // Pull arg-0 of the new clause's head.
        Shumway.Compiler.Ast.Term head = newClause.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)newClause.Term).Args[0]
            : newClause.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm headComp || headComp.Args.Length == 0)
            return -1;
        var arg0 = headComp.Args[0];

        // Walk the const-label cascade for level 0 only. The cascade
        // is atom → integer → structure within one level; on multi-
        // arg the cascade's last default points at the
        // NEXT LEVEL's switch_on_arg, which marks the level boundary
        // and stops the walk — arg-0's bucket is in level 0 only,
        // higher-level buckets are routed through different chain
        // extensions.
        int CascadeLookup(int startAddr, int key, Shumway.Core.Opcode wantedOpcode)
        {
            int p = startAddr;
            while (p > 0 && p + 5 <= prog.Length)
            {
                var op = (Shumway.Core.Opcode)prog[p];
                // SwitchOnArg marks the boundary between level 0 and
                // the next indexable level — stop here.
                if (op == Shumway.Core.Opcode.SwitchOnArg) return -1;
                if (op != Shumway.Core.Opcode.SwitchOnAtom
                    && op != Shumway.Core.Opcode.SwitchOnInteger
                    && op != Shumway.Core.Opcode.SwitchOnStructure) return -1;
                int tableId = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
                var table = engine.GetSwitchTable(tableId);
                if (table is null) return -1;
                if (op == wantedOpcode)
                {
                    int target = table.Lookup(key);
                    return target == table.DefaultAddress ? -1 : target;
                }
                p = table.DefaultAddress;
                if (p == varLbl) return -1;
            }
            return -1;
        }

        switch (arg0)
        {
            case Shumway.Compiler.Ast.AtomTerm a:
                int atomId = Shumway.Core.AtomTable.Intern(a.Name, permanent: true).Id;
                return CascadeLookup(constLbl, atomId, Shumway.Core.Opcode.SwitchOnAtom);
            case Shumway.Compiler.Ast.IntTerm n
                when n.Value >= int.MinValue && n.Value <= int.MaxValue:
                return CascadeLookup(constLbl, (int)n.Value, Shumway.Core.Opcode.SwitchOnInteger);
            case Shumway.Compiler.Ast.CompoundTerm c when c.Functor == "." && c.Args.Length == 2:
                return listLbl == varLbl ? -1 : listLbl;
            case Shumway.Compiler.Ast.CompoundTerm c:
                int functorId = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
                // struct cascade: structLbl points at switch_on_structure.
                if (structLbl <= 0 || structLbl == varLbl) return -1;
                if (prog[structLbl] != (byte)Shumway.Core.Opcode.SwitchOnStructure) return -1;
                int sTableId = Shumway.Core.BytecodeIO.ReadInt32(prog, structLbl + 1);
                var sTable = engine.GetSwitchTable(sTableId);
                if (sTable is null) return -1;
                int sTarget = sTable.Lookup(functorId);
                return sTarget == sTable.DefaultAddress ? -1 : sTarget;
            default:
                return -1;
        }
    }

    /// <summary>In-place extension of an extensible-
    /// indexed dynamic predicate's chains for an <c>assertz</c>.
    /// Compiles the new clause body, appends it to the buffer, then
    /// either (155b) extends the bucket chain for the new clause's
    /// arg-0 key when that key has an existing bucket, or (155c)
    /// creates a brand-new bucket chain at the end of the buffer
    /// and adds the new (key → chain-head) entry to the appropriate
    /// sub-switch table when the key is new. The var-fallthrough
    /// chain is always extended.
    ///
    /// Returns <c>false</c> if the predicate isn't using the chunk-
    /// 155a layout, the new clause is var-arg-at-0 (would need to
    /// extend every bucket — a concern), or the new key
    /// needs a sub-switch that doesn't exist yet (e.g. first int
    /// assertz to an atom-only predicate). In any of those cases
    /// the caller falls back to rebuild.</summary>
    private bool TryAppendToIndexedDynamic(
        Activation engine, int functorId, Shumway.Compiler.Ast.Clause newClause)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        // capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;

        // Pull the new clause's head arg-0 — we classify it to decide
        // the same-key vs new-key vs var-arg path.
        Shumway.Compiler.Ast.Term head = newClause.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)newClause.Term).Args[0]
            : newClause.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm headComp || headComp.Args.Length == 0)
            return false;
        var arg0 = headComp.Args[0];
        bool isVarArg = arg0 is Shumway.Compiler.Ast.VarTerm;

        // Plan which chain tails the new clause's body will be
        // linked into. var-arg-at-0 extends every
        // chain — var fallthrough, list bucket if present, and
        // every bucket chain reachable through the atom / integer /
        // structure sub-switch tables. Concrete arg-0 (chunks
        // 155b / 155c) extends just (var chain + the specific
        // bucket chain, creating it for new-key).
        int bucketChainHead = -1;
        bool isNewKey = false;
        // for multi-arg layouts, the var slot at
        // predAddr+2 points at the next level's switch_on_arg, not
        // at the final var chain. Walk through the level cascade to
        // reach the actual chain head.
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return false;
        var chainTailNexts = new List<int>();
        int newKeyValue = 0;
        int subSwitchTableId = -1;

        if (isVarArg)
        {
            // Collect every chain's tail-next operand. Includes the
            // var chain itself so we don't double-extend below.
            if (!CollectAllChainTailNextOperands(engine, predAddr, chainTailNexts))
                return false;
        }
        else
        {
            int varTailNext = WalkChainToTailNextOperand(prog, varChainHead, failStub);
            if (varTailNext < 0) return false;
            chainTailNexts.Add(varTailNext);
            bucketChainHead = FindBucketChainHead(engine, predAddr, newClause);
            isNewKey = bucketChainHead < 0;
            if (!isNewKey)
            {
                int bucketTailNext = WalkChainToTailNextOperand(prog, bucketChainHead, failStub);
                if (bucketTailNext < 0) return false;
                chainTailNexts.Add(bucketTailNext);
            }
            else
            {
                // Locate the sub-switch matching the new key's type;
                // bail to rebuild if the type's sub-switch doesn't
                // exist yet (would be a layout change).
                if (!TryLocateSubSwitchForArg(
                        engine, predAddr, arg0, out newKeyValue, out subSwitchTableId))
                    return false;
            }
        }

        // Corruption tripwire — every tail slot we're about to patch must
        // lie inside this activation's believed content length. A slot at
        // or beyond ProgramLength means the chain in the shared buffer
        // extends past this activation's append position (a stale
        // ProgramLength): AppendCode would OVERWRITE those live entries and
        // the tail patch would then write the new entry's own address into
        // its own <next> operand — the self-pointing retry_me_else cycle.
        // Rebuild from the store instead of writing the corruption.
        foreach (int tn in chainTailNexts)
            if (tn + sizeof(int) > engine.ProgramLength)
            {
                ChainCorruptionRecover(
                    "assertz-indexed", engine, functorId,
                    $"tail slot {tn} beyond content length {engine.ProgramLength}");
                return true;   // the rebuild absorbed the store (new clause included)
            }

        // Compile the new clause (transforms identical to the chain
        // path; shared helper with a fact fast path).
        var compiledClause = CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return false;

        // Body chunk: [meta(dbg, 0)] + body bytes. Clause-source-position
        // index is irrelevant for runtime-asserted clauses; the dbg marker is
        // gated on the compile_mode flag (release omits it — the interpreter
        // never dispatches a no-op on entry).
        var bodyEmitter = new Shumway.Compiler.Wam.BytecodeEmitter();
        if (_flags.EmitDebugInfo) bodyEmitter.EmitMetaDbgInfo(0);
        int bodyContentLocalStart = bodyEmitter.Position;
        bodyEmitter.AppendBytes(compiledClause.Bytecode);
        byte[] bodyChunk = bodyEmitter.ToBytes();
        int bodyAddr = engine.AppendCode(bodyChunk);
        prog = engine.CurrentProgram!;

        // Patch call sites inside the body to absolute targets.
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = bodyAddr + bodyContentLocalStart + site.OpcodeOffset + 1;
            int target = addrMap.TryGetValue(site.CalleeFunctorId, out int addr)
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, target);
        }

        // Helper: append a non-head chain entry (retry_me_else
        // <fail_stub>; check_visible; execute <targetBody>).
        int AppendNonHeadEntry(int targetBody)
        {
            var em = new Shumway.Compiler.Wam.BytecodeEmitter();
            em.EmitRetryMeElse(failStub);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(targetBody);
            return engine.AppendCode(em.ToBytes());
        }

        // For the new-key concrete case, the new bucket itself is
        // built (and added to the sub-switch table) BEFORE we walk
        // the chain-tail list — the new bucket isn't in
        // chainTailNexts because it didn't exist when we planned.
        if (!isVarArg && isNewKey)
        {
            // Build a fresh bucket chain containing every var-arg
            // clause's body (they match every concrete key) plus the
            // new clause's body, then add (new_key → new_chain_head)
            // to the sub-switch table.
            var varArgBodies = CollectVarArgBodies(engine, varChainHead, functorId);
            int newBucketHead = BuildAndAppendNewBucketChain(
                engine, failStub, headArity: headComp.Args.Length,
                varArgBodies, bodyAddr);
            prog = engine.CurrentProgram!;
            var oldTable = engine.GetSwitchTable(subSwitchTableId);
            if (oldTable is null) return false;
            var newTable = oldTable.WithAdditionalEntry(newKeyValue, newBucketHead);
            engine.ReplaceSwitchTable(subSwitchTableId, newTable);
            MirrorSwitchTableIntoDynamicLink(subSwitchTableId, newTable);
        }

        // For every chain in the plan, append a new chain entry
        // pointing at the new body and patch the chain's prior tail.
        // This covers (depending on path):
        //   - bucket + var (2 entries).
        //   - var only (the new bucket already includes
        //     the new clause as its tail).
        //   - every chain (var + list + every bucket
        //     across all sub-switches).
        foreach (int tailNext in chainTailNexts)
        {
            int newEntry = AppendNonHeadEntry(bodyAddr);
            prog = engine.CurrentProgram!;
            // Tripwire: a fresh append always lands beyond every existing
            // chain slot. newEntry at or below the slot being patched means
            // the append position was stale and the write would splice the
            // chain into itself. (The pre-append ProgramLength check above
            // makes this unreachable; keep it as the last line of defense —
            // the write below is the one that creates the dispatch cycle.)
            if (newEntry <= tailNext)
            {
                ChainCorruptionRecover(
                    "assertz-indexed-patch", engine, functorId,
                    $"new entry {newEntry} at/below tail slot {tailNext}");
                return true;
            }
            Shumway.Core.BytecodeIO.WriteInt32(prog, tailNext, newEntry);
        }

        // The new chunks may have grown the persistent buffer (chunk
        // 151b) — keep PrologEngine's cached reference current (owner),
        // or mark the host buffer for rebuild (non-owner engine: its
        // in-place extension is invisible to the newer buffer).
        SyncOrInvalidateAfterMutation(engine, ownsHost);

        // Refresh interpreter pools — the clause may have interned new
        // literals (skipped when the pools didn't grow).
        RefreshLiteralPoolsIfGrown(engine);
        return true;
    }

    /// <summary>locates the sub-switch
    /// (<c>switch_on_atom</c> / <c>switch_on_integer</c> /
    /// <c>switch_on_structure</c>) that handles the new clause's
    /// arg-0 type and returns its table id and the key value for the
    /// new arg. Returns <c>false</c> if the predicate doesn't yet
    /// have a sub-switch of that type — adding one would be a layout
    /// change beyond scope.</summary>
    private static bool TryLocateSubSwitchForArg(
        Activation engine, int predAddr, Shumway.Compiler.Ast.Term arg0,
        out int keyValue, out int tableId)
    {
        keyValue = 0; tableId = -1;
        var prog = engine.CurrentProgram!;
        int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 6);
        int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, predAddr + 2);

        Shumway.Core.Opcode wantedOp;
        switch (arg0)
        {
            case Shumway.Compiler.Ast.AtomTerm a:
                wantedOp = Shumway.Core.Opcode.SwitchOnAtom;
                keyValue = Shumway.Core.AtomTable.Intern(a.Name, permanent: true).Id;
                break;
            case Shumway.Compiler.Ast.IntTerm n
                when n.Value >= int.MinValue && n.Value <= int.MaxValue:
                wantedOp = Shumway.Core.Opcode.SwitchOnInteger;
                keyValue = (int)n.Value;
                break;
            case Shumway.Compiler.Ast.CompoundTerm c when c.Functor != "." || c.Args.Length != 2:
                wantedOp = Shumway.Core.Opcode.SwitchOnStructure;
                keyValue = Shumway.Core.FunctorTable.Intern(
                    Shumway.Core.AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length);
                break;
            default:
                return false;  // list (handled by listLbl, not a sub-switch) or unsupported
        }

        // Walk the cascade from constLbl looking for the wanted
        // sub-switch opcode. Stop at SwitchOnArg — multi-
        // arg layouts put a level boundary there, and the new key's
        // sub-switch must live within level 0.
        int p = constLbl;
        while (p > 0 && p + 5 <= prog.Length && p != varLbl)
        {
            var op = (Shumway.Core.Opcode)prog[p];
            if (op == Shumway.Core.Opcode.SwitchOnArg) return false;
            if (op != Shumway.Core.Opcode.SwitchOnAtom
                && op != Shumway.Core.Opcode.SwitchOnInteger
                && op != Shumway.Core.Opcode.SwitchOnStructure)
                return false;
            if (op == wantedOp)
            {
                tableId = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
                return true;
            }
            int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, p + 1);
            var table = engine.GetSwitchTable(tid);
            if (table is null) return false;
            p = table.DefaultAddress;
        }
        return false;
    }

    /// <summary>walks the var-fallthrough chain and
    /// returns the body addresses of clauses whose arg-0 is var
    /// (so they'd be merged into every concrete bucket chain). The
    /// var chain enumerates clauses in source order, so its Nth
    /// entry's <c>execute &lt;body&gt;</c> target is the body of
    /// <c>_dynStore[functorId][N]</c>; the dynamic-store
    /// clause carries the original arg-0 classification.</summary>
    private List<int> CollectVarArgBodies(Activation engine, int varChainHead, int functorId)
    {
        var result = new List<int>();
        if (!_dynStore.TryGetClauses(functorId, out var clauses))
            return result;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        int cur = varChainHead;
        int idx = 0;
        // Cycle guard — same bound as WalkChainToTailNextOperand: a
        // corrupted <next> cycle must terminate the walk, not hang it.
        int stepsLeft = prog.Length / 5 + 1;
        while (true)
        {
            if (cur < 0 || cur + 27 > prog.Length || --stepsLeft < 0) break;
            int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
            int execOpPos = cur + chainHeaderSize + 17;
            if (execOpPos + 5 > prog.Length) break;
            if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) break;
            int bodyAddr = Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1);
            if (idx < clauses.Count - 1 && IsVarArgAt0(clauses[idx]))
                result.Add(bodyAddr);
            idx++;
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStub) break;
            cur = next;
        }
        return result;
    }

    private static bool IsVarArgAt0(Shumway.Compiler.Ast.Clause c)
    {
        Shumway.Compiler.Ast.Term head = c.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)c.Term).Args[0]
            : c.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm hc || hc.Args.Length == 0)
            return false;
        return hc.Args[0] is Shumway.Compiler.Ast.VarTerm;
    }

    /// <summary>recursively enumerates every chain head
    /// reachable from a switch address. Walks both single- and
    /// multi-level layouts: switch_on_term and switch_on_arg cascade
    /// through 4 child labels; switch_on_atom / _integer /
    /// _structure (and their _arg variants) recurse through every
    /// table value plus the default. Chain heads — try_me_else, or
    /// a demoted retry_me_else + Nop at +5 — are added to
    /// <paramref name="heads"/>. Recursion stops at fail-stub
    /// addresses or unrecognised opcodes.</summary>
    private void EnumerateChainHeadsRecursive(
        Activation engine, int addr, HashSet<int> heads, HashSet<int> visited)
    {
        if (addr <= 0) return;
        var prog = engine.CurrentProgram;
        if (prog is null || addr + 1 > prog.Length) return;
        if (!visited.Add(addr)) return;
        var op = (Shumway.Core.Opcode)prog[addr];
        switch (op)
        {
            case Shumway.Core.Opcode.SwitchOnTerm:
            {
                int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                int listLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 9);
                int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 13);
                EnumerateChainHeadsRecursive(engine, varLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, constLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, listLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, structLbl, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnArg:
            {
                int varLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                int constLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 9);
                int listLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 13);
                int structLbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 17);
                EnumerateChainHeadsRecursive(engine, varLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, constLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, listLbl, heads, visited);
                EnumerateChainHeadsRecursive(engine, structLbl, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtom:
            case Shumway.Core.Opcode.SwitchOnInteger:
            case Shumway.Core.Opcode.SwitchOnStructure:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                foreach (int v in table.Values)
                    EnumerateChainHeadsRecursive(engine, v, heads, visited);
                EnumerateChainHeadsRecursive(engine, table.DefaultAddress, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtomArg:
            case Shumway.Core.Opcode.SwitchOnIntegerArg:
            case Shumway.Core.Opcode.SwitchOnStructureArg:
            {
                // arg_idx at +1, table_id at +5.
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                foreach (int v in table.Values)
                    EnumerateChainHeadsRecursive(engine, v, heads, visited);
                EnumerateChainHeadsRecursive(engine, table.DefaultAddress, heads, visited);
                break;
            }
            case Shumway.Core.Opcode.TryMeElse:
                heads.Add(addr);
                break;
            case Shumway.Core.Opcode.RetryMeElse:
                // Demoted chain head (after asserta) — has Nop at +5.
                if (addr + 6 <= prog.Length
                    && prog[addr + 5] == (byte)Shumway.Core.Opcode.Nop)
                    heads.Add(addr);
                break;
            // fail_stub or anything else: stop.
        }
    }

    /// <summary>Collects every chain's tail-next
    /// operand, used by the var-arg-at-0 / multi-arg extension paths
    /// that need to append a new entry to every chain. Builds on
    /// <see cref="EnumerateChainHeadsRecursive"/> so multi-level
    /// layouts are fully covered.</summary>
    private bool CollectAllChainTailNextOperands(
        Activation engine, int predAddr, List<int> tailNextOperands)
    {
        var prog = engine.CurrentProgram;
        if (prog is null) return false;
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;
        var heads = new HashSet<int>();
        var visited = new HashSet<int>();
        // predAddr+1 is the top-level switch_on_term / switch_on_arg.
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);
        foreach (int head in heads)
        {
            int tailNext = WalkChainToTailNextOperand(prog, head, failStub);
            if (tailNext < 0) return false;
            tailNextOperands.Add(tailNext);
        }
        return true;
    }

    // ====================================================================
    // in-place asserta for extensible-indexed dynamic
    // predicates.
    // ====================================================================

    /// <summary>Prepends a clause to an extensible-
    /// indexed predicate in place. Asserta is harder than assertz
    /// because the chain head's address changes (the new entry
    /// becomes the head, the old head gets demoted to a non-head),
    /// and the old head was referenced from external pointer slots
    /// — switch_on_term operands, switch_on_atom/integer/structure
    /// table values, sub-switch default-cascade addresses. Each of
    /// those slots that pointed at an old head gets redirected to
    /// the new head; the demoted old head's <c>&lt;next&gt;</c>
    /// operand is unchanged (still references the second entry in
    /// the chain or fail_stub).
    ///
    /// Returns <c>false</c> when the predicate isn't using the
    /// layout, the new key has no existing sub-switch
    /// (would be a layout change), or any chain to demote isn't
    /// currently a <c>try_me_else</c> head (a chain whose only
    /// remaining live entry has died — unusual but a possibility
    /// after a retract).</summary>
    private bool TryPrependToIndexedDynamic(
        Activation engine, int functorId, Shumway.Compiler.Ast.Clause newClause)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        // capture buffer ownership before any AppendCode.
        bool ownsHost = EngineOwnsHostBuffer(engine);
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;
        if (failStub <= 0) return false;

        Shumway.Compiler.Ast.Term head = newClause.Kind == Shumway.Compiler.Ast.ClauseKind.Rule
            ? ((Shumway.Compiler.Ast.CompoundTerm)newClause.Term).Args[0]
            : newClause.Term;
        if (head is not Shumway.Compiler.Ast.CompoundTerm headComp || headComp.Args.Length == 0)
            return false;
        var arg0 = headComp.Args[0];
        int arity = headComp.Args.Length;
        bool isVarArg = arg0 is Shumway.Compiler.Ast.VarTerm;

        // for multi-arg, the var slot at predAddr+2
        // cascades through switch_on_arg before reaching a chain
        // head. Walk the cascade to find the final var chain.
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return false;

        // Plan: which chain heads need demotion? var-arg touches
        // every chain; same-key touches (bucket + var); new-key
        // touches (var) and creates a brand-new bucket without
        // demotion.
        var chainsToDemote = new List<int>();
        bool isNewKey = false;
        int newKeyValue = 0;
        int subSwitchTableId = -1;

        if (isVarArg)
        {
            var heads = new HashSet<int>();
            if (!CollectAllChainHeadsForRedirect(engine, predAddr, heads))
                return false;
            chainsToDemote.AddRange(heads);
        }
        else
        {
            chainsToDemote.Add(varChainHead);
            int bucketChainHead = FindBucketChainHead(engine, predAddr, newClause);
            isNewKey = bucketChainHead < 0;
            if (!isNewKey)
            {
                chainsToDemote.Add(bucketChainHead);
            }
            else
            {
                if (!TryLocateSubSwitchForArg(
                        engine, predAddr, arg0, out newKeyValue, out subSwitchTableId))
                    return false;
            }
        }

        // Validate every chain head we plan to demote currently has
        // try_me_else as its first opcode. A chain whose head was
        // demoted by a prior asserta or whose head was retracted
        // (died != MaxValue) but never compacted would fail this
        // check — let the rebuild handle those rarities.
        foreach (int h in chainsToDemote)
        {
            if (h < 0 || h + 9 > prog.Length) return false;
            if (prog[h] != (byte)Shumway.Core.Opcode.TryMeElse) return false;
        }
        // Also dedupe — a chain referenced by multiple keys (e.g.
        // every bucket reachable from a shared sub-switch default)
        // would otherwise be demoted twice. CollectAllChainHeadsForRedirect
        // dedupes, but the same-key path adds bucket + var manually.
        chainsToDemote = chainsToDemote.Distinct().ToList();

        // Compile the new clause's body (shared helper with
        // a fact fast path).
        var compiledClause = CompileRuntimeAssertClause(engine, functorId, newClause);
        if (compiledClause is null) return false;

        var bodyEmitter = new Shumway.Compiler.Wam.BytecodeEmitter();
        if (_flags.EmitDebugInfo) bodyEmitter.EmitMetaDbgInfo(0);
        int bodyContentLocalStart = bodyEmitter.Position;
        bodyEmitter.AppendBytes(compiledClause.Bytecode);
        byte[] bodyChunk = bodyEmitter.ToBytes();
        int bodyAddr = engine.AppendCode(bodyChunk);
        prog = engine.CurrentProgram!;

        // Patch call sites in the body to absolute targets.
        foreach (var site in compiledClause.CallSites)
        {
            int operandPos = bodyAddr + bodyContentLocalStart + site.OpcodeOffset + 1;
            int target = addrMap.TryGetValue(site.CalleeFunctorId, out int addr)
                ? addr
                : Shumway.Core.CallTarget.ForUndefined(site.CalleeFunctorId);
            Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, target);
        }

        // For new-key concrete: build a brand-new bucket chain
        // (NEW BODY FIRST, then var-args), add to switch table.
        // No demotion needed for the new bucket.
        if (isNewKey)
        {
            var varArgBodies = CollectVarArgBodies(engine, varChainHead, functorId);
            // Asserta-flavoured layout: new body first, var-args after.
            int newBucketHead = BuildAndAppendBucketChainAsserta(
                engine, failStub, arity, bodyAddr, varArgBodies);
            prog = engine.CurrentProgram!;
            var oldTable = engine.GetSwitchTable(subSwitchTableId);
            if (oldTable is null) return false;
            var newTable = oldTable.WithAdditionalEntry(newKeyValue, newBucketHead);
            engine.ReplaceSwitchTable(subSwitchTableId, newTable);
            MirrorSwitchTableIntoDynamicLink(subSwitchTableId, newTable);
        }

        // Demote each chain to demote, build new head chunk, record
        // the old → new redirect.
        var redirectMap = new Dictionary<int, int>();
        foreach (int oldHead in chainsToDemote)
        {
            // Demote try_me_else (9 bytes) to retry_me_else (5
            // bytes) + 4 nops (4 bytes). The <next> operand at
            // +1..+4 stays — retry_me_else uses it identically.
            prog[oldHead] = (byte)Shumway.Core.Opcode.RetryMeElse;
            prog[oldHead + 5] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 6] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 7] = (byte)Shumway.Core.Opcode.Nop;
            prog[oldHead + 8] = (byte)Shumway.Core.Opcode.Nop;

            // New head: try_me_else <oldHead> arity + check_visible
            // + execute <bodyAddr>.
            var em = new Shumway.Compiler.Wam.BytecodeEmitter();
            em.EmitTryMeElse(oldHead, arity);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodyAddr);
            int newHead = engine.AppendCode(em.ToBytes());
            prog = engine.CurrentProgram!;
            redirectMap[oldHead] = newHead;
        }

        // Walk every pointer slot that could reference a chain head
        // and redirect any that match.
        RedirectChainHeads(engine, predAddr, redirectMap);

        SyncOrInvalidateAfterMutation(engine, ownsHost);
        // refresh skipped when the pools didn't grow.
        RefreshLiteralPoolsIfGrown(engine);
        return true;
    }

    /// <summary>like
    /// <see cref="BuildAndAppendNewBucketChain"/> but with the new
    /// clause's body FIRST (the asserta order) followed by the var-
    /// arg bodies in source order. Returns the new bucket chain
    /// head address.</summary>
    private int BuildAndAppendBucketChainAsserta(
        Activation engine, int failStub, int headArity,
        int newBodyAddr, IReadOnlyList<int> varArgBodies)
    {
        var bodies = new List<int> { newBodyAddr };
        bodies.AddRange(varArgBodies);
        int count = bodies.Count;
        const int HeadEntrySize = 9 + 17 + 5;
        const int NonHeadEntrySize = 5 + 17 + 5;
        int startAddr = engine.ProgramLength;
        var em = new Shumway.Compiler.Wam.BytecodeEmitter();
        for (int i = 0; i < count; i++)
        {
            int thisSize = i == 0 ? HeadEntrySize : NonHeadEntrySize;
            int nextAddr = (i == count - 1) ? failStub
                                            : startAddr + em.Position + thisSize;
            if (i == 0) em.EmitTryMeElse(nextAddr, headArity);
            else        em.EmitRetryMeElse(nextAddr);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodies[i]);
        }
        int chunkAddr = engine.AppendCode(em.ToBytes());
        System.Diagnostics.Debug.Assert(chunkAddr == startAddr);
        return chunkAddr;
    }

    /// <summary>Collects every chain head reachable
    /// from the predicate's entry into <paramref name="heads"/>,
    /// across every level of multi-arg switch dispatch.
    /// Delegates to <see cref="EnumerateChainHeadsRecursive"/>.</summary>
    private bool CollectAllChainHeadsForRedirect(
        Activation engine, int predAddr, HashSet<int> heads)
    {
        if (engine.CurrentProgram is null) return false;
        var visited = new HashSet<int>();
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);
        return true;
    }

    /// <summary>walks every pointer slot that can
    /// reference a chain head and replaces any value present in
    /// <paramref name="redirect"/> with the new address. Touches:
    /// <c>switch_on_term</c>'s var / list / struct operands at
    /// <c>predAddr + 2 / 10 / 14</c> (const_lbl is excluded —
    /// it points at a sub-switch, never a chain head directly);
    /// every sub-switch table's keys-and-values plus its default
    /// address. Each modified switch table is replaced with a new
    /// instance and mirrored into the cached <c>_dynamicLink</c>.</summary>
    private void RedirectChainHeads(
        Activation engine, int predAddr, IReadOnlyDictionary<int, int> redirect)
    {
        if (redirect.Count == 0) return;
        // walk the predicate's dispatch graph recursively,
        // patching every switch operand and switch-table value/default
        // that matches an entry in redirect.
        var visitedSwitches = new HashSet<int>();
        RedirectChainHeadsRecursive(engine, predAddr + 1, redirect, visitedSwitches);
    }

    private void RedirectChainHeadsRecursive(
        Activation engine, int addr, IReadOnlyDictionary<int, int> redirect,
        HashSet<int> visited)
    {
        if (addr <= 0) return;
        var prog = engine.CurrentProgram!;
        if (addr + 1 > prog.Length) return;
        if (!visited.Add(addr)) return;
        var op = (Shumway.Core.Opcode)prog[addr];

        void PatchOperand(int operandPos)
        {
            int cur = Shumway.Core.BytecodeIO.ReadInt32(prog, operandPos);
            if (redirect.TryGetValue(cur, out int repl))
                Shumway.Core.BytecodeIO.WriteInt32(prog, operandPos, repl);
        }

        switch (op)
        {
            case Shumway.Core.Opcode.SwitchOnTerm:
                // 4 address operands at +1, +5, +9, +13.
                for (int j = 0; j < 4; j++) PatchOperand(addr + 1 + 4 * j);
                // Recurse into each label.
                for (int j = 0; j < 4; j++)
                {
                    int lbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1 + 4 * j);
                    RedirectChainHeadsRecursive(engine, lbl, redirect, visited);
                }
                break;
            case Shumway.Core.Opcode.SwitchOnArg:
                // arg_idx at +1; 4 address operands at +5, +9, +13, +17.
                for (int j = 0; j < 4; j++) PatchOperand(addr + 5 + 4 * j);
                for (int j = 0; j < 4; j++)
                {
                    int lbl = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5 + 4 * j);
                    RedirectChainHeadsRecursive(engine, lbl, redirect, visited);
                }
                break;
            case Shumway.Core.Opcode.SwitchOnAtom:
            case Shumway.Core.Opcode.SwitchOnInteger:
            case Shumway.Core.Opcode.SwitchOnStructure:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 1);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                var newTable = RedirectSwitchTable(table, redirect);
                var live = newTable ?? table;
                if (newTable is not null)
                {
                    engine.ReplaceSwitchTable(tid, newTable);
                    MirrorSwitchTableIntoDynamicLink(tid, newTable);
                }
                foreach (int v in live.Values)
                    RedirectChainHeadsRecursive(engine, v, redirect, visited);
                RedirectChainHeadsRecursive(engine, live.DefaultAddress, redirect, visited);
                break;
            }
            case Shumway.Core.Opcode.SwitchOnAtomArg:
            case Shumway.Core.Opcode.SwitchOnIntegerArg:
            case Shumway.Core.Opcode.SwitchOnStructureArg:
            {
                int tid = Shumway.Core.BytecodeIO.ReadInt32(prog, addr + 5);
                var table = engine.GetSwitchTable(tid);
                if (table is null) break;
                var newTable = RedirectSwitchTable(table, redirect);
                var live = newTable ?? table;
                if (newTable is not null)
                {
                    engine.ReplaceSwitchTable(tid, newTable);
                    MirrorSwitchTableIntoDynamicLink(tid, newTable);
                }
                foreach (int v in live.Values)
                    RedirectChainHeadsRecursive(engine, v, redirect, visited);
                RedirectChainHeadsRecursive(engine, live.DefaultAddress, redirect, visited);
                break;
            }
            // Other opcodes (chain heads, fail_stub, anything else):
            // stop. Chain heads aren't switch nodes — they don't have
            // outgoing pointers that need redirecting beyond their
            // <next>, which is internal to the chain.
        }
    }

    /// <summary>returns a new
    /// <see cref="Shumway.Core.SwitchTable"/> with every value
    /// (including the default address) replaced if present in
    /// <paramref name="redirect"/>. Returns <c>null</c> when no
    /// entry was redirected — caller skips the replacement.</summary>
    private static Shumway.Core.SwitchTable? RedirectSwitchTable(
        Shumway.Core.SwitchTable old, IReadOnlyDictionary<int, int> redirect)
    {
        bool changed = false;
        int[] keys = old.Keys.ToArray();
        int[] values = old.Values.ToArray();
        for (int i = 0; i < values.Length; i++)
        {
            if (redirect.TryGetValue(values[i], out int repl))
            {
                values[i] = repl;
                changed = true;
            }
        }
        int newDefault = old.DefaultAddress;
        if (redirect.TryGetValue(newDefault, out int defRepl))
        {
            newDefault = defRepl;
            changed = true;
        }
        return changed ? new Shumway.Core.SwitchTable(keys, values, newDefault) : null;
    }

    // ====================================================================
    // in-place retract for extensible-indexed dynamic
    // predicates.
    // ====================================================================

    /// <summary>returns the body address of the
    /// <paramref name="clauseIndex"/>'th still-alive clause in the
    /// var-fallthrough chain, where "alive" is defined as
    /// <c>died == long.MaxValue</c> in the entry's
    /// <c>check_visible</c>. Previously-retracted entries (died set
    /// to some generation by a prior retract) are
    /// skipped, so the index aligns with the post-removal
    /// <c>_dynamicClauses</c> ordering — except that this lookup
    /// runs BEFORE the current <c>RemoveAt</c>, so
    /// <paramref name="clauseIndex"/> is the position in the
    /// pre-removal list. Returns <c>-1</c> on layout mismatch or
    /// when the index runs off the chain.</summary>
    private int FindBodyAddrForClauseIndex(Activation engine, int functorId, int clauseIndex)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return -1;
        var addrMap = engine.CurrentFunctorAddresses;
        if (addrMap is null || !addrMap.TryGetValue(functorId, out int predAddr)) return -1;
        var prog = engine.CurrentProgram!;
        int failStub = engine.DynamicFailStubAddr;
        int varChainHead = FindFinalVarChainHead(engine, predAddr);
        if (varChainHead < 0) return -1;
        int cur = varChainHead;
        int aliveIdx = 0;
        while (true)
        {
            if (cur < 0 || cur + 27 > prog.Length) return -1;
            int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
            int diedAddr = cur + chainHeaderSize + 9;
            int execOpPos = cur + chainHeaderSize + 17;
            if (execOpPos + 5 > prog.Length) return -1;
            if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) return -1;
            long died = Shumway.Core.BytecodeIO.ReadInt64(prog, diedAddr);
            if (died == long.MaxValue)
            {
                if (aliveIdx == clauseIndex)
                    return Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1);
                aliveIdx++;
            }
            int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
            if (next == failStub) return -1;
            cur = next;
        }
    }

    /// <summary>Walks every chain in the extensible-
    /// indexed predicate (each bucket chain reached through a switch
    /// table value, the list chain head, and the var-fallthrough
    /// chain), and patches the died slot of every entry whose
    /// <c>execute</c> targets <paramref name="bodyAddr"/>. The died
    /// slot is the second 8-byte field of the entry's
    /// <c>check_visible</c>, located at chain-header-size + 1 + 8
    /// from the entry's start. Returns <c>true</c> when at least one
    /// entry was patched (the chain actually held a reference to the
    /// body); <c>false</c> when the predicate isn't using the chunk-
    /// 155a layout or no chain referenced the retired body.</summary>
    private bool TryPatchDiedInAllIndexedChains(Activation engine, int functorId, int bodyAddr)
    {
        if (!IsExtensibleIndexedLayout(engine, functorId)) return false;
        var addrMap = engine.CurrentFunctorAddresses!;
        var prog = engine.CurrentProgram!;
        int predAddr = addrMap[functorId];
        int failStub = engine.DynamicFailStubAddr;

        // enumerate every chain head reachable from the
        // top-level switch, including multi-level cascades through
        // switch_on_arg.
        var heads = new HashSet<int>();
        var visited = new HashSet<int>();
        EnumerateChainHeadsRecursive(engine, predAddr + 1, heads, visited);

        bool anyPatched = false;
        foreach (int head in heads)
        {
            int cur = head;
            while (cur > 0 && cur + 27 <= prog.Length)
            {
                int chainHeaderSize = ChainEntryHeaderSize(prog, cur);
                int execOpPos = cur + chainHeaderSize + 17;
                if (execOpPos + 5 > prog.Length) break;
                if (prog[execOpPos] != (byte)Shumway.Core.Opcode.Execute) break;
                int target = Shumway.Core.BytecodeIO.ReadInt32(prog, execOpPos + 1);
                if (target == bodyAddr)
                {
                    int diedAddr = cur + chainHeaderSize + 9;
                    Shumway.Core.BytecodeIO.WriteInt64(prog, diedAddr, _dbGeneration.Value);
                    anyPatched = true;
                }
                int next = Shumway.Core.BytecodeIO.ReadInt32(prog, cur + 1);
                if (next == failStub) break;
                cur = next;
            }
        }
        return anyPatched;
    }

    /// <summary>writes <paramref name="newTable"/> into
    /// the dynamic region's slot of the cached <see cref="_dynamicLink"/>
    /// so the next query's <see cref="SetupQueryFromTerm"/> (which
    /// rebuilds the merged engine.SwitchTables list from
    /// staticLink + _dynamicLink + queryLink) carries the
    /// mutation forward. The merged-table id at runtime is
    /// <c>staticLink.SwitchTables.Count + dynamicLocalId</c>; we
    /// undo the offset to find the right slot in _dynamicLink.
    /// </summary>
    private void MirrorSwitchTableIntoDynamicLink(int mergedTableId, Shumway.Core.SwitchTable newTable)
    {
        if (_dynamicLink is null) return;
        int staticCount = _staticLink?.SwitchTables.Count ?? 0;
        int dynLocalId = mergedTableId - staticCount;
        if (dynLocalId < 0 || dynLocalId >= _dynamicLink.SwitchTables.Count) return;
        if (_dynamicLink.SwitchTables is List<Shumway.Core.SwitchTable> dynList)
            dynList[dynLocalId] = newTable;
        // If the link's IReadOnlyList isn't actually a List (some
        // alternative implementation), the mutation can't be made
        // persistent — degrades gracefully: the current
        // query holds the update via engine.SwitchTables, the next
        // query will rebuild from the unmutated link and miss it.
        // That just regresses to the rebuild fallback for
        // the affected predicate, which is correct, only slower.
    }

    /// <summary>emits a fresh bucket chain containing
    /// every var-arg clause's body (in source order) followed by
    /// the new clause's body, appends it to the buffer, and returns
    /// the chain head address. The chain head uses
    /// <c>try_me_else</c> (9 bytes); subsequent entries use
    /// <c>retry_me_else</c> (5 bytes); the last entry's
    /// <c>&lt;next&gt;</c> is the fail stub.</summary>
    private int BuildAndAppendNewBucketChain(
        Activation engine, int failStub, int headArity,
        IReadOnlyList<int> varArgBodies, int newBodyAddr)
    {
        // Chain bodies in source order: var-arg first, then new.
        var bodies = new List<int>(varArgBodies);
        bodies.Add(newBodyAddr);
        int count = bodies.Count;

        // Plan the chunk: layout & size up front so we can compute
        // each entry's address (each entry's <next> points at the
        // following entry's absolute address).
        const int HeadEntrySize = 9 + 17 + 5;
        const int NonHeadEntrySize = 5 + 17 + 5;

        // We don't know the chain's start address yet — depends on
        // engine.ProgramLength. Probe AppendCode by appending a
        // single empty buffer; instead, just compute offsets relative
        // to ProgramLength now and then emit in one go.
        int startAddr = engine.ProgramLength;
        // Build the entire chain in one BytecodeEmitter so the
        // offsets are right by construction, then AppendCode the
        // result. Each entry's <next> is the absolute address of
        // the next entry, or fail_stub for the last.
        var em = new Shumway.Compiler.Wam.BytecodeEmitter();
        for (int i = 0; i < count; i++)
        {
            int thisSize = i == 0 ? HeadEntrySize : NonHeadEntrySize;
            int nextAddr = (i == count - 1) ? failStub
                                            : startAddr + em.Position + thisSize;
            if (i == 0) em.EmitTryMeElse(nextAddr, headArity);
            else        em.EmitRetryMeElse(nextAddr);
            em.EmitCheckVisible(born: _dbGeneration.Value, died: long.MaxValue);
            em.EmitExecute(bodies[i]);
        }
        int chunkAddr = engine.AppendCode(em.ToBytes());
        // chunkAddr should equal startAddr (we computed offsets to
        // match). Verify in debug builds.
        System.Diagnostics.Debug.Assert(chunkAddr == startAddr,
            $"in-place assertz: new bucket chain address mismatch (expected {startAddr}, got {chunkAddr}).");
        return chunkAddr;
    }

    /// <summary>Pulls the first free chunk whose
    /// length is at least <paramref name="needed"/> off the chain
    /// table's free-list and returns its address; the chunk's tail
    /// (beyond <paramref name="needed"/> bytes) goes back on the list.
    /// Returns -1 when no fit is available, meaning the caller should
    /// fall back to <c>engine.AppendCode</c>. The free-list lives on the
    /// per-buffer <see cref="DynChainTable"/> (its addresses
    /// are buffer-relative), so chunks freed in one query are reusable
    /// by the next while that buffer is reused.</summary>
    private static int TryReuseFreeChunk(
        List<(int Addr, int Length)> freeChunks, int needed)
    {
        for (int i = 0; i < freeChunks.Count; i++)
        {
            var (addr, length) = freeChunks[i];
            if (length < needed) continue;
            freeChunks.RemoveAt(i);
            int leftover = length - needed;
            if (leftover > 0)
                freeChunks.Add((addr + needed, leftover));
            return addr;
        }
        return -1;
    }

    /// <summary>Test hook: returns the absolute byte offset of clause
    /// <paramref name="clauseIndex"/>'s died slot in the running program,
    /// or <c>null</c> when no chain state exists. Used by tests
    /// to verify <c>retract</c> patches the slot.</summary>
    internal int? PeekDiedAddr(int functorId, int clauseIndex)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return null;
        return chain.Entries[clauseIndex].DiedOperandAddr;
    }

    /// <summary>Test hook: returns the absolute byte offset of clause
    /// <paramref name="clauseIndex"/>'s chain-instruction <c>&lt;next&gt;</c>
    /// operand in the running program, or <c>null</c> when no chain state
    /// exists. -1 when the clause was emitted without a chain instruction
    /// in front of it.</summary>
    internal int? PeekNextAddr(int functorId, int clauseIndex)
    {
        if (!_dynChainTable.Chains.TryGetValue(functorId, out var chain)) return null;
        if (clauseIndex < 0 || clauseIndex >= chain.Entries.Count) return null;
        return chain.Entries[clauseIndex].NextOperandAddr;
    }

}

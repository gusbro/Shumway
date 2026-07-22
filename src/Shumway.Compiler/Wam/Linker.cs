using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Links a <see cref="CompiledModule"/> (and optionally additional predicates)
/// into a runnable byte buffer. Each predicate's bytecode is concatenated, and
/// every <see cref="CallSite"/>'s target operand is patched to point at the
/// callee predicate's start address inside the program.
///
/// <para>The linker has no knowledge of "entry points" or the launcher
/// wrapping that turns a program into something the interpreter can run
/// top-down. Callers typically prepend such a launcher manually — set
/// argument registers, emit one <c>call</c> to the desired entry predicate,
/// emit <c>halt</c>, then append the linked module bytes — and translate
/// the call's target by the prefix length.</para>
/// </summary>
public sealed class Linker
{
    /// <summary>Outcome of <see cref="Link"/>: the concatenated bytecode, a
    /// map from functor id to that predicate's address inside the bytecode,
    /// and the module-level switch table list with all addresses already
    /// shifted into the program-absolute address space.
    ///
    /// <para><see cref="UnresolvedSites"/> (ADR-015) lists every call site
    /// whose callee was not in this predicate set nor in the supplied
    /// external symbols — it received the undefined-predicate sentinel.
    /// A caller that links a stable region before the predicates it calls
    /// (a cached static region calling later-laid-out dynamic predicates)
    /// uses this to re-patch those sites once the callee addresses are
    /// known. <see cref="(int, int).Item1">Offset</see> is the call
    /// opcode's position in <see cref="Bytecode"/>; the operand is at
    /// <c>Offset + 1</c>.</para></summary>
    public sealed record LinkResult(
        byte[] Bytecode,
        IReadOnlyDictionary<int, int> Addresses,
        IReadOnlyList<SwitchTable> SwitchTables,
        IReadOnlyDictionary<int, CompiledPredicate> PredicatesByAddress,
        IReadOnlyList<(int Offset, int FunctorId)> UnresolvedSites);

    public LinkResult Link(
        CompiledModule module, int loadOffset = 0,
        IReadOnlyDictionary<int, int>? externalSymbols = null,
        int switchTableIdBase = 0)
    {
        ArgumentNullException.ThrowIfNull(module);
        return Link(module.Predicates, loadOffset, externalSymbols, switchTableIdBase);
    }

    /// <summary>Concatenates the predicates' bytecode and patches every internal
    /// reference (call sites and choice-point dispatch BPs) so the result is
    /// runnable starting at byte <paramref name="loadOffset"/>. Pass a non-zero
    /// <paramref name="loadOffset"/> when the linked program will be appended
    /// to a prefix (e.g. a launcher); every address is shifted by that much.
    ///
    /// <para><paramref name="externalSymbols"/> (ADR-015) maps functor ids to
    /// already-linked absolute addresses outside this predicate set. A call
    /// whose callee is not among <paramref name="predicates"/> is resolved
    /// against it before falling back to the undefined-predicate sentinel —
    /// so a transient query chunk can be linked against a persistent code
    /// region that was linked earlier.</para></summary>
    public LinkResult Link(
        IReadOnlyList<CompiledPredicate> predicates, int loadOffset = 0,
        IReadOnlyDictionary<int, int>? externalSymbols = null,
        int switchTableIdBase = 0)
    {
        ArgumentNullException.ThrowIfNull(predicates);

        var bytes = new List<byte>();
        var addresses = new Dictionary<int, int>();
        var predicatesByAddress = new Dictionary<int, CompiledPredicate>();
        var unresolvedCalls = new List<(int Offset, int FunctorId)>();
        var switchTables = new List<SwitchTable>();

        foreach (var p in predicates)
        {
            if (addresses.ContainsKey(p.FunctorId))
                throw new InvalidOperationException(
                    "Duplicate predicate definition: functor id "
                    + $"{p.FunctorId} (name '{NameForFunctor(p.FunctorId)}'/{p.Arity}) "
                    + "appears in two predicates of the same module.");

            int basePos = bytes.Count;
            int absAddr = basePos + loadOffset;
            addresses[p.FunctorId] = absAddr;
            predicatesByAddress[absAddr] = p;
            bytes.AddRange(p.Bytecode);

            foreach (var site in p.CallSites)
                unresolvedCalls.Add((basePos + site.OpcodeOffset, site.CalleeFunctorId));
        }

        byte[] program = bytes.ToArray();

        // Patch call site target operands with the callee's absolute
        // address. A call to an undefined predicate is not a link error:
        // it is patched with a CallTarget sentinel so the interpreter
        // raises existence_error if (and only if) the call is reached.
        //
        // if the callee resolves to a builtin (e.g.
        // a foreign predicate the linker discovered through
        // --foreign-dll, registered into BuiltinsRegistry before
        // calling Link), rewrite the Call opcode at `off` to
        // CallBuiltin in place. Same 9-byte footprint (opcode +
        // int32 + int32), so the operand-slot positions don't move.
        // The reverse mapping doesn't apply: the compiler already
        // emits CallBuiltin directly for any predicate that was a
        // builtin at compile time, and the runtime never demotes
        // a CallBuiltin back to Call.
        var unresolvedSites = new List<(int Offset, int FunctorId)>();
        foreach (var (off, fid) in unresolvedCalls)
        {
            // Builtin? Rewrite the opcode and use the builtin id as the operand.
            // Same-size in-place swap in both cases:
            //   Call (9b) → CallBuiltin (9b)
            //   Execute (5b) → ExecuteBuiltin (5b)
            if (Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out int builtinId))
            {
                byte existing = program[off];
                if (existing == (byte)Opcode.Call)
                {
                    program[off] = (byte)Opcode.CallBuiltin;
                    BytecodeIO.WriteInt32(program, off + 1, builtinId);
                    continue;
                }
                if (existing == (byte)Opcode.DebugLastCall)
                {
                    // ADR-035 — the reason debug_lastcall is Call-shaped: a
                    // last goal that turns out to be a builtin rewrites in
                    // place to CallBuiltin (both 9 bytes). The return stub
                    // behind it (deallocate_proceed) is exactly the epilogue a
                    // CallBuiltin in last position wants, so the site keeps
                    // working — it just loses the ability to tail-call, which
                    // for a builtin costs nothing a debugger cares about.
                    program[off] = (byte)Opcode.CallBuiltin;
                    BytecodeIO.WriteInt32(program, off + 1, builtinId);
                    // -1 is CallBuiltin's no-trim sentinel — what the compiler
                    // itself emits for a builtin in last position. Trimming here
                    // would discard the very Y slots the frame was kept for.
                    BytecodeIO.WriteInt32(program, off + 5, -1);
                    continue;
                }
                if (existing == (byte)Opcode.Execute)
                {
                    // tail-call rewrite. ExecuteBuiltin
                    // has the same 5-byte width as Execute, so the
                    // swap is opcode-byte + operand-patch with no
                    // following Nops needed. Drops Execute's address
                    // operand semantics for the builtin id.
                    program[off] = (byte)Opcode.ExecuteBuiltin;
                    BytecodeIO.WriteInt32(program, off + 1, builtinId);
                    continue;
                }
                throw new InvalidOperationException(
                    $"Linker: unexpected opcode 0x{existing:X2} at call site for functor id {fid}.");
            }
            int target;
            if (addresses.TryGetValue(fid, out int addr))
                target = addr;
            else if (externalSymbols is not null
                     && externalSymbols.TryGetValue(fid, out int extAddr))
                target = extAddr;
            else
            {
                target = CallTarget.ForUndefined(fid);
                unresolvedSites.Add((off, fid));
            }
            BytecodeIO.WriteInt32(program, off + 1, target);
        }

        // Shift dispatch BPs and switch-table-id operands from predicate-local
        // to program-absolute. Re-iterate predicates to recover each one's
        // basePos and switch-table base.
        int basePosTracker = 0;
        int switchTableBaseTracker = switchTableIdBase;
        foreach (var p in predicates)
        {
            foreach (int dispatchSite in p.DispatchSites)
            {
                int localBp = BytecodeIO.ReadInt32(program, basePosTracker + dispatchSite);
                BytecodeIO.WriteInt32(program, basePosTracker + dispatchSite,
                    basePosTracker + loadOffset + localBp);
            }
            foreach (int idSite in p.SwitchTableIdSites)
            {
                int localId = BytecodeIO.ReadInt32(program, basePosTracker + idSite);
                BytecodeIO.WriteInt32(program, basePosTracker + idSite,
                    switchTableBaseTracker + localId);
            }
            foreach (var table in p.SwitchTables)
                switchTables.Add(table.WithShiftedAddresses(basePosTracker + loadOffset));

            basePosTracker += p.Bytecode.Length;
            switchTableBaseTracker += p.SwitchTables.Count;
        }

        // predicatesByAddress carries the *un-patched* per-predicate
        // bytecode views. The Tier-1 IL compiler reads operand values
        // straight from these — call targets stay at their placeholder
        // zeros (the IL resolves call sites via callee functor id, not
        // the embedded address) and switch-table ids stay at predicate-
        // local indices (so `predicate.SwitchTables[tableId]` doesn't
        // overflow). The Tier-0 interpreter dispatches on the patched
        // `program` byte array, so it gets resolved addresses without
        // help.
        return new LinkResult(
            program, addresses, switchTables, predicatesByAddress, unresolvedSites);
    }

    private static string NameForFunctor(int functorId)
    {
        if (!FunctorTable.TryLookup(functorId, out var entry)) return "?";
        var atom = AtomTable.GetById(entry.AtomId);
        return atom?.Name ?? "?";
    }
}

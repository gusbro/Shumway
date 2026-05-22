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
    /// shifted into the program-absolute address space.</summary>
    public sealed record LinkResult(
        byte[] Bytecode,
        IReadOnlyDictionary<int, int> Addresses,
        IReadOnlyList<SwitchTable> SwitchTables,
        IReadOnlyDictionary<int, CompiledPredicate> PredicatesByAddress);

    public LinkResult Link(
        CompiledModule module, int loadOffset = 0,
        IReadOnlyDictionary<int, int>? externalSymbols = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        return Link(module.Predicates, loadOffset, externalSymbols);
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
        IReadOnlyDictionary<int, int>? externalSymbols = null)
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
        foreach (var (off, fid) in unresolvedCalls)
        {
            int target;
            if (addresses.TryGetValue(fid, out int addr))
                target = addr;
            else if (externalSymbols is not null
                     && externalSymbols.TryGetValue(fid, out int extAddr))
                target = extAddr;
            else
                target = CallTarget.ForUndefined(fid);
            BytecodeIO.WriteInt32(program, off + 1, target);
        }

        // Shift dispatch BPs and switch-table-id operands from predicate-local
        // to program-absolute. Re-iterate predicates to recover each one's
        // basePos and switch-table base.
        int basePosTracker = 0;
        int switchTableBaseTracker = 0;
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
        return new LinkResult(program, addresses, switchTables, predicatesByAddress);
    }

    private static string NameForFunctor(int functorId)
    {
        if (!FunctorTable.TryLookup(functorId, out var entry)) return "?";
        var atom = AtomTable.GetById(entry.AtomId);
        return atom?.Name ?? "?";
    }
}

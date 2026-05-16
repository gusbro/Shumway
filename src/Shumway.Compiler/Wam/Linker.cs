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
    /// <summary>Outcome of <see cref="Link"/>: the concatenated bytecode and a
    /// map from functor id to that predicate's address inside the bytecode.
    /// Callers needing to wrap the program with a launcher use the address map
    /// to seed the launcher's <c>call</c> target.</summary>
    public sealed record LinkResult(byte[] Bytecode, IReadOnlyDictionary<int, int> Addresses);

    public LinkResult Link(CompiledModule module, int loadOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(module);
        return Link(module.Predicates, loadOffset);
    }

    /// <summary>Concatenates the predicates' bytecode and patches every internal
    /// reference (call sites and choice-point dispatch BPs) so the result is
    /// runnable starting at byte <paramref name="loadOffset"/>. Pass a non-zero
    /// <paramref name="loadOffset"/> when the linked program will be appended
    /// to a prefix (e.g. a launcher); every address is shifted by that much.</summary>
    public LinkResult Link(IReadOnlyList<CompiledPredicate> predicates, int loadOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(predicates);

        var bytes = new List<byte>();
        var addresses = new Dictionary<int, int>();
        var unresolvedCalls = new List<(int Offset, int FunctorId)>();
        var unresolvedDispatch = new List<int>();   // byte offsets, value at each is predicate-local BP

        foreach (var p in predicates)
        {
            if (addresses.ContainsKey(p.FunctorId))
                throw new InvalidOperationException(
                    "Duplicate predicate definition: functor id "
                    + $"{p.FunctorId} (name '{NameForFunctor(p.FunctorId)}'/{p.Arity}) "
                    + "appears in two predicates of the same module.");

            int basePos = bytes.Count;
            addresses[p.FunctorId] = basePos + loadOffset;
            bytes.AddRange(p.Bytecode);

            foreach (var site in p.CallSites)
                unresolvedCalls.Add((basePos + site.OpcodeOffset, site.CalleeFunctorId));
            foreach (int dispatchSite in p.DispatchSites)
            {
                // Each dispatch site currently holds a predicate-local BP. Add
                // basePos + loadOffset to make it absolute (i.e. the same final
                // address scheme that call sites and the addresses map use).
                int siteAbs = basePos + dispatchSite;
                unresolvedDispatch.Add(siteAbs);
                // Stash the predicate's basePos via the current value at the
                // site: it's read-then-written in the patching loop below.
                // (We can't simply store basePos here because the byte buffer
                // already has predicate-local values written. So we read the
                // existing value, add basePos + loadOffset, write back.)
                // Done in the patching pass below — we just need to remember
                // siteAbs and the shift to apply.
            }
        }

        byte[] program = bytes.ToArray();

        // Patch call site target operands with the callee's absolute address.
        foreach (var (off, fid) in unresolvedCalls)
        {
            if (!addresses.TryGetValue(fid, out int target))
                throw new InvalidOperationException(
                    $"Unresolved call to functor id {fid} "
                    + $"(name '{NameForFunctor(fid)}'): the predicate has no clauses in "
                    + "this module.");
            BytecodeIO.WriteInt32(program, off + 1, target);
        }

        // Shift dispatch BPs from predicate-local to program-absolute. For each
        // dispatch site we need to know the predicate's basePos — recover it by
        // iterating predicates a second time in parallel with their dispatch
        // sites.
        int basePosTracker = 0;
        foreach (var p in predicates)
        {
            foreach (int dispatchSite in p.DispatchSites)
            {
                int localBp = BytecodeIO.ReadInt32(program, basePosTracker + dispatchSite);
                BytecodeIO.WriteInt32(program, basePosTracker + dispatchSite,
                    basePosTracker + loadOffset + localBp);
            }
            basePosTracker += p.Bytecode.Length;
        }

        return new LinkResult(program, addresses);
    }

    private static string NameForFunctor(int functorId)
    {
        if (!FunctorTable.TryLookup(functorId, out var entry)) return "?";
        var atom = AtomTable.GetById(entry.AtomId);
        return atom?.Name ?? "?";
    }
}

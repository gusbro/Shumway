namespace Shumway.Core;

/// <summary>The module-qualified name of a predicate: <c>module$name/arity</c>.
///
/// <para>ONE copy, deliberately. Both meta-call dispatchers -- the bytecode
/// interpreter's and Tier-1 IL's -- resolve a module-tagged goal by mangling
/// its functor, and each held its own identical copy of this. Two copies of a
/// naming CONVENTION is not duplication of convenience: if one ever drifted,
/// the same goal would resolve to different predicates on different tiers,
/// and the symptom would be a program that answers correctly until it gets
/// hot.</para></summary>
public static class ModuleQualify
{
    /// <summary>Memoised because the computation builds a STRING and interns
    /// it: two table reads, a concatenation, a hash over the result, and a
    /// functor intern -- on a path measured at 2,728 calls in one clpfd solve
    /// of queens 6, none of which the engine's meta-route cache covers (it
    /// takes only UNTAGGED goals, and 97% of the meta-calls there are
    /// tagged). Measured end to end, the memo takes 7-9% off what a solve
    /// allocates.
    ///
    /// <para>It needs no invalidation and carries no lifetime argument: atom
    /// ids are permanent and interning is deterministic, so the same triple
    /// yields the same functor for the life of the process. That is what
    /// separates this from caching a RESOLUTION, which depends on the address
    /// map and has to be stamped with it.</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (int Module, int Name, int Arity), int> Memo = new();

    public static int Mangle(int moduleAtomId, int nameAtomId, int arity)
        => Memo.GetOrAdd((moduleAtomId, nameAtomId, arity), static k =>
        {
            string module = AtomTable.GetById(k.Module)?.Name ?? "";
            string name = AtomTable.GetById(k.Name)?.Name ?? "";
            int mangledAtom = AtomTable.Intern(module + "$" + name, permanent: true).Id;
            return FunctorTable.Intern(mangledAtom, k.Arity);
        });
}

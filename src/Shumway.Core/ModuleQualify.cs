namespace Shumway.Core;

/// <summary>The module-qualified name of a predicate: <c>module$name/arity</c>.
///
/// <para>One copy, deliberately. Both meta-call dispatchers -- the bytecode
/// interpreter's and Tier-1 IL's -- resolve a module-tagged goal by mangling
/// its functor, and each held its own identical copy of this. Two copies of a
/// naming convention is not duplication of convenience: if one ever drifted,
/// the same goal would resolve to different predicates on different tiers,
/// and the symptom would be a program that answers correctly until it gets
/// hot.</para></summary>
public static class ModuleQualify
{
    /// <summary>Memoised because the computation builds a string and interns
    /// it: two table reads, a concatenation, a hash over the result, and a
    /// functor intern -- on a path measured at 2,728 calls in one clpfd solve
    /// of queens 6, none of which the engine's meta-route cache covers (it
    /// takes only untagged goals, and 97% of the meta-calls there are
    /// tagged). Measured end to end, the memo takes 7-9% off what a solve
    /// allocates.
    ///
    /// <para>An entry keeps the atoms it was made from: a dead atom's id goes
    /// to another name (ADR-003), so a hit counts only while both ids still
    /// name those atoms. It needs no other invalidation: interning is
    /// deterministic. That is what separates this from caching a resolution,
    /// which depends on the address map and has to be stamped with it.</para></summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (int Module, int Name, int Arity), (Atom? Module, Atom? Name, int Functor)> Memo = new();

    public static int Mangle(int moduleAtomId, int nameAtomId, int arity)
    {
        var key = (moduleAtomId, nameAtomId, arity);
        Atom? module = AtomTable.GetById(moduleAtomId), name = AtomTable.GetById(nameAtomId);
        if (Memo.TryGetValue(key, out var hit)
            && ReferenceEquals(hit.Module, module) && ReferenceEquals(hit.Name, name))
            return hit.Functor;
        int mangledAtom = AtomTable.Intern((module?.Name ?? "") + "$" + (name?.Name ?? ""), permanent: true).Id;
        int functor = FunctorTable.Intern(mangledAtom, arity);
        Memo[key] = (module, name, functor);
        return functor;
    }
}

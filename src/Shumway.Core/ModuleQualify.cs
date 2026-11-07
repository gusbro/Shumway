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
    /// <summary>The codes of a meta-argument spec beside 0 to 9 (a goal or
    /// closure taking that many arguments) and -1 (data): a <c>:</c>
    /// argument, a term the callee reads in the caller's module, which every
    /// caller qualifies; and the same on a builtin, which the global module
    /// and a legacy module leave bare, since bare already names the global
    /// store there (ADR-038, amendment).</summary>
    public const int ColonArg = -2;
    public const int ColonBuiltinArg = -3;

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

    /// <summary>The inverse of <see cref="Mangle"/> by shape alone: the module
    /// and the bare functor of a <c>module$name</c> functor, or false for a
    /// name with no separator. A name starting with <c>$</c> is an engine
    /// name, not a qualified one. Whether the prefix is a module is for the
    /// caller to check: a user predicate may be called <c>'a$b'</c>.</summary>
    public static bool TryDemangle(int functorId, out int moduleAtomId, out int bareFunctorId)
    {
        moduleAtomId = -1;
        bareFunctorId = functorId;
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        string? name = AtomTable.GetById(atomId)?.Name;
        if (name is null) return false;
        int dollar = name.IndexOf('$');
        if (dollar <= 0 || dollar == name.Length - 1) return false;
        moduleAtomId = AtomTable.Intern(name.Substring(0, dollar), permanent: true).Id;
        bareFunctorId = FunctorTable.Intern(
            AtomTable.Intern(name.Substring(dollar + 1), permanent: true).Id, arity);
        return true;
    }

    /// <summary>The functor a diagnostic names: a module's own predicate
    /// by its bare name, as the program wrote it.</summary>
    public static int DisplayFunctor(int functorId)
        => TryDemangle(functorId, out _, out int bare) ? bare : functorId;
}

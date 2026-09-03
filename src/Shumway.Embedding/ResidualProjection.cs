using System.Collections.Generic;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>Shared post-processing of residual-goal projections (the third argument of
/// <c>copy_term/3</c> / the prelude's <c>'$dbg_residuals'/2</c>): renaming the copy
/// variables back to the names the user knows, and bucketing each goal under the first
/// named variable it mentions. One implementation for the REPL's answer display and the
/// debugger's Constraints view — the two must agree on what a residual looks like.</summary>
public static class ResidualProjection
{
    /// <summary>Rebuilds <paramref name="term"/> with every variable whose name has an
    /// entry in <paramref name="renames"/> replaced by a variable of the mapped name.
    /// Untouched subterms are returned by reference.</summary>
    public static Term SubstituteVarNames(Term term, IReadOnlyDictionary<string, string> renames)
    {
        switch (term)
        {
            case VarTerm v when renames.TryGetValue(v.Name, out string? newName):
                return new VarTerm(newName);
            case CompoundTerm { Functor: ".", Args.Length: 2 }:
                // A list is walked along its spine rather than into its tail: a
                // long one would otherwise cost a C# frame per element, which is
                // a stack overflow where the stack is small (a browser).
                return SubstituteInList(term, renames);
            case CompoundTerm c:
                var newArgs = new Term[c.Args.Length];
                bool changed = false;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    newArgs[i] = SubstituteVarNames(c.Args[i], renames);
                    if (!ReferenceEquals(newArgs[i], c.Args[i])) changed = true;
                }
                return changed ? new CompoundTerm(c.Functor, newArgs) : term;
            default:
                return term;
        }
    }

    /// <summary>Replaces every cycle back-edge variable (TermReader's
    /// <c>IsCycleBack</c>) with a variable spelled <c>...</c> — the one-line
    /// error message's honest form for an infinite tail
    /// (<c>type_error(list, ['1'|...])</c>), where the answer display's
    /// named-equation idiom has no room. A VarTerm so the renderer emits the
    /// three dots bare, not as a quoted atom.</summary>
    public static Term ElideCycleMarkers(Term term)
    {
        switch (term)
        {
            case VarTerm { IsCycleBack: true }:
                return new VarTerm("...");
            case CompoundTerm { Functor: ".", Args.Length: 2 } cons:
            {
                var heads = new List<Term>();
                Term cursor = cons;
                bool changed = false;
                while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cc)
                {
                    Term head = ElideCycleMarkers(cc.Args[0]);
                    if (!ReferenceEquals(head, cc.Args[0])) changed = true;
                    heads.Add(head);
                    cursor = cc.Args[1];
                }
                Term tail = ElideCycleMarkers(cursor);
                if (!ReferenceEquals(tail, cursor)) changed = true;
                if (!changed) return term;
                for (int i = heads.Count - 1; i >= 0; i--)
                    tail = new CompoundTerm(".", new[] { heads[i], tail });
                return tail;
            }
            case CompoundTerm c:
            {
                var newArgs = new Term[c.Args.Length];
                bool changed = false;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    newArgs[i] = ElideCycleMarkers(c.Args[i]);
                    if (!ReferenceEquals(newArgs[i], c.Args[i])) changed = true;
                }
                return changed ? new CompoundTerm(c.Functor, newArgs) : term;
            }
            default:
                return term;
        }
    }

    /// <summary>Replaces every compound BELOW the root whose
    /// <c>CycleId</c> (TermReader's cycle-owner stamp) has a name in
    /// <paramref name="names"/> with a variable of that name. The root is
    /// left in place so a value whose own root is the owner still shows its
    /// structure (<c>L = ['1'|L]</c>), while occurrences inside other values
    /// chain by name (<c>E = type_error(list, _S1)</c>). Run BEFORE
    /// <see cref="SubstituteVarNames"/>: its rebuilds drop the stamp.</summary>
    public static Term SubstituteCycleOwnersBelowRoot(
        Term term, IReadOnlyDictionary<string, string> names)
    {
        if (term is not CompoundTerm root) return term;
        if (root is { Functor: ".", Args.Length: 2 })
            return SubstituteOwnersInList(root, names, skipRootOwner: true);
        var newArgs = new Term[root.Args.Length];
        bool changed = false;
        for (int i = 0; i < root.Args.Length; i++)
        {
            newArgs[i] = SubstituteOwners(root.Args[i], names);
            if (!ReferenceEquals(newArgs[i], root.Args[i])) changed = true;
        }
        return changed ? new CompoundTerm(root.Functor, newArgs) : term;
    }

    private static Term SubstituteOwners(Term term, IReadOnlyDictionary<string, string> names)
    {
        switch (term)
        {
            case CompoundTerm c when c.CycleId is { } cid
                                     && names.TryGetValue(cid, out string? name):
                return new VarTerm(name);
            case CompoundTerm { Functor: ".", Args.Length: 2 } cons:
                return SubstituteOwnersInList(cons, names, skipRootOwner: false);
            case CompoundTerm c:
            {
                var newArgs = new Term[c.Args.Length];
                bool changed = false;
                for (int i = 0; i < c.Args.Length; i++)
                {
                    newArgs[i] = SubstituteOwners(c.Args[i], names);
                    if (!ReferenceEquals(newArgs[i], c.Args[i])) changed = true;
                }
                return changed ? new CompoundTerm(c.Functor, newArgs) : term;
            }
            default:
                return term;
        }
    }

    private static Term SubstituteOwnersInList(
        CompoundTerm list, IReadOnlyDictionary<string, string> names, bool skipRootOwner)
    {
        var heads = new List<Term>();
        Term cursor = list;
        bool changed = false, first = true;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            // An interior cons that is itself a cycle owner replaces the
            // whole remaining spine with its name.
            if (!(first && skipRootOwner) && cons.CycleId is { } cid
                && names.TryGetValue(cid, out string? name))
            {
                changed = true;
                cursor = new VarTerm(name);
                goto rebuild;
            }
            first = false;
            Term head = SubstituteOwners(cons.Args[0], names);
            if (!ReferenceEquals(head, cons.Args[0])) changed = true;
            heads.Add(head);
            cursor = cons.Args[1];
        }
        {
            Term tail = SubstituteOwners(cursor, names);
            if (!ReferenceEquals(tail, cursor)) changed = true;
            cursor = tail;
        }
    rebuild:
        if (!changed) return list;
        Term rebuilt = cursor;
        for (int i = heads.Count - 1; i >= 0; i--)
            rebuilt = new CompoundTerm(".", new[] { heads[i], rebuilt });
        return rebuilt;
    }

    private static Term SubstituteInList(Term list, IReadOnlyDictionary<string, string> renames)
    {
        var heads = new List<Term>();
        var originals = new List<Term>();
        Term cursor = list;
        bool changed = false;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            Term head = SubstituteVarNames(cons.Args[0], renames);
            if (!ReferenceEquals(head, cons.Args[0])) changed = true;
            heads.Add(head);
            originals.Add(cons);
            cursor = cons.Args[1];
        }
        Term tail = SubstituteVarNames(cursor, renames);
        if (!ReferenceEquals(tail, cursor)) changed = true;
        if (!changed) return list;

        for (int i = heads.Count - 1; i >= 0; i--)
            tail = new CompoundTerm(".", new[] { heads[i], tail });
        return tail;
    }

    /// <summary>The first name from <paramref name="owners"/> that occurs as a variable
    /// in <paramref name="term"/>, or null — the owner-variable rule both displays use:
    /// a goal is shown once, under the first of its variables the user can see.</summary>
    public static string? FindMentionedOwner(Term term, IReadOnlyList<string> owners)
    {
        // Iterative, for the same reason SubstituteVarNames is: a goal may
        // mention a list of any length, and one C# frame per element is a stack
        // overflow waiting for a big enough answer.
        var pending = new Stack<Term>();
        pending.Push(term);
        while (pending.Count > 0)
        {
            switch (pending.Pop())
            {
                case VarTerm v when owners.Contains(v.Name):
                    return v.Name;
                case CompoundTerm c:
                    // Pushed in reverse so the walk still finds the FIRST
                    // mentioned owner in argument order.
                    for (int i = c.Args.Length - 1; i >= 0; i--) pending.Push(c.Args[i]);
                    break;
            }
        }
        return null;
    }

    /// <summary>Maps the copy's variable names onto the names the answer displays,
    /// by walking a copied value and the original it was copied from in step.
    ///
    /// <para>The residual goals a constraint library projects are expressed over the
    /// COPY, so without this they mention variables that appear nowhere in the answer:
    /// <c>Qs = [_G6, _G8], _G43 in 1..10</c> reads as three unrelated things. The
    /// root's own name comes from <paramref name="rootName"/> — that is the name the
    /// user typed — and every variable below it takes the original's name, which is
    /// what the binding line prints for it.</para></summary>
    public static void MapCopyNames(
        Term copy, Term? original, string rootName, Dictionary<string, string> map)
    {
        // Explicit stack: a value can be a long list, and the walk must not be
        // bounded by the C# stack.
        var work = new Stack<(Term Copy, Term? Original, bool AtRoot)>();
        work.Push((copy, original, true));
        while (work.Count > 0)
        {
            var (c, o, atRoot) = work.Pop();
            if (c is VarTerm cv)
            {
                // First mapping wins: the caller maps roots before nested
                // occurrences, so a variable the user named keeps its name.
                if (atRoot) map.TryAdd(cv.Name, rootName);
                else if (o is VarTerm ov) map.TryAdd(cv.Name, ov.Name);
                continue;
            }
            if (c is CompoundTerm cc && o is CompoundTerm oc
                && cc.Functor == oc.Functor && cc.Args.Length == oc.Args.Length)
                for (int i = 0; i < cc.Args.Length; i++)
                    work.Push((cc.Args[i], oc.Args[i], false));
        }
    }

    /// <summary>Walks a Prolog list term, yielding its elements. A non-list or partial
    /// tail ends the walk (best-effort — the projection built the list, a malformed one
    /// only loses its tail).</summary>
    public static IEnumerable<Term> ListElements(Term? list)
    {
        Term cursor = list ?? new AtomTerm("[]");
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } c)
        {
            yield return c.Args[0];
            cursor = c.Args[1];
        }
    }

    /// <summary>Buckets residual goals by owner variable. The goals are already renamed
    /// to the owner naming (see <see cref="SubstituteVarNames"/>); a goal mentioning no
    /// owner lands in <paramref name="unattached"/>.</summary>
    public static Dictionary<string, List<Term>> BucketByOwner(
        IEnumerable<Term> goals, IReadOnlyList<string> owners, List<Term> unattached)
    {
        var byOwner = new Dictionary<string, List<Term>>();
        foreach (Term g in goals)
        {
            string? owner = FindMentionedOwner(g, owners);
            if (owner is null) { unattached.Add(g); continue; }
            if (!byOwner.TryGetValue(owner, out var list))
                byOwner[owner] = list = new List<Term>();
            list.Add(g);
        }
        return byOwner;
    }
}

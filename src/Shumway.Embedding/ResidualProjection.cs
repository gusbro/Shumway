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

    /// <summary>The first name from <paramref name="owners"/> that occurs as a variable
    /// in <paramref name="term"/>, or null — the owner-variable rule both displays use:
    /// a goal is shown once, under the first of its variables the user can see.</summary>
    public static string? FindMentionedOwner(Term term, IReadOnlyList<string> owners)
    {
        switch (term)
        {
            case VarTerm v:
                return owners.Contains(v.Name) ? v.Name : null;
            case CompoundTerm c:
                foreach (Term a in c.Args)
                {
                    string? r = FindMentionedOwner(a, owners);
                    if (r is not null) return r;
                }
                return null;
            default:
                return null;
        }
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

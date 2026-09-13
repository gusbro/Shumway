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
        => Rebuild(term, node => node is VarTerm v
                                 && renames.TryGetValue(v.Name, out string? renamed)
            ? new VarTerm(renamed) : null);

    /// <summary>Replaces every cycle back-edge variable (TermReader's
    /// <c>IsCycleBack</c>) with a variable spelled <c>...</c> — the one-line
    /// error message's honest form for an infinite tail
    /// (<c>type_error(list, ['1'|...])</c>), where the answer display's
    /// named-equation idiom has no room. A VarTerm so the renderer emits the
    /// three dots bare, not as a quoted atom.</summary>
    public static Term ElideCycleMarkers(Term term)
        => Rebuild(term, node => node is VarTerm { IsCycleBack: true }
            ? new VarTerm("...") : null);

    /// <summary>Replaces every compound BELOW the root whose
    /// <c>CycleId</c> (TermReader's cycle-owner stamp) has a name in
    /// <paramref name="names"/> with a variable of that name. The root is
    /// left in place so a value whose own root is the owner still shows its
    /// structure (<c>L = ['1'|L]</c>), while occurrences inside other values
    /// chain by name (<c>E = type_error(list, _S1)</c>). Run BEFORE
    /// <see cref="SubstituteVarNames"/>: its rebuilds drop the stamp.</summary>
    public static Term SubstituteCycleOwnersBelowRoot(
        Term term, IReadOnlyDictionary<string, string> names)
        => term is CompoundTerm
            ? Rebuild(term, OwnerNamer(names), mapRoot: false)
            : term;

    private static Term SubstituteOwners(Term term, IReadOnlyDictionary<string, string> names)
        => Rebuild(term, OwnerNamer(names));

    /// <summary>A compound stamped as a cycle owner whose name the display
    /// knows becomes a variable of that name, taking the whole subterm with
    /// it. An interior cons is one of those, which is what replaces the rest
    /// of a spine with its name.</summary>
    private static Func<Term, Term?> OwnerNamer(IReadOnlyDictionary<string, string> names)
        => node => node is CompoundTerm { CycleId: { } cid }
                   && names.TryGetValue(cid, out string? owner)
            ? new VarTerm(owner) : null;

    /// <summary>Rebuilds a term with <paramref name="map"/> applied to every
    /// node, on an EXPLICIT stack. How deep a term nests is the program's
    /// choice, so a recursive rebuild would spend a C# frame per level and a
    /// .NET stack overflow cannot be caught: it takes the process down. A node
    /// the map replaces is not descended into, and a subterm nothing touched
    /// comes back BY REFERENCE, so an untouched term is not copied.
    ///
    /// <para><paramref name="mapRoot"/> false leaves the root itself alone and
    /// maps only below it, which is what lets a value whose own root is a cycle
    /// owner still show its structure.</para>
    ///
    /// <para>A rebuilt compound loses its CycleId stamp, exactly as the
    /// recursive form it replaces did; SubstituteCycleOwnersBelowRoot has to
    /// run before anything that rebuilds.</para></summary>
    private static Term Rebuild(Term root, Func<Term, Term?> map, bool mapRoot = true)
    {
        if (mapRoot && map(root) is { } mappedRoot) return mappedRoot;
        if (root is not CompoundTerm rootCompound) return root;

        var pending = new Stack<Rebuilding>();
        pending.Push(new Rebuilding(rootCompound));
        while (true)
        {
            Rebuilding frame = pending.Peek();
            if (frame.Next < frame.Node.Args.Length)
            {
                Term arg = frame.Node.Args[frame.Next];
                if (map(arg) is { } replaced) frame.Put(replaced);
                else if (arg is CompoundTerm child) pending.Push(new Rebuilding(child));
                else frame.Put(arg);
                continue;
            }
            Term built = frame.Changed
                ? new CompoundTerm(frame.Node.Functor, frame.Args) : frame.Node;
            pending.Pop();
            if (pending.Count == 0) return built;
            pending.Peek().Put(built);
        }
    }

    /// <summary>One compound part-way through being rebuilt.</summary>
    private sealed class Rebuilding(CompoundTerm node)
    {
        public CompoundTerm Node { get; } = node;
        public Term[] Args { get; } = new Term[node.Args.Length];
        public int Next { get; private set; }
        public bool Changed { get; private set; }

        public void Put(Term value)
        {
            Args[Next] = value;
            if (!ReferenceEquals(value, Node.Args[Next])) Changed = true;
            Next++;
        }
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

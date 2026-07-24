using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    /// <summary><c>'$findall_collect'(List)</c> — closes the
    /// open findall buffer and unifies <c>List</c> with its collected
    /// solutions. Each solution is materialised with its own variable map
    /// so distinct solutions never accidentally share a variable.</summary>
    public static bool FindallCollect(Activation engine)
    {
        var frame = FindallHost(engine).PopFindallFrame();
        Cell list = Cell.Atom(AtomTable.EmptyListId);
        for (int i = frame.Count - 1; i >= 0; i--)
        {
            // Each entry is a cell image or an AST term
            // (value-leaf fallback); re-emit it onto the heap either way.
            Cell elem = frame[i] is Cell[] snap
                ? FindallSnapshot.EmitSnapshot(engine, snap)
                : Materializer.MaterializeAsCell(engine, (Term)frame[i]);
            int cons = engine.AllocateHeap(2);
            engine.SetHeap(cons, elem);
            engine.SetHeap(cons + 1, list);
            list = Cell.Lis(cons);
        }
        return engine.UnifyRegisterWithCell(0, list);
    }

    private static PrologEngine FindallHost(Activation engine) =>
        engine.Host as PrologEngine
        ?? throw new InvalidOperationException(
            "The in-engine findall builtins require a PrologEngine host.");

    /// <summary><c>'$bagof_collect'(Groups)</c> — closes the open
    /// solution buffer (the bagof/3 rewrite shares findall's '$findall_push'
    /// and '$findall_record') and unifies its argument with the list of
    /// <c>Witness-Bag</c> pairs that bagof/3 backtracks over: one pair per
    /// distinct witness, in standard order of the witness, each bag holding
    /// its solutions in generation order.</summary>
    public static bool BagofCollect(Activation engine)
    {
        var frame = FindallHost(engine).PopFindallFrame();
        Cell groups = Materializer.MaterializeAsCell(
            engine, BuildWitnessGroups(frame, sortBags: false));
        return engine.UnifyRegisterWithCell(0, groups);
    }

    /// <summary><c>'$setof_collect'(Groups)</c> — as
    /// <see cref="BagofCollect"/>, but each bag is sorted into standard order
    /// and stripped of duplicates — the only difference between bagof/3 and
    /// setof/3.</summary>
    public static bool SetofCollect(Activation engine)
    {
        var frame = FindallHost(engine).PopFindallFrame();
        Cell groups = Materializer.MaterializeAsCell(
            engine, BuildWitnessGroups(frame, sortBags: true));
        return engine.UnifyRegisterWithCell(0, groups);
    }

    private sealed class WitnessGroup
    {
        public readonly Term Canonical;
        public readonly List<(Term Witness, Term Template)> Pairs = new();
        public WitnessGroup(Term canonical) => Canonical = canonical;
    }

    /// <summary>Turns the buffer of <c>Witness-Template</c> pairs collected by
    /// a bagof/3 or setof/3 goal into the <c>[Witness-Bag, ...]</c> list the
    /// rewrite backtracks over with member/2.
    ///
    /// <para>Two pairs join the same group when their witnesses are variants
    /// of one another (equal up to variable renaming); the groups come out in
    /// standard order of the witness. Within a group the witness variables
    /// are rebound to a single shared set — SWI's <c>bind_bagof_keys</c> step
    /// — so a witness variable a solution happens to share with its template
    /// stays shared across the whole bag. Bag elements keep generation order
    /// (<paramref name="sortBags"/> false, bagof/3) or are sorted and
    /// de-duplicated (<paramref name="sortBags"/> true, setof/3).</para></summary>
    private static Term BuildWitnessGroups(List<object> pairs, bool sortBags)
    {
        // bagof/setof always record AST terms (they inspect witnesses for
        // grouping), so every entry here is a Term — never an I2b cell image.
        var groups = new List<WitnessGroup>();
        foreach (object pairObj in pairs)
        {
            var cons = (CompoundTerm)pairObj;       // '-'(Witness, Template)
            Term witness = cons.Args[0];
            Term canonical = CanonicalizeVars(witness, new Dictionary<string, string>());

            WitnessGroup? group = null;
            foreach (WitnessGroup candidate in groups)
            {
                if (TermStandardOrder.Compare(candidate.Canonical, canonical) == 0)
                {
                    group = candidate;
                    break;
                }
            }
            if (group is null)
            {
                group = new WitnessGroup(canonical);
                groups.Add(group);
            }
            group.Pairs.Add((witness, cons.Args[1]));
        }

        groups.Sort((a, b) => TermStandardOrder.Compare(a.Canonical, b.Canonical));

        int fresh = 0;
        var groupTerms = new List<Term>(groups.Count);
        foreach (WitnessGroup group in groups)
        {
            Term[] slotVars = Array.Empty<Term>();
            // Every group has at least one pair, so the i == 0 iteration
            // always replaces this placeholder with the real witness.
            Term representative = new AtomTerm("$w");
            var bag = new List<Term>(group.Pairs.Count);

            for (int i = 0; i < group.Pairs.Count; i++)
            {
                (Term witness, Term template) = group.Pairs[i];

                // Index the witness's distinct variables in first-occurrence
                // order. Variant witnesses index identically, so slot k names
                // the same logical variable in every pair of the group.
                var witnessSlots = new Dictionary<string, int>();
                IndexVars(witness, witnessSlots);
                if (i == 0)
                {
                    slotVars = new Term[witnessSlots.Count];
                    for (int s = 0; s < slotVars.Length; s++)
                        slotVars[s] = new VarTerm("$BV" + fresh++);
                    representative = RebindVars(
                        witness, witnessSlots, slotVars,
                        new Dictionary<string, Term>(), ref fresh);
                }

                // A template variable the witness also binds maps to the
                // shared slot variable; every other one gets a per-solution
                // fresh variable, so distinct solutions never share by chance.
                bag.Add(RebindVars(
                    template, witnessSlots, slotVars,
                    new Dictionary<string, Term>(), ref fresh));
            }

            if (sortBags)
            {
                bag.Sort(TermStandardOrder.Compare);
                int write = bag.Count == 0 ? 0 : 1;
                for (int read = 1; read < bag.Count; read++)
                {
                    if (TermStandardOrder.Compare(bag[read], bag[write - 1]) != 0)
                        bag[write++] = bag[read];
                }
                if (write < bag.Count) bag.RemoveRange(write, bag.Count - write);
            }

            groupTerms.Add(new CompoundTerm(
                "-", new[] { representative, MakeProperList(bag) }));
        }

        return MakeProperList(groupTerms);
    }

    /// <summary>Copies a term, renaming every variable to a canonical name in
    /// first-occurrence order. Two terms are variants of one another exactly
    /// when their canonical forms are structurally equal — how
    /// <see cref="BuildWitnessGroups"/> decides group membership.</summary>
    private static Term CanonicalizeVars(Term term, Dictionary<string, string> map)
    {
        switch (term)
        {
            case VarTerm v:
                if (!map.TryGetValue(v.Name, out string? canonical))
                {
                    canonical = "_C" + map.Count.ToString("D8");
                    map[v.Name] = canonical;
                }
                return new VarTerm(canonical);
            case CompoundTerm c:
                var args = new Term[c.Args.Length];
                for (int i = 0; i < c.Args.Length; i++)
                    args[i] = CanonicalizeVars(c.Args[i], map);
                return new CompoundTerm(c.Functor, args);
            default:
                return term;
        }
    }

    /// <summary>Records each distinct variable of <paramref name="term"/> in
    /// first-occurrence order, mapping its name to a slot index.</summary>
    private static void IndexVars(Term term, Dictionary<string, int> slots)
    {
        switch (term)
        {
            case VarTerm v:
                if (!slots.ContainsKey(v.Name)) slots[v.Name] = slots.Count;
                break;
            case CompoundTerm c:
                foreach (Term arg in c.Args) IndexVars(arg, slots);
                break;
        }
    }

    /// <summary>Copies a term, replacing variables: one named in
    /// <paramref name="witnessSlots"/> becomes the shared
    /// <paramref name="slotVars"/> entry for its slot; any other becomes a
    /// fresh variable, reused within this call through
    /// <paramref name="localMap"/> but distinct from every other
    /// solution's variables.</summary>
    private static Term RebindVars(
        Term term,
        Dictionary<string, int> witnessSlots,
        Term[] slotVars,
        Dictionary<string, Term> localMap,
        ref int fresh)
    {
        switch (term)
        {
            case VarTerm v:
                if (witnessSlots.TryGetValue(v.Name, out int slot))
                    return slotVars[slot];
                if (!localMap.TryGetValue(v.Name, out Term? local))
                {
                    local = new VarTerm("$BV" + fresh++);
                    localMap[v.Name] = local;
                }
                return local;
            case CompoundTerm c:
                var args = new Term[c.Args.Length];
                for (int i = 0; i < c.Args.Length; i++)
                    args[i] = RebindVars(
                        c.Args[i], witnessSlots, slotVars, localMap, ref fresh);
                return new CompoundTerm(c.Functor, args);
            default:
                return term;
        }
    }

    /// <summary>Builds a proper Prolog list term from <paramref name="elems"/>.</summary>
    private static Term MakeProperList(IReadOnlyList<Term> elems)
    {
        Term list = new AtomTerm("[]");
        for (int i = elems.Count - 1; i >= 0; i--)
            list = new CompoundTerm(".", new[] { elems[i], list });
        return list;
    }

    // findall/3, bagof/3, setof/3 and forall/2 are now prelude predicates
    // (live-engine collect over call/1 + the $findall_* collectors); the old
    // isolated-sub-engine builtins were removed — they lacked the parent's
    // bundle-precompiled definitions and hid the goal's side effects. The
    // witness-grouping $bagof_collect / $setof_collect below still serve the
    // MetaTransform callable-goal rewrite. See Prelude findall/3, bagof/3,
    // setof/3, forall/2.

    /// <summary>Reads the term currently bound in <c>X[<paramref name="regIdx"/>]</c>
    /// as an AST <see cref="Term"/>. Wraps the register's cell on the heap
    /// briefly so the existing <see cref="TermReader.Materialize"/> can do its
    /// REF-chasing work uniformly — for atomic registers this costs one
    /// throwaway heap cell.</summary>
    private static Term MaterializeRegister(Activation engine, int regIdx)
    {
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, engine.GetRegister(regIdx));
        return TermReader.Materialize(engine, slot);
    }

    /// <summary><c>'$tbl_seen'/1</c> — succeeds, recording the
    /// argument, the first time it is called with a given (structurally
    /// canonicalised) ground term; fails on every later call with an
    /// equal term. The tabling driver uses it as an O(1) duplicate-answer
    /// test, which is what makes the semi-naive fixpoint sub-quadratic —
    /// the alternative, scanning the asserted answers, is O(n) per check.</summary>
    public static bool TableSeen(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$tbl_seen'/1 requires a PrologEngine host.");
        var sb = new System.Text.StringBuilder();
        Canonicalize(MaterializeRegister(engine, 0), sb,
            new Dictionary<string, int>());
        return host.RegisterTablingKey(sb.ToString());
    }

    /// <summary><c>'$tbl_seen_clear'/0</c> — empties the
    /// engine's tabling key set, so a later re-derivation of a subgoal is
    /// not deduplicated against answers from before a table invalidation.</summary>
    public static bool TableSeenClear(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$tbl_seen_clear'/0 requires a PrologEngine host.");
        host.ClearTablingKeys();
        return true;
    }

    /// <summary><c>'$tbl_solve_complete'(+Goal)</c> — succeeds
    /// iff <paramref name="Goal"/> has at least one solution when run to a
    /// <em>complete</em> tabled evaluation. It runs in a sub-engine whose
    /// table is first abolished, so the negated subgoal's fixpoint is
    /// computed in full and in isolation — which is what makes <c>\+</c>
    /// over a tabled goal sound for a stratified program.</summary>
    public static bool TableSolveComplete(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$tbl_solve_complete'/1 requires a PrologEngine host.");
        Term goal = MaterializeRegister(engine, 0);
        Term wrapped = new CompoundTerm(",",
            new[] { (Term)new AtomTerm("abolish_all_tables"), goal });
        var sub = host.CreateSubEngine();
        foreach (var _ in sub.QueryAll(wrapped))
            return true;
        return false;
    }

    /// <summary>Appends a structurally faithful, injective encoding of a
    /// term to <paramref name="sb"/> — length-prefixed names so no two
    /// distinct terms can collide. Variables are encoded by first-occurrence
    /// index (tracked in <paramref name="vars"/>), so the encoding is
    /// invariant under variable renaming: two variant non-ground answers
    /// (e.g. <c>p(X)</c> and <c>p(Y)</c>) canonicalise to the same string
    /// and the tabling driver deduplicates them as one answer.</summary>
    private static void Canonicalize(
        Term t, System.Text.StringBuilder sb, Dictionary<string, int> vars)
    {
        switch (t)
        {
            case VarTerm v:
                if (!vars.TryGetValue(v.Name, out int vid))
                {
                    vid = vars.Count;
                    vars[v.Name] = vid;
                }
                sb.Append('v').Append(vid).Append('.');
                break;
            case AtomTerm a:
                sb.Append('a').Append(a.Name.Length).Append('_').Append(a.Name);
                break;
            case IntTerm i:
                sb.Append('i').Append(i.Value).Append('.');
                break;
            case CompoundTerm c:
                sb.Append('c').Append(c.Functor.Length).Append('_').Append(c.Functor)
                  .Append('/').Append(c.Args.Length).Append('(');
                foreach (var arg in c.Args) Canonicalize(arg, sb, vars);
                sb.Append(')');
                break;
            default:
                string s = t.ToString() ?? "";
                sb.Append('o').Append(s.Length).Append('_').Append(s);
                break;
        }
    }

}

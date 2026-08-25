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
    /// <summary><c>'$check_partial_list'(L)</c> — succeeds when L is a
    /// partial list (a variable, or a list ending in [] or a variable) and
    /// raises <c>type_error(list, L)</c> otherwise. The solutions argument
    /// of findall/bagof/setof is checked with it BEFORE the goal runs.
    /// </summary>
    public static bool CheckPartialList(Activation engine)
    {
        Cell given = ResolveLocal(engine, engine.GetRegister(0));
        Cell cur = given;
        while (true)
        {
            if (cur.Tag is Tag.Ref or Tag.AttVar or Tag.Pstr) return true;
            if (cur.Tag == Tag.Atom && cur.AsAtomId == AtomTable.EmptyListId) return true;
            if (cur.Tag != Tag.Lis)
                throw new Shumway.Core.PrologRuntimeException(
                    "type_error", "list", engine, given);
            cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
    }

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

    /// <summary>One recorded bagof/setof solution: the witness's canonical
    /// key (computed on the live heap at record time — variants share a key)
    /// and the <c>Witness-Template</c> pair as a backtrack-safe payload (a
    /// cell image, or an AST term when the image path cannot carry a value
    /// leaf).</summary>
    private sealed class BagofPair
    {
        public readonly string Key;
        public readonly object Payload;
        public BagofPair(string key, object payload) { Key = key; Payload = payload; }
    }

    /// <summary><c>'$bagof_record'(Witness-Template)</c> — the bagof/setof
    /// record step. Walks the WITNESS on the live heap to build its
    /// canonical grouping key (variable identity by deref address in
    /// first-occurrence order, so variant witnesses key identically), then
    /// snapshots the whole pair the same way findall/3 snapshots a solution
    /// — witness/template variable sharing survives inside one image. Cost
    /// per solution is a heap walk plus the findall-grade snapshot; no
    /// managed AST is built on this path.</summary>
    public static bool BagofRecord(Activation engine)
    {
        Cell pair = ResolveLocal(engine, engine.GetRegister(0));
        // The rewrite always passes '-'(Witness, Template).
        Cell witness = pair.Tag == Tag.Str
            ? engine.GetHeap(pair.AsHeapIndex + 1)
            : pair;
        var sb = new System.Text.StringBuilder(64);
        AppendHeapWitnessKey(engine, witness,
            new Dictionary<int, int>(), new HashSet<int>(), sb, 0);
        Cell[]? snap = FindallSnapshot.TrySnapshotRegister(engine, 0);
        object payload = snap is not null
            ? snap
            : MaterializeRegister(engine, 0);
        FindallHost(engine).RecordFindallEntry(
            new BagofPair(sb.ToString(), payload));
        return true;
    }

    /// <summary>Canonical witness key, straight off the heap: unbound
    /// variables (and attvars) key by first-occurrence index of their deref
    /// address — so two witnesses that alias their variables in the same
    /// pattern produce the same key, and any other pattern a different one.
    /// Every token is delimited, so neighbouring tokens cannot collide. A
    /// revisited struct address keys as a back-reference (rational trees
    /// must not loop the walk).</summary>
    private static void AppendHeapWitnessKey(
        Activation engine, Cell cell, Dictionary<int, int> varSlots,
        HashSet<int> onPath, System.Text.StringBuilder sb, int depth)
    {
        if (cell.Tag == Tag.Ref)
        {
            int addr = engine.Deref(cell.AsHeapIndex);
            Cell target = engine.GetHeap(addr);
            if (target.Tag is Tag.Ref or Tag.AttVar)
            {
                if (!varSlots.TryGetValue(addr, out int slot))
                {
                    slot = varSlots.Count;
                    varSlots[addr] = slot;
                }
                sb.Append('v').Append(slot).Append(';');
                return;
            }
            cell = target;
        }
        switch (cell.Tag)
        {
            case Tag.AttVar:
            {
                int home = engine.Deref(cell.AsHeapIndex);
                if (!varSlots.TryGetValue(home, out int slot))
                {
                    slot = varSlots.Count;
                    varSlots[home] = slot;
                }
                sb.Append('v').Append(slot).Append(';');
                break;
            }
            case Tag.Atom:
                sb.Append('a').Append(cell.AsAtomId).Append(';');
                break;
            case Tag.Int:
                sb.Append('i').Append(cell.AsInt).Append(';');
                break;
            case Tag.Float:
                sb.Append('f').Append(System.BitConverter.DoubleToInt64Bits(
                    Cell.DecodeFloat(cell, engine.GetHeap(cell.FloatPairedIndex))))
                  .Append(';');
                break;
            case Tag.BigInt:
                sb.Append('B').Append(engine.AsBigInt(cell)).Append(';');
                break;
            case Tag.Rational:
                sb.Append('R').Append(engine.AsRational(cell)).Append(';');
                break;
            case Tag.Str:
            {
                int fIdx = cell.AsHeapIndex;
                if (!onPath.Add(fIdx)) { sb.Append('L').Append(';'); break; }
                var (atomId, arity) = FunctorTable.Lookup(
                    engine.GetHeap(fIdx).AsFunctorId);
                sb.Append('c').Append(atomId).Append('/').Append(arity).Append('(');
                for (int i = 0; i < arity; i++)
                    AppendHeapWitnessKey(engine, engine.GetHeap(fIdx + 1 + i),
                        varSlots, onPath, sb, depth + 1);
                sb.Append(')');
                onPath.Remove(fIdx);
                break;
            }
            case Tag.Lis:
            case Tag.Pstr:
            {
                // Uniform list walk — a packed list and its cons form are
                // the same term, so they must key identically.
                if (cell.Tag == Tag.Lis && !onPath.Add(cell.AsHeapIndex))
                {
                    sb.Append('L').Append(';');
                    break;
                }
                if (engine.TryUnconsListLike(cell, out Cell head, out Cell tail))
                {
                    sb.Append('[');
                    AppendHeapWitnessKey(engine, head, varSlots, onPath, sb, depth + 1);
                    AppendHeapWitnessKey(engine, tail, varSlots, onPath, sb, depth + 1);
                    sb.Append(']');
                }
                if (cell.Tag == Tag.Lis) onPath.Remove(cell.AsHeapIndex);
                break;
            }
            default:
                sb.Append('t').Append((int)cell.Tag).Append('_')
                  .Append(cell.Payload).Append(';');
                break;
        }
    }

    /// <summary><c>'$bagof_next'(Kind, Witness-Bag)</c> — closes the open
    /// solution buffer, splits the recorded pairs into witness groups by the
    /// keys '$bagof_record' computed (linear — a hash probe per solution),
    /// and enumerates the groups on BACKTRACKING in standard order of the
    /// witness. Each group is materialised only when demanded: its pair
    /// images are emitted onto the heap and every pair's witness is UNIFIED
    /// with the group representative's — which is exactly the
    /// bind-bagof-keys step, aliasing witness variables shared with the
    /// templates — so a caller that cuts after the first group never pays
    /// for the rest. The cursor holds IMAGES (managed data), never heap
    /// addresses, so a heap collection between two groups moves nothing it
    /// relies on. Kind <c>setof</c> sorts and de-duplicates each bag;
    /// <c>bagof</c> keeps generation order. Fails when the goal produced no
    /// solutions.</summary>
    public static bool BagofNext(Activation engine)
    {
        Cell kindCell = ResolveLocal(engine, engine.GetRegister(0));
        bool sortBags = kindCell.Tag == Tag.Atom
            && AtomTable.GetById(kindCell.AsAtomId)?.Name == "setof";
        var frame = FindallHost(engine).PopFindallFrame();
        if (frame.Count == 0) return false;

        var byKey = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var groups = new List<List<object>>();
        foreach (object entry in frame)
        {
            var rec = (BagofPair)entry;
            if (!byKey.TryGetValue(rec.Key, out List<object>? group))
            {
                group = new List<object>();
                byKey[rec.Key] = group;
                groups.Add(group);
            }
            group.Add(rec.Payload);
        }

        // Standard order of the witness: emit each group's representative
        // pair once and compare witnesses with the engine comparator. The
        // emitted cells are used only within this call (builtins run
        // between safe points — no collection moves them) and then become
        // ordinary garbage.
        var reps = new Cell[groups.Count];
        for (int i = 0; i < groups.Count; i++)
            reps[i] = WitnessOf(engine, EmitPair(engine, groups[i][0]));
        var order = new int[groups.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        System.Array.Sort(order, (x, y) =>
            StandardOrderComparator.Compare(engine, reps[x], reps[y]));
        var ordered = new List<object>[groups.Count];
        for (int i = 0; i < order.Length; i++) ordered[i] = groups[order[i]];

        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, ordered.Length, arity: 2, returnPc,
            (e, i) => UnifyGroup(e, ordered[i], sortBags));
    }

    /// <summary>Emits one recorded pair (image or AST fallback) onto the
    /// heap.</summary>
    private static Cell EmitPair(Activation engine, object payload)
        => payload is Cell[] snap
            ? FindallSnapshot.EmitSnapshot(engine, snap)
            : Materializer.MaterializeAsCell(engine, (Term)payload);

    private static Cell WitnessOf(Activation engine, Cell pair)
    {
        Cell d = ResolveLocal(engine, pair);
        return d.Tag == Tag.Str ? engine.GetHeap(d.AsHeapIndex + 1) : d;
    }

    private static Cell TemplateOf(Activation engine, Cell pair)
    {
        Cell d = ResolveLocal(engine, pair);
        return d.Tag == Tag.Str ? engine.GetHeap(d.AsHeapIndex + 2) : d;
    }

    /// <summary>Materialises ONE witness group and unifies register 1 with
    /// its <c>Witness-Bag</c> pair.</summary>
    private static bool UnifyGroup(Activation engine, List<object> group, bool sortBags)
    {
        Cell repPair = EmitPair(engine, group[0]);
        Cell witness = WitnessOf(engine, repPair);
        int witnessSlot = engine.AllocateHeap(1);
        engine.SetHeap(witnessSlot, witness);

        var bag = new List<Cell>(group.Count) { TemplateOf(engine, repPair) };
        for (int i = 1; i < group.Count; i++)
        {
            Cell p = EmitPair(engine, group[i]);
            // bind_bagof_keys: aliasing this pair's witness into the shared
            // one also aliases the witness variables its template shares.
            // Variant witnesses always unify.
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, WitnessOf(engine, p));
            if (!engine.Unify(witnessSlot, slot)) return false;
            bag.Add(TemplateOf(engine, p));
        }

        if (sortBags)
        {
            bag.Sort((x, y) => StandardOrderComparator.Compare(engine, x, y));
            int write = 1;
            for (int read = 1; read < bag.Count; read++)
                if (StandardOrderComparator.Compare(engine, bag[read], bag[write - 1]) != 0)
                    bag[write++] = bag[read];
            if (write < bag.Count) bag.RemoveRange(write, bag.Count - write);
        }

        Cell list = Cell.Atom(AtomTable.EmptyListId);
        for (int i = bag.Count - 1; i >= 0; i--)
        {
            int cons = engine.AllocateHeap(2);
            engine.SetHeap(cons, bag[i]);
            engine.SetHeap(cons + 1, list);
            list = Cell.Lis(cons);
        }

        int fid = FunctorTable.Intern(AtomTable.Intern("-", permanent: true).Id, 2);
        int str = engine.AllocateHeap(3);
        engine.SetHeap(str, Cell.Functor(fid));
        engine.SetHeap(str + 1, engine.GetHeap(witnessSlot));
        engine.SetHeap(str + 2, list);
        return engine.UnifyRegisterWithCell(1, Cell.Str(str));
    }

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

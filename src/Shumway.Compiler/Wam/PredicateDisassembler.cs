using System.Text;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Core;

namespace Shumway.Compiler.Wam;

/// <summary>
/// Compiles the static predicates in a Prolog source and renders their WAM
/// bytecode as human-readable disassembly — the post-indexing layout
/// (<c>switch_on_term</c> / <c>try</c> / <c>retry</c> / <c>trust</c> chains and
/// per-clause bodies) the Tier-0 interpreter actually runs. Intended for
/// inspecting code generation while optimising; backs the <c>shumway-disasm</c>
/// CLI and is directly callable from tests / tooling.
/// </summary>
public static class PredicateDisassembler
{
    /// <summary>One predicate's compiled form: its <c>Name/Arity</c> label and
    /// the disassembled text, or a compile error in <see cref="Error"/>.</summary>
    public sealed record Entry(string Name, int Arity, string Text, string? Error);

    /// <summary>Parses <paramref name="source"/>, groups its facts / rules (DCG
    /// rules expanded; directives skipped) by predicate in first-seen order, and
    /// compiles each with the indexing <see cref="PredicateCompiler"/>.
    /// <paramref name="filter"/>, when non-null, restricts the result to the
    /// named <c>Name/Arity</c> indicators.</summary>
    public static IReadOnlyList<Entry> Disassemble(
        string source,
        IReadOnlyCollection<(string Name, int Arity)>? filter = null,
        bool emitDebugInfo = false,
        bool arityCompat = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Phase 30 Arity/Prolog32 sources ($...$ atoms, #line markers, the
        // `extrn` declaration operator) lex only with arity_compat on — the
        // corpus files under C:\temp\test / testGen start with a `#line`
        // directive, so the flag must be set before the first token.
        ClauseReader reader = arityCompat
            ? new ClauseReader(
                new global::Shumway.Compiler.Lexer.Lexer(source),
                OperatorTable.Default(),
                new Parsing.PrologFlags { ArityCompat = true })
            : new ClauseReader(source);
        // Same transform pipeline the engine runs (DCG + meta-call lowering +
        // phrase + mode specialization), so the disassembly is exactly what the
        // interpreter executes — including the synthesised if-then-else / `\+`
        // helper predicates. A mode-free table makes specialization a no-op.
        var clauses = ClausePipeline.Apply(
            reader.ReadAll(), new Modes.ModeTable());

        // Group by (head functor, arity), preserving first-seen order.
        var order = new List<(string Name, int Arity)>();
        var groups = new Dictionary<(string, int), List<Clause>>();
        foreach (Clause clause in clauses)
        {
            if (clause.Kind == ClauseKind.Directive) continue;
            (string name, int arity) = HeadIndicator(clause);
            var key = (name, arity);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<Clause>();
                order.Add(key);
            }
            list.Add(clause);
        }

        var result = new List<Entry>();
        foreach ((string name, int arity) in order)
        {
            if (filter is not null && !filter.Contains((name, arity))) continue;
            string label = $"{name}/{arity}";
            try
            {
                CompiledPredicate pred = new PredicateCompiler { EmitDebugInfo = emitDebugInfo }
                    .Compile(groups[(name, arity)]);
                result.Add(new Entry(name, arity, Format(label, pred.Bytecode), Error: null));
            }
            catch (Exception ex)
            {
                result.Add(new Entry(name, arity, Text: "", Error: ex.Message));
            }
        }
        return result;
    }

    /// <summary>Renders a single predicate's bytecode region as a header line
    /// plus one line per decoded instruction (<c>offset: mnemonic [operands]</c>).</summary>
    public static string Format(string label, byte[] bytecode)
    {
        ArgumentNullException.ThrowIfNull(bytecode);
        var sb = new StringBuilder();
        sb.AppendLine($"=== {label}  ({bytecode.Length} bytes) ===");
        foreach (DisassembledInstruction ins in Disassembler.Iterate(bytecode, 0, bytecode.Length))
        {
            // The Meta mnemonic already names its sub-opcode (e.g.
            // "meta dbg_info"), so MetaSubOpcode is not re-appended here.
            sb.Append($"  {ins.Address,4}: {ins.Mnemonic}");
            if (ins.Operands is { Length: > 0 })
                sb.Append("  [" + string.Join(", ", ins.Operands) + "]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------
    //  Indexing-quality audit (diagnostic).
    //
    //  For each multi-clause static predicate, categorise how well Shumway's
    //  emitted indexing separates a *ground* call from the rest of the clause
    //  set, and — where it does not — why. Shumway's confirmed real behaviour:
    //    * arg0 is fully value-indexed (switch_on_atom / _integer /
    //      _structure; all lists share one label);
    //    * within a list/struct arg0 bucket, ADR-027 second-level (sub-arg)
    //      indexing can discriminate the list head / a struct sub-arg;
    //    * BUT no *sibling*-argument indexing is applied inside a multi-clause
    //      ground arg0 value bucket — those stay a linear try/retry/trust scan
    //      (switch_on_arg only serves the var-arg0 fallthrough path and
    //      singleton buckets).
    //  The audit models exactly this so the residual linear scan a real call
    //  faces, and its avoidable cause, can be quantified across a corpus.
    // ---------------------------------------------------------------------

    /// <summary>One predicate's indexing verdict. <see cref="WorstBucket"/> is
    /// the number of clauses a single ground call can be forced to scan
    /// linearly given Shumway's real indexing; <see cref="Potential"/> is what
    /// that would drop to if the identified fix (per <see cref="Category"/>)
    /// were applied; <see cref="DiscrimArg"/> is the 1-based sibling argument
    /// that would discriminate (or -1).</summary>
    public sealed record IndexAuditEntry(
        string Name, int Arity, int Clauses, string Category,
        int WorstBucket, int PotAtomInt, int PotStruct, int PotStructNoWild,
        int CutPct, bool Det, int DiscrimArg, string WorstKey);

    /// <summary>Parses <paramref name="source"/>, groups static predicates, and
    /// returns an indexing verdict for each with ≥1 clause. Categories:
    /// <c>INDEXED_OK</c> (arg0 alone leaves ≤2), <c>INDEXED_SUBARG</c> (ADR-027
    /// reduces the worst list/struct bucket), <c>MISSED_MULTIARG</c> (a sibling
    /// arg would make the worst ground bucket ~deterministic but Shumway leaves
    /// a linear scan — the actionable gap), <c>OVERLAP</c> (no single-arg ground
    /// discriminator — inherent scan), <c>VAR_HEADED</c> (var-arg0 clauses
    /// dominate the worst bucket), <c>NOARG</c> (arity 0).</summary>
    public static IReadOnlyList<IndexAuditEntry> AuditIndexing(
        string source, bool arityCompat = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ClauseReader reader = arityCompat
            ? new ClauseReader(
                new global::Shumway.Compiler.Lexer.Lexer(source),
                OperatorTable.Default(),
                new Parsing.PrologFlags { ArityCompat = true })
            : new ClauseReader(source);
        var clauses = ClausePipeline.Apply(reader.ReadAll(), new Modes.ModeTable());

        var order = new List<(string, int)>();
        var groups = new Dictionary<(string, int), List<Clause>>();
        foreach (Clause clause in clauses)
        {
            if (clause.Kind == ClauseKind.Directive) continue;
            (string name, int arity) = HeadIndicator(clause);
            var key = (name, arity);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<Clause>();
                order.Add(key);
            }
            list.Add(clause);
        }

        var result = new List<IndexAuditEntry>();
        foreach ((string name, int arity) in order)
            result.Add(AnalyzePredicate(name, arity, groups[(name, arity)]));
        return result;
    }

    /// <summary>The outcome of splitting a clause set by an indexing key at some
    /// position: <see cref="Res"/> = worst-case residual scan (largest key-group +
    /// wildcards that merge into every group); <see cref="GroupMax"/> = that
    /// largest key-group WITHOUT the wildcards — the residual once a committed
    /// (cutting) target prunes the trailing wildcard/var clauses; <see cref="Arg"/>
    /// = the 0-based sibling arg chosen (-1 for a sub-arg path). A default with
    /// <c>Res == int.MaxValue</c> means "no partitioning position found".</summary>
    private readonly record struct Split(int Res, int GroupMax, int Wild, int Arg)
    {
        public static readonly Split None = new(int.MaxValue, 0, 0, -1);
        public bool Partitions => Res != int.MaxValue;
    }

    private static IndexAuditEntry AnalyzePredicate(string name, int arity, List<Clause> clauses)
    {
        int n = clauses.Count;
        if (arity == 0)
            return new IndexAuditEntry(name, arity, n, "NOARG", n, n, n, n, 0, false, -1, "-");

        // Head args per clause.
        var heads = new List<Term[]>(n);
        foreach (Clause c in clauses)
            heads.Add(HeadArgs(c));

        // Bucket by arg0 principal key; a var arg0 merges into every bucket.
        var buckets = new Dictionary<string, List<int>>();      // key -> clause indices
        var varClauses = new List<int>();
        for (int i = 0; i < n; i++)
        {
            string? k = Arg0Bucket(heads[i]);
            if (k is null) varClauses.Add(i);
            else (buckets.TryGetValue(k, out var l) ? l : (buckets[k] = new List<int>())).Add(i);
        }
        int varN = varClauses.Count;

        // Worst bucket by base scan (bucket size + var-arg0 clauses). If there is
        // no ground bucket, all clauses are var-headed → the "worst bucket" is the
        // var-fallthrough chain itself (which Shumway indexes via switch_on_arg).
        string worstKey = "var";
        List<int> worst = varClauses;
        int worstBase = varN;
        foreach (var (k, idxs) in buckets)
        {
            int b = idxs.Count + varN;
            if (b > worstBase) { worstBase = b; worst = idxs; worstKey = k; }
        }

        if (worstBase <= 2)
            return new IndexAuditEntry(name, arity, n, "INDEXED_OK",
                worstBase, worstBase, worstBase, worstBase, 0, worstBase <= 1, -1, worstKey);

        // The clauses a ground call to worstKey actually scans = bucket ∪ var.
        var scanned = worstKey == "var" ? varClauses : Concat(worst, varClauses);
        bool keyIsListOrStruct = worstKey == "list" || worstKey.StartsWith("struct:", StringComparison.Ordinal);

        // Fraction of the scanned chain whose clause body commits (a top-level cut).
        // A committed target prunes the trailing wildcard/var clauses, so the
        // realistic residual is GroupMax, not GroupMax+Wild (the user's point).
        int cutCount = 0;
        foreach (int ci in scanned) if (BodyCommits(clauses[ci])) cutCount++;
        int cutPct = scanned.Count == 0 ? 0 : (int)Math.Round(100.0 * cutCount / scanned.Count);

        // --- Capability A: atom/int keys only. --- Capability B: + structure keys.
        Split aiSub = keyIsListOrStruct ? ProbeSubArg(heads, worst, varN, structKeys: false) : Split.None;
        Split aiSib = ProbeSibling(heads, scanned, structKeys: false);
        Split ai = Min(aiSub, aiSib);
        Split bSub = keyIsListOrStruct ? ProbeSubArg(heads, worst, varN, structKeys: true) : Split.None;
        Split bSib = ProbeSibling(heads, scanned, structKeys: true);
        Split bcap = Min(Min(bSub, bSib), ai);   // B is a superset of A

        int potAI = ai.Partitions && ai.Res < worstBase ? ai.Res : worstBase;
        int potStruct = bcap.Partitions && bcap.Res < worstBase ? bcap.Res : worstBase;
        // Cut-aware residual: the winning split's largest key-group, wildcards
        // pruned. Deterministic when that group is a singleton.
        Split win = bcap.Partitions && bcap.Res < worstBase ? bcap : (ai.Partitions ? ai : Split.None);
        int potNoWild = win.Partitions ? win.GroupMax : potStruct;
        bool det = win.Partitions && win.GroupMax <= 1;

        string cat; int discrim = win.Arg >= 0 ? win.Arg + 1 : -1;
        if (potAI < worstBase) cat = "IDX_ATOMINT";
        else if (potStruct < worstBase) cat = "IDX_STRUCT";
        else if (varN >= (worstKey == "var" ? n : worst.Count)) { cat = "VAR_HEADED"; det = false; }
        else { cat = "OVERLAP"; det = false; }

        return new IndexAuditEntry(name, arity, n, cat,
            worstBase, potAI, potStruct, potNoWild, cutPct, det, discrim, worstKey);
    }

    private static List<int> Concat(List<int> a, List<int> b)
    {
        var r = new List<int>(a.Count + b.Count);
        r.AddRange(a); r.AddRange(b);
        return r;
    }

    private static Split Min(Split a, Split b) => a.Res <= b.Res ? a : b;

    /// <summary>Does the clause body commit via a top-level cut (a <c>!</c>
    /// conjunct in the main <c>,</c>-chain)? If so, entering it prunes the
    /// choice points to later clauses — so an index that routes here makes the
    /// call deterministic regardless of trailing match-all clauses. Descends
    /// only through <c>,</c>/2 (a cut inside <c>;</c>/<c>-&gt;</c> is scoped and
    /// does not prune sibling clauses).</summary>
    private static bool BodyCommits(Clause c)
    {
        if (c.Kind != ClauseKind.Rule || c.Term is not CompoundTerm r || r.Args.Length != 2)
            return false;
        return TopLevelCut(r.Args[1]);

        static bool TopLevelCut(Term t) => t switch
        {
            AtomTerm a => a.Name == "!",
            CompoundTerm c when c.Functor == "," && c.Args.Length == 2 =>
                TopLevelCut(c.Args[0]) || TopLevelCut(c.Args[1]),
            _ => false,
        };
    }

    /// <summary>Best split over sibling args j≥1: for each arg position, partition
    /// the clauses by key (a var at that position is a wildcard joining every
    /// group). <paramref name="structKeys"/> false = only atom/int values
    /// discriminate (the cheap capability); true = struct-functor and list also
    /// discriminate (structure-keyed). Returns the minimal-residual split, or
    /// <see cref="Split.None"/>.</summary>
    private static Split ProbeSibling(List<Term[]> heads, List<int> clauseIdxs, bool structKeys)
    {
        if (clauseIdxs.Count == 0) return Split.None;
        int maxArity = 0;
        foreach (int ci in clauseIdxs) maxArity = Math.Max(maxArity, heads[ci].Length);

        Split best = Split.None;
        for (int j = 1; j < maxArity; j++)
        {
            var byKey = new Dictionary<string, int>();
            int wild = 0;
            foreach (int ci in clauseIdxs)
            {
                Term[] h = heads[ci];
                string? k = j < h.Length ? (structKeys ? GroundKey(h[j]) : AtomIntKey(h[j])) : null;
                if (k is null) { wild++; continue; }
                byKey[k] = byKey.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            if (byKey.Count < 2) continue;      // no discrimination at this arg
            int groupMax = 0;
            foreach (int c in byKey.Values) groupMax = Math.Max(groupMax, c);
            int res = groupMax + wild;           // wildcard clauses merge everywhere
            if (res < best.Res) best = new Split(res, groupMax, wild, j);
        }
        return best;
    }

    /// <summary>Best split over a sub-path into arg0 (the list head / a struct
    /// sub-arg; depth ≤ 2). <paramref name="structKeys"/> false = ADR-027 v1
    /// (homogeneous atom/int sub-keys); true = also key on the sub-term's functor
    /// (structure-keyed sub — the <c>addlay_p</c> list-head-functor case).</summary>
    private static Split ProbeSubArg(List<Term[]> heads, List<int> bucket, int varN, bool structKeys)
    {
        Split best = Split.None;
        const int maxSub = 8;

        for (int s = 0; s < maxSub; s++)
        {
            Probe(s, -1);
            for (int t = 0; t < maxSub; t++) Probe(s, t);
        }
        return best;

        void Probe(int s, int t)
        {
            var byKey = new Dictionary<string, int>();
            int wild = 0; bool sawAtom = false, sawInt = false;
            foreach (int ci in bucket)
            {
                Term arg0 = ArgAt(heads[ci], 0);
                Term? sub = SubTerm(arg0, s);
                if (sub is not null && t >= 0) sub = SubTerm(sub, t);
                if (sub is null) { wild++; continue; }
                string? k = structKeys
                    ? GroundKey(sub)
                    : GroundAtomOrInt(sub, ref sawAtom, ref sawInt);
                if (k is null) { wild++; continue; }
                byKey[k] = byKey.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            if (!structKeys && sawAtom && sawInt) return;   // ADR-027 declines heterogeneous
            if (byKey.Count < 2) return;
            int groupMax = 0;
            foreach (int c in byKey.Values) groupMax = Math.Max(groupMax, c);
            int res = groupMax + wild + varN;    // var-arg0 + var-at-sub clauses = wildcards
            if (res < best.Res) best = new Split(res, groupMax, wild + varN, -1);
        }
    }

    // --- small term helpers for the audit -------------------------------------

    private static Term[] HeadArgs(Clause c)
    {
        Term head = c.Kind == ClauseKind.Rule && c.Term is CompoundTerm r ? r.Args[0] : c.Term;
        return head is CompoundTerm hc ? hc.Args : Array.Empty<Term>();
    }

    private static Term ArgAt(Term[] args, int i) => i < args.Length ? args[i] : new VarTerm("_");

    /// <summary>arg0 bucket key: null for a var (merges into all buckets); all
    /// lists share the single "list" bucket (switch_on_term list label);
    /// structs bucket by functor/arity; atoms/ints/others by value.</summary>
    private static string? Arg0Bucket(Term[] args)
    {
        if (args.Length == 0) return null;
        Term a = args[0];
        return a switch
        {
            VarTerm => null,
            CompoundTerm c when c.Functor == "." && c.Args.Length == 2 => "list",
            CompoundTerm c => $"struct:{c.Functor}/{c.Args.Length}",
            AtomTerm at => $"atom:{at.Name}",
            IntTerm it => $"int:{it.Value}",
            _ => $"other:{a}",
        };
    }

    /// <summary>A principal key for sibling-arg discrimination (any ground type;
    /// a var returns null = wildcard).</summary>
    private static string? GroundKey(Term t) => t switch
    {
        VarTerm => null,
        AtomTerm a => "a:" + a.Name,
        IntTerm i => "i:" + i.Value,
        CompoundTerm c when c.Functor == "." && c.Args.Length == 2 => "L",
        CompoundTerm c => "s:" + c.Functor + "/" + c.Args.Length,
        _ => "o:" + t,
    };

    /// <summary>An atom/int-only discrimination key (the cheap
    /// switch_on_{atom,integer}_arg capability); null (wildcard) for a var or a
    /// compound/list — those do not discriminate without structure keying.</summary>
    private static string? AtomIntKey(Term t) => t switch
    {
        AtomTerm a => "a:" + a.Name,
        IntTerm i => "i:" + i.Value,
        _ => null,
    };

    /// <summary>Only atom/int ground keys (ADR-027's homogeneous sub-key
    /// requirement); tracks whether atoms/ints were seen so a heterogeneous
    /// mix can be rejected. Returns null for a var or a non-atom/int term.</summary>
    private static string? GroundAtomOrInt(Term t, ref bool sawAtom, ref bool sawInt)
    {
        switch (t)
        {
            case AtomTerm a: sawAtom = true; return "a:" + a.Name;
            case IntTerm i: sawInt = true; return "i:" + i.Value;
            default: return null;
        }
    }

    /// <summary>Step into sub-position <paramref name="idx"/> of a list/compound
    /// (mirrors the interpreter's SubCell): a list '.'/2 exposes head=0/tail=1;
    /// a compound exposes its args. Null if the term is not a compound or the
    /// index is out of range (a var, atom, etc. — a path miss).</summary>
    private static Term? SubTerm(Term t, int idx)
    {
        if (t is CompoundTerm c && idx >= 0 && idx < c.Args.Length) return c.Args[idx];
        return null;
    }

    private static (string Name, int Arity) HeadIndicator(Clause clause)
    {
        // A Rule is `:-/2` with the head at Args[0]; a Fact is the term itself.
        Term head = clause.Kind == ClauseKind.Rule && clause.Term is CompoundTerm r
            ? r.Args[0]
            : clause.Term;
        return head switch
        {
            CompoundTerm c => (c.Functor, c.Args.Length),
            AtomTerm a => (a.Name, 0),
            _ => (head.ToString() ?? "?", 0),
        };
    }
}

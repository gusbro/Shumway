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
        int WorstBucket, int Potential, int DiscrimArg, string WorstKey);

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

    private static IndexAuditEntry AnalyzePredicate(string name, int arity, List<Clause> clauses)
    {
        int n = clauses.Count;
        if (arity == 0)
            return new IndexAuditEntry(name, arity, n, "NOARG", n, n, -1, "-");

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

        // Worst ground bucket by base scan (bucket size + var-arg0 clauses).
        string worstKey = "-";
        List<int>? worst = null;
        int worstBase = 0;
        foreach (var (k, idxs) in buckets)
        {
            int b = idxs.Count + varN;
            if (b > worstBase) { worstBase = b; worst = idxs; worstKey = k; }
        }

        // No ground bucket exceeds var-only, or arg0 already isolates everything.
        if (worst is null)
        {
            // All clauses var-headed: switch_on_arg on the var path is the only
            // lever, and the var clause is inherently in every path.
            int vp = ProbeSibling(heads, varClauses, out int varg);
            return new IndexAuditEntry(name, arity, n,
                n <= 2 ? "INDEXED_OK" : "VAR_HEADED", n, vp, varg, "var");
        }
        if (worstBase <= 2)
            return new IndexAuditEntry(name, arity, n, "INDEXED_OK", worstBase, worstBase, -1, worstKey);

        // The clauses a ground call to worstKey actually scans = bucket ∪ var.
        var scanned = new List<int>(worst);
        scanned.AddRange(varClauses);

        // (1) ADR-027 sub-arg: only for list/struct buckets. Does a sub-path
        //     (list head, or a struct sub-arg; depth ≤ 2) partition into ≥2
        //     homogeneous ground atom/int keys?
        bool keyIsListOrStruct = worstKey == "list" || worstKey.StartsWith("struct:", StringComparison.Ordinal);
        int subResidual = keyIsListOrStruct
            ? ProbeSubArg(heads, worst, varClauses, worstKey)
            : -1;

        // (2) Sibling arg (multi-arg) — Shumway does NOT apply this inside a
        //     ground bucket. Would some arg j≥1 reduce the scan?
        int sibResidual = ProbeSibling(heads, scanned, out int discrimArg);

        if (subResidual >= 0 && subResidual < worstBase)
            // ADR-027 already handles it.
            return new IndexAuditEntry(name, arity, n, "INDEXED_SUBARG",
                worstBase, subResidual, -1, worstKey);

        if (sibResidual >= 0 && sibResidual < worstBase)
            return new IndexAuditEntry(name, arity, n, "MISSED_MULTIARG",
                worstBase, sibResidual, discrimArg + 1, worstKey);

        if (varN >= worst.Count)
            return new IndexAuditEntry(name, arity, n, "VAR_HEADED",
                worstBase, worstBase, -1, worstKey);

        return new IndexAuditEntry(name, arity, n, "OVERLAP",
            worstBase, worstBase, -1, worstKey);
    }

    /// <summary>Best worst-case sub-bucket over sibling args j≥1 for the given
    /// clause set: for each arg position, split the clauses by ground principal
    /// key (a var at that position joins every split); returns the smallest
    /// resulting worst-split size that has ≥2 distinct ground keys, else -1.
    /// <paramref name="bestArg"/> receives the 0-based arg index chosen.</summary>
    private static int ProbeSibling(List<Term[]> heads, List<int> clauseIdxs, out int bestArg)
    {
        bestArg = -1;
        if (clauseIdxs.Count == 0) return -1;
        int maxArity = 0;
        foreach (int ci in clauseIdxs) maxArity = Math.Max(maxArity, heads[ci].Length);

        int best = int.MaxValue;
        for (int j = 1; j < maxArity; j++)
        {
            var byKey = new Dictionary<string, int>();
            int wild = 0, distinct = 0;
            foreach (int ci in clauseIdxs)
            {
                Term[] h = heads[ci];
                string? k = j < h.Length ? GroundKey(h[j]) : null;
                if (k is null) { wild++; continue; }
                byKey[k] = byKey.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            distinct = byKey.Count;
            if (distinct < 2) continue;         // no discrimination at this arg
            int worstSplit = 0;
            foreach (int c in byKey.Values) worstSplit = Math.Max(worstSplit, c);
            worstSplit += wild;                  // var-at-j clauses merge everywhere
            if (worstSplit < best) { best = worstSplit; bestArg = j; }
        }
        return best == int.MaxValue ? -1 : best;
    }

    /// <summary>ADR-027 model: does a sub-path into the list head / a struct
    /// sub-arg (depth ≤ 2) split the bucket into ≥2 homogeneous ground atom/int
    /// keys? Returns the worst-split residual (largest homogeneous group + the
    /// var-at-path clauses + var-arg0 clauses) or -1 if no such path.</summary>
    private static int ProbeSubArg(List<Term[]> heads, List<int> bucket, List<int> varClauses, string worstKey)
    {
        // Candidate sub-terms per clause at a chosen (sub0[, sub1]) path.
        // For a list bucket the arg is '.'/2: sub0=0 (head), sub0=1 (tail).
        // For a struct bucket f/M the arg is that compound: sub0 in 0..M-1.
        // Depth-2 (list head → token's sub-arg) is folded in by re-probing the
        // head compound's args.
        int best = int.MaxValue;

        // Enumerate depth-1 sub positions.
        int maxSub = 8;
        for (int s = 0; s < maxSub; s++)
        {
            var byKey = new Dictionary<string, int>();
            int wild = 0; bool sawAtom = false, sawInt = false, bail = false;
            // depth-2 accumulation over the same s (head is a compound, probe its args)
            for (int pass = 0; pass < 1 && !bail; pass++) { }
            foreach (int ci in bucket)
            {
                Term arg0 = ArgAt(heads[ci], 0);
                Term? sub = SubTerm(arg0, s);
                if (sub is null) { wild++; continue; }   // path misses -> wildcard
                string? k = GroundAtomOrInt(sub, ref sawAtom, ref sawInt);
                if (k is null) { wild++; continue; }
                byKey[k] = byKey.TryGetValue(k, out int c) ? c + 1 : 1;
            }
            if (sawAtom && sawInt) continue;             // heterogeneous -> ADR-027 declines
            if (byKey.Count >= 2)
            {
                int worstSplit = 0;
                foreach (int c in byKey.Values) worstSplit = Math.Max(worstSplit, c);
                worstSplit += wild + varClauses.Count;
                best = Math.Min(best, worstSplit);
            }
        }

        // Depth-2: list head is a compound sharing one functor; probe its sub-args.
        // (struct arg that is itself a compound handled the same way.)
        for (int s = 0; s < maxSub; s++)
        {
            for (int t = 0; t < maxSub; t++)
            {
                var byKey = new Dictionary<string, int>();
                int wild = 0; bool sawAtom = false, sawInt = false;
                foreach (int ci in bucket)
                {
                    Term arg0 = ArgAt(heads[ci], 0);
                    Term? mid = SubTerm(arg0, s);
                    Term? sub = mid is null ? null : SubTerm(mid, t);
                    if (sub is null) { wild++; continue; }
                    string? k = GroundAtomOrInt(sub, ref sawAtom, ref sawInt);
                    if (k is null) { wild++; continue; }
                    byKey[k] = byKey.TryGetValue(k, out int c) ? c + 1 : 1;
                }
                if (sawAtom && sawInt) continue;
                if (byKey.Count >= 2)
                {
                    int worstSplit = 0;
                    foreach (int c in byKey.Values) worstSplit = Math.Max(worstSplit, c);
                    worstSplit += wild + varClauses.Count;
                    best = Math.Min(best, worstSplit);
                }
            }
        }
        return best == int.MaxValue ? -1 : best;
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

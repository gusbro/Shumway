using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

// Goal parsing / reference collection: LINKER logic that happens to live on
// the emitter's type. In its own file because the net48 build of the
// toolchain excludes ExecutableEmitter.cs (Emit shells out to dotnet publish
// — a .NET 10 SDK affair) but still links --goal roots through these.
public static partial class ExecutableEmitter
{
    /// <summary>Parses and validates <paramref name="goal"/>. A
    /// trailing <c>.</c> (the Prolog clause terminator) is stripped
    /// if present so users can pass <c>"main"</c> or <c>"main."</c>
    /// interchangeably. Returns the normalised
    /// <c>goal-as-Prolog-source</c> with a guaranteed trailing dot,
    /// plus the head predicate's <see cref="PredicateRef"/> the linker
    /// should treat as an additional reachability root.</summary>
    public static bool TryValidateGoal(string goal,
        out string normalisedGoal,
        out PredicateRef headPred,
        out string? error)
    {
        normalisedGoal = "";
        headPred = default;
        error = null;
        if (string.IsNullOrWhiteSpace(goal))
        {
            error = "goal is empty.";
            return false;
        }
        string trimmed = goal.Trim();
        if (trimmed.EndsWith('.')) trimmed = trimmed[..^1].Trim();
        if (trimmed.Length == 0)
        {
            error = "goal is empty after stripping trailing '.'.";
            return false;
        }
        Term term;
        try
        {
            var parser = new Parser(new Lexer(trimmed + " ."), OperatorTable.Default());
            term = parser.ReadClauseTerm();
        }
        catch (ParseException ex)
        {
            error = $"goal parse error: {ex.Message}";
            return false;
        }
        switch (term)
        {
            case AtomTerm a:
                headPred = new PredicateRef(a.Name, 0);
                break;
            case CompoundTerm c:
                headPred = new PredicateRef(c.Functor, c.Args.Length);
                break;
            default:
                error = "goal must be a callable term (atom or compound), not a number / variable.";
                return false;
        }
        normalisedGoal = trimmed + ".";
        return true;
    }

    /// <summary>Collects the predicate references a runtime goal makes, so the
    /// linker can treat the goal like a query typed at the REPL rather than
    /// requiring its head to be a user predicate.
    ///
    /// <para><paramref name="callRefs"/> are the functors in CALL position —
    /// the goal itself and the goals under the standard control constructs
    /// (<c>,</c> <c>;</c> <c>-&gt;</c> <c>*-&gt;</c> <c>\+</c> <c>not</c>
    /// <c>once</c> <c>ignore</c> <c>call/1</c>). Each must resolve somewhere
    /// (user predicate, builtin or prelude) or the link fails — this keeps a
    /// typo in the goal a link-time error.</para>
    ///
    /// <para><paramref name="termRefs"/> are every OTHER callable subterm, at
    /// any depth — e.g. <c>mi_pred</c> in <c>time(mi_pred)</c> or a closure in
    /// <c>findall/3</c>. They are speculative: one that resolves to a user
    /// predicate becomes a reachability root; anything else (a data atom, a
    /// builtin) is silently ignored. Over-collection only links more, never
    /// breaks the link.</para></summary>
    public static bool TryCollectGoalRefs(string goal,
        out List<PredicateRef> callRefs,
        out List<PredicateRef> termRefs,
        out string? error)
    {
        callRefs = new List<PredicateRef>();
        termRefs = new List<PredicateRef>();
        if (!TryValidateGoal(goal, out _, out _, out error)) return false;
        string trimmed = goal.Trim();
        if (trimmed.EndsWith('.')) trimmed = trimmed[..^1].Trim();
        var parser = new Parser(new Lexer(trimmed + " ."), OperatorTable.Default());
        Term term = parser.ReadClauseTerm();
        var seenCall = new HashSet<PredicateRef>();
        var seenTerm = new HashSet<PredicateRef>();
        CollectGoalRefs(term, callPosition: true, callRefs, termRefs, seenCall, seenTerm);
        return true;
    }

    private static void CollectGoalRefs(Term t, bool callPosition,
        List<PredicateRef> callRefs, List<PredicateRef> termRefs,
        HashSet<PredicateRef> seenCall, HashSet<PredicateRef> seenTerm)
    {
        switch (t)
        {
            case AtomTerm a:
                if (a.Name is "true" or "fail" or "false" or "!" or "[]") return;
                Add(new PredicateRef(a.Name, 0));
                return;
            case CompoundTerm c:
                // Control constructs: their goal arguments stay in call
                // position; a variable goal there is fine (runtime meta-call).
                if (callPosition && c.Args.Length == 2
                    && c.Functor is "," or ";" or "->" or "*->")
                {
                    CollectGoalRefs(c.Args[0], true, callRefs, termRefs, seenCall, seenTerm);
                    CollectGoalRefs(c.Args[1], true, callRefs, termRefs, seenCall, seenTerm);
                    return;
                }
                if (callPosition && c.Args.Length == 1
                    && c.Functor is "\\+" or "not" or "once" or "ignore" or "call")
                {
                    CollectGoalRefs(c.Args[0], true, callRefs, termRefs, seenCall, seenTerm);
                    return;
                }
                Add(new PredicateRef(c.Functor, c.Args.Length));
                foreach (var arg in c.Args)
                    CollectGoalRefs(arg, false, callRefs, termRefs, seenCall, seenTerm);
                return;
            default:
                return;   // variable / number / string — no reference
        }

        void Add(PredicateRef r)
        {
            if (callPosition) { if (seenCall.Add(r)) callRefs.Add(r); }
            else { if (seenTerm.Add(r)) termRefs.Add(r); }
        }
    }
}

using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;

namespace Shumway.Embedding;

/// <summary>
/// Compiles one Prolog source file (or in-memory source) into an in-
/// memory <see cref="ShmoObject"/>. Used by the <c>shumway-compile</c>
/// CLI (chunk 161) and by the linker's tests; the in-process embedder
/// can also call it to produce <c>.shmo</c> artifacts on the fly.
///
/// <para>Pipeline:</para>
/// <list type="number">
/// <item>Parse with <see cref="ClauseReader"/>.</item>
/// <item>Apply <see cref="DcgTransform"/> (DCG rules become normal rules
/// with the diff-list pair appended to head &amp; goals).</item>
/// <item>Walk directives for <c>:- module/1</c>, <c>:- public/1</c>,
/// <c>:- dynamic/1</c>, <c>:- ensure_linked/1</c> (the last is chunk 162).</item>
/// <item>For every non-directive clause, classify the head's
/// <c>Name/Arity</c>, attach the right visibility, and walk the body
/// emitting call edges into the per-predicate call graph.</item>
/// <item>Compile the surviving rule/fact clauses via
/// <see cref="ModuleCompiler"/> and encode through
/// <see cref="CompiledModuleCodec"/>.</item>
/// </list>
///
/// <para>The linker (chunk 163) filters out builtins from the call
/// graph and resolves the remainder against the union of every loaded
/// <c>.shmo</c>'s <c>:- public</c>/<c>:- dynamic</c> set. Anything still
/// unresolved is the missing-predicate report.</para>
/// </summary>
public static class ShmoCompiler
{
    /// <summary>Compiles <paramref name="path"/> to a <see cref="ShmoObject"/>.
    /// The module name defaults to the file's bare name (without
    /// extension) when no <c>:- module(Name).</c> directive is
    /// present.</summary>
    public static ShmoObject CompileFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string source = File.ReadAllText(path);
        string fallback = Path.GetFileNameWithoutExtension(path);
        return CompileSource(source, fallback);
    }

    /// <summary>Compiles <paramref name="source"/> in memory.
    /// <paramref name="moduleNameFallback"/> is used when the source
    /// has no <c>:- module/1</c> directive; pass the empty string for
    /// "<c>user</c>" — Shumway's default.</summary>
    public static ShmoObject CompileSource(string source, string moduleNameFallback = "user")
    {
        ArgumentNullException.ThrowIfNull(source);
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();

        var allClauses = new ClauseReader(new Lexer(source), OperatorTable.Default())
            .ReadAll()
            .ToList();
        var expanded = DcgTransform.Apply(allClauses);

        string moduleName = moduleNameFallback;
        var publicSet = new HashSet<PredicateRef>();
        var dynamicSet = new HashSet<PredicateRef>();
        var ensureLinked = new List<PredicateRef>();
        var qualifiedRefs = new List<QualifiedPredicateRef>();
        var clauses = new List<Clause>();

        foreach (var clause in expanded)
        {
            if (clause.Kind == ClauseKind.Directive
                && clause.Term is CompoundTerm d
                && d.Functor == ":-" && d.Args.Length == 1)
            {
                ProcessDirective(d.Args[0], ref moduleName,
                    publicSet, dynamicSet, ensureLinked);
                continue;
            }
            clauses.Add(clause);
        }

        var definedOrder = new List<PredicateRef>();
        var definedSet = new HashSet<PredicateRef>();
        var callGraph = new Dictionary<PredicateRef, HashSet<PredicateRef>>();

        foreach (var clause in clauses)
        {
            PredicateRef? head = TryExtractHead(clause);
            if (head is null) continue;
            if (definedSet.Add(head.Value))
                definedOrder.Add(head.Value);
            if (!callGraph.TryGetValue(head.Value, out var edges))
            {
                edges = new HashSet<PredicateRef>();
                callGraph[head.Value] = edges;
            }
            Term body = ExtractBody(clause);
            CollectCalls(body, edges, qualifiedRefs);
        }

        // A :- dynamic declaration with no clauses still counts as
        // defined (with visibility=Dynamic) — the linker uses it to
        // satisfy references.
        foreach (var d in dynamicSet)
        {
            if (definedSet.Add(d))
                definedOrder.Add(d);
            if (!callGraph.ContainsKey(d))
                callGraph[d] = new HashSet<PredicateRef>();
        }

        var defined = new List<ShmoDefinedPredicate>(definedOrder.Count);
        foreach (var p in definedOrder)
        {
            var vis = dynamicSet.Contains(p)
                ? PredicateVisibility.Dynamic
                : publicSet.Contains(p)
                    ? PredicateVisibility.Public
                    : PredicateVisibility.Local;
            defined.Add(new ShmoDefinedPredicate(p, vis));
        }

        var module = new ModuleCompiler().Compile(clauses);
        byte[] bytecode = CompiledModuleCodec.Encode(module);

        var callGraphRO = new Dictionary<PredicateRef, IReadOnlyList<PredicateRef>>();
        foreach (var (k, v) in callGraph)
            callGraphRO[k] = v.ToArray();

        return new ShmoObject(
            moduleName: moduleName,
            source: source,
            bytecode: bytecode,
            defined: defined,
            ensureLinked: ensureLinked,
            callGraph: callGraphRO,
            qualifiedRefs: qualifiedRefs);
    }

    // ------------------------------------------------------------------------
    // Directive handling
    // ------------------------------------------------------------------------

    private static void ProcessDirective(Term body, ref string moduleName,
        HashSet<PredicateRef> publicSet,
        HashSet<PredicateRef> dynamicSet,
        List<PredicateRef> ensureLinked)
    {
        if (body is CompoundTerm m && m.Functor == "module" && m.Args.Length == 1
            && m.Args[0] is AtomTerm a)
        {
            moduleName = a.Name;
            return;
        }
        if (body is CompoundTerm pub && pub.Functor == "public" && pub.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(pub.Args[0], "public"))
                publicSet.Add(spec);
            return;
        }
        if (body is CompoundTerm dyn && dyn.Functor == "dynamic" && dyn.Args.Length == 1)
        {
            foreach (var spec in ReadFunctorSpecs(dyn.Args[0], "dynamic"))
                dynamicSet.Add(spec);
            return;
        }
        // ensure_linked/1 is parsed by chunk 162. Other directives
        // (op/3, set_prolog_flag, etc.) are ignored by the shmo writer
        // — they don't affect link-time semantics.
    }

    private static IEnumerable<PredicateRef> ReadFunctorSpecs(Term arg, string directive)
    {
        if (TryReadFunctorSpec(arg, out var single))
        {
            yield return single;
            yield break;
        }
        // List of Name/Arity.
        Term cursor = arg;
        var collected = new List<PredicateRef>();
        while (cursor is CompoundTerm cons && cons.Functor == "." && cons.Args.Length == 2)
        {
            if (!TryReadFunctorSpec(cons.Args[0], out var spec))
                throw new InvalidOperationException(
                    $"Malformed :- {directive} directive (expected Name/Arity or a list of them).");
            collected.Add(spec);
            cursor = cons.Args[1];
        }
        if (cursor is AtomTerm { Name: "[]" })
        {
            foreach (var s in collected) yield return s;
            yield break;
        }
        throw new InvalidOperationException(
            $"Malformed :- {directive} directive (expected Name/Arity or a list of them).");
    }

    private static bool TryReadFunctorSpec(Term term, out PredicateRef spec)
    {
        if (term is CompoundTerm slash && slash.Functor == "/" && slash.Args.Length == 2
            && slash.Args[0] is AtomTerm name && slash.Args[1] is IntTerm arity)
        {
            spec = new PredicateRef(name.Name, (int)arity.Value);
            return true;
        }
        spec = default;
        return false;
    }

    // ------------------------------------------------------------------------
    // Clause head extraction
    // ------------------------------------------------------------------------

    private static PredicateRef? TryExtractHead(Clause c)
    {
        Term headTerm = c.Kind == ClauseKind.Rule
            && c.Term is CompoundTerm rule
            && rule.Functor == ":-" && rule.Args.Length == 2
                ? rule.Args[0]
                : c.Term;

        return headTerm switch
        {
            AtomTerm at => new PredicateRef(at.Name, 0),
            CompoundTerm ct => new PredicateRef(ct.Functor, ct.Args.Length),
            _ => null,
        };
    }

    private static Term ExtractBody(Clause c)
    {
        if (c.Kind == ClauseKind.Rule
            && c.Term is CompoundTerm rule
            && rule.Functor == ":-" && rule.Args.Length == 2)
        {
            return rule.Args[1];
        }
        return new AtomTerm("true");
    }

    // ------------------------------------------------------------------------
    // Body walking — extract every call site
    // ------------------------------------------------------------------------

    private static void CollectCalls(Term body,
        HashSet<PredicateRef> edges,
        List<QualifiedPredicateRef> qualifiedRefs)
    {
        switch (body)
        {
            case CompoundTerm c:
                // Conjunction / disjunction / if-then / soft cut / not-provable
                // — control structures, descend into args but emit nothing.
                if ((c.Functor == "," || c.Functor == ";" || c.Functor == "->"
                     || c.Functor == "*->" ) && c.Args.Length == 2)
                {
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    CollectCalls(c.Args[1], edges, qualifiedRefs);
                    return;
                }
                if ((c.Functor == "\\+" || c.Functor == "not") && c.Args.Length == 1)
                {
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    return;
                }
                // Module-qualified goal: Module:Goal. Emit a qualified
                // ref (resolved against that module's public set by the
                // linker) and don't add the goal to the unqualified
                // edges — it's not a free reference.
                if (c.Functor == ":" && c.Args.Length == 2
                    && c.Args[0] is AtomTerm modAtom)
                {
                    AddQualifiedCallTarget(modAtom.Name, c.Args[1], qualifiedRefs);
                    return;
                }
                // call/1 with a statically known goal: descend.
                if (c.Functor == "call" && c.Args.Length == 1)
                {
                    CollectCalls(c.Args[0], edges, qualifiedRefs);
                    return;
                }
                // Anything else is a direct call site — emit name/arity.
                edges.Add(new PredicateRef(c.Functor, c.Args.Length));
                return;

            case AtomTerm a:
                // Cut is structural — not a call.
                if (a.Name == "!") return;
                // Atom as goal — name/0.
                edges.Add(new PredicateRef(a.Name, 0));
                return;

            // Numbers / strings / vars as goals are call/1 fodder — at
            // shmo time we have no way to resolve them; the user must
            // declare :- ensure_linked/1 for any predicate reachable
            // only via runtime meta-call.
            default:
                return;
        }
    }

    private static void AddQualifiedCallTarget(string module, Term goal,
        List<QualifiedPredicateRef> qrefs)
    {
        switch (goal)
        {
            case AtomTerm a:
                if (a.Name != "!")
                    qrefs.Add(new QualifiedPredicateRef(module, a.Name, 0));
                return;
            case CompoundTerm c:
                qrefs.Add(new QualifiedPredicateRef(module, c.Functor, c.Args.Length));
                return;
        }
    }
}

using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

/// <summary>
/// High-level entry point for embedding Shumway in a .NET host. Accumulates
/// consulted Prolog source, then satisfies queries by compiling the source and
/// the query into a single bytecode program, running it through the
/// interpreter, and reading back the resulting variable bindings.
///
/// <para>This is the chunk-9 MVP: every <see cref="Query"/> compiles, links and
/// runs from scratch on a fresh engine. That's adequate for tests and for
/// experimenting interactively, but the embedding ADR's full API surface
/// (engine pooling, multi-solution streaming, foreign predicates, struct
/// mapping, etc.) is not yet built. Single-solution queries are all that's
/// supported here — to get the next solution today you'd have to add a
/// failure-after-success goal explicitly.</para>
/// </summary>
public sealed class PrologEngine
{
    private string _accumulatedSource = "";
    private readonly OperatorTable _operators = OperatorTable.Default();

    /// <summary>The sink that I/O builtins (<c>write/1</c>, <c>nl/0</c>,
    /// <c>writeln/1</c>) write into. Defaults to <see cref="System.Console.Out"/>;
    /// swap in a <see cref="System.IO.StringWriter"/> to capture program
    /// output in tests.</summary>
    public System.IO.TextWriter Out { get; set; } = Console.Out;

    public PrologEngine()
    {
        // The standard builtins (=/2, ==/2, etc.) need to be registered before
        // the WAM compiler can recognise them. EnsureRegistered is idempotent.
        Shumway.Builtins.StandardBuiltins.EnsureRegistered();
    }

    /// <summary>Loads Prolog source. Multiple calls accumulate — later consults
    /// see the operator declarations from earlier ones. The source is stored
    /// verbatim and re-parsed on every query.
    ///
    /// <para>The call drives the source through <see cref="ClauseReader"/> once
    /// up front, which executes any <c>:- op</c> directives so the operator
    /// table is up-to-date for a subsequent <see cref="Query"/> on the
    /// just-consulted operators. The parsed clauses are otherwise discarded;
    /// the final compile happens at query time.</para></summary>
    public void ConsultString(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Run the source through the reader to apply :- op directives — the
        // ReadAll().ToList() materialises the iterator so directives are
        // processed immediately even though we don't keep the clauses here.
        _ = new ClauseReader(new Lexer(source), _operators).ReadAll().ToList();
        _accumulatedSource += "\n" + source;
    }

    /// <summary>Parses and runs a query, returning the first solution if one
    /// exists or a failed <see cref="Solution"/> otherwise. Equivalent to
    /// <c>QueryAll(queryText).FirstOrDefault(failed)</c>.</summary>
    public Solution Query(string queryText)
    {
        foreach (var sol in QueryAll(queryText))
            return sol;
        return new Solution(success: false, bindings: ImmutableDictionary<string, Term>.Empty);
    }

    /// <summary>Parses and runs a query, lazily yielding every solution. The
    /// engine state is preserved between yields so the iterator can drive the
    /// interpreter through backtracking on demand.
    ///
    /// <para>Common idioms:</para>
    /// <list type="bullet">
    /// <item><c>engine.QueryAll("p(X).").First()</c> — first solution.</item>
    /// <item><c>engine.QueryAll("p(X).").ToList()</c> — all solutions
    ///   enumerated eagerly.</item>
    /// <item><c>engine.QueryAll("p(X).").Count()</c> — how many succeed.</item>
    /// <item><c>foreach (var s in engine.QueryAll("p(X).")) …</c> — iterate.</item>
    /// </list></summary>
    public IEnumerable<Solution> QueryAll(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);

        var (program, varNames, varHeapIndices, engine, interp) = SetupQuery(queryText);

        var result = interp.Run(program, 0);
        while (result == InterpreterResult.Halted)
        {
            yield return BuildSolution(varNames, varHeapIndices, engine);
            result = interp.Backtrack(program);
        }
    }

    /// <summary>Compiles the consulted source + the query's synthetic wrapper,
    /// links into a runnable program prefixed by a "call wrapper; halt"
    /// launcher, allocates fresh heap unbounds for the query's variables and
    /// stores them in X[0..n-1]. Returns everything the iterator needs to
    /// run and re-run the program.</summary>
    private (byte[] Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Engine Engine,
             BytecodeInterpreter Interp) SetupQuery(string queryText)
    {
        var queryParser = new Parser(new Lexer(queryText), _operators);
        Term queryTerm = queryParser.ReadClauseTerm();

        var varNames = new List<string>();
        var seen = new HashSet<string>();
        CollectVariables(queryTerm, varNames, seen);

        const string queryFunctor = "__query__";
        Term head = varNames.Count == 0
            ? new AtomTerm(queryFunctor)
            : new CompoundTerm(
                queryFunctor,
                varNames.Select(n => (Term)new VarTerm(n)).ToArray());
        Term clauseTerm = new CompoundTerm(":-", new[] { head, queryTerm });
        var syntheticClause = new Clause(ClauseKind.Rule, clauseTerm, queryTerm.Position);

        var allClauses = string.IsNullOrEmpty(_accumulatedSource)
            ? new List<Clause>()
            : new ClauseReader(new Lexer(_accumulatedSource), _operators).ReadAll().ToList();
        allClauses.Add(syntheticClause);
        var module = new ModuleCompiler().Compile(allClauses);

        var launcher = new BytecodeEmitter();
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        byte[] prefix = launcher.ToBytes();

        var linkResult = new Linker().Link(module, loadOffset: prefix.Length);
        int queryFunctorId = FunctorTable.Intern(
            AtomTable.Intern(queryFunctor, permanent: true).Id,
            varNames.Count);
        BytecodeIO.WriteInt32(prefix, callPos + 1, linkResult.Addresses[queryFunctorId]);

        byte[] program = new byte[prefix.Length + linkResult.Bytecode.Length];
        Array.Copy(prefix, program, prefix.Length);
        Array.Copy(linkResult.Bytecode, 0, program, prefix.Length, linkResult.Bytecode.Length);

        var engine = new Engine { Out = Out };
        var interp = new BytecodeInterpreter(engine);

        int[] varHeapIndices = new int[varNames.Count];
        for (int i = 0; i < varNames.Count; i++)
        {
            int h = engine.AllocateHeapUnbound();
            varHeapIndices[i] = h;
            engine.SetRegister(i, Cell.Ref(h));
        }

        return (program, varNames, varHeapIndices, engine, interp);
    }

    private static Solution BuildSolution(
        List<string> varNames, int[] varHeapIndices, Engine engine)
    {
        var bindings = new Dictionary<string, Term>(varNames.Count);
        for (int i = 0; i < varNames.Count; i++)
            bindings[varNames[i]] = TermReader.Materialize(engine, varHeapIndices[i]);
        return new Solution(success: true, bindings: bindings);
    }

    private static void CollectVariables(Term term, List<string> order, HashSet<string> seen)
    {
        switch (term)
        {
            case VarTerm v when v.Name != "_":
                if (seen.Add(v.Name)) order.Add(v.Name);
                break;
            case CompoundTerm c:
                foreach (Term arg in c.Args)
                    CollectVariables(arg, order, seen);
                break;
        }
    }
}

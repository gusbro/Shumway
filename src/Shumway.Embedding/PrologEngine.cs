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

    /// <summary>Parses and runs a query. The query text must be a single
    /// clause-terminated goal (e.g. <c>"p(X)."</c> or <c>"p(X), q(Y)."</c>).
    /// Returns the first solution if there is one, or a failed
    /// <see cref="Solution"/> otherwise.</summary>
    public Solution Query(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);

        // 1. Parse the query (with the operator table that's been accumulating
        //    via consulted :- op directives).
        var queryParser = new Parser(new Lexer(queryText), _operators);
        Term queryTerm = queryParser.ReadClauseTerm();

        // 2. Collect query variables in first-occurrence order. Anonymous
        //    variables (_) are skipped — each occurrence is a fresh distinct
        //    variable that the caller can't name.
        var varNames = new List<string>();
        var seen = new HashSet<string>();
        CollectVariables(queryTerm, varNames, seen);

        // 3. Synthesize a clause: __query__(V1, ..., Vn) :- queryBody.
        const string queryFunctor = "__query__";
        Term head = varNames.Count == 0
            ? new AtomTerm(queryFunctor)
            : new CompoundTerm(
                queryFunctor,
                varNames.Select(n => (Term)new VarTerm(n)).ToArray());
        Term clauseTerm = new CompoundTerm(":-", new[] { head, queryTerm });
        var syntheticClause = new Clause(ClauseKind.Rule, clauseTerm, queryTerm.Position);

        // 4. Compile everything (consulted source + synthetic clause) into one
        //    module. Re-parsing the consulted source applies the :- op
        //    directives idempotently.
        var allClauses = string.IsNullOrEmpty(_accumulatedSource)
            ? new List<Clause>()
            : new ClauseReader(new Lexer(_accumulatedSource), _operators).ReadAll().ToList();
        allClauses.Add(syntheticClause);
        var module = new ModuleCompiler().Compile(allClauses);

        // 5. Generate a launcher: "call __query__/n; halt".
        var launcher = new BytecodeEmitter();
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        byte[] prefix = launcher.ToBytes();

        // 6. Link, with loadOffset set so every address already accounts for
        //    the launcher prefix.
        var linkResult = new Linker().Link(module, loadOffset: prefix.Length);
        int queryFunctorId = FunctorTable.Intern(
            AtomTable.Intern(queryFunctor, permanent: true).Id,
            varNames.Count);
        BytecodeIO.WriteInt32(prefix, callPos + 1, linkResult.Addresses[queryFunctorId]);

        byte[] program = new byte[prefix.Length + linkResult.Bytecode.Length];
        Array.Copy(prefix, program, prefix.Length);
        Array.Copy(linkResult.Bytecode, 0, program, prefix.Length, linkResult.Bytecode.Length);

        // 7. Set up X[0..n-1] = REFs to fresh heap unbounds, one per query
        //    variable. The heap indices are remembered so we can materialise
        //    the final bindings after the run, even though X[0..n-1] may be
        //    clobbered by the callee.
        var engine = new Engine();
        var interp = new BytecodeInterpreter(engine);

        int[] varHeapIndices = new int[varNames.Count];
        for (int i = 0; i < varNames.Count; i++)
        {
            int h = engine.AllocateHeapUnbound();
            varHeapIndices[i] = h;
            engine.SetRegister(i, Cell.Ref(h));
        }

        // 8. Run.
        var result = interp.Run(program, 0);
        if (result == InterpreterResult.Failed)
            return new Solution(success: false, bindings: ImmutableDictionary<string, Term>.Empty);

        // 9. Materialise each variable's binding by walking the heap from the
        //    remembered head index.
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

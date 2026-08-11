using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>Runs an AST goal through the same machinery as the string
    /// form, yielding each solution in turn. The free variables of
    /// <paramref name="goal"/> show up in <see cref="Solution.Bindings"/>
    /// under the names they carry in the AST (synthetic <c>_GN</c> names if
    /// the term came from <see cref="TermReader.Materialize"/>).</summary>
    public IEnumerable<Solution> QueryAll(Term goal)
    {
        ArgumentNullException.ThrowIfNull(goal);
        LastHaltExitCode = null;
        var setup = SetupQueryFromTerm(goal);
        return RunIteration(this, setup.Program, setup.VarNames, setup.VarHeapIndices,
            setup.Activation, setup.Interp);
    }

    /// <summary>As <see cref="QueryAll(Term)"/> but the supplied
    /// <paramref name="cancellationToken"/> aborts a long-running search at the
    /// next safe point — the engine throws <see cref="OperationCanceledException"/>
    /// (it bubbles past any surrounding <c>catch/3</c>). Runs on the calling
    /// thread; fire the token from another thread (e.g. a key watcher).</summary>
    public IEnumerable<Solution> QueryAll(Term goal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(goal);
        LastHaltExitCode = null;
        var setup = SetupQueryFromTerm(goal);
        return RunIterationCancellable(setup, cancellationToken);
    }

    /// <summary>Drives the interpreter's run / backtrack loop and yields a
    /// <see cref="Solution"/> at each <see cref="InterpreterResult.Halted"/>
    /// outcome. A <see cref="PrologHaltException"/> ends the iteration
    /// gracefully (the user invoked <c>halt/0</c> or <c>halt/1</c>) — the
    /// embedding caller stops seeing further solutions rather than a .NET
    /// exception propagating out of their <c>foreach</c>.</summary>
    private static IEnumerable<Solution> RunIteration(
        PrologEngine host,
        Shumway.Core.ProgramView program,
        List<string> varNames,
        int[] varHeapIndices,
        Activation engine,
        BytecodeInterpreter interp)
    {
        // Heap-buffer pool: the finally runs exactly when the activation
        // dies — enumeration completed, disposed early (foreach break /
        // Query taking one solution), or unwound by an exception — and NOT
        // on suspension between yields. Solutions hold materialized AST
        // terms, never references into the surrendered buffer.
        try
        {
            InterpreterResult result;
            bool halted = false;
            // Choice-point level before the query runs. After a solution, if
            // the engine's B has fallen back to (or below) this, no
            // query-local choice point remains — the solution is the last
            // one. Lets the top-level skip the `;` prompt + trailing
            // `false` for a deterministic goal, matching other Prologs.
            int baseB = engine.B;
            try { result = host.RunCatching(interp, program, engine, () => interp.Run(program, 0)); }
            catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; result = InterpreterResult.Failed; }
            catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
            catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }

            while (!halted && result == InterpreterResult.Halted)
            {
                bool isLast = engine.B <= baseB;
                // ADR-035 — the machine is about to hand the answer back and stand still.
                // A step still in flight can never be satisfied from here (no port is
                // coming until somebody asks for another solution), and a debugger left
                // waiting for it thinks the program is running.
                host.DebugSession?.OnLeaveProlog(engine);
                yield return BuildSolution(varNames, varHeapIndices, engine, isLast, host);
                // A known-last solution: don't backtrack — there's nothing
                // to find and re-running would just confirm failure.
                if (isLast) break;
                try { result = host.RunCatching(interp, program, engine, () => interp.Backtrack(program)); }
                catch (PrologHaltException hex) { halted = true; host.LastHaltExitCode = hex.ExitCode; break; }
                catch (ShumwayPrologException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
                catch (PrologRuntimeException) { { var st = host.CaptureStackTrace(engine); host.LastErrorStackTrace = st.Plain; host.LastErrorStackTraceWithPositions = st.WithPositions; throw; } }
            }
        }
        finally
        {
            // The activation is dead — the query failed, ran out of solutions, or the
            // caller stopped asking. Same as a yield, and more final.
            // setup_call_cleanup/3: fire any cleanup whose scope is abandoned by
            // the teardown (a caller that stopped with choice points still live —
            // the SWI toplevel-cancel case). Runs BEFORE the heap buffer is
            // surrendered so the cleanup goal still has its heap.
            if (engine.HasCleanupHandlers || engine.HasPendingCleanups)
                interp.RunTeardownCleanups(program);
            host.DebugSession?.OnLeaveProlog(engine);
            host._heapPool.Return(engine);
        }
    }

    /// <summary>Runs an interpreter step, intercepting <c>throw/1</c> for
    /// in-engine <c>catch/3</c>. When the thrown ball unifies
    /// with the catcher of an active catch frame, the engine rolls back to
    /// that frame and resumes at its recovery goal; the loop repeats if
    /// recovery (or the continuation) throws again. A ball that no frame
    /// catches propagates unchanged. A Core <see cref="PrologRuntimeException"/>
    /// is funnelled through the same path as its ISO <c>error/2</c> term.</summary>
    private InterpreterResult RunCatching(
        BytecodeInterpreter interp, Shumway.Core.ProgramView program, Activation engine,
        Func<InterpreterResult> action)
    {
        Func<InterpreterResult> step = action;
        while (true)
        {
            try
            {
                return step();
            }
            catch (ShumwayPrologException ex)
            {
                int addr = TryCatch(engine, ex.Term, out _);
                if (addr < 0) throw;
                if (CatchDiag)
                    Console.Error.WriteLine(
                        $"[CATCH-RESUME] ball={ex.Term} addr=0x{addr:X}");
                step = () => interp.Run(program, addr);
            }
            catch (PrologRuntimeException ex)
            {
                Term ball = MetaBuiltins.TranslateRuntimeError(ex);
                int addr = TryCatch(engine, ball, out bool insideCatch);
                if (addr >= 0)
                    step = () => interp.Run(program, addr);
                else if (insideCatch)
                    // It passed through a catch (just no catcher matched),
                    // so it propagates as the Prolog-visible error/2 term.
                    throw new ShumwayPrologException(ball);
                else
                    // No catch at all — keep the raw Core exception.
                    throw;
            }
        }
    }

    /// <summary>Walks the catch-frame stack from the innermost frame out,
    /// trial-unifying <paramref name="ballTerm"/> with each active frame's
    /// catcher. On the first match it rolls the machine back to that frame,
    /// binds the catcher to the ball for real, loads the recovery goal's
    /// arguments into the registers, and returns the recovery predicate's
    /// code address. Returns -1 when no frame catches the ball;
    /// <paramref name="hadActiveFrame"/> then reports whether any active
    /// catch frame was seen at all (it was just a catcher mismatch) — used
    /// to decide whether an uncaught runtime error keeps its raw form.</summary>
    internal static readonly bool CatchDiag =
        System.Environment.GetEnvironmentVariable("SHUMWAY_CATCH_DIAG") == "1";

    private static int TryCatch(Activation engine, Term ballTerm, out bool hadActiveFrame)
        => TryCatchFrom(engine, ballTerm, 0, out hadActiveFrame);

    /// <summary>The <see cref="TryCatch"/> walk restricted to frames at or
    /// above <paramref name="minFrameIndex"/> — the nested in-engine goal
    /// driver (a wakeup, a findall body) may only resolve balls against
    /// frames opened INSIDE itself; anything older belongs to an outer
    /// driver's scope.</summary>
    private static int TryCatchFrom(
        Activation engine, Term ballTerm, int minFrameIndex, out bool hadActiveFrame)
    {
        hadActiveFrame = false;
        for (int i = engine.CatchFrameCount - 1; i >= minFrameIndex; i--)
        {
            CatchFrame frame = engine.GetCatchFrame(i);
            if (!frame.Active) continue;
            hadActiveFrame = true;

            // Speculatively unify the ball with the catcher, then undo —
            // testing the match must not disturb the machine. That includes
            // the WAKEUP QUEUE: a catcher containing attributed variables
            // queues verify_attributes wakeups during the trial, and their
            // recorded heap indices point into the trial region truncated
            // right below — flushing them later read garbage cells (clpz's
            // all_distinct crashed on a phantom functor id).
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            int savedWakeups = engine.PendingWakeupCount;
            engine.SetHb(engine.HeapTop);
            Cell trialBall = Materializer.MaterializeAsCell(engine, ballTerm);
            bool matched = engine.UnifyHeapWithCell(frame.CatcherHeapIdx, trialBall);
            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
            engine.TruncatePendingWakeups(savedWakeups);
            if (!matched) continue;

            // Commit: roll back everything the guarded goal did, then bind
            // the catcher to the ball for keeps and prime the recovery call.
            engine.UnwindToCatchFrame(i);
            Cell ball = Materializer.MaterializeAsCell(engine, ballTerm);
            engine.UnifyHeapWithCell(frame.CatcherHeapIdx, ball);
            return SetupRecoveryCall(engine, frame.RecoveryHeapIdx);
        }
        return -1;
    }

    /// <summary>Decodes the recovery goal cell — a <c>'$catchrec_N'(Vars)</c>
    /// helper call — into argument registers and returns its code address,
    /// so the interpreter can be re-entered to run the recovery.</summary>
    private static int SetupRecoveryCall(Activation engine, int recoveryHeapIdx)
    {
        Cell goal = engine.GetHeap(recoveryHeapIdx);
        if (goal.Tag == Tag.Ref)
            goal = engine.GetHeap(engine.Deref(goal.AsHeapIndex));

        int functorId;
        int argBase;
        int arity;
        if (goal.Tag == Tag.Atom)
        {
            functorId = FunctorTable.Intern(goal.AsAtomId, 0);
            arity = 0;
            argBase = -1;
        }
        else if (goal.Tag == Tag.Str)
        {
            int functorIdx = goal.AsHeapIndex;
            functorId = engine.GetHeap(functorIdx).AsFunctorId;
            (_, arity) = FunctorTable.Lookup(functorId);
            argBase = functorIdx + 1;
        }
        else
        {
            // ISO §8.15.3.3 — a non-callable Recovery goal
            // is type_error(callable, Recovery); an unbound one is
            // instantiation_error.
            if (goal.Tag == Tag.Ref)
                throw new Shumway.Core.PrologRuntimeException("instantiation_error");
            throw new Shumway.Core.PrologRuntimeException("type_error", "callable");
        }

        for (int i = 0; i < arity; i++)
            engine.SetRegister(i, engine.GetHeap(argBase + i));

        var addresses = engine.CurrentFunctorAddresses;
        if (addresses is not null && addresses.TryGetValue(functorId, out int address))
            return address;
        // Last chance: a '$catchrec_N' compiled by a DIFFERENT activation's
        // assert/setup (the Logtalk suspended-outer-query shape) — materialize
        // it into this one. See TryMaterializeAssertHelper.
        int late = engine.ResolveLateHelper?.Invoke(functorId) ?? -1;
        if (late >= 0) return late;
        throw new InvalidOperationException(
            "catch/3 recovery helper predicate has no compiled address.");
    }

    /// <summary>Parses and runs a query, returning the first solution if one
    /// exists or a failed <see cref="Solution"/> otherwise. Equivalent to
    /// <c>QueryAll(queryText).FirstOrDefault(failed)</c>.</summary>
    public Solution Query(string queryText)
    {
        foreach (var sol in QueryAll(queryText))
            return sol;
        return new Solution(success: false, bindings: ImmutableDictionary<string, Term>.Empty,
            engine: this);
    }

    /// <summary>typed-result query: runs the query and
    /// projects the binding of <paramref name="variableName"/>
    /// through <see cref="FromTerm{T}"/> for every solution. The
    /// natural shape for a query that asks for one specific value
    /// out of each answer (the typical embedding-side
    /// "give me all the X such that p(X)" use case).
    /// <c>foreach (var x in engine.Query&lt;int&gt;("p(X).", "X")) ...</c></summary>
    public IEnumerable<T> Query<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] T>(string queryText, string variableName)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentNullException.ThrowIfNull(variableName);
        foreach (var sol in QueryAll(queryText))
        {
            if (!sol.Bindings.TryGetValue(variableName, out var t))
                throw new InvalidOperationException(
                    $"Query<T> asked for variable '{variableName}' but the query "
                    + $"does not bind it. Bound variables: "
                    + (sol.Bindings.Count == 0
                        ? "(none)"
                        : string.Join(", ", sol.Bindings.Keys)));
            yield return FromTerm<T>(t);
        }
    }

    /// <summary>single-variable overload: when the
    /// query has exactly one non-anonymous variable, infer the
    /// name. Useful for the common
    /// <c>engine.Query&lt;int&gt;("between(1, 5, X).")</c> idiom
    /// where naming the variable in C# is just noise.
    ///
    /// <para>Throws when the query has zero variables (a yes/no
    /// query — use <see cref="QueryAll(string)"/>) or more than
    /// one variable (the explicit-name overload disambiguates).</para></summary>
    public IEnumerable<T> Query<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] T>(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        // Parse once to discover the query's variable set, then defer
        // to the explicit-name overload. The Term parse here costs an
        // extra walk, but it's a one-shot setup pass — the iteration
        // dwarfs it for any non-trivial query.
        var queryParser = new Parser(
            new Lexer(queryText, _flags.CharConversionEnabled ? _flags.CharConversion : null),
            _operators, _flags);
        Term queryTerm = queryParser.ReadClauseTerm();
        var vars = new List<string>();
        var seen = new HashSet<string>();
        CollectVariables(queryTerm, vars, seen);
        if (vars.Count == 0)
            throw new InvalidOperationException(
                $"engine.Query<{typeof(T).Name}>(\"{queryText}\") has no variables — "
                + "use QueryAll(string) for boolean queries, or add the variable to "
                + "extract.");
        if (vars.Count > 1)
            throw new InvalidOperationException(
                $"engine.Query<{typeof(T).Name}>(\"{queryText}\") has multiple variables "
                + $"({string.Join(", ", vars)}); use the (queryText, variableName) "
                + "overload to disambiguate.");
        return Query<T>(queryText, vars[0]);
    }

    /// <summary>runs the query and returns the first
    /// solution's binding of <paramref name="variableName"/>
    /// projected through <see cref="FromTerm{T}"/>; <c>default</c>
    /// (a null reference / zero value) when the query fails. Drops
    /// the remaining solutions; the engine state is unaffected (the
    /// underlying iterator handles disposal).</summary>
    public T? QueryFirst<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] T>(string queryText, string variableName)
    {
        foreach (var v in Query<T>(queryText, variableName))
            return v;
        return default;
    }

    /// <summary>single-variable overload of
    /// <see cref="QueryFirst{T}(string,string)"/>.</summary>
    public T? QueryFirst<[System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(ConventionConverters.ConventionMembers)] T>(string queryText)
    {
        foreach (var v in Query<T>(queryText))
            return v;
        return default;
    }

    /// <summary>Parses and runs a query, lazily yielding every solution. The
    /// engine state is preserved between yields so the iterator can drive the
    /// interpreter through backtracking on demand.</summary>
    public IEnumerable<Solution> QueryAll(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        LastHaltExitCode = null;
        var setup = SetupQuery(queryText);
        return RunIteration(this, setup.Program, setup.VarNames, setup.VarHeapIndices,
            setup.Activation, setup.Interp);
    }

    /// <summary>Theme 2 — a cancellable lazy solution stream. Identical to
    /// <see cref="QueryAll(string)"/> but the supplied
    /// <paramref name="cancellationToken"/> aborts a long-running search: the
    /// interpreter observes the request the next time the heap GC watermark is
    /// crossed (so the common per-goal path pays nothing — a heap-bounded loop
    /// such as <c>repeat, fail</c> is not cancellable) and throws
    /// <see cref="OperationCanceledException"/> (NOT a Prolog ball — a
    /// surrounding <c>catch/3</c> never intercepts it). Still synchronous: it
    /// runs on the calling thread. Use <see cref="QueryAsync"/> to run off-thread.</summary>
    public IEnumerable<Solution> QueryAll(string queryText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        LastHaltExitCode = null;
        var setup = SetupQuery(queryText);
        return RunIterationCancellable(setup, cancellationToken);
    }

    private IEnumerable<Solution> RunIterationCancellable(
        (Shumway.Core.ProgramView Program, List<string> VarNames, int[] VarHeapIndices,
         Activation Activation, BytecodeInterpreter Interp) setup,
        CancellationToken cancellationToken)
    {
        using var reg = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static e => ((Activation)e!).RequestCancellation(), setup.Activation)
            : default;
        foreach (var sol in RunIteration(this, setup.Program, setup.VarNames,
                     setup.VarHeapIndices, setup.Activation, setup.Interp))
            yield return sol;
    }

    /// <summary>Theme 2 — an asynchronous, cancellable solution stream. Drives
    /// the (synchronous, CPU-bound) search on a thread-pool thread so the
    /// caller's thread is free between solutions, and surfaces results via
    /// <c>await foreach</c>. Cancellation works as in
    /// <see cref="QueryAll(string, CancellationToken)"/> — the engine aborts at
    /// the next heap GC watermark crossing. One query at a time per engine; pair
    /// with <see cref="EnginePool"/> for concurrency.</summary>
    public async IAsyncEnumerable<Solution> QueryAsync(
        string queryText,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        LastHaltExitCode = null;
        var setup = SetupQuery(queryText);
        using var reg = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(static e => ((Activation)e!).RequestCancellation(), setup.Activation)
            : default;
        using var iter = RunIteration(this, setup.Program, setup.VarNames,
            setup.VarHeapIndices, setup.Activation, setup.Interp).GetEnumerator();
        while (true)
        {
            // Each MoveNext runs one Run/Backtrack step off the calling thread.
            // The engine is thread-agile and the steps are awaited (never
            // overlapping), so a different pool thread per step is sound.
            bool has = await Task.Run(() => iter.MoveNext(), cancellationToken).ConfigureAwait(false);
            if (!has) break;
            yield return iter.Current;
        }
    }

    private (Shumway.Core.ProgramView Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Activation Activation,
             BytecodeInterpreter Interp) SetupQuery(string queryText)
    {
        var queryParser = new Parser(
            new Lexer(queryText, _flags.CharConversionEnabled ? _flags.CharConversion : null),
            _operators, _flags);
        Term queryTerm = queryParser.ReadClauseTerm();
        return SetupQueryFromTerm(queryTerm);
    }

    /// <summary>Parses <paramref name="queryText"/> as a goal and returns
    /// the parsed AST plus the list of distinct named variables in order
    /// of first occurrence. Lets a top-level synthesise a wrapped goal
    /// (e.g. appending <c>copy_term/3</c> to extract residual
    /// constraints) over the same variables before calling
    /// <see cref="QueryAll(Term)"/>.</summary>
    public (Term Goal, IReadOnlyList<string> VarNames) ParseGoal(string queryText)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        var queryParser = new Parser(
            new Lexer(queryText, _flags.CharConversionEnabled ? _flags.CharConversion : null),
            _operators, _flags);
        Term queryTerm = queryParser.ReadClauseTerm();
        var names = new List<string>();
        CollectVariables(queryTerm, names, new HashSet<string>());
        return (queryTerm, names);
    }

    /// <summary>Shared workhorse used by both the string-parsing
    /// <see cref="SetupQuery(string)"/> and the Term-level
    /// <see cref="QueryAll(Term)"/>: gathers every module's clauses through
    /// DCG / meta / module-mangle transforms, wraps the goal in a synthetic
    /// clause in the user module, compiles + links, primes X[0..n-1] with
    /// fresh heap unbounds, and hands the lot back to the caller's
    /// run/backtrack iterator.</summary>
    private (Shumway.Core.ProgramView Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Activation Activation,
             BytecodeInterpreter Interp) SetupQueryFromTerm(Term queryTerm)
    {
        // ADR-035 — serialized against the debug session's own thread. A breakpoint can
        // arrive while the engine is IDLE (F9 at the prompt), and the session's idle
        // watcher applies it from its own thread — which raced this method's table
        // rebuild the moment a query started at the same instant, and a Dictionary read
        // concurrent with a write throws ConcurrentOperationsNotSupported (seen live: F9
        // followed immediately by a query). Uncontended cost is a fenced check; the
        // watcher takes the same gate in AddBreakpoint/RemoveBreakpoint/ClearBreakpoints.
        lock (_debugArmGate)
            return SetupQueryFromTermUnderGate(queryTerm);
    }

    private (Shumway.Core.ProgramView Program,
             List<string> VarNames,
             int[] VarHeapIndices,
             Activation Activation,
             BytecodeInterpreter Interp) SetupQueryFromTermUnderGate(Term queryTerm)
    {
        long profT0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            return SetupQueryFromTermUnderGateCore(queryTerm);
        }
        finally
        {
            if (LoadProfEnabled)
            {
                ProfSetupTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profT0;
                ProfSetupCalls++;
            }
        }
    }

    /// <summary>Config for query-setup activations: TINY initial heap/stack —
    /// <see cref="HeapBufferPool.Adopt"/> (called right after construction)
    /// supplies the real buffers, recycled across activations or allocated at
    /// default size when the pool is empty. The constructor's default-size
    /// buffers were zeroed per query and immediately discarded on adoption
    /// (~600 KB of pure allocation churn per QueryAll).</summary>
    private static readonly Shumway.Core.ActivationConfig PooledActivationConfig = new()
    {
        InitialHeapSize = 16,
        InitialStackSize = 16,
    };

    private (Shumway.Core.ProgramView Program, List<string> VarNames, int[] VarHeapIndices,
             Shumway.Core.Activation Engine,
             BytecodeInterpreter Interp) SetupQueryFromTermUnderGateCore(Term queryTerm)
    {
        long profPl0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        // auto-compaction. When the accumulated
        // mutation count crosses the watermark, invalidate the
        // persistent buffer here at query setup (the safe point —
        // no in-flight choice points hold addresses into it). The
        // rebuild that follows below picks up the trim automatically.
        //
        // ADR-035 D5 — except during a DEBUG EVALUATION (an Immediate-window goal, a
        // breakpoint condition): that nested query's setup is NOT a safe point — the outer
        // query is suspended mid-flight and everything it holds points into the current
        // buffers. Compaction is paused, not skipped: the counter keeps accumulating and
        // the next real query's setup does the deferred work.
        if (_debugEvalDepth == 0 && _persistentMutationsSinceCompact >= CompactWatermark)
            CompactDynamicCodeBuffer();

        // live-linked-consult forward-reference sites now live on
        // the per-buffer chain table, so they follow their buffer's lifetime
        // automatically (a rebuilt buffer starts a fresh table).

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

        // What the user typed, kept for the call stack's query frame (see AddFrame). Only
        // under a debug session: this renders a term, and nobody else is looking. A host that
        // wrapped the goal says so with QueryLabel — rendering ITS wrapper back would name
        // the frame after machinery the user never wrote.
        CurrentQueryText = DebugSession is null
            ? null
            : QueryLabel ?? AstTermRenderer.Render(queryTerm, 999, _operators);

        // the Phase-19+ implicit_dynamic pre-scan is NO
        // LONGER applied to the query body. Pre-declaring an
        // assertz-target made it observable as an EMPTY dynamic
        // predicate from the query's start, so a goal sequenced BEFORE
        // the assertz in the same query (`catch(call(zzz(1)), _, true),
        // assertz(zzz(1))`) saw it fail instead of raising
        // existence_error — diverging from ISO/SWI and from the same
        // goal without the later assertz. The REPL pattern the pre-scan
        // existed for (`?- assertz(pepe), call(pepe).`) is covered by
        // the runtime path: assertz auto-promotes and
        // materialises a trampoline mid-query; a direct call site's
        // unresolved sentinel re-resolves through
        // ResolveTargetMaybeAutoPromoted and a meta-call probes the
        // live CurrentFunctorAddresses.

        // Validate public uniqueness across modules. The check raises before
        // any compilation so the error message points squarely at the user's
        // module declarations rather than at the bytecode that wouldn't link.
        long profUq0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (LoadProfEnabled) ProfPrologTicks += profUq0 - profPl0;
        ValidatePublicUniqueness();
        if (LoadProfEnabled) ProfUniqTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profUq0;

        // JIT indexing: a dynamic predicate compiles
        // unindexed until its runtime call count crosses the JIT
        // threshold. A cold-but-now-hot predicate (or vice versa) has
        // a stale cached compile at the wrong indexing level — drop it
        // so ModuleCompiler rebuilds it (the drop bumps _programStamp,
        // so the compiled program product below rebuilds too — which is
        // why this loop runs BEFORE the product validity check). The
        // unindexed set then names every dynamic functor still below
        // the threshold.
        long profHt0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var unindexedFunctors = new HashSet<int>();
        bool anyHotnessFlip = false;
        foreach (int fid in _dynStore.Functors)
        {
            if (_jitIndexProfile.HotnessChangedSinceCompile(fid))
            {
                // route through the drop helper so the merged
                // skip-compile cache stays in step.
                DropDynamicPredicateCacheEntry(fid);
                anyHotnessFlip = true;
            }
            if (!_jitIndexProfile.IsHot(fid))
                unindexedFunctors.Add(fid);
        }
        foreach (int fid in _dynStore.ClauseFunctors)
        {
            if (_jitIndexProfile.HotnessChangedSinceCompile(fid))
            {
                DropDynamicPredicateCacheEntry(fid);
                anyHotnessFlip = true;
            }
            if (!_jitIndexProfile.IsHot(fid))
                unindexedFunctors.Add(fid);
        }
        // a cold→hot transition needs the persistent buffer
        // rebuilt so the JIT-promoted indexed compilation actually
        // takes effect at runtime — without this the cache holds the
        // indexed form but the live dispatch still runs the chain
        // emitted at predicate-cold time.
        if (anyHotnessFlip) InvalidatePersistent();
        long profPc0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (LoadProfEnabled) ProfHotnessTicks += profPc0 - profHt0;

        // Pre-compute the fail-stub address — it sits at the end of the
        // launcher prefix, at offset Call(9) + Halt(1) = 10. Both compiles
        // below (program product and query overlay) need it so dynamic
        // predicates emit their last-clause chain instruction with the
        // absolute target.
        int failStubAddr =
            OpcodeTable.Get(Opcode.Call).Size + OpcodeTable.Get(Opcode.Halt).Size;

        // ---- compiled program product ----
        // Everything below up to (and including) the region partition is a
        // pure function of the PROGRAM (static + dynamic clauses), not of the
        // query: compile it once and reuse it until the program changes. The
        // per-query work is then only the synthetic __query__ clause — the
        // "small bootstrap" — instead of an O(program) re-walk per query.
        var product = _programProduct;
        if (product is not null
            && (product.DerivationGen != _derivationGen
                || product.ProgramStamp != _programStamp
                || !ReferenceEquals(product.StaticLinkRef, _staticLink)
                || product.EmitDebugInfo != _flags.EmitDebugInfo
                || product.DebugCodegen != _flags.DebugCodegen))
            product = null;
        if (product is null)
        {
        if (LoadProfEnabled) ProfProductBuilds++;
        long profPb0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // Apply DCG → clause and meta-call (\+ / not) transforms per module,
        // then mangle local functors so each module ends up with its own
        // private namespace. The synthetic query clause is transformed and
        // rewritten under the user module's context but kept out of that
        // module's local set — its head functor stays bare so the launcher
        // can call it by name.
        List<Clause> allRewritten;
        HashSet<int>? userLocalsCache;
        // every module's locals set, for the per-fid dynamic-
        // clause rewrite below (a dynamic clause attributed to module M
        // rewrites under M's context, not user's).
        Dictionary<string, HashSet<int>>? moduleLocalsCache;
        // head functor ids of every clause in allRewritten
        // (static + dynamic program). Maintained alongside the clause
        // list so the stub emission and the cacheableFunctors snapshot
        // below stop re-interning every clause head per query.
        HashSet<int> rewrittenHeadFids;

        // mode specialization. Built once per query setup; the
        // transform appends an implicit cut to every clause of a
        // predicate whose declared modes are all deterministic. Applied
        // after DCG / meta / phrase expansion so it conjoins onto the
        // final plain-rule body.
        var modeTable = Modes;

        // Whether any module's transform actually re-ran this build (vs every
        // module reusing its cached rewrite). Keys the elision-replay cache:
        // unchanged static content + unchanged fid sets ⇒ unchanged decisions.
        bool staticContentChanged = false;
        if (_staticRewriteGen == _derivationGen && _staticRewriteClauses is not null)
        {
            // the static program hasn't changed since the
            // last setup: reuse the transformed + rewritten clause list.
            // Copy it, because stubs and the synthetic query clause are
            // appended below; the Clause objects themselves are immutable
            // ASTs and safe to share across queries.
            allRewritten = new List<Clause>(_staticRewriteClauses);
            userLocalsCache = _staticRewriteUserLocals;
            moduleLocalsCache = _staticRewriteModuleLocals;
            rewrittenHeadFids = new HashSet<int>(_staticRewriteHeadFids!);
        }
        else
        {
            // the derivation is being REGENERATED: MetaTransform
            // helper names ('$disj_N', '$catchgoal_N', ...) come from the
            // global NextMetaHelperId counter, so the fresh ASTs reference
            // fresh helper ids. Any COMPILED artifact from the previous
            // derivation still calls the OLD helper ids — helpers that no
            // longer exist in the new clause set, so the link bakes an
            // undefined sentinel and the call raises existence_error
            // ('$disj_N'/K — the long-standing Logtalk '$disj_95' gap; hit
            // reliably by lgtunit's runtime send-cache asserta, which bumps
            // the derivation between queries). Nothing compiled may outlive
            // the derivation that produced its call sites — but that is a
            // PER-MODULE property, not a whole-program one: a module whose
            // transform is reused verbatim below keeps its helper clauses in
            // allRewritten, so its compiled predicates stay linkable. Only a
            // module that actually REGENERATES mints fresh helper ids, and
            // only ITS compiled predicates are dropped (DropStaticCompiledFids
            // in the loop below — the targeted version of the old blanket
            // _staticPredicateCache.Clear()).
            //  * the linked STATIC REGION (_staticLink) is still dropped — a
            //    dynamic clause recompiled under the new derivation calls new
            //    '$disj_N' helpers, and those helper predicates are STATIC:
            //    they only reach the code space through a fresh static link.
            // The dynamic COMPILED cache still clears wholesale. DO NOT relax
            // this to the rewrite entries' per-entry fingerprint: reusing a
            // dynamic predicate's compiled bytecode across a derivation bump
            // broke clpz's propagators at runtime (constraints silently
            // stopped firing) even though every unit gate stayed green — the
            // rewrite ASTs are safely reusable, the compiled form is not.
            // Recompiling the handful of dynamic predicates from their kept
            // ASTs is cheap.
            _dynamicPredicateCache.Clear();
            _skipCompileMergedCache = null;
            _staticLink = null;
            // The IL tier's promoted delegates are deliberately NOT evicted
            // here: a delegate compiled under the previous derivation calls
            // that derivation's '$disj_N' helper ids, and those stay
            // resolvable forever through the late-helper registry
            // (RegisterLateHelpers + TryMaterializeAssertHelper) — while a
            // blanket eviction would unwire delegates whose resume markers
            // are live in suspended frames ("no IL delegate is bound").
            allRewritten = new List<Clause>();
            userLocalsCache = null;
            moduleLocalsCache = new Dictionary<string, HashSet<int>>();
            foreach (var (name, manifest) in _modules)
            {
                // ADR-035 — the meta-wrapper unfold (below) erases the call to a
                // user control wrapper (ifthenelse/3, ifthen/2), inlining its body as
                // raw ->/;/\+ at the CALLER's position. Under a debug session that is
                // doubly wrong: the wrapper's own clauses take no stop site (nothing
                // ever calls them), so a breakpoint in ifthenelse/3 never binds; and
                // the caller sprouts anonymous control-construct frames (";/2", ",")
                // at its own line instead of a clean step-into. So for a DEBUGGABLE
                // module we keep the wrapper as a real predicate. Opaque modules
                // (prelude, :- disable_debug) run without stop sites anyway, so they
                // still get the optimization.
                bool opaqueModule = _nonDebuggableModules.Contains(name);
                int bundleLocalsCount =
                    _precompiledModuleLocals.TryGetValue(name, out var bundleLocals)
                        ? bundleLocals.Count : 0;
                // Per-module reuse: an unchanged manifest keeps its previous
                // transform verbatim (see ModuleTransformEntry).
                if (_moduleTransformCache.TryGetValue(name, out var mte)
                    && ReferenceEquals(mte.ClausesRef, manifest.Clauses)
                    && ClauseSnapshotMatches(mte.ClauseSnapshot, manifest.Clauses)
                    && mte.PublicCount == manifest.PublicFunctors.Count
                    && mte.ImportCount == manifest.Imports.Count
                    && mte.ExportCount == manifest.ExportFunctors.Count
                    && mte.ModesVersion == modeTable.Version
                    && mte.Opaque == opaqueModule
                    && mte.DebugCodegen == _flags.DebugCodegen
                    && mte.InlineIte == EnableInlineIte
                    && mte.BundleLocalsCount == bundleLocalsCount
                    && QualifiedResolutionsStillValid(mte.QualifiedResolutions))
                {
                    allRewritten.AddRange(mte.Rewritten);
                    if (name == DefaultModuleName) userLocalsCache = mte.Locals;
                    moduleLocalsCache[name] = mte.Locals;
                    continue;
                }
                // This module regenerates: its previous compiled predicates
                // reference helper ids that are about to be re-minted.
                staticContentChanged = true;
                if (LoadProfEnabled)
                    Console.Error.WriteLine(
                        $"[PROF-REGEN] build={ProfProductBuilds} module={name} clauses={manifest.Clauses.Count}"
                        + (mte is null ? " (first)" : $" (was {mte.ClauseSnapshot.Length})"));
                if (mte is not null) DropStaticCompiledFids(mte.HeadFids);
                // module-local meta-wrapper unfold (ifthen/2-style user
                // control wrappers called with statically-known goals become inline
                // if-then-else, eliminating the goal-term build + wrapper frame +
                // runtime meta-dispatch). Runs BEFORE the pipeline so MetaTransform
                // lowers the inserted control constructs. manifest.Clauses is the
                // STATIC clause set (dynamic-head clauses were routed to
                // _dynamicClauses), so a detected wrapper is immutable by invariant.
                var unfolded = (_flags.DebugCodegen && !opaqueModule)
                    ? manifest.Clauses
                    : MetaWrapperUnfold.Apply(manifest.Clauses);
                var transformed = ClausePipeline.Apply(unfolded, modeTable, inlineIte: EnableInlineIte, helperIdProvider: NextMetaHelperId, dcgFailFast: !_flags.DebugCodegen);

                var locals = ComputeLocalFunctors(transformed, manifest.PublicFunctors);
                // fold in the bare local fids contributed by a
                // bundled (precompiled) version of this module — those
                // predicates aren't in manifest.Clauses, so the line above
                // can't see them.
                if (bundleLocals is not null)
                    locals.UnionWith(bundleLocals);
                if (name == DefaultModuleName) userLocalsCache = locals;
                moduleLocalsCache[name] = locals;

                var ctx = new ModuleRewrite.Context(name, locals, _dynStore.Functors, manifest.Imports)
                { QualifiedStaticResolver = ResolveQualifiedStatic };
                // ADR-035 — a library's HELPERS are library code too. MetaTransform
                // lowers control constructs into generated predicates ('$call_conj' and
                // friends), which are not in manifest.Clauses and so cannot be marked at
                // consult time — but they are right here, in `transformed`, and they
                // carry the library's source positions. Left debuggable they would take
                // stop sites at the library's line numbers and attribute them to the
                // user's file. Compiling a module is what makes its predicates; if the
                // module is not debuggable, neither is anything it made.
                var moduleRewritten = new List<Clause>(transformed.Count);
                var moduleHeadFids = new HashSet<int>();
                foreach (var clause in transformed)
                {
                    var rewritten = ModuleRewrite.Rewrite(clause, ctx);
                    allRewritten.Add(rewritten);
                    moduleRewritten.Add(rewritten);
                    moduleHeadFids.Add(HeadFunctorIdOf(rewritten));
                    if (opaqueModule && TryReadClauseHead(rewritten, out var spec))
                        _nonDebuggableFunctors.Add(FunctorTable.Intern(
                            AtomTable.Intern(spec.Name, permanent: true).Id, spec.Arity));
                }
                // The freshly-minted head fids may also carry stale compiled
                // entries (a previous shape of this module under an old name
                // split, or a fid migrating between modules).
                DropStaticCompiledFids(moduleHeadFids);
                _moduleTransformCache[name] = new ModuleTransformEntry
                {
                    ClausesRef = manifest.Clauses,
                    ClauseSnapshot = manifest.Clauses.ToArray(),
                    PublicCount = manifest.PublicFunctors.Count,
                    ImportCount = manifest.Imports.Count,
                    ExportCount = manifest.ExportFunctors.Count,
                    ModesVersion = modeTable.Version,
                    Opaque = opaqueModule,
                    DebugCodegen = _flags.DebugCodegen,
                    InlineIte = EnableInlineIte,
                    BundleLocalsCount = bundleLocalsCount,
                    QualifiedResolutions = ctx.QualifiedResolutions,
                    Rewritten = moduleRewritten,
                    Locals = locals,
                    HeadFids = moduleHeadFids,
                };
            }
            // Modules that vanished (reconsult trim, abolish of a whole file)
            // leave stale compiled entries behind — drop them with their cache
            // rows.
            if (_moduleTransformCache.Count > _modules.Count)
            {
                List<string>? dead = null;
                foreach (var key in _moduleTransformCache.Keys)
                    if (!_modules.ContainsKey(key)) (dead ??= new()).Add(key);
                if (dead is not null)
                    foreach (var key in dead)
                    {
                        staticContentChanged = true;
                        DropStaticCompiledFids(_moduleTransformCache[key].HeadFids);
                        _moduleTransformCache.Remove(key);
                    }
            }
            rewrittenHeadFids = new HashSet<int>();
            foreach (var c in allRewritten)
                rewrittenHeadFids.Add(HeadFunctorIdOf(c));
            // snapshot for the next setup (the transform chain
            // is a pure function of the consulted program; every input
            // mutation bumps _derivationGen).
            _staticRewriteClauses = new List<Clause>(allRewritten);
            _staticRewriteUserLocals = userLocalsCache;
            _staticRewriteModuleLocals = moduleLocalsCache;
            _staticRewriteHeadFids = new HashSet<int>(rewrittenHeadFids);
            _staticRewriteGen = _derivationGen;
        }

        // Dynamic clauses asserted at runtime (or declared
        // `:- dynamic foo/N.` in source, then routed into
        // _dynamicClauses at consult). The dynamic predicate itself
        // sits in the flat global namespace (no module prefix on its
        // head), but a CALL inside the dynamic clause's body to a
        // user-module-local predicate (e.g. `helper/0` from
        // `main :- helper.` when `main` is dynamic and `helper` is a
        // plain user-module clause) needs the same mangling the rest
        // of the user module is getting — otherwise the call site
        // stays bare while the target was mangled to `user$helper/0`
        // and dispatch fails with existence_error.
        //
        // Pass the user-module locals into the rewrite so body calls
        // resolve through them. The user module is the right default:
        // source-declared dynamic clauses from modules without a
        // `:- module/1` directive land in user, and runtime-asserted
        // clauses have no inherent module so user is the conventional
        // home. Multi-module hosts with per-module dynamic-clause
        // namespacing are a more invasive change parked for later.
        if (_dynStore.ClauseFunctorCount > 0)
        {
            // per-functor transform cache. A functor's entry
            // is dropped by InvalidateDynamicCache when its clause list
            // mutates; validity against the rewrite-context inputs is
            // PER ENTRY (the locals-set instance it was built under +
            // the mode-table version) — the per-module transform cache
            // hands back the same locals HashSet while a module is
            // unchanged, so reference equality is exact. So a query after
            // N asserts re-transforms only the asserted functors, and a
            // derivation bump re-transforms only entries whose module's
            // locals actually changed.
            var userLocals = userLocalsCache ?? EmptyLocalsSentinel;
            var dynCtx = new ModuleRewrite.Context(
                DefaultModuleName, userLocals, _dynStore.Functors);
            // per-module contexts for dynamic predicates whose
            // clauses came from a named module (bundle seeds / source-
            // carrying entries). Built lazily; everything unattributed
            // keeps the user context above.
            Dictionary<string, ModuleRewrite.Context>? namedDynCtx = null;
            foreach (var (fid, clauses) in _dynStore.Slots)
            {
                if (clauses.Count == 0) continue;
                var fidCtx = dynCtx;
                if (_dynamicSeedModule.TryGetValue(fid, out var seedModule))
                {
                    namedDynCtx ??= new Dictionary<string, ModuleRewrite.Context>();
                    if (!namedDynCtx.TryGetValue(seedModule, out fidCtx))
                    {
                        HashSet<int>? seedLocals = null;
                        moduleLocalsCache?.TryGetValue(seedModule, out seedLocals);
                        fidCtx = new ModuleRewrite.Context(
                            seedModule,
                            seedLocals ?? EmptyLocalsSentinel,
                            _dynStore.Functors);
                        namedDynCtx[seedModule] = fidCtx;
                    }
                }
                if (!_dynamicRewriteCache.TryGetValue(fid, out var entry)
                    || !ReferenceEquals(entry.LocalsRef, fidCtx.LocalFunctors)
                    || entry.ModesVersion != modeTable.Version)
                {
                    if (entry.Clauses is not null)
                    {
                        // Stale context: the re-transform below mints fresh
                        // helper ids, so the compiled entry (which calls the
                        // old ones) must go with it.
                        DropDynamicPredicateCacheEntry(fid);
                    }
                    var transformed = ClausePipeline.Apply(clauses, modeTable, inlineIte: EnableInlineIte, helperIdProvider: NextMetaHelperId, dcgFailFast: !_flags.DebugCodegen);
                    var rewritten = new List<Clause>(transformed.Count);
                    foreach (var clause in transformed)
                        rewritten.Add(ModuleRewrite.Rewrite(clause, fidCtx));
                    // Head fids include any MetaTransform helper clauses'
                    // heads, mirroring what the per-clause HeadFunctorIdOf
                    // walk over allRewritten used to collect.
                    var headFids = new List<int>(rewritten.Count);
                    foreach (var c in rewritten)
                        headFids.Add(HeadFunctorIdOf(c));
                    entry = (rewritten, headFids, fidCtx.LocalFunctors, modeTable.Version);
                    _dynamicRewriteCache[fid] = entry;
                }
                allRewritten.AddRange(entry.Clauses);
                foreach (int f in entry.HeadFids)
                    rewrittenHeadFids.Add(f);
            }
        }

        // Stub clauses for declared-but-empty dynamic functors. Without
        // these, calls to a dynamic predicate that's been declared but
        // never assertz'd would fail at link time with an unresolved-call
        // error. The stub always fails — its purpose is just to give the
        // predicate a valid bytecode home. the precomputed
        // head-fid set replaces the per-query re-intern of every clause
        // head; stub fids are added to it as they're emitted.
        EmitEmptyDynamicStubs(allRewritten, queryTerm.Position, rewrittenHeadFids);

        // Snapshot the functor ids of every clause that exists *before*
        // the synthetic query clause is added — the static + dynamic
        // program. Only these are eligible for the static cache:
        // the __query__ clause, and any auxiliary predicate a transform
        // or the compiler derives from a query's control constructs, are
        // query-specific — caching them would let one query's goal leak
        // into the next. (rewrittenHeadFids is exactly the
        // head fids of allRewritten at this point, stubs included.)
        var cacheableFunctors = rewrittenHeadFids;

        // Skip-compile cache. Two contributors live here:
        //   - Bundle skip-compile: populated by LoadBundle from
        //     a bundle's compiled bytecode blob.
        //   - Dynamic predicate cache: populated lazily by the
        //     query-setup path itself; invalidated on every assertz /
        //     asserta / retract / abolish that touches the functor.
        // ModuleCompiler reuses any cached predicate whose bytecode doesn't
        // reference per-module literal pools.
        // the three-way merge is maintained incrementally
        // across queries instead of being re-copied per query: built here
        // on demand, nulled wherever _staticPredicateCache is cleared,
        // kept in step with every dynamic-cache add / remove
        // (DropDynamicPredicateCacheEntry) and with the two populate
        // loops below. Merge precedence unchanged: bundle precompiled
        //, static, then dynamic —
        // dynamic last so a predicate that turned dynamic wins over a
        // stale static entry (a consult clears the static cache anyway).
        long profPb1 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (LoadProfEnabled) ProfPbRewriteTicks += profPb1 - profPb0;
        var mergedSkip = _skipCompileMergedCache;
        if (mergedSkip is null)
        {
            mergedSkip = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>(
                _precompiledClauseCache);
            foreach (var (fid, pred) in _staticPredicateCache)
                mergedSkip[fid] = pred;
            foreach (var (fid, pred) in _dynamicPredicateCache)
                mergedSkip[fid] = pred;
            _skipCompileMergedCache = mergedSkip;
        }
        IReadOnlyDictionary<int, Shumway.Compiler.Wam.CompiledPredicate>? skipCompileCache =
            mergedSkip.Count == 0 ? null : mergedSkip;
        // Pre-compute the fail-stub address — it sits at the end of the
        // launcher prefix, at offset Call(9) + Halt(1) = 10. We need it
        // available to the compiler so dynamic predicates emit their
        // last-clause chain instruction with the absolute target.
        // ADR-030 cut elision, hoisted OUT of ModuleCompiler for this call site
        // (ElideRedundantCuts: false below). Elision is a WHOLE-program
        // analysis, so a module reused verbatim by the per-module transform
        // cache can still change its elision outcome when a DIFFERENT module's
        // regeneration flips a callee's det-ness — and its skip-cached compiled
        // predicate would silently keep the old decision (an un-elided cut is
        // harmless; a stale ELIDED cut re-exposes choice points). Running the
        // elision here lets us diff the elided-fid set against the previous
        // build's and drop exactly the flipped predicates from the skip cache.
        long profPe0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (_flags.ElideRedundantCuts)
        {
            // Replay fast path: the elision decisions are a pure function of
            // the eligible (static) clause content, the defined-indicator set
            // and the per-indicator eligibility — a dynamic clause's BODY is
            // never analyzed (ineligible predicates never enter the det set).
            // When no module re-transformed and both fid sets match the
            // previous build's, replay the cached substitution map (original
            // clause → its elided form; same objects each build) instead of
            // re-running the whole-program fixpoint.
            var dynFidsNow = new HashSet<int>();
            foreach (int f in rewrittenHeadFids)
                if (_dynStore.Functors.Contains(f)) dynFidsNow.Add(f);
            if (!staticContentChanged
                && _elideSubstitutions is { } subst
                && _elideKeyHeadFids!.SetEquals(rewrittenHeadFids)
                && _elideKeyDynFids!.SetEquals(dynFidsNow))
            {
                if (subst.Count > 0)
                {
                    var replayed = new List<Clause>(allRewritten.Count);
                    foreach (var c in allRewritten)
                        replayed.Add(subst.TryGetValue(c, out var e) ? e : c);
                    allRewritten = replayed;
                }
                // decisions unchanged ⇒ elided set unchanged ⇒ no flip drops.
            }
            else
            {
                var preElide = allRewritten;
                allRewritten = Shumway.Compiler.Wam.DeterminismAnalysis
                    .EliminateRedundantTrailingCuts(
                        preElide, c => !_dynStore.Functors.Contains(HeadFunctorIdOf(c)));
                var elidedNow = new HashSet<int>();
                var substNew = new Dictionary<Clause, Clause>();
                for (int i = 0; i < allRewritten.Count; i++)
                    if (!ReferenceEquals(allRewritten[i], preElide[i]))
                    {
                        elidedNow.Add(HeadFunctorIdOf(allRewritten[i]));
                        substNew[preElide[i]] = allRewritten[i];
                    }
                if (_lastElidedStaticFids is { } prevElided)
                {
                    List<int>? flipped = null;
                    foreach (int fid in elidedNow)
                        if (!prevElided.Contains(fid)) (flipped ??= new()).Add(fid);
                    foreach (int fid in prevElided)
                        if (!elidedNow.Contains(fid)) (flipped ??= new()).Add(fid);
                    if (flipped is not null)
                    {
                        if (LoadProfEnabled)
                            Console.Error.WriteLine(
                                $"[PROF-DROP] elide-flip: {flipped.Count} fids"
                                + $" (e.g. {DescribeFid(flipped[0])})");
                        DropStaticCompiledFids(flipped);
                    }
                }
                _lastElidedStaticFids = elidedNow;
                _elideSubstitutions = substNew;
                _elideKeyHeadFids = new HashSet<int>(rewrittenHeadFids);
                _elideKeyDynFids = dynFidsNow;
            }
        }
        long profPe1 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (LoadProfEnabled) ProfPbElideTicks += profPe1 - profPe0;
        long profPbC0 = Shumway.Compiler.Wam.ModuleCompiler.ProfCompiledPreds;
        var module = new ModuleCompiler
        {
            EmitDebugInfo = _flags.EmitDebugInfo,
            DebugCodegen = _flags.DebugCodegen,               // ADR-035
            DebugFileId = _debugFileId,                      // ADR-035
            NonDebuggableFunctors = _nonDebuggableFunctors,  // ADR-035
            ElideRedundantCuts = false,   // ADR-030 — pre-elided above
        }.Compile(
            allRewritten, skipCompileCache, unindexedFunctors, _literalPools,
            dynamicFunctors: _dynStore.Functors, failStubAddr: failStubAddr);

        // Cross-activation helper visibility (the Logtalk-under-promotion fix):
        // every helper compiled by this setup stays materializable on demand
        // into any OTHER live activation (see TryMaterializeAssertHelper).
        long profPe2 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (LoadProfEnabled)
        {
            ProfPbModCompileTicks += profPe2 - profPe1;
            long pbCompiled =
                Shumway.Compiler.Wam.ModuleCompiler.ProfCompiledPreds - profPbC0;
            ProfPbCompiledPreds += pbCompiled;
            Console.Error.WriteLine(
                $"[PROF-BUILD] compiled={pbCompiled} staticCache={_staticPredicateCache.Count}"
                + $" dynCache={_dynamicPredicateCache.Count}"
                + $" missNoEntry={Shumway.Compiler.Wam.ModuleCompiler.ProfMissNoEntry}"
                + $" missRejected={Shumway.Compiler.Wam.ModuleCompiler.ProfMissRejected}");
        }
        RegisterLateHelpers(module.Predicates);
        if (LoadProfEnabled) ProfPbLateHelpersTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profPe2;

        // Populate the dynamic cache with any newly-compiled dynamic
        // predicate whose bytecode is safe to reuse next query (no
        // pool-specific literal ids). A predicate is "dynamic" iff its
        // functor is in _dynStore.Functors — whether its clauses live in
        // _modules (source-declared `:- dynamic foo/N.` plus inline
        // facts) or _dynamicClauses (runtime assertz / asserta), both
        // contribute to the same predicate. Cached entries are kept
        // until the next assertz / retract / abolish invalidates them.
        if (_dynStore.FunctorCount > 0)
        {
            foreach (var pred in module.Predicates)
            {
                if (!_dynStore.IsDynamic(pred.FunctorId)) continue;
                // Snapshot the JIT-indexing decision this compile used so
                // a later query can detect a cold→hot flip.
                _jitIndexProfile.RecordCompileDecision(
                    pred.FunctorId, _jitIndexProfile.IsHot(pred.FunctorId));
                if (_dynamicPredicateCache.ContainsKey(pred.FunctorId)) continue;
                if (ReferenceEquals(pred.PoolsRef, _literalPools)
                    || Shumway.Compiler.Wam.ModuleCompiler.IsCachedPredicateReusable(pred))
                {
                    _dynamicPredicateCache[pred.FunctorId] = pred;
                    // mirror into the merged skip-compile
                    // cache (dynamic has top precedence in the merge).
                    if (_skipCompileMergedCache is not null)
                        _skipCompileMergedCache[pred.FunctorId] = pred;
                }
            }
        }

        long profPb2 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (LoadProfEnabled) ProfPbCompileTicks += profPb2 - profPb1;
        // --- ADR-015 chunk B + persistent code space -------
        // Partition the compiled PROGRAM predicates into two regions:
        //   * static  — cacheable + non-dynamic, linked once.
        //   * dynamic — cacheable + dynamic, linked once into the
        //     persistent buffer; mutated in place by
        //     assertz / retract / abolish across queries.
        // (The query overlay — the synthetic __query__ clause plus its $q
        // helpers — is compiled per query, after this product block.)
        var pStatic = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        var pDynamic = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        var pExtraQuery = new List<Shumway.Compiler.Wam.CompiledPredicate>();
        var addedFids = new HashSet<int>();
        foreach (var pred in module.Predicates)
        {
            bool isCacheable = cacheableFunctors.Contains(pred.FunctorId);
            bool isDynamic = _dynStore.IsDynamic(pred.FunctorId);
            if (isCacheable && !isDynamic) pStatic.Add(pred);
            else if (isCacheable && isDynamic) pDynamic.Add(pred);
            else pExtraQuery.Add(pred);
            addedFids.Add(pred.FunctorId);
        }
        // source-less bundle predicates are already
        // compiled (LoadEntryFromBytecode populated
        // _precompiledStaticPredicates). Append them to the static
        // region — they bypassed the AST → ModuleCompiler pipeline
        // entirely, and their bytecode is byte-identical to what
        // we'd have produced from source. Any predicate id that
        // also appeared in module.Predicates above (e.g. a later
        // source-carrying consult of the same functor) wins by
        // staying in module.Predicates and we skip the precompiled
        // copy so we don't add the same id twice to the linker.
        foreach (var (fid, pred) in _precompiledStaticPredicates)
        {
            if (!addedFids.Add(fid)) continue;
            bool isDynamic = _dynStore.IsDynamic(fid);
            if (!isDynamic) pStatic.Add(pred);
            else pDynamic.Add(pred);
        }

        // A compiled predicate must NEVER be silently dropped: when the static
        // link is REUSED, any static-classified predicate absent from it (a
        // MetaTransform helper freshly minted by a dynamic recompile — the
        // '$disj_N' of a mutated predicate's new derivation, whose ids did not
        // exist when the cached region was linked) is re-routed to the QUERY
        // region so it still links and lands in the address map. Without this,
        // the recompiled dynamic clause (linked on a persistent rebuild) calls
        // — or meta-calls, via a findall collect-loop goal term — a helper
        // nothing ever linked: existence_error('$disj_N'/K), hit by Logtalk
        // under IL promotion.
        if (_staticLink is { } cachedStatic)
        {
            for (int i = pStatic.Count - 1; i >= 0; i--)
            {
                if (cachedStatic.Addresses.ContainsKey(pStatic[i].FunctorId))
                    continue;
                pExtraQuery.Add(pStatic[i]);
                pStatic.RemoveAt(i);
            }
        }

        // Cache freshly-compiled static predicates. A predicate
        // is cacheable only if its functor headed a clause in the static +
        // dynamic program (cacheableFunctors) — that excludes every
        // query-derived auxiliary — and it is not dynamic. The literal-pool
        // reusability guard is the same one the dynamic cache uses.
        foreach (var pred in module.Predicates)
        {
            int fid = pred.FunctorId;
            if (!cacheableFunctors.Contains(fid) || _dynStore.IsDynamic(fid)) continue;
            if (_staticPredicateCache.ContainsKey(fid)) continue;
            if (ReferenceEquals(pred.PoolsRef, _literalPools)
                    || Shumway.Compiler.Wam.ModuleCompiler.IsCachedPredicateReusable(pred))
            {
                _staticPredicateCache[fid] = pred;
                // mirror into the merged skip-compile cache.
                // Dynamic entries take precedence in the merge; a fid here
                // is never in the dynamic cache (it isn't in
                // _dynStore.Functors, and abolish drops cache entries when
                // a functor leaves the dynamic set), but keep the guard
                // so the precedence is structural rather than assumed.
                if (_skipCompileMergedCache is not null
                    && !_dynamicPredicateCache.ContainsKey(fid))
                    _skipCompileMergedCache[fid] = pred;
            }
        }

        product = _programProduct = new CompiledProgramProduct
        {
            DerivationGen = _derivationGen,
            ProgramStamp = _programStamp,
            EmitDebugInfo = _flags.EmitDebugInfo,
            DebugCodegen = _flags.DebugCodegen,
            StaticLinkRef = _staticLink,
            StaticPreds = pStatic,
            DynamicPreds = pDynamic,
            ExtraQueryPreds = pExtraQuery,
            CacheableFunctors = cacheableFunctors,
        };
        if (LoadProfEnabled) ProfPbPartitionTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profPb2;
        }   // ---- end compiled-program-product build ----

        // ---- per-query small bootstrap ----
        long profQc0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (LoadProfEnabled) ProfProductCheckTicks += profQc0 - profPc0;
        // Synthetic query clause — rewrite in the user module's context, but
        // with the user locals (which don't include __query__) so the
        // head functor remains bare. the stub's synthesized helpers
        // use the reserved `$q` namespace: they are rewritten under the SAME
        // user-module mangling as the consulted clauses' helpers, so without
        // the prefix a stub `$disj_1` collides with a consulted `$disj_1`
        // (the helper-name-collision latent bug). `$q` names are reused
        // query-to-query, keeping the atom space bounded.
        List<Clause> queryClauses;
        {
            var prevPrefix = Shumway.Compiler.Parsing.MetaTransform.HelperPrefix;
            Shumway.Compiler.Parsing.MetaTransform.HelperPrefix = "$q";
            List<Clause> queryTransformed;
            try
            {
                queryTransformed = PhraseTransform.Apply(
                    MetaTransform.Apply(
                        DcgTransform.Apply(new[] { syntheticClause })));
            }
            finally
            {
                Shumway.Compiler.Parsing.MetaTransform.HelperPrefix = prevPrefix;
            }
            // ADR-038 — resolve the query goal through the user module's import
            // table too, so a REPL `?- use_module(library(X))` then a bare call
            // to an imported predicate resolves to Source$name.
            var userLocals = _staticRewriteUserLocals ?? new HashSet<int>();
            var ctx = _modules.TryGetValue(DefaultModuleName, out var userManifest)
                ? new ModuleRewrite.Context(
                    DefaultModuleName, userLocals,
                    _dynStore.Functors, userManifest.Imports)
                { QualifiedStaticResolver = ResolveQualifiedStatic }
                : new ModuleRewrite.Context(
                    DefaultModuleName, userLocals,
                    _dynStore.Functors)
                { QualifiedStaticResolver = ResolveQualifiedStatic };
            queryClauses = new List<Clause>(queryTransformed.Count);
            foreach (var clause in queryTransformed)
                queryClauses.Add(ModuleRewrite.Rewrite(clause, ctx));
        }

        // Compile ONLY the query clauses (against the shared literal pools and
        // the same fail-stub address); everything else comes from the product.
        var queryModule = new ModuleCompiler
        {
            EmitDebugInfo = _flags.EmitDebugInfo,
            DebugCodegen = _flags.DebugCodegen,               // ADR-035
            DebugFileId = _debugFileId,                      // ADR-035
            NonDebuggableFunctors = _nonDebuggableFunctors,  // ADR-035
            ElideRedundantCuts = _flags.ElideRedundantCuts,   // ADR-030
        }.Compile(
            queryClauses, cache: null, unindexedFunctors, _literalPools,
            dynamicFunctors: _dynStore.Functors, failStubAddr: failStubAddr);
        RegisterLateHelpers(queryModule.Predicates);

        var staticPreds = product.StaticPreds;
        var dynamicPreds = product.DynamicPreds;
        var queryPreds = new List<Shumway.Compiler.Wam.CompiledPredicate>(
            product.ExtraQueryPreds.Count + queryModule.Predicates.Count);
        queryPreds.AddRange(product.ExtraQueryPreds);
        queryPreds.AddRange(queryModule.Predicates);

        var launcher = new BytecodeEmitter();
        int callPos = launcher.Position;
        launcher.EmitCall(targetAddress: 0, numLivePermanents: 0);
        launcher.EmitHalt();
        // ADR-015 chunk C step 4: a fail-stub at a known offset in the
        // prefix. Dynamic predicates' last-clause chain instructions point
        // here via `retry_me_else <fail-stub>` (instead of trust_me) so a
        // future assertz can patch the operand in place. retry_me_else
        // does not remove the CP, so the stub itself runs trust_me first
        // to pop the dynamic predicate's chain CP — otherwise backtracking
        // would loop right back to this fail-stub forever. Then
        // call_builtin fail/0 returns false and the interpreter resumes
        // backtracking at whatever caller-side CP survives.
        // The compilers were already told this address (failStubAddr above);
        // assert the launcher's position agrees.
        if (launcher.Position != failStubAddr)
            throw new InvalidOperationException(
                $"launcher position {launcher.Position} != pre-computed fail-stub addr {failStubAddr}");
        launcher.EmitTrustMe();
        int failFunctorId = FunctorTable.Intern(
            AtomTable.Intern("fail", permanent: true).Id, 0);
        if (!Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(
            failFunctorId, out int failBuiltinId))
            throw new InvalidOperationException(
                "fail/0 builtin must be registered for ADR-015 dynamic dispatch.");
        launcher.EmitCallBuiltin(failBuiltinId, numLivePermanents: 0);
        byte[] prefix = launcher.ToBytes();

        var staticLink = _staticLink
            ?? (_staticLink = GetOrLinkStatic(staticPreds, prefix.Length));
        // The product built before any static link existed patches its
        // reference to the one just built — they are consistent by
        // construction (the link was made from the product's StaticPreds).
        product.StaticLinkRef ??= _staticLink;

        // Dynamic region: linked once into the persistent buffer.
        // Mid-query assertz extends in place; only a change to the
        // dynamic-functor set (abolish, consult) invalidates this.
        bool builtPersistentNow = _persistentProgram is null || _dynamicLink is null;
        if (builtPersistentNow)
        {
            int dynamicLoadOffset = prefix.Length + staticLink.Bytecode.Length;
            _dynamicLink = new Linker().Link(
                dynamicPreds,
                loadOffset: dynamicLoadOffset,
                externalSymbols: staticLink.Addresses,
                switchTableIdBase: staticLink.SwitchTables.Count);
            _persistentLength =
                prefix.Length + staticLink.Bytecode.Length + _dynamicLink.Bytecode.Length;
            // Over-allocate so capacity-doubling AppendCode appends
            // cheaply mid-query without forcing immediate realloc.
            int initialCapacity = Math.Max(_persistentLength * 2, 1024);
            _persistentProgram = new byte[initialCapacity];
            Array.Copy(prefix, _persistentProgram, prefix.Length);
            Array.Copy(staticLink.Bytecode, 0, _persistentProgram,
                prefix.Length, staticLink.Bytecode.Length);
            Array.Copy(_dynamicLink.Bytecode, 0, _persistentProgram,
                dynamicLoadOffset, _dynamicLink.Bytecode.Length);
            // The static→dynamic unresolved sites get patched in
            // _persistentProgram with the dynamic region's freshly
            // assigned addresses.
            foreach (var (offset, fid) in staticLink.UnresolvedSites)
                if (_dynamicLink.Addresses.TryGetValue(fid, out int dynAddr))
                    BytecodeIO.WriteInt32(_persistentProgram!, prefix.Length + offset + 1, dynAddr);
        }
        // pick the per-query overlay's start address with
        // enough headroom over the persistent length for mid-query
        // assertz extensions (typically far less than 64 MB).
        _querySplit = _persistentLength + PersistentToQueryGap;

        // Build the merged external-symbols table for the query
        // linker — it resolves calls into both the static and
        // dynamic regions of the persistent buffer. cached
        // alongside the persistent link itself (the LinkResult address
        // maps are immutable), together with the bare-alias overlay and
        // the merged predicates-by-address map — rebuilding the three
        // per query re-copied two large dictionaries and re-ran the
        // alias loop's per-functor string work for no observable change
        // while the persistent regions are reused.
        if (builtPersistentNow || _persistentAddressesCache is null)
        {
            var pa = CollectionsCompat.Copy(staticLink.Addresses);
            foreach (var (fid, a) in _dynamicLink!.Addresses) pa[fid] = a;
            _persistentAddressesCache = pa;

            // Runtime call/1 dispatches a goal by its bare
            // functor, but a module-local predicate is linked under its
            // mangled "module$name" functor. Pre-compute the persistent
            // regions' bare-functor aliases once per rebuild; the
            // per-query loop below only has to alias the (tiny) query
            // region. Module set changes always invalidate the
            // persistent regions, so the _modules guard inside stays
            // consistent with this cache's lifetime.
            var baseMap = new Dictionary<int, int>(pa);
            AddBareLocalAliases(baseMap, pa);
            _persistentAddressBaseCache = baseMap;

            var pba = CollectionsCompat.Copy(staticLink.PredicatesByAddress);
            foreach (var (a, p) in _dynamicLink.PredicatesByAddress) pba[a] = p;
            _persistentPredsByAddressCache = pba;
        }
        var persistentAddresses = _persistentAddressesCache;

        // The per-query region is appended in a SEPARATE buffer at a
        // logical address well above the persistent buffer's end.
        // The ProgramView built below routes addresses in [0, split)
        // to the persistent buffer and [split, split+queryLen) to the
        // per-query overlay; persistent growth between now and the
        // query's end stays in [persistentLength, split) so the
        // overlay's linked addresses remain stable.
        var queryLink = new Linker().Link(
            queryPreds,
            loadOffset: _querySplit,
            externalSymbols: persistentAddresses,
            switchTableIdBase:
                // _dynamicLink is non-null here (built in the
                // builtPersistentNow block above); ! matches the sibling
                // sites at _dynamicLink!.Addresses / .SwitchTables.
                staticLink.SwitchTables.Count + _dynamicLink!.SwitchTables.Count);

        byte[] queryBytes = queryLink.Bytecode;
        if (LoadProfEnabled)
            ProfQueryCompileTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profQc0;
        long profMg0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

        // Merge the three regions' link metadata; downstream code is
        // region-agnostic and reads this combined view. The persistent base
        // (which already carries the persistent regions' bare-name aliases)
        // is shared BY REFERENCE under a small per-query overlay
        // (LayeredIntMap) instead of being copied — the three O(program)
        // dictionary copies here were the largest warm-setup cost on a
        // clpz-sized program. The overlay holds the query region's links,
        // its bare aliases, the IL/region markers, and any mid-query
        // trampoline installs; overlay wins on lookup, preserving the old
        // construction order (a query-region REAL address shadows a
        // colliding persistent alias).
        var queryAddrOverlay = CollectionsCompat.Copy(queryLink.Addresses);
        var mergedAddresses = new Shumway.Core.LayeredIntMap<int>(
            queryAddrOverlay, _persistentAddressBaseCache!);
        // keep the functor→address map of the most recent
        // query so the profiler can resolve a recorded callee address
        // back to a Name/Arity. Only assembled when profiling is
        // compiled in — otherwise it's a cheap reference assignment we
        // skip entirely.
        if (Shumway.Core.Profiler.Enabled)
            _profileFunctorAddresses = mergedAddresses;
        // The merged switch-table list is still rebuilt per query (cheap
        // reference copies): the new-key assertz path REPLACES
        // entries of _dynamicLink.SwitchTables in place for cross-query
        // persistence, so a cached merged snapshot would go stale.
        var mergedSwitchTables =
            new List<Shumway.Core.SwitchTable>(staticLink.SwitchTables);
        mergedSwitchTables.AddRange(_dynamicLink!.SwitchTables);
        mergedSwitchTables.AddRange(queryLink.SwitchTables);
        // persistent part pre-merged at rebuild time; query region layered
        // on top (address spaces are disjoint — the query region lives above
        // the split — but the layered lookup doesn't rely on that).
        var mergedPredicatesByAddress =
            new Shumway.Core.LayeredIntMap<Shumway.Compiler.Wam.CompiledPredicate>(
                CollectionsCompat.Copy(queryLink.PredicatesByAddress),
                _persistentPredsByAddressCache!);
        // The "program" in the LinkResult is now a logical concept —
        // the live bytes live across two physical buffers. Downstream
        // consumers that don't access linkResult.Bytecode (most of
        // them) work unchanged; the few that do get the persistent
        // half — the static and dynamic regions they care about.
        var linkResult = new Linker.LinkResult(
            _persistentProgram!, mergedAddresses, mergedSwitchTables,
            mergedPredicatesByAddress, Array.Empty<(int, int)>());

        // The synthetic query stays under its bare functor (it's local to
        // user but ModuleRewrite never mangles __query__ because it's not
        // present in user's local set: it was added after locals were
        // computed and isn't part of the user-defined predicates).
        int queryFunctorId = FunctorTable.Intern(
            AtomTable.Intern(queryFunctor, permanent: true).Id,
            varNames.Count);
        // Patch the launcher's call target — the prefix sits at
        // _persistentProgram offset 0, so callPos points there.
        BytecodeIO.WriteInt32(_persistentProgram!, callPos + 1, linkResult.Addresses[queryFunctorId]);

        // `program` is the persistent byte[] (used by all mutation
        // paths: assertz/retract/abolish chain patching, AppendCode);
        // `programView` is the two-buffer logical view passed to the
        // interpreter and IL helpers — they read across the gap into
        // the per-query overlay transparently.
        byte[] program = _persistentProgram!;
        var programView = new Shumway.Core.ProgramView(
            _persistentProgram!, queryBytes, _querySplit);

        // (Static-predicate caching runs at product build, inside the
        // compiled-program-product block above.)

        // Runtime call/1 dispatches a goal by its bare functor,
        // but a module-local predicate is linked under its mangled
        // "module$name" functor. Add a bare-functor alias for each so a
        // runtime call/N can resolve a local predicate by its plain name.
        // the persistent regions' aliases are already in the layered base
        // (pre-computed at persistent rebuild — see
        // _persistentAddressBaseCache); only the query region's handful
        // of entries still need the per-query string walk. addressMap IS
        // mergedAddresses — the aliases and markers land in the same
        // per-query overlay (they were the only delta between the two maps,
        // and the merged map's remaining consumers — the launcher patch and
        // the profiler's diagnostic view — tolerate them).
        var addressMap = mergedAddresses;
        AddBareLocalAliases(addressMap, queryLink.Addresses);

        // --strip-wam: a predicate whose WAM body was dropped from the bundle
        // (its IL delegate carries the body) has no entry in linkResult.Addresses,
        // so it is invisible to every dispatch path that resolves a goal by
        // functor id through CurrentFunctorAddresses — the runtime meta-call
        // sites (MetaCallInEngine, DispatchCall, and the IL meta-call helper
        // IlMetaCallHelper.Dispatch). Map each such IL-only functor to its
        // resume marker (EncodeResumeMarker(fid, 0)): the marker flows through
        // SetPc and the main Dispatch loop's IsResumeMarker check routes it to
        // the IL delegate via IlByFunctorId — exactly the path a
        // compiled CallIl already uses. Only inject where there is no WAM
        // address (a non-stripped IL predicate keeps its WAM and meta-calls
        // through it unchanged). A module-local predicate is registered under
        // its mangled "module$name" functor, so it also needs a bare-name alias
        // (mirroring the WAM bare-alias loop above) pointing at the SAME marker
        // — a runtime meta-call (an if-then-else condition, call/N) names the
        // predicate by its plain name.
        foreach (int ilFid in IlPromotion.PromotedFunctorIds())
        {
            int marker = Activation.EncodeResumeMarker(ilFid, 0);
            if (!addressMap.ContainsKey(ilFid))
                addressMap[ilFid] = marker;
            var (atomId, arity) = FunctorTable.Lookup(ilFid);
            string name = AtomTable.GetById(atomId)?.Name ?? "";
            int dollar = name.IndexOf('$');
            if (dollar <= 0) continue;
            if (!_modules.ContainsKey(name.Substring(0, dollar))) continue;
            int bareFid = FunctorTable.Intern(
                AtomTable.Intern(name.Substring(dollar + 1), permanent: true).Id, arity);
            if (!addressMap.ContainsKey(bareFid))
                addressMap[bareFid] = marker;
        }

        // region member-entry aliases, LOWEST priority: an absorbed-only
        // member with no WAM address (stripped) and no standalone IL delegate (pruned)
        // still resolves by fid — into its region method at the member's entry cursor.
        // The ContainsKey guards keep every better resolution (a real WAM address, or
        // a standalone delegate's (fid, 0) marker from the loop above) ahead of it.
        // Same bare-name aliasing as above: meta-calls name predicates unmangled.
        foreach (var (memberFid, marker) in _regionMemberAliases)
        {
            if (!addressMap.ContainsKey(memberFid))
                addressMap[memberFid] = marker;
            var (atomId, arity) = FunctorTable.Lookup(memberFid);
            string name = AtomTable.GetById(atomId)?.Name ?? "";
            int dollar = name.IndexOf('$');
            if (dollar <= 0) continue;
            if (!_modules.ContainsKey(name.Substring(0, dollar))) continue;
            int bareFid = FunctorTable.Intern(
                AtomTable.Intern(name.Substring(dollar + 1), permanent: true).Id, arity);
            if (!addressMap.ContainsKey(bareFid))
                addressMap[bareFid] = marker;
        }

        if (LoadProfEnabled)
            ProfMergeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profMg0;
        long profAc0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var engine = new Activation(PooledActivationConfig)
        {
            Out = Out,
            Host = this,
            Operators = new OperatorTableAdapter(_operators),
            // Per-engine stream registry — wired through
            // so StreamBuiltins reaches handles, the alias map, and
            // the current-input / current-output cursors.
            Streams = Streams,
            // The current-query address map lets IL-emitted Execute
            // opcodes resolve their tail-call target via a
            // stable functor-id lookup instead of an embedded address
            // that would only be valid for one query's linked layout.
            CurrentFunctorAddresses = addressMap,
            // ADR-038 — the module import map for runtime variable meta-calls.
            CurrentImportMap = BuildRuntimeImportMap(),
            // the ISO `unknown` flag, wired through dispatch.
            OnUnknown = _flags.Unknown switch
            {
                "fail" => Shumway.Core.UnknownAction.Fail,
                "warning" => Shumway.Core.UnknownAction.Warning,
                _ => Shumway.Core.UnknownAction.Error,
            },
            // String literal pool for IL-emitted get_pstr/put_pstr
            // and the linked program byte array for the
            // IL Call re-entry helper.
            CurrentStringLiterals = queryModule.StringLiterals,
            CurrentProgram = program,
            // ADR-015 — bytecode-level dynamic dispatch reads the
            // host's generation at every enter_dynamic opcode
            // through the shared GenerationBox (a field read) instead of
            // a Func<long> invoke per dynamic call.
            DbGenerationBox = _dbGeneration,
            // ADR-015 chunk C step 4: where the fail-stub lives in the
            // prefix. Used by the upcoming incremental-assertz path and
            // by dynamic predicates' last-clause chain instructions.
            DynamicFailStubAddr = failStubAddr,
            // ADR-034 — the host-lifetime mutated-dynamics set, shared by
            // reference so baked staleness tests see mutations live.
            MutatedDynamicFids = _mutatedDynamicFids,
            // ADR-035 — the debug seam. Null unless a session is attached
            // (trace/0, or a debugger), in which case the Tier-0 interpreter
            // raises the four Prolog ports on it. A LAZY session
            // (ActivateOnAttach, not yet armed) deliberately leaves it null:
            // the interpreter's existing Debug?-null-checks then cost what
            // release costs, which is the whole point of the mode.
            Debug = DebugFullyArmed ? DebugSession : null,
            // ADR-035 D5+ — with a session watching, trail EVERY binding (the HB
            // optimisation's untrailed young-var bindings are unrecoverable, and Set
            // Next Statement rewinds by unwinding the trail to a recorded mark).
            TrailEverything = DebugFullyArmed && DebugSession is not null,
            // ADR-035 — inert unless the program was compiled under
            // compile_mode=debug (only then does any debug_lastcall exist).
            LastCallOptimisation = _flags.DebugLco,
            // ADR-039 — snapshot the prefer_rationals flag for '/' semantics.
            PreferRationals = _flags.PreferRationals,
        };
        if (LoadProfEnabled)
            ProfActCtorTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profAc0;
        // Heap-buffer pool: seed the fresh activation with the recycled
        // buffer (if any) BEFORE anything materializes onto the heap.
        _heapPool.Adopt(engine);
        // the persistent buffer is over-allocated, so the
        // engine's ProgramLength must reflect the live region (not the
        // raw byte[] capacity) for AppendCode's offset accounting. The
        // overlay + split let the dispatch loop refresh the
        // ProgramView correctly after a mid-query AppendCode.
        engine.SetInitialProgramLength(_persistentLength);
        engine.CurrentQueryOverlay = queryBytes;
        engine.CurrentQuerySplit = _querySplit;
        // the dispatch loop caches its ProgramView and
        // refreshes only when this generation flips, so the per-
        // query rewire above has to advertise itself.
        engine.BumpProgramGeneration();
        // expose the linked switch tables on the engine
        // as a MUTABLE list. The same list reference is handed to the
        // interpreter; the new-key assertz path swaps entries in place
        // and the interpreter sees the update on the next dispatch
        // because it reads through the list reference each time.
        // mergedSwitchTables is already a fresh per-query list (built in the
        // merge step above and aliased into linkResult) — reuse it instead of
        // copying it a second time.
        var mutableSwitchTables = mergedSwitchTables;
        engine.SwitchTables = mutableSwitchTables;
        engine.ResolveLateHelper = fid => TryMaterializeAssertHelper(engine, fid);
        // ADR-041 — first-arg clause selection for unindexed dynamic chains at
        // enter_dynamic (determinism must not depend on JIT hotness). Reads
        // _currentPredicatesByAddress at call time (set later in this setup).
        engine.DynChainSelect = (e, pc) =>
            ChainPatcher.SelectDynChainCandidate(e, pc, _currentPredicatesByAddress);
        // ISO number_chars/number_codes fall back to the full term reader for the
        // operator/quoting cases the token parser can't cover (`'-'1` → -1).
        MetaBuiltins.WireNumberFromChars(engine);

        var interp = new BytecodeInterpreter(
            engine, queryModule.StringLiterals, queryModule.FloatLiterals,
            mutableSwitchTables, queryModule.BigIntLiterals);

        // --strip-wam: register each persisted dispatch graph onto this query's
        // fresh engine, so a stripped indexed predicate resolves its entry clause
        // from the graph (its WAM body is gone). The indexed-dispatch cache is
        // per-engine, so this runs per query.
        if (_persistedIndexGraphs.Count > 0)
            foreach (var (fid, graphBytes) in _persistedIndexGraphs)
                Shumway.Compiler.Il.IlIndexedDispatch.RegisterPersistedGraph(
                    engine, fid, graphBytes);

        // wire the direct IL-delegate table (the
        // CallIl opcode reads this) and rewrite every Call site whose
        // callee already has IL into a CallIl. The opcode's slow path
        // (Call → DispatchToTier1OrBytecode → Tier1Dispatcher?.OnDispatch)
        // is the hottest non-Dispatch frame in the dotnet-trace profile
        // for a bundle-IL workload; CallIl bypasses it with a direct
        // delegate invoke. Same byte width and operand layout as Call,
        // so the patch is one opcode-byte swap + one 4-byte operand
        // overwrite (target address → callee functor id).
        long profIn0 = LoadProfEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        InstallCallIlRewrites(
            interp, mergedPredicatesByAddress, queryLink.PredicatesByAddress, queryBytes);
        if (LoadProfEnabled)
            ProfInstallTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profIn0;

        // ADR-015 chunk C step 4: refresh the interpreter's literal pools
        // after an incremental assertz/asserta interns a new literal.
        engine.RefreshLiteralPoolsCallback = (s, f, b) =>
        {
            interp.RefreshLiteralPools(s, f, b);
            engine.CurrentStringLiterals = s;
        };
        // record the pool lengths the interpreter was built
        // with; RefreshLiteralPoolsIfGrown compares against these so the
        // per-assert refresh (three Snapshot() array copies) only runs
        // when a compile actually interned a new literal. per
        // engine (see _interpPoolCounts).
        _interpPoolCounts.AddOrUpdate(engine, new[]
        {
            queryModule.StringLiterals.Count,
            queryModule.FloatLiterals.Count,
            queryModule.BigIntLiterals.Count,
        });

        // lets a PrologRuntimeException thrown from a
        // builtin Impl carry the offending term in its error/2 value
        // slot, instead of the Phase-9 fresh anonymous variable.
        // Eager materialisation here means the term survives sub-engine
        // teardown — the per-query Activation is gone by the time the
        // parent's catch/3 handler translates the runtime exception.
        // Nested catch/3 resolution for in-engine sub-goal drivers (wakeups,
        // findall): resolves the ball against frames opened inside the nested
        // goal only, so the driver's own C# frame — which owns the interrupted
        // caller's continuation — survives a caught throw. See
        // Activation.NestedCatchResolver.
        engine.NestedCatchResolver = (ex, minFrameIndex) =>
        {
            Term? nestedBall = ex switch
            {
                ShumwayPrologException spe => spe.Term,
                Shumway.Core.PrologRuntimeException pre =>
                    MetaBuiltins.TranslateRuntimeError(pre),
                _ => null,
            };
            if (nestedBall is null) return -1;
            return TryCatchFrom(engine, nestedBall, minFrameIndex, out _);
        };
        // Cheap throw: a throw/1 whose catcher was opened in the SAME dispatch
        // invocation resolves to a PC jump — no .NET exception construction or
        // EH unwinding (clpz's with_local_attributes throws per propagation).
        engine.InlineThrowResolver = (ballIdx, minFrameIndex) =>
        {
            Term ball = TermReader.Materialize(engine, ballIdx);
            if (ball is Shumway.Compiler.Ast.VarTerm) return -1;   // ISO error path
            return TryCatchFrom(engine, ball, minFrameIndex, out _);
        };
        engine.MaterializeCellToTerm = cell =>
        {
            // Snapshot to a heap slot so the standard "read by heap
            // index" TermReader path applies (avoids a cell-direct
            // reader variant).
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, cell);
            return TermReader.Materialize(engine, slot);
        };

        // opt-in SHUMWAY_CP_TRACE dump. The diagnostic prints
        // "name/arity@offset" for each live CP's saved BP using the
        // same address->predicate map the stack-trace resolver uses,
        // so we can spot a CP that should have been cut but is still
        // alive at the moment a builtin is re-entered with an
        // unbound arg.
        {
            var resolverMap = mergedPredicatesByAddress;
            // Diagnostic / error-path only: sort lazily on first resolve. Eagerly
            // sorting every merged predicate address cost O(N log N) at EVERY
            // query setup, dominating warm setups on large programs.
            int[]? sortedAddrs = null;
            engine.ResolveAddressToLabel = addr =>
            {
                sortedAddrs ??= resolverMap.Keys.OrderBy(a => a).ToArray();
                if (sortedAddrs.Length == 0) return null;
                int idx = Array.BinarySearch(sortedAddrs, addr);
                if (idx < 0) idx = ~idx - 1;
                if (idx < 0) return null;
                int entryAddr = sortedAddrs[idx];
                if (!resolverMap.TryGetValue(entryAddr, out var pred))
                    return null;
                var (atomId, arity) = FunctorTable.Lookup(pred.FunctorId);
                string name = AtomTable.GetById(atomId)?.Name ?? "?";
                return $"{name}/{arity}@+{addr - entryAddr}";
            };
        }

        // ADR-015 chunk C step 4: per-functor chain state — record where
        // each clause's check_visible died slot lives in the running
        // program. retract patches the slot in place; next call's
        // check_visible filters the clause out (the bytecode-level
        // logical-update view path that supersedes chunk C's redirect).
        // only rebuild chain state from scratch when the
        // persistent buffer is fresh. While it's being reused across
        // queries, the incremental assertz / asserta / retract paths
        // maintain _dynChains directly — a contiguous walk from
        // predAddr can't see chunks appended elsewhere by AppendCode.
        if (builtPersistentNow)
        {
            // a fresh buffer gets a fresh chain table. Older
            // tables stay alive through _engineChainTables for any outer
            // (nested-query) engines still running on their buffers.
            ResetDynChains();
            PopulateDynChains(program, addressMap, mergedPredicatesByAddress);
        }
        // associate this engine with the table describing the
        // buffer it runs on: every in-place dynamic mutation this engine
        // performs resolves chain state through this association, so a
        // nested query's rebuild can never make it patch wrong offsets.
        AssociateEngineWithCurrentChains(engine);
        // ... and track it for the dynamic-mutation broadcast (a mutation
        // from a nested query must also reach the buffers of suspended
        // outer engines — see _liveEngines).
        RegisterLiveEngine(engine);

        if (StackDiagEnabled)
        {
            int probeFid = FunctorTable.Intern(
                AtomTable.Intern("$lgt_file_loading_stack_", permanent: true).Id, 2);
            int chainEntries = DynChains.Chains.TryGetValue(probeFid, out var pch)
                ? pch.Entries.Count : -1;
            int storeCount = _dynStore.TryGetClauses(probeFid, out var pcs) ? pcs.Count : -1;
            Console.Error.WriteLine(
                $"[STK-SETUP] eng={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine):X8}"
                + $" builtNow={builtPersistentNow}"
                + $" buf={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(program):X8}"
                + $" tbl={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(DynChains):X8}"
                + $" chainEntries={chainEntries} store={storeCount}");
        }

        // Tier-1 promotion: hook the interpreter up to this engine's
        // IlPromotionStore via an address-keyed adapter. The store itself
        // is functor-keyed and persists across queries; the adapter holds
        // the per-query PredicatesByAddress map so it can translate the
        // bytecode-PC the interpreter has into the functor the store
        // wants.
        interp.Tier1Dispatcher = new Tier1DispatcherAdapter(
            IlPromotion, linkResult.PredicatesByAddress, _jitIndexProfile);

        // PGO phase-2 pass. Once per query setup, off the
        // hot path: any promoted, instrumented predicate that has
        // accumulated enough profile samples is recompiled to its
        // optimised (dispatch-reordered) form. The functor-keyed view of
        // this query's program is O(all predicates) — build it only when
        // there is actually PGO work pending (there almost never is).
        if (IlPromotion.HasPgoWork)
        {
            var functorToPredicate = new Dictionary<int, Shumway.Compiler.Wam.CompiledPredicate>();
            foreach (var (_, pred) in linkResult.PredicatesByAddress)
                functorToPredicate[pred.FunctorId] = pred;
            IlPromotion.ConsiderPgoRecompiles(functorToPredicate, functorToPredicate);
        }
        // IlSubroutineRunner / BacktrackRunner /
        // SetBacktrackFloor wirings deleted. The IL Call /
        // meta-CP backtrack-driver / floor pin
        // were all replaced by threaded resume-marker dispatch
        //.
        // Remember the per-query address → predicate map so error
        // reporting can translate the engine's PC and env-
        // chain return addresses into Name/Arity stack frames.
        _currentPredicatesByAddress = linkResult.PredicatesByAddress;

        // ADR-035 — which source sites this engine's code actually contains (what a
        // breakpoint can bind to), then (re)apply the armed ones to the program this
        // query will run. Addresses are derived, not stored: the source site is the
        // truth, and the code space it maps into can be relinked or compacted
        // between queries. Both loops are skipped entirely unless something was
        // compiled debuggable, so release queries pay nothing.
        _sortedPredEntries = null;             // the layout may have moved
        _fidMemoLo = _fidMemoHi = int.MaxValue;   // and with it the address→functor memo
        if (_flags.DebugCodegen || _compiledSites.Count > 0)
        {
            // SCALE GUARD: this rebuild costs O(every stop site + clause frame in the
            // program) — for a codebase of hundreds of modules, paying it at EVERY query
            // setup dwarfed the query. The derived tables depend only on the
            // (address → compiled predicate) mapping, so when that mapping is unchanged
            // — same addresses, same predicate INSTANCES, the common case for every
            // query between consults/asserts — the previous tables stand. The check is
            // an O(predicates) reference walk, exact by construction.
            bool layoutUnchanged = _debugTablesBuiltFor is { } prev
                && prev.Count == _currentPredicatesByAddress.Count;
            if (layoutUnchanged)
                foreach (var (predAddr, pred) in _currentPredicatesByAddress)
                {
                    if (_debugTablesBuiltFor!.TryGetValue(predAddr, out var old)
                        && ReferenceEquals(old, pred)) continue;
                    layoutUnchanged = false;
                    break;
                }
            if (!layoutUnchanged)
            {
                RebuildDebugTables();
                _debugTablesBuiltFor =
                    CollectionsCompat.Copy(_currentPredicatesByAddress);
            }
        }
        // A freshly-rebuilt persistent buffer carries no Break bytes and the recorded originals
        // belong to the now-dead one; a reused buffer still carries them. Only un-patch when the
        // buffer actually holds our patches. (RefreshBreakpoints, mid-query, follows the live
        // activation via _lastQueryEngine — set just below — so a realloc is tracked there.)
        //
        // ADR-035 D5 — a DEBUG EVALUATION's nested query does not touch the sync at all: the
        // armed table describes the OUTER query's buffer, which is where the machine returns
        // when the evaluation is done, and re-deriving it here would point it at the eval's.
        // The usual case reuses the outer's buffer anyway (patches in place, table already
        // right); in the rare rebuilt-under-eval case the fresh buffer simply runs without
        // Break bytes — an eval's stops are suppressed regardless — and the flag below tells
        // the NEXT real setup that this persistent buffer never received its patches, so its
        // per-byte un-patch must not expect to find them (a false "drift" alarm otherwise).
        if (_debugEvalDepth == 0)
        {
            SyncBreakpoints(program,
                bufferCarriesOurPatches: !builtPersistentNow && !_persistentRebuiltPatchFree);
            _persistentRebuiltPatchFree = false;
        }
        else if (builtPersistentNow)
        {
            _persistentRebuiltPatchFree = true;
        }
        // Shared BY REFERENCE, and shared even when it is empty: a breakpoint can be armed
        // on a query that is already running (F9 during a long goal), and the Break byte it
        // patches into the program is reached by an activation that was set up before the
        // table had anything in it. Handing over null when the table happened to be empty
        // would leave that activation with a Break it cannot decode.
        engine.BreakpointOriginals = _breakpointPatches;

        int[] varHeapIndices = new int[varNames.Count];
        for (int i = 0; i < varNames.Count; i++)
        {
            int h = engine.AllocateHeapUnbound();
            varHeapIndices[i] = h;
            engine.SetRegister(i, Cell.Ref(h));
        }

        // ADR-016: register the heap roots the engine cannot see. The
        // query-variable cells are read out of the heap by BuildSolution
        // after the query, so a collection during the query must keep
        // them alive (mark) and rewrite their recorded indices
        // (relocate) — otherwise the extracted bindings are scrambled.
        // The global-variable store's compound values are roots for the
        // same reason within a query.
        var globals = GlobalVars;
        engine.OnGcMark = (markCell, markReferents) =>
        {
            for (int i = 0; i < varHeapIndices.Length; i++) markCell(varHeapIndices[i]);
            foreach (var (_, cell) in globals.All()) markReferents(cell);
            // ADR-035: an attached debug session holds heap indices too (the
            // open goals' argument cells).
            engine.Debug?.MarkHeapRoots(markCell);
        };
        engine.OnGcRelocate = (relocIndex, relocCell, relocBoundary) =>
        {
            for (int i = 0; i < varHeapIndices.Length; i++)
                varHeapIndices[i] = relocIndex(varHeapIndices[i]);
            globals.RelocateCells(relocCell);
            engine.Debug?.RelocateHeapRoots(engine, relocIndex, relocBoundary);
        };

        // Tier-0 deterministic benchmark metric: keep a reference to the
        // per-query engine so the harness can read its monotonic
        // CellsAllocated after the query completes (the engine is
        // otherwise local and discarded). Read-only diagnostic; does not
        // affect execution.
        _lastQueryEngine = engine;
        if (LoadProfEnabled)
            ProfActivationTicks += System.Diagnostics.Stopwatch.GetTimestamp() - profAc0;
        return (programView, varNames, varHeapIndices, engine, interp);
    }

    private Activation? _lastQueryEngine;

    /// <summary>Monotonic count of WAM heap cells reserved by the most
    /// recent query's engine (0 before any query). A deterministic,
    /// wall-clock-independent metric for allocation-affecting changes —
    /// see <see cref="Activation.CellsAllocated"/> and the benchmark
    /// harness <c>--alloc</c> mode.</summary>
    public long LastQueryCellsAllocated => _lastQueryEngine?.CellsAllocated ?? 0;

    /// <summary>Adds a fail-only stub clause for every dynamic functor that
    /// has neither static nor asserted clauses yet, so that calls to it
    /// resolve at link time (and fail at runtime — which is what an
    /// "empty dynamic predicate" should do).</summary>
    private void EmitEmptyDynamicStubs(
        List<Clause> allRewritten, Shumway.Compiler.Lexer.SourcePosition pos,
        HashSet<int> seen)
    {
        // `seen` arrives as the precomputed head-fid set of
        // allRewritten (maintained by the caller's cached transform
        // bookkeeping), replacing the per-query walk that re-interned
        // every clause head. Stub fids are added to the set so the
        // caller's cacheableFunctors snapshot includes them, exactly as
        // the old post-stub HeadFunctorIdOf walk did.
        if (_dynStore.FunctorCount == 0) return;

        foreach (int fid in _dynStore.Functors)
        {
            if (!seen.Add(fid)) continue;
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            Term head = arity == 0
                ? (Term)new AtomTerm(name)
                : new CompoundTerm(
                    name,
                    Enumerable.Range(0, arity).Select(_ => (Term)new VarTerm("_")).ToArray());
            Term stubTerm = new CompoundTerm(":-", new[] { head, (Term)new AtomTerm("fail") });
            allRewritten.Add(new Clause(ClauseKind.Rule, stubTerm, pos));
        }
    }

    /// <summary>the bare-functor alias computation,
    /// factored out so it can run once per persistent rebuild over the
    /// persistent regions' addresses (cached in
    /// <see cref="_persistentAddressBaseCache"/>) and per query over just
    /// the query region's addresses. For every <c>module$name</c> entry in
    /// <paramref name="entries"/> whose module is loaded, adds
    /// <c>name/arity → address</c> to <paramref name="map"/> unless the
    /// bare functor already resolves (a real definition or an earlier
    /// alias wins, preserving the original first-wins semantics).</summary>
    private static string DescribeFid(int fid)
    {
        var (atomId, arity) = FunctorTable.Lookup(fid);
        return $"{AtomTable.GetById(atomId)?.Name}/{arity}";
    }

    private void AddBareLocalAliases(
        Dictionary<int, int> map, IReadOnlyDictionary<int, int> entries,
        HashSet<int>? recordAdded = null)
        => AddBareLocalAliasesCore(map.ContainsKey, (k, v) => map[k] = v, entries, recordAdded);

    private void AddBareLocalAliases(
        Shumway.Core.LayeredIntMap<int> map, IReadOnlyDictionary<int, int> entries,
        HashSet<int>? recordAdded = null)
        => AddBareLocalAliasesCore(map.ContainsKey, (k, v) => map[k] = v, entries, recordAdded);

    private void AddBareLocalAliasesCore(
        Func<int, bool> containsKey, Action<int, int> add,
        IReadOnlyDictionary<int, int> entries, HashSet<int>? recordAdded)
    {
        foreach (var (mangledFunctorId, address) in entries)
        {
            var (atomId, arity) = FunctorTable.Lookup(mangledFunctorId);
            string mangledName = AtomTable.GetById(atomId)?.Name ?? "";
            int dollar = mangledName.IndexOf('$');
            if (dollar <= 0) continue;
            if (!_modules.ContainsKey(mangledName.Substring(0, dollar))) continue;
            int bareFunctorId = FunctorTable.Intern(
                AtomTable.Intern(mangledName.Substring(dollar + 1), permanent: true).Id,
                arity);
            if (!containsKey(bareFunctorId))
            {
                add(bareFunctorId, address);
                recordAdded?.Add(bareFunctorId);
            }
        }
    }

    /// <summary>Returns the functor ids that are <em>local</em> to a module
    /// (defined as a head functor but not exported via <c>:- public</c>).
    /// Used by <see cref="ModuleRewrite"/> to decide which call targets need
    /// the synthetic <c>module$name</c> prefix.</summary>
    /// <summary>ADR-038 — builds the per-query runtime import map for variable
    /// meta-calls: <c>(moduleAtomId, bareFunctorId) → mangled Source$name functor
    /// id</c>. Returns <c>null</c> when no loaded module imports anything, so the
    /// hot meta-dispatch path pays nothing in the common case.</summary>
    internal static long PackImportKey(int moduleAtomId, int bareFunctorId) =>
        ((long)moduleAtomId << 32) | (uint)bareFunctorId;

    // Cached per derivation generation — imports only change with a consult,
    // but this ran per QUERY, string-interning module + "$" + name for every
    // import of every module each time (a visible slice of warm setup on a
    // many-module load like the Scryer clpz chain).
    private IReadOnlyDictionary<long, int>? _runtimeImportMapCache;
    private int _runtimeImportMapGen = -1;

    private IReadOnlyDictionary<long, int>? BuildRuntimeImportMap()
    {
        if (_runtimeImportMapGen == _derivationGen) return _runtimeImportMapCache;
        Dictionary<long, int>? map = null;
        foreach (var (name, manifest) in _modules)
        {
            if (manifest.Imports.Count == 0) continue;
            int moduleAtomId = AtomTable.Intern(name, permanent: true).Id;
            foreach (var (bareFid, srcModule) in manifest.Imports)
            {
                var (nameAtomId, arity) = FunctorTable.Lookup(bareFid);
                string predName = AtomTable.GetById(nameAtomId)?.Name ?? "";
                int mangledAtom = AtomTable.Intern(
                    srcModule + "$" + predName, permanent: true).Id;
                int mangledSourceFid = FunctorTable.Intern(mangledAtom, arity);
                (map ??= new())[PackImportKey(moduleAtomId, bareFid)] = mangledSourceFid;
            }
        }
        _runtimeImportMapCache = map;
        _runtimeImportMapGen = _derivationGen;
        return map;
    }

    private static HashSet<int> ComputeLocalFunctors(
        IEnumerable<Clause> clauses, HashSet<int> publicFunctors)
    {
        var locals = new HashSet<int>();
        foreach (var c in clauses)
        {
            if (!TryExtractHead(c, out string name, out int arity)) continue;
            int fid = FunctorTable.Intern(
                AtomTable.Intern(name, permanent: true).Id, arity);
            if (!publicFunctors.Contains(fid) && !IsGlobalHookFunctor(fid)) locals.Add(fid);
        }
        return locals;
    }

    internal static bool TryExtractHead(Clause clause, out string name, out int arity)
    {
        Term headTerm = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        switch (headTerm)
        {
            case AtomTerm a: name = a.Name; arity = 0; return true;
            case CompoundTerm c: name = c.Functor; arity = c.Args.Length; return true;
            default: name = ""; arity = 0; return false;
        }
    }

    /// <summary>Throws if more than one module declares the same functor
    /// public — the public namespace is flat across all loaded modules.</summary>
    private void ValidatePublicUniqueness()
    {
        var owner = new Dictionary<int, string>();
        foreach (var (name, manifest) in _modules)
        {
            foreach (int fid in manifest.PublicFunctors)
            {
                if (owner.TryGetValue(fid, out var other))
                {
                    // Multifile escape hatch: if both the
                    // already-owning module and the current one declare
                    // the functor :- multifile, the duplicate is
                    // intentional. Each module's clauses live
                    // independently; the linker concatenates them as if
                    // they came from one source.
                    bool bothMultifile =
                        _modules[other].MultifileFunctors.Contains(fid)
                        && manifest.MultifileFunctors.Contains(fid);
                    if (bothMultifile) continue;

                    var (atomId, arity) = FunctorTable.Lookup(fid);
                    string functorName = AtomTable.GetById(atomId)?.Name ?? "?";
                    throw new InvalidOperationException(
                        $"Functor {functorName}/{arity} is declared :- public in both "
                        + $"module '{other}' and module '{name}'. Public predicates must "
                        + "be unique across the engine (unless both modules also "
                        + "declare it :- multifile).");
                }
                owner[fid] = name;
            }
        }
    }

    private static Solution BuildSolution(
        List<string> varNames, int[] varHeapIndices, Activation engine,
        bool isLast = false,
        PrologEngine? host = null)
    {
        var bindings = new Dictionary<string, Term>(varNames.Count);
        var rootAddrs = new Dictionary<string, int>(varNames.Count);
        for (int i = 0; i < varNames.Count; i++)
        {
            bindings[varNames[i]] = TermReader.Materialize(engine, varHeapIndices[i]);
            // Record the value's root-node address — the address a cyclic
            // term's _C{addr} marker carries when it cycles back to the root,
            // so the REPL can display the cycle as the variable itself.
            int addr = engine.Deref(varHeapIndices[i]);
            Cell c = engine.GetHeap(addr);
            int root = c.Tag switch
            {
                Tag.Lis or Tag.Str => c.AsHeapIndex,
                Tag.Functor => addr,
                _ => -1,
            };
            if (root >= 0) rootAddrs[varNames[i]] = root;
        }
        return new Solution(success: true, bindings: bindings, isLast: isLast, engine: host,
            valueRootAddresses: rootAddrs);
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

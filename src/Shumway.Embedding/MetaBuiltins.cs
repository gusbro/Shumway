using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Meta-predicate builtins (predicates that call other predicates as goals).
/// They live in the Embedding layer rather than <c>Shumway.Builtins</c>
/// because their implementations spawn sub-<see cref="PrologEngine"/>s, which
/// Builtins can't reference without creating a circular dependency. Registered
/// from <see cref="PrologEngine"/>'s constructor through
/// <see cref="EnsureRegistered"/>.
/// </summary>
public static class MetaBuiltins
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        BuiltinsRegistry.Register("findall", 3, Findall);
        BuiltinsRegistry.Register("bagof",   3, Bagof);
        BuiltinsRegistry.Register("setof",   3, Setof);
        BuiltinsRegistry.Register("copy_term", 2, CopyTerm);

        BuiltinsRegistry.Register("call", 1, Call1);
        BuiltinsRegistry.Register("call", 2, Call2);
        BuiltinsRegistry.Register("call", 3, Call3);
        BuiltinsRegistry.Register("call", 4, Call4);
        BuiltinsRegistry.Register("call", 5, Call5);
        BuiltinsRegistry.Register("call", 6, Call6);
        BuiltinsRegistry.Register("call", 7, Call7);

        BuiltinsRegistry.Register("assertz", 1, Assertz);
        BuiltinsRegistry.Register("asserta", 1, Asserta);
        BuiltinsRegistry.Register("retract", 1, Retract);

        BuiltinsRegistry.Register("throw", 1, Throw);
        BuiltinsRegistry.Register("catch", 3, Catch);

        BuiltinsRegistry.Register("clause",            2, Clause);
        BuiltinsRegistry.Register("current_predicate", 1, CurrentPredicate);
        BuiltinsRegistry.Register("abolish",           1, Abolish);

        BuiltinsRegistry.Register("numbervars",        3, NumberVars);
    }

    // ============================================================================
    // numbervars/3
    // ============================================================================

    /// <summary><c>numbervars(Term, Start, End)</c> — walks <c>Term</c>
    /// left-to-right and binds every distinct unbound variable to a
    /// compound <c>'$VAR'(N)</c> with consecutive integers starting at
    /// <c>Start</c>. The next-free integer is unified with <c>End</c>.
    ///
    /// <para>Shared variables (same heap address visited twice) get the
    /// same number — the walk derefs each cell before deciding. Already-
    /// bound variables and non-variable subterms pass through unchanged.
    /// Mostly used to make terms presentable before printing or
    /// asserting.</para></summary>
    public static bool NumberVars(Engine engine)
    {
        Cell startC = engine.GetRegister(1);
        Cell startDeref = startC.Tag == Tag.Ref
            ? engine.GetHeap(engine.Deref(startC.AsHeapIndex))
            : startC;
        if (startDeref.Tag != Tag.Int)
            throw new InvalidOperationException(
                "numbervars/3: second argument (Start) must be a ground integer.");
        long start = startDeref.AsInt;

        // Copy the input register to a heap slot so we have a stable address
        // to walk from. The walk visits each cell, derefs, and on the first
        // sight of an unbound REF binds it to a fresh '$VAR'(N) compound.
        int rootSlot = engine.AllocateHeap(1);
        engine.SetHeap(rootSlot, engine.GetRegister(0));

        var visited = new HashSet<int>();
        long counter = start;
        WalkAndNumber(engine, rootSlot, visited, ref counter);

        return engine.UnifyRegisterWithCell(2, Cell.Int(counter));
    }

    private static void WalkAndNumber(
        Engine engine, int heapIdx, HashSet<int> visited, ref long counter)
    {
        int addr = engine.Deref(heapIdx);
        if (!visited.Add(addr)) return;

        Cell cell = engine.GetHeap(addr);
        switch (cell.Tag)
        {
            case Tag.Ref:
                // Unbound — bind to '$VAR'(counter).
                int varAtom = AtomTable.Intern("$VAR", permanent: true).Id;
                int functorId = FunctorTable.Intern(varAtom, 1);
                int strBase = engine.AllocateHeap(3);
                engine.SetHeap(strBase, Cell.Str(strBase + 1));
                engine.SetHeap(strBase + 1, Cell.Functor(functorId));
                engine.SetHeap(strBase + 2, Cell.Int(counter));
                counter++;
                // Bind addr to the new STR via a Ref to it (so trail catches it).
                int strRefSlot = engine.AllocateHeap(1);
                engine.SetHeap(strRefSlot, Cell.Ref(strBase));
                engine.Unify(addr, strRefSlot);
                break;

            case Tag.Str:
            {
                int functorIdx = cell.AsHeapIndex;
                var (_, arity) = FunctorTable.Lookup(
                    engine.GetHeap(functorIdx).AsFunctorId);
                for (int i = 0; i < arity; i++)
                    WalkAndNumber(engine, functorIdx + 1 + i, visited, ref counter);
                break;
            }
            case Tag.Lis:
            {
                int headIdx = cell.AsHeapIndex;
                WalkAndNumber(engine, headIdx, visited, ref counter);
                WalkAndNumber(engine, headIdx + 1, visited, ref counter);
                break;
            }
            // Atoms, ints, floats, PSTRs: leaf, nothing to do.
        }
    }

    // ============================================================================
    // clause/2, current_predicate/1, abolish/1
    // ============================================================================

    /// <summary><c>clause(Head, Body)</c> — succeeds with the first stored
    /// clause whose head unifies with <c>Head</c> and body with
    /// <c>Body</c>. Searches the dynamic store first (in assertion order)
    /// then the static clauses of every loaded module.
    ///
    /// <para>Phase-1 limitation: returns only the first match. ISO
    /// <c>clause/2</c> is multi-solution; full backtracking through clause
    /// candidates needs the call/N choice-point integration that's not in
    /// v1.</para></summary>
    public static bool Clause(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "clause/2 requires the engine to be hosted by a PrologEngine.");

        Term headPattern = MaterializeRegister(engine, 0);
        int fid = ExtractCallableFunctorId(headPattern, "clause/2");

        var candidates = new List<Clause>();
        candidates.AddRange(host.DynamicClausesFor(fid));
        candidates.AddRange(host.StaticClausesFor(fid));

        foreach (var candidate in candidates)
        {
            // Build a wrapping `:- /2 Head Body` term so head + body share
            // one Materialize call (var identity preserved across them).
            Term head = candidate.Kind == ClauseKind.Rule
                ? ((CompoundTerm)candidate.Term).Args[0]
                : candidate.Term;
            Term body = candidate.Kind == ClauseKind.Rule
                ? ((CompoundTerm)candidate.Term).Args[1]
                : new AtomTerm("true");
            Term pair = new CompoundTerm(":-", new[] { head, body });

            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            Cell wrapperCell = Materializer.MaterializeAsCell(engine, pair);
            // wrapperCell is Cell.Ref(strBase). Args live at strBase+2 and
            // strBase+3 (one STR + one Functor cell come first).
            int strBase = wrapperCell.AsHeapIndex;
            int headAddr = strBase + 2;
            int bodyAddr = strBase + 3;

            bool ok = engine.UnifyRegisterWithHeapAt(0, headAddr)
                   && engine.UnifyRegisterWithHeapAt(1, bodyAddr);
            if (ok)
            {
                engine.SetHb(savedHb);
                return true;
            }

            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
        }
        return false;
    }

    /// <summary><c>current_predicate(Name/Arity)</c> — Phase-1 ground-mode
    /// only: succeeds iff a predicate with the given functor signature is
    /// loaded (built-in, static, or dynamic). Variable-mode enumeration
    /// would need a sub-engine wrapper around the full predicate index;
    /// landing in a later chunk.</summary>
    public static bool CurrentPredicate(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "current_predicate/1 requires a PrologEngine host.");

        Term spec = MaterializeRegister(engine, 0);
        if (spec is CompoundTerm c && c.Functor == "/" && c.Args.Length == 2
            && c.Args[0] is AtomTerm name && c.Args[1] is IntTerm arity)
        {
            int fid = FunctorTable.Intern(
                AtomTable.Intern(name.Name, permanent: true).Id, (int)arity.Value);
            return host.HasPredicate(fid);
        }

        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        throw new ShumwayPrologException(
            IsoError.TypeError("predicate_indicator", spec));
    }

    /// <summary><c>abolish(Name/Arity)</c> — removes every asserted clause
    /// of the named dynamic predicate and unregisters it so subsequent
    /// assertions raise the "not declared dynamic" error until a new
    /// <c>:- dynamic</c> declaration arrives.</summary>
    public static bool Abolish(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "abolish/1 requires a PrologEngine host.");

        Term spec = MaterializeRegister(engine, 0);
        if (spec is CompoundTerm c && c.Functor == "/" && c.Args.Length == 2
            && c.Args[0] is AtomTerm name && c.Args[1] is IntTerm arity)
        {
            int fid = FunctorTable.Intern(
                AtomTable.Intern(name.Name, permanent: true).Id, (int)arity.Value);
            host.AbolishDynamic(fid);
            return true;
        }

        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        throw new ShumwayPrologException(
            IsoError.TypeError("predicate_indicator", spec));
    }

    private static int ExtractCallableFunctorId(Term head, string builtinName)
    {
        return head switch
        {
            AtomTerm a => FunctorTable.Intern(
                AtomTable.Intern(a.Name, permanent: true).Id, 0),
            CompoundTerm c => FunctorTable.Intern(
                AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length),
            VarTerm => throw new ShumwayPrologException(IsoError.InstantiationError()),
            _ => throw new ShumwayPrologException(
                IsoError.TypeError("callable", head)),
        };
    }

    // ============================================================================
    // throw / catch
    // ============================================================================

    /// <summary><c>throw(Error)</c> — raises <see cref="ShumwayPrologException"/>
    /// carrying <c>Error</c>'s materialised term. Propagates up the C# stack
    /// until a <c>catch/3</c> or the engine's top-level intercepts it.</summary>
    public static bool Throw(Engine engine)
    {
        Term error = MaterializeRegister(engine, 0);
        throw new ShumwayPrologException(error);
    }

    /// <summary><c>catch(Goal, Catcher, Recovery)</c> — runs <c>Goal</c> in a
    /// peer sub-engine.
    /// <list type="bullet">
    /// <item>If <c>Goal</c> succeeds, the first solution's bindings flow back
    ///   to the caller and <c>catch</c> succeeds without consulting
    ///   <c>Catcher</c> / <c>Recovery</c>.</item>
    /// <item>If <c>Goal</c> fails cleanly, <c>catch</c> fails too.</item>
    /// <item>If <c>Goal</c> throws (via <c>throw/1</c>), the thrown term is
    ///   materialised on the caller's heap and trial-unified with
    ///   <c>Catcher</c>. On a match, the bindings stick and <c>Recovery</c>
    ///   runs in a fresh sub-engine; on a mismatch, the trial bindings are
    ///   unwound and the original exception is re-raised.</item>
    /// </list></summary>
    public static bool Catch(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "catch/3 requires the engine to be hosted by a PrologEngine.");

        Term goal = MaterializeRegister(engine, 0);

        var sub = host.CreateSubEngine();
        try
        {
            foreach (Solution sol in sub.QueryAll(goal))
            {
                BindBack(engine, sol.Bindings);
                return true;
            }
            return false;
        }
        catch (ShumwayPrologException ex)
        {
            // Trial-unify the thrown term with the caller's Catcher register.
            // The state save / unwind matches retract's pattern: if the
            // unification fails we must roll back every speculative binding
            // before re-raising so the surrounding context sees the engine
            // state it had before the throw.
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            Cell thrownCell = Materializer.MaterializeAsCell(engine, ex.Term);
            int thrownSlot = engine.AllocateHeap(1);
            engine.SetHeap(thrownSlot, thrownCell);

            if (!engine.UnifyRegisterWithHeapAt(1, thrownSlot))
            {
                engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
                engine.SetHeapTop(savedHeapTop);
                engine.SetHb(savedHb);
                throw;   // rethrow the original ShumwayPrologException
            }
            engine.SetHb(savedHb);

            // Catcher matched — its bindings stick. Re-read Recovery now so
            // any variables shared with Catcher show up substituted.
            Term recovery = MaterializeRegister(engine, 2);
            var sub2 = host.CreateSubEngine();
            foreach (Solution sol in sub2.QueryAll(recovery))
            {
                BindBack(engine, sol.Bindings);
                return true;
            }
            return false;
        }
    }

    /// <summary>Shared helper: walks a sub-engine solution's bindings and
    /// unifies each caller-heap variable (identified by the <c>_GN</c> name
    /// convention) with the bound value materialised onto the caller's
    /// heap. Returns <c>false</c> at the first unification failure so the
    /// outer builtin can give up its current iteration.</summary>
    private static bool BindBack(Engine engine, IReadOnlyDictionary<string, Term> bindings)
    {
        foreach (var (name, value) in bindings)
        {
            int addr = ExtractAddrFromName(name);
            if (addr < 0) continue;
            Cell boundCell = Materializer.MaterializeAsCell(engine, value);
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, boundCell);
            if (!engine.Unify(addr, slot)) return false;
        }
        return true;
    }

    // ============================================================================
    // call/N — runtime meta-call via sub-engine + bind-back of input vars
    // ============================================================================

    public static bool Call1(Engine engine) => CallN(engine, totalArity: 1);
    public static bool Call2(Engine engine) => CallN(engine, totalArity: 2);
    public static bool Call3(Engine engine) => CallN(engine, totalArity: 3);
    public static bool Call4(Engine engine) => CallN(engine, totalArity: 4);
    public static bool Call5(Engine engine) => CallN(engine, totalArity: 5);
    public static bool Call6(Engine engine) => CallN(engine, totalArity: 6);
    public static bool Call7(Engine engine) => CallN(engine, totalArity: 7);

    /// <summary><c>call(Goal, ExtraArgs...)</c> — runs <c>Goal</c> (optionally
    /// extended with extra args appended to its argument list) in a peer
    /// sub-engine and propagates the first solution's bindings back into the
    /// caller's heap.
    ///
    /// <para>Implementation: the input registers are read as
    /// <see cref="Term"/>s with synthetic <c>_GN</c> variable names that
    /// encode the caller's heap address. The composed goal runs in the
    /// sub-engine; the resulting <see cref="Solution"/> binds each
    /// <c>_GN</c>, and we use the embedded address to find the caller's
    /// variable cell and unify it with the materialised bound term. Only the
    /// first solution is taken — multi-solution call/N would need a runtime
    /// "execute-by-functor" opcode that's not in v1.</para></summary>
    private static bool CallN(Engine engine, int totalArity)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "call/N requires the engine to be hosted by a PrologEngine.");

        Term goal = MaterializeRegister(engine, 0);
        var extras = new Term[totalArity - 1];
        for (int i = 0; i < extras.Length; i++)
            extras[i] = MaterializeRegister(engine, i + 1);

        Term callGoal = AppendArgs(goal, extras);

        var sub = host.CreateSubEngine();
        foreach (Solution sol in sub.QueryAll(callGoal))
        {
            foreach (var (name, value) in sol.Bindings)
            {
                int addr = ExtractAddrFromName(name);
                if (addr < 0) continue;
                Cell boundCell = Materializer.MaterializeAsCell(engine, value);
                int slot = engine.AllocateHeap(1);
                engine.SetHeap(slot, boundCell);
                if (!engine.Unify(addr, slot)) return false;
            }
            return true;
        }
        return false;
    }

    private static Term AppendArgs(Term goal, Term[] extras)
    {
        if (extras.Length == 0) return goal;
        return goal switch
        {
            AtomTerm a => new CompoundTerm(a.Name, extras),
            CompoundTerm c => new CompoundTerm(
                c.Functor,
                c.Args.Concat(extras).ToArray()),
            _ => throw new InvalidOperationException(
                "call/N: goal must be an atom or compound."),
        };
    }

    private static int ExtractAddrFromName(string name)
    {
        if (name.Length >= 3 && name[0] == '_' && name[1] == 'G'
            && int.TryParse(name.AsSpan(2), out int addr))
            return addr;
        return -1;
    }

    // ============================================================================
    // assertz / asserta / retract
    // ============================================================================

    public static bool Assertz(Engine engine) => AssertImpl(engine, prepend: false);
    public static bool Asserta(Engine engine) => AssertImpl(engine, prepend: true);

    private static bool AssertImpl(Engine engine, bool prepend)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "assert: PrologEngine host required.");

        Term clauseTerm = MaterializeRegister(engine, 0);
        var clause = Shumway.Compiler.Ast.Clause.From(clauseTerm);
        if (prepend) host.Asserta(clause);
        else host.Assertz(clause);
        return true;
    }

    /// <summary><c>retract(Clause)</c> — finds the first asserted clause
    /// whose head (and body, if <c>Clause</c> is a rule) unifies with the
    /// pattern, removes it from the dynamic store, and keeps the resulting
    /// bindings. Fails when no clause matches.</summary>
    public static bool Retract(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "retract: PrologEngine host required.");

        Term pattern = MaterializeRegister(engine, 0);
        var patternClause = Shumway.Compiler.Ast.Clause.From(pattern);
        int patternFid = ExtractHeadFunctorIdFromClause(patternClause);

        var candidates = host.DynamicClausesFor(patternFid);
        if (candidates.Count == 0) return false;

        foreach (var candidate in candidates)
        {
            // Trial-unify against a fresh copy of the candidate clause. If it
            // matches, commit (keep the bindings, drop the original from the
            // dynamic store); if it doesn't, unwind every speculative
            // binding the trial made before trying the next candidate.
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            Cell candidateCell = Materializer.MaterializeAsCell(engine, candidate.Term);
            int candSlot = engine.AllocateHeap(1);
            engine.SetHeap(candSlot, candidateCell);

            if (engine.UnifyRegisterWithHeapAt(0, candSlot))
            {
                host.RemoveDynamicByReference(patternFid, candidate);
                engine.SetHb(savedHb);
                return true;
            }

            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
        }
        return false;
    }

    private static int ExtractHeadFunctorIdFromClause(Clause clause)
    {
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        return head switch
        {
            AtomTerm a => FunctorTable.Intern(
                AtomTable.Intern(a.Name, permanent: true).Id, 0),
            CompoundTerm c => FunctorTable.Intern(
                AtomTable.Intern(c.Functor, permanent: true).Id, c.Args.Length),
            _ => throw new InvalidOperationException(
                "retract: clause pattern head must be atom or compound."),
        };
    }

    /// <summary><c>copy_term(Term, Copy)</c> — unifies <c>Copy</c> with a
    /// fresh-variable copy of <c>Term</c>. Bound subterms are preserved by
    /// value; unbound variables become brand-new unbound variables in the
    /// copy, with sharing preserved (multiple occurrences of the same input
    /// var map to one new var).
    ///
    /// <para>Implementation: <see cref="TermReader.Materialize"/> walks the
    /// input and turns truly-unbound REFs into <see cref="VarTerm"/>s with
    /// synthetic <c>_GN</c> names keyed by heap address; the immediately
    /// following <see cref="Materializer.MaterializeAsCell"/> call uses a
    /// fresh var-name → heap-index map, so each <c>_GN</c> resolves to a new
    /// unbound — and shared occurrences in the AST keep sharing.</para></summary>
    public static bool CopyTerm(Engine engine)
    {
        Term original = MaterializeRegister(engine, 0);
        Cell copyCell = Materializer.MaterializeAsCell(engine, original);
        return engine.UnifyRegisterWithCell(1, copyCell);
    }

    /// <summary><c>findall(Template, Goal, List)</c> — runs <c>Goal</c> in a
    /// fresh peer engine, captures the value of <c>Template</c> at every
    /// solution, and unifies <c>List</c> with the resulting list (empty when
    /// no solution exists).
    ///
    /// <para>The sub-engine approach sidesteps choice-point stack manipulation
    /// on the calling engine. The trade-off is that <c>Template</c> and
    /// <c>Goal</c> have to round-trip through the AST <see cref="Term"/>
    /// representation — variable identity is preserved via the synthetic
    /// <c>_GN</c> names <see cref="TermReader"/> assigns, which is why the
    /// substitution step at the end works.</para></summary>
    public static bool Findall(Engine engine)
    {
        var results = CollectSolutions(engine, stripExistentials: false);
        return BindList(engine, results);
    }

    /// <summary><c>bagof(Template, Goal, Bag)</c> — like <c>findall/3</c> but
    /// <em>fails</em> when <c>Goal</c> has no solutions instead of returning
    /// <c>[]</c>. ISO bagof also splits the solution stream by free-variable
    /// groupings; Phase 1 doesn't do that yet, so this implementation is
    /// effectively "findall + fail-on-empty". The <c>Var^Goal</c> existential
    /// quantifier is recognised and stripped (every var is implicitly
    /// existential without grouping, so it's a no-op).</summary>
    public static bool Bagof(Engine engine)
    {
        var results = CollectSolutions(engine, stripExistentials: true);
        if (results.Count == 0) return false;
        return BindList(engine, results);
    }

    /// <summary><c>setof(Template, Goal, Set)</c> — like <c>bagof/3</c> but
    /// the result is sorted in standard order and duplicate terms are
    /// removed. Like bagof, fails when no solutions exist. The sort runs
    /// on the AST level via <see cref="TermStandardOrder.Compare"/> so the
    /// outcome only depends on solution content, not on which heap
    /// addresses the sub-engine happened to allocate.</summary>
    public static bool Setof(Engine engine)
    {
        var results = CollectSolutions(engine, stripExistentials: true);
        if (results.Count == 0) return false;

        results.Sort(TermStandardOrder.Compare);

        // Dedup adjacent equals in place.
        int write = 1;
        for (int read = 1; read < results.Count; read++)
        {
            if (TermStandardOrder.Compare(results[read], results[write - 1]) != 0)
                results[write++] = results[read];
        }
        if (write < results.Count) results.RemoveRange(write, results.Count - write);

        return BindList(engine, results);
    }

    /// <summary>Shared workhorse for findall/bagof/setof: reads Template and
    /// Goal, optionally strips <c>^/2</c> existential wrappers from the
    /// goal, runs it in a peer engine, and projects each solution's
    /// bindings through Template. The result list is built by the
    /// per-builtin tail logic (which decides what to do on empty).</summary>
    private static List<Term> CollectSolutions(Engine engine, bool stripExistentials)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "Collection meta-builtins require the engine to be hosted by "
                + "a PrologEngine. Engine.Host is "
                + (engine.Host?.GetType().Name ?? "null") + ".");

        Term template = MaterializeRegister(engine, 0);
        Term goal = MaterializeRegister(engine, 1);
        if (stripExistentials) goal = StripExistentials(goal);

        var sub = host.CreateSubEngine();
        var results = new List<Term>();
        foreach (Solution sol in sub.QueryAll(goal))
            results.Add(Substitute(template, sol.Bindings));
        return results;
    }

    /// <summary>Builds a Prolog list from the collected results and unifies
    /// it with the caller's third argument.</summary>
    private static bool BindList(Engine engine, IReadOnlyList<Term> results)
    {
        Term listTerm = new AtomTerm("[]");
        for (int i = results.Count - 1; i >= 0; i--)
            listTerm = new CompoundTerm(".", new[] { results[i], listTerm });

        Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
        return engine.UnifyRegisterWithCell(2, listCell);
    }

    /// <summary>Strips any leading <c>^/2</c> existential wrappers off a goal.
    /// <c>X^Y^Goal</c> reduces to <c>Goal</c>; the stripped variables become
    /// ordinary free variables of the inner goal. Without solution grouping
    /// this is purely a no-op syntactically — every variable is already
    /// existential — but stripping makes ISO-compliant user code work.</summary>
    private static Term StripExistentials(Term goal)
    {
        while (goal is CompoundTerm c && c.Functor == "^" && c.Args.Length == 2)
            goal = c.Args[1];
        return goal;
    }

    /// <summary>Reads the term currently bound in <c>X[<paramref name="regIdx"/>]</c>
    /// as an AST <see cref="Term"/>. Wraps the register's cell on the heap
    /// briefly so the existing <see cref="TermReader.Materialize"/> can do its
    /// REF-chasing work uniformly — for atomic registers this costs one
    /// throwaway heap cell.</summary>
    private static Term MaterializeRegister(Engine engine, int regIdx)
    {
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, engine.GetRegister(regIdx));
        return TermReader.Materialize(engine, slot);
    }

    /// <summary>Walks <paramref name="term"/> and replaces every
    /// <see cref="VarTerm"/> whose name appears in
    /// <paramref name="bindings"/> with its bound value. Used by
    /// <see cref="Findall"/> to project the sub-engine's solution bindings
    /// through the user-supplied template.</summary>
    private static Term Substitute(Term term, IReadOnlyDictionary<string, Term> bindings)
    {
        switch (term)
        {
            case VarTerm v when bindings.TryGetValue(v.Name, out Term? bound):
                // Recurse: a binding might itself contain variables that we
                // need to further substitute (the sub-engine reports
                // dereferenced terms, but a residual unbound var has its
                // _GN name preserved and shouldn't be re-walked endlessly).
                return Substitute(bound, bindings);
            case CompoundTerm c:
                var newArgs = new Term[c.Args.Length];
                for (int i = 0; i < c.Args.Length; i++)
                    newArgs[i] = Substitute(c.Args[i], bindings);
                return new CompoundTerm(c.Functor, newArgs);
            default:
                return term;
        }
    }
}

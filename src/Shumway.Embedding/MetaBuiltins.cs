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

        const string FindAgg = "Findall & aggregation";
        const string Control = "Control";
        const string Database = "Database";
        const string Term = "Term inspection & construction";
        const string Reflect = "Flags, operators & reflection";
        const string Io = "Input / output";

        BuiltinsRegistry.Register("findall", 3, Findall,
            FindAgg, "findall(?Template, :Goal, -List)", "Collects an instance of Template for every solution of Goal into a list.");
        // In-engine findall plumbing (chunk 83) — MetaTransform rewrites
        // findall/3 with a callable goal into a goal sequence using these.
        BuiltinsRegistry.Register("$findall_push",    0, FindallPush);
        BuiltinsRegistry.Register("$findall_record",  1, FindallRecord);
        BuiltinsRegistry.Register("$findall_collect", 1, FindallCollect);
        // In-engine bagof/setof plumbing (chunk 84) — reuse the findall
        // frame stack ('$findall_push' / '$findall_record'); only the
        // collect step differs (it groups the solutions by witness).
        BuiltinsRegistry.Register("$bagof_collect",   1, BagofCollect);
        BuiltinsRegistry.Register("$setof_collect",   1, SetofCollect);
        BuiltinsRegistry.Register("bagof",   3, Bagof,
            FindAgg, "bagof(?Template, :Goal, -List)", "Collects Goal's solutions, grouped by free-variable witness; fails when there are none.");
        BuiltinsRegistry.Register("setof",   3, Setof,
            FindAgg, "setof(?Template, :Goal, -List)", "Like bagof/3 but the result list is sorted and duplicate-free.");
        BuiltinsRegistry.Register("forall",  2, Forall,
            FindAgg, "forall(:Condition, :Action)", "Succeeds if Action holds for every solution of Condition.");
        BuiltinsRegistry.Register("copy_term", 2, CopyTerm,
            Term, "copy_term(+Term, -Copy)", "Copies a term with fresh variables.");
        BuiltinsRegistry.Register("$copy_term_3_prep", 3, CopyTerm3Prep);

        BuiltinsRegistry.Register("call", 1, Call1,
            Control, "call(:Goal)", "Calls a goal.");
        BuiltinsRegistry.Register("call", 2, Call2,
            Control, "call(:Goal, +Extra1)", "Calls a goal extended with one extra argument.");
        BuiltinsRegistry.Register("call", 3, Call3,
            Control, "call(:Goal, +Extra1, +Extra2)", "Calls a goal extended with two extra arguments.");
        BuiltinsRegistry.Register("call", 4, Call4,
            Control, "call(:Goal, +Extra1, ..., +Extra3)", "Calls a goal extended with three extra arguments.");
        BuiltinsRegistry.Register("call", 5, Call5,
            Control, "call(:Goal, +Extra1, ..., +Extra4)", "Calls a goal extended with four extra arguments.");
        BuiltinsRegistry.Register("call", 6, Call6,
            Control, "call(:Goal, +Extra1, ..., +Extra5)", "Calls a goal extended with five extra arguments.");
        BuiltinsRegistry.Register("call", 7, Call7,
            Control, "call(:Goal, +Extra1, ..., +Extra6)", "Calls a goal extended with six extra arguments.");
        // '$call'/2 (chunk 88): a cut-barrier-carrying meta-call. The
        // $call_* control helpers re-enter call dispatch through it so a
        // `!` in a runtime compound goal cuts to the enclosing call's
        // barrier. Like call/N it is intercepted by the interpreter.
        BuiltinsRegistry.Register("$call", 2, CallWithBarrier);

        BuiltinsRegistry.Register("assertz", 1, Assertz,
            Database, "assertz(+Clause)", "Adds a clause to the end of its dynamic predicate.");
        BuiltinsRegistry.Register("asserta", 1, Asserta,
            Database, "asserta(+Clause)", "Adds a clause to the front of its dynamic predicate.");
        BuiltinsRegistry.Register("retract", 1, Retract,
            Database, "retract(+Clause)", "Removes the first clause that unifies with the argument.");

        BuiltinsRegistry.Register("throw", 1, Throw,
            Control, "throw(+Exception)", "Throws an exception term, unwinding to the nearest catch/3.");
        BuiltinsRegistry.Register("catch", 3, Catch,
            Control, "catch(:Goal, +Catcher, :Recovery)", "Runs Goal, running Recovery if a thrown exception unifies with Catcher.");
        // In-engine catch/3 plumbing (chunk 85) — MetaTransform rewrites a
        // catch/3 with a callable goal into a goal-helper guarded by these.
        BuiltinsRegistry.Register("$catch_begin", 2, CatchBegin);
        BuiltinsRegistry.Register("$catch_end",   0, CatchEnd);

        // clause/2 and current_predicate/1 are now Prolog-level predicates
        // defined in the prelude (chunk 40). They call these helpers to
        // bridge into the engine's clause and functor stores, then iterate
        // via the prelude's member/2.
        BuiltinsRegistry.Register("$all_clauses_of",            2, AllClausesOf);
        BuiltinsRegistry.Register("$all_predicate_indicators",  1, AllPredicateIndicators);
        BuiltinsRegistry.Register("$listable_predicates", 1, ListablePredicates);
        // Tabling (chunk 106) — a per-engine string set giving the
        // semi-naive driver an O(1) "is this answer new?" test.
        BuiltinsRegistry.Register("$tbl_seen", 1, TableSeen);
        BuiltinsRegistry.Register("abolish",                    1, Abolish,
            Database, "abolish(+PredicateIndicator)", "Removes every clause of the named dynamic predicate.");

        BuiltinsRegistry.Register("numbervars",        3, NumberVars,
            Term, "numbervars(+Term, +Start, -End)", "Binds the unbound variables of Term to '$VAR'(N) terms with consecutive N from Start.");
        BuiltinsRegistry.Register("term_to_atom",      2, TermToAtom,
            Term, "term_to_atom(?Term, ?Atom)", "Converts between a term and its textual atom representation.");

        BuiltinsRegistry.Register("functor", 3, Functor,
            Term, "functor(?Term, ?Name, ?Arity)", "Relates a term to its functor name and arity.");
        BuiltinsRegistry.Register("arg",     3, Arg,
            Term, "arg(+N, +Term, ?Arg)", "Unifies Arg with the Nth argument of the compound term.");
        BuiltinsRegistry.Register("=..",     2, Univ,
            Term, "=..(?Term, ?List)", "Relates a term to the list of its functor and arguments.");

        BuiltinsRegistry.Register("read_term_from_atom", 2, ReadTermFromAtom,
            Term, "read_term_from_atom(+Atom, -Term)", "Parses an atom into a term.");

        BuiltinsRegistry.Register("op", 3, Op,
            Reflect, "op(+Priority, +Type, +Name)", "Declares an operator of the given priority and type.");
        BuiltinsRegistry.Register("set_prolog_flag",     2, SetPrologFlag,
            Reflect, "set_prolog_flag(+Flag, +Value)", "Sets a Prolog flag.");
        BuiltinsRegistry.Register("current_prolog_flag", 2, CurrentPrologFlag,
            Reflect, "current_prolog_flag(?Flag, ?Value)", "Reads the value of a Prolog flag.");
        BuiltinsRegistry.Register("with_output_to", 2, WithOutputTo,
            Io, "with_output_to(+Sink, :Goal)", "Runs a goal, capturing its output into an atom, string or code list.");
        BuiltinsRegistry.Register("atom_to_term",   3, AtomToTerm,
            Term, "atom_to_term(+Atom, -Term, -Bindings)", "Parses an atom into a term plus its variable bindings.");
        BuiltinsRegistry.Register("read_term_from_stream", 2, ReadTermFromStream,
            Io, "read_term_from_stream(+Stream, -Term)", "Reads one term from a read-mode stream.");
        // ISO read_term/2 — accepts a stream handle in arg 1 and unifies
        // the parsed term with arg 2. Chunk 59: delegate to the existing
        // stream-aware reader so the builtin set covers both names.
        BuiltinsRegistry.Register("read_term", 2, ReadTermFromStream,
            Io, "read_term(+Stream, -Term)", "Reads one term from a read-mode stream.");
    }

    /// <summary><c>read_term_from_stream(Stream, Term)</c> — reads
    /// characters from a read-mode stream until it sees a clause-ending
    /// <c>.</c> followed by whitespace or EOF, parses the buffer as a
    /// Prolog term, and unifies the result with <c>Term</c>. Hits EOF
    /// before any text yields the atom <c>end_of_file</c>.</summary>
    public static bool ReadTermFromStream(Engine engine)
    {
        Cell handleCell = ResolveLocal(engine, engine.GetRegister(0));
        if (handleCell.Tag != Tag.Foreign)
            throw new PrologRuntimeException("type_error", "stream");
        var reader = engine.AsForeign<System.IO.StreamReader>(handleCell);
        if (reader is null)
            throw new PrologRuntimeException("existence_error", "stream");

        var sb = new System.Text.StringBuilder();
        bool sawAnyChar = false;
        while (true)
        {
            int c = reader.Read();
            if (c < 0)
            {
                if (!sawAnyChar)
                {
                    int eofId = AtomTable.Intern("end_of_file", permanent: true).Id;
                    return engine.UnifyRegisterWithCell(1, Cell.Atom(eofId));
                }
                break;
            }
            sawAnyChar = true;
            sb.Append((char)c);
            if (c == '.')
            {
                int next = reader.Peek();
                if (next < 0 || char.IsWhiteSpace((char)next)) break;
            }
        }

        var parser = new Shumway.Compiler.Parsing.Parser(
            new Shumway.Compiler.Lexer.Lexer(sb.ToString()),
            Shumway.Compiler.Parsing.OperatorTable.Default());
        Term parsed = parser.ReadClauseTerm();
        Cell cell = Materializer.MaterializeAsCell(engine, parsed);
        return engine.UnifyRegisterWithCell(1, cell);
    }

    /// <summary><c>with_output_to(Sink, Goal)</c> — runs <c>Goal</c> with
    /// the engine's output sink temporarily redirected. Phase 1
    /// recognises <c>atom(A)</c> and <c>string(S)</c> sinks: both capture
    /// everything <c>Goal</c> writes (via <c>write/1</c>, <c>format/2</c>,
    /// etc.) and unify the result with their inner variable. The sub-
    /// engine spawned for <c>Goal</c> uses the redirected sink for the
    /// duration of the call; the parent's <see cref="PrologEngine.Out"/>
    /// is untouched.</summary>
    public static bool WithOutputTo(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "with_output_to/2 requires the engine to be hosted by a PrologEngine.");

        // Read the Sink term (X[0]) and the Goal term (X[1]).
        Cell sinkCell = ResolveLocal(engine, engine.GetRegister(0));
        if (sinkCell.Tag != Tag.Str)
            throw new ShumwayPrologException(
                IsoError.TypeError("output_sink_spec", new VarTerm("_")));
        int functorIdx = sinkCell.AsHeapIndex;
        var (atomId, arity) = FunctorTable.Lookup(
            engine.GetHeap(functorIdx).AsFunctorId);
        string sinkType = AtomTable.GetById(atomId)?.Name ?? "";
        if (arity != 1 || (sinkType != "atom" && sinkType != "string"))
            throw new ShumwayPrologException(
                IsoError.DomainError("output_sink", new VarTerm("_")));

        Term goal = MaterializeRegister(engine, 1);
        var sw = new System.IO.StringWriter();
        var sub = host.CreateSubEngine();
        sub.Out = sw;

        bool succeeded = false;
        foreach (Solution sol in sub.QueryAll(goal))
        {
            BindBack(engine, sol.Bindings);
            succeeded = true;
            break;
        }

        // Whether or not Goal succeeded, expose the captured text — that's
        // the SWI convention. (Caller can still observe failure via the
        // return value.)
        string captured = sw.ToString();
        int sinkArgAddr = functorIdx + 1;
        if (sinkType == "atom")
        {
            int aid = AtomTable.Intern(captured, permanent: false).Id;
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, Cell.Atom(aid));
            if (!engine.Unify(sinkArgAddr, slot)) return false;
        }
        else // string
        {
            int pstrIdx = engine.MakePstr(captured);
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, Cell.Ref(pstrIdx));
            if (!engine.Unify(sinkArgAddr, slot)) return false;
        }
        return succeeded;
    }

    /// <summary><c>atom_to_term(Atom, Term, Bindings)</c> — parses
    /// <c>Atom</c>'s text as a Prolog term, unifies the result with
    /// <c>Term</c>, and unifies <c>Bindings</c> with a list of
    /// <c>'='(Name, Var)</c> compounds for each named variable.</summary>
    public static bool AtomToTerm(Engine engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));
        if (atomCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(IsoError.TypeError("atom", new VarTerm("_")));
        string source = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        if (!source.TrimEnd().EndsWith(".", StringComparison.Ordinal))
            source += ".";

        var parser = new Shumway.Compiler.Parsing.Parser(
            new Shumway.Compiler.Lexer.Lexer(source),
            Shumway.Compiler.Parsing.OperatorTable.Default());
        Term parsed = parser.ReadClauseTerm();

        // Collect variable names from the parsed term in first-occurrence
        // order. Materialise the term once on the heap so each unique name
        // resolves to one shared heap cell, then read back each var's
        // heap-bound value for the bindings list.
        var names = new List<string>();
        var seen = new HashSet<string>();
        CollectNamedVarsFromTerm(parsed, names, seen);

        Cell parsedCell = Materializer.MaterializeAsCell(engine, parsed);
        if (!engine.UnifyRegisterWithCell(1, parsedCell)) return false;

        // The Materializer's internal varMap is private; re-walk the term to
        // find each variable's heap address by re-materialising with a
        // shared map ourselves. Simpler: read each var's binding back via
        // re-parsing — but parsed already has the names. Re-materialise the
        // bindings list using fresh vars that match by name into parsed.
        // We do this by building '=(Name, Var)' terms whose Var slots
        // share names with the parsed term — Materializer will then
        // resolve them through its varMap and produce the same heap cells.
        var pairs = new List<Term>(names.Count);
        foreach (string name in names)
        {
            pairs.Add(new CompoundTerm("=", new Term[]
            {
                new AtomTerm(name),
                new VarTerm(name),
            }));
        }
        // To force shared identity between vars in pairs and vars in parsed,
        // construct a top-level wrapper term containing both, materialise
        // together, then extract the bindings half.
        Term wrapper = new CompoundTerm("$pair", new Term[]
        {
            parsed,
            BuildListTerm(pairs),
        });
        Cell wrapCell = Materializer.MaterializeAsCell(engine, wrapper);
        // wrapCell is Cell.Ref to STR for $pair/2. Args at strBase+2 and +3.
        int wrapBase = wrapCell.AsHeapIndex;
        int bindingsAddr = wrapBase + 3;
        return engine.UnifyRegisterWithHeapAt(2, bindingsAddr);
    }

    private static void CollectNamedVarsFromTerm(Term t, List<string> order, HashSet<string> seen)
    {
        switch (t)
        {
            case VarTerm v when v.Name != "_":
                if (seen.Add(v.Name)) order.Add(v.Name);
                break;
            case CompoundTerm c:
                foreach (Term arg in c.Args) CollectNamedVarsFromTerm(arg, order, seen);
                break;
        }
    }

    private static Term BuildListTerm(IReadOnlyList<Term> items)
    {
        Term acc = new AtomTerm("[]");
        for (int i = items.Count - 1; i >= 0; i--)
            acc = new CompoundTerm(".", new[] { items[i], acc });
        return acc;
    }

    /// <summary><c>set_prolog_flag(Flag, Value)</c> — updates a parser-
    /// visible flag (chunk 58). Phase 1 recognises only
    /// <c>double_quotes</c> with values <c>codes</c>, <c>chars</c>,
    /// <c>atom</c>, or <c>string</c>; other flags raise a domain error.
    /// Setting <c>double_quotes</c> takes effect for the next parse —
    /// either a query, an <c>assertz</c> of a clause carrying a string
    /// literal, or a <c>:- consult</c> reading more source.</summary>
    public static bool SetPrologFlag(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "set_prolog_flag/2 requires the engine to be hosted by a PrologEngine.");

        Cell flagCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell valueCell = ResolveLocal(engine, engine.GetRegister(1));
        if (flagCell.Tag == Tag.Ref || valueCell.Tag == Tag.Ref)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (flagCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(
                IsoError.TypeError("atom", new VarTerm("_")));
        if (valueCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(
                IsoError.TypeError("atom", new VarTerm("_")));

        string flagName = AtomTable.GetById(flagCell.AsAtomId)?.Name ?? "";
        string valueName = AtomTable.GetById(valueCell.AsAtomId)?.Name ?? "";

        if (flagName == "double_quotes")
        {
            host.Flags.DoubleQuotes = valueName switch
            {
                "codes"  => Shumway.Compiler.Parsing.DoubleQuotesMode.Codes,
                "chars"  => Shumway.Compiler.Parsing.DoubleQuotesMode.Chars,
                "atom"   => Shumway.Compiler.Parsing.DoubleQuotesMode.Atom,
                "string" => Shumway.Compiler.Parsing.DoubleQuotesMode.String,
                _ => throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName))),
            };
            return true;
        }

        throw new ShumwayPrologException(
            IsoError.DomainError("prolog_flag", new AtomTerm(flagName)));
    }

    /// <summary><c>current_prolog_flag(Flag, Value)</c> — reads a flag's
    /// current value (chunk 58). With Flag bound, unifies Value with
    /// the stored value; with Flag unbound, Phase 1 just fails (full
    /// enumeration of every flag isn't worth the runtime CP plumbing
    /// for the small set of flags v1 actually supports).</summary>
    public static bool CurrentPrologFlag(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "current_prolog_flag/2 requires the engine to be hosted by a PrologEngine.");

        Cell flagCell = ResolveLocal(engine, engine.GetRegister(0));
        if (flagCell.Tag != Tag.Atom) return false;
        string flagName = AtomTable.GetById(flagCell.AsAtomId)?.Name ?? "";

        if (flagName == "double_quotes")
        {
            string valueName = host.Flags.DoubleQuotes switch
            {
                Shumway.Compiler.Parsing.DoubleQuotesMode.Codes  => "codes",
                Shumway.Compiler.Parsing.DoubleQuotesMode.Chars  => "chars",
                Shumway.Compiler.Parsing.DoubleQuotesMode.Atom   => "atom",
                _ => "string",
            };
            int aid = AtomTable.Intern(valueName, permanent: true).Id;
            return engine.UnifyRegisterWithCell(1, Cell.Atom(aid));
        }
        return false;
    }

    /// <summary><c>op(Precedence, Type, Name)</c> — runtime operator
    /// declaration. Mirrors the <c>:- op(...)</c> directive but takes
    /// effect immediately for subsequent parses (queries, asserted
    /// clauses, read_term_from_atom). Errors mirror ISO: instantiation
    /// when any arg is unbound, type_error when one's the wrong shape.</summary>
    public static bool Op(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "op/3 requires the engine to be hosted by a PrologEngine.");

        Cell precCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell typeCell = ResolveLocal(engine, engine.GetRegister(1));
        Cell nameCell = ResolveLocal(engine, engine.GetRegister(2));

        if (precCell.Tag != Tag.Int)
            throw new ShumwayPrologException(IsoError.TypeError("integer", new VarTerm("_")));
        int precedence = (int)precCell.AsInt;
        if (precedence < 0 || precedence > 1200)
            throw new ShumwayPrologException(
                IsoError.DomainError("operator_priority", new IntTerm(precedence)));

        if (typeCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(IsoError.TypeError("atom", new VarTerm("_")));
        string typeName = AtomTable.GetById(typeCell.AsAtomId)?.Name ?? "";
        Shumway.Compiler.Parsing.OperatorType opType = typeName switch
        {
            "fx" => Shumway.Compiler.Parsing.OperatorType.Fx,
            "fy" => Shumway.Compiler.Parsing.OperatorType.Fy,
            "xf" => Shumway.Compiler.Parsing.OperatorType.Xf,
            "yf" => Shumway.Compiler.Parsing.OperatorType.Yf,
            "xfx" => Shumway.Compiler.Parsing.OperatorType.Xfx,
            "xfy" => Shumway.Compiler.Parsing.OperatorType.Xfy,
            "yfx" => Shumway.Compiler.Parsing.OperatorType.Yfx,
            _ => throw new ShumwayPrologException(
                IsoError.DomainError("operator_specifier", new AtomTerm(typeName))),
        };

        // Name may be a single atom or a list of atoms (the conventional
        // op/3 multi-name form).
        if (nameCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(nameCell.AsAtomId)?.Name ?? "";
            host.DefineOperator(name, precedence, opType);
            return true;
        }
        if (nameCell.Tag == Tag.Lis)
        {
            Cell cur = nameCell;
            while (cur.Tag == Tag.Lis)
            {
                Cell head = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex));
                if (head.Tag != Tag.Atom)
                    throw new ShumwayPrologException(IsoError.TypeError("atom", new VarTerm("_")));
                string name = AtomTable.GetById(head.AsAtomId)?.Name ?? "";
                host.DefineOperator(name, precedence, opType);
                cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
            }
            return true;
        }
        throw new ShumwayPrologException(IsoError.TypeError("atom_or_list", new VarTerm("_")));
    }

    /// <summary><c>read_term_from_atom(Atom, Term)</c> — parses the text
    /// stored in <c>Atom</c> as a Prolog term and unifies the result with
    /// <c>Term</c>. The full ISO <c>read_term/2</c> reads from an
    /// arbitrary stream — Phase 1 only handles the in-memory atom case,
    /// which is the use the embedding API actually needs.</summary>
    public static bool ReadTermFromAtom(Engine engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));
        if (atomCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(IsoError.TypeError("atom", new VarTerm("_")));
        string source = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        if (!source.TrimEnd().EndsWith(".", StringComparison.Ordinal))
            source += ".";
        var parser = new Shumway.Compiler.Parsing.Parser(
            new Shumway.Compiler.Lexer.Lexer(source),
            Shumway.Compiler.Parsing.OperatorTable.Default());
        Term parsed = parser.ReadClauseTerm();
        Cell parsedCell = Materializer.MaterializeAsCell(engine, parsed);
        return engine.UnifyRegisterWithCell(1, parsedCell);
    }

    // ============================================================================
    // functor/3, arg/3, =../2
    // ============================================================================

    /// <summary><c>functor(Term, Name, Arity)</c> — bidirectional term
    /// introspection. With <c>Term</c> bound, decomposes into its functor
    /// name and arity (atomic terms are name = themselves, arity = 0).
    /// With <c>Term</c> unbound and <c>Name</c> + <c>Arity</c> ground,
    /// builds a fresh compound with <c>Arity</c> anonymous unbound
    /// arguments.</summary>
    public static bool Functor(Engine engine)
    {
        Cell t = ResolveLocal(engine, engine.GetRegister(0));

        if (t.Tag == Tag.Atom || t.Tag == Tag.Int || t.Tag == Tag.Float)
        {
            if (!engine.UnifyRegisterWithCell(1, t)) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(0));
        }
        if (t.Tag == Tag.Str)
        {
            int functorIdx = t.AsHeapIndex;
            var (atomId, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(atomId))) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(arity));
        }
        if (t.Tag == Tag.Lis)
        {
            int dotId = AtomTable.Intern(".", permanent: true).Id;
            if (!engine.UnifyRegisterWithCell(1, Cell.Atom(dotId))) return false;
            return engine.UnifyRegisterWithCell(2, Cell.Int(2));
        }
        if (t.Tag == Tag.Ref)
        {
            // Construct mode: Name and Arity must be ground.
            Cell n = ResolveLocal(engine, engine.GetRegister(1));
            Cell a = ResolveLocal(engine, engine.GetRegister(2));
            if (a.Tag != Tag.Int)
                throw new ShumwayPrologException(
                    IsoError.TypeError("integer", new VarTerm("_")));
            long arity = a.AsInt;
            if (arity < 0)
                throw new ShumwayPrologException(
                    IsoError.DomainError("not_less_than_zero", new VarTerm("_")));
            if (arity == 0)
            {
                // T becomes Name itself (atomic).
                if (n.Tag != Tag.Atom && n.Tag != Tag.Int && n.Tag != Tag.Float)
                    throw new ShumwayPrologException(
                        IsoError.TypeError("atomic", new VarTerm("_")));
                return engine.UnifyRegisterWithCell(0, n);
            }
            if (n.Tag != Tag.Atom)
                throw new ShumwayPrologException(
                    IsoError.TypeError("atom", new VarTerm("_")));
            int functorId = FunctorTable.Intern(n.AsAtomId, (int)arity);
            int strBase = engine.AllocateHeap(2 + (int)arity);
            engine.SetHeap(strBase, Cell.Str(strBase + 1));
            engine.SetHeap(strBase + 1, Cell.Functor(functorId));
            for (int i = 0; i < arity; i++)
            {
                int slot = strBase + 2 + i;
                engine.SetHeap(slot, Cell.UnboundVar(slot));
            }
            return engine.UnifyRegisterWithCell(0, Cell.Ref(strBase));
        }
        return false;
    }

    /// <summary><c>arg(N, Term, Arg)</c> — the N-th argument (1-indexed)
    /// of a compound term. Fails when N is out of range or <c>Term</c>
    /// isn't a compound.</summary>
    public static bool Arg(Engine engine)
    {
        Cell nCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell tCell = ResolveLocal(engine, engine.GetRegister(1));
        if (nCell.Tag != Tag.Int)
            throw new ShumwayPrologException(
                IsoError.TypeError("integer", new VarTerm("_")));
        long n = nCell.AsInt;

        if (tCell.Tag == Tag.Str)
        {
            int functorIdx = tCell.AsHeapIndex;
            var (_, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            if (n < 1 || n > arity) return false;
            return engine.UnifyRegisterWithHeapAt(2, functorIdx + (int)n);
        }
        if (tCell.Tag == Tag.Lis)
        {
            // List has arity 2: arg(1) = head, arg(2) = tail.
            if (n < 1 || n > 2) return false;
            int headIdx = tCell.AsHeapIndex;
            return engine.UnifyRegisterWithHeapAt(2, headIdx + (int)(n - 1));
        }
        return false;
    }

    /// <summary><c>T =.. List</c> — the "univ" operator. Decomposes a
    /// compound into <c>[Functor | Args]</c> (or yields <c>[Atom]</c>
    /// for atomic <c>T</c>), or composes <c>T</c> from such a list.</summary>
    public static bool Univ(Engine engine)
    {
        Cell t = ResolveLocal(engine, engine.GetRegister(0));

        // Decompose modes.
        if (t.Tag == Tag.Atom || t.Tag == Tag.Int || t.Tag == Tag.Float)
        {
            int listIdx = BuildListFromCells(engine, new[] { t });
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }
        if (t.Tag == Tag.Str)
        {
            int functorIdx = t.AsHeapIndex;
            var (atomId, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            var cells = new Cell[1 + arity];
            cells[0] = Cell.Atom(atomId);
            for (int i = 0; i < arity; i++)
                cells[1 + i] = engine.GetHeap(functorIdx + 1 + i);
            int listIdx = BuildListFromCells(engine, cells);
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }
        if (t.Tag == Tag.Lis)
        {
            int dotId = AtomTable.Intern(".", permanent: true).Id;
            int headIdx = t.AsHeapIndex;
            int listIdx = BuildListFromCells(engine, new[]
            {
                Cell.Atom(dotId),
                engine.GetHeap(headIdx),
                engine.GetHeap(headIdx + 1),
            });
            return engine.UnifyRegisterWithHeapAt(1, listIdx);
        }
        if (t.Tag == Tag.Ref)
        {
            // Compose: read the list.
            Cell listC = ResolveLocal(engine, engine.GetRegister(1));
            var cells = new List<Cell>();
            Cell cur = listC;
            while (cur.Tag == Tag.Lis)
            {
                cells.Add(engine.GetHeap(cur.AsHeapIndex));
                cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
            }
            if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
                throw new ShumwayPrologException(
                    IsoError.TypeError("list", new VarTerm("_")));
            if (cells.Count == 0)
                throw new ShumwayPrologException(
                    IsoError.DomainError("non_empty_list", new VarTerm("_")));

            Cell first = ResolveLocal(engine, cells[0]);
            if (cells.Count == 1)
            {
                // Single-element list — T is atomic.
                if (first.Tag != Tag.Atom && first.Tag != Tag.Int && first.Tag != Tag.Float)
                    throw new ShumwayPrologException(
                        IsoError.TypeError("atomic", new VarTerm("_")));
                return engine.UnifyRegisterWithCell(0, first);
            }
            // Multi-element: first must be an atom (the functor name).
            if (first.Tag != Tag.Atom)
                throw new ShumwayPrologException(
                    IsoError.TypeError("atom", new VarTerm("_")));
            int arity = cells.Count - 1;
            int functorId = FunctorTable.Intern(first.AsAtomId, arity);
            int strBase = engine.AllocateHeap(2 + arity);
            engine.SetHeap(strBase, Cell.Str(strBase + 1));
            engine.SetHeap(strBase + 1, Cell.Functor(functorId));
            for (int i = 0; i < arity; i++)
                engine.SetHeap(strBase + 2 + i, cells[1 + i]);
            return engine.UnifyRegisterWithCell(0, Cell.Ref(strBase));
        }
        return false;
    }

    /// <summary>Builds a fresh proper list whose head slots hold the given
    /// cell values. Same layout pattern as <c>SortBuiltins.BuildList</c>:
    /// 2N + 1 contiguous cells, alternating Lis / head pairs terminated
    /// by the empty-list atom.</summary>
    private static int BuildListFromCells(Engine engine, IReadOnlyList<Cell> elements)
    {
        if (elements.Count == 0)
        {
            int nilSlot = engine.AllocateHeap(1);
            engine.SetHeap(nilSlot, Cell.Atom(AtomTable.EmptyListId));
            return nilSlot;
        }
        int start = engine.AllocateHeap(2 * elements.Count + 1);
        for (int i = 0; i < elements.Count; i++)
        {
            int lisIdx = start + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, elements[i]);
        }
        engine.SetHeap(start + 2 * elements.Count, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    /// <summary><c>term_to_atom(Term, Atom)</c> — bidirectional bridge
    /// between a Prolog term and its atom-text representation. With
    /// <c>Term</c> ground the term is rendered through <see cref="TermReader"/>
    /// (via the standard <see cref="Shumway.Builtins.TermRenderer"/> output)
    /// and the result interned as an atom. With <c>Atom</c> ground the atom
    /// text is parsed as a Prolog term via <see cref="Parser"/>.</summary>
    public static bool TermToAtom(Engine engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(1));

        if (atomCell.Tag == Tag.Atom)
        {
            // Atom → Term direction: parse the atom name as a Prolog term.
            string name = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            // The parser expects a clause-terminating dot; help it by
            // appending one when the user-supplied text doesn't have one.
            string source = name.TrimEnd().EndsWith(".", StringComparison.Ordinal)
                ? name
                : name + ".";
            var parser = new Shumway.Compiler.Parsing.Parser(
                new Shumway.Compiler.Lexer.Lexer(source),
                Shumway.Compiler.Parsing.OperatorTable.Default());
            Term parsed = parser.ReadClauseTerm();
            Cell newCell = Materializer.MaterializeAsCell(engine, parsed);
            return engine.UnifyRegisterWithCell(0, newCell);
        }

        // Term → Atom direction: render and intern.
        using var sw = new System.IO.StringWriter();
        Shumway.Builtins.TermRenderer.Render(engine, engine.GetRegister(0), sw);
        string rendered = sw.ToString();
        int newAtomId = AtomTable.Intern(rendered, permanent: false).Id;
        return engine.UnifyRegisterWithCell(1, Cell.Atom(newAtomId));
    }

    private static Cell ResolveLocal(Engine engine, Cell c)
    {
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
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

    /// <summary><c>'$all_clauses_of'(HeadPattern, Pairs)</c> — returns a
    /// proper list of <c>Head-Body</c> pairs whose head functor matches
    /// the <em>functor</em> of <paramref name="HeadPattern"/>. Each
    /// returned head/body is a freshly materialised heap copy so the
    /// caller can unify with each pair's first element (the head) and
    /// then with the second element (the body) without sharing variable
    /// identity between candidates.
    ///
    /// <para>The prelude's <c>clause/2</c> uses this helper to fan out
    /// across candidates via <c>member/2</c>, so backtracking through
    /// matching clauses happens via the standard WAM choice-point
    /// machinery rather than through builtin-internal state.</para></summary>
    public static bool AllClausesOf(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$all_clauses_of'/2 requires a PrologEngine host.");

        Term headPattern = MaterializeRegister(engine, 0);
        int fid = ExtractCallableFunctorId(headPattern, "'$all_clauses_of'/2");

        var candidates = new List<Clause>();
        candidates.AddRange(host.DynamicClausesFor(fid));
        candidates.AddRange(host.StaticClausesFor(fid));

        // Build the list of '-/2'(Head, Body) pairs as AST terms, then
        // materialise the whole list onto the heap in one pass — that
        // way each candidate's variables stay independent of the others
        // and of the caller's head pattern.
        Term tail = new AtomTerm("[]");
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            var candidate = candidates[i];
            Term head = candidate.Kind == ClauseKind.Rule
                ? ((CompoundTerm)candidate.Term).Args[0]
                : candidate.Term;
            Term body = candidate.Kind == ClauseKind.Rule
                ? ((CompoundTerm)candidate.Term).Args[1]
                : new AtomTerm("true");
            // Pair shape `-(Head, Body)` matches how Prolog spells
            // `H-B` after operator parsing.
            Term pair = new CompoundTerm("-", new[] { head, body });
            tail = new CompoundTerm(".", new[] { pair, tail });
        }
        Cell listCell = Materializer.MaterializeAsCell(engine, tail);
        return engine.UnifyRegisterWithCell(1, listCell);
    }

    /// <summary><c>'$all_predicate_indicators'(List)</c> — returns a list
    /// of <c>Name/Arity</c> terms covering every predicate the engine
    /// knows about: builtins, dynamic functors, and static predicates
    /// from every loaded module. The prelude's <c>current_predicate/1</c>
    /// uses this helper to back-enumerate via <c>member/2</c>.</summary>
    public static bool AllPredicateIndicators(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$all_predicate_indicators'/1 requires a PrologEngine host.");

        var seen = new HashSet<int>();
        var indicators = new List<Term>();

        void AddIndicator(int functorId)
        {
            if (!seen.Add(functorId)) return;
            var (atomId, arity) = FunctorTable.Lookup(functorId);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            indicators.Add(new CompoundTerm("/",
                new Term[] { new AtomTerm(name), new IntTerm(arity) }));
        }

        foreach (int fid in BuiltinsRegistry.AllRegisteredFunctorIds())
            AddIndicator(fid);
        foreach (int fid in host.AllStaticAndDynamicFunctors())
            AddIndicator(fid);

        Term listTerm = new AtomTerm("[]");
        for (int i = indicators.Count - 1; i >= 0; i--)
            listTerm = new CompoundTerm(".", new[] { indicators[i], listTerm });
        Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
        return engine.UnifyRegisterWithCell(0, listCell);
    }

    /// <summary><c>'$listable_predicates'/1</c> — the user-defined
    /// predicates <c>listing/0,1</c> may print, each as a
    /// <c>pi(Name, Arity, Dynamic)</c> term where <c>Dynamic</c> is
    /// <c>true</c> or <c>false</c>. Builtins and the library predicates of
    /// <c>$prelude</c> / <c>clpfd</c> are excluded.</summary>
    public static bool ListablePredicates(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$listable_predicates'/1 requires a PrologEngine host.");

        var entries = new List<Term>();
        foreach (var (fid, isDynamic) in host.ListablePredicates())
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name ?? "?";
            entries.Add(new CompoundTerm("pi", new Term[]
            {
                new AtomTerm(name),
                new IntTerm(arity),
                new AtomTerm(isDynamic ? "true" : "false"),
            }));
        }

        Term listTerm = new AtomTerm("[]");
        for (int i = entries.Count - 1; i >= 0; i--)
            listTerm = new CompoundTerm(".", new[] { entries[i], listTerm });
        Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
        return engine.UnifyRegisterWithCell(0, listCell);
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

    /// <summary>Promotes a Core-level <see cref="PrologRuntimeException"/>
    /// into the canonical ISO <c>error(Kind, _)</c> Prolog term that
    /// user-written catchers expect.</summary>
    internal static Term TranslateRuntimeError(PrologRuntimeException re) => re.Kind switch
    {
        "evaluation_error" => IsoError.EvaluationError(re.Detail),
        "instantiation_error" => IsoError.InstantiationError(),
        "type_error" => IsoError.TypeError(re.Detail, new VarTerm("_")),
        "existence_error" => IsoError.ExistenceError(
            "procedure", new AtomTerm(re.Detail)),
        "domain_error" => IsoError.DomainError(re.Detail, new VarTerm("_")),
        _ => new CompoundTerm("error",
            new Term[] { new AtomTerm(re.Kind), new AtomTerm(re.Detail) }),
    };

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
        ShumwayPrologException? toCatch = null;
        try
        {
            foreach (Solution sol in sub.QueryAll(goal))
            {
                BindBack(engine, sol.Bindings);
                return true;
            }
            // No solutions. If the sub-engine halted, re-raise so the
            // parent QueryAll (or surrounding catch) sees the halt — ISO
            // catch/3 explicitly does NOT intercept halt.
            if (sub.LastHaltExitCode.HasValue)
                throw new PrologHaltException(sub.LastHaltExitCode.Value);
            return false;
        }
        catch (ShumwayPrologException ex)
        {
            toCatch = ex;
        }
        catch (PrologRuntimeException re)
        {
            // Promote the Core-level structured error into the ISO
            // error(Kind, _) term, then funnel into the same recovery
            // path the user's throw/1 would have hit.
            toCatch = new ShumwayPrologException(TranslateRuntimeError(re));
        }

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

        Cell thrownCell = Materializer.MaterializeAsCell(engine, toCatch!.Term);
        int thrownSlot = engine.AllocateHeap(1);
        engine.SetHeap(thrownSlot, thrownCell);

        if (!engine.UnifyRegisterWithHeapAt(1, thrownSlot))
        {
            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
            throw toCatch;   // rethrow the translated (or original) ShumwayPrologException
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

    /// <summary><c>'$catch_begin'(Catcher, RecoveryGoal)</c> (chunk 85) —
    /// opens a catch/3 scope. Copies the catcher and the recovery-goal call
    /// onto the heap (so they survive a caught throw's heap truncation) and
    /// pushes a catch frame snapshotting the live machine. Emitted by the
    /// MetaTransform rewrite of catch/3 as the first goal of the goal
    /// helper, so the engine reads the recovery continuation off that
    /// helper's environment header.</summary>
    public static bool CatchBegin(Engine engine)
    {
        int catcherSlot = engine.AllocateHeap(1);
        engine.SetHeap(catcherSlot, engine.GetRegister(0));
        int recoverySlot = engine.AllocateHeap(1);
        engine.SetHeap(recoverySlot, engine.GetRegister(1));
        engine.PushCatchFrame(catcherSlot, recoverySlot);
        return true;
    }

    /// <summary><c>'$catch_end'/0</c> (chunk 85) — closes a catch/3 scope:
    /// the guarded goal has produced a solution, so the catch frame is
    /// deactivated and a throw from the continuation will no longer be
    /// caught here. Backtracking into the guarded goal re-activates it.</summary>
    public static bool CatchEnd(Engine engine)
    {
        engine.DeactivateTopCatchFrame();
        return true;
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

    /// <summary><c>'$call'(Goal, Barrier)</c> — the cut-barrier-carrying
    /// meta-call (chunk 88). It is intercepted by the bytecode interpreter
    /// exactly like <c>call/N</c> and never reaches this body; the entry
    /// exists only so the compiler emits a <c>call_builtin</c> for it.</summary>
    public static bool CallWithBarrier(Engine engine) =>
        throw new InvalidOperationException(
            "'$call'/2 must be dispatched by the interpreter, not invoked directly.");

    /// <summary><c>call(Goal, ExtraArgs...)</c> — runs <c>Goal</c> (optionally
    /// extended with extra args appended to its argument list) in a peer
    /// sub-engine and propagates each solution's bindings back into the
    /// caller's heap.
    ///
    /// <para>Implementation: the input registers are read as
    /// <see cref="Term"/>s with synthetic <c>_GN</c> variable names that
    /// encode the caller's heap address. The composed goal runs in the
    /// sub-engine; the resulting <see cref="Solution"/> binds each
    /// <c>_GN</c>, and we use the embedded address to find the caller's
    /// variable cell and unify it with the materialised bound term.</para>
    ///
    /// <para><b>Multi-solution support (chunk 56)</b>: when the goal has
    /// alternatives the builtin pushes a runtime IL-style choice point
    /// before binding the first solution. On backtrack the CP's resume
    /// delegate advances the sub-engine's enumerator, undoing the current
    /// bindings (via the standard trail unwind) and applying the next
    /// solution. This is what makes <c>maplist</c>, <c>forall</c>, and
    /// other prelude predicates that meta-call backtracking goals work
    /// correctly when the goal has more than one solution.</para></summary>
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

        // The call_builtin instruction is 9 bytes long; the return PC
        // for our CP is the byte immediately after it. We capture this
        // here (before the sub-engine runs) so the closure recursion in
        // AdvanceCallNEnumerator carries the right value.
        int returnPc = engine.P + 9;

        var sub = host.CreateSubEngine();
        var iter = sub.QueryAll(callGoal).GetEnumerator();
        return AdvanceCallNEnumerator(engine, iter, returnPc, isResume: false);
    }

    /// <summary>Pulls the next solution from <paramref name="iter"/> and
    /// binds it back into <paramref name="engine"/>. The first
    /// invocation (<paramref name="isResume"/> <c>false</c>) is from
    /// inside <see cref="CallN"/> — the interpreter's call_builtin
    /// success path advances PC by 9 (the opcode's size) so we don't
    /// need to set it ourselves. Subsequent invocations (from a
    /// backtrack-popped CP) <em>do</em> need to set PC explicitly via
    /// <see cref="Engine.ResumeAtReturnPc"/> because the
    /// PopIlChoicePointAndRestore path would otherwise drop us at the
    /// outer continuation (the saved Cp), not the next instruction
    /// after the original call_builtin.
    ///
    /// <para>The CP push happens <em>before</em> the bind-back so the
    /// trail unwind on backtrack peels off the current solution's
    /// bindings and leaves the heap in the state expected by the next
    /// solution.</para></summary>
    private static bool AdvanceCallNEnumerator(
        Engine engine, IEnumerator<Solution> iter, int returnPc, bool isResume)
    {
        if (!iter.MoveNext())
        {
            iter.Dispose();
            return false;
        }

        // Push a CP optimistically — we don't know without consuming
        // whether there's another solution, but if there isn't the
        // resume delegate's first MoveNext returns false and the CP
        // collapses cleanly.
        Func<Engine, int, bool> resume = (e, _) =>
            AdvanceCallNEnumerator(e, iter, returnPc, isResume: true);
        engine.PushBuiltinChoicePoint(resume, arity: 0);

        Solution sol = iter.Current;
        foreach (var (name, value) in sol.Bindings)
        {
            int addr = ExtractAddrFromName(name);
            if (addr < 0) continue;
            Cell boundCell = Materializer.MaterializeAsCell(engine, value);
            int slot = engine.AllocateHeap(1);
            engine.SetHeap(slot, boundCell);
            if (!engine.Unify(addr, slot)) return false;
        }
        if (isResume) engine.ResumeAtReturnPc(returnPc);
        return true;
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
    /// <summary><c>retract/1</c> — removes a clause unifying with the
    /// pattern. It is <em>re-satisfiable</em> per ISO: on backtracking it
    /// retracts the next matching clause, so <c>(retract(C), fail ; true)</c>
    /// removes every match. The candidate set is snapshotted at call time
    /// (ISO's logical-update view).</summary>
    public static bool Retract(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "retract: PrologEngine host required.");

        Term pattern = MaterializeRegister(engine, 0);
        var patternClause = Shumway.Compiler.Ast.Clause.From(pattern);
        int patternFid = ExtractHeadFunctorIdFromClause(patternClause);

        var candidates = new List<Clause>(host.DynamicClausesFor(patternFid));
        // The call_builtin opcode is 9 bytes; a resumed step continues at
        // the instruction after it.
        int returnPc = engine.P + 9;
        return RetractStep(engine, host, patternFid, candidates, 0, returnPc,
            isResume: false);
    }

    /// <summary>Removes the next clause (from <paramref name="startIndex"/>
    /// onward) that unifies with the retract pattern. When later candidates
    /// remain it leaves a choice point whose resume retracts the following
    /// match — that is what makes <c>retract/1</c> enumerate every matching
    /// clause on backtracking.</summary>
    private static bool RetractStep(Engine engine, PrologEngine host,
        int patternFid, List<Clause> candidates, int startIndex, int returnPc,
        bool isResume)
    {
        int matchIndex = FindRetractMatch(engine, candidates, startIndex);
        if (matchIndex < 0) return false;

        // Push the choice point before the real unification below, so a
        // backtrack's trail unwind peels off exactly this solution's
        // bindings before the resume retracts the next match.
        if (matchIndex + 1 < candidates.Count)
        {
            int next = matchIndex + 1;
            Func<Engine, int, bool> resume = (e, _) => RetractStep(
                e, host, patternFid, candidates, next, returnPc, isResume: true);
            engine.PushBuiltinChoicePoint(resume, arity: 0);
        }

        Clause candidate = candidates[matchIndex];
        int savedHb = engine.Hb;
        engine.SetHb(engine.HeapTop);
        Cell candidateCell = Materializer.MaterializeAsCell(engine, candidate.Term);
        int candSlot = engine.AllocateHeap(1);
        engine.SetHeap(candSlot, candidateCell);
        engine.UnifyRegisterWithHeapAt(0, candSlot);   // matched in FindRetractMatch
        host.RemoveDynamicByReference(patternFid, candidate);
        engine.SetHb(savedHb);
        if (isResume) engine.ResumeAtReturnPc(returnPc);
        return true;
    }

    /// <summary>Index of the first candidate (from <paramref name="startIndex"/>)
    /// whose clause unifies with the retract pattern in register 0, or −1
    /// when none does. The trial unification is fully rolled back; the
    /// caller re-does it for the chosen candidate after its choice point
    /// is in place.</summary>
    private static int FindRetractMatch(
        Engine engine, List<Clause> candidates, int startIndex)
    {
        for (int i = startIndex; i < candidates.Count; i++)
        {
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            Cell candidateCell =
                Materializer.MaterializeAsCell(engine, candidates[i].Term);
            int candSlot = engine.AllocateHeap(1);
            engine.SetHeap(candSlot, candidateCell);
            bool matches = engine.UnifyRegisterWithHeapAt(0, candSlot);

            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
            if (matches) return i;
        }
        return -1;
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

    /// <summary><c>'$copy_term_3_prep'(Term, Copy, AttrInfo)</c> — the C#
    /// half of <c>copy_term/3</c> (chunk 81). Copies <c>Term</c> into
    /// <c>Copy</c> with fresh plain variables and produces
    /// <c>AttrInfo</c>: a list of <c>ag(Module, AttrValue, Var)</c>
    /// triples, one per (attributed variable, module) pair found in
    /// <c>Term</c>. <c>Copy</c> and <c>AttrInfo</c> are materialised in a
    /// single pass so a variable shared between the term and an
    /// attribute value is the <em>same</em> fresh variable in both — the
    /// prelude's <c>copy_term/3</c> then runs <c>attribute_goals/4</c>
    /// over the triples, and the residual goals come out expressed over
    /// <c>Copy</c>'s variables.</summary>
    public static bool CopyTerm3Prep(Engine engine)
    {
        // Distinct attributed variables reachable from the term at X[0].
        var attvars = new System.Collections.Generic.List<int>();
        var seen = new System.Collections.Generic.HashSet<int>();
        CollectAttvars(engine, engine.GetRegister(0), attvars, seen);

        Term original = MaterializeRegister(engine, 0);

        var infos = new System.Collections.Generic.List<Term>();
        foreach (int vAddr in attvars)
        {
            // The same _G<addr> name TermReader.Materialize gives this
            // attributed variable, so the shared-var-map join lands it on
            // the copy's variable.
            var vVar = new VarTerm("_G" + vAddr);
            foreach (int moduleId in engine.AttrModules(vAddr))
            {
                int attrValueIdx = engine.GetAttr(vAddr, moduleId);
                Term attrValue = TermReader.Materialize(engine, attrValueIdx);
                string moduleName = AtomTable.GetById(moduleId)?.Name
                    ?? throw new InvalidOperationException(
                        $"copy_term/3: module atom id {moduleId} is not registered.");
                infos.Add(new CompoundTerm("ag", new Term[]
                    { new AtomTerm(moduleName), attrValue, vVar }));
            }
        }

        // One materialisation of Copy + AttrInfo, so a _G name occurring
        // in both maps to a single fresh variable.
        Term combined = new CompoundTerm("-", new[] { original, MakeListTerm(infos) });
        // MaterializeAsCell hands back Ref(strBase) for a compound; the STR
        // cell at strBase points at the functor, and the two args follow
        // the functor cell — so the args are at functorIdx+1 / functorIdx+2.
        Cell combinedCell = Materializer.MaterializeAsCell(engine, combined);
        int functorIdx = engine.GetHeap(combinedCell.AsHeapIndex).AsHeapIndex;
        return engine.UnifyRegisterWithHeapAt(1, functorIdx + 1)
            && engine.UnifyRegisterWithHeapAt(2, functorIdx + 2);
    }

    /// <summary>Collects the distinct heap addresses of attributed
    /// variables reachable from <paramref name="cell"/>. The shared
    /// visited set also guards against a cyclic term looping.</summary>
    private static void CollectAttvars(Engine engine, Cell cell,
        System.Collections.Generic.List<int> addrs,
        System.Collections.Generic.HashSet<int> seen)
    {
        if (cell.Tag == Tag.Ref)
            cell = engine.GetHeap(engine.Deref(cell.AsHeapIndex));
        switch (cell.Tag)
        {
            case Tag.AttVar:
                int va = cell.AsHeapIndex;
                if (seen.Add(va)) addrs.Add(va);
                break;
            case Tag.Str:
                int fIdx = cell.AsHeapIndex;
                if (!seen.Add(fIdx)) break;
                var (_, arity) = FunctorTable.Lookup(engine.GetHeap(fIdx).AsFunctorId);
                for (int i = 0; i < arity; i++)
                    CollectAttvars(engine, engine.GetHeap(fIdx + 1 + i), addrs, seen);
                break;
            case Tag.Lis:
                int h = cell.AsHeapIndex;
                if (!seen.Add(h)) break;
                CollectAttvars(engine, engine.GetHeap(h), addrs, seen);
                CollectAttvars(engine, engine.GetHeap(h + 1), addrs, seen);
                break;
        }
    }

    /// <summary>Builds a proper-list AST term from the given items.</summary>
    private static Term MakeListTerm(System.Collections.Generic.List<Term> items)
    {
        Term list = new AtomTerm("[]");
        for (int i = items.Count - 1; i >= 0; i--)
            list = new CompoundTerm(".", new[] { items[i], list });
        return list;
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

    /// <summary><c>'$findall_push'/0</c> (chunk 83) — opens a fresh
    /// solution buffer on the engine's findall stack. Emitted by the
    /// MetaTransform rewrite of <c>findall/3</c> as the first goal of the
    /// collect loop.</summary>
    public static bool FindallPush(Engine engine)
    {
        FindallHost(engine).PushFindallFrame();
        return true;
    }

    /// <summary><c>'$findall_record'(Template)</c> (chunk 83) — copies the
    /// current value of <c>Template</c> (a snapshot AST term, off the WAM
    /// heap so backtracking can't unwind it) into the open findall
    /// buffer, then succeeds so the trailing <c>fail</c> drives
    /// enumeration on to the goal's next solution.</summary>
    public static bool FindallRecord(Engine engine)
    {
        FindallHost(engine).RecordFindallSolution(MaterializeRegister(engine, 0));
        return true;
    }

    /// <summary><c>'$findall_collect'(List)</c> (chunk 83) — closes the
    /// open findall buffer and unifies <c>List</c> with its collected
    /// solutions. Each solution is materialised with its own variable map
    /// so distinct solutions never accidentally share a variable.</summary>
    public static bool FindallCollect(Engine engine)
    {
        var frame = FindallHost(engine).PopFindallFrame();
        Cell list = Cell.Atom(AtomTable.EmptyListId);
        for (int i = frame.Count - 1; i >= 0; i--)
        {
            Cell elem = Materializer.MaterializeAsCell(engine, frame[i]);
            int cons = engine.AllocateHeap(2);
            engine.SetHeap(cons, elem);
            engine.SetHeap(cons + 1, list);
            list = Cell.Lis(cons);
        }
        return engine.UnifyRegisterWithCell(0, list);
    }

    private static PrologEngine FindallHost(Engine engine) =>
        engine.Host as PrologEngine
        ?? throw new InvalidOperationException(
            "The in-engine findall builtins require a PrologEngine host.");

    /// <summary><c>'$bagof_collect'(Groups)</c> (chunk 84) — closes the open
    /// solution buffer (the bagof/3 rewrite shares findall's '$findall_push'
    /// and '$findall_record') and unifies its argument with the list of
    /// <c>Witness-Bag</c> pairs that bagof/3 backtracks over: one pair per
    /// distinct witness, in standard order of the witness, each bag holding
    /// its solutions in generation order.</summary>
    public static bool BagofCollect(Engine engine)
    {
        var frame = FindallHost(engine).PopFindallFrame();
        Cell groups = Materializer.MaterializeAsCell(
            engine, BuildWitnessGroups(frame, sortBags: false));
        return engine.UnifyRegisterWithCell(0, groups);
    }

    /// <summary><c>'$setof_collect'(Groups)</c> (chunk 84) — as
    /// <see cref="BagofCollect"/>, but each bag is sorted into standard order
    /// and stripped of duplicates — the only difference between bagof/3 and
    /// setof/3.</summary>
    public static bool SetofCollect(Engine engine)
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
    private static Term BuildWitnessGroups(List<Term> pairs, bool sortBags)
    {
        var groups = new List<WitnessGroup>();
        foreach (Term pair in pairs)
        {
            var cons = (CompoundTerm)pair;          // '-'(Witness, Template)
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

    /// <summary><c>bagof(Template, Goal, Bag)</c> — the variable-goal fallback
    /// for bagof/3. When <c>Goal</c> is callable at compile time the
    /// MetaTransform rewrite handles bagof/3 in the live engine with full
    /// witness grouping (chunk 84); this builtin only runs when <c>Goal</c> is
    /// a variable bound at run time, and keeps the pre-chunk-84 behaviour —
    /// "findall + fail-on-empty", no witness grouping. <c>Var^Goal</c>
    /// existential wrappers are stripped.</summary>
    public static bool Bagof(Engine engine)
    {
        var results = CollectSolutions(engine, stripExistentials: true);
        if (results.Count == 0) return false;
        return BindList(engine, results);
    }

    /// <summary><c>setof(Template, Goal, Set)</c> — the variable-goal fallback
    /// for setof/3 (see <see cref="Bagof"/> for the bagof/setof split). Sorts
    /// the bag into standard order and removes duplicates, but keeps the
    /// pre-chunk-84 no-grouping behaviour; the compile-time path with full
    /// witness grouping is the MetaTransform rewrite. The sort runs at the AST
    /// level via <see cref="TermStandardOrder.Compare"/>.</summary>
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

    /// <summary><c>forall(Cond, Then)</c> — the variable-goal fallback for
    /// forall/2. With callable arguments the MetaTransform rewrites
    /// <c>forall(C, T)</c> to <c>\+ (C, \+ T)</c>, which splices both goals
    /// into the clause body and runs them in the live engine (chunk 84); this
    /// builtin only runs when an argument is still a variable at compile time.
    /// It succeeds iff every solution of <c>Cond</c> makes <c>Then</c> succeed,
    /// enumerating <c>Cond</c> in a peer sub-engine and checking <c>Then</c>
    /// per solution, bailing on the first counter-example.</summary>
    public static bool Forall(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "forall/2 requires the engine to be hosted by a PrologEngine.");

        Term cond = MaterializeRegister(engine, 0);
        Term then = MaterializeRegister(engine, 1);

        var subCond = host.CreateSubEngine();
        foreach (Solution sol in subCond.QueryAll(cond))
        {
            Term thenInstance = Substitute(then, sol.Bindings);
            var subThen = host.CreateSubEngine();
            bool thenSucceeded = false;
            foreach (Solution _ in subThen.QueryAll(thenInstance))
            {
                thenSucceeded = true;
                break;
            }
            if (!thenSucceeded) return false;
        }
        return true;
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

    /// <summary><c>'$tbl_seen'/1</c> (chunk 106) — succeeds, recording the
    /// argument, the first time it is called with a given (structurally
    /// canonicalised) ground term; fails on every later call with an
    /// equal term. The tabling driver uses it as an O(1) duplicate-answer
    /// test, which is what makes the semi-naive fixpoint sub-quadratic —
    /// the alternative, scanning the asserted answers, is O(n) per check.</summary>
    public static bool TableSeen(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$tbl_seen'/1 requires a PrologEngine host.");
        var sb = new System.Text.StringBuilder();
        Canonicalize(MaterializeRegister(engine, 0), sb);
        return host.RegisterTablingKey(sb.ToString());
    }

    /// <summary>Appends a structurally faithful, injective encoding of a
    /// ground term to <paramref name="sb"/> — length-prefixed names so no
    /// two distinct ground terms can collide.</summary>
    private static void Canonicalize(Term t, System.Text.StringBuilder sb)
    {
        switch (t)
        {
            case AtomTerm a:
                sb.Append('a').Append(a.Name.Length).Append('_').Append(a.Name);
                break;
            case IntTerm i:
                sb.Append('i').Append(i.Value).Append('.');
                break;
            case CompoundTerm c:
                sb.Append('c').Append(c.Functor.Length).Append('_').Append(c.Functor)
                  .Append('/').Append(c.Args.Length).Append('(');
                foreach (var arg in c.Args) Canonicalize(arg, sb);
                sb.Append(')');
                break;
            default:
                string s = t.ToString() ?? "";
                sb.Append('o').Append(s.Length).Append('_').Append(s);
                break;
        }
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

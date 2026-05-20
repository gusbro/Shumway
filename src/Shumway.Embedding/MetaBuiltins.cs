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
        BuiltinsRegistry.Register("forall",  2, Forall);
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

        // clause/2 and current_predicate/1 are now Prolog-level predicates
        // defined in the prelude (chunk 40). They call these helpers to
        // bridge into the engine's clause and functor stores, then iterate
        // via the prelude's member/2.
        BuiltinsRegistry.Register("$all_clauses_of",            2, AllClausesOf);
        BuiltinsRegistry.Register("$all_predicate_indicators",  1, AllPredicateIndicators);
        BuiltinsRegistry.Register("abolish",                    1, Abolish);

        BuiltinsRegistry.Register("numbervars",        3, NumberVars);
        BuiltinsRegistry.Register("term_to_atom",      2, TermToAtom);

        BuiltinsRegistry.Register("functor", 3, Functor);
        BuiltinsRegistry.Register("arg",     3, Arg);
        BuiltinsRegistry.Register("=..",     2, Univ);

        BuiltinsRegistry.Register("read_term_from_atom", 2, ReadTermFromAtom);

        BuiltinsRegistry.Register("op", 3, Op);
        BuiltinsRegistry.Register("set_prolog_flag",     2, SetPrologFlag);
        BuiltinsRegistry.Register("current_prolog_flag", 2, CurrentPrologFlag);
        BuiltinsRegistry.Register("with_output_to", 2, WithOutputTo);
        BuiltinsRegistry.Register("atom_to_term",   3, AtomToTerm);
        BuiltinsRegistry.Register("read_term_from_stream", 2, ReadTermFromStream);
        // ISO read_term/2 — accepts a stream handle in arg 1 and unifies
        // the parsed term with arg 2. Chunk 59: delegate to the existing
        // stream-aware reader so the builtin set covers both names.
        BuiltinsRegistry.Register("read_term", 2, ReadTermFromStream);
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
    private static Term TranslateRuntimeError(PrologRuntimeException re) => re.Kind switch
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

    /// <summary><c>forall(Cond, Then)</c> — succeeds iff every solution
    /// of <c>Cond</c> makes <c>Then</c> succeed too. Implemented by
    /// running <c>Cond</c> in a peer sub-engine to fully enumerate its
    /// solutions, then for each solution applying its bindings to
    /// <c>Then</c> and running that in a fresh sub-engine. Bails on the
    /// first counter-example.
    ///
    /// <para>This sits as a C# builtin (rather than the obvious Prolog
    /// <c>\+ (Cond, \+ Then)</c>) because Phase-1 <c>call/N</c> only
    /// returns one solution; a Prolog-level forall would silently miss
    /// counter-examples from goals whose first solution happens to
    /// satisfy <c>Then</c>. Going via the sub-engine bypasses the
    /// single-solution call/N restriction entirely.</para></summary>
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

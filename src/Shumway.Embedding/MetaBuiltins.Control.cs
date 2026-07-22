using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    /// <summary><c>debugger_break/0</c> (ADR-035) — stop here, if anyone is watching.
    ///
    /// <para>The Prolog counterpart of <c>Debugger.Break()</c>, and it is exactly that
    /// underneath: in a managed process, a break is something the runtime can ask for
    /// directly, and the debugger honours it without any of the machinery a breakpoint
    /// needs. Which makes this the shortest path there is from a program to a stopped
    /// debugger — put it in the clause you care about, run under <c>--debug</c>, attach, and
    /// the next time that clause is reached you are standing in it.</para>
    ///
    /// <para>With no debugger attached it does nothing and succeeds — a program can be left
    /// with these in it. And the snapshot is written FIRST, so that by the time the debugger
    /// has the process, the Prolog stack it is about to show is already in memory.</para>
    /// </summary>
    public static bool DebuggerBreak(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException("debugger_break/0 requires a PrologEngine host.");
        if (!System.Diagnostics.Debugger.IsAttached)
            return true;   // nobody is watching: this is a no-op, by design

        Shumway.Embedding.Debugging.ShumwayDebugHelper.Session?.BreakHere(engine);
        return true;
    }

    /// <summary><c>notrace/0</c> (ADR-035) — detaches the tracer, from the
    /// running activation as well as the engine.</summary>
    public static bool NoTrace(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException("notrace/0 requires a PrologEngine host.");
        host.SetTracing(false);
        engine.Debug = null;
        return true;
    }

    public static bool Repeat(Activation engine)
    {
        ArmRepeat(engine, engine.BuiltinReturnPc);
        return true;
    }

    /// <summary>ADR-022 — runs the embedded native block named by argument 0,
    /// with its Prolog variables in registers 1.. (see the registration in
    /// <see cref="EnsureRegistered"/>).</summary>
    public static bool NativeRun(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new System.InvalidOperationException("'$native_run' requires a PrologEngine host.");
        // read the block name as a raw atom cell and resolve through
        // the host's atom-id cache: no Term materialization, no string hashing on
        // the per-dispatch path.
        Cell nameCell = RegisterMarshalling.DerefRegisterCell(engine, 0);
        if (nameCell.Tag != Tag.Atom)
            throw new System.InvalidOperationException("'$native_run': block name must be an atom.");
        var block = host.NativeBlockByAtomId(nameCell.AsAtomId)
            ?? throw new System.InvalidOperationException(
                $"'$native_run': native block '{AtomTable.GetById(nameCell.AsAtomId)?.Name}' is not registered.");
        // Compile the block to a delegate on first execution (in engine context,
        // so interop resolves to concrete methods); cache it, with the interpreter
        // as the fallback when compilation isn't possible (an unsupported
        // construct, or Native AOT). Item 2 — replaces the interpreter on the hot
        // path with JIT-compiled IL.
        if (!block.CompileTried)
        {
            block.Compiled = NativeBlockCompiler.TryCompile(
                block.Vars, block.Stmts, block.ScalarGlobals, regOffset: 1, host.ResolveNativeInterop, host);
            block.CompileTried = true;
        }
        return block.Compiled is not null
            ? block.Compiled(engine)
            // the entry-based overload reuses the block's cached
            // invariant maps instead of rebuilding them per dispatch.
            : NativeBlockRunner.RunBlock(engine, block, regOffset: 1);
    }

    // ----- ADR-024 generic-term interop (reftype tier) -----------------------

    /// <summary>Extracts the <see cref="TermSlot"/> a register holds (a Foreign
    /// cell), or null. reads the dereferenced cell directly; the old
    /// path materialized a <c>'$foreign'(Id)</c> CompoundTerm+IntTerm per call just
    /// to extract the id, on the hottest cursor builtins (fill_par/reftype_term/
    /// make_c_string/make_prolog_string).</summary>
    private static TermSlot? ReadSlot(Activation engine, int reg)
    {
        Cell c = RegisterMarshalling.DerefRegisterCell(engine, reg);
        return c.Tag == Tag.Foreign ? engine.AsForeign<TermSlot>(c) : null;
    }

    /// <summary>ADR-024 — creates a fresh empty term slot and binds it (as a
    /// Foreign cell) to argument 0. Used to obtain a reftype where a `:- c`
    /// region's <c>reftype</c> global isn't available (tests, and any predicate
    /// that needs an ad-hoc slot).</summary>
    public static bool NewReftypeSlot(Activation engine)
        => engine.UnifyRegisterWithCell(0, engine.MakeForeign(new TermSlot()));

    /// <summary>ADR-024 — <c>fill_par(Term, RefType)</c>: store the Prolog term in
    /// the slot (term → cursor). Zero-copy at the AST level — the term is read as
    /// it currently stands.</summary>
    public static bool FillPar(Activation engine)
    {
        var slot = ReadSlot(engine, 1);
        if (slot is null) return false;
        slot.SetValue(RegisterMarshalling.ReadRegisterAsTerm(engine, 0));
        return true;
    }

    /// <summary>ADR-024 — <c>reftype_term(Term, RefType)</c>: materialize the
    /// slot's cursor to a Prolog term and unify it with argument 0 (cursor →
    /// term).</summary>
    public static bool ReftypeTerm(Activation engine)
    {
        var slot = ReadSlot(engine, 1);
        if (slot is null) return false;
        return RegisterMarshalling.UnifyRegisterWithTerm(engine, 0, slot.Materialize());
    }

    /// <summary>ADR-024 — <c>preftype(RefType)</c>: succeeds when argument 0 is a
    /// valid reftype slot.</summary>
    public static bool Preftype(Activation engine) => ReadSlot(engine, 0) is not null;

    /// <summary>ADR-024 — <c>reftype_term(Term, Type, RefType)</c>: the /2 form with
    /// an extra type-tag argument (ignored; the slot knows its own shape).</summary>
    public static bool ReftypeTerm3(Activation engine)
    {
        var slot = ReadSlot(engine, 2);
        if (slot is null) return false;
        return RegisterMarshalling.UnifyRegisterWithTerm(engine, 0, slot.Materialize());
    }

    /// <summary>ADR-024 — <c>fill_reftype(Term, Type, RefType)</c>: store the Prolog
    /// term in the slot (the type-tag argument is ignored).</summary>
    public static bool FillReftype3(Activation engine)
    {
        var slot = ReadSlot(engine, 2);
        if (slot is null) return false;
        slot.SetValue(RegisterMarshalling.ReadRegisterAsTerm(engine, 0));
        return true;
    }

    /// <summary>ADR-024 — <c>make_c_string(Holder, _, Value, _)</c>: store Value
    /// into the holder slot (a reusable buffer — set, not unify, so successive
    /// fills don't alias their Prolog values). When the first argument is a plain
    /// atom (a value, not a holder), it degrades to identity <c>arg0 = arg2</c>.
    /// The max-length / actual-length arguments are vestigial in .NET.</summary>
    public static bool MakeCString4(Activation engine) => CStringMove(engine, holderReg: 0, valueReg: 2);

    /// <summary>ADR-024 — <c>make_prolog_string(Source, Var)</c> /
    /// <c>make_prolog_string_c</c>: read the source into the Prolog variable. When
    /// Source is a holder slot, Var gets the holder's current value; when Source is
    /// a plain atom (value), it is identity <c>arg0 = arg1</c>.</summary>
    public static bool MakePrologString2(Activation engine) => PrologStringMove(engine, sourceReg: 0, varReg: 1);

    // Holder → a TermSlot wrapped as a Foreign cell; an atom → a value.
    private static bool CStringMove(Activation engine, int holderReg, int valueReg)
    {
        var holder = ReadSlot(engine, holderReg);
        var value = RegisterMarshalling.ReadRegisterAsTerm(engine, valueReg);
        if (holder is not null) { holder.SetValue(value); return true; }
        // value mode: identity arg0 = arg2 (one must be an atom).
        return UnifyAtomPair(engine, holderReg, valueReg);
    }

    private static bool PrologStringMove(Activation engine, int sourceReg, int varReg)
    {
        var holder = ReadSlot(engine, sourceReg);
        if (holder is not null)
            return RegisterMarshalling.UnifyRegisterWithTerm(engine, varReg, holder.Materialize());
        // ADR-024 char* return: the source is a native `char*` pointer (an integer
        // from a `:- native` P/Invoke function whose `:- c` return type is char*).
        // Read the NUL-terminated native string with the engine's text encoding.
        var src = RegisterMarshalling.ReadRegisterAsTerm(engine, sourceReg);
        if (src is IntTerm ptr)
        {
            var enc = (engine.Host as PrologEngine)?.NativeTextEncoding ?? NativeReftype.DefaultEncoding;
            string s = NativeReftype.ReadString((System.IntPtr)ptr.Value, enc);
            return RegisterMarshalling.UnifyRegisterWithTerm(engine, varReg, new AtomTerm(s));
        }
        return UnifyAtomPair(engine, sourceReg, varReg);
    }

    /// <summary>The value-mode identity: unify the two registers, requiring the
    /// shared value to be an atom (the body of <c>p(S, S) :- atom(S)</c>).</summary>
    private static bool UnifyAtomPair(Activation engine, int r0, int r1)
    {
        var t0 = RegisterMarshalling.ReadRegisterAsTerm(engine, r0);
        var t1 = RegisterMarshalling.ReadRegisterAsTerm(engine, r1);
        if (t0 is AtomTerm) return RegisterMarshalling.UnifyRegisterWithTerm(engine, r1, t0);
        if (t1 is AtomTerm) return RegisterMarshalling.UnifyRegisterWithTerm(engine, r0, t1);
        return false;
    }

    /// <summary>ADR-024 — <c>quote_str(X, XR)</c>: XR is X rendered in writeq
    /// (quoted) form, as an atom. (prlg_ifce.pl does this through C string buffers;
    /// the cursor model renders directly.)</summary>
    public static bool QuoteStr(Activation engine)
    {
        using var sw = new System.IO.StringWriter();
        Shumway.Builtins.TermRenderer.Render(engine, engine.GetRegister(0), sw,
            new Shumway.Builtins.TermRenderOptions { Operators = engine.Operators, Quoted = true });
        int id = AtomTable.Intern(sw.ToString(), permanent: false).Id;
        return engine.UnifyRegisterWithCell(1, Shumway.Core.Cell.Atom(id));
    }

    private static void ArmRepeat(Activation engine, int returnPc)
    {
        // The CP re-arms with ONE cached delegate (held on the cursor) rather
        // than a fresh closure per backtrack — repeat drives unbounded
        // failure-driven loops (`repeat, Goal, fail`), so a per-backtrack
        // closure was ~100 bytes of Gen0 garbage per iteration, the same
        // bottleneck fixed in between/3.
        var cursor = new RepeatCursor(returnPc);
        engine.PushBuiltinChoicePoint(cursor.Resume, arity: 0);
    }

    private sealed class RepeatCursor
    {
        private readonly int _returnPc;
        public readonly Func<Activation, int, bool> Resume;

        public RepeatCursor(int returnPc)
        {
            _returnPc = returnPc;
            Resume = Step;
        }

        private bool Step(Activation engine, int _)
        {
            engine.PushBuiltinChoicePoint(Resume, arity: 0);   // re-arm, same delegate
            engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    // (CallNCursor + AppendArgs removed — call/N is dispatched in the live
    // engine by DispatchCall / IlMetaCallHelper; MetaBuiltins.CallN is now a
    // dead-path guard that throws. See CallN.)

    private static int ExtractAddrFromName(string name)
    {
        if (name.Length >= 3 && name[0] == '_' && name[1] == 'G'
            && int.TryParse(name.AsSpan(2), out int addr))
            return addr;
        return -1;
    }

    // ============================================================================
    // consult / reconsult
    // ============================================================================

    /// <summary><c>consult(+File)</c> — loads a Prolog source or compiled
    /// Shumway bundle. Routes by extension through
    /// <see cref="PrologEngine.ConsultFile"/>: <c>.shum</c> goes through
    /// <see cref="PrologEngine.LoadBundle"/>, everything else is read as
    /// Prolog source and handed to <see cref="PrologEngine.ConsultString"/>.
    /// ISO errors: <c>instantiation_error</c> for an unbound file arg,
    /// <c>type_error(atom, _)</c> for a non-atom, and
    /// <c>existence_error(source_sink, _)</c> when the path doesn't
    /// exist.</summary>
    public static bool Consult(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "consult/1 requires the engine to be hosted by a PrologEngine.");

        Cell cell = MaterializeRegisterAsCell(engine, 0);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (cell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException(
                "type_error(atom, _)");

        string path = AtomTable.GetById(cell.AsAtomId)?.Name ?? "";
        if (!System.IO.File.Exists(path))
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");

        // Runtime consult: thread the live engine so source-declared dynamic
        // clauses become visible to a later call in the same query.
        host.ConsultFileLive(path, engine);
        return true;
    }

    /// <summary><c>use_module(+Spec)</c> — SWI-style library loader. With
    /// <c>library(Name)</c> loads a built-in library: the constraint
    /// libraries <c>clpfd</c> / <c>clpr</c>, or a Scryer/Trealla
    /// compatibility library (<c>dcgs</c>, <c>format</c>, <c>dif</c>, and the
    /// prelude-covered no-ops — see <see cref="CompatLibraries"/>). With an
    /// atom, behaves like <see cref="Consult"/>.</summary>
    public static bool UseModule(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "use_module/1 requires the engine to be hosted by a PrologEngine.");

        Term arg = MaterializeRegister(engine, 0);
        if (arg is Shumway.Compiler.Ast.VarTerm)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");

        if (arg is Shumway.Compiler.Ast.CompoundTerm c
            && c.Functor == "library" && c.Args.Length == 1
            && c.Args[0] is Shumway.Compiler.Ast.AtomTerm libAtom)
        {
            switch (libAtom.Name)
            {
                case "clpfd": host.UseClpfd(); return true;
                case "clpr":  host.UseClpr();  return true;
                default:
                    // Scryer/Trealla stdlib compatibility libraries (dcgs,
                    // format, dif, and the prelude-covered no-ops).
                    if (host.UseCompatLibrary(libAtom.Name)) return true;
                    throw new Shumway.Core.PrologRuntimeException(
                        $"existence_error(library, {libAtom.Name})");
            }
        }

        if (arg is Shumway.Compiler.Ast.AtomTerm pathAtom)
        {
            if (!System.IO.File.Exists(pathAtom.Name))
                throw new Shumway.Core.PrologRuntimeException(
                    $"existence_error(source_sink, '{pathAtom.Name}')");
            host.ConsultFile(pathAtom.Name);
            return true;
        }

        throw new Shumway.Core.PrologRuntimeException(
            "type_error(atom_or_library, _)");
    }

    /// <summary><c>save_state(+File)</c> — Arity-Prolog compatible
    /// builtin. Writes a snapshot of the engine's state (consult
    /// history + dynamic clauses) to <c>File</c>. See
    /// <see cref="PrologEngine.SaveState"/>.</summary>
    public static bool SaveState1(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "save_state/1 requires the engine to be hosted by a PrologEngine.");
        string path = RequireAtomPath(engine, register: 0, builtin: "save_state/1");
        host.SaveState(path, dynamicOnly: false);
        return true;
    }

    /// <summary><c>save_state(+File, +Options)</c> — option-list variant.
    /// Currently recognises <c>dynamic_only(true)</c>.</summary>
    public static bool SaveState2(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "save_state/2 requires the engine to be hosted by a PrologEngine.");
        string path = RequireAtomPath(engine, register: 0, builtin: "save_state/2");
        Term opts = MaterializeRegister(engine, 1);
        bool dynamicOnly = ExtractDynamicOnly(opts);
        host.SaveState(path, dynamicOnly);
        return true;
    }

    /// <summary><c>save/0</c> — Arity-compatible: snapshots the user dynamic
    /// database in memory (replacing any previous snapshot). See
    /// <see cref="PrologEngine.SaveDb"/>.</summary>
    public static bool Save0(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "save/0 requires the engine to be hosted by a PrologEngine.");
        host.SaveDb();
        return true;
    }

    /// <summary><c>save(+File)</c> — Arity-compatible: writes the dynamic-
    /// database snapshot to <c>File</c>. See
    /// <see cref="PrologEngine.SaveDbToFile"/>.</summary>
    public static bool Save1(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "save/1 requires the engine to be hosted by a PrologEngine.");
        string path = RequireAtomPath(engine, register: 0, builtin: "save/1");
        host.SaveDbToFile(path);
        return true;
    }

    /// <summary><c>restore/0</c> — Arity-compatible destructive REPLACE:
    /// wipes every user dynamic predicate's clauses and re-installs the last
    /// <c>save/0</c> snapshot (no snapshot = wipe only). Declarations and
    /// static predicates are untouched. See
    /// <see cref="PrologEngine.RestoreDb"/>.</summary>
    public static bool Restore0(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "restore/0 requires the engine to be hosted by a PrologEngine.");
        host.RestoreDb(engine);
        return true;
    }

    /// <summary><c>restore(+File)</c> — <c>restore/0</c> semantics with the
    /// snapshot read from <c>File</c> (written by <c>save/1</c>). See
    /// <see cref="PrologEngine.RestoreDbFromFile"/>.</summary>
    public static bool Restore1(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "restore/1 requires the engine to be hosted by a PrologEngine.");
        string path = RequireAtomPath(engine, register: 0, builtin: "restore/1");
        if (!System.IO.File.Exists(path))
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        try { host.RestoreDbFromFile(engine, path); }
        catch (InvalidDataException ex)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"type_error(save_file, '{path}') /* {ex.Message} */");
        }
        return true;
    }

    /// <summary><c>restore_state(+File)</c> — loads a snapshot previously
    /// written by <c>save_state/1,2</c>. See
    /// <see cref="PrologEngine.RestoreState"/>.</summary>
    public static bool RestoreState1(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "restore_state/1 requires the engine to be hosted by a PrologEngine.");
        string path = RequireAtomPath(engine, register: 0, builtin: "restore_state/1");
        if (!System.IO.File.Exists(path))
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        try { host.RestoreState(path); }
        catch (InvalidDataException ex)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"type_error(save_state_file, '{path}') /* {ex.Message} */");
        }
        return true;
    }

    private static string RequireAtomPath(Activation engine, int register, string builtin)
    {
        Cell cell = MaterializeRegisterAsCell(engine, register);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (cell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException(
                "type_error(atom, _)");
        return AtomTable.GetById(cell.AsAtomId)?.Name
            ?? throw new InvalidOperationException(
                $"{builtin}: atom id {cell.AsAtomId} has no entry in the atom table.");
    }

    private static bool ExtractDynamicOnly(Term opts)
    {
        // Walk a [...] list literal looking for dynamic_only(true).
        // An unbound tail or a non-list term raises a domain error.
        Term cursor = opts;
        while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
        {
            if (cons.Args[0] is CompoundTerm { Functor: "dynamic_only", Args.Length: 1 } o
                && o.Args[0] is AtomTerm a)
            {
                if (a.Name == "true") return true;
                if (a.Name == "false") return false;
            }
            cursor = cons.Args[1];
        }
        if (cursor is AtomTerm { Name: "[]" }) return false;
        throw new Shumway.Core.PrologRuntimeException(
            "type_error(list, _)");
    }

    /// <summary><c>reconsult(+File)</c> — classical edit-reload semantics:
    /// abolishes every predicate whose indicator is defined in
    /// <c>File</c> in the target module, then loads <c>File</c>. Argument
    /// validation and error shapes are identical to
    /// <see cref="Consult"/>.</summary>
    public static bool Reconsult(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "reconsult/1 requires the engine to be hosted by a PrologEngine.");

        Cell cell = MaterializeRegisterAsCell(engine, 0);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (cell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException(
                "type_error(atom, _)");

        string path = AtomTable.GetById(cell.AsAtomId)?.Name ?? "";
        if (!System.IO.File.Exists(path))
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");

        host.ReconsultFile(path);
        return true;
    }

    // ============================================================================
    // assertz / asserta / retract
    // ============================================================================

    public static bool Assertz(Activation engine) => AssertImpl(engine, prepend: false);
    public static bool Asserta(Activation engine) => AssertImpl(engine, prepend: true);

    private static bool AssertImpl(Activation engine, bool prepend)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "assert: PrologEngine host required.");

        Term clauseTerm = MaterializeRegister(engine, 0);
        var clause = Shumway.Compiler.Ast.Clause.From(clauseTerm);
        // Asserta/Assertz extract the head functor id anyway —
        // take it from the return instead of re-extracting (a second
        // string intern per assert).
        int fid = prepend ? host.Asserta(clause) : host.Assertz(clause);
        // ADR-015 chunk C step 4: incremental dispatch — the canonical
        // path (the chunk-C redirect is gone).
        //   assertz → append a chunk and patch the tail's <next>.
        //   asserta → append a chunk, patch the trampoline's execute,
        //             and demote the old head's try_me_else in place to
        //             retry_me_else + 4 nops (same 9-byte footprint).
        if (prepend)
            host.PrependDynamicClauseIncremental(engine, fid, clause);
        else
            host.AppendDynamicClauseIncremental(engine, fid, clause);
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
    /// <summary><c>'$retractall_modifiable'(Head)</c> — retractall/1's guard
    /// (see <see cref="PrologEngine.IsRetractAllModifiable"/>): succeeds when
    /// Head's predicate is dynamic (run the retract loop), FAILS when it is
    /// undefined (retractall is a no-op), and raises
    /// <c>permission_error(modify, static_procedure)</c> for a static procedure
    /// or builtin.</summary>
    public static bool RetractAllModifiable(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$retractall_modifiable'/1: PrologEngine host required.");
        int headHeap = engine.MaterializeRegisterForTrace(0);
        int fid = ReadPatternHeadFunctorId(engine, headHeap);
        return host.IsRetractAllModifiable(fid);
    }

    public static bool Retract(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "retract: PrologEngine host required.");

        // read the pattern's head functor id straight from
        // the heap, without materialising the whole pattern as a
        // Term AST first. ExtractHeadFunctorIdFromClause walked the
        // freshly-built AST and re-interned the head's name —
        // measured ~7% of total Blint.pl time, all of which was
        // wasted: the heap representation already has the functor id
        // sitting one slot inside the STR.
        int patternHeap = engine.MaterializeRegisterForTrace(0);
        int patternFid = ReadPatternHeadFunctorId(engine, patternHeap);

        // ISO §7.12.2.h — retracting from a static
        // predicate is permission_error(modify, static_procedure, _),
        // not a silent failure. The check fires after the head's type
        // check (above) so type errors win precedence.
        if (!host.IsDynamic(patternFid))
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "modify,static_procedure");

        // scan the LIVE clause list directly — no snapshot copy.
        // This is sound for the first step: the scan runs to completion
        // before anything can mutate the list (no goal executes between
        // here and the match). The logical-update-view snapshot is taken
        // ONLY if a choice point is pushed (the remaining-candidates tail
        // is copied into the resume closure at push time, which is still
        // call time) — the common `retract(_), !` idiom never pays it.
        IReadOnlyList<Clause> candidates = host.DynamicClausesFor(patternFid);
        RetractTrace.Begin(null!, patternFid, candidates.Count);
        int returnPc = engine.BuiltinReturnPc;
        // patternHeap is the pattern's heap home (the result of
        // MaterializeRegister on register 0). Pre-fix this was re-read
        // from register 0 inside every RetractStep, but the CP save
        // for register 0 turned out to be unreliable — under heavy
        // dynamic-mutation load (Blint.pl's `retract(next_char_i(X))`
        // loop linting Blint.pl is the surfacing example) the saved
        // arg slot in the CP frame gets clobbered between push and
        // pop, so the resume reads a stale REF and binds the
        // pattern's var to the entire candidate STR instead of its
        // arg. The pattern itself, on the other hand, lives at a
        // heap address that's BELOW the CP's saved heap top — so
        // it survives the backtrack truncation. Capturing it once
        // here side-steps the register-clobber.
        return RetractStep(engine, host, patternFid, candidates, returnPc,
            patternHeap);
    }

    /// <summary>Reads the pattern's head functor id straight from the
    /// heap. Mirrors the ISO callability check in
    /// <see cref="ExtractHeadFunctorIdFromClause"/> but avoids the
    /// Term AST allocation — for retract's hot path the heap shape
    /// is sufficient.</summary>
    private static int ReadPatternHeadFunctorId(Activation engine, int patternHeap)
    {
        int idx = engine.Deref(patternHeap);
        Cell c = engine.GetHeap(idx);
        // retract((Head :- Body)) — descend into the Head slot.
        if (c.Tag == Tag.Str)
        {
            int sa = c.AsHeapIndex;
            Cell f = engine.GetHeap(sa);
            if (f.Tag == Tag.Functor)
            {
                int fid = f.AsFunctorId;
                var (atomId, arity) = FunctorTable.Lookup(fid);
                if (arity == 2 && atomId == _ruleFunctorAtomId)
                {
                    // Head is at sa + 1
                    int headIdx = engine.Deref(sa + 1);
                    Cell hc = engine.GetHeap(headIdx);
                    return ReadFunctorIdFromCell(engine, hc);
                }
                return fid;
            }
        }
        return ReadFunctorIdFromCell(engine, c);
    }

    private static int ReadFunctorIdFromCell(Activation engine, Cell c)
    {
        if (c.Tag == Tag.Atom)
            return FunctorTable.Intern(c.AsAtomId, 0);
        if (c.Tag == Tag.Str)
        {
            int sa = c.AsHeapIndex;
            Cell f = engine.GetHeap(sa);
            if (f.Tag == Tag.Functor) return f.AsFunctorId;
        }
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        throw new Shumway.Core.PrologRuntimeException("type_error", "callable");
    }

    private static readonly int _ruleFunctorAtomId =
        AtomTable.Intern(":-", permanent: true).Id;

    /// <summary>Removes the first clause that unifies with the retract
    /// pattern — the entry step, scanning the LIVE clause list
    /// (no snapshot copy; nothing can mutate the list before this scan
    /// completes). When later candidates remain it leaves a choice point
    /// whose resume retracts the following match — that is what makes
    /// <c>retract/1</c> enumerate every matching clause on backtracking.</summary>
    private static bool RetractStep(Activation engine, PrologEngine host,
        int patternFid, IReadOnlyList<Clause> candidates, int returnPc,
        int patternHeap)
    {
        RetractTrace.StepEntry(engine, isResume: false, startIndex: 0);
        int matchIndex = FindRetractMatch(
            engine, candidates, 0, candidates.Count, patternHeap);
        if (matchIndex < 0)
        {
            RetractTrace.NoMatch(candidates.Count);
            return false;
        }
        RetractTrace.MatchFound(matchIndex, candidates[matchIndex]);

        // Push the choice point before the real unification below, so a
        // backtrack's trail unwind peels off exactly this solution's
        // bindings before the resume retracts the next match.
        if (matchIndex + 1 < candidates.Count)
        {
            // snapshot ONLY the remaining candidates into the
            // resume closure, here at push time (still call time, so the
            // ISO logical-update view is the same one a full up-front copy
            // captured). The live list mutates the moment the retract
            // returns; the resume must not read it.
            //
            // the copy lands in a pooled per-engine buffer
            // instead of a fresh array, and the whole enumeration shares
            // this ONE snapshot — each resume advances a start index
            // rather than re-copying its own tail-of-tail (the old code's
            // O(k²) copying across a k-solution enumeration). The buffer
            // returns to the pool at the enumeration's terminal resume, or
            // via OnPrune when a cut discards the CP (the audit's
            // `retract(_), !` case — pre-431 that tail copy was pure
            // garbage).
            int count = candidates.Count - (matchIndex + 1);
            Clause[] snap = host.RentRetractSnapshot(count);
            for (int i = 0; i < count; i++)
                snap[i] = candidates[matchIndex + 1 + i];
            // The resume + onPrune delegates live on ONE cursor (allocated
            // here, re-pushed unchanged on every backtrack) rather than a
            // fresh pair per matching clause. patternHeap is re-read from
            // register 0 on resume, so the cursor need not close over it; the
            // CP is pushed with arity 1 so the WAM saves register 0 and the
            // GC relocates it.
            var cursor = new RetractCursor(host, patternFid, snap, count, returnPc);
            RetractTrace.PrePush(engine);
            engine.PushBuiltinChoicePoint(cursor.Resume, arity: 1, cursor.OnPrune);
            RetractTrace.PostPush(engine);
        }

        Clause candidate = candidates[matchIndex];
        int savedHb = engine.Hb;
        engine.SetHb(engine.HeapTop);
        Cell candidateCell = Materializer.MaterializeAsCell(engine, candidate.Term);
        int candSlot = engine.AllocateHeap(1);
        engine.SetHeap(candSlot, candidateCell);

        RetractTrace.HeapStateBeforeUnify(engine, patternHeap, candSlot, savedHb);
        bool unifyResult = engine.Unify(patternHeap, candSlot);
        RetractTrace.HeapStateAfterUnify(engine, patternHeap, candSlot, unifyResult);

        // the first step's candidates ARE the live list, so
        // matchIndex is the live index — pass it through to skip the
        // O(N) IndexOf.
        host.RemoveDynamicByReference(engine, patternFid, candidate,
            knownIndex: matchIndex);
        engine.SetHb(savedHb);
        return true;
    }

    /// <summary>resume state for a backtrackable <c>retract/1</c>
    /// enumeration: the call-time snapshot of remaining candidates, the
    /// running start index, and cached <c>Resume</c> + <c>OnPrune</c>
    /// delegates (allocated once per enumeration, re-pushed unchanged on each
    /// backtrack — no per-clause closure pair). Semantics are identical to the
    /// pre-cursor resume; only the per-step allocation moved onto the cursor.
    /// <c>_snapCount</c> bounds the used range of <c>_snap</c>, which may be a
    /// pooled buffer longer than the snapshot it holds.</summary>
    private sealed class RetractCursor
    {
        private readonly PrologEngine _host;
        private readonly int _patternFid;
        private readonly Clause[] _snap;
        private readonly int _snapCount;
        private readonly int _returnPc;
        private int _startIndex;
        public readonly Func<Activation, int, bool> Resume;
        public readonly Action OnPrune;

        public RetractCursor(PrologEngine host, int patternFid, Clause[] snap,
            int snapCount, int returnPc)
        {
            _host = host;
            _patternFid = patternFid;
            _snap = snap;
            _snapCount = snapCount;
            _returnPc = returnPc;
            _startIndex = 0;
            Resume = (e, _) => Step(e);
            OnPrune = () => _host.ReturnRetractSnapshot(_snap, _snapCount);
        }

        private bool Step(Activation engine)
        {
            // ADR-016: re-read the pattern from register 0. The choice point
            // was pushed with arity 1, so the WAM CP machinery saved
            // register 0 (the pattern's REF) and the heap GC relocates it
            // like any saved argument; the restore repopulates register 0
            // before this delegate runs. Closing a raw heap index over the
            // resume would dangle after a mid-enumeration collection moved
            // the pattern cell.
            int patternHeap = engine.MaterializeRegisterForTrace(0);
            RetractTrace.StepEntry(engine, isResume: true, _startIndex);
            int matchIndex = FindRetractMatch(
                engine, _snap, _startIndex, _snapCount, patternHeap);
            if (matchIndex < 0)
            {
                // Enumeration exhausted — nothing references the snapshot once
                // this (already-popped) CP's delegate returns; recycle it.
                _host.ReturnRetractSnapshot(_snap, _snapCount);
                RetractTrace.NoMatch(_snapCount);
                return false;
            }
            RetractTrace.MatchFound(matchIndex, _snap[matchIndex]);

            Clause candidate = _snap[matchIndex];
            bool morePending = matchIndex + 1 < _snapCount;
            if (morePending)
            {
                // Re-arm with the SAME snapshot + delegates, advancing the
                // start index — the snapshot is immutable and exclusively
                // owned by this enumeration (the CP that carried it was
                // popped before this delegate ran), so no copy is needed.
                _startIndex = matchIndex + 1;
                RetractTrace.PrePush(engine);
                engine.PushBuiltinChoicePoint(Resume, arity: 1, OnPrune);
                RetractTrace.PostPush(engine);
            }

            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);
            Cell candidateCell = Materializer.MaterializeAsCell(engine, candidate.Term);
            int candSlot = engine.AllocateHeap(1);
            engine.SetHeap(candSlot, candidateCell);

            RetractTrace.HeapStateBeforeUnify(engine, patternHeap, candSlot, savedHb);
            bool unifyResult = engine.Unify(patternHeap, candSlot);
            RetractTrace.HeapStateAfterUnify(engine, patternHeap, candSlot, unifyResult);

            // A resume scans a tail snapshot (indices don't map onto the
            // mutated live list): pass -1 to fall back to the IndexOf path.
            _host.RemoveDynamicByReference(engine, _patternFid, candidate,
                knownIndex: -1);
            engine.SetHb(savedHb);
            if (!morePending)
            {
                // Last candidate consumed and no new CP holds the snapshot —
                // recycle it (the `candidate` local keeps the clause alive
                // through the clear).
                _host.ReturnRetractSnapshot(_snap, _snapCount);
            }
            engine.ResumeAtReturnPc(_returnPc);
            return true;
        }
    }

    /// <summary>Index of the first candidate (from <paramref name="startIndex"/>)
    /// whose clause unifies with the retract pattern in register 0, or −1
    /// when none does. The trial unification is fully rolled back; the
    /// caller re-does it for the chosen candidate after its choice point
    /// is in place.
    ///
    /// <para>each trial used to materialise the WHOLE candidate
    /// clause onto the engine heap before unifying — for a keyed retract
    /// over a long predicate (Blint's <c>retract(saved_cur_line_i(Line,_))</c>
    /// over ~125 clauses) that is ~K clause materialisations per call, all
    /// but one rolled back. <see cref="DefiniteMismatch"/> now skips a
    /// candidate on a PROVEN structural mismatch (distinct atoms / ints /
    /// functors at the same position) with zero allocation; only candidates
    /// it cannot refute pay the materialise-and-unify trial.</para></summary>
    private static int FindRetractMatch(
        Activation engine, IReadOnlyList<Clause> candidates, int startIndex,
        int endExclusive, int patternHeap)
    {
        // endExclusive bounds the scan explicitly — a resume's
        // candidates live in a pooled buffer that may be longer than the
        // snapshot it holds, so candidates.Count is not the right bound.
        for (int i = startIndex; i < endExclusive; i++)
        {
            if (DefiniteMismatch(engine, patternHeap, candidates[i].Term, depth: 4))
                continue;
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            Cell candidateCell =
                Materializer.MaterializeAsCell(engine, candidates[i].Term);
            int candSlot = engine.AllocateHeap(1);
            engine.SetHeap(candSlot, candidateCell);
            bool matches = engine.Unify(patternHeap, candSlot);

            engine.UnwindTrails(savedBindingTrail, savedExtraTrail);
            engine.SetHeapTop(savedHeapTop);
            engine.SetHb(savedHb);
            if (matches) return i;
        }
        return -1;
    }

    /// <summary>true only when the pattern at
    /// <paramref name="heapIdx"/> PROVABLY cannot unify with the candidate
    /// AST <paramref name="ast"/>: distinct atoms, distinct inline ints,
    /// distinct principal functors, or an atomic vs a compound. Anything
    /// uncertain — variables on either side, big integers, floats vs the
    /// float table, partial strings, foreigns, depth exhausted — returns
    /// false and the caller falls back to the real materialise-and-unify
    /// trial, so this can only SKIP work, never change the outcome.</summary>
    private static bool DefiniteMismatch(Activation engine, int heapIdx, Term ast, int depth)
    {
        if (depth <= 0 || ast is VarTerm) return false;
        int idx = engine.Deref(heapIdx);
        Cell c = engine.GetHeap(idx);
        switch (c.Tag)
        {
            case Tag.Atom:
                return ast switch
                {
                    // cached id — this used to re-intern the
                    // candidate's atom by name on EVERY retract trial.
                    AtomTerm a => a.ResolveAtomId() != c.AsAtomId,
                    IntTerm or FloatTerm or CompoundTerm or BigIntTerm => true,
                    _ => false,
                };
            case Tag.Int:
                return ast switch
                {
                    IntTerm n => n.Value != c.AsInt,
                    AtomTerm or FloatTerm or CompoundTerm => true,
                    _ => false,   // BigIntTerm etc.: uncertain
                };
            case Tag.Str:
            {
                Cell f = engine.GetHeap(c.AsHeapIndex);
                if (f.Tag != Tag.Functor) return false;
                switch (ast)
                {
                    case CompoundTerm ct:
                    {
                        // functor ids are canonical (one id per
                        // (atom, arity) pair — FunctorTable.Intern converges
                        // losers of its publish race onto the winner), so a
                        // single cached-id comparison replaces the per-trial
                        // FunctorTable.Lookup + by-name atom re-intern.
                        if (ct.ResolveFunctorId() != f.AsFunctorId)
                            return true;
                        int arity = ct.Args.Length;
                        for (int i = 0; i < arity; i++)
                            if (DefiniteMismatch(engine, c.AsHeapIndex + 1 + i,
                                    ct.Args[i], depth - 1))
                                return true;
                        return false;
                    }
                    case AtomTerm or IntTerm or FloatTerm or BigIntTerm:
                        return true;
                    default:
                        return false;   // StringTerm vs './2 shapes: uncertain
                }
            }
            case Tag.Lis:   // ADR-017 inline cons: [head, tail] at AsHeapIndex
                return ast switch
                {
                    CompoundTerm ct when ct.Functor == "." && ct.Args.Length == 2 =>
                        DefiniteMismatch(engine, c.AsHeapIndex, ct.Args[0], depth - 1)
                        || DefiniteMismatch(engine, c.AsHeapIndex + 1, ct.Args[1], depth - 1),
                    CompoundTerm => true,
                    AtomTerm or IntTerm or FloatTerm or BigIntTerm => true,
                    _ => false,   // StringTerm: a string can be a char list
                };
            default:
                // Ref/AttVar (could bind to anything), Float/BigInt/String/
                // Pstr/Foreign (cross-representation equivalences live in
                // the real unifier): uncertain.
                return false;
        }
    }

    private static int ExtractHeadFunctorIdFromClause(Clause clause)
    {
        Term head = clause.Kind == ClauseKind.Rule
            ? ((CompoundTerm)clause.Term).Args[0]
            : clause.Term;
        return head switch
        {
            // read-through the node's cached ids. The atom is
            // interned transient here, but every asserted clause is also
            // compiled by ClauseCompiler, whose InternAtom pins predicate
            // and literal names permanent — promotion keeps the id, so the
            // cache stays valid.
            AtomTerm a => FunctorTable.Intern(a.ResolveAtomId(), 0),
            CompoundTerm c => c.ResolveFunctorId(),
            // ISO §8.9.3 — an unbound head raises
            // instantiation_error; anything else non-callable raises
            // type_error(callable, _).
            VarTerm => throw new Shumway.Core.PrologRuntimeException("instantiation_error"),
            _ => throw new Shumway.Core.PrologRuntimeException("type_error", "callable"),
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
    public static bool CopyTerm(Activation engine)
    {
        // heap-to-heap copy — no intermediate managed AST tree
        // (was MaterializeRegister + MaterializeAsCell, ~1.3 KB garbage/call).
        Cell copyCell = HeapTermCopy.CopyRegister(engine, 0);
        return engine.UnifyRegisterWithCell(1, copyCell);
    }

    /// <summary><c>'$copy_term_3_prep'(Term, Copy, AttrInfo)</c> — the C#
    /// half of <c>copy_term/3</c>. Copies <c>Term</c> into
    /// <c>Copy</c> with fresh plain variables and produces
    /// <c>AttrInfo</c>: a list of <c>ag(Module, AttrValue, Var)</c>
    /// triples, one per (attributed variable, module) pair found in
    /// <c>Term</c>. <c>Copy</c> and <c>AttrInfo</c> are materialised in a
    /// single pass so a variable shared between the term and an
    /// attribute value is the <em>same</em> fresh variable in both — the
    /// prelude's <c>copy_term/3</c> then runs <c>attribute_goals/4</c>
    /// over the triples, and the residual goals come out expressed over
    /// <c>Copy</c>'s variables.</summary>
    public static bool CopyTerm3Prep(Activation engine)
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
    private static void CollectAttvars(Activation engine, Cell cell,
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
    public static bool Findall(Activation engine)
    {
        var results = CollectSolutions(engine, stripExistentials: false);
        return BindList(engine, results);
    }

    /// <summary><c>'$findall_push'/0</c> — opens a fresh
    /// solution buffer on the engine's findall stack. Emitted by the
    /// MetaTransform rewrite of <c>findall/3</c> as the first goal of the
    /// collect loop.</summary>
    public static bool FindallPush(Activation engine)
    {
        FindallHost(engine).PushFindallFrame();
        return true;
    }

    /// <summary><c>'$findall_record'(Template)</c> — copies the
    /// current value of <c>Template</c> (a snapshot AST term, off the WAM
    /// heap so backtracking can't unwind it) into the open findall
    /// buffer, then succeeds so the trailing <c>fail</c> drives
    /// enumeration on to the goal's next solution.</summary>
    public static bool FindallRecord(Activation engine)
    {
        FindallHost(engine).RecordFindallSolution(MaterializeRegister(engine, 0));
        return true;
    }

    /// <summary><c>'$findall_record_s'(Template)</c> — the
    /// findall/3 record path. Snapshots the solution straight into a
    /// backtrack-safe <see cref="Cell"/> image (no per-node managed AST). A
    /// value-leaf template (float / bigint / string / pstr / foreign) can't be
    /// imaged flatly, so <see cref="FindallSnapshot.TrySnapshotRegister"/>
    /// returns null and we fall back to the AST path. bagof/setof keep the
    /// AST-only <see cref="FindallRecord"/> because they inspect the recorded
    /// terms for witness grouping.</summary>
    public static bool FindallRecordSnapshot(Activation engine)
    {
        Cell[]? snap = FindallSnapshot.TrySnapshotRegister(engine, 0);
        if (snap != null)
            FindallHost(engine).RecordFindallSnapshot(snap);
        else
            FindallHost(engine).RecordFindallSolution(MaterializeRegister(engine, 0));
        return true;
    }

}

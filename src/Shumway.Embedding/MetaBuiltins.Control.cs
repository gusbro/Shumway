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

        // The attached session decides HOW to stop: the channel session (VS) takes the
        // managed Debugger.Break() path, gated on a debugger actually being attached; a
        // direct-attach session (the web page, the tests) stops in place. With no debug
        // session at all this is a no-op that succeeds — a program can be left with these
        // in it. (The old code hard-wired the VS path AND gated the whole builtin on
        // Debugger.IsAttached, so debugger_break never stopped a frontend-driven session.)
        if (engine.Debug is Shumway.Embedding.Debugging.DebugService svc)
            svc.RaiseDebuggerBreak(engine);
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
        path = ConsultPipeline.ResolveSourcePath(path);   // SWI-style: consult(algo) → algo.pl
        if (!System.IO.File.Exists(path))
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");

        // Runtime consult: thread the live engine so source-declared dynamic
        // clauses become visible to a later call in the same query.
        host.ConsultFileLive(path, engine);
        return true;
    }

    /// <summary><c>'$timeout_push'(+Seconds)</c> — starts the deadline that
    /// <c>call_with_timeout/2</c> enforces. Seconds may be an integer or a
    /// float; a non-positive one times the goal out immediately, which is what
    /// asking for no time should mean.</summary>
    public static bool TimeoutPush(Activation engine)
    {
        Term arg = MaterializeRegister(engine, 0);
        double seconds = arg switch
        {
            Shumway.Compiler.Ast.IntTerm i => i.Value,
            Shumway.Compiler.Ast.FloatTerm f => f.Value,
            Shumway.Compiler.Ast.VarTerm =>
                throw new Shumway.Core.PrologRuntimeException("instantiation_error"),
            _ => throw new Shumway.Core.PrologRuntimeException(
                "type_error", "number"),
        };
        engine.PushDeadline(seconds);
        return true;
    }

    /// <summary><c>'$timeout_pop'</c> — ends the innermost deadline.</summary>
    public static bool TimeoutPop(Activation engine)
    {
        engine.PopDeadline();
        return true;
    }

    /// <summary><c>ensure_loaded(+File)</c> — ISO §7.4.2.8. Loads File unless
    /// it is already loaded, so a file naming its own dependencies can be
    /// consulted from several places without its clauses being added twice.
    /// Same argument and error contract as <see cref="Consult"/>.</summary>
    public static bool EnsureLoaded(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "ensure_loaded/1 requires the engine to be hosted by a PrologEngine.");

        Cell cell = MaterializeRegisterAsCell(engine, 0);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (cell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException(
                "type_error(atom, _)");

        string path = AtomTable.GetById(cell.AsAtomId)?.Name ?? "";
        // A relative name resolves against the directory of the file being
        // loaded, as `:- include/1` does — `:- ensure_loaded(file_1)` inside
        // dir/main.pl means dir/file_1.pl, not one relative to the CWD.
        string resolved = ConsultPipeline.ResolveSourcePath(path);
        if (!System.IO.File.Exists(resolved)
            && host._consultBaseDir is { } baseDir
            && !System.IO.Path.IsPathRooted(path))
        {
            string rebased = ConsultPipeline.ResolveSourcePath(
                System.IO.Path.Combine(baseDir, path));
            if (System.IO.File.Exists(rebased)) resolved = rebased;
        }
        path = resolved;
        if (!System.IO.File.Exists(path))
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");

        // The whole difference from consult/1.
        if (host.IsLoadedAndUnchanged(path)) return true;

        // Loaded but CHANGED: reloading has to REPLACE what the file defines,
        // not append to it — otherwise the stale clauses stay and the file is
        // effectively loaded twice, which is the one thing this predicate
        // exists to prevent.
        if (host.WasConsulted(path)) host.ReconsultFile(path);
        else host.ConsultFileLive(path, engine);
        return true;
    }

    /// <summary><c>use_module(+Spec)</c> — SWI-style library loader, the goal
    /// (query) form of the <c>:- use_module</c> directive. <c>library(Name)</c>
    /// loads a baked constraint/compatibility library or resolves a
    /// <c>.pl</c>/<c>.shum</c> on the library search path (ADR-038); an atom is a
    /// file to consult. When the loaded module is export-qualified, its whole
    /// export surface is imported into the top-level <c>user</c> module so a
    /// following interactive query can call the imported predicates.</summary>
    public static bool UseModule(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "use_module/1 requires the engine to be hosted by a PrologEngine.");

        Term arg = MaterializeRegister(engine, 0);
        if (arg is Shumway.Compiler.Ast.VarTerm)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (arg is not (Shumway.Compiler.Ast.AtomTerm
            or Shumway.Compiler.Ast.CompoundTerm { Functor: "library", Args.Length: 1 }))
            throw new Shumway.Core.PrologRuntimeException(
                "type_error(atom_or_library, _)");

        // Same resolution as the consult-time directive (file search path,
        // coroutining, compat libraries), returning the loaded export-qualified
        // module name (or null).
        string? src = host.ExecuteUseModuleDirective(arg, throwOnUnresolved: true);
        if (src is not null) host.ImportAllExportsIntoUser(src);
        return true;
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
        path = ConsultPipeline.ResolveSourcePath(path);   // SWI-style: reconsult(algo) → algo.pl
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

    /// <summary>ISO §8.9.1.3 assert-time validation: the head must be an
    /// atom or compound; the body must convert to a goal — a number (or
    /// other non-callable) anywhere in the <c>,</c>/<c>;</c>/<c>-&gt;</c>
    /// control skeleton raises <c>type_error(callable, Culprit)</c>. A var
    /// in goal position is fine (it meta-calls). Without this the bad body
    /// surfaces later as an uncatchable compiler exception at dispatch.</summary>
    private static void ValidateAssertClause(Term clauseTerm)
    {
        if (clauseTerm is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        Term head = clauseTerm;
        Term? body = null;
        if (clauseTerm is CompoundTerm { Functor: ":-", Args.Length: 2 } rule)
        {
            head = rule.Args[0];
            body = rule.Args[1];
        }
        if (head is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (head is not AtomTerm and not CompoundTerm)
            throw new ShumwayPrologException(IsoError.TypeError("callable", head));
        // Control constructs are not procedures — asserting a clause for
        // `!`, `,`/2, `;`/2, `->`/2 or `*->`/2 is modifying a static
        // procedure (WG17 reading; SWI and GNU agree).
        string hn = head is AtomTerm ha ? ha.Name : ((CompoundTerm)head).Functor;
        int harity = head is CompoundTerm hc ? hc.Args.Length : 0;
        if ((harity == 0 && hn == "!")
            || (harity == 1 && hn == ":-")
            || (harity == 2 && hn is "," or ";" or "->" or "*->" or ":-"))
            throw new ShumwayPrologException(IsoError.PermissionError(
                "modify", "static_procedure",
                new CompoundTerm("/", new Term[] { new AtomTerm(hn), new IntTerm(harity) })));
        if (body is not null) ValidateGoalTerm(body);
    }

    private static void ValidateGoalTerm(Term goal)
    {
        switch (goal)
        {
            case VarTerm:
                return;
            case CompoundTerm { Args.Length: 2 } c
                when c.Functor is "," or ";" or "->" or "*->":
                ValidateGoalTerm(c.Args[0]);
                ValidateGoalTerm(c.Args[1]);
                return;
            case AtomTerm or CompoundTerm:
                return;
            default:
                throw new ShumwayPrologException(IsoError.TypeError("callable", goal));
        }
    }

    /// <summary><c>asserta(Clause, -Ref)</c> / <c>assertz(Clause, -Ref)</c> —
    /// the de-facto clause-reference forms. Ref must be UNBOUND
    /// (uninstantiation_error otherwise) and binds to the opaque
    /// <c>'$clause_ref'(Id)</c> for the freshly asserted clause.</summary>
    public static bool AssertaRef(Activation engine) => AssertRefImpl(engine, prepend: true);
    public static bool AssertzRef(Activation engine) => AssertRefImpl(engine, prepend: false);

    private static bool AssertRefImpl(Activation engine, bool prepend)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException("assert/2: PrologEngine host required.");
        Term refArg = MaterializeRegister(engine, 1);
        if (refArg is not VarTerm)
            throw new ShumwayPrologException(IsoError.UninstantiationError(refArg));
        var (fid, clause) = AssertCore(engine, host, prepend);
        long id = host.ClauseRefFor(fid, clause);
        Cell refCell = Materializer.MaterializeAsCell(engine,
            new CompoundTerm("$clause_ref", new Term[] { new IntTerm(id) }));
        return engine.UnifyRegisterWithCell(1, refCell);
    }


    /// <summary><c>'$clause_refs_of'(+Head, -Refs)</c> — the list of
    /// <c>'$clause_ref'(Id)</c> terms for Head's predicate's CURRENT
    /// clauses (call-time snapshot; clause/3's enumeration walks it).</summary>
    public static bool ClauseRefsOf(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException("clause/3: PrologEngine host required.");
        int headHeap = engine.MaterializeRegisterForTrace(0);
        int fid = ReadPatternHeadFunctorId(engine, ref headHeap);
        IReadOnlyList<Clause> clauses = host.DynamicClausesFor(fid);
        Term list = new AtomTerm("[]");
        for (int i = clauses.Count - 1; i >= 0; i--)
        {
            long id = host.ClauseRefFor(fid, clauses[i]);
            list = new CompoundTerm(".", new[]
            {
                (Term)new CompoundTerm("$clause_ref", new Term[] { new IntTerm(id) }),
                list,
            });
        }
        Cell c = Materializer.MaterializeAsCell(engine, list);
        return engine.UnifyRegisterWithCell(1, c);
    }

    /// <summary><c>'$clause_ref_fetch'(+Ref, ?Head, ?Body)</c> — unifies
    /// Head/Body with the clause the reference designates; fails when the
    /// clause was erased/retracted. A bound non-reference is
    /// <c>type_error(db_reference, Culprit)</c>.</summary>
    public static bool ClauseRefFetch(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException("clause/3: PrologEngine host required.");
        Term r = MaterializeRegister(engine, 0);
        if (r is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (r is not CompoundTerm { Functor: "$clause_ref", Args: [IntTerm idT] })
            throw new ShumwayPrologException(IsoError.TypeError("db_reference", r));
        if (!host.TryGetClauseByRef(idT.Value, out _, out Clause clause)) return false;
        Term head = clause.Term is CompoundTerm { Functor: ":-", Args.Length: 2 } rule
            ? rule.Args[0] : clause.Term;
        Term body = clause.Term is CompoundTerm { Functor: ":-", Args.Length: 2 } rule2
            ? rule2.Args[1] : new AtomTerm("true");
        // ONE materialization of the whole (Head :- Body) pair so the
        // clause's variables stay shared between the two unifications.
        Cell pair = Materializer.MaterializeAsCell(engine,
            new CompoundTerm(":-", new[] { head, body }));
        int pairIdx = engine.AllocateHeap(1);
        engine.SetHeap(pairIdx, pair);
        int baseIdx = engine.Deref(pairIdx);
        Cell str = engine.GetHeap(baseIdx);
        if (str.Tag != Tag.Str) return false;
        int args = str.AsHeapIndex + 1;
        int hSlot = engine.AllocateHeap(2);
        engine.SetHeap(hSlot, engine.GetHeap(args));
        engine.SetHeap(hSlot + 1, engine.GetHeap(args + 1));
        return engine.UnifyRegisterWithHeapAt(1, hSlot)
            && engine.UnifyRegisterWithHeapAt(2, hSlot + 1);
    }

    /// <summary><c>'$clause_ref_erase'(+Ref)</c> — removes the referenced
    /// clause (idempotent on a stale reference).</summary>
    public static bool ClauseRefErase(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException("erase/1: PrologEngine host required.");
        Term r = MaterializeRegister(engine, 0);
        if (r is not CompoundTerm { Functor: "$clause_ref", Args: [IntTerm idT] })
            return false;
        if (host.TryGetClauseByRef(idT.Value, out int fid, out Clause clause))
            host.RemoveDynamicByReference(engine, fid, clause);
        return true;
    }

    private static bool AssertImpl(Activation engine, bool prepend)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "assert: PrologEngine host required.");
        AssertCore(engine, host, prepend);
        return true;
    }

    private static (int Fid, Clause Clause) AssertCore(
        Activation engine, PrologEngine host, bool prepend)
    {
        Term clauseTerm = MaterializeRegister(engine, 0);
        clauseTerm = StripAssertQualifiers(clauseTerm);
        ValidateAssertClause(clauseTerm);
        var clause = Shumway.Compiler.Ast.Clause.From(clauseTerm);
        // Asserta/Assertz extract the head functor id anyway —
        // take it from the return instead of re-extracting (a second
        // string intern per assert).
        int fid = prepend ? host.Asserta(clause) : host.Assertz(clause);
        // ADR-015 incremental dispatch — the canonical path:
        //   assertz → append a chunk and patch the tail's <next>.
        //   asserta → append a chunk, patch the trampoline's execute,
        //             and demote the old head's try_me_else in place to
        //             retry_me_else + 4 nops (same 9-byte footprint).
        if (prepend)
            host.PrependDynamicClauseIncremental(engine, fid, clause);
        else
            host.AppendDynamicClauseIncremental(engine, fid, clause);
        return (fid, clause);
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
        int fid = ReadPatternHeadFunctorId(engine, ref headHeap);
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
        int patternFid = ReadPatternHeadFunctorId(engine, ref patternHeap);

        // ISO §7.12.2.h — retracting from a static
        // predicate is permission_error(modify, static_procedure, _),
        // not a silent failure. The check fires after the head's type
        // check (above) so type errors win precedence.
        // Same triage as retractall: dynamic → run the retract loop;
        // static/builtin → permission_error (thrown by the check);
        // UNDEFINED → plain failure, not an error.
        if (!host.IsRetractAllModifiable(patternFid)) return false;

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
        // §7.6.2 — the stored clause is the CONVERTED one, so the pattern has
        // to be converted the same way or `retract((p(X) :- X, call(X)))` would
        // stop matching what `assertz` of the same text just stored. A body
        // that is a bare VARIABLE stays as it is: it is the pattern's wildcard
        // (`retract((p(_) :- Body))`), not a goal to convert. GNU and SWI both
        // behave exactly this way.
        patternHeap = ConvertRetractPattern(engine, patternHeap);

        return RetractStep(engine, host, patternFid, candidates, returnPc,
            patternHeap);
    }

    /// <summary>Rebuilds the retract pattern with §7.6.2 applied to its body,
    /// sharing every leaf with the original so bindings still reach the
    /// caller. Returns <paramref name="patternHeap"/> unchanged when the body
    /// needs no conversion, which is the overwhelmingly common case.</summary>
    private static int ConvertRetractPattern(Activation engine, int patternHeap)
    {
        Cell pat = engine.GetHeap(engine.Deref(patternHeap));
        if (pat.Tag != Tag.Str) return patternHeap;
        int sa = pat.AsHeapIndex;
        Cell f = engine.GetHeap(sa);
        if (f.Tag != Tag.Functor) return patternHeap;
        var (atomId, arity) = FunctorTable.Lookup(f.AsFunctorId);
        if (arity != 2 || AtomTable.GetById(atomId)?.Name != ":-") return patternHeap;

        Cell body = ResolveLocal(engine, engine.GetHeap(sa + 1));
        Cell converted = ConvertBodyCell(engine, body, topLevel: true);
        if (converted.Equals(body)) return patternHeap;

        int rebuilt = engine.AllocateHeap(3);
        engine.SetHeap(rebuilt, f);
        engine.SetHeap(rebuilt + 1, engine.GetHeap(sa + 1));
        engine.SetHeap(rebuilt + 2, converted);
        int home = engine.AllocateHeap(1);
        engine.SetHeap(home, Cell.Str(rebuilt));
        return home;
    }

    /// <summary>§7.6.2 on a body already on the heap: a variable goal becomes
    /// <c>call(V)</c> (sharing V), and the control skeleton `,` / `;` / `-&gt;`
    /// is descended. Everything else — and a top-level variable when
    /// <paramref name="topLevel"/> — is returned as is.</summary>
    private static Cell ConvertBodyCell(Activation engine, Cell body, bool topLevel)
    {
        if (body.Tag is Tag.Ref or Tag.AttVar)
        {
            if (topLevel) return body;
            int callBase = engine.AllocateHeap(2);
            engine.SetHeap(callBase, Cell.Functor(CallOneFunctorId));
            engine.SetHeap(callBase + 1, body);
            return Cell.Str(callBase);
        }
        if (body.Tag != Tag.Str) return body;
        int sa = body.AsHeapIndex;
        Cell f = engine.GetHeap(sa);
        if (f.Tag != Tag.Functor) return body;
        var (atomId, arity) = FunctorTable.Lookup(f.AsFunctorId);
        if (arity != 2) return body;
        if (AtomTable.GetById(atomId)?.Name is not ("," or ";" or "->")) return body;

        Cell l = ResolveLocal(engine, engine.GetHeap(sa + 1));
        Cell r = ResolveLocal(engine, engine.GetHeap(sa + 2));
        Cell nl = ConvertBodyCell(engine, l, topLevel: false);
        Cell nr = ConvertBodyCell(engine, r, topLevel: false);
        if (nl.Equals(l) && nr.Equals(r)) return body;
        int b = engine.AllocateHeap(3);
        engine.SetHeap(b, f);
        engine.SetHeap(b + 1, nl);
        engine.SetHeap(b + 2, nr);
        return Cell.Str(b);
    }

    private static readonly int CallOneFunctorId =
        FunctorTable.Intern(AtomTable.Intern("call", permanent: true).Id, 1);

    /// <summary>Reads the pattern's head functor id straight from the
    /// heap. Mirrors the ISO callability check in
    /// <see cref="ExtractHeadFunctorIdFromClause"/> but avoids the
    /// Term AST allocation — for retract's hot path the heap shape
    /// is sufficient.</summary>
    /// <summary>Peels <c>Module:</c> qualifiers off an assert argument.
    /// Dynamics are flat-global (invariant), so the qualifier validates and
    /// drops — <c>assertz(m:f(1))</c>, <c>assertz(m:(H :- B))</c> and the
    /// head-qualified <c>assertz((m:H :- B))</c> all assert the bare clause.
    /// Nested qualifiers peel through (innermost wins, vacuously — they all
    /// drop). A variable module is an instantiation_error, a non-atom a
    /// type_error(atom) — checked before the drop.</summary>
    private static Term StripAssertQualifiers(Term t)
    {
        t = StripColonChain(t);
        if (t is CompoundTerm { Functor: ":-", Args.Length: 2 } rule
            && rule.Args[0] is CompoundTerm { Functor: ":", Args.Length: 2 })
        {
            Term head = StripColonChain(rule.Args[0]);
            t = new CompoundTerm(":-", new[] { head, rule.Args[1] })
                { Position = rule.Position };
        }
        return t;
    }

    private static Term StripColonChain(Term t)
    {
        while (t is CompoundTerm { Functor: ":", Args.Length: 2 } q)
        {
            switch (q.Args[0])
            {
                case AtomTerm: break;
                case VarTerm:
                    throw new Shumway.Core.PrologRuntimeException("instantiation_error");
                default:
                    throw new Shumway.Core.PrologRuntimeException("type_error", "atom");
            }
            t = q.Args[1];
        }
        return t;
    }

    private static readonly int _colonFunctorAtomId =
        AtomTable.Intern(":", permanent: true).Id;

    /// <summary>Reads the pattern's head functor id, peeling any top-level
    /// <c>Module:</c> qualifier chain IN PLACE first: dynamics are
    /// flat-global, so the qualifier validates and drops, and
    /// <paramref name="patternHeap"/> moves to the inner subterm ITSELF —
    /// the caller's variables keep their identity (a re-materialized copy
    /// would silently stop binding them). Covers <c>retract(m:Head)</c> and
    /// <c>retract(m:(H :- B))</c>; the head-qualified rule spelling stays on
    /// the normal path (its ':'/2 head is not a dynamic predicate —
    /// permission_error, as for any non-dynamic). The unqualified fast path
    /// pays ONE extra int compare on the functor lookup it already did.</summary>
    private static int ReadPatternHeadFunctorId(Activation engine, ref int patternHeap)
    {
        int idx = engine.Deref(patternHeap);
        Cell c = engine.GetHeap(idx);
        while (c.Tag == Tag.Str)
        {
            int sa = c.AsHeapIndex;
            Cell f = engine.GetHeap(sa);
            if (f.Tag != Tag.Functor) break;
            int fid = f.AsFunctorId;
            var (atomId, arity) = FunctorTable.Lookup(fid);
            if (arity == 2 && atomId == _colonFunctorAtomId)
            {
                int mIdx = engine.Deref(sa + 1);
                Cell mc = engine.GetHeap(mIdx);
                if (mc.Tag == Tag.Ref || mc.Tag == Tag.AttVar)
                    throw new Shumway.Core.PrologRuntimeException("instantiation_error");
                if (mc.Tag != Tag.Atom)
                    throw new Shumway.Core.PrologRuntimeException("type_error", "atom");
                patternHeap = idx = engine.Deref(sa + 2);
                c = engine.GetHeap(idx);
                continue;
            }
            if (arity == 2 && atomId == _ruleFunctorAtomId)
            {
                // retract((Head :- Body)) — descend into the Head slot.
                int headIdx = engine.Deref(sa + 1);
                Cell hc = engine.GetHeap(headIdx);
                return ReadFunctorIdFromCell(engine, hc);
            }
            return fid;
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
        // Non-callable pattern (a number, a string): the cell rides along so
        // the translated type_error(callable, Culprit) carries the value.
        throw new Shumway.Core.PrologRuntimeException("type_error", "callable", engine, c);
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
        Cell candidateCell = Materializer.MaterializeAsCell(engine,
            RuleFormCandidate(candidate.Term, IsRuleFormPattern(engine, patternHeap)));
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
            Cell candidateCell = Materializer.MaterializeAsCell(engine,
                RuleFormCandidate(candidate.Term, IsRuleFormPattern(engine, patternHeap)));
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
    /// <summary>True when the retract pattern on the heap is the rule form
    /// <c>(Head :- Body)</c>. A rule-form pattern must also match stored
    /// FACTS — ISO treats a fact as <c>(Head :- true)</c> — so candidates
    /// are normalized with <see cref="RuleFormCandidate"/> before the trial
    /// unification.</summary>
    private static bool IsRuleFormPattern(Activation engine, int patternHeap)
    {
        int idx = engine.Deref(patternHeap);
        Cell c = engine.GetHeap(idx);
        if (c.Tag != Tag.Str) return false;
        Cell f = engine.GetHeap(c.AsHeapIndex);
        if (f.Tag != Tag.Functor) return false;
        var (aid, ar) = FunctorTable.Lookup(f.AsFunctorId);
        return ar == 2 && aid == _ruleFunctorAtomId;
    }

    private static Term RuleFormCandidate(Term t, bool ruleForm)
        => ruleForm && t is not CompoundTerm { Functor: ":-", Args.Length: 2 }
            ? new CompoundTerm(":-", new[] { t, new AtomTerm("true") })
            : t;

    private static int FindRetractMatch(
        Activation engine, IReadOnlyList<Clause> candidates, int startIndex,
        int endExclusive, int patternHeap)
    {
        // endExclusive bounds the scan explicitly — a resume's
        // candidates live in a pooled buffer that may be longer than the
        // snapshot it holds, so candidates.Count is not the right bound.
        bool ruleForm = IsRuleFormPattern(engine, patternHeap);
        for (int i = startIndex; i < endExclusive; i++)
        {
            Term candTerm = RuleFormCandidate(candidates[i].Term, ruleForm);
            if (DefiniteMismatch(engine, patternHeap, candTerm, depth: 4))
                continue;
            int savedHeapTop = engine.HeapTop;
            int savedBindingTrail = engine.BindingTrailTop;
            int savedExtraTrail = engine.ExtraTrailTop;
            int savedHb = engine.Hb;
            engine.SetHb(engine.HeapTop);

            Cell candidateCell =
                Materializer.MaterializeAsCell(engine, candTerm);
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
        // Distinct attributed variables reachable from the term at X[0] —
        // TRANSITIVELY: an attribute value can reference further attributed
        // variables (a clpz propagator's partner variable, tuples_in's
        // relation variable carrying clpz_relation), and projecting a hook
        // over the copy needs THEIR copied attributes too. The list is a
        // worklist: scanning a value may append more variables.
        var attvars = new System.Collections.Generic.List<int>();
        var seen = new System.Collections.Generic.HashSet<int>();
        var seenStructs = new System.Collections.Generic.HashSet<int>();
        CollectAttvars(engine, engine.GetRegister(0), attvars, seen, seenStructs);

        Term original = MaterializeRegister(engine, 0);

        var infos = new System.Collections.Generic.List<Term>();
        for (int i = 0; i < attvars.Count; i++)
        {
            int vAddr = attvars[i];
            // The same _G<addr> name TermReader.Materialize gives this
            // attributed variable, so the shared-var-map join lands it on
            // the copy's variable.
            var vVar = new VarTerm("_G" + vAddr);
            foreach (int moduleId in engine.AttrModules(vAddr))
            {
                int attrValueIdx = engine.GetAttr(vAddr, moduleId);
                CollectAttvars(engine, Cell.Ref(attrValueIdx), attvars, seen, seenStructs);
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
    /// variables reachable from <paramref name="cell"/>.
    /// <paramref name="seenVars"/> deduplicates the variables;
    /// <paramref name="seenStructs"/> guards against a cyclic term looping.
    ///
    /// <para>The two sets must stay SEPARATE. An unbound variable inside a
    /// list or structure lives in the argument cell itself, so once it gains
    /// an attribute the attvar's address IS the compound's — sharing one set
    /// makes the compound's own visited-mark swallow the variable. That is
    /// the whole of <c>Qs ins 1..N</c> projecting no domains at all.</para>
    ///
    /// <para>Internal: the debugger's attvar transplant walks a SUSPENDED
    /// activation with the same collector.</para></summary>
    internal static void CollectAttvars(Activation engine, Cell cell,
        System.Collections.Generic.List<int> addrs,
        System.Collections.Generic.HashSet<int> seenVars,
        System.Collections.Generic.HashSet<int> seenStructs)
    {
        // ITERATIVE over the list spine. Recursing once per element cost one C#
        // frame per element: a thousand-element list overflowed the stack in a
        // browser (where it is small) and a hundred thousand did on the desktop.
        // A list continues the loop; only a nested compound goes on the work
        // list, so the memory used tracks the term's SHAPE, not its length.
        System.Collections.Generic.Stack<Cell>? pending = null;
        while (true)
        {
            if (cell.Tag == Tag.Ref)
                cell = engine.GetHeap(engine.Deref(cell.AsHeapIndex));
            switch (cell.Tag)
            {
                case Tag.AttVar:
                    int va = cell.AsHeapIndex;
                    if (seenVars.Add(va)) addrs.Add(va);
                    break;
                case Tag.Str:
                    int fIdx = cell.AsHeapIndex;
                    if (!seenStructs.Add(fIdx)) break;
                    var (_, arity) = FunctorTable.Lookup(engine.GetHeap(fIdx).AsFunctorId);
                    // Pushed last-to-first so they come off in argument order:
                    // the collection order is what the answer displays.
                    for (int i = arity - 1; i >= 0; i--)
                        Visit(engine.GetHeap(fIdx + 1 + i));
                    break;
                case Tag.Lis:
                    int h = cell.AsHeapIndex;
                    if (!seenStructs.Add(h)) break;
                    Visit(engine.GetHeap(h + 1));        // the rest, for later
                    cell = engine.GetHeap(h);            // this element, now
                    continue;
            }
            if (pending is null || pending.Count == 0) return;
            cell = pending.Pop();
        }

        // Only what can hold an attributed variable is worth remembering.
        void Visit(Cell c)
        {
            if (c.Tag is Tag.Ref or Tag.AttVar or Tag.Str or Tag.Lis)
                (pending ??= new System.Collections.Generic.Stack<Cell>()).Push(c);
        }
    }

    /// <summary><c>'$dbg_fix_foreign'(+Term)</c> — ADR-035 attvar transplant support.
    /// A transplanted attribute value travels to the evaluation activation as compiled
    /// term-building code, where a FOREIGN payload (clpfd's native domain object) can
    /// only arrive as its <c>'$foreign'(N)</c> round-trip form — and N indexes the
    /// SUSPENDED activation's per-activation foreign table. This walks the term IN
    /// PLACE on the evaluation activation, re-registers each such object here
    /// (<see cref="Activation.MakeForeign"/>) and overwrites the compound's referring
    /// cell with the real FOREIGN cell. The source activation is read through
    /// <see cref="PrologEngine.DebugTransplantSource"/>; with none set the term is left
    /// alone (and a native consumer will say so loudly).</summary>
    public static bool DbgFixForeign(Activation engine)
    {
        if (engine.Host is not PrologEngine host
            || host.DebugTransplantSource is not { } source)
            return true;

        int foreignFid = FunctorTable.Intern(
            AtomTable.Intern("$foreign", permanent: true).Id, 1);
        var seen = new System.Collections.Generic.HashSet<int>();

        void FixAt(int addr)
        {
            if (!seen.Add(addr)) return;
            Cell c = engine.GetHeap(addr);
            if (c.Tag == Tag.Ref)
            {
                int d = engine.Deref(c.AsHeapIndex);
                if (d != addr) FixAt(d);
                return;
            }
            switch (c.Tag)
            {
                case Tag.Str:
                {
                    int fIdx = c.AsHeapIndex;
                    int fid = engine.GetHeap(fIdx).AsFunctorId;
                    if (fid == foreignFid)
                    {
                        Cell arg = engine.GetHeap(fIdx + 1);
                        if (arg.Tag == Tag.Ref)
                            arg = engine.GetHeap(engine.Deref(arg.AsHeapIndex));
                        if (arg.Tag == Tag.Int)
                        {
                            object? obj = source.ForeignById((int)arg.AsInt);
                            engine.SetHeap(addr, engine.MakeForeign(obj));
                        }
                        return;
                    }
                    var (_, arity) = FunctorTable.Lookup(fid);
                    for (int i = 0; i < arity; i++) FixAt(fIdx + 1 + i);
                    break;
                }
                case Tag.Lis:
                {
                    int h = c.AsHeapIndex;
                    FixAt(h);
                    FixAt(h + 1);
                    break;
                }
            }
        }

        Cell start = engine.GetRegister(0);
        if (start.Tag == Tag.Ref) FixAt(engine.Deref(start.AsHeapIndex));
        else if (start.Tag is Tag.Str or Tag.Lis)
        {
            // A register value has no address of its own; walk its children.
            if (start.Tag == Tag.Str)
            {
                int fIdx = start.AsHeapIndex;
                var (_, arity) = FunctorTable.Lookup(engine.GetHeap(fIdx).AsFunctorId);
                for (int i = 0; i < arity; i++) FixAt(fIdx + 1 + i);
            }
            else
            {
                FixAt(start.AsHeapIndex);
                FixAt(start.AsHeapIndex + 1);
            }
        }
        return true;
    }

    /// <summary><c>term_attvars(+Term, -Vars)</c> — unifies <c>Vars</c> with
    /// the list of the distinct attributed variables reachable from
    /// <c>Term</c>, first-occurrence order. The list holds the REAL
    /// variables (references to their heap cells), not copies — binding one
    /// of them fires its hooks.</summary>
    public static bool TermAttvars(Activation engine)
    {
        var attvars = new System.Collections.Generic.List<int>();
        var seen = new System.Collections.Generic.HashSet<int>();
        CollectAttvars(engine, engine.GetRegister(0), attvars, seen,
            new System.Collections.Generic.HashSet<int>());
        return engine.UnifyRegisterWithHeapAt(1, BuildRefList(engine, attvars));
    }

    /// <summary><c>'$dif_check'(X, Y, Out)</c> — the C# core of
    /// <c>dif/2</c>. Trial-unifies X and Y (fully rolled back, queued
    /// hook wakeups included):
    /// not unifiable → <c>Out = none</c> (the disequality holds forever);
    /// unifiable with no bindings → the terms are identical, FAIL;
    /// unifiable via bindings → <c>Out</c> is the list of the real
    /// variables the trial bound — the suspension points the library
    /// watches to re-check the disequality.</summary>
    public static bool DifCheck(Activation engine)
    {
        if (!engine.TrialUnifyCollectingBoundVars(0, 1, out var boundVars))
            return engine.UnifyRegisterWithCell(2,
                Cell.Atom(AtomTable.Intern("none", permanent: true).Id));
        if (boundVars.Count == 0) return false;
        return engine.UnifyRegisterWithHeapAt(2, BuildRefList(engine, boundVars));
    }

    /// <summary><c>?=(X, Y)</c> — succeeds iff the (in)equality of X and Y is
    /// already DECIDED: they are identical, or they cannot unify. Further
    /// instantiation cannot change the outcome. (SWI/SICStus §; the condition
    /// <c>when/2</c> waits on.) Implemented as a fully-rolled-back trial
    /// unification: cannot unify → decided; unifiable with no bindings → they
    /// are identical → decided; unifiable via bindings → undecided → fail.</summary>
    public static bool DecidedUnify(Activation engine)
    {
        if (!engine.BeginTrialUnify(0, 1, out var boundVars, out var scope))
            return true;   // cannot unify → the inequality is decided
        engine.EndTrialUnify(scope);
        return boundVars.Count == 0;   // no bindings → already identical
    }

    /// <summary><c>unifiable(X, Y, Unifier)</c> — if X and Y can unify,
    /// <c>Unifier</c> is the list of <c>V = Value</c> bindings that would make
    /// them equal (the original variables of X/Y preserved); fails when they
    /// cannot unify. No binding is left behind. (SWI builtin.)</summary>
    public static bool Unifiable(Activation engine)
    {
        if (!engine.BeginTrialUnify(0, 1, out var boundVars, out var scope))
            return false;   // cannot unify → the predicate fails

        // While the trial bindings are live, snapshot each `V = Value` pair as
        // a managed AST. TermReader.Materialize names every unbound variable
        // `_G<addr>` — so the value side and the `V` side (which is exactly the
        // bound variable, named the same way) refer to the same original
        // variables. Every such addr is a pre-existing variable (< the scope's
        // heap top): unification binds variables to existing cells, it never
        // introduces fresh unbound ones.
        var pairs = new System.Collections.Generic.List<Term>(boundVars.Count);
        foreach (int a in boundVars)
            pairs.Add(new CompoundTerm("=", new Term[]
            {
                new VarTerm("_G" + a),
                TermReader.Materialize(engine, a),
            }));

        engine.EndTrialUnify(scope);

        // Rebuild the unifier on the heap, mapping each `_G<addr>` name back to
        // the original variable at that address (still unbound post-rollback).
        Term listAst = MakeListTerm(pairs);
        var shared = new System.Collections.Generic.Dictionary<string, int>();
        CollectOriginalVarNames(listAst, shared);
        Cell listCell = Materializer.MaterializeAsCellSharing(engine, listAst, shared);
        return engine.UnifyRegisterWithCell(2, listCell);
    }

    /// <summary>Seeds <paramref name="shared"/> with every <c>_G&lt;addr&gt;</c>
    /// variable name in <paramref name="term"/> mapped to its heap address, so
    /// <see cref="Materializer.MaterializeAsCellSharing"/> reuses the original
    /// variable cells rather than allocating fresh ones.</summary>
    private static void CollectOriginalVarNames(Term term,
        System.Collections.Generic.Dictionary<string, int> shared)
    {
        switch (term)
        {
            case VarTerm v:
                if (v.Name.Length > 2 && v.Name[0] == '_' && v.Name[1] == 'G'
                    && int.TryParse(v.Name.AsSpan(2), out int addr))
                    shared[v.Name] = addr;
                break;
            case CompoundTerm c:
                foreach (Term arg in c.Args) CollectOriginalVarNames(arg, shared);
                break;
        }
    }

    /// <summary><c>'$attv_snapshot'(-S)</c> — S is an opaque snapshot of the
    /// set of attributed-variable homes known to the engine right now. The
    /// C# half of <c>call_residue_vars/2</c>, paired with
    /// <c>'$attv_new_since'/2</c>.</summary>
    public static bool AttvSnapshot(Activation engine)
    {
        var set = new System.Collections.Generic.HashSet<int>(
            engine.AttrTableKeysSnapshot());
        return engine.UnifyRegisterWithCell(0, engine.MakeForeign(set));
    }

    /// <summary><c>'$attv_new_since'(+S, -Vars)</c> — Vars is the list of
    /// the variables that gained attributes after snapshot <c>S</c> was
    /// taken and are still unbound attributed variables now.</summary>
    public static bool AttvNewSince(Activation engine)
    {
        var snapshot = engine.AsForeign<System.Collections.Generic.HashSet<int>>(
                engine.GetRegister(0) is { Tag: Tag.Ref } r
                    ? engine.GetHeap(engine.Deref(r.AsHeapIndex))
                    : engine.GetRegister(0))
            ?? throw new Shumway.Core.PrologRuntimeException("type_error", "attv_snapshot");
        var fresh = new System.Collections.Generic.List<int>();
        foreach (int addr in engine.AttrTableKeysSnapshot())
            if (!snapshot.Contains(addr) && engine.IsAttVarAt(addr))
                fresh.Add(addr);
        fresh.Sort();   // stable first-created-first order (heap addresses grow)
        return engine.UnifyRegisterWithHeapAt(1, BuildRefList(engine, fresh));
    }

    /// <summary>Builds a proper heap list whose elements are references to
    /// the given heap addresses (the real cells — unbound/attributed
    /// variables stay themselves) and returns the heap index of its
    /// head.</summary>
    private static int BuildRefList(Activation engine,
        System.Collections.Generic.List<int> addrs)
    {
        int n = addrs.Count;
        if (n == 0)
        {
            int e = engine.AllocateHeap(1);
            engine.SetHeap(e, Cell.Atom(AtomTable.EmptyListId));
            return e;
        }
        int start = engine.AllocateHeap(2 * n + 1);
        for (int i = 0; i < n; i++)
        {
            int lisIdx = start + 2 * i;
            engine.SetHeap(lisIdx, Cell.Lis(lisIdx + 1));
            engine.SetHeap(lisIdx + 1, Cell.Ref(addrs[i]));
        }
        engine.SetHeap(start + 2 * n, Cell.Atom(AtomTable.EmptyListId));
        return start;
    }

    /// <summary>Builds a proper-list AST term from the given items.</summary>
    private static Term MakeListTerm(System.Collections.Generic.List<Term> items)
    {
        Term list = new AtomTerm("[]");
        for (int i = items.Count - 1; i >= 0; i--)
            list = new CompoundTerm(".", new[] { items[i], list });
        return list;
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

    /// <summary><c>'$te_after'(HookIndex)</c> — the in-file term_expansion order
    /// guard. An in-file hook's clause is committed as
    /// <c>Head :- '$te_after'(HookIndex), Body</c>. It succeeds when
    /// <see cref="PrologEngine._consultExpandPos"/> is -1 (any consult other than
    /// the one that defined the hook — the hook always applies then) or greater
    /// than HookIndex (during that consult's re-expansion pass — the hook applies
    /// only to clauses AFTER its own definition, matching SWI/Scryer).</summary>
    public static bool TeAfter(Activation engine)
    {
        if (engine.Host is not PrologEngine host || host._consultExpandPos < 0)
            return true;
        return MaterializeRegister(engine, 0) is IntTerm n
            && host._consultExpandPos > (int)n.Value;
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

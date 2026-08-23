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
    /// <summary>Loads the CLP(FD) constraint library into this
    /// engine, making the finite-domain constraints — <c>#=</c>, <c>#\=</c>,
    /// <c>#&lt;</c>, <c>#&gt;</c>, <c>#=&lt;</c>, <c>#&gt;=</c>, <c>in</c>,
    /// <c>ins</c> — and their operators available to subsequently consulted
    /// source and queries. CLP(FD) is opt-in: an engine that never calls
    /// this carries none of the library's weight.</summary>
    public void UseClpfd()
    {
        ConsultString(Clpfd.Source);
        MarkModuleNonDebuggable(Clpfd.ModuleName);   // ADR-035 — a library, not the user's code
    }

    /// <summary>Loads the CLP(R) constraint library into this
    /// engine, making linear-equality constraints over the reals available
    /// through the <c>{Constraint}</c> wrapper. CLP(R) is opt-in: an engine
    /// that never calls this carries none of the library's weight.
    ///
    /// <para>CLP(R) and CLP(FD) can share an engine — both declare their
    /// <c>verify_attributes/4</c> hook <c>:- multifile</c> — as long as no
    /// variable carries both libraries' constraints.</para></summary>
    public void UseClpr()
    {
        ConsultString(Clpr.Source);
        MarkModuleNonDebuggable(Clpr.ModuleName);   // ADR-035 — a library, not the user's code
    }

    /// <summary>Loads the coroutining library into this engine:
    /// <c>freeze/2</c>, <c>frozen/2</c> and the <c>dif/2</c> disequality
    /// constraint. Opt-in like the CLP libraries, and built on the same
    /// multifile <c>verify_attributes/4</c> hook, so it coexists with
    /// CLP(FD)/CLP(R) on one engine.</summary>
    private bool _coroutiningLoaded;
    public void UseCoroutining()
    {
        if (_coroutiningLoaded) return;   // idempotent — re-consult would trip public uniqueness
        _coroutiningLoaded = true;
        ConsultString(Coroutining.Source);
        MarkModuleNonDebuggable(Coroutining.ModuleName);   // ADR-035 — a library, not the user's code
    }

    // Compatibility libraries loaded on demand by use_module(library(Name)),
    // tracked so a repeated import (or a program that imports the same library
    // as one of its dependencies) does not re-consult and trip the
    // public-predicate uniqueness check.
    private readonly HashSet<string> _loadedCompatLibraries = new();

    /// <summary>Loads a built-in Scryer/Trealla compatibility library by name
    /// (see <see cref="CompatLibraries"/>), idempotently. Returns <c>true</c>
    /// if <paramref name="name"/> is a known compatibility library (whether it
    /// carries Prolog source or is a prelude-covered no-op), <c>false</c> for
    /// an unknown library name.</summary>
    // ADR-040 — the preferred dialect for resolving an ambiguous library name.
    // null = auto (no preference); coexistence still works because the registry
    // falls back to every pack, so a name unique to one dialect always resolves.
    private string? _activeLibraryDialect;

    /// <summary>Selects the preferred dialect (<c>scryer</c>, <c>swi</c>, …) for
    /// resolving a <c>use_module(library(X))</c> whose name two dialects both
    /// provide (ADR-040 explicit selection). Does NOT restrict loading: a library
    /// unique to another dialect still resolves (coexistence is the default), so
    /// a Scryer <c>clpz</c> and an SWI <c>http</c> load together regardless. Also
    /// settable from Prolog with <c>set_prolog_flag(library_dialect, swi)</c>.</summary>
    public void SetLibraryDialect(string dialect)
    {
        if (!DialectRegistry.IsKnownDialect(dialect))
            throw new System.ArgumentException($"unknown library dialect '{dialect}'");
        _activeLibraryDialect = dialect;
    }

    /// <summary>The active library dialect, or null for auto. Read by the
    /// <c>library_dialect</c> prolog flag.</summary>
    internal string? ActiveLibraryDialect => _activeLibraryDialect;

    // ADR-040 D5.2 — a search directory tagged with the dialect its libraries
    // are written in. Resolving library(X) from a tagged dir loads X (and its
    // pack-resolved dependency subtree) in that dialect, parsed with the
    // dialect's double_quotes. Keyed by normalised full directory path.
    private System.Collections.Generic.Dictionary<string, string>? _libraryDirDialect;

    /// <summary>Adds <paramref name="path"/> to the library search path AND tags
    /// it with a dialect (ADR-040 D5.2): a <c>use_module(library(X))</c> that
    /// resolves <c>X</c> from here loads it in <paramref name="dialect"/> — the
    /// dir's dialect becomes active (name resolution + double_quotes) for that
    /// load. Pointing <c>-L</c> at a Scryer checkout as <c>scryer</c> and an SWI
    /// one as <c>swi</c> lets both systems' libraries load, each correctly.</summary>
    public void AddLibraryDirectory(string path, string dialect)
    {
        if (!DialectRegistry.IsKnownDialect(dialect))
            throw new System.ArgumentException($"unknown library dialect '{dialect}'");
        AddLibraryDirectory(path);
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
        (_libraryDirDialect ??= new(System.StringComparer.OrdinalIgnoreCase))[full] = dialect;
        // Trealla surfaces BUILTIN-level names (limit/2, load_text/2) that its
        // programs use without importing anything, so mounting a trealla tree
        // loads the (tiny) shim eagerly — the lazy WithDialect trigger only
        // fires when a library file actually loads. The scryer/swi shims stay
        // lazy: their names all arrive via imports.
        if (dialect == TreallaShim.LibraryName) EnsureTreallaShim();
    }

    // The dialect a resolved library path belongs to (its directory's tag), or
    // null when no ancestor directory is tagged. Walks up so a SUBDIRECTORY
    // library (library(dcg/basics) → <tagged>/dcg/basics.pl) inherits the
    // tagged root's dialect.
    private string? DialectForResolvedPath(string resolvedPath)
    {
        if (_libraryDirDialect is null) return null;
        string? dir = System.IO.Path.GetDirectoryName(resolvedPath);
        while (dir is not null)
        {
            string full = dir;
            try { full = System.IO.Path.GetFullPath(dir); } catch { /* use as-is */ }
            if (_libraryDirDialect.TryGetValue(full, out string? d)) return d;
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ADR-040 — true once any module with a non-null dialect has been loaded. The
    // fast exit for an all-Shumway engine: the dialect-sensitive builtin path is a
    // no-op, so a strict program keeps ISO behaviour at zero cost.
    private bool _anyDialectedModule;
    internal void NoteDialectedModule() => _anyDialectedModule = true;

    // Sorted snapshot of the current query's predicate addresses, rebuilt when
    // the address map changes (once per query setup). Binary-searched to map a
    // code address back to its predicate.
    private int[]? _sortedPredAddrs;
    private object? _sortedPredAddrsFor;

    /// <summary>ADR-040 — the source dialect of the nearest caller (the running
    /// goal, else an ancestor on the call-return chain) that lives in a dialected
    /// module, or null. Only meaningful cost when a dialect-sensitive builtin is
    /// on its would-raise path.</summary>
    internal string? CallerDialect(Activation engine)
    {
        if (!_anyDialectedModule || _currentPredicatesByAddress is null) return null;
        string? d = DialectAtAddress(engine.P);
        if (d is not null) return d;
        foreach (int addr in engine.EnumerateCallReturnAddresses())
        {
            d = DialectAtAddress(addr);
            if (d is not null) return d;
        }
        return null;
    }

    private string? DialectAtAddress(int addr)
    {
        var map = _currentPredicatesByAddress;
        if (map is null) return null;
        if (!ReferenceEquals(_sortedPredAddrsFor, map))
        {
            _sortedPredAddrs = System.Linq.Enumerable.ToArray(map.Keys);
            System.Array.Sort(_sortedPredAddrs);
            _sortedPredAddrsFor = map;
        }
        int[] keys = _sortedPredAddrs!;
        int idx = System.Array.BinarySearch(keys, addr);
        if (idx < 0) idx = ~idx - 1;   // nearest entry at or below addr
        if (idx < 0) return null;
        int fid = map[keys[idx]].FunctorId;
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(fid);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        int dollar = name.IndexOf('$');
        if (dollar <= 0) return null;   // bare-global (not Module$name-mangled)
        string module = name.Substring(0, dollar);
        return _modules.TryGetValue(module, out var m) ? m.Dialect : null;
    }

    /// <summary>Whether the caller's module was loaded as <paramref name="dialect"/>.
    /// Used by dialect-sensitive builtins (<see cref="Shumway.Builtins.IDialectAwareHost"/>).</summary>
    public bool CallerModuleHasDialect(Activation engine, string dialect)
        => CallerDialect(engine) == dialect;

    // Runs <paramref name="body"/> with <paramref name="dialect"/> active — its
    // name resolution preferred and its double_quotes in force — restoring both
    // after. The subtree a library pulls in inherits the dialect for the load.
    /// <summary>Runs <paramref name="body"/> as if loading a library of
    /// <paramref name="dialect"/>: its <c>double_quotes</c>, its parsing
    /// leniencies and its operators apply for the duration and are restored
    /// after (ADR-040). Consulting the SOURCE of a Scryer or SWI library is the
    /// case this exists for outside the loader — the text means what its own
    /// system says it means, and reading it as ISO gets it wrong.
    ///
    /// <para>An unknown or empty dialect runs <paramref name="body"/> plainly,
    /// so a caller need not check first.</para></summary>
    public T WithLibraryDialect<T>(string? dialect, System.Func<T> body)
        // The ! is for net48, whose IsNullOrEmpty lacks the NotNullWhen flow
        // annotation the modern compiler reasons from.
        => string.IsNullOrEmpty(dialect) || !DialectRegistry.IsKnownDialect(dialect!)
            ? body()
            : WithDialect(dialect!, body);

    private T WithDialect<T>(string dialect, System.Func<T> body)
    {
        string? savedDialect = _activeLibraryDialect;
        var savedDq = Flags.DoubleQuotes;
        bool savedSep = Flags.DigitSeparators;
        _activeLibraryDialect = dialect;
        Flags.DoubleQuotes = DialectRegistry.DoubleQuotesOf(dialect);
        // SWI sources use digit-group separators (10_000) and bare operator
        // atoms in operand positions (`… as volatile`); scoped to the load so
        // ISO strictness holds everywhere else.
        bool savedLenientOps = Flags.LenientBareOperatorOperands;
        bool savedLenientQuote = Flags.LenientQuoteCharLiteral;
        bool savedLenientArgs = Flags.LenientArgumentPriority;
        bool savedLenientEsc = Flags.LenientEscapes;
        string savedDisc = Flags.DiscontiguousCheck;
        // SWI-only OPERATORS, scoped like the flags: `as` (dynamic/table
        // decorations) would otherwise break user programs that use `as` as a
        // predicate or DCG-nonterminal head. Save prior definitions so nested
        // swi loads restore correctly and a user-defined `as` op survives.
        bool swiOps = dialect == SwiShim.LibraryName;
        bool treallaOps = dialect == TreallaShim.LibraryName;
        bool hadAs = Operators.TryGetInfix("as", out int asPrec, out var asType);
        bool hadTl = Operators.TryGetPrefix("thread_local", out int tlPrec, out var tlType);
        // Trealla-only OPERATORS, scoped like SWI's: the ? / ++ / -- / @
        // mode-annotation prefixes its `:- help(f(?term, ...), ...)` doc
        // directives use on every library predicate.
        bool hadQm = Operators.TryGetPrefix("?", out int qmPrec, out var qmType);
        bool hadPp = Operators.TryGetPrefix("++", out int ppPrec, out var ppType);
        bool hadMm = Operators.TryGetPrefix("--", out int mmPrec, out var mmType);
        bool hadAt = Operators.TryGetPrefix("@", out int atPrec, out var atType);
        bool hadPc = Operators.TryGetPrefix(":", out int pcPrec, out var pcType);
        // ANY dialect-tagged tree load accepts scattered clauses with a
        // warning — third-party sources use the literate style; the strict
        // default stays for native consults.
        if (dialect is not null) Flags.DiscontiguousCheck = "warning";
        if (treallaOps)
        {
            Operators.Define("?", 500, Shumway.Compiler.Parsing.OperatorType.Fx);
            Operators.Define("++", 100, Shumway.Compiler.Parsing.OperatorType.Fy);
            Operators.Define("--", 100, Shumway.Compiler.Parsing.OperatorType.Fy);
            Operators.Define("@", 100, Shumway.Compiler.Parsing.OperatorType.Fy);
            // PREFIX `:` for their `:callable` mode annotations — a
            // separate entry from the INFIX `:` module qualifier at 200,
            // which stays untouched (the module-arc invariant).
            Operators.Define(":", 200, Shumway.Compiler.Parsing.OperatorType.Fy);
        }
        if (dialect == SwiShim.LibraryName)
        {
            Flags.DigitSeparators = true;
            Flags.LenientBareOperatorOperands = true;
            Flags.LenientQuoteCharLiteral = true;
            Flags.LenientArgumentPriority = true;
            Flags.LenientEscapes = true;
            Operators.Define("as", 700, Shumway.Compiler.Parsing.OperatorType.Xfx);
            Operators.Define("thread_local", 1150, Shumway.Compiler.Parsing.OperatorType.Fx);
        }
        // ADR-040 — loading an SWI-dialect module auto-loads the SWI compat shim
        // (nb_setarg, copy_term_nat, …) so the module's system-predicate uses
        // resolve, exactly as SWI's own system predicates are always present.
        if (dialect == SwiShim.LibraryName) EnsureSwiShim();
        // The scryer analogue: emulations of the Rust-VM '$...' natives.
        if (dialect == "scryer") EnsureScryerShim();
        // The trealla analogue (its libraries are pure Prolog; the shim is tiny).
        if (dialect == TreallaShim.LibraryName) EnsureTreallaShim();
        try { return body(); }
        finally
        {
            _activeLibraryDialect = savedDialect;
            Flags.DoubleQuotes = savedDq;
            Flags.DigitSeparators = savedSep;
            Flags.LenientBareOperatorOperands = savedLenientOps;
            Flags.LenientQuoteCharLiteral = savedLenientQuote;
            Flags.LenientArgumentPriority = savedLenientArgs;
            Flags.LenientEscapes = savedLenientEsc;
            Flags.DiscontiguousCheck = savedDisc;
            if (treallaOps)
            {
                Operators.Define("?", hadQm ? qmPrec : 0,
                    hadQm ? qmType : Shumway.Compiler.Parsing.OperatorType.Fx);
                Operators.Define("++", hadPp ? ppPrec : 0,
                    hadPp ? ppType : Shumway.Compiler.Parsing.OperatorType.Fy);
                Operators.Define("--", hadMm ? mmPrec : 0,
                    hadMm ? mmType : Shumway.Compiler.Parsing.OperatorType.Fy);
                Operators.Define("@", hadAt ? atPrec : 0,
                    hadAt ? atType : Shumway.Compiler.Parsing.OperatorType.Fy);
                Operators.Define(":", hadPc ? pcPrec : 0,
                    hadPc ? pcType : Shumway.Compiler.Parsing.OperatorType.Fy);
            }
            if (swiOps)
            {
                Operators.Define("as", hadAs ? asPrec : 0,
                    hadAs ? asType : Shumway.Compiler.Parsing.OperatorType.Xfx);
                Operators.Define("thread_local", hadTl ? tlPrec : 0,
                    hadTl ? tlType : Shumway.Compiler.Parsing.OperatorType.Fx);
            }
        }
    }

    // ADR-040 — libraries whose SWI-shipped version depends on a system predicate
    // Shumway does NOT provide (the MARKER), so loading it would break at runtime,
    // AND for which Shumway ships a complete native equivalent. The name is the
    // candidacy gate: only these names trigger the marker scan. If a resolved file
    // named like a candidate CONTAINS its marker, the load is discarded and the
    // native equivalent is used (use_module is a no-op); a user's own same-named
    // library WITHOUT the marker loads normally. Value = a distinctive substring
    // (a call to the unsupported system predicate) sought in a first-pass read.
    private static readonly Dictionary<string, string[]> NativeOverrideMarkers =
        new(StringComparer.Ordinal)
        {
            // library(when): SWI's when.pl dispatches conditions through
            // '$eval_when_condition'/2, a kernel helper we lack; Shumway ships its
            // own coroutining when/2.
            ["when"] = new[] { "$eval_when_condition", "library(atts)" },
            // library(arithmetic): user-defined evaluable functions ride SWI's
            // GLOBAL goal_expansion + module introspection (import_module,
            // imported_from). On Shumway that hook mis-expands every later
            // consult's arithmetic — a poison pill — and the feature itself
            // (user evaluables) is unsupported. The shim stubs
            // arithmetic_function/1 (accepted, unregistered) and
            // arithmetic_expression_value/2 (builtin evaluation).
            ["arithmetic"] = new[] { "math_goal_expansion" },
            // library(listing): Shumway ships listing/0,1 + portray_clause/1,2
            // natively; SWI's listing.pl needs dicts (`_{}`) + settings and
            // would shadow ours. do_portray_clause is its internal renderer.
            ["listing"] = new[] { "do_portray_clause" },
            // library(prolog_stack): prints the SWI VM backtrace via
            // prolog_frame_attribute — no such VM here; the shim's backtrace/1
            // no-op is the equivalent surface (debug.pl autoloads only that).
            ["prolog_stack"] = new[] { "prolog_frame_attribute" },
            // SCRYER library(format): its rendering core (format_args_cells)
            // reaches builtins:parse_write_options via charsio — bootstrap
            // internals we don't provide. The scryer pack's own Format shim
            // (the one every engine WITHOUT a Scryer tree already uses) is the
            // equivalent surface.
            ["format"] = new[] { "format_args_cells", "library(pio)" },
            // TREALLA library(builtins): its auto-load base wraps dozens of C
            // natives ($load_ops, $bb_*, ...); the engine IS that layer.
            ["builtins"] = new[] { "$load_ops" },
            // TREALLA library(atts): get_atts/put_atts are C builtins there;
            // Shumway ships its own atts (CompatLibraries) over the native
            // attribute-list primitives.
            ["atts"] = new[] { "user:goal_expansion(get_atts" },
            // TREALLA library(iso_ext): rides $register_cleanup/$call_cleanup/
            // $countall C natives; setup_call_cleanup & co are native here.
            ["iso_ext"] = new[] { "$register_cleanup" },
            // TREALLA library(charsio): rides $char_type/$get_chars natives;
            // the engine's charsio surface is builtin.
            ["charsio"] = new[] { "$char_type" },
            // TREALLA library(error): rides $first_non_octet; must_be/can_be
            // are native + prelude.
            ["error"] = new[] { "$first_non_octet" },
            // ANY tree's atts-based freeze/when/dif (Trealla's and Scryer's
            // are both Triska's code): the engine's native coroutining is
            // the certified implementation of all three, and the atts-based
            // ones ride hook subtleties of their home VM (R10 measured the
            // regressions: the whole dif family flipped when the mounted
            // tree's versions shadowed ours).
            ["freeze"] = new[] { "library(atts)" },
            ["dif"] = new[] { "library(atts)" },
            // SCRYER library(time): wraps the '$cpu_now' native. Shumway's
            // native time/1 and sleep/1 are already bare-global; the file's
            // versions would shadow them with broken ones.
            ["time"] = new[] { "$cpu_now" },
        };

    /// <summary>True when <paramref name="name"/> is a native-override CANDIDATE
    /// and the resolved file at <paramref name="path"/> carries the candidate's
    /// marker — meaning it is the (unsupportable) SWI version, so the load should
    /// be discarded in favour of Shumway's native equivalent. A non-candidate name
    /// short-circuits without touching the file.</summary>
    private bool ShouldUseNativeOverride(string name, string path)
    {
        if (!NativeOverrideMarkers.TryGetValue(name, out string[]? markers)) return false;
        try
        {
            string text = System.IO.File.ReadAllText(path);
            foreach (string marker in markers)
                if (text.Contains(marker, StringComparison.Ordinal)) return true;
            return false;
        }
        catch { return false; }   // unreadable → fall through and load it
    }

    /// <summary>Ensures Shumway's native equivalent of an overridden library is
    /// loaded (called when the SWI file is discarded), so the predicates the
    /// program expects are present. Returns the name of an export-qualified
    /// module when the native equivalent carries importer-facing wrappers
    /// (atts; trealla's freeze), else null (bare-global surface).</summary>
    private string? LoadNativeOverride(string name)
    {
        switch (name)
        {
            case "when": UseCoroutining(); break;        // our coroutining when/2
            case "arithmetic": EnsureSwiShim(); break;   // shim stubs (see marker note)
            case "listing": break;                       // native listing/portray_clause builtins
            case "prolog_stack": EnsureSwiShim(); break; // shim backtrace/1 no-op
            case "format": UseCompatLibrary("format"); break;   // scryer pack Format shim
            case "builtins": break;                      // the engine IS the builtins layer
            case "atts": UseCompatLibrary("atts"); break;
            case "iso_ext": break;                       // native setup_call_cleanup & co
            case "charsio": break;                       // builtin charsio surface
            case "error": break;                         // native must_be/can_be
            case "freeze":
                UseCoroutining();
                // Trealla's frozen/2 answers 'freeze:freeze(Var, Goal)' — a
                // re-establishing, module-qualified goal — where the engine's
                // (SICStus-style) frozen/2 answers the bare goal. Their
                // programs pattern-match that shape, so a trealla-dialect
                // resolution of library(freeze) serves a wrapper module whose
                // frozen/2 speaks their format; freeze/2 itself stays the
                // native bare-global (not exported — no delegation loop).
                if (_activeLibraryDialect == "trealla")
                {
                    if (_loadedCompatLibraries.Add("trealla_freeze"))
                        ConsultStringInner(TreallaFreezeShim, recordInHistory: false, librarySource: true);
                    return "trealla_freeze";
                }
                break;
            case "dif": UseCoroutining(); break;
            case "time": break;                          // native time/1 + sleep/1 serve
        }
        return null;
    }

    private bool _treallaShimLoaded;

    /// <summary>Consults the Trealla compat shim once — triggered when a
    /// trealla-dialect library loads (<see cref="WithDialect"/>).</summary>
    internal void EnsureTreallaShim()
    {
        if (_treallaShimLoaded) return;
        _treallaShimLoaded = true;
        var savedDq = Flags.DoubleQuotes;
        Flags.DoubleQuotes = DialectRegistry.DoubleQuotesOf(TreallaShim.LibraryName);
        try { ConsultStringInner(TreallaShim.Source, recordInHistory: false, librarySource: true); }
        finally { Flags.DoubleQuotes = savedDq; }
    }

    private bool _swiShimLoaded;

    /// <summary>Consults the SWI compat shim once. Triggered automatically when an
    /// SWI-dialect module loads (<see cref="WithDialect"/>) and explicitly by
    /// <c>use_module(library(swi))</c>.</summary>
    internal void EnsureSwiShim()
    {
        if (_swiShimLoaded) return;
        _swiShimLoaded = true;
        var savedDq = Flags.DoubleQuotes;
        Flags.DoubleQuotes = DialectRegistry.DoubleQuotesOf(SwiShim.LibraryName);
        try { ConsultStringInner(SwiShim.Source, recordInHistory: false, librarySource: true); }
        finally { Flags.DoubleQuotes = savedDq; }
    }

    // Sequence for the per-consult early-hook hidden modules (ConsultPipeline);
    // nested library consults each need a distinct name.
    internal int _earlyHookSeq;

    private bool _scryerShimLoaded;

    /// <summary>Consults the Scryer compat shim once — bare-global emulations of
    /// the Rust-VM <c>'$...'</c> natives Scryer libraries bottom out in (random,
    /// files, os, charsio's char_type, crypto's random bytes) plus builtins.pl
    /// helpers. Triggered automatically when a scryer-dialect module is
    /// loaded.</summary>
    internal void EnsureScryerShim()
    {
        if (_scryerShimLoaded) return;
        _scryerShimLoaded = true;
        ConsultStringInner(ScryerShim.Source, recordInHistory: false, librarySource: true);
    }

    // The trealla-dialect frozen/2 wrapper (see LoadNativeOverride "freeze").
    // Reads the native coroutining attribute directly; the freeze: module
    // prefix in the answer is DATA (their format), never called here.
    private const string TreallaFreezeShim = """
        :- module(trealla_freeze, [frozen/2]).
        frozen(X, G) :-
            (   var(X), get_attr(X, coroutining, frozen(G0)) ->
                G = freeze:freeze(X, G0)
            ;   G = true
            ).
        """;

    internal bool UseCompatLibrary(string name)
    {
        // Explicit `use_module(library(swi))` — load the SWI compat shim (like
        // SWI's own library(sicstus)). Routes through the same one-shot loader
        // the swi-dialect auto-load uses.
        if (name == SwiShim.LibraryName) { EnsureSwiShim(); return true; }
        if (!DialectRegistry.TryResolve(_activeLibraryDialect, name,
                out string source, out var doubleQuotes, out _))
            return false;
        // recordInHistory:false — the importing program's own source (which
        // carries the use_module directive) is what SaveState replays; the
        // directive re-loads the library on restore, so recording the library
        // body too would double-consult it (and trip public uniqueness).
        if (_loadedCompatLibraries.Add(name) && source.Length > 0)
        {
            // ADR-040 Component 4 — parse the shim with its dialect's
            // double_quotes (Scryer = chars, SWI = codes), then restore, so two
            // dialects' libraries parse correctly in the same engine.
            var savedDq = Flags.DoubleQuotes;
            Flags.DoubleQuotes = doubleQuotes;
            try { ConsultStringInner(source, recordInHistory: false, librarySource: true); }
            finally { Flags.DoubleQuotes = savedDq; }
        }
        return true;
    }

    // ADR-038 — the ordered library search path. Directories come from (in this
    // precedence) the file_search_path(library, Dir) / library_directory(Dir)
    // dynamic facts, the AddLibraryDirectory API, the SHUMWAY_LIBRARY_PATH env
    // var, and (added by the REPL/CLI) the shipped lib/ directory. Lazily built
    // so the env read happens once, on first library resolution.
    private List<string>? _libraryDirs;

    private void EnsureLibraryDirs()
    {
        if (_libraryDirs is not null) return;
        _libraryDirs = new List<string>();
        string? env = Environment.GetEnvironmentVariable("SHUMWAY_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(env))
            // Trim() by hand rather than StringSplitOptions.TrimEntries, which
            // is a .NET 5+ enum value an #if cannot paper over.
            foreach (string entry in env.Split(System.IO.Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string d = entry.Trim();
                if (d.Length == 0) continue;
                // Each entry may carry a :dialect tag (ADR-040 D5.2).
                AddLibraryDirectorySpec(d);
            }
    }

    private void AddLibraryDirNormalized(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = System.IO.Path.GetFullPath(path); } catch { full = path; }
        if (!_libraryDirs!.Contains(full, StringComparer.OrdinalIgnoreCase))
            _libraryDirs!.Add(full);
    }

    /// <summary>Adds <paramref name="path"/> to this engine's library search
    /// path (ADR-038), so a later <c>use_module(library(X))</c> can resolve
    /// <c>X.pl</c> / <c>X.shum</c> under it. Idempotent; the directory need not
    /// exist yet.</summary>
    public void AddLibraryDirectory(string path)
    {
        EnsureLibraryDirs();
        AddLibraryDirNormalized(path);
    }

    /// <summary>Adds a library directory from a CLI/env spec that MAY carry a
    /// dialect tag as a leading <c>dialect:</c> prefix (ADR-040 D5.2) — e.g.
    /// <c>scryer:C:/Scryer/lib</c> or <c>swi:/opt/swipl/library</c>. Leading is
    /// drive-letter-safe by construction: the prefix (before the FIRST colon) is
    /// a dialect only when it is a known one, and a Windows drive letter
    /// (<c>C</c>, <c>D</c>) never is — so a plain path (<c>C:/foo</c>) or an
    /// untagged dir is unaffected. Accepted in <c>SHUMWAY_LIBRARY_PATH</c> entries
    /// and the REPL/CLI <c>-L</c> flag alike.</summary>
    public void AddLibraryDirectorySpec(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return;
        int colon = spec.IndexOf(':');
        if (colon > 0)
        {
            string prefix = spec[..colon];
            if (DialectRegistry.IsKnownDialect(prefix))
            {
                AddLibraryDirectory(spec[(colon + 1)..], prefix);
                return;
            }
        }
        AddLibraryDirectory(spec);
    }

    /// <summary>Adds Shumway's shipped default library directories to the search
    /// path (ADR-038): a <c>lib/</c> folder beside the running executable (where
    /// the REPL/CLI's copy of the repo <c>lib/</c> lands, and where <c>--exe</c>
    /// deploys it) and, if different, a <c>lib/</c> under the current directory.
    /// The REPL/CLIs call this at startup so <c>use_module(library(X))</c> finds
    /// the bundled libraries with no configuration.</summary>
    public void AddDefaultLibraryDirectories()
    {
        AddLibraryDirIfExists(System.IO.Path.Combine(AppContext.BaseDirectory, "lib"));
        AddLibraryDirIfExists(System.IO.Path.Combine(
            System.IO.Directory.GetCurrentDirectory(), "lib"));
    }

    private void AddLibraryDirIfExists(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) AddLibraryDirectory(path);
        }
        catch { /* an inaccessible probe path is simply skipped */ }
    }

    // The library directories in resolution order: dynamic facts first (so a
    // program's own :- file_search_path / library_directory wins), then the
    // API/env/shipped dirs.
    private IEnumerable<string> EnumerateLibraryDirs()
    {
        int fsp = FunctorTable.Intern(AtomTable.Intern("file_search_path").Id, 2);
        if (_dynStore.TryGetClauses(fsp, out var fspClauses))
            foreach (Clause cl in fspClauses)
                if (cl.Term is CompoundTerm { Functor: "file_search_path",
                        Args: [AtomTerm { Name: "library" }, var d] }
                    && TryDirText(d, out string dir))
                    yield return dir;

        int ld = FunctorTable.Intern(AtomTable.Intern("library_directory").Id, 1);
        if (_dynStore.TryGetClauses(ld, out var ldClauses))
            foreach (Clause cl in ldClauses)
                if (cl.Term is CompoundTerm { Functor: "library_directory", Args: [var d] }
                    && TryDirText(d, out string dir))
                    yield return dir;

        EnsureLibraryDirs();
        foreach (string d in _libraryDirs!) yield return d;
    }

    private static bool TryDirText(Term t, out string dir)
    {
        switch (t)
        {
            case AtomTerm a: dir = a.Name; return true;
            case StringTerm s: dir = s.Content; return true;
            default: dir = ""; return false;
        }
    }

    /// <summary>Reads a <c>library(...)</c> argument as a relative library name:
    /// a plain atom (<c>lists</c>), or a <c>/</c>-path of atoms for a
    /// subdirectory library (SWI's <c>library(dcg/basics)</c> →
    /// <c>"dcg/basics"</c>, resolved against each search dir).</summary>
    private static bool TryLibraryRelName(Term t, out string rel)
    {
        switch (t)
        {
            case AtomTerm a: rel = a.Name; return true;
            case CompoundTerm { Functor: "/", Args: [var l, var r] }
                when TryLibraryRelName(l, out string ls) && TryLibraryRelName(r, out string rs):
                rel = ls + "/" + rs;
                return true;
            default: rel = ""; return false;
        }
    }

    /// <summary>Resolves <c>library(<paramref name="name"/>)</c> to a file on the
    /// library search path (ADR-038): the first <c>Dir/name.pl</c> or
    /// <c>Dir/name.shum</c> that exists, in search-path order. Returns the full
    /// path in <paramref name="path"/>, or <c>false</c> if none is found.</summary>
    internal bool TryResolveLibrary(string name, out string path)
    {
        foreach (string dir in EnumerateLibraryDirs())
        {
            foreach (string ext in LibraryExtensions)
            {
                string candidate = System.IO.Path.Combine(dir, name + ext);
                try
                {
                    if (System.IO.File.Exists(candidate))
                    {
                        path = System.IO.Path.GetFullPath(candidate);
                        return true;
                    }
                }
                catch { /* an invalid path component — skip this candidate */ }
            }
        }
        path = "";
        return false;
    }

    private static readonly string[] LibraryExtensions = { ".pl", ".shum" };

    // ADR-038 — the module name the most recent consult defined, set at
    // manifest creation (ConsultPipeline). A nested library consult sets it, so
    // ExecuteUseModuleDirective reads it right after ConsultFile returns to learn
    // which module a use_module(library(X)) actually loaded (X.pl may declare a
    // module named other than X).
    internal string? _lastConsultedModuleName;

    // ADR-038 — resolved library path → the module its file defined, so a second
    // import of the same library (idempotent, not re-consulted) still yields the
    // module name for the importer's import table.
    private readonly Dictionary<string, string> _libraryModuleByPath =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Executes a <c>use_module/1</c> directive body.
    /// <c>library(Name)</c> loads a constraint/compatibility library or resolves
    /// a <c>.pl</c>/<c>.shum</c> on the search path; a plain atom names a file to
    /// consult. Returns the name of the loaded <em>export-qualified</em> module
    /// (ADR-038 — the importer builds its import table from this module's
    /// exports), or <c>null</c> for a legacy bare-global module, a baked library,
    /// or an unresolved/failed import. <paramref name="throwOnUnresolved"/>
    /// selects behaviour for an unknown library / missing file: the consult-time
    /// directive path warns and continues (<c>false</c>); the goal-form
    /// <c>use_module/1</c> builtin raises an ISO error (<c>true</c>).</summary>
    // Depth of use_module-driven loads in progress. A module file consulted
    // DIRECTLY (depth 0 — REPL command line, consult/1, embedding
    // ConsultFile/ConsultString) auto-imports its exports into `user`
    // (SWI behaviour); a dependency loaded via use_module only feeds the
    // IMPORTER's table.
    internal int _useModuleLoadDepth;

    internal string? ExecuteUseModuleDirective(Term spec, bool throwOnUnresolved = false)
    {
        _useModuleLoadDepth++;
        try { return ExecuteUseModuleDirectiveCore(spec, throwOnUnresolved); }
        finally { _useModuleLoadDepth--; }
    }

    private string? ExecuteUseModuleDirectiveCore(Term spec, bool throwOnUnresolved)
    {
        if (spec is CompoundTerm { Functor: "library", Args: [var libArg] }
            && TryLibraryRelName(libArg, out string libName))
        {
            switch (libName)
            {
                // (1) baked C# libraries — take precedence, they carry native
                // hooks and stay bare-global (no import table).
                case "clpfd": UseClpfd(); return null;
                case "clpr":  UseClpr();  return null;
                case "coroutining": UseCoroutining(); return null;
                default:
                    // (1.5) the module is ALREADY LOADED (typically from a
                    // bundle whose manifests LoadBundle reconstructed):
                    // import straight from the live manifest — predicates
                    // AND exported operators — with no file involved.
                    // SWI semantics: use_module of a loaded module imports.
                    if (_modules.TryGetValue(libName, out var loadedManifest)
                        && loadedManifest.IsExportQualified)
                        return libName;
                    // (2) ADR-038 — a .pl/.shum on the library search path.
                    if (TryResolveLibrary(libName, out string libPath))
                    {
                        // ADR-040 — the SWI-shipped version of a native-override
                        // candidate (detected by its marker in the file) can't run
                        // here; discard the load and use Shumway's native equivalent
                        // (use_module becomes a no-op). A non-candidate, or a
                        // same-named file without the marker, loads normally.
                        // ADR-040 D5.2 — a dir tagged with a dialect loads its
                        // libraries in that dialect (name resolution +
                        // double_quotes) for the whole subtree. Computed BEFORE
                        // the override check: a native override can be
                        // dialect-sensitive (trealla's freeze wrapper), so it
                        // must run inside the same dialect scope the file
                        // itself would have loaded under.
                        string? dirDialect = DialectForResolvedPath(libPath);
                        if (ShouldUseNativeOverride(libName, libPath))
                        {
                            string? overrideModule = dirDialect is not null
                                ? WithDialect(dirDialect, () => LoadNativeOverride(libName))
                                : LoadNativeOverride(libName);
                            return libName == "atts" ? "atts" : overrideModule;
                        }
                        return dirDialect is not null
                            ? WithDialect(dirDialect, () => LoadResolvedLibrary(libName, libPath))
                            : LoadResolvedLibrary(libName, libPath);
                    }
                    // (3) built-in Scryer/Trealla compatibility table. Most
                    // entries are bare-global (nothing to import); atts is a
                    // REAL module with exports (the hProlog-compat wrappers
                    // shadow the raw builtins for importers only), so its
                    // name flows back for RecordImports.
                    if (UseCompatLibrary(libName))
                        return libName == "atts" ? "atts" : null;
                    // (4) genuinely unknown.
                    if (throwOnUnresolved)
                        throw new Shumway.Core.PrologRuntimeException(
                            $"existence_error(library, {libName})");
                    // Name WHERE it looked. "Unknown library" alone leaves the
                    // reader guessing between a misspelling, a search path that
                    // was never added, and a file that is not where it is
                    // expected — three different fixes.
                    string searched = string.Join(", ", EnumerateLibraryDirs());
                    Warn($"warning: unknown library '{libName}' in use_module/1 — ignored"
                       + (searched.Length == 0
                            ? " (the library search path is empty)"
                            : $" (searched: {searched})"));
                    return null;
            }
        }
        if (spec is AtomTerm fileAtom)
        {
            // Already-loaded module (e.g. consulted directly on the command
            // line, or imported earlier) — importing it again is a no-op, but
            // still yield its name so an importer can build its import table.
            if (_modules.ContainsKey(fileAtom.Name))
                return ExportQualifiedNameOrNull(fileAtom.Name);
            string path = fileAtom.Name;
            if (_consultBaseDir is not null && !System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(_consultBaseDir, path);
            if (!System.IO.Path.HasExtension(path) && System.IO.File.Exists(path + ".pl"))
                path += ".pl";
            if (!System.IO.File.Exists(path))
            {
                if (throwOnUnresolved)
                    throw new Shumway.Core.PrologRuntimeException(
                        $"existence_error(source_sink, '{fileAtom.Name}')");
                Warn(
                    $"warning: use_module/1 target '{fileAtom.Name}' not found — ignored");
                return null;
            }
            return LoadResolvedLibrary(fileAtom.Name, path);
        }
        return null;
    }

    // Consults a library resolved off the search path, idempotently, and returns
    // the loaded module's name when it is export-qualified (ADR-038), else null.
    // ConsultFile is extension-routed (.shum → LoadBundle, else source). A failed
    // import warns and continues — a predicate genuinely needed surfaces a clearer
    // existence_error at its call site — rather than aborting the importing consult.
    private string? LoadResolvedLibrary(string name, string path)
    {
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch { full = path; }

        // Already loaded, and still what was loaded: importing it again is the
        // no-op it should be. CHANGED on disk is the other case — someone is
        // editing it — and then reloading the importer has to bring the change
        // in, or the program runs against a version that no longer exists.
        bool changed = FileDiffersFromLoad(full);
        if (!changed)
        {
            if (_libraryModuleByPath.TryGetValue(full, out string? known))
                return ExportQualifiedNameOrNull(known);
            if (_consultedPaths.Contains(full))
                return null;   // loaded by another route; no recorded module mapping
        }
        try
        {
            // "The module this consult declared" is a source-file notion: a
            // .shum holds MANY modules and brings them all in at once, so it
            // sets nothing. Cleared first either way, so a stale name from an
            // earlier load cannot be mistaken for this one's.
            bool isBundle = path.EndsWith(".shum", System.StringComparison.OrdinalIgnoreCase);
            _lastConsultedModuleName = null;

            // Reloading REPLACES what the file defines rather than adding to it;
            // a first load has nothing to replace, so the two are the same call.
            if (_consultedPaths.Contains(full)) ReconsultFile(path);
            else ConsultFile(path);

            // From a bundle, the module being imported is the one named like the
            // library — which is what `library(clpz)` asked for. Without this an
            // export-qualified module loaded from a bundle fed no import table,
            // so its predicates were invisible to the importer (the operators
            // arrived, the predicates did not).
            string? loaded = isBundle
                ? (_modules.ContainsKey(name) ? name : null)
                : _lastConsultedModuleName;
            if (loaded is not null) _libraryModuleByPath[full] = loaded;
            return ExportQualifiedNameOrNull(loaded);
        }
        catch (System.Exception ex)
        {
            Warn(
                $"warning: use_module(library({name})) failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>One bare module aliased into <c>user</c> (or, for a collision
    /// skip, the indicators that collided).</summary>
    internal readonly record struct BareModulePromotion(
        string Module, List<(string Name, int Arity)> Predicates);

    /// <summary>The outcome of <see cref="PromoteBareBundleModulesToUser"/>:
    /// modules whose locals were aliased into <c>user</c>, and modules skipped
    /// wholesale because a predicate name collided.</summary>
    internal readonly record struct BundlePromotionResult(
        List<BareModulePromotion> Promoted,
        List<BareModulePromotion> SkippedForCollision);

    /// <summary>REPL usability (ADR-038): a bundle loaded interactively leaves
    /// the top level standing in <c>user</c>, so the bundle's module-local
    /// predicates are invisible — unlike consulting the equivalent source,
    /// where you stand in the file's module and can call its predicates. Alias
    /// each "bare" (non-export-qualified) module's local predicates into
    /// <c>user</c>'s import table (<c>name → module</c>, resolving to
    /// <c>module$name</c>) so the top level can call them. Libraries
    /// (<c>:- module(Name, [Exports])</c>) are never touched — their names are
    /// deliberately namespaced.
    ///
    /// <para>Full fidelity to "standing in the module": <c>user</c> also
    /// inherits each promoted module's IMPORT table, so a raw goal using a name
    /// the module imported from a library (e.g. <c>X in 1..3</c> when it did
    /// <c>use_module(library(clpz))</c>) resolves the same way the module's own
    /// clauses do.</para>
    ///
    /// <para>Collisions are handled ALL-OR-NOTHING per module: if any name a
    /// module would contribute to <c>user</c> (a local alias or an inherited
    /// import) would land under two different targets — another bare module's,
    /// or one already claimed in <c>user</c> — that whole module is skipped, so
    /// <c>user</c> never sees a module half-promoted. The decision is computed
    /// over all candidates at once, so a name shared by two modules skips both.
    /// Public/dynamic predicates are already bare-global and need no alias.</para></summary>
    internal BundlePromotionResult PromoteBareBundleModulesToUser()
    {
        var promoted = new List<BareModulePromotion>();
        var skipped = new List<BareModulePromotion>();
        if (!_modules.TryGetValue(DefaultModuleName, out ModuleManifest? userManifest))
            return new BundlePromotionResult(promoted, skipped);

        // Bare candidates: non-library modules carrying aliasable locals.
        var candidates = new List<string>();
        foreach (var (name, m) in _modules)
        {
            if (name == DefaultModuleName || name == PreludeModuleName) continue;
            if (m.IsExportQualified) continue;   // library — never promote
            if (_precompiledModuleLocals.TryGetValue(name, out var locals)
                && locals.Count > 0)
                candidates.Add(name);
        }
        if (candidates.Count == 0)
            return new BundlePromotionResult(promoted, skipped);

        // Each candidate contributes name→target entries: a local fid targets
        // its own module (module$name); an imported fid targets its source.
        List<(int Fid, string Target)> Contributions(string mod)
        {
            var list = new List<(int, string)>();
            foreach (int fid in _precompiledModuleLocals[mod]) list.Add((fid, mod));
            foreach (var (fid, src) in _modules[mod].Imports) list.Add((fid, src));
            return list;
        }

        // Global fid → distinct targets, seeded with what user already resolves,
        // so >1 target on a fid means a genuine disagreement (a collision).
        var targets = new Dictionary<int, HashSet<string>>();
        foreach (var (fid, src) in userManifest.Imports)
            (targets[fid] = new HashSet<string>()).Add(src);
        foreach (string mod in candidates)
            foreach (var (fid, tgt) in Contributions(mod))
            {
                if (!targets.TryGetValue(fid, out var set))
                    targets[fid] = set = new HashSet<string>();
                set.Add(tgt);
            }

        bool changed = false;
        foreach (string mod in candidates)
        {
            var contrib = Contributions(mod);
            // Collision = any contributed fid whose global target set disagrees.
            var colliding = new List<(string Name, int Arity)>();
            foreach (var (fid, _) in contrib)
                if (targets[fid].Count > 1)
                {
                    var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
                    colliding.Add((Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?", arity));
                }
            if (colliding.Count > 0)
            {
                skipped.Add(new BareModulePromotion(mod, colliding));
                continue;   // all-or-nothing: promote none of this module
            }
            // Clean — commit its locals (reported) and inherited imports (silent).
            var aliased = new List<(string, int)>();
            foreach (int fid in _precompiledModuleLocals[mod])
                if (userManifest.Imports.TryAdd(fid, mod))
                {
                    changed = true;
                    var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
                    aliased.Add((Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?", arity));
                }
            foreach (var (fid, src) in _modules[mod].Imports)
                if (userManifest.Imports.TryAdd(fid, src))
                    changed = true;
            promoted.Add(new BareModulePromotion(mod, aliased));
        }
        if (changed) InvalidatePersistent();
        return new BundlePromotionResult(promoted, skipped);
    }

    /// <summary>ADR-038 — resolves which module actually PROVIDES an export of
    /// <paramref name="sourceModule"/>. A module may list an export it does not
    /// define locally: a re-export of a predicate it imported (SICStus-style —
    /// chase the import chain to the DEFINING module, so the importer binds
    /// straight to it), or a re-export of a bare-global builtin/prelude predicate
    /// (SWI's <c>library(terms)</c> lists the builtin <c>term_variables/2</c> for
    /// SICStus source compatibility — return <c>null</c>: no mapping, the call
    /// falls through to the bare-global). A dynamic-declared export is also
    /// <c>null</c>: dynamics bypass mangling, so the bare name IS the store.</summary>
    internal string? ExportProvider(string sourceModule, int fid)
    {
        string cur = sourceModule;
        HashSet<string>? seen = null;
        while (true)
        {
            if (!_modules.TryGetValue(cur, out ModuleManifest? m)) return null;
            if (m.DynamicFunctors.Contains(fid)) return null;
            if (DefinedHeads(cur, m).Contains(fid)) return cur;
            if (!m.Imports.TryGetValue(fid, out string? next)) return null;
            if (!(seen ??= new HashSet<string>()).Add(cur)) return null;   // cycle
            cur = next;
        }
    }

    // Clause-head sets per module, invalidated by clause-list identity + count
    // (a module reload replaces the manifest; a same-manifest consult appends)
    // and by the count of precompiled locals, which arrive without touching the
    // clause list at all.
    private readonly Dictionary<string,
        (object ClausesRef, int Count, int LocalCount, HashSet<int> Heads)>
        _moduleDefinedHeadsCache = new();

    private HashSet<int> DefinedHeads(string moduleName, ModuleManifest m)
    {
        _precompiledModuleLocals.TryGetValue(moduleName, out var locals);
        int localCount = locals?.Count ?? 0;
        if (_moduleDefinedHeadsCache.TryGetValue(moduleName, out var e)
            && ReferenceEquals(e.ClausesRef, m.Clauses) && e.Count == m.Clauses.Count
            && e.LocalCount == localCount)
            return e.Heads;
        var heads = new HashSet<int>();
        foreach (var c in m.Clauses)
            heads.Add(ConsultPipeline.HeadFunctorIdOf(c));
        // A module loaded from a BUNDLE has no clauses — it has compiled code,
        // and what it defines is recorded as its precompiled locals. Without
        // them an export-qualified library loaded from a .shum exports names
        // that resolve to nothing: the importer's table stays empty, and the
        // predicates are invisible while the operators are not.
        if (locals is not null) heads.UnionWith(locals);
        _moduleDefinedHeadsCache[moduleName] = (m.Clauses, m.Clauses.Count, localCount, heads);
        return heads;
    }

    /// <summary>ADR-038 — imports the whole export surface of
    /// <paramref name="sourceModule"/> into the top-level <c>user</c> module's
    /// import table (first-import-wins), so an interactive query following a
    /// goal-form <c>use_module(library(X))</c> resolves the imported predicates.
    /// Invalidates the rewrite caches when it adds anything.</summary>
    internal void ImportAllExportsIntoUser(string sourceModule)
    {
        // ADR-046 — the module's exported operators become part of the
        // top-level syntax too (a directly-consulted module or a goal-form
        // use_module is a user-level import).
        ApplyExportedOperators(sourceModule, _operators);
        if (!_modules.TryGetValue(sourceModule, out ModuleManifest? srcManifest)) return;
        if (!_modules.TryGetValue(DefaultModuleName, out ModuleManifest? userManifest)) return;
        bool changed = false;
        List<int>? added = null;
        Dictionary<string, List<int>>? kept = null;
        foreach (int fid in srcManifest.ExportFunctors)
        {
            // Chase re-exports to the defining module; a re-exported bare-global
            // (builtin/prelude/dynamic) gets no mapping and resolves bare.
            if (ExportProvider(sourceModule, fid) is not { } provider) continue;
            if (userManifest.Imports.TryAdd(fid, provider))
            {
                changed = true;
                (added ??= new List<int>()).Add(fid);
            }
            else if (userManifest.Imports[fid] is { } existing && existing != provider)
            {
                kept ??= new Dictionary<string, List<int>>();
                if (!kept.TryGetValue(existing, out var list))
                    kept[existing] = list = new List<int>();
                list.Add(fid);
            }
        }
        if (kept is not null)
            foreach (var (winner, fids) in kept)
                Warn(
                    $"warning: {IndicatorList(fids)} already imported from "
                    + $"'{winner}' — keeping '{winner}', ignoring '{sourceModule}'.");
        if (added is not null) WarnImportsShadowGlobals(added, sourceModule);
        if (changed) InvalidatePersistent();
    }

    // The prelude is exempt from shadow warnings — importing a name it also
    // defines is the libc analogy, routine and intentional.
    private const string PreludeModuleName = "$prelude";

    /// <summary>Top-level imports win over bare-global publics, so loading two
    /// libraries with overlapping surfaces (the clpfd + clpz coexistence
    /// surprise) silently reroutes bare calls. Warn, aggregated per shadowed
    /// module, when freshly added `user` imports hide an already-loaded
    /// module's public predicates.</summary>
    private void WarnImportsShadowGlobals(List<int> addedFids, string sourceModule)
    {
        Dictionary<string, List<int>>? shadowed = null;
        foreach (int fid in addedFids)
        {
            foreach (var (modName, m) in _modules)
            {
                if (modName == DefaultModuleName || modName == PreludeModuleName
                    || modName == sourceModule) continue;
                if (!m.PublicFunctors.Contains(fid)) continue;
                shadowed ??= new Dictionary<string, List<int>>();
                if (!shadowed.TryGetValue(modName, out var list))
                    shadowed[modName] = list = new List<int>();
                list.Add(fid);
                break;
            }
        }
        if (shadowed is null) return;
        foreach (var (owner, fids) in shadowed)
            Warn(
                $"warning: importing {IndicatorList(fids)} from '{sourceModule}' "
                + $"shadows the global definition(s) from '{owner}' at the top level.");
    }

    /// <summary>The reverse load order: a module's bare-global publics landing
    /// while `user` already imports those names — the imports keep winning, so
    /// the newly loaded module's definitions are unreachable bare. Aggregated
    /// per import source.</summary>
    internal void WarnPublicShadowedByUserImports(string moduleName, ModuleManifest manifest)
    {
        if (moduleName == DefaultModuleName || moduleName == PreludeModuleName) return;
        if (!_modules.TryGetValue(DefaultModuleName, out ModuleManifest? user)) return;
        if (user.Imports.Count == 0) return;
        Dictionary<string, List<int>>? bySource = null;
        foreach (int fid in manifest.PublicFunctors)
        {
            if (!user.Imports.TryGetValue(fid, out var src) || src == moduleName) continue;
            bySource ??= new Dictionary<string, List<int>>();
            if (!bySource.TryGetValue(src, out var list))
                bySource[src] = list = new List<int>();
            list.Add(fid);
        }
        if (bySource is null) return;
        foreach (var (src, fids) in bySource)
            Warn(
                $"warning: the global {IndicatorList(fids)} from '{moduleName}' "
                + $"is shadowed at the top level by the existing import(s) from '{src}'.");
    }

    /// <summary>Consult-path recording of `user`-level imports (a
    /// <c>:- use_module</c> in a plain non-module file). Directive semantics
    /// keep the LAST import on a collision (unchanged); emits the same
    /// user-level shadow warnings as the goal-form import.</summary>
    internal void RecordUserImports(
        ModuleManifest userManifest, IEnumerable<KeyValuePair<int, string>> imports)
    {
        Dictionary<string, List<int>>? addedBySource = null;
        foreach (var (fid, src) in imports)
        {
            if (userManifest.Imports.TryGetValue(fid, out var existing))
            {
                if (existing != src)
                {
                    Warn(
                        $"warning: {IndicatorList(new List<int> { fid })} import from "
                        + $"'{src}' replaces the earlier import from '{existing}'.");
                    userManifest.Imports[fid] = src;
                }
                continue;
            }
            userManifest.Imports[fid] = src;
            addedBySource ??= new Dictionary<string, List<int>>();
            if (!addedBySource.TryGetValue(src, out var list))
                addedBySource[src] = list = new List<int>();
            list.Add(fid);
        }
        if (addedBySource is not null)
        {
            foreach (var (src, fids) in addedBySource)
                WarnImportsShadowGlobals(fids, src);
        }
    }

    private readonly Dictionary<string,
        (object ClausesRef, int ClauseCount, int PublicCount, int ExportCount,
         HashSet<int> Mangled)> _moduleMangledCache = new();

    /// <summary>Explicit (<c>:- module</c>) modules the user consulted
    /// DIRECTLY — REPL command line, <c>consult/1</c>, the embedding's
    /// ConsultFile/ConsultString — as opposed to arriving as a
    /// <c>use_module</c> dependency. These are the modules whose locals the
    /// consult-direct bare-call fallback may resolve
    /// (<see cref="ResolveDirectConsultLocal"/>): consulting a source means
    /// being able to call its predicates. A module that later gets consulted
    /// directly joins the set from that moment on.</summary>
    internal readonly HashSet<string> _directlyConsultedModules = new();

    /// <summary>The (module, functor) pairs behind the qualified
    /// <c>current_predicate(M:PI)</c>: what each module DEFINES — clause
    /// heads, its <c>:- dynamic</c> declarations, a precompiled bundle's
    /// recorded locals and publics. Imports and re-exports are not
    /// definitions and are absent. Names are the user-facing bare spelling
    /// (manifests hold pre-rewrite clauses); <c>$</c>-names never surface.
    /// Modules sorted, fids first-seen order — a stable enumeration.</summary>
    internal IEnumerable<(string Module, int Fid)> DefinedModulePredicates(string? onlyModule)
    {
        var names = new List<string>();
        foreach (string mod in _modules.Keys)
            if ((onlyModule is null || mod == onlyModule) && !mod.StartsWith('$'))
                names.Add(mod);
        names.Sort(StringComparer.Ordinal);
        foreach (string mod in names)
        {
            ModuleManifest manifest = _modules[mod];
            var seen = new HashSet<int>();
            bool Fresh(int fid)
            {
                if (!seen.Add(fid)) return false;
                var (atomId, _) = Shumway.Core.FunctorTable.Lookup(fid);
                string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
                return name is { Length: > 0 } && name.IndexOf('$') < 0;
            }
            foreach (var c in manifest.Clauses)
            {
                if (c.Kind == Shumway.Compiler.Ast.ClauseKind.Directive) continue;
                int fid = ConsultPipeline.HeadFunctorIdOf(c);
                if (Fresh(fid)) yield return (mod, fid);
            }
            foreach (int fid in manifest.DynamicFunctors)
                if (Fresh(fid)) yield return (mod, fid);
            // Consult-path dynamics: the store is flat-global, but the
            // declaring module is on record (first declarer wins) — a
            // `:- dynamic` in M's source counts as M defining it.
            foreach (var (fid, declarer) in _dynamicDeclaringModule)
                if (declarer == mod && Fresh(fid)) yield return (mod, fid);
            foreach (int fid in manifest.PublicFunctors)
                if (Fresh(fid)) yield return (mod, fid);
            if (_precompiledModuleLocals.TryGetValue(mod, out var bundleLocals))
                foreach (int fid in bundleLocals)
                    if (Fresh(fid)) yield return (mod, fid);
        }
    }

    /// <summary>Whether <paramref name="module"/> DEFINES the functor — the
    /// membership form of <see cref="DefinedModulePredicates"/>.</summary>
    internal bool ModuleDefinesFunctor(string module, int fid)
    {
        foreach (var (_, f) in DefinedModulePredicates(module))
            if (f == fid) return true;
        return false;
    }

    /// <summary>The module <paramref name="module"/> imports the functor
    /// from, or null — one hop of the import table, for the M:X viewpoint
    /// resolution (predicate_property's imported_from, clause's view).</summary>
    internal string? ModuleImportSource(string module, int fid)
        => _modules.TryGetValue(module, out ModuleManifest? m)
           && m.Imports.TryGetValue(fid, out string? src)
            ? src : null;

    /// <summary>Static clauses with head functor <paramref name="fid"/> in
    /// ONE module — <see cref="StaticClausesFor"/> restricted to
    /// <paramref name="module"/>, for the qualified <c>clause(M:H, B)</c>.</summary>
    internal IEnumerable<Clause> StaticClausesInModule(string module, int fid)
    {
        if (!_modules.TryGetValue(module, out ModuleManifest? manifest)) yield break;
        foreach (var c in manifest.Clauses)
        {
            if (c.Kind == Shumway.Compiler.Ast.ClauseKind.Directive) continue;
            if (ConsultPipeline.HeadFunctorIdOf(c) == fid) yield return c;
        }
    }

    /// <summary>The consult-direct bare-call fallback (the agreed top-level
    /// semantics, uniform across REPL / web / embedding). Runs only where a
    /// bare goal is otherwise about to raise <c>existence_error</c>, so it
    /// can never shadow a builtin, a bare-global public, a dynamic (an
    /// <c>assertz</c>-created one included) or a <c>user</c> import.
    /// Resolves to the ONE directly consulted explicit module that defines
    /// the name as a mangled local; two candidates throw the ambiguity
    /// existence_error naming both. Returns the code address, or -1.</summary>
    internal int ResolveDirectConsultLocal(Activation engine, int fid)
    {
        if (_directlyConsultedModules.Count == 0) return -1;
        if (_dynStore.IsDynamic(fid)) return -1;
        var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fid);
        string? name = Shumway.Core.AtomTable.GetById(atomId)?.Name;
        // '$' anywhere: engine/transform internals and already-mangled
        // spellings — neither participates in the convenience.
        if (name is not { Length: > 0 } || name.IndexOf('$') >= 0) return -1;
        string? found = null;
        List<string>? candidates = null;
        foreach (string mod in _directlyConsultedModules)
        {
            if (!_modules.TryGetValue(mod, out ModuleManifest? m)) continue;
            if (!GetModuleMangledSet(mod, m).Contains(fid)) continue;
            found ??= mod;
            (candidates ??= new List<string>()).Add(mod);
        }
        if (found is null) return -1;
        if (candidates!.Count > 1)
        {
            candidates.Sort(StringComparer.Ordinal);
            throw Shumway.Core.PrologRuntimeException.AmbiguousModuleLocal(fid, candidates);
        }
        int mangledFid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(found + "$" + name, permanent: true).Id, arity);
        if (engine.CurrentFunctorAddresses is { } map
            && map.TryGetValue(mangledFid, out int addr)
            && !Shumway.Core.CallTarget.IsUnresolved(addr)
            && addr >= 0)
            return addr;
        return -1;
    }

    /// <summary>True when every recorded static Module:Goal resolution of a
    /// cached transform still resolves the same today — the per-entry
    /// revalidation that lets an unrelated module load reuse the transform
    /// verbatim (a version-counter key re-transformed every qualified-goal
    /// user per consult of the clpz load chain).</summary>
    internal bool QualifiedResolutionsStillValid(
        Dictionary<(string Mod, string Name, int Arity), string?>? resolutions)
    {
        if (resolutions is null) return true;
        foreach (var (key, resolved) in resolutions)
            if (ResolveQualifiedStatic(key.Mod, key.Name, key.Arity) != resolved)
                return false;
        return true;
    }

    /// <summary>Compile-time resolution of a statically written
    /// <c>Module:Goal</c> body goal — mirrors the runtime PrepareMqualGoal
    /// chain exactly: the module's mangled definitions → its import table →
    /// the bare name (own legacy publics, globals, builtins, prelude,
    /// dynamics). Returns <c>null</c> when the module isn't loaded (keep the
    /// runtime ':'/2 dispatch; a later load makes
    /// <see cref="QualifiedResolutionsStillValid"/> re-transform the
    /// caller).</summary>
    internal string? ResolveQualifiedStatic(string module, string name, int arity)
    {
        if (!_modules.TryGetValue(module, out ModuleManifest? m)) return null;
        int fid = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(name, permanent: true).Id, arity);
        if (_dynStore.IsDynamic(fid)) return name;   // dynamics are flat-global
        if (GetModuleMangledSet(module, m).Contains(fid)) return module + "$" + name;
        if (m.Imports.TryGetValue(fid, out string? src)) return src + "$" + name;
        return name;
    }

    // The functors module `m` links under its mangled name: clause heads
    // (minus legacy publics — those stay bare) plus an export-qualified
    // module's exports, plus a precompiled bundle's locals. A dynamic fid in
    // the set is harmless: ResolveQualifiedStatic's dynamic check runs FIRST,
    // so the fingerprint doesn't need to track dynamic promotions.
    private HashSet<int> GetModuleMangledSet(string moduleName, ModuleManifest m)
    {
        if (_moduleMangledCache.TryGetValue(moduleName, out var e)
            && ReferenceEquals(e.ClausesRef, m.Clauses)
            && e.ClauseCount == m.Clauses.Count
            && e.PublicCount == m.PublicFunctors.Count
            && e.ExportCount == m.ExportFunctors.Count)
            return e.Mangled;
        var set = new HashSet<int>();
        foreach (var c in m.Clauses)
        {
            if (c.Kind is Shumway.Compiler.Ast.ClauseKind.Directive) continue;
            set.Add(ConsultPipeline.HeadFunctorIdOf(c));
        }
        if (m.IsExportQualified) set.UnionWith(m.ExportFunctors);
        else set.ExceptWith(m.PublicFunctors);
        if (_precompiledModuleLocals.TryGetValue(moduleName, out var bundleLocals))
            set.UnionWith(bundleLocals);
        _moduleMangledCache[moduleName] =
            (m.Clauses, m.Clauses.Count, m.PublicFunctors.Count,
             m.ExportFunctors.Count, set);
        return set;
    }

    private static string IndicatorList(List<int> fids)
    {
        const int cap = 8;
        var parts = new List<string>(Math.Min(fids.Count, cap));
        for (int i = 0; i < fids.Count && i < cap; i++)
        {
            var (atomId, arity) = Shumway.Core.FunctorTable.Lookup(fids[i]);
            parts.Add($"{Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "?"}/{arity}");
        }
        string joined = string.Join(", ", parts);
        return fids.Count > cap ? $"{joined} (+{fids.Count - cap} more)" : joined;
    }

    // The module name if it names an export-qualified module (ADR-038), else null
    // — a legacy bare-global module contributes no import entries.
    private string? ExportQualifiedNameOrNull(string? moduleName) =>
        moduleName is not null
        && _modules.TryGetValue(moduleName, out ModuleManifest? m)
        && m.IsExportQualified
            ? moduleName
            : null;

    internal int _nativeBlockConsultSeq;
    // the engine's monotonic synthesized-helper sequence: every
    // consult/assert transform on this engine draws unique helper ids, so a
    // second consult's `$disj_N` can never collide with the first's in the same
    // module. Per-engine (not global) so the atom space stays bounded across
    // engines/processes; the query stub uses the reserved `$q` prefix instead.
    private int _metaHelperSeq;
    internal int NextMetaHelperId() => ++_metaHelperSeq;

    /// <summary>A bundle's <see cref="Shumway.Compiler.Parsing.MetaTransform"/>
    /// helpers (<c>$disj_N</c> / <c>$neg_N</c> / <c>$once_N</c> / …) were
    /// numbered by ShmoCompiler's per-module 0-based counter at compile time —
    /// a counter this engine's runtime <see cref="NextMetaHelperId"/> knows
    /// nothing about. But a bundled module's DYNAMIC clauses are re-transformed
    /// at query setup with <see cref="NextMetaHelperId"/>, which also starts
    /// low, so a dynamic clause's helper can mint the SAME mangled functor id
    /// (e.g. <c>clpz$$disj_253</c>) as a compiled STATIC helper. The static-link
    /// partition adds the query-setup (dynamic) predicate first, so it shadows
    /// the bundled static body — a caller then reaches the wrong helper (the bug
    /// that broke clpz narrowing's bounded-domain if-then-else). Advancing the
    /// runtime counter past every bundled helper id keeps the two number ranges
    /// disjoint — the same guarantee the live/JIT path gets for free by
    /// numbering every helper (static and dynamic) from one counter.</summary>
    internal void ObserveBundleHelperId(int functorId)
    {
        var (atomId, _) = Shumway.Core.FunctorTable.Lookup(functorId);
        string name = Shumway.Core.AtomTable.GetById(atomId)?.Name ?? "";
        // MetaTransform helper names are `<prefix>$<kind>_<id>` (module mangling
        // prepends `mod$`). The id is the trailing `_<digits>` and is preceded
        // by a `$<kind>_` marker — required so a plain user predicate whose name
        // happens to end in `_<digits>` is not mistaken for a helper.
        int lastUnderscore = name.LastIndexOf('_');
        if (lastUnderscore <= 0 || lastUnderscore == name.Length - 1) return;
        int dollar = name.LastIndexOf('$', lastUnderscore);
        if (dollar < 0 || dollar >= lastUnderscore - 1) return;
        if (!int.TryParse(name.AsSpan(lastUnderscore + 1), out int id)) return;
        if (id > _metaHelperSeq) _metaHelperSeq = id;
    }


    /// <summary>ADR-025 — enables the inline if-then-else lowering: an eligible
    /// plain-goal <c>(C -&gt; T ; E)</c> / <c>(A ; B)</c> compiles INSIDE the host
    /// clause (get_level; try_me_else; cut; jump) instead of a synthesized
    /// 2-clause helper reached by a Call. STATIC consult paths only — the
    /// runtime assert path always uses the helper form. Default OFF (stage (c)
    /// of the ADR-025 rollout): a predicate with an inline ITE is not yet
    /// Tier-1-promotable (the IL compiler rejects the shape gracefully and it
    /// stays on Tier-0), so flipping this on trades Tier-1 eligibility for the
    /// Tier-0 win until the ADR's stage (b) lands. Set before consulting.</summary>
    public bool EnableInlineIte { get; set; }
}

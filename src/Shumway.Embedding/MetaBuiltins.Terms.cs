using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    /// <summary><c>atom_to_term(Atom, Term, Bindings)</c> — parses
    /// <c>Atom</c>'s text as a Prolog term, unifies the result with
    /// <c>Term</c>, and unifies <c>Bindings</c> with a list of
    /// <c>'='(Name, Var)</c> compounds for each named variable.</summary>
    public static bool AtomToTerm(Activation engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));
        if (atomCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(IsoError.TypeError("atom", new VarTerm("_")));
        string source = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        if (!source.TrimEnd().EndsWith(".", StringComparison.Ordinal))
            // Space before the dot: a source ending in a graphic-char atom
            // (`*`, `*/`, `.+`) would otherwise fuse the terminator into the
            // atom (`*.` lexes as one atom, not `*` + end), so the clause reads
            // with no terminator dot. The space keeps the dot a real end token.
            source += " .";

        Term parsed = ParseClauseText(engine, source);

        // Collect variable names from the parsed term in first-occurrence
        // order. Materialise the term once on the heap so each unique name
        // resolves to one shared heap cell, then read back each var's
        // heap-bound value for the bindings list.
        var names = new List<string>();
        var seen = new HashSet<string>();
        CollectNamedVarsFromTerm(parsed, names, seen);

        // Bindings vars must BE the term's vars (SWI contract — and what
        // singleton computation and read_term_from_chars build on). Build
        // '=(Name, Var)' pairs whose Var slots share names with the parsed
        // term, then materialise term and pairs TOGETHER so the
        // Materializer's varMap resolves each name to one shared heap cell.
        // (Materialising the term separately first handed register 1 a copy
        // DETACHED from the bindings — the trap this comment guards.)
        var pairs = new List<Term>(names.Count);
        foreach (string name in names)
        {
            pairs.Add(new CompoundTerm("=", new Term[]
            {
                new AtomTerm(name),
                new VarTerm(name),
            }));
        }
        Term wrapper = new CompoundTerm("$pair", new Term[]
        {
            parsed,
            BuildListTerm(pairs),
        });
        Cell wrapCell = Materializer.MaterializeAsCell(engine, wrapper);
        // wrapCell is Cell.Ref to STR for $pair/2. Args at strBase+2 and +3.
        int wrapBase = wrapCell.AsHeapIndex;
        if (!engine.UnifyRegisterWithHeapAt(1, wrapBase + 2)) return false;
        return engine.UnifyRegisterWithHeapAt(2, wrapBase + 3);
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
    /// visible flag. The parser-visible set is
    /// <c>double_quotes</c> with values <c>codes</c>, <c>chars</c>,
    /// <c>atom</c>, or <c>string</c>; other flags raise a domain error.
    /// Setting <c>double_quotes</c> takes effect for the next parse —
    /// either a query, an <c>assertz</c> of a clause carrying a string
    /// literal, or a <c>:- consult</c> reading more source.</summary>
    public static bool SetPrologFlag(Activation engine)
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

        string flagName = AtomTable.GetById(flagCell.AsAtomId)?.Name ?? "";

        // Checked before the atom rule below, which every other flag follows.
        if (flagName == "answer_max_depth")
        {
            if (valueCell.Tag != Tag.Int)
                throw new ShumwayPrologException(
                    IsoError.TypeError("integer", new VarTerm("_")));
            if (valueCell.AsInt < 0)
                throw new ShumwayPrologException(
                    IsoError.DomainError("not_less_than_zero", new IntTerm(valueCell.AsInt)));
            host.Flags.AnswerMaxDepth = (int)System.Math.Min(valueCell.AsInt, int.MaxValue);
            return true;
        }

        if (valueCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(
                IsoError.TypeError("atom", new VarTerm("_")));

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
        if (flagName == "library_dialect")
        {
            // ADR-040 — the preferred dialect for resolving an ambiguous
            // use_module(library(X)). Distinct from the read-only ISO `dialect`
            // flag (which reports our identity, "shumway").
            try { host.SetLibraryDialect(valueName); }
            catch (System.ArgumentException)
            {
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            }
            return true;
        }
        if (flagName == "unknown")
        {
            if (valueName != "error" && valueName != "fail" && valueName != "warning")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.Unknown = valueName;
            // take effect mid-query: dispatch reads the
            // live engine's OnUnknown, not the host flags.
            engine.OnUnknown = valueName switch
            {
                "fail" => Shumway.Core.UnknownAction.Fail,
                "warning" => Shumway.Core.UnknownAction.Warning,
                _ => Shumway.Core.UnknownAction.Error,
            };
            return true;
        }
        if (flagName == "arity_compat")
        {
            // Arity/Prolog32 compatibility mode. The parse-time
            // features ($...$ atoms, #line, directive annotations) apply to
            // SUBSEQUENT consults; the ClauseReader's directive pre-pass
            // handles a mid-file flip.
            if (valueName != "true" && valueName != "false")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.ArityCompat = valueName == "true";
            if (valueName == "true")
            {
                // Arity call semantics: undefined predicates FAIL. An
                // explicit set_prolog_flag(unknown, _) afterwards overrides.
                host.Flags.Unknown = "fail";
                engine.OnUnknown = Shumway.Core.UnknownAction.Fail;
            }
            return true;
        }
        if (flagName == "occurs_check")
        {
            if (valueName != "false" && valueName != "true" && valueName != "error")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.OccursCheck = valueName;
            return true;
        }
        if (flagName == "implicit_dynamic")
        {
            if (valueName != "true" && valueName != "false")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.ImplicitDynamic = valueName == "true";
            return true;
        }
        if (flagName == "prefer_rationals")
        {
            // ADR-039 — takes effect immediately on the running activation too,
            // so `set_prolog_flag(prefer_rationals, true), X is 1/3` is rational
            // within one query.
            if (valueName != "true" && valueName != "false")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.PreferRationals = engine.PreferRationals = valueName == "true";
            return true;
        }
        if (flagName == "compile_mode")
        {
            if (valueName != "debug" && valueName != "release")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.EmitDebugInfo = host.Flags.DebugCodegen = valueName == "debug";
            return true;
        }
        if (flagName == "debug_lco")
        {
            // ADR-035 — last-call optimisation for debug-compiled code. Takes
            // effect immediately, on the running activation as well as on later
            // queries: flipping it is the whole point (a debugger does it from
            // the Immediate window mid-session).
            if (valueName != "on" && valueName != "off")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.DebugLco = valueName == "on";
            engine.LastCallOptimisation = host.Flags.DebugLco;
            return true;
        }
        if (flagName == "char_conversion")
        {
            // ISO §7.11.2.1 — whether read-time character conversion
            // (the table) is applied.
            if (valueName != "on" && valueName != "off")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.CharConversionEnabled = valueName == "on";
            return true;
        }
        if (flagName == "debug")
        {
            // ISO §7.11.2.2 — Shumway has no interactive debugger, but
            // the flag itself is required; accepted and stored.
            if (valueName != "on" && valueName != "off")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.Debug = valueName == "on";
            return true;
        }

        // a flag that exists but is not user-
        // settable raises permission_error(modify, flag, F) (ISO
        // §8.17.1.3 c); only a flag that does not exist at all raises
        // domain_error(prolog_flag, F).
        switch (flagName)
        {
            case "bounded":
            case "max_integer":
            case "min_integer":
            case "integer_rounding_function":
            case "max_arity":
            case "dialect":
            case "argv":
                throw new ShumwayPrologException(IsoError.PermissionError(
                    "modify", "flag", new AtomTerm(flagName)));
        }

        throw new ShumwayPrologException(
            IsoError.DomainError("prolog_flag", new AtomTerm(flagName)));
    }

    /// <summary><c>current_prolog_flag(Flag, Value)</c> — reads a flag's
    /// current value. Recognised flags:
    /// <list type="bullet">
    /// <item><c>double_quotes</c> — parser-visible (set via
    /// <c>set_prolog_flag</c>).</item>
    /// <item><c>argv</c> — command-line argument list (list of atoms),
    /// populated by the host.</item>
    /// <item><c>dialect</c> — <c>shumway</c>.</item>
    /// <item><c>bounded</c> — <c>false</c> (Shumway has BigInt).</item>
    /// <item><c>integer_rounding_function</c> — <c>toward_zero</c>.</item>
    /// <item><c>unknown</c>, <c>occurs_check</c> — informational
    /// flag state (engine doesn't yet vary behaviour on them).</item>
    /// <item><c>max_arity</c> — large integer constant.</item>
    /// <item><c>version_data</c> — <c>shumway(Major, Minor, Patch, [])</c>,
    /// the engine version (the GProlog/SWI convention consumers like the
    /// Logtalk adapter query).</item>
    /// </list>
    /// With Flag unbound, every flag is enumerated on backtracking
    /// (ISO §8.17.2).</summary>
    public static bool CurrentPrologFlag(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "current_prolog_flag/2 requires the engine to be hosted by a PrologEngine.");

        Cell flagCell = ResolveLocal(engine, engine.GetRegister(0));
        if (flagCell.Tag == Tag.Ref)
        {
            // Unbound flag — enumerate every readable flag as a
            // backtrackable cursor (the stream_property/2 pattern).
            int returnPc = engine.BuiltinReturnPc;
            return IndexEnumCursor.Start(
                engine, EnumerableFlags.Length, 2, returnPc,
                (e, i) => PrologFlagUnify(e, host, i));
        }
        if (flagCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(
                IsoError.TypeError("atom", new VarTerm("_")));
        string flagName = AtomTable.GetById(flagCell.AsAtomId)?.Name ?? "";

        switch (flagName)
        {
            case "double_quotes":
                return UnifyAtom(engine, 1, host.Flags.DoubleQuotes switch
                {
                    Shumway.Compiler.Parsing.DoubleQuotesMode.Codes  => "codes",
                    Shumway.Compiler.Parsing.DoubleQuotesMode.Chars  => "chars",
                    Shumway.Compiler.Parsing.DoubleQuotesMode.Atom   => "atom",
                    _ => "string",
                });

            case "argv":
            {
                // Build a Prolog list-of-atoms via the standard
                // term materialiser, then unify with register 1.
                Term listTerm = BuildAtomList(host.Flags.Argv);
                Cell listCell = Materializer.MaterializeAsCell(engine, listTerm);
                return engine.UnifyRegisterWithCell(1, listCell);
            }

            case "dialect":
                return UnifyAtom(engine, 1, "shumway");

            case "pid":   // read-only, as in SWI; same value pid/1 reports
                return engine.UnifyRegisterWithCell(1,
                    Cell.Int(System.Environment.ProcessId));

            // SWI's platform flags: each EXISTS only on its platform (reading
            // it elsewhere fails silently), which is what portable code's
            // `( current_prolog_flag(windows, true) -> ... ; ... )` relies on.
            case "windows":
                return OperatingSystem.IsWindows() && UnifyAtom(engine, 1, "true");
            case "unix":
                return (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                        || OperatingSystem.IsFreeBSD())
                    && UnifyAtom(engine, 1, "true");
            case "apple":
                return OperatingSystem.IsMacOS() && UnifyAtom(engine, 1, "true");

            case "version_data":
            {
                Term versionTerm = new CompoundTerm("shumway", new Term[]
                {
                    new IntTerm(PrologEngine.VersionMajor),
                    new IntTerm(PrologEngine.VersionMinor),
                    new IntTerm(PrologEngine.VersionPatch),
                    new AtomTerm("[]"),
                });
                Cell versionCell = Materializer.MaterializeAsCell(engine, versionTerm);
                return engine.UnifyRegisterWithCell(1, versionCell);
            }

            case "library_dialect":   // ADR-040 — preferred shim dialect
                return UnifyAtom(engine, 1, host.ActiveLibraryDialect ?? "auto");

            case "bounded":
                return UnifyAtom(engine, 1, "false");

            case "integer_rounding_function":
                return UnifyAtom(engine, 1, "toward_zero");

            case "unknown":
                return UnifyAtom(engine, 1, host.Flags.Unknown);

            case "occurs_check":
                return UnifyAtom(engine, 1, host.Flags.OccursCheck);

            case "implicit_dynamic":
                return UnifyAtom(engine, 1, host.Flags.ImplicitDynamic ? "true" : "false");

            case "prefer_rationals":
                return UnifyAtom(engine, 1, host.Flags.PreferRationals ? "true" : "false");

            case "answer_max_depth":
                return engine.UnifyRegisterWithCell(1, Cell.Int(host.Flags.AnswerMaxDepth));

            case "arity_compat":
                return UnifyAtom(engine, 1, host.Flags.ArityCompat ? "true" : "false");

            case "compile_mode":
                return UnifyAtom(engine, 1, host.Flags.EmitDebugInfo ? "debug" : "release");

            case "debug_lco":   // ADR-035
                return UnifyAtom(engine, 1, host.Flags.DebugLco ? "on" : "off");

            case "char_conversion":
                return UnifyAtom(engine, 1, host.Flags.CharConversionEnabled ? "on" : "off");

            case "debug":
                return UnifyAtom(engine, 1, host.Flags.Debug ? "on" : "off");

            case "max_arity":
                // ISO requires this be either an integer or
                // unbounded. Shumway's WAM register layout limits
                // arity to fit in a uint16; pick a comfortably large
                // value here.
                return engine.UnifyRegisterWithCell(1, Cell.Int(255));

            default:
                return false;
        }
    }

    /// <summary>The flags <c>current_prolog_flag/2</c> enumerates when
    /// its first argument is unbound. Every readable flag appears here;
    /// the value is produced by the same bound-name switch.</summary>
    private static readonly string[] EnumerableFlags =
    {
        "bounded", "max_arity", "integer_rounding_function",
        "double_quotes", "unknown", "occurs_check", "char_conversion",
        "debug", "dialect", "library_dialect", "version_data", "argv", "pid",
        "implicit_dynamic", "arity_compat",
        "compile_mode", "debug_lco", "prefer_rationals", "answer_max_depth",
    };

    private static bool PrologFlagUnify(Activation engine, PrologEngine host, int idx)
    {
        if (!UnifyAtom(engine, 0, EnumerableFlags[idx])) return false;
        // Register 0's variable is now bound to the flag name, so
        // re-entering the builtin takes the bound-name switch and
        // unifies register 1 with the flag's value.
        return CurrentPrologFlag(engine);
    }

    /// <summary>Constructs the AST Term for a Prolog list whose
    /// elements are atoms drawn from <paramref name="items"/>.
    /// Right-leaning <c>.</c>/2 cons-cell shape, terminated by
    /// <c>[]</c>.</summary>
    private static Term BuildAtomList(IReadOnlyList<string> items)
    {
        Term acc = new AtomTerm("[]");
        for (int i = items.Count - 1; i >= 0; i--)
            acc = new CompoundTerm(".", new Term[] { new AtomTerm(items[i]), acc });
        return acc;
    }

    private static bool UnifyAtom(Activation engine, int register, string name)
    {
        int aid = AtomTable.Intern(name, permanent: true).Id;
        return engine.UnifyRegisterWithCell(register, Cell.Atom(aid));
    }

    /// <summary><c>statistics(?Key, ?Value)</c> — timing/resource statistics,
    /// the SWI/Scryer/GNU idiom. Supported keys:
    /// <list type="bullet">
    /// <item><c>runtime</c> / <c>walltime</c> — <c>[Total_ms, SinceLast_ms]</c>;
    /// <c>runtime</c> is process CPU time, <c>walltime</c> is wall-clock. The
    /// second element is the elapsed time since the previous call with the same
    /// key, so <c>statistics(runtime, _), Goal, statistics(runtime, [_, T])</c>
    /// times <c>Goal</c>.</item>
    /// <item><c>cputime</c> / <c>process_cputime</c> — CPU time in seconds
    /// (float).</item>
    /// <item><c>real_time</c> — wall-clock seconds since the engine started
    /// (float).</item>
    /// </list>
    /// An unrecognised key unifies with <c>[0, 0]</c> (lenient, so a program
    /// probing several keys keeps working).</summary>
    public static bool Statistics2(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "statistics/2 requires the engine to be hosted by a PrologEngine.");
        Cell keyCell = ResolveLocal(engine, engine.GetRegister(0));
        if (keyCell.Tag == Tag.Ref)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (keyCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(
                IsoError.TypeError("atom", new VarTerm("_")));
        string key = AtomTable.GetById(keyCell.AsAtomId)?.Name ?? "";
        switch (key)
        {
            case "runtime":
            {
                long total = PrologEngine.StatsRuntimeMs();
                return UnifyMsPair(engine, total, host.StatsTakeRuntimeDelta(total));
            }
            case "walltime":
            {
                long total = host.StatsWalltimeMs();
                return UnifyMsPair(engine, total, host.StatsTakeWalltimeDelta(total));
            }
            case "cputime":
            case "process_cputime":
                return engine.UnifyRegisterWithCell(1, Materializer.MaterializeAsCell(
                    engine, new FloatTerm(PrologEngine.StatsRuntimeMs() / 1000.0)));
            case "real_time":
                return engine.UnifyRegisterWithCell(1, Materializer.MaterializeAsCell(
                    engine, new FloatTerm(host.StatsWalltimeMs() / 1000.0)));
            default:
                return UnifyMsPair(engine, 0, 0);
        }
    }

    /// <summary><c>statistics/0</c> — writes a short report of where the time
    /// and the memory went, to the current output.
    ///
    /// <para>The counters are the RUNNING activation's, which is the only place
    /// they exist: an engine's heap and trails belong to the query in progress.
    /// So this reports what the query calling it is using at that moment —
    /// which is what someone typing <c>statistics.</c> after a run wants to
    /// know.</para></summary>
    public static bool Statistics0(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "statistics/0 requires the engine to be hosted by a PrologEngine.");

        long runtime = PrologEngine.StatsRuntimeMs();
        var report = new System.Text.StringBuilder();
        report.Append("Runtime:   ").Append(Seconds(runtime))
              .Append(" sec  (").Append(runtime).Append(" ms)\n");
        report.Append("Walltime:  ").Append(Seconds(host.StatsWalltimeMs()))
              .Append(" sec\n");
        report.Append("Heap:      ").Append(Count(engine.HeapTop)).Append(" cells in use of ")
              .Append(Count(engine.HeapCapacity)).Append(" (")
              .Append(Count((long)engine.HeapCapacity * 8 / 1024)).Append(" KB)\n");
        // ADR-004 — two trails, reported as the two they are.
        report.Append("Trail:     ").Append(Count(engine.BindingTrailTop))
              .Append(" bindings, ").Append(Count(engine.ExtraTrailTop)).Append(" other\n");
        report.Append("Stack:     ").Append(Count(engine.StackTop)).Append(" words in use of ")
              .Append(Count(engine.StackCapacity)).Append('\n');

        host.Out.Write(report.ToString());
        return true;

        static string Seconds(long ms) => (ms / 1000.0).ToString("0.000",
            System.Globalization.CultureInfo.InvariantCulture);
        // Grouped, because these numbers are read rather than computed with.
        static string Count(long n) => n.ToString("N0",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool UnifyMsPair(Activation engine, long total, long sinceLast)
    {
        Term list = new CompoundTerm(".", new Term[]
        {
            new IntTerm(total),
            new CompoundTerm(".", new Term[] { new IntTerm(sinceLast), new AtomTerm("[]") }),
        });
        return engine.UnifyRegisterWithCell(1, Materializer.MaterializeAsCell(engine, list));
    }

    /// <summary><c>prolog_load_context(?Key, ?Value)</c> — SWI/Scryer load-context
    /// introspection, the way a <c>term_expansion</c>/<c>goal_expansion</c> hook
    /// reads the module it is expanding for (the module is NOT a hook argument).
    /// Keys: <c>module</c> (the module being loaded), <c>file</c> / <c>source</c>
    /// (its path), <c>directory</c> (its directory). Fails outside a consult, or
    /// for a key whose value is unknown.</summary>
    public static bool PrologLoadContext2(Activation engine)
    {
        if (engine.Host is not PrologEngine host) return false;
        Term keyTerm = RegisterMarshalling.ReadRegisterAsTerm(engine, 0);
        if (keyTerm is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (keyTerm is not AtomTerm key)
            throw new ShumwayPrologException(
                IsoError.TypeError("atom", keyTerm));
        // ADR-044: the file/directory answers are paths, so canonical form.
        string? value = key.Name switch
        {
            "module" => host._currentLoadModule,
            "file" or "source" => host._currentLoadFile is { } f
                ? PrologPath.ToCanonical(f) : null,
            "directory" => host._consultBaseDir is { } d
                ? PrologPath.ToCanonicalDirectory(d) : null,
            _ => null,
        };
        if (value is null) return false;
        int aid = AtomTable.Intern(value, permanent: true).Id;
        return engine.UnifyRegisterWithCell(1, Cell.Atom(aid));
    }

    /// <summary><c>absolute_file_name(+FileSpec, -Absolute)</c> —
    /// resolves a file path to an absolute one. The basic 2-arg
    /// form: <c>FileSpec</c> must be a bound atom or PSTR; the
    /// result is the absolute path as an atom. Internally just
    /// <see cref="Path.GetFullPath(string)"/>, so the resolution
    /// is relative to the current working directory of the host
    /// process.
    ///
    /// <para>ADR-038: a <c>library(X)</c> spec resolves through the engine's
    /// library search path (the same one <c>use_module(library(X))</c> uses) to
    /// the absolute path of <c>X.pl</c> / <c>X.shum</c>; an unresolved alias
    /// fails.</para>
    ///
    /// <para>Not supported: SWI's 3-arg form with options
    /// (<c>extensions</c>, <c>file_type</c>, <c>access</c>) — those need a small
    /// option parser. Add when a program actually needs them.</para></summary>
    public static bool AbsoluteFileName2(Activation engine)
    {
        // ADR-038 — resolve a library(X) alias off the search path.
        if (RegisterMarshalling.ReadRegisterAsTerm(engine, 0)
                is CompoundTerm { Functor: "library", Args: [AtomTerm libName] })
        {
            if (engine.Host is PrologEngine libHost
                && libHost.TryResolveLibrary(libName.Name, out string libPath))
            {
                int laid = AtomTable.Intern(libPath, permanent: true).Id;
                return engine.UnifyRegisterWithCell(1, Cell.Atom(laid));
            }
            return false;
        }
        if (!TryGetStringArg(engine, 0, out string spec))
            return false;
        try
        {
            string absolute = Path.GetFullPath(spec);
            // ADR-044: a directory answers with its trailing separator, so the
            // result can have a file name concatenated onto it.
            absolute = Directory.Exists(absolute)
                ? PrologPath.ToCanonicalDirectory(absolute)
                : PrologPath.ToCanonical(absolute);
            int aid = AtomTable.Intern(absolute, permanent: true).Id;
            return engine.UnifyRegisterWithCell(1, Cell.Atom(aid));
        }
        catch (ArgumentException)
        {
            throw new ShumwayPrologException(
                IsoError.DomainError("source_sink", new AtomTerm(spec)));
        }
        catch (PathTooLongException)
        {
            throw new ShumwayPrologException(
                IsoError.RepresentationError("max_path_length"));
        }
    }

    /// <summary><c>file_name_extension(?Base, ?Ext, ?Full)</c> —
    /// SWI / SICStus shape. Two productive modes:
    /// <list type="bullet">
    /// <item><c>Full</c> bound → split at the last '.', unify
    /// <c>Base</c> with everything before and <c>Ext</c> with
    /// everything after. A file with no '.' produces
    /// <c>Ext = ''</c> and <c>Base = Full</c>.</item>
    /// <item><c>Base</c> and <c>Ext</c> bound → compose as
    /// <c>Base + '.' + Ext</c>, or just <c>Base</c> when <c>Ext</c>
    /// is the empty atom.</item>
    /// </list>
    /// Other combinations raise <c>instantiation_error</c>.</summary>
    public static bool FileNameExtension3(Activation engine)
    {
        Cell baseCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell extCell = ResolveLocal(engine, engine.GetRegister(1));
        Cell fullCell = ResolveLocal(engine, engine.GetRegister(2));

        // Decompose mode: Full is bound, compute Base + Ext.
        if (fullCell.Tag == Tag.Atom)
        {
            string full = AtomTable.GetById(fullCell.AsAtomId)?.Name ?? "";
            int dot = full.LastIndexOf('.');
            string @base, ext;
            if (dot < 0)
            {
                @base = full;
                ext = "";
            }
            else
            {
                @base = full[..dot];
                ext = full[(dot + 1)..];
            }
            return UnifyAtom(engine, 0, @base) && UnifyAtom(engine, 1, ext);
        }

        // Compose mode: Base and Ext bound, build Full.
        if (baseCell.Tag == Tag.Atom && extCell.Tag == Tag.Atom)
        {
            string @base = AtomTable.GetById(baseCell.AsAtomId)?.Name ?? "";
            string ext = AtomTable.GetById(extCell.AsAtomId)?.Name ?? "";
            string full = ext.Length == 0 ? @base : @base + "." + ext;
            return UnifyAtom(engine, 2, full);
        }

        // At least one side underspecified.
        throw new ShumwayPrologException(IsoError.InstantiationError());
    }

    /// <summary><c>is_digit(+Char)</c> — true when <c>Char</c> is a
    /// one-character atom whose code is an ASCII digit. SICStus and
    /// older SWI versions ship it; Blint.pl uses it inside
    /// number-parsing helpers.</summary>
    public static bool IsDigit1(Activation engine)
    {
        Cell cell = ResolveLocal(engine, engine.GetRegister(0));
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (cell.Tag != Tag.Atom) return false;
        string name = AtomTable.GetById(cell.AsAtomId)?.Name ?? "";
        return name.Length == 1 && name[0] >= '0' && name[0] <= '9';
    }

    /// <summary><c>working_directory(?Old, +New)</c> — SWI form.
    /// Unifies <c>Old</c> with the current working directory (as
    /// an atom); when <c>New</c> is bound and different, changes
    /// the host process's CWD to it. The idiomatic read-only call
    /// is <c>working_directory(D, D)</c>.</summary>
    public static bool WorkingDirectory2(Activation engine)
    {
        // ADR-044: canonical form, ending in '/' (SWI's convention too).
        string oldCwd = PrologPath.ToCanonicalDirectory(Directory.GetCurrentDirectory());
        int oldAid = AtomTable.Intern(oldCwd, permanent: true).Id;
        if (!engine.UnifyRegisterWithCell(0, Cell.Atom(oldAid)))
            return false;

        if (!TryGetStringArg(engine, 1, out string newCwd))
            throw new ShumwayPrologException(IsoError.InstantiationError());
        // Compare in canonical form: the caller may hand back either what we
        // returned or the same directory in native form.
        string newCanonical = PrologPath.ToCanonicalDirectory(newCwd);
        if (newCanonical != oldCwd)
        {
            try { Directory.SetCurrentDirectory(newCwd); }
            catch (DirectoryNotFoundException)
            {
                throw new ShumwayPrologException(
                    IsoError.ExistenceError("source_sink", new AtomTerm(newCwd)));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                    || ex is IOException)
            {
                throw new ShumwayPrologException(
                    IsoError.PermissionError("open", "source_sink", new AtomTerm(newCwd)));
            }
        }
        return true;
    }

    /// <summary>Reads register <paramref name="register"/> as a
    /// string: a bound atom (its name) or a PSTR (its content).
    /// Unbound → instantiation error; other types → type_error.</summary>
    private static bool TryGetStringArg(Activation engine, int register, out string value)
    {
        value = "";
        Cell cell = ResolveLocal(engine, engine.GetRegister(register));
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (cell.Tag == Tag.Atom)
        {
            value = AtomTable.GetById(cell.AsAtomId)?.Name ?? "";
            return true;
        }
        // PSTR: materialise the register through MaterializeRegister
        // (which derefs and reads the heap header). Atoms are already
        // special-cased above; this path handles PSTR and any
        // foreign-object-as-string the reader knows about.
        Term materialised = MaterializeRegister(engine, register);
        if (materialised is StringTerm s)
        {
            value = s.Content;
            return true;
        }
        if (materialised is AtomTerm a)
        {
            value = a.Name;
            return true;
        }
        throw new ShumwayPrologException(
            IsoError.TypeError("atom", new VarTerm("_")));
    }

    /// <summary><c>op(Precedence, Type, Name)</c> — runtime operator
    /// declaration. Mirrors the <c>:- op(...)</c> directive but takes
    /// effect immediately for subsequent parses (queries, asserted
    /// clauses, read_term_from_atom). Errors mirror ISO: instantiation
    /// when any arg is unbound, type_error when one's the wrong shape.</summary>
    public static bool Op(Activation engine)
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
            ValidateOpDefine(host, name, precedence, opType);
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
                ValidateOpDefine(host, name, precedence, opType);
                host.DefineOperator(name, precedence, opType);
                cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
            }
            return true;
        }
        throw new ShumwayPrologException(IsoError.TypeError("atom_or_list", new VarTerm("_")));
    }

    /// <summary>ISO §8.14.3.3 (+ Cor.2) op/3 permission rules: <c>','</c> is
    /// untouchable; <c>'|'</c> may only be infix with priority &gt; 1000 (or
    /// removed); <c>'[]'</c>/<c>'{}'</c> can never be operators; and no atom
    /// may be both an infix and a postfix operator.</summary>
    private static void ValidateOpDefine(
        PrologEngine host, string name, int precedence,
        Shumway.Compiler.Parsing.OperatorType opType)
    {
        bool isInfix = opType is Shumway.Compiler.Parsing.OperatorType.Xfx
            or Shumway.Compiler.Parsing.OperatorType.Xfy
            or Shumway.Compiler.Parsing.OperatorType.Yfx;
        bool isPostfix = opType is Shumway.Compiler.Parsing.OperatorType.Xf
            or Shumway.Compiler.Parsing.OperatorType.Yf;
        if (name == ",")
            throw new ShumwayPrologException(
                IsoError.PermissionError("modify", "operator", new AtomTerm(",")));
        if (name == "|" && (!isInfix || (precedence != 0 && precedence <= 1000)))
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "operator", new AtomTerm("|")));
        if (name is "[]" or "{}")
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "operator", new AtomTerm(name)));
        if (precedence != 0 && isInfix
            && host.Operators.TryGetPostfix(name, out _, out _))
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "operator", new AtomTerm(name)));
        if (precedence != 0 && isPostfix
            && host.Operators.TryGetInfix(name, out _, out _))
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "operator", new AtomTerm(name)));
    }

    /// <summary><c>current_op(?Priority, ?Type, ?Name)</c> — ISO §8.17.3.
    /// Enumerates every defined operator on backtracking, with any of
    /// the three args optionally constraining the search. Uses the
    /// standard PushBuiltinChoicePoint pattern for the multi-solution
    /// dispatch.</summary>
    public static bool CurrentOp(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "current_op/3 requires the engine to be hosted by a PrologEngine.");

        // Snapshot the current operator set so backtracking iteration
        // sees a stable view even if op/3 mutates the table mid-enum.
        var ops = host.EnumerateOperators().ToArray();
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, ops.Length, 3, returnPc,  // arity 3 (current_op/3)
            (e, i) => CurrentOpUnify(e, ops, i));
    }

    private static bool CurrentOpUnify(
        Activation engine,
        (int Precedence, Shumway.Compiler.Parsing.OperatorType Type, string Name)[] ops,
        int idx)
    {
        var (prec, type, name) = ops[idx];
        string typeName = type switch
        {
            Shumway.Compiler.Parsing.OperatorType.Fx => "fx",
            Shumway.Compiler.Parsing.OperatorType.Fy => "fy",
            Shumway.Compiler.Parsing.OperatorType.Xf => "xf",
            Shumway.Compiler.Parsing.OperatorType.Yf => "yf",
            Shumway.Compiler.Parsing.OperatorType.Xfx => "xfx",
            Shumway.Compiler.Parsing.OperatorType.Xfy => "xfy",
            Shumway.Compiler.Parsing.OperatorType.Yfx => "yfx",
            _ => "?",
        };

        if (!engine.UnifyRegisterWithCell(0, Cell.Int(prec))) return false;
        if (!engine.UnifyRegisterWithCell(1,
                Cell.Atom(AtomTable.Intern(typeName, permanent: true).Id))) return false;
        if (!engine.UnifyRegisterWithCell(2,
                Cell.Atom(AtomTable.Intern(name, permanent: true).Id))) return false;
        return true;
    }

    /// <summary><c>char_conversion(+InChar, +OutChar)</c> — ISO §8.14.9.
    /// Updates the engine's char-conversion table on
    /// <see cref="PrologFlags.CharConversion"/> with a one-character
    /// mapping. An identity mapping (<c>InChar == OutChar</c>) removes
    /// the entry. Both arguments must be one-character atoms (chunk
    /// 152).</summary>
    public static bool CharConversion(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "char_conversion/2 requires the engine to be hosted by a PrologEngine.");
        Cell inCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell outCell = ResolveLocal(engine, engine.GetRegister(1));
        if (inCell.Tag == Tag.Ref)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (outCell.Tag == Tag.Ref)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (inCell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException("type_error", "character");
        if (outCell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException("type_error", "character");
        string inName = AtomTable.GetById(inCell.AsAtomId)?.Name ?? "";
        string outName = AtomTable.GetById(outCell.AsAtomId)?.Name ?? "";
        if (inName.Length != 1)
            throw new Shumway.Core.PrologRuntimeException("type_error", "character");
        if (outName.Length != 1)
            throw new Shumway.Core.PrologRuntimeException("type_error", "character");
        char ic = inName[0], oc = outName[0];
        if (ic == oc) host.Flags.CharConversion.Remove(ic);
        else host.Flags.CharConversion[ic] = oc;
        return true;
    }

    /// <summary><c>current_char_conversion(?InChar, ?OutChar)</c> —
    /// ISO §8.14.10. Enumerates the active char-conversion table on
    /// backtracking. The Phase-9 PushBuiltinChoicePoint pattern drives
    /// the multi-solution dispatch.</summary>
    public static bool CurrentCharConversion(Activation engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "current_char_conversion/2 requires the engine to be hosted by a PrologEngine.");
        // Snapshot so backtracking sees a stable view.
        var entries = host.Flags.CharConversion
            .Select(kv => (In: kv.Key, Out: kv.Value))
            .ToArray();
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, entries.Length, 2, returnPc,  // arity 2 (current_char_conversion/2)
            (e, i) => CurrentCharConversionUnify(e, entries, i));
    }

    private static bool CurrentCharConversionUnify(
        Activation engine, (char In, char Out)[] entries, int idx)
    {
        var (ic, oc) = entries[idx];
        int inAtomId = AtomTable.Intern(ic.ToString(), permanent: true).Id;
        int outAtomId = AtomTable.Intern(oc.ToString(), permanent: true).Id;
        if (!engine.UnifyRegisterWithCell(0, Cell.Atom(inAtomId))) return false;
        if (!engine.UnifyRegisterWithCell(1, Cell.Atom(outAtomId))) return false;
        return true;
    }

    /// <summary><c>read_term_from_atom(Atom, Term)</c> — parses the text
    /// stored in <c>Atom</c> as a Prolog term and unifies the result with
    /// <c>Term</c>. The full ISO <c>read_term/2</c> reads from an
    /// arbitrary stream — this handles only the in-memory atom case,
    /// which is the use the embedding API actually needs.</summary>
    public static bool ReadTermFromAtom(Activation engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));
        if (atomCell.Tag != Tag.Atom)
            throw new ShumwayPrologException(IsoError.TypeError("atom", new VarTerm("_")));
        string source = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        if (!source.TrimEnd().EndsWith(".", StringComparison.Ordinal))
            // Space before the dot: a source ending in a graphic-char atom
            // (`*`, `*/`, `.+`) would otherwise fuse the terminator into the
            // atom (`*.` lexes as one atom, not `*` + end), so the clause reads
            // with no terminator dot. The space keeps the dot a real end token.
            source += " .";
        Term parsed = ParseClauseText(engine, source);
        Cell parsedCell = Materializer.MaterializeAsCell(engine, parsed);
        return engine.UnifyRegisterWithCell(1, parsedCell);
    }

    /// <summary><c>read_term_from_atom(+Atom, -Term, +Options)</c> —
    /// SWI / GProlog compat. The options list is accepted but
    /// currently ignored (no read-time options affect the parser
    /// yet).</summary>
    public static bool ReadTermFromAtom3(Activation engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));
        if (atomCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
        string source = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
        if (!source.TrimEnd().EndsWith(".", StringComparison.Ordinal))
            // Space before the dot: a source ending in a graphic-char atom
            // (`*`, `*/`, `.+`) would otherwise fuse the terminator into the
            // atom (`*.` lexes as one atom, not `*` + end), so the clause reads
            // with no terminator dot. The space keeps the dot a real end token.
            source += " .";
        Term parsed = ParseClauseText(engine, source);
        Cell parsedCell = Materializer.MaterializeAsCell(engine, parsed);
        return engine.UnifyRegisterWithCell(1, parsedCell);
    }

    /// <summary><c>name(?AtomOrNumber, ?Codes)</c> — old-style GProlog
    /// bidirectional conversion. With first arg bound, builds the
    /// list of character codes for its print form. With second arg
    /// bound, tries to parse the codes as a number first; on
    /// parse-failure interns as an atom.</summary>
    public static bool NameBuiltin(Activation engine)
    {
        Cell firstCell = ResolveLocal(engine, engine.GetRegister(0));
        if (firstCell.Tag == Tag.Atom)
        {
            string name = AtomTable.GetById(firstCell.AsAtomId)?.Name ?? "";
            return UnifyCodesList(engine, regOut: 1, name);
        }
        if (firstCell.Tag == Tag.Int)
        {
            string s = firstCell.AsInt.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return UnifyCodesList(engine, regOut: 1, s);
        }
        if (firstCell.Tag == Tag.Float)
        {
            double v = Cell.DecodeFloat(firstCell, engine.GetHeap(firstCell.FloatPairedIndex));
            string s = Shumway.Builtins.Number.FormatPrologFloat(v);
            return UnifyCodesList(engine, regOut: 1, s);
        }
        if (firstCell.Tag == Tag.Ref)
        {
            // Read the codes list and decide: numeric → number, else atom.
            string s = ReadCodesAsString(engine, engine.GetRegister(1));
            if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long iv))
                return engine.UnifyRegisterWithCell(0, Cell.Int(iv));
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double dv))
                return engine.UnifyRegisterWithHeapAt(0, engine.MakeFloat(dv));
            int atomId = AtomTable.Intern(s, permanent: false).Id;
            return engine.UnifyRegisterWithCell(0, Cell.Atom(atomId));
        }
        throw new PrologRuntimeException("type_error", "atomic");
    }

    private static bool UnifyCodesList(Activation engine, int regOut, string text)
    {
        if (text.Length == 0)
        {
            return engine.UnifyRegisterWithCell(regOut,
                Cell.Atom(AtomTable.EmptyListId));
        }
        int baseIdx = engine.AllocateHeap(2 * text.Length + 1);
        for (int i = 0; i < text.Length; i++)
        {
            int lisIdx = baseIdx + 2 * i;
            int headIdx = lisIdx + 1;
            engine.SetHeap(lisIdx, Cell.Lis(headIdx));
            engine.SetHeap(headIdx, Cell.Int(text[i]));
        }
        engine.SetHeap(baseIdx + 2 * text.Length, Cell.Atom(AtomTable.EmptyListId));
        return engine.UnifyRegisterWithHeapAt(regOut, baseIdx);
    }

    private static string ReadCodesAsString(Activation engine, Cell codesCell)
    {
        var sb = new System.Text.StringBuilder();
        Cell cur = ResolveLocal(engine, codesCell);
        while (cur.Tag == Tag.Lis)
        {
            Cell head = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex));
            if (head.Tag != Tag.Int)
                throw new PrologRuntimeException("type_error", "character_code");
            // BMP-only, as char_code/2 (truncating builds another char).
            if (head.AsInt < 0 || head.AsInt > char.MaxValue)
                throw new PrologRuntimeException(
                    "representation_error", "character_code");
            sb.Append((char)head.AsInt);
            cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list");
        return sb.ToString();
    }

    /// <summary><c>get_time(-Time)</c> — current wall-clock time in
    /// seconds since the Unix epoch, as a float. SWI-compat.</summary>
    public static bool GetTime(Activation engine)
    {
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .TotalSeconds;
        int idx = engine.MakeFloat(now);
        return engine.UnifyRegisterWithHeapAt(0, idx);
    }

}

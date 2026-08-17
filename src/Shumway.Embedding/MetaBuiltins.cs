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
public static partial class MetaBuiltins
{
    private static int _initialized;

    public static void EnsureRegistered()
    {
        if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        const string Control = "Control";
        const string Database = "Database";
        const string Term = "Term inspection & construction";
        const string Reflect = "Flags, operators & reflection";
        const string Io = "Input / output";

        // findall/3 is a prelude predicate (live-engine collect loop over
        // call/1), NOT a builtin — the old isolated-sub-engine builtin lacked
        // the parent's bundle-precompiled definitions (a source-stripped
        // bundle's module-local goal was absent from the sub-engine) and hid
        // the goal's side effects. See Prelude findall/3. A statically-callable
        // findall/3 is still rewritten inline by MetaTransform to the same
        // $findall_* loop; this covers the runtime variable-goal case.
        // In-engine findall plumbing — MetaTransform rewrites
        // findall/3 with a callable goal into a goal sequence using these.
        BuiltinsRegistry.Register("$findall_push",    0, FindallPush);
        BuiltinsRegistry.Register("$findall_record",  1, FindallRecord);
        BuiltinsRegistry.Register("$te_after",        1, TeAfter);
        BuiltinsRegistry.Register("$findall_record_s", 1, FindallRecordSnapshot);
        BuiltinsRegistry.Register("$findall_collect", 1, FindallCollect);
        // In-engine bagof/setof plumbing — reuse the findall
        // frame stack ('$findall_push' / '$findall_record'); only the
        // collect step differs (it groups the solutions by witness).
        BuiltinsRegistry.Register("$bagof_collect",   1, BagofCollect);
        BuiltinsRegistry.Register("$setof_collect",   1, SetofCollect);
        // bagof/3 & setof/3 variable-goal fallbacks are prelude predicates
        // (live-engine findall + fail-on-empty), NOT builtins — the old
        // isolated-sub-engine builtins lacked the parent's bundle-precompiled
        // definitions. A statically-callable bagof/setof is still rewritten by
        // MetaTransform with full witness grouping. See Prelude bagof/3, setof/3.
        // forall/2 is a prelude predicate (live-engine \+ (call(C), \+ call(A))),
        // not a builtin — the old isolated-sub-engine builtin hid the called
        // goals' side effects. See Prelude forall/2.
        BuiltinsRegistry.Register("copy_term", 2, CopyTerm,
            Term, "copy_term(+Term, -Copy)", "Copies a term with fresh variables.");
        // Scryer system builtin (iso_ext's copy_term_nat/2 wraps it): a copy
        // where attributed variables come out as fresh PLAIN variables — which
        // is exactly what HeapTermCopy-backed copy_term/2 produces.
        BuiltinsRegistry.Register("$copy_term_without_attr_vars", 2, CopyTerm);
        BuiltinsRegistry.Register("$copy_term_3_prep", 3, CopyTerm3Prep);
        BuiltinsRegistry.Register("$dbg_fix_foreign", 1, DbgFixForeign);
        // SWI-shim helper builtins (bare-global internals; the public SWI names
        // nb_setarg/nb_linkarg/same_term live in the swi shim library).
        BuiltinsRegistry.Register("$nb_setarg", 3, NbSetArg);
        BuiltinsRegistry.Register("$same_term", 2, SameTerm);
        BuiltinsRegistry.Register("term_attvars", 2, TermAttvars,
            Term, "term_attvars(+Term, -Vars)",
            "Unifies Vars with the attributed variables reachable from Term.");
        BuiltinsRegistry.Register("$dif_check", 3, DifCheck);
        BuiltinsRegistry.Register("$attv_snapshot", 1, AttvSnapshot);
        // call_with_timeout/2,3 live in the prelude; these carry the deadline.
        BuiltinsRegistry.Register("$timeout_push", 1, TimeoutPush);
        BuiltinsRegistry.Register("$timeout_pop", 0, TimeoutPop);
        BuiltinsRegistry.Register("$attv_new_since", 2, AttvNewSince);
        BuiltinsRegistry.Register("?=", 2, DecidedUnify,
            Term, "?=(@X, @Y)",
            "Succeeds if the (in)equality of X and Y is already decided (identical, or cannot unify).");
        BuiltinsRegistry.Register("unifiable", 3, Unifiable,
            Term, "unifiable(@X, @Y, -Unifier)",
            "If X and Y unify, Unifier is the list of V=Value bindings that make them equal; else fails.");

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
        BuiltinsRegistry.Register("call", 8, Call8,
            Control, "call(:Goal, +Extra1, ..., +Extra7)", "Calls a goal extended with seven extra arguments (ISO requires call/2..8).");
        // '$call'/2: a cut-barrier-carrying meta-call. The
        // $call_* control helpers re-enter call dispatch through it so a
        // `!` in a runtime compound goal cuts to the enclosing call's
        // barrier. Like call/N it is intercepted by the interpreter.
        BuiltinsRegistry.Register("$call", 2, CallWithBarrier);
        BuiltinsRegistry.Register("repeat", 0, Repeat,
            Control, "repeat",
            "Succeeds, and succeeds again on every backtrack — an unbounded choice point.");

        // ADR-022 item 1 — the embedded-native-block dispatcher. The native
        // transform rewrites a captured block to `'$native_run'('$nb$…', V1..Vk)`;
        // this fixed builtin (one impl, registered for every arity the var count
        // can take) reads the block name from register 0, looks the block up in the
        // engine's table, and runs it with the variables in registers 1.. . Using a
        // fixed builtin + a name argument (rather than a per-block synthesized
        // builtin) keeps the bytecode reference portable cross-process — the
        // CompiledModuleCodec round-trips the name, and there is no per-block id to
        // keep consistent between compile and load.
        // Arity 1..65 → up to 64 Prolog variables per block (name + V1..V64).
        // NativeTransform raises a clear consult-time error above 64, so a
        // too-big block can never surface as a bewildering runtime
        // existence_error($native_run/N). Corpus max is well under 32.
        for (int arity = 1; arity <= 65; arity++)
            BuiltinsRegistry.Register("$native_run", arity, NativeRun);

        // ADR-024 — generic-term interop (the reftype tier). A reftype/preftype is
        // a zero-copy cursor (a TermSlot wrapped as a Foreign cell) over a real
        // Prolog term. These intrinsics replace the prlg_ifce.pl definitions
        // (recognized by name at consult); the .NET interop side reads/builds the
        // term through ReftypeApi / TermSlot.
        BuiltinsRegistry.Register("$new_reftype_slot", 1, NewReftypeSlot);
        BuiltinsRegistry.Register("fill_par", 2, FillPar);
        BuiltinsRegistry.Register("reftype_term", 2, ReftypeTerm);
        BuiltinsRegistry.Register("preftype", 1, Preftype);
        // The /3 forms carry an extra type-tag argument (arg 1) that the cursor
        // model doesn't need — the slot knows its own shape. Provided for sources
        // that call them directly; the builtin ignores the tag.
        BuiltinsRegistry.Register("reftype_term", 3, ReftypeTerm3);
        BuiltinsRegistry.Register("fill_reftype", 3, FillReftype3);
        BuiltinsRegistry.Register("quote_str", 2, QuoteStr);
        // ADR-024 — Arity string-conversion predicates. In Shumway an Arity
        // "string" is an atom and there are no C buffers, so these are the
        // identities the user gave:
        //   make_prolog_string(String, String)  :- atom(String), !.
        //   make_c_string(String, _, String, _) :- atom(String), !.
        BuiltinsRegistry.Register("make_prolog_string", 2, MakePrologString2);
        BuiltinsRegistry.Register("make_prolog_string_c", 2, MakePrologString2);
        BuiltinsRegistry.Register("make_c_string", 4, MakeCString4);

        BuiltinsRegistry.Register("assertz", 1, Assertz,
            Database, "assertz(+Clause)", "Adds a clause to the end of its dynamic predicate.");
        BuiltinsRegistry.Register("asserta", 1, Asserta,
            Database, "asserta(+Clause)", "Adds a clause to the front of its dynamic predicate.");
        // 'assert/1' is the historical name; ISO and SWI
        // both accept it as a synonym for assertz/1.
        BuiltinsRegistry.Register("assert",  1, Assertz,
            Database, "assert(+Clause)", "Synonym for assertz/1 (historical SWI/GProlog name).");
        // chain GC for retracted clauses (ADR-015 follow-up).
        BuiltinsRegistry.Register("garbage_collect_clauses", 0, GarbageCollectClauses0,
            Database, "garbage_collect_clauses",
            "Re-threads every dynamic predicate's chain to skip retracted clauses (ADR-015).");
        BuiltinsRegistry.Register("garbage_collect_clauses", 1, GarbageCollectClauses1,
            Database, "garbage_collect_clauses(+Name/Arity)",
            "Re-threads the named predicate's chain to skip retracted clauses.");
        BuiltinsRegistry.Register("compact_dynamic_buffer", 0, CompactDynamicBuffer,
            Database, "compact_dynamic_buffer",
            "Invalidates the persistent dynamic-code buffer so "
            + "the next query rebuilds it from current _dynamicClauses. Reclaims memory "
            + "consumed by appended-but-now-unreachable chain entries from many "
            + "in-place assertz / asserta / retract cycles, at the cost of one "
            + "re-link of the dynamic region on the next query.");
        BuiltinsRegistry.Register("compact_dynamic_buffer", 1, CompactDynamicBuffer1,
            Database, "compact_dynamic_buffer(+Name/Arity)",
            "Per-predicate hint variant. Validates Name/Arity "
            + "names a dynamic predicate, then triggers the same full rebuild as the "
            + "0-arg form. The single buffer holds every dynamic predicate's bytecode "
            + "interleaved, so independent per-predicate reclamation isn't currently "
            + "feasible without partial-relink support — the API surface is per-predicate "
            + "for forward compatibility.");
        BuiltinsRegistry.Register("retract", 1, Retract,
            Database, "retract(+Clause)", "Removes the first clause that unifies with the argument.");
        BuiltinsRegistry.Register("$retractall_modifiable", 1, RetractAllModifiable);
        // ADR-016: reachability-based heap garbage collection. Runs as a
        // goal (a safe point — all structures complete and rooted in
        // registers / Y slots / choice points). Always succeeds.
        BuiltinsRegistry.Register("garbage_collect", 0, GarbageCollect,
            Control, "garbage_collect",
            "Mark-compacts the heap, reclaiming cells unreachable from the live "
            + "machine state (ADR-016). Always succeeds.");

        // Opt-in Tier-1 warm-up. Bundle load is lazy — a predicate promotes to
        // IL once its invocation counter crosses the threshold. compile_all
        // front-loads that for a program that will run enough to want it.
        BuiltinsRegistry.Register("compile_all", 0, CompileAll0,
            Control, "compile_all",
            "Eagerly compiles every compilable static predicate to Tier-1 IL "
            + "now, instead of waiting for each to promote lazily on use. For a "
            + "program that will do enough queries to want the whole set hot up "
            + "front (a server warming up). No-op when Tier-1 is disabled or "
            + "under Native AOT. Always succeeds.");
        BuiltinsRegistry.Register("compile_all", 1, CompileAll1,
            Control, "compile_all(-Count)",
            "As compile_all/0, unifying Count with the number of predicates "
            + "newly compiled to Tier-1 IL by this call.");

        // ADR-035 — the four-port tracer.
        BuiltinsRegistry.Register("trace", 0, Trace,
            Control, "trace",
            "Turns on the four-port tracer: from here on, every goal prints a line "
            + "at its call, exit, redo and fail ports. Takes effect immediately, "
            + "including for the goals remaining in the current query.");
        BuiltinsRegistry.Register("notrace", 0, NoTrace,
            Control, "notrace", "Turns the four-port tracer off.");

        BuiltinsRegistry.Register("debugger_break", 0, DebuggerBreak,
            Control, "debugger_break",
            "Stops in the attached source-level debugger, here, with this clause's stack "
            + "and variables. Succeeds without doing anything if no debugger is attached, "
            + "so it is safe to leave in a program. Requires the code to have been compiled "
            + "for debugging (shumway --debug).");

        BuiltinsRegistry.Register("throw", 1, Throw,
            Control, "throw(+Exception)", "Throws an exception term, unwinding to the nearest catch/3.");
        // catch/3 is a prelude predicate built on the catch-frame
        // plumbing ($catch_begin/$catch_end), not a builtin — the old isolated-
        // sub-engine builtin hid the guarded goal's side effects. MetaTransform
        // rewrites a statically-callable catch/3 inline to the same shape; the
        // prelude clause is the runtime fallback for a variable Goal/Recovery.
        BuiltinsRegistry.Register("$catch_begin", 2, CatchBegin);
        BuiltinsRegistry.Register("$catch_end",   0, CatchEnd);
        // setup_call_cleanup/3 cleanup-handler primitives.
        BuiltinsRegistry.Register("$scc_register", 1, SccRegister);
        BuiltinsRegistry.Register("$cp_owners", 0, CpOwners);
        BuiltinsRegistry.Register("$scc_forget", 1, SccForget);
        BuiltinsRegistry.Register("$pop_pending_cleanup", 1, PopPendingCleanup);

        // clause/2 and current_predicate/1 are now Prolog-level predicates
        // defined in the prelude. They call these helpers to
        // bridge into the engine's clause and functor stores, then iterate
        // via the prelude's member/2.
        BuiltinsRegistry.Register("$all_clauses_of",            2, AllClausesOf);
        BuiltinsRegistry.Register("$clause_enum",               2, ClauseEnum);
        BuiltinsRegistry.Register("$all_predicate_indicators",  1, AllPredicateIndicators);
        BuiltinsRegistry.Register("$current_predicate_enum",    1, CurrentPredicateEnum);
        BuiltinsRegistry.Register("$listable_predicates", 1, ListablePredicates);
        // listing path bypasses clause/2 + write/1 to
        // preserve the original VarTerm names parser captured. The
        // clause/2 path materialises through the heap, where every
        // unbound var picks up a synthetic _G<addr> name and the
        // user's "X" or "Acc" is lost.
        BuiltinsRegistry.Register("$listing_pred_source", 2, ListingPredSource);
        // SWI / SICStus / GNU-style clause pretty-printer.
        BuiltinsRegistry.Register("portray_clause", 1, PortrayClause1,
            Io, "portray_clause(+Clause)",
            "Pretty-prints Clause to the current output as a Prolog clause: head + indented body goals, "
            + "synthetic variable names renamed to A, B, C, ...");
        BuiltinsRegistry.Register("portray_clause", 2, PortrayClause2,
            Io, "portray_clause(+Stream, +Clause)",
            "Like portray_clause/1 but writes to the given stream.");
        // Tabling — a per-engine string set giving the
        // semi-naive driver an O(1) "is this answer new?" test.
        BuiltinsRegistry.Register("$tbl_seen", 1, TableSeen);
        // Tabling — table invalidation and tabled negation.
        BuiltinsRegistry.Register("$tbl_seen_clear", 0, TableSeenClear);
        BuiltinsRegistry.Register("$tbl_solve_complete", 1, TableSolveComplete);
        BuiltinsRegistry.Register("abolish",                    1, Abolish,
            Database, "abolish(+PredicateIndicator)", "Removes every clause of the named dynamic predicate.");

        BuiltinsRegistry.Register("numbervars",        3, NumberVars,
            Term, "numbervars(+Term, +Start, -End)", "Binds the unbound variables of Term to '$VAR'(N) terms with consecutive N from Start.");
        BuiltinsRegistry.Register("numbervars",        4, NumberVars4,
            Term, "numbervars(+Term, +Start, -End, +Options)", "As numbervars/3 with an (accepted, ignored) SWI option list.");
        BuiltinsRegistry.Register("term_variables",    2, TermVariables,
            Term, "term_variables(+Term, -Variables)",
            "Unifies Variables with the list of distinct unbound variables of Term, in first-occurrence (depth-first, left-to-right) order (ISO §8.5.5).");
        BuiltinsRegistry.Register("term_to_atom",      2, TermToAtom,
            Term, "term_to_atom(?Term, ?Atom)", "Converts between a term and its textual atom representation.");
        BuiltinsRegistry.Register("term_string",       2, TermString,
            Term, "term_string(?Term, ?String)", "Converts between a term and its textual string representation (SWI).");
        BuiltinsRegistry.Register("term_string",       3, TermString,
            Term, "term_string(?Term, ?String, +Options)", "As term_string/2 with an (accepted, ignored) SWI option list.");

        BuiltinsRegistry.Register("functor", 3, Functor,
            Term, "functor(?Term, ?Name, ?Arity)", "Relates a term to its functor name and arity.");
        BuiltinsRegistry.Register("compound_name_arity", 3, CompoundNameArity,
            Term, "compound_name_arity(?Compound, ?Name, ?Arity)", "Like functor/3 but restricted to compound terms (arity >= 1) (SWI).");
        BuiltinsRegistry.Register("arg",     3, Arg,
            Term, "arg(+N, +Term, ?Arg)", "Unifies Arg with the Nth argument of the compound term.");
        BuiltinsRegistry.Register("is_stream", 1, IsStream,
            Reflect, "is_stream(@Term)", "Succeeds if Term is a stream handle or a registered stream alias (SWI).");
        BuiltinsRegistry.Register("=..",     2, Univ,
            Term, "=..(?Term, ?List)", "Relates a term to the list of its functor and arguments.");

        BuiltinsRegistry.Register("read_term_from_atom", 2, ReadTermFromAtom,
            Term, "read_term_from_atom(+Atom, -Term)", "Parses an atom into a term.");
        // SWI/GProlog compat — /3 takes an options list
        // that we currently accept but ignore (no options affect the
        // parser yet).
        BuiltinsRegistry.Register("read_term_from_atom", 3, ReadTermFromAtom3,
            Term, "read_term_from_atom(+Atom, -Term, +Options)",
            "Parses an atom into a term; Options accepted for SWI/GProlog compat (currently ignored).");

        // GProlog name/2 — atom/number ↔ list of codes.
        BuiltinsRegistry.Register("name", 2, NameBuiltin,
            Term, "name(?AtomOrNumber, ?Codes)",
            "Bidirectional conversion between an atom/number and its character-code list.");

        // SWI global variables.
        const string Globals = "Global variables";
        BuiltinsRegistry.Register("nb_setval", 2, Shumway.Builtins.GlobalVarsBuiltins.NbSetval,
            Globals, "nb_setval(+Key, +Value)",
            "Non-backtrackable global variable assignment.");
        BuiltinsRegistry.Register("nb_getval", 2, Shumway.Builtins.GlobalVarsBuiltins.NbGetval,
            Globals, "nb_getval(+Key, -Value)",
            "Reads a non-backtrackable global variable; existence_error if unset.");
        BuiltinsRegistry.Register("nb_current", 2, Shumway.Builtins.GlobalVarsBuiltins.NbCurrent,
            Globals, "nb_current(?Key, ?Value)",
            "Enumerates global variables; fails for an unset Key (no throw).");
        BuiltinsRegistry.Register("b_setval", 2, Shumway.Builtins.GlobalVarsBuiltins.BSetval,
            Globals, "b_setval(+Key, +Value)",
            "Backtrackable global variable assignment: the previous value is restored on backtracking.");
        BuiltinsRegistry.Register("b_getval", 2, Shumway.Builtins.GlobalVarsBuiltins.BGetval,
            Globals, "b_getval(+Key, -Value)",
            "Reads a backtrackable global variable; existence_error if unset.");
        // flag/3 — a separate namespace from the global-variable store, with an
        // integer default of 0 and an atomic read-modify-write.
        BuiltinsRegistry.Register("flag", 3, Shumway.Builtins.FlagBuiltins.Flag3,
            Globals, "flag(+Key, ?Old, +New)",
            "Unifies Old with the flag's value (0 if unset), then sets it to New (an arithmetic expression is evaluated). Not backtracked.");
        BuiltinsRegistry.Register("set_flag", 2, Shumway.Builtins.FlagBuiltins.SetFlag2,
            Globals, "set_flag(+Key, +Value)",
            "Sets a flag to Value (an arithmetic expression is evaluated), discarding the old value.");
        BuiltinsRegistry.Register("get_flag", 2, Shumway.Builtins.FlagBuiltins.GetFlag2,
            Globals, "get_flag(+Key, -Value)",
            "Reads a flag's value (0 if never set).");
        // Scryer's global-variable primitives — what iso_ext.pl's bb_put/2,
        // bb_b_put/2 and bb_get/2 lower to (clpz drives its propagation-queue
        // state through them). Same store as nb_/b_ above; the backtrackable
        // flavor shares b_setval's current non-trailed stub.
        BuiltinsRegistry.Register("$store_global_var", 2,
            Shumway.Builtins.GlobalVarsBuiltins.NbSetval,
            Globals, "'$store_global_var'(+Key, +Value)",
            "Scryer primitive behind bb_put/2: non-backtrackable global assignment.");
        BuiltinsRegistry.Register("$store_backtrackable_global_var", 2,
            Shumway.Builtins.GlobalVarsBuiltins.BSetval,
            Globals, "'$store_backtrackable_global_var'(+Key, +Value)",
            "Scryer primitive behind bb_b_put/2: backtrackable global assignment.");
        BuiltinsRegistry.Register("$fetch_global_var", 2,
            Shumway.Builtins.GlobalVarsBuiltins.FetchGlobalVar,
            Globals, "'$fetch_global_var'(+Key, -Value)",
            "Scryer primitive behind bb_get/2: reads a global variable, failing (not throwing) when unset.");

        // SWI time builtins (minimal — get_time as float
        // epoch seconds, stamp_date_time as a single date_time
        // compound with the local-time components).
        const string Time = "Time";
        BuiltinsRegistry.Register("get_time", 1, GetTime,
            Time, "get_time(-Time)",
            "Current wall-clock time in seconds since the Unix epoch (a float).");
        BuiltinsRegistry.Register("stamp_date_time", 3, StampDateTime,
            Time, "stamp_date_time(+Stamp, -DateTime, +TimeZone)",
            "Converts a Unix-epoch stamp to a date(Y,M,D,H,Mi,S,Off,Tz,DST) term.");

        BuiltinsRegistry.Register("current_op", 3, CurrentOp,
            Reflect, "current_op(?Priority, ?Type, ?Name)",
            "Enumerates the operator table; backtracks over every operator (ISO §8.17.3).");
        BuiltinsRegistry.Register("char_conversion", 2, CharConversion,
            Reflect, "char_conversion(+InChar, +OutChar)",
            "Registers a one-character-to-one-character mapping the lexer applies "
            + "to the start of each unquoted token (ISO §8.14.9). InChar == OutChar removes the entry.");
        BuiltinsRegistry.Register("current_char_conversion", 2, CurrentCharConversion,
            Reflect, "current_char_conversion(?InChar, ?OutChar)",
            "Enumerates the active char-conversion table (ISO §8.14.10).");
        BuiltinsRegistry.Register("op", 3, Op,
            Reflect, "op(+Priority, +Type, +Name)", "Declares an operator of the given priority and type.");
        BuiltinsRegistry.Register("set_prolog_flag",     2, SetPrologFlag,
            Reflect, "set_prolog_flag(+Flag, +Value)", "Sets a Prolog flag.");
        BuiltinsRegistry.Register("current_prolog_flag", 2, CurrentPrologFlag,
            Reflect, "current_prolog_flag(?Flag, ?Value)", "Reads the value of a Prolog flag.");
        BuiltinsRegistry.Register("statistics", 0, Statistics0,
            Reflect, "statistics",
            "Writes a report of runtime, walltime and heap/trail/stack use to the current output.");
        BuiltinsRegistry.Register("statistics", 2, Statistics2,
            Reflect, "statistics(?Key, ?Value)",
            "Timing/resource statistics: runtime/walltime give [Total_ms, SinceLast_ms]; cputime gives seconds.");
        BuiltinsRegistry.Register("predicate_property", 2, PredicateProperty,
            Reflect, "predicate_property(+Head, ?Property)",
            "Enumerates the properties (defined plus one of built_in/dynamic/static) of the predicate named by Head's functor; fails for an undefined predicate.");
        BuiltinsRegistry.Register("module_property", 2, ModuleProperty,
            Reflect, "module_property(?Module, ?Property)",
            "Introspects a loaded module: exports(List) of Name/Arity indicators, or class(user/system/library). Enumerates modules when Module is unbound.");
        // with_output_to/2 itself is a PRELUDE predicate (the goal must run in
        // the LIVE engine so its side effects — op/3, assertz — survive);
        // these are its redirection primitives.
        BuiltinsRegistry.Register("$wot_begin", 1, WotBegin,
            Io, "'$wot_begin'(+Sink)", "Internal: begins a with_output_to capture.");
        BuiltinsRegistry.Register("$wot_end", 1, WotEnd,
            Io, "'$wot_end'(+Sink)", "Internal: ends a with_output_to capture and unifies the sink.");
        BuiltinsRegistry.Register("atom_to_term",   3, AtomToTerm,
            Term, "atom_to_term(+Atom, -Term, -Bindings)", "Parses an atom into a term plus its variable bindings.");
        BuiltinsRegistry.Register("read_term_from_stream", 2, ReadTermFromStream,
            Io, "read_term_from_stream(+Stream, -Term)", "Reads one term from a read-mode stream.");
        BuiltinsRegistry.Register("current_stream",  3, CurrentStream,
            Io, "current_stream(?Filename, ?Mode, ?Stream)",
            "Enumerates open streams (ISO §8.11.8.1).");
        BuiltinsRegistry.Register("stream_property", 2, StreamProperty,
            Io, "stream_property(?Stream, ?Property)",
            "Enumerates (Stream, Property) pairs for every open stream (ISO §8.11.8.2).");
        BuiltinsRegistry.Register("set_stream_position", 2, SetStreamPosition,
            Io, "set_stream_position(+Stream, +Position)",
            "Seeks the stream to the given byte position (ISO §8.11.10).");
        // ISO read_term/2 — accepts a stream handle in arg 1 and unifies
        // the parsed term with arg 2. delegate to the existing
        // stream-aware reader so the builtin set covers both names.
        BuiltinsRegistry.Register("read_term", 2, ReadTermFromStream,
            Io, "read_term(+Stream, -Term)", "Reads one term from a read-mode stream.");
        // ISO read_term/3 — read_term(+Stream, -Term, +Options). Honours the
        // variable_names/1, singletons/1 and variables/1 read options (binding
        // each to a proper list that shares the term's variable cells); other
        // options (double_quotes/1, syntax_error/1, ...) are ignored. Binding
        // singletons/variable_names is required, not cosmetic: a loader that
        // walks an unbound singletons list with member/2 loops forever
        // (Logtalk's linter).
        BuiltinsRegistry.Register("read_term", 3, ReadTermWithOptions,
            Io, "read_term(+Stream, -Term, +Options)",
            "Reads one term from a read-mode stream; honours variable_names/1, singletons/1 and variables/1 options.");
        BuiltinsRegistry.Register("read",      1, Read1,
            Io, "read(-Term)", "Reads one term from current input (ISO §8.14.2).");
        BuiltinsRegistry.Register("read",      2, Read2,
            Io, "read(+Stream, -Term)", "Reads one term from a stream (ISO §8.14.2).");
        BuiltinsRegistry.Register("http_download", 2, HttpDownload,
            Io, "http_download(+URL, +File)",
            "Downloads URL's raw bytes to File (HTTP/HTTPS); a network or "
            + "HTTP failure raises existence_error(url, URL).");
        BuiltinsRegistry.Register("prolog_load_context", 2, PrologLoadContext2,
            Io, "prolog_load_context(?Key, ?Value)",
            "SWI/Scryer load-context introspection (module / file / source / "
            + "directory), used by term_expansion/goal_expansion hooks to read the "
            + "module being loaded. Fails outside a consult.");
        BuiltinsRegistry.Register("absolute_file_name", 2, AbsoluteFileName2,
            Io, "absolute_file_name(+FileSpec, -Absolute)",
            "Resolves a file specification to an absolute path. The basic 2-arg form: "
            + "takes an atom (a path, possibly relative) and unifies the second arg with "
            + "the absolute form. The 3-arg SWI form with options (extensions, file_type, "
            + "access, file_search_path) is not yet supported.");
        BuiltinsRegistry.Register("working_directory", 2, WorkingDirectory2,
            Io, "working_directory(-Old, +New)",
            "Unifies Old with the current working directory; if New differs, changes "
            + "the cwd to it. Use working_directory(D, D) to read without changing.");
        BuiltinsRegistry.Register("prolog_to_os_filename", 2, PrologToOsFilename2,
            Io, "prolog_to_os_filename(?PrologPath, ?OsPath)",
            "Converts between Shumway's canonical '/'-separated path form and the "
            + "host's native form (ADR-044). Either argument may be the bound one; "
            + "on Unix both forms are the same.");
        // Unshadowable alias for shim internals: a loaded library may EXPORT
        // working_directory/2 (Scryer files.pl), and imports win over builtins
        // at resolution — a shim emulation calling the builtin by its public
        // name would loop through the very library it serves.
        BuiltinsRegistry.Register("$sys_working_directory", 2, WorkingDirectory2);
        BuiltinsRegistry.Register("file_name_extension", 3, FileNameExtension3,
            Io, "file_name_extension(?Base, ?Ext, ?Full)",
            "Relates a file name to its base and extension. With Full bound, splits at "
            + "the last '.'; with Base and Ext bound, composes Base + '.' + Ext (or "
            + "just Base when Ext is empty). SWI / SICStus compatible.");
        BuiltinsRegistry.Register("is_digit", 1, IsDigit1,
            Term, "is_digit(+Char)",
            "True when Char is a one-character atom representing an ASCII digit.");

        // consult/1 and reconsult/1. Both route through
        // PrologEngine.ConsultFile: .shum extension goes through
        // LoadBundle, everything else is read as Prolog source and
        // handed to ConsultString. SWI treats reconsult/1 as a synonym
        // for consult/1; we do the same.
        BuiltinsRegistry.Register("consult", 1, Consult,
            Database, "consult(+File)",
            "Loads File and adds its clauses to the database, appending to any "
            + "existing predicates. File is an atom path; a .shum extension routes "
            + "through LoadBundle, everything else is read as Prolog source. An "
            + "extensionless File that does not exist is retried as File.pl "
            + "(SWI-style).");
        BuiltinsRegistry.Register("ensure_loaded", 1, EnsureLoaded,
            Database, "ensure_loaded(+File)",
            "Loads File unless it is already loaded, in which case it does "
            + "nothing (ISO 7.4.2.8). Lets several files each name their own "
            + "dependencies without any of them being loaded twice. A File "
            + "that CHANGED on disk since it was loaded is reloaded. Argument "
            + "and errors are as consult/1.");
        BuiltinsRegistry.Register("use_module", 1, UseModule,
            Database, "use_module(+Spec)",
            "Loads a library or file. Spec is either library(Name) — where Name "
            + "is one of the built-in libraries (clpfd, clpr) — or an atom path "
            + "(equivalent to consult/1). use_module(library(clpfd)) enables the "
            + "CLP(FD) library; use_module(library(clpr)) enables CLP(R). The two "
            + "libraries cannot coexist in the same engine.");
        BuiltinsRegistry.Register("save_state", 1, SaveState1,
            Database, "save_state(+File)",
            "Writes a snapshot of the engine's user-visible state to File. "
            + "Captures every consulted source (in order, minus the prelude) "
            + "plus every currently asserted dynamic clause. The snapshot is "
            + "a Shumway V6 bundle; restore_state/1 reconstitutes equivalent "
            + "state on a fresh engine. Arity-Prolog compatible builtin.");
        BuiltinsRegistry.Register("save_state", 2, SaveState2,
            Database, "save_state(+File, +Options)",
            "Like save_state/1 but accepts an options list. Recognised: "
            + "dynamic_only(true) restricts the snapshot to dynamic clauses "
            + "(no consult history); restore_state/1 then merges them into "
            + "the engine's current state via assertz without resetting.");
        // Arity save/restore — dynamic-database snapshots with destructive
        // REPLACE semantics (distinct from save_state's merge/replay family).
        BuiltinsRegistry.Register("save", 0, Save0,
            Database, "save",
            "Snapshots the current user dynamic database (all dynamic "
            + "predicates' clauses) in memory, replacing any previous save/0 "
            + "snapshot. System-internal ($-prefixed) dynamics are excluded. "
            + "Restore with restore/0. Arity-Prolog compatible builtin.");
        BuiltinsRegistry.Register("save", 1, Save1,
            Database, "save(+File)",
            "Like save/0 but writes the dynamic-database snapshot to File "
            + "(a compact binary only Shumway reads back). Restore with "
            + "restore/1. Arity-Prolog compatible builtin.");
        BuiltinsRegistry.Register("restore", 0, Restore0,
            Database, "restore",
            "Destructively resets the user dynamic database to the last "
            + "save/0 snapshot: every user dynamic predicate's clauses are "
            + "removed (declarations survive - calls fail rather than raise) "
            + "and the snapshot's clauses re-installed. Without a prior "
            + "save/0 the snapshot is empty, so restore/0 just clears all "
            + "user dynamics. Static predicates are never touched. Effects "
            + "are permanent (not undone by backtracking) and visible to "
            + "later goals of the same query. Arity-Prolog compatible.");
        BuiltinsRegistry.Register("restore", 1, Restore1,
            Database, "restore(+File)",
            "restore/0 semantics with the snapshot read from File (written "
            + "by save/1). Raises existence_error if File does not exist. "
            + "Arity-Prolog compatible builtin.");
        // recorded database (Arity-Prolog).
        BuiltinsRegistry.Register("recorda", 3, Recorda3,
            Database, "recorda(+Key, ?Term, -Ref)",
            "Adds Term at the start of the chain stored under Key in the "
            + "recorded database, returning a fresh reference. The recorded DB "
            + "is separate from dynamic predicates: keys are arbitrary terms "
            + "(not functor/arity).");
        BuiltinsRegistry.Register("recordz", 3, Recordz3,
            Database, "recordz(+Key, ?Term, -Ref)",
            "Like recorda/3 but appends Term at the end of the chain under Key.");
        BuiltinsRegistry.Register("recorded", 3, Recorded3,
            Database, "recorded(+Key, ?Term, -Ref)",
            "Enumerates on backtracking the (Term, Ref) pairs stored under Key.");
        BuiltinsRegistry.Register("erase", 1, Erase1,
            Database, "erase(+Ref)",
            "Removes the recorded entry with reference Ref. Fails on an "
            + "unknown / already-erased reference.");
        BuiltinsRegistry.Register("eraseall", 1, EraseAll1,
            Database, "eraseall(+Key)",
            "Removes every recorded entry stored under Key.");
        BuiltinsRegistry.Register("instance", 2, Instance2,
            Database, "instance(+Ref, -Term)",
            "Unifies Term with the term recorded under Ref.");
        BuiltinsRegistry.Register("key_count", 2, KeyCount2,
            Database, "key_count(+Key, -Count)",
            "Unifies Count with the number of recorded entries stored under Key.");
        BuiltinsRegistry.Register("keys", 1, Keys1,
            Database, "keys(?Key)",
            "Enumerates on backtracking every key currently in the recorded database. "
            + "If Key is ground, succeeds iff at least one entry is stored under it.");
        BuiltinsRegistry.Register("ref", 1, Ref1,
            Database, "ref(?X)",
            "Succeeds when X is a live recorded-database reference (an integer "
            + "previously returned by recorda/3 or recordz/3 and not yet erased).");
        BuiltinsRegistry.Register("replace", 2, Replace2,
            Database, "replace(+Ref, +Term)",
            "Replaces the term in the entry with reference Ref. The chain "
            + "position and the reference itself are preserved.");
        BuiltinsRegistry.Register("nref", 2, Nref2,
            Database, "nref(+Ref, -Next)",
            "Unifies Next with the reference of the entry immediately after Ref "
            + "in its key's chain. Fails if Ref is the last entry.");
        BuiltinsRegistry.Register("pref", 2, Pref2,
            Database, "pref(+Ref, -Prev)",
            "Unifies Prev with the reference of the entry immediately before Ref "
            + "in its key's chain. Fails if Ref is the first entry.");
        BuiltinsRegistry.Register("record_after", 3, RecordAfter3,
            Database, "record_after(+Ref, ?Term, -NewRef)",
            "Inserts Term immediately after the entry with reference Ref in "
            + "the same key's chain.");
        BuiltinsRegistry.Register("record_before", 3, RecordBefore3,
            Database, "record_before(+Ref, ?Term, -NewRef)",
            "Inserts Term immediately before the entry with reference Ref in "
            + "the same key's chain.");

        // Edinburgh-style I/O (Arity-Prolog
        // compatible). Layer over the ISO stream registry: see/tell
        // open + set-as-current, seen/told close + revert to user
        // defaults; seeing/telling report the current handle's
        // filename or `user` for user_input/user_output. get/get0/put
        // operate on character codes, like get_code/put_code; skip
        // reads until a target code; tab/2 is the handle-taking form
        // of the prelude's tab/1.
        BuiltinsRegistry.Register("see", 1, See1,
            Io, "see(+File)",
            "Opens File for reading and makes it the current input stream. "
            + "An already-open see-stream is closed first.");
        BuiltinsRegistry.Register("seeing", 1, Seeing1,
            Io, "seeing(?File)",
            "Unifies File with the name of the current input stream's "
            + "file (or `user` when current input is user_input).");
        BuiltinsRegistry.Register("seen", 0, Seen0,
            Io, "seen",
            "Closes the current input stream (if not user_input) and "
            + "reverts current input to user_input.");
        BuiltinsRegistry.Register("tell", 1, Tell1,
            Io, "tell(+File)",
            "Opens File for writing and makes it the current output stream. "
            + "An already-open tell-stream is closed first.");
        BuiltinsRegistry.Register("telling", 1, Telling1,
            Io, "telling(?File)",
            "Unifies File with the name of the current output stream's "
            + "file (or `user` when current output is user_output).");
        BuiltinsRegistry.Register("told", 0, Told0,
            Io, "told",
            "Closes the current output stream (if not user_output) and "
            + "reverts current output to user_output.");
        BuiltinsRegistry.Register("get", 1, Get1,
            Io, "get(?Code)",
            "Reads the next printable character code from the current input "
            + "stream (skipping non-printing codes < 32). EOF returns -1.");
        BuiltinsRegistry.Register("get", 2, Get2,
            Io, "get(+Stream, ?Code)",
            "Stream variant of get/1.");
        BuiltinsRegistry.Register("get0", 1, Get0_1,
            Io, "get0(?Code)",
            "Reads the next character code from the current input stream "
            + "without skipping non-printing codes. EOF returns -1.");
        BuiltinsRegistry.Register("get0", 2, Get0_2,
            Io, "get0(+Stream, ?Code)",
            "Stream variant of get0/1.");
        BuiltinsRegistry.Register("put", 1, Put1,
            Io, "put(+Code)",
            "Writes the character with the given code to the current output "
            + "stream. Edinburgh-style alias of put_code/1.");
        BuiltinsRegistry.Register("put", 2, Put2,
            Io, "put(+Stream, +Code)",
            "Stream variant of put/1.");
        BuiltinsRegistry.Register("skip", 1, Skip1,
            Io, "skip(+Code)",
            "Reads from the current input stream, discarding characters until "
            + "the code Code is read.");
        BuiltinsRegistry.Register("skip", 2, Skip2,
            Io, "skip(+Stream, +Code)",
            "Stream variant of skip/1.");
        BuiltinsRegistry.Register("tab", 2, Tab2,
            Io, "tab(+Stream, +N)",
            "Stream variant of tab/1 — writes N spaces to Stream.");

        // Arity-Prolog string<->term
        // conversion. In Arity, "string" means atom; these are write-
        // and writeq-style counterparts of term_to_atom/2.
        BuiltinsRegistry.Register("string_term", 2, StringTerm2,
            Term, "string_term(?Atom, ?Term)",
            "Bidirectional: parses Atom as a Prolog term (binding Term), or "
            + "renders Term using write/1 form (binding Atom). 'string' in "
            + "Arity-Prolog terminology means atom — the textual representation "
            + "is interned as an atom, not stored as a Shumway StringTerm.");
        BuiltinsRegistry.Register("string_termq", 2, StringTermq2,
            Term, "string_termq(?Atom, ?Term)",
            "writeq-style variant of string_term/2: atoms / functors are "
            + "quoted when needed so the rendered atom re-parses to the same "
            + "term. Equivalent to term_to_atom/2.");
        BuiltinsRegistry.Register("string_search", 3, StringSearch3,
            Term, "string_search(+SubAtom, +Atom, ?Location)",
            "Searches Atom for the substring SubAtom; on success unifies "
            + "Location with the 0-based starting offset. Backtrackable: "
            + "produces every occurrence in left-to-right order.");
        BuiltinsRegistry.Register("string_search", 4, StringSearch4,
            Term, "string_search(+Case, +SubAtom, +Atom, ?Location)",
            "Arity string_search/4: like string_search/3 with a leading case "
            + "flag — 0 searches case-sensitively, 1 case-insensitively.");

        // Arity-Prolog file-system operations on
        // top of System.IO. chdir/1 is a 1-arg alias of
        // working_directory/2 living in the prelude; everything else
        // is a C# builtin.
        BuiltinsRegistry.Register("mkdir", 1, Mkdir1,
            Io, "mkdir(+Path)",
            "Creates the directory Path (and any missing parents). "
            + "Succeeds silently when the directory already exists.");
        BuiltinsRegistry.Register("rmdir", 1, Rmdir1,
            Io, "rmdir(+Path)",
            "Removes the directory Path. Fails when the directory is "
            + "non-empty; raises existence_error if it doesn't exist.");
        BuiltinsRegistry.Register("delete", 1, Delete1,
            Io, "delete(+File)",
            "Deletes the file File. Raises existence_error if absent, "
            + "permission_error if locked / read-only.");
        BuiltinsRegistry.Register("rename", 2, Rename2,
            Io, "rename(+From, +To)",
            "Renames / moves a file from From to To. Raises existence_error "
            + "if From doesn't exist or permission_error if To already exists.");
        BuiltinsRegistry.Register("directory", 6, Directory6,
            Io, "directory(+Path, -Name, -Mode, -Time, -Date, -Size)",
            "Backtracks over the entries in Path, binding Name (atom), "
            + "Mode (Arity-style bitfield: 1=read-only, 2=hidden, 4=system, "
            + "16=directory, 32=archive), Time (HH:MM:SS atom), Date "
            + "(YYYY-MM-DD atom) and Size (bytes; 0 for directories).");
        BuiltinsRegistry.Register("exists_file", 1, ExistsFile1,
            Io, "exists_file(+File)",
            "Succeeds when File exists and is a regular file.");
        BuiltinsRegistry.Register("file_permission", 2, FilePermission2,
            Io, "file_permission(+File, +Permission)",
            "Succeeds when File (a file or directory) grants Permission — "
            + "read, write, execute or search (GProlog-compatible). A "
            + "nonexistent path fails; unknown permissions raise "
            + "domain_error(os_file_permission, _).");
        BuiltinsRegistry.Register("copy_file", 2, CopyFile2,
            Io, "copy_file(+From, +To)",
            "Copies file From to To (overwriting To). Raises "
            + "existence_error(source_sink, From) when From is missing.");
        BuiltinsRegistry.Register("getenv", 2, GetEnv2,
            Io, "getenv(+Name, -Value)",
            "Unifies Value with the environment variable Name's contents "
            + "as an atom; fails (does not raise) when Name is unset — "
            + "SWI-compatible, so `(getenv(X,V) ; V = Default)` works.");
        // Unshadowable alias — see $sys_working_directory (Scryer os.pl
        // exports getenv/2; the shim's emulation must not resolve back to it).
        BuiltinsRegistry.Register("$sys_getenv", 2, GetEnv2);
        BuiltinsRegistry.Register("exists_directory", 1, ExistsDirectory1,
            Io, "exists_directory(+Path)",
            "Succeeds when Path exists and is a directory.");

        // process / file-metadata
        // primitives backing the shumway dialect section of Logtalk's
        // os.lgt (and generally useful for scripting).
        BuiltinsRegistry.Register("shell", 1, Shell1,
            Io, "shell(+Command)",
            "Runs Command through the platform shell (cmd.exe /C on "
            + "Windows, /bin/sh -c elsewhere) and succeeds iff it exits 0.");
        BuiltinsRegistry.Register("shell", 2, Shell2,
            Io, "shell(+Command, -Status)",
            "Runs Command through the platform shell and unifies Status "
            + "with its exit code.");
        BuiltinsRegistry.Register("pid", 1, Pid1,
            Io, "pid(-Pid)",
            "Unifies Pid with the current process id.");
        BuiltinsRegistry.Register("$choice_level", 1, ChoiceLevel1);
        BuiltinsRegistry.Register("sleep", 1, Sleep1,
            Io, "sleep(+Seconds)",
            "Suspends execution for Seconds (integer or float).");
        BuiltinsRegistry.Register("file_size", 2, FileSize2,
            Io, "file_size(+File, -Bytes)",
            "Unifies Bytes with File's size. Raises existence_error when "
            + "File doesn't exist.");
        BuiltinsRegistry.Register("file_modification_time", 2, FileModificationTime2,
            Io, "file_modification_time(+File, -Time)",
            "Unifies Time with File's last-modification time as integer "
            + "Unix-epoch seconds. Raises existence_error when absent.");
        BuiltinsRegistry.Register("directory_files", 2, DirectoryFiles2,
            Io, "directory_files(+Directory, -Files)",
            "Unifies Files with the list of entry names (atoms) in "
            + "Directory, including '.' and '..' — SWI-compatible.");

        // pseudo-random generation. Per-engine
        // System.Random seedable via randomize/1.
        BuiltinsRegistry.Register("randomize", 1, Randomize1,
            Term, "randomize(+Seed)",
            "Reseeds the engine's random generator. Seed is an integer.");
        BuiltinsRegistry.Register("random", 1, Random1,
            Term, "random(-X)",
            "Unifies X with a fresh pseudo-random float in [0.0, 1.0).");
        BuiltinsRegistry.Register("random_between", 3, RandomBetween3,
            Term, "random_between(+Low, +High, -X)",
            "Unifies X with a fresh pseudo-random integer in [Low, High] "
            + "(inclusive on both ends, matching SWI semantics).");
        // seed introspection pair.
        BuiltinsRegistry.Register("get_seed", 1, GetSeed1,
            Term, "get_seed(-Seed)",
            "Unifies Seed with a value that set_seed/1 can later use to "
            + "reproduce exactly the random sequence that follows this "
            + "call (the generator is reseeded as a side effect).");
        BuiltinsRegistry.Register("set_seed", 1, SetSeed1,
            Term, "set_seed(+Seed)",
            "Reseeds the engine's random generator; alias of randomize/1.");

        // DCG / macro expansion hook exposed.
        BuiltinsRegistry.Register("expand_term", 2, ExpandTerm2,
            Term, "expand_term(+Term, -Expanded)",
            "If Term has the form Head --> Body, expands it via the DCG "
            + "transformation Shumway applies internally on consult. "
            + "Non-DCG terms pass through unchanged.");

        // file_list/1,2 (Arity-Prolog). Persists
        // user predicates as plain Prolog text re-consultable by
        // consult/1.
        BuiltinsRegistry.Register("file_list", 1, FileList1,
            Database, "file_list(+File)",
            "Saves the entire user database (all listable predicates) "
            + "to File as plain Prolog source.");
        BuiltinsRegistry.Register("file_list", 2, FileList2,
            Database, "file_list(+File, +Spec)",
            "Saves selected predicates to File. Spec is either Name/Arity "
            + "or a list [Name1/Arity1, Name2/Arity2, ...].");

        BuiltinsRegistry.Register("restore_state", 1, RestoreState1,
            Database, "restore_state(+File)",
            "Restores a snapshot produced by save_state/1,2. Full-mode "
            + "snapshots reset the engine first and replay the saved "
            + "consults; dynamic-only snapshots merge their clauses into "
            + "the engine via assertz. Throws existence_error if File doesn't "
            + "exist, or type_error if it isn't a save_state snapshot.");
        BuiltinsRegistry.Register("reconsult", 1, Reconsult,
            Database, "reconsult(+File)",
            "Like consult/1 but first abolishes every predicate whose indicator "
            + "appears in File (in the target module), so an edit-reload cycle "
            + "replaces the file's predicates rather than duplicating clauses. "
            + "Predicates not mentioned in File are left untouched (classical "
            + "GProlog / SICStus semantics).");
    }

}

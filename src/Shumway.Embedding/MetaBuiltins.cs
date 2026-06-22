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
        // forall/2 is a prelude predicate (live-engine \+ (call(C), \+ call(A))),
        // not a builtin — the old isolated-sub-engine builtin hid the called
        // goals' side effects. See Prelude forall/2.
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
        for (int arity = 1; arity <= 33; arity++)
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

        BuiltinsRegistry.Register("assertz", 1, Assertz,
            Database, "assertz(+Clause)", "Adds a clause to the end of its dynamic predicate.");
        BuiltinsRegistry.Register("asserta", 1, Asserta,
            Database, "asserta(+Clause)", "Adds a clause to the front of its dynamic predicate.");
        // Chunk 145: 'assert/1' is the historical name; ISO and SWI
        // both accept it as a synonym for assertz/1.
        BuiltinsRegistry.Register("assert",  1, Assertz,
            Database, "assert(+Clause)", "Synonym for assertz/1 (historical SWI/GProlog name).");
        // Chunk 150: chain GC for retracted clauses (ADR-015 follow-up).
        BuiltinsRegistry.Register("garbage_collect_clauses", 0, GarbageCollectClauses0,
            Database, "garbage_collect_clauses",
            "Re-threads every dynamic predicate's chain to skip retracted clauses (ADR-015).");
        BuiltinsRegistry.Register("garbage_collect_clauses", 1, GarbageCollectClauses1,
            Database, "garbage_collect_clauses(+Name/Arity)",
            "Re-threads the named predicate's chain to skip retracted clauses.");
        BuiltinsRegistry.Register("compact_dynamic_buffer", 0, CompactDynamicBuffer,
            Database, "compact_dynamic_buffer",
            "Phase-11 chunk 157: invalidates the persistent dynamic-code buffer so "
            + "the next query rebuilds it from current _dynamicClauses. Reclaims memory "
            + "consumed by appended-but-now-unreachable chain entries from many "
            + "in-place assertz / asserta / retract cycles, at the cost of one "
            + "re-link of the dynamic region on the next query.");
        BuiltinsRegistry.Register("compact_dynamic_buffer", 1, CompactDynamicBuffer1,
            Database, "compact_dynamic_buffer(+Name/Arity)",
            "Phase-12 chunk 158: per-predicate hint variant. Validates Name/Arity "
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

        BuiltinsRegistry.Register("throw", 1, Throw,
            Control, "throw(+Exception)", "Throws an exception term, unwinding to the nearest catch/3.");
        // catch/3 is a prelude predicate built on the chunk-85 catch-frame
        // plumbing ($catch_begin/$catch_end), not a builtin — the old isolated-
        // sub-engine builtin hid the guarded goal's side effects. MetaTransform
        // rewrites a statically-callable catch/3 inline to the same shape; the
        // prelude clause is the runtime fallback for a variable Goal/Recovery.
        BuiltinsRegistry.Register("$catch_begin", 2, CatchBegin);
        BuiltinsRegistry.Register("$catch_end",   0, CatchEnd);

        // clause/2 and current_predicate/1 are now Prolog-level predicates
        // defined in the prelude (chunk 40). They call these helpers to
        // bridge into the engine's clause and functor stores, then iterate
        // via the prelude's member/2.
        BuiltinsRegistry.Register("$all_clauses_of",            2, AllClausesOf);
        BuiltinsRegistry.Register("$clause_enum",               2, ClauseEnum);
        BuiltinsRegistry.Register("$all_predicate_indicators",  1, AllPredicateIndicators);
        BuiltinsRegistry.Register("$current_predicate_enum",    1, CurrentPredicateEnum);
        BuiltinsRegistry.Register("$listable_predicates", 1, ListablePredicates);
        // Chunk 254 — listing path bypasses clause/2 + write/1 to
        // preserve the original VarTerm names parser captured. The
        // clause/2 path materialises through the heap, where every
        // unbound var picks up a synthetic _G<addr> name and the
        // user's "X" or "Acc" is lost.
        BuiltinsRegistry.Register("$listing_pred_source", 2, ListingPredSource);
        // Chunk 257 — SWI / SICStus / GNU-style clause pretty-printer.
        BuiltinsRegistry.Register("portray_clause", 1, PortrayClause1,
            Io, "portray_clause(+Clause)",
            "Pretty-prints Clause to the current output as a Prolog clause: head + indented body goals, "
            + "synthetic variable names renamed to A, B, C, ...");
        BuiltinsRegistry.Register("portray_clause", 2, PortrayClause2,
            Io, "portray_clause(+Stream, +Clause)",
            "Like portray_clause/1 but writes to the given stream.");
        // Tabling (chunk 106) — a per-engine string set giving the
        // semi-naive driver an O(1) "is this answer new?" test.
        BuiltinsRegistry.Register("$tbl_seen", 1, TableSeen);
        // Tabling (chunk 107) — table invalidation and tabled negation.
        BuiltinsRegistry.Register("$tbl_seen_clear", 0, TableSeenClear);
        BuiltinsRegistry.Register("$tbl_solve_complete", 1, TableSolveComplete);
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
        // Chunk 145: SWI/GProlog compat — /3 takes an options list
        // that we currently accept but ignore (no options affect the
        // parser yet).
        BuiltinsRegistry.Register("read_term_from_atom", 3, ReadTermFromAtom3,
            Term, "read_term_from_atom(+Atom, -Term, +Options)",
            "Parses an atom into a term; Options accepted for SWI/GProlog compat (currently ignored).");

        // Chunk 145: GProlog name/2 — atom/number ↔ list of codes.
        BuiltinsRegistry.Register("name", 2, NameBuiltin,
            Term, "name(?AtomOrNumber, ?Codes)",
            "Bidirectional conversion between an atom/number and its character-code list.");

        // Chunk 145: SWI global variables.
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
            "Backtrackable global variable assignment (Phase-10 stub stores non-backtrackably).");
        BuiltinsRegistry.Register("b_getval", 2, Shumway.Builtins.GlobalVarsBuiltins.BGetval,
            Globals, "b_getval(+Key, -Value)",
            "Reads a backtrackable global variable; existence_error if unset.");

        // Chunk 145: SWI time builtins (minimal — get_time as float
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
            "Reflection", "current_op(?Priority, ?Type, ?Name)",
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
        BuiltinsRegistry.Register("with_output_to", 2, WithOutputTo,
            Io, "with_output_to(+Sink, :Goal)", "Runs a goal, capturing its output into an atom, string or code list.");
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
        // the parsed term with arg 2. Chunk 59: delegate to the existing
        // stream-aware reader so the builtin set covers both names.
        BuiltinsRegistry.Register("read_term", 2, ReadTermFromStream,
            Io, "read_term(+Stream, -Term)", "Reads one term from a read-mode stream.");
        BuiltinsRegistry.Register("read",      1, Read1,
            Io, "read(-Term)", "Reads one term from current input (ISO §8.14.2).");
        BuiltinsRegistry.Register("read",      2, Read2,
            Io, "read(+Stream, -Term)", "Reads one term from a stream (ISO §8.14.2).");
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
        BuiltinsRegistry.Register("file_name_extension", 3, FileNameExtension3,
            Io, "file_name_extension(?Base, ?Ext, ?Full)",
            "Relates a file name to its base and extension. With Full bound, splits at "
            + "the last '.'; with Base and Ext bound, composes Base + '.' + Ext (or "
            + "just Base when Ext is empty). SWI / SICStus compatible.");
        BuiltinsRegistry.Register("is_digit", 1, IsDigit1,
            Term, "is_digit(+Char)",
            "True when Char is a one-character atom representing an ASCII digit.");

        // Chunk 235: consult/1 and reconsult/1. Both route through
        // PrologEngine.ConsultFile: .shum extension goes through
        // LoadBundle, everything else is read as Prolog source and
        // handed to ConsultString. SWI treats reconsult/1 as a synonym
        // for consult/1; we do the same.
        BuiltinsRegistry.Register("consult", 1, Consult,
            Database, "consult(+File)",
            "Loads File and adds its clauses to the database, appending to any "
            + "existing predicates. File is an atom path; a .shum extension routes "
            + "through LoadBundle, everything else is read as Prolog source.");
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
        // Phase 24 chunk 266: recorded database (Arity-Prolog).
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

        // Phase 24 chunk 267 — Edinburgh-style I/O (Arity-Prolog
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

        // Phase 24 chunk 268 (partial) — Arity-Prolog string<->term
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

        // Phase 24 chunk 271 — Arity-Prolog file-system operations on
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
        BuiltinsRegistry.Register("exists_directory", 1, ExistsDirectory1,
            Io, "exists_directory(+Path)",
            "Succeeds when Path exists and is a directory.");

        // Phase 24 chunk 272 — pseudo-random generation. Per-engine
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

        // Phase 24 chunk 273 — DCG / macro expansion hook exposed.
        BuiltinsRegistry.Register("expand_term", 2, ExpandTerm2,
            Term, "expand_term(+Term, -Expanded)",
            "If Term has the form Head --> Body, expands it via the DCG "
            + "transformation Shumway applies internally on consult. "
            + "Non-DCG terms pass through unchanged.");

        // Phase 24 chunk 274 — file_list/1,2 (Arity-Prolog). Persists
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

    /// <summary><c>current_stream(?Filename, ?Mode, ?Stream)</c> —
    /// ISO §8.11.8.1. Enumerates every registered stream on
    /// backtracking. Filename and Mode arguments unify against each
    /// handle's metadata; the stream arg is bound to a Foreign cell
    /// wrapping the underlying <see cref="Shumway.Core.StreamHandle"/>.
    /// (Chunk 140b.)</summary>
    public static bool CurrentStream(Engine engine)
    {
        var registry = engine.Streams
            ?? throw new InvalidOperationException("Engine has no stream registry.");
        var handles = registry.All().ToArray();
        int returnPc = engine.BuiltinReturnPc;
        // arity 3 (current_stream/3): save the arg registers so a wrapping
        // findall can't clobber them between solutions (chunk-293/294 fix,
        // missed for these enumerators).
        return IndexEnumCursor.Start(engine, handles.Length, 3, returnPc,
            (e, i) => CurrentStreamUnify(e, handles, i));
    }

    private static bool CurrentStreamUnify(
        Engine engine, Shumway.Core.StreamHandle[] handles, int idx)
    {
        var h = handles[idx];
        string fnText = h.Filename ?? h.Alias ?? "";
        Cell fnCell = Cell.Atom(AtomTable.Intern(fnText, permanent: false).Id);
        Cell modeCell = Cell.Atom(AtomTable.Intern(h.Mode, permanent: true).Id);
        Cell streamCell = engine.MakeForeign(h);

        if (!engine.UnifyRegisterWithCell(0, fnCell)) return false;
        if (!engine.UnifyRegisterWithCell(1, modeCell)) return false;
        if (!engine.UnifyRegisterWithCell(2, streamCell)) return false;
        return true;
    }

    /// <summary><c>stream_property(?Stream, ?Property)</c> — ISO §8.11.8.2.
    /// Enumerates (Stream, Property) pairs for every registered stream.
    /// Properties: <c>file_name(F)</c>, <c>mode(M)</c>,
    /// <c>alias(A)</c>, <c>input</c>, <c>output</c>,
    /// <c>end_of_stream(at|not)</c>. (Chunk 140b.)</summary>
    public static bool StreamProperty(Engine engine)
    {
        var registry = engine.Streams
            ?? throw new InvalidOperationException("Engine has no stream registry.");
        var pairs = new List<(Shumway.Core.StreamHandle Handle, Term Property)>();
        foreach (var h in registry.All())
        {
            if (h.Filename is string fn)
                pairs.Add((h, new CompoundTerm("file_name", new Term[] { new AtomTerm(fn) })));
            pairs.Add((h, new CompoundTerm("mode", new Term[] { new AtomTerm(h.Mode) })));
            if (h.Alias is string al)
                pairs.Add((h, new CompoundTerm("alias", new Term[] { new AtomTerm(al) })));
            pairs.Add((h, h.IsReader ? (Term)new AtomTerm("input") : new AtomTerm("output")));
            if (h.IsReader)
            {
                string state = (ReferenceEquals(h, registry.UserInput)
                                || h.Reader!.Peek() >= 0)
                    ? "not" : "at";
                pairs.Add((h, new CompoundTerm("end_of_stream",
                    new Term[] { new AtomTerm(state) })));
            }
            // Chunk 140d: position/1 — present when the underlying
            // .NET stream is seekable. user_input / user_output
            // (console-backed) aren't.
            long? pos = TryGetStreamPosition(h);
            if (pos.HasValue)
                pairs.Add((h, new CompoundTerm("position",
                    new Term[] { new IntTerm(pos.Value) })));
        }
        int returnPc = engine.BuiltinReturnPc;
        var pairArr = pairs.ToArray();
        return IndexEnumCursor.Start(engine, pairArr.Length, 2, returnPc,  // arity 2 (stream_property/2)
            (e, i) => StreamPropertyUnify(e, pairArr, i));
    }

    private static bool StreamPropertyUnify(
        Engine engine, (Shumway.Core.StreamHandle Handle, Term Property)[] pairs, int idx)
    {
        var (h, prop) = pairs[idx];
        Cell streamCell = engine.MakeForeign(h);
        Cell propCell = Materializer.MaterializeAsCell(engine, prop);

        if (!engine.UnifyRegisterWithCell(0, streamCell)) return false;
        if (!engine.UnifyRegisterWithCell(1, propCell)) return false;
        return true;
    }

    /// <summary>Returns the underlying .NET stream's byte position when
    /// the stream is seekable, or null otherwise (e.g. console-backed
    /// user_input / user_output). Used both for the
    /// <c>position(N)</c> property of <c>stream_property/2</c> and as
    /// the seekable-stream check for <c>set_stream_position/2</c>.
    /// (Chunk 140d.)</summary>
    private static long? TryGetStreamPosition(Shumway.Core.StreamHandle h)
    {
        try
        {
            // Binary stream first — its raw .NET Stream is the
            // authoritative position source.
            if (h.BinaryStream is System.IO.Stream bs)
                return bs.CanSeek ? bs.Position : null;
            if (h.Reader is System.IO.StreamReader sr)
                return sr.BaseStream.CanSeek ? sr.BaseStream.Position : null;
            if (h.Writer is System.IO.StreamWriter sw)
                return sw.BaseStream.CanSeek ? sw.BaseStream.Position : null;
        }
        catch (NotSupportedException) { /* fall through */ }
        catch (ObjectDisposedException) { /* fall through */ }
        return null;
    }

    /// <summary><c>set_stream_position(+Stream, +Position)</c> — ISO
    /// §8.11.10. Seeks the underlying byte stream to the given
    /// position. Position is an integer (byte offset), matching what
    /// <c>stream_property(_, position(N))</c> yields. (Chunk 140d.)
    /// </summary>
    public static bool SetStreamPosition(Engine engine)
    {
        var h = Shumway.Builtins.StreamBuiltins.ResolveStream(
            engine, engine.GetRegister(0));
        Cell posCell = MaterializeRegisterAsCell(engine, 1);
        if (posCell.Tag == Tag.Ref)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (posCell.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("domain_error", "stream_position");
        long target = posCell.AsInt;

        System.IO.Stream? baseStream = h.BinaryStream
            ?? (h.Reader is System.IO.StreamReader sr
                ? sr.BaseStream
                : h.Writer is System.IO.StreamWriter sw ? sw.BaseStream : null);
        if (baseStream is null || !baseStream.CanSeek)
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "reposition,stream");

        // Writer needs flush before the seek so any buffered output
        // lands at the *current* position rather than the new one.
        if (h.Writer is System.IO.StreamWriter w) w.Flush();
        baseStream.Position = target;
        return true;
    }

    private static Cell MaterializeRegisterAsCell(Engine engine, int reg)
    {
        Cell c = engine.GetRegister(reg);
        if (c.Tag != Tag.Ref) return c;
        return engine.GetHeap(engine.Deref(c.AsHeapIndex));
    }

    /// <summary><c>read_term_from_stream(Stream, Term)</c> — reads
    /// characters from a read-mode stream until it sees a clause-ending
    /// <c>.</c> followed by whitespace or EOF, parses the buffer as a
    /// Prolog term, and unifies the result with <c>Term</c>. Hits EOF
    /// before any text yields the atom <c>end_of_file</c>.</summary>
    public static bool ReadTermFromStream(Engine engine) =>
        ReadOneTermInto(engine,
            ResolveTextReader(engine, engine.GetRegister(0)), regOut: 1);

    /// <summary><c>read/1</c> — ISO §8.14.2. Reads one term from the
    /// current input stream. (Chunk 143.)</summary>
    public static bool Read1(Engine engine)
    {
        var h = engine.Streams?.CurrentInput
            ?? throw new InvalidOperationException("Engine has no stream registry.");
        return ReadOneTermInto(engine, ResolveTextReaderFromHandle(h), regOut: 0);
    }

    /// <summary><c>read(+Stream, -Term)</c> — ISO §8.14.2.</summary>
    public static bool Read2(Engine engine) =>
        ReadOneTermInto(engine,
            ResolveTextReader(engine, engine.GetRegister(0)), regOut: 1);

    private static System.IO.TextReader ResolveTextReader(Engine engine, Cell streamArg)
    {
        var h = Shumway.Builtins.StreamBuiltins.ResolveStream(engine, streamArg);
        return ResolveTextReaderFromHandle(h);
    }

    /// <summary>Chunk 257 — mirror of <see cref="ResolveTextReader"/>
    /// for write-mode streams. Used by <c>portray_clause/2</c>.</summary>
    private static System.IO.TextWriter ResolveTextWriter(Engine engine, Cell streamArg)
    {
        var h = Shumway.Builtins.StreamBuiltins.ResolveStream(engine, streamArg);
        if (!h.IsWriter)
            throw new PrologRuntimeException("permission_error", "output,stream");
        if (h.IsBinary)
            throw new PrologRuntimeException("permission_error", "output,binary_stream");
        return h.Writer!;
    }

    private static System.IO.TextReader ResolveTextReaderFromHandle(Shumway.Core.StreamHandle h)
    {
        if (!h.IsReader)
            throw new PrologRuntimeException("permission_error", "input,stream");
        if (h.IsBinary)
            // ISO §8.14.2.3.g — text-term read on a binary stream.
            throw new PrologRuntimeException("permission_error", "input,binary_stream");
        return h.Reader!;
    }

    private static bool ReadOneTermInto(Engine engine, System.IO.TextReader reader, int regOut)
    {
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
                    return engine.UnifyRegisterWithCell(regOut, Cell.Atom(eofId));
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
        return engine.UnifyRegisterWithCell(regOut, cell);
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
        if (flagName == "unknown")
        {
            if (valueName != "error" && valueName != "fail" && valueName != "warning")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.Unknown = valueName;
            // Chunk 417 — take effect mid-query: dispatch reads the
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
            // Phase 30 — Arity/Prolog32 compatibility mode. The parse-time
            // features ($...$ atoms, #line, directive annotations) apply to
            // SUBSEQUENT consults; the ClauseReader's directive pre-pass
            // handles a mid-file flip.
            if (valueName != "true" && valueName != "false")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.ArityCompat = valueName == "true";
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
        if (flagName == "compile_mode")
        {
            if (valueName != "debug" && valueName != "release")
                throw new ShumwayPrologException(
                    IsoError.DomainError("flag_value", new AtomTerm(valueName)));
            host.Flags.EmitDebugInfo = valueName == "debug";
            return true;
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
    /// </list>
    /// With Flag unbound this builtin fails — full enumeration via
    /// backtracking is a future chunk.</summary>
    public static bool CurrentPrologFlag(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "current_prolog_flag/2 requires the engine to be hosted by a PrologEngine.");

        Cell flagCell = ResolveLocal(engine, engine.GetRegister(0));
        if (flagCell.Tag != Tag.Atom) return false;
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

            case "arity_compat":
                return UnifyAtom(engine, 1, host.Flags.ArityCompat ? "true" : "false");

            case "compile_mode":
                return UnifyAtom(engine, 1, host.Flags.EmitDebugInfo ? "debug" : "release");

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

    private static bool UnifyAtom(Engine engine, int register, string name)
    {
        int aid = AtomTable.Intern(name, permanent: true).Id;
        return engine.UnifyRegisterWithCell(register, Cell.Atom(aid));
    }

    /// <summary><c>absolute_file_name(+FileSpec, -Absolute)</c> —
    /// resolves a file path to an absolute one. The basic 2-arg
    /// form: <c>FileSpec</c> must be a bound atom or PSTR; the
    /// result is the absolute path as an atom. Internally just
    /// <see cref="Path.GetFullPath(string)"/>, so the resolution
    /// is relative to the current working directory of the host
    /// process.
    ///
    /// <para>Not supported: SWI's 3-arg form with options
    /// (<c>extensions</c>, <c>file_type</c>, <c>access</c>,
    /// <c>file_search_path</c>) — those need the
    /// <c>file_search_path/2</c> registry and a small option
    /// parser. Add when a program actually needs them.</para></summary>
    public static bool AbsoluteFileName2(Engine engine)
    {
        if (!TryGetStringArg(engine, 0, out string spec))
            return false;
        try
        {
            string absolute = Path.GetFullPath(spec);
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
    public static bool FileNameExtension3(Engine engine)
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
    public static bool IsDigit1(Engine engine)
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
    public static bool WorkingDirectory2(Engine engine)
    {
        string oldCwd = Directory.GetCurrentDirectory();
        // Ensure a trailing separator so it matches SWI's convention.
        if (!oldCwd.EndsWith(Path.DirectorySeparatorChar)
            && !oldCwd.EndsWith(Path.AltDirectorySeparatorChar))
            oldCwd += Path.DirectorySeparatorChar;
        int oldAid = AtomTable.Intern(oldCwd, permanent: true).Id;
        if (!engine.UnifyRegisterWithCell(0, Cell.Atom(oldAid)))
            return false;

        if (!TryGetStringArg(engine, 1, out string newCwd))
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (newCwd != oldCwd && newCwd != oldCwd.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar))
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
    private static bool TryGetStringArg(Engine engine, int register, out string value)
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

    /// <summary><c>current_op(?Priority, ?Type, ?Name)</c> — ISO §8.17.3.
    /// Enumerates every defined operator on backtracking, with any of
    /// the three args optionally constraining the search. Uses the
    /// standard PushBuiltinChoicePoint pattern for the multi-solution
    /// dispatch (chunk 138).</summary>
    public static bool CurrentOp(Engine engine)
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
        Engine engine,
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
    public static bool CharConversion(Engine engine)
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
    /// the multi-solution dispatch (chunk 152).</summary>
    public static bool CurrentCharConversion(Engine engine)
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
        Engine engine, (char In, char Out)[] entries, int idx)
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

    /// <summary><c>read_term_from_atom(+Atom, -Term, +Options)</c> —
    /// SWI / GProlog compat. The options list is accepted but
    /// currently ignored (no read-time options affect the parser
    /// yet). Chunk 145.</summary>
    public static bool ReadTermFromAtom3(Engine engine)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));
        if (atomCell.Tag != Tag.Atom)
            throw new PrologRuntimeException("type_error", "atom");
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

    /// <summary><c>name(?AtomOrNumber, ?Codes)</c> — old-style GProlog
    /// bidirectional conversion. With first arg bound, builds the
    /// list of character codes for its print form. With second arg
    /// bound, tries to parse the codes as a number first; on
    /// parse-failure interns as an atom. Chunk 145.</summary>
    public static bool NameBuiltin(Engine engine)
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
            string s = v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (!s.Contains('.') && !s.Contains('e') && !s.Contains('E')) s += ".0";
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

    private static bool UnifyCodesList(Engine engine, int regOut, string text)
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

    private static string ReadCodesAsString(Engine engine, Cell codesCell)
    {
        var sb = new System.Text.StringBuilder();
        Cell cur = ResolveLocal(engine, codesCell);
        while (cur.Tag == Tag.Lis)
        {
            Cell head = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex));
            if (head.Tag != Tag.Int)
                throw new PrologRuntimeException("type_error", "character_code");
            sb.Append((char)head.AsInt);
            cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
        }
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            throw new PrologRuntimeException("type_error", "list");
        return sb.ToString();
    }

    /// <summary><c>get_time(-Time)</c> — current wall-clock time in
    /// seconds since the Unix epoch, as a float. SWI-compat. Chunk 145.</summary>
    public static bool GetTime(Engine engine)
    {
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            .TotalSeconds;
        int idx = engine.MakeFloat(now);
        return engine.UnifyRegisterWithHeapAt(0, idx);
    }

    /// <summary><c>stamp_date_time(+Stamp, -DateTime, +TimeZone)</c> —
    /// converts a Unix-epoch stamp (float seconds) into the SWI
    /// <c>date(Y, M, D, H, Mi, S, Off, TZ, DST)</c> compound. The
    /// TimeZone arg is honoured for the atoms <c>'UTC'</c> and
    /// <c>local</c>; any other atom is treated as the local zone
    /// (full IANA-name lookup isn't worth the System.TimeZoneInfo
    /// wiring for the typical caller). Chunk 145.</summary>
    public static bool StampDateTime(Engine engine)
    {
        Cell stampCell = ResolveLocal(engine, engine.GetRegister(0));
        Cell tzCell = ResolveLocal(engine, engine.GetRegister(2));
        if (stampCell.Tag == Tag.Ref)
            throw new PrologRuntimeException("instantiation_error");
        double stamp = stampCell.Tag switch
        {
            Tag.Float => Cell.DecodeFloat(stampCell, engine.GetHeap(stampCell.FloatPairedIndex)),
            Tag.Int => stampCell.AsInt,
            _ => throw new PrologRuntimeException("type_error", "number"),
        };
        string tzName = tzCell.Tag == Tag.Atom
            ? (AtomTable.GetById(tzCell.AsAtomId)?.Name ?? "local")
            : "local";

        DateTime utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddSeconds(stamp);
        DateTime local = string.Equals(tzName, "UTC", StringComparison.OrdinalIgnoreCase)
            ? utc
            : utc.ToLocalTime();
        TimeSpan offset = string.Equals(tzName, "UTC", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.Zero
            : TimeZoneInfo.Local.GetUtcOffset(utc);

        var dt = new CompoundTerm("date", new Term[]
        {
            new IntTerm(local.Year),
            new IntTerm(local.Month),
            new IntTerm(local.Day),
            new IntTerm(local.Hour),
            new IntTerm(local.Minute),
            new FloatTerm(local.Second + local.Millisecond / 1000.0),
            new IntTerm((long)offset.TotalSeconds),
            new AtomTerm(tzName),
            new AtomTerm("-"),  // DST flag — '-' = unknown/n-a.
        });
        Cell dtCell = Materializer.MaterializeAsCell(engine, dt);
        return engine.UnifyRegisterWithCell(1, dtCell);
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
    // Cached atom ids — boyer hits =../2 in a tight loop; avoid the
    // AtomTable hash lookup per call. Permanent atoms get permanent
    // ids that never get reused, so caching is safe.
    private static int _dotAtomIdCache;
    private static int DotAtomId
    {
        get
        {
            if (_dotAtomIdCache == 0)
                _dotAtomIdCache = AtomTable.Intern(".", permanent: true).Id;
            return _dotAtomIdCache;
        }
    }

    public static bool Univ(Engine engine)
    {
        Cell t = ResolveLocal(engine, engine.GetRegister(0));

        // Decompose modes — build the list directly in the heap with
        // a single allocation, no intermediate Cell[] buffer.
        if (t.Tag == Tag.Atom || t.Tag == Tag.Int || t.Tag == Tag.Float)
        {
            // Single-element list: [t] = .(t, []).
            int idx = engine.AllocateHeap(3);
            engine.SetHeap(idx,     Cell.Lis(idx + 1));
            engine.SetHeap(idx + 1, t);
            engine.SetHeap(idx + 2, Cell.Atom(AtomTable.EmptyListId));
            return engine.UnifyRegisterWithHeapAt(1, idx);
        }
        if (t.Tag == Tag.Str)
        {
            int functorIdx = t.AsHeapIndex;
            var (atomId, arity) = FunctorTable.Lookup(
                engine.GetHeap(functorIdx).AsFunctorId);
            // Fast path: [Functor | Args] built directly. Layout:
            //   idx+0: Lis(idx+1)        -- first cons
            //   idx+1: Atom(functor)     -- head: the functor atom
            //   idx+2: Lis(idx+3)        -- next cons (arg 0)
            //   idx+3: <arg 0>           -- head: copied from STR
            //   ...
            //   idx+2k: Lis(idx+2k+1)    -- cons for arg k-1
            //   idx+2k+1: <arg k-1>
            //   idx+2(arity+1): Atom([]) -- terminating nil
            int total = 2 * (1 + arity) + 1;
            int idx = engine.AllocateHeap(total);
            engine.SetHeap(idx,     Cell.Lis(idx + 1));
            engine.SetHeap(idx + 1, Cell.Atom(atomId));
            for (int i = 0; i < arity; i++)
            {
                int cons = idx + 2 + 2 * i;
                engine.SetHeap(cons,     Cell.Lis(cons + 1));
                engine.SetHeap(cons + 1, engine.GetHeap(functorIdx + 1 + i));
            }
            engine.SetHeap(idx + 2 * (1 + arity), Cell.Atom(AtomTable.EmptyListId));
            return engine.UnifyRegisterWithHeapAt(1, idx);
        }
        if (t.Tag == Tag.Lis)
        {
            // Lis cell represents a [Head|Tail] cons — its =.. result
            // is the 3-element list ['.', Head, Tail].
            int headIdx = t.AsHeapIndex;
            int idx = engine.AllocateHeap(7);
            engine.SetHeap(idx,     Cell.Lis(idx + 1));
            engine.SetHeap(idx + 1, Cell.Atom(DotAtomId));
            engine.SetHeap(idx + 2, Cell.Lis(idx + 3));
            engine.SetHeap(idx + 3, engine.GetHeap(headIdx));
            engine.SetHeap(idx + 4, Cell.Lis(idx + 5));
            engine.SetHeap(idx + 5, engine.GetHeap(headIdx + 1));
            engine.SetHeap(idx + 6, Cell.Atom(AtomTable.EmptyListId));
            return engine.UnifyRegisterWithHeapAt(1, idx);
        }
        if (t.Tag == Tag.Ref)
        {
            // Compose: walk the list twice — once to count, once to
            // build the STR. The list is on the heap so the walk is a
            // pointer chase, no allocation.
            Cell listC = ResolveLocal(engine, engine.GetRegister(1));
            int count = 0;
            Cell cur = listC;
            while (cur.Tag == Tag.Lis)
            {
                count++;
                cur = ResolveLocal(engine, engine.GetHeap(cur.AsHeapIndex + 1));
            }
            if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
                throw new ShumwayPrologException(
                    IsoError.TypeError("list", new VarTerm("_")));
            if (count == 0)
                throw new ShumwayPrologException(
                    IsoError.DomainError("non_empty_list", new VarTerm("_")));

            // Fetch the functor cell (the first element).
            int headIdx = listC.AsHeapIndex;
            Cell first = ResolveLocal(engine, engine.GetHeap(headIdx));
            if (count == 1)
            {
                if (first.Tag != Tag.Atom && first.Tag != Tag.Int && first.Tag != Tag.Float)
                    throw new ShumwayPrologException(
                        IsoError.TypeError("atomic", new VarTerm("_")));
                return engine.UnifyRegisterWithCell(0, first);
            }
            if (first.Tag != Tag.Atom)
                throw new ShumwayPrologException(
                    IsoError.TypeError("atom", new VarTerm("_")));
            int arity = count - 1;
            int functorId = FunctorTable.Intern(first.AsAtomId, arity);
            // Walk the list a second time to copy args into the STR.
            int strBase = engine.AllocateHeap(2 + arity);
            engine.SetHeap(strBase, Cell.Str(strBase + 1));
            engine.SetHeap(strBase + 1, Cell.Functor(functorId));
            // Skip the first element (functor name) and copy the rest.
            cur = ResolveLocal(engine, engine.GetHeap(headIdx + 1));
            for (int i = 0; i < arity; i++)
            {
                int curHead = cur.AsHeapIndex;
                engine.SetHeap(strBase + 2 + i, engine.GetHeap(curHead));
                cur = ResolveLocal(engine, engine.GetHeap(curHead + 1));
            }
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

        // Term → Atom direction: render and intern. Match SWI's
        // term_to_atom/2 — render with operator notation (so `hola/2`
        // comes out as `hola/2`, not `/(hola, 2)`) and quoting (so the
        // atom round-trips back through the parser in the reverse
        // direction).
        using var sw = new System.IO.StringWriter();
        Shumway.Builtins.TermRenderer.Render(engine, engine.GetRegister(0), sw,
            new Shumway.Builtins.TermRenderOptions
            {
                Operators = engine.Operators,
                Quoted = true,
                // TightSymbolicOperators defaults true — symbolic ops
                // render space-free, matching other Prologs.
            });
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
        // Chunk 131e: ISO precedence — var second arg →
        // instantiation_error; bound non-int → type_error(integer, _).
        if (startDeref.Tag == Tag.Ref)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (startDeref.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error", "integer");
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

    /// <summary><c>'$clause_enum'(Head, Head-Body)</c> — the LAZY backing for
    /// <c>clause/2</c>. The prelude passes the query's <c>Head-Body</c> pair as
    /// the second argument (built Prolog-side, so its variables are the user's),
    /// and this yields each matching clause one at a time on backtracking:
    /// per candidate it materialises just that clause's <c>-(Head, Body)</c>
    /// pair (head and body share variables, so the pair must be one
    /// materialisation) and unifies the query pair against it. Replaces
    /// <see cref="AllClausesOf"/> + <c>member/2</c>, which built the whole
    /// O(#clauses) pair list on the heap up front — here only the candidate
    /// being tried is on the heap, and a backtrack reclaims it.
    ///
    /// <para>The first register (Head) is used only to find the functor; the
    /// actual unification is against the pair in the second register, so the
    /// shared <c>Head</c> variable binds consistently from there.</para></summary>
    public static bool ClauseEnum(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$clause_enum'/2 requires a PrologEngine host.");

        Term headPattern = MaterializeRegister(engine, 0);
        int fid = ExtractCallableFunctorId(headPattern, "clause/2");

        var candidates = new List<Clause>();
        candidates.AddRange(host.DynamicClausesFor(fid));
        candidates.AddRange(host.StaticClausesFor(fid));

        int returnPc = engine.BuiltinReturnPc;
        // arity 2 (clause/2): save the arg registers across backtracks.
        return Shumway.Core.IndexEnumCursor.Start(engine, candidates.Count, 2, returnPc,
            (e, i) => ClauseEnumUnify(e, candidates[i]));
    }

    private static bool ClauseEnumUnify(Engine engine, Clause candidate)
    {
        Term head = candidate.Kind == ClauseKind.Rule
            ? ((CompoundTerm)candidate.Term).Args[0]
            : candidate.Term;
        Term body = candidate.Kind == ClauseKind.Rule
            ? ((CompoundTerm)candidate.Term).Args[1]
            : new AtomTerm("true");
        // One materialisation so the clause's Head and Body share variables;
        // unify the query's Head-Body pair (register 1) against it.
        Cell pairCell = Materializer.MaterializeAsCell(
            engine, new CompoundTerm("-", new[] { head, body }));
        return engine.UnifyRegisterWithCell(1, pairCell);
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

    /// <summary><c>'$current_predicate_enum'(?PI)</c> — the LAZY backing for
    /// <c>current_predicate/1</c>. Yields each known predicate's
    /// <c>Name/Arity</c> indicator one at a time on backtracking (a cursor
    /// over the snapshot), instead of building the whole O(n) indicator list
    /// on the heap up front for <c>member/2</c> to walk. Indicators are ground,
    /// so the per-step unification just filters against a bound <c>PI</c>.</summary>
    public static bool CurrentPredicateEnum(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$current_predicate_enum'/1 requires a PrologEngine host.");

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

        int returnPc = engine.BuiltinReturnPc;
        return Shumway.Core.IndexEnumCursor.Start(engine, indicators.Count, 1, returnPc,
            (e, i) => e.UnifyRegisterWithCell(0, Materializer.MaterializeAsCell(e, indicators[i])));
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
            string mangled = AtomTable.GetById(atomId)?.Name ?? "?";
            // Chunk 256: present the user-facing name. Local
            // predicates carry a "user$" (or other module) prefix
            // from ModuleRewrite; surface the unprefixed name so
            // `listing(foo)` finds the predicate the user wrote
            // as `foo(X) :- ...`.
            string name = PrologEngine.DemangleLocalName(mangled);
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

    /// <summary>Chunk 254 — <c>$listing_pred_source(+Name, +Arity)</c>.
    /// Prints every AST clause whose head functor matches
    /// <c>Name/Arity</c>, using <see cref="AstTermRenderer"/> so the
    /// original <see cref="Shumway.Compiler.Ast.VarTerm.Name"/> from
    /// the parser survives — the user sees <c>greet(X, Y) :- Y = hello(X)</c>
    /// instead of <c>greet(_G23, _G24) :- _G24 = hello(_G23)</c>.
    ///
    /// <para>The clauses come from both static-module sources
    /// (parsed by <c>ConsultString</c>, names preserved) and
    /// <c>:- dynamic foo/N. foo(a).</c>-seed clauses (also parsed
    /// from source). Runtime-asserted clauses arrive via the heap
    /// and carry synthetic <c>_G&lt;addr&gt;</c> names; this builtin
    /// renders whatever names the AST holds — preserved when source
    /// is available, synthetic otherwise.</para>
    ///
    /// <para>Output layout mirrors the prelude's portray_clause:
    /// facts on one line, rules with the head and an indented body
    /// line per goal.</para></summary>
    public static bool ListingPredSource(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$listing_pred_source'/2 requires a PrologEngine host.");

        Cell nameCell = MaterializeRegisterAsCell(engine, 0);
        Cell arityCell = MaterializeRegisterAsCell(engine, 1);
        if (nameCell.Tag != Tag.Atom || arityCell.Tag != Tag.Int)
            return false;
        string displayName = AtomTable.GetById(nameCell.AsAtomId)?.Name ?? "";
        int arity = (int)arityCell.AsInt;

        // Chunk 256: ModuleRewrite mangles local predicates as
        // <module>$<name>. The user's `listing(helper)` arrives
        // here with the unmangled name; find every fid whose
        // demangled name matches (the predicate may be stored
        // under user$helper, foo$helper, or just helper if it's
        // public).
        var matchingFids = new List<int>();
        foreach (var (fid, _) in host.ListablePredicates())
        {
            var (atomId, fidArity) = FunctorTable.Lookup(fid);
            if (fidArity != arity) continue;
            string mangled = AtomTable.GetById(atomId)?.Name ?? "";
            if (mangled == displayName
                || PrologEngine.DemangleLocalName(mangled) == displayName)
                matchingFids.Add(fid);
        }

        var output = engine.Out;
        int printed = 0;
        foreach (int fid in matchingFids)
        {
            foreach (var clause in host.ClausesForListing(fid))
            {
                PrintAstClause(output, clause);
                printed++;
            }
            // Chunk 255: no AST clauses but the predicate may still
            // exist as a precompiled record loaded from a source-
            // stripped bundle. Surface a comment so the user sees the
            // predicate is real — bare `true.` would lie by implying
            // there's no body to show when there are clauses, just
            // no source for them.
            if (printed == 0)
            {
                var pre = host.PrecompiledRecordFor(fid);
                if (pre is not null)
                {
                    string clauseWord = pre.ClauseCount == 1 ? "clause" : "clauses";
                    output.WriteLine(
                        $"% {displayName}/{arity}: {pre.ClauseCount} {clauseWord}, source stripped (no listing available)");
                    printed++;
                }
            }
        }
        return true;
    }

    /// <summary>Chunk 257 — delegates to the shared
    /// <see cref="ClausePortrayer"/>. The Clause's wrapping
    /// (Fact's bare head vs Rule's <c>:-(H,B)</c> compound) is
    /// detected by the portrayer from the Term's own shape — no
    /// need to thread <see cref="Shumway.Compiler.Ast.ClauseKind"/>
    /// through.</summary>
    private static void PrintAstClause(
        System.IO.TextWriter output, Shumway.Compiler.Ast.Clause clause)
    {
        ClausePortrayer.Print(output, clause.Term);
    }

    /// <summary>Chunk 257 — <c>portray_clause(+Clause)</c>: prints
    /// Clause to the engine's current output using the standard
    /// portray layout (head + indented body goals, synthetic
    /// variables renumbered to A, B, C, …).</summary>
    public static bool PortrayClause1(Engine engine)
    {
        Term term = MaterializeRegister(engine, 0);
        ClausePortrayer.Print(engine.Out, term);
        return true;
    }

    /// <summary>Chunk 257 — <c>portray_clause(+Stream, +Clause)</c>:
    /// like <see cref="PortrayClause1"/> but writes to the given
    /// output stream. The stream must be a Foreign cell bound to
    /// a write-mode handle (the same shape current_output / open
    /// produce).</summary>
    public static bool PortrayClause2(Engine engine)
    {
        TextWriter writer = ResolveTextWriter(engine, engine.GetRegister(0));
        Term term = MaterializeRegister(engine, 1);
        ClausePortrayer.Print(writer, term);
        return true;
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
            host.AbolishDynamic(engine, fid);
            return true;
        }

        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        throw new ShumwayPrologException(
            IsoError.TypeError("predicate_indicator", spec));
    }

    /// <summary><c>garbage_collect_clauses/0</c> — chunk 150. Walks
    /// every dynamic predicate's chain and re-threads it through only
    /// the live entries, bypassing the retracted ones still sitting
    /// in the bytecode. The dispatch cost of subsequent calls then
    /// drops from O(ever-asserted) back to O(live). The dead-clause
    /// bytecode is left orphaned; the program buffer doesn't shrink.</summary>
    public static bool GarbageCollectClauses0(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "garbage_collect_clauses/0 requires a PrologEngine host.");
        foreach (int fid in host.AllDynamicFunctors())
            host.GarbageCollectClauses(engine, fid);
        return true;
    }

    /// <summary><c>garbage_collect_clauses(+Name/Arity)</c> — chunk 150.
    /// Same as the 0-arg form but restricted to a single predicate.</summary>
    public static bool GarbageCollectClauses1(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "garbage_collect_clauses/1 requires a PrologEngine host.");
        Term spec = MaterializeRegister(engine, 0);
        if (spec is CompoundTerm c && c.Functor == "/" && c.Args.Length == 2
            && c.Args[0] is AtomTerm name && c.Args[1] is IntTerm arity)
        {
            int fid = FunctorTable.Intern(
                AtomTable.Intern(name.Name, permanent: true).Id, (int)arity.Value);
            host.GarbageCollectClauses(engine, fid);
            return true;
        }
        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        throw new ShumwayPrologException(
            IsoError.TypeError("predicate_indicator", spec));
    }

    /// <summary><c>compact_dynamic_buffer/0</c> — Phase-11 chunk 157.
    /// Invalidates the persistent dynamic-code buffer so the next
    /// query rebuilds it from current <c>_dynamicClauses</c>.
    /// Reclaims memory consumed by chain entries and clause bodies
    /// appended by in-place assertz / asserta / retract that are no
    /// longer reachable from any current clause. The rebuild cost
    /// is one re-link of the dynamic region on the next query;
    /// chunks 155b-f then start fresh at append-only growth, so
    /// callers should invoke compaction periodically (e.g. after a
    /// large batch of mutations) rather than per-mutation.
    /// </summary>
    public static bool CompactDynamicBuffer(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "compact_dynamic_buffer/0 requires a PrologEngine host.");
        host.CompactDynamicCodeBuffer();
        return true;
    }

    /// <summary><c>compact_dynamic_buffer(+Name/Arity)</c> — Phase-12
    /// chunk 158 per-predicate variant. Validates the predicate
    /// indicator, errors on bad inputs (instantiation /
    /// type_error / domain_error / permission_error for non-
    /// dynamic), then falls through to the same full rebuild as
    /// the 0-arg form. The persistent buffer holds every dynamic
    /// predicate's bytecode interleaved, so independent per-
    /// predicate compaction isn't feasible without partial-relink
    /// support — the API surface is per-predicate as a forward-
    /// compatibility hint.</summary>
    public static bool CompactDynamicBuffer1(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "compact_dynamic_buffer/1 requires a PrologEngine host.");
        Term spec = MaterializeRegister(engine, 0);
        if (spec is VarTerm)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (spec is not CompoundTerm c || c.Functor != "/" || c.Args.Length != 2
            || c.Args[0] is not AtomTerm nameAtom || c.Args[1] is not IntTerm arityInt)
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator", spec));
        int fid = FunctorTable.Intern(
            AtomTable.Intern(nameAtom.Name, permanent: true).Id, (int)arityInt.Value);
        if (!host.IsDynamic(fid))
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "modify,static_procedure");
        host.CompactDynamicCodeBuffer();
        return true;
    }

    /// <summary>Promotes a Core-level <see cref="PrologRuntimeException"/>
    /// into the canonical ISO <c>error(Kind, _)</c> Prolog term that
    /// user-written catchers expect.</summary>
    /// <summary>Builds the three-argument
    /// <c>permission_error(Op, ObjType, Obj)</c> from a Detail string
    /// shaped <c>"Op,ObjType"</c>. The Obj slot is a fresh anonymous
    /// variable — PrologRuntimeException can't carry a Term payload
    /// yet, so the offending object is lost in translation; a catcher
    /// can still pattern-match on Op and ObjType.</summary>
    private static Term BuildPermissionError(PrologRuntimeException re)
    {
        string[] parts = re.Detail.Split(',', 2);
        string op = parts.Length > 0 ? parts[0] : "?";
        string objType = parts.Length > 1 ? parts[1] : "?";
        return new CompoundTerm("permission_error", new Term[]
        {
            new AtomTerm(op),
            new AtomTerm(objType),
            ValueTermOrVar(re),
        });
    }

    /// <summary>Builds the ISO Context indicator <c>Name/Arity</c> from
    /// the exception's stamped builtin identity (chunk 130), or returns
    /// <c>null</c> when no builtin stamped it — meaning the throw arose
    /// outside builtin dispatch (e.g. from the bytecode interpreter's
    /// undefined-procedure resolver) and the Context should fall back
    /// to a fresh anonymous variable.</summary>
    private static Term? StampedContext(PrologRuntimeException re) =>
        re.BuiltinName is string name
            ? new CompoundTerm("/",
                new Term[] { new AtomTerm(name), new IntTerm(re.BuiltinArity) })
            : null;

    /// <summary>Constructs <c>error(Inner, Context)</c> with the
    /// stamped Context if one is available, falling back to a fresh
    /// anonymous variable when the exception predates any builtin
    /// dispatch.</summary>
    private static Term WrapWithStampedContext(Term inner, PrologRuntimeException re) =>
        new CompoundTerm("error",
            new Term[] { inner, StampedContext(re) ?? new VarTerm("_") });

    internal static Term TranslateRuntimeError(PrologRuntimeException re) => re.Kind switch
    {
        "evaluation_error" => WrapWithStampedContext(
            new CompoundTerm("evaluation_error", new Term[] { new AtomTerm(re.Detail) }), re),
        "instantiation_error" => WrapWithStampedContext(
            new AtomTerm("instantiation_error"), re),
        // Chunk 144: type_error / domain_error now report the
        // offending value in the second slot when the throw site
        // captured it.
        "type_error" => WrapWithStampedContext(
            new CompoundTerm("type_error",
                new Term[] { new AtomTerm(re.Detail), ValueTermOrVar(re) }), re),
        "existence_error" => WrapWithStampedContext(
            new CompoundTerm("existence_error",
                new Term[] { new AtomTerm("procedure"), ProcedureIndicatorTerm(re.Detail) }), re),
        "domain_error" => WrapWithStampedContext(
            new CompoundTerm("domain_error",
                new Term[] { new AtomTerm(re.Detail), ValueTermOrVar(re) }), re),
        "representation_error" => WrapWithStampedContext(
            new CompoundTerm("representation_error", new Term[] { new AtomTerm(re.Detail) }), re),
        "syntax_error" => WrapWithStampedContext(
            new CompoundTerm("syntax_error", new Term[] { new AtomTerm(re.Detail) }), re),
        "resource_error" => WrapWithStampedContext(
            new CompoundTerm("resource_error", new Term[] { new AtomTerm(re.Detail) }), re),
        // Chunk 131e: ISO permission_error has three args. The Detail
        // string encodes "Operation,ObjectType" (e.g. "modify,static_procedure");
        // we split on the comma and put a fresh var in the Obj slot
        // (chunk 144 carries the offending object too when present).
        "permission_error" => WrapWithStampedContext(
            BuildPermissionError(re), re),
        "system_error" => WrapWithStampedContext(
            string.IsNullOrEmpty(re.Detail)
                ? (Term)new AtomTerm("system_error")
                : new CompoundTerm("system_error", new Term[] { new AtomTerm(re.Detail) }),
            re),
        _ => new CompoundTerm("error",
            new Term[] { new AtomTerm(re.Kind), new AtomTerm(re.Detail) }),
    };

    /// <summary>Returns the captured offending term (from
    /// <see cref="PrologRuntimeException.Value"/>) when the throw site
    /// snapshotted one, or a fresh anonymous var otherwise. (Chunk 144.)
    /// </summary>
    private static Term ValueTermOrVar(PrologRuntimeException re) =>
        re.Value as Term ?? new VarTerm("_");

    /// <summary>Builds the procedure-indicator term for an
    /// <c>existence_error(procedure, Name/Arity)</c> from the
    /// <see cref="PrologRuntimeException.Detail"/> string
    /// <c>"Name/Arity"</c> (as written by
    /// <see cref="PrologRuntimeException.UndefinedProcedure"/>). ISO requires
    /// the culprit to be the COMPOUND <c>'/'(Name, Arity)</c>, not an atom whose
    /// name happens to be <c>"Name/Arity"</c> — otherwise a catcher pattern
    /// <c>error(existence_error(procedure, foo/3), _)</c> can never unify with
    /// the ball. Splits on the LAST <c>/</c> (so a quoted name containing a
    /// slash, e.g. <c>'a/b'/2</c>, still resolves correctly) and falls back to
    /// the bare atom if the suffix isn't a non-negative integer.</summary>
    private static Term ProcedureIndicatorTerm(string detail)
    {
        int slash = detail.LastIndexOf('/');
        if (slash > 0 && slash < detail.Length - 1
            && int.TryParse(detail.AsSpan(slash + 1), out int arity) && arity >= 0)
            return new CompoundTerm("/",
                new Term[] { new AtomTerm(detail.Substring(0, slash)), new IntTerm(arity) });
        return new AtomTerm(detail);
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
        // Chunk 136: ISO §7.8.10.3.a — an unbound ball is
        // instantiation_error. (Other shapes are user-defined and
        // pass through verbatim.)
        if (error is VarTerm)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        throw new ShumwayPrologException(error);
    }

    // catch/3 is now a prelude predicate built on the chunk-85 catch-frame
    // plumbing ($catch_begin/$catch_end), running the guarded goal in the LIVE
    // engine. The old isolated-sub-engine builtin (which ran Goal in a peer
    // sub-engine and bound back only the first solution) was removed — it hid
    // the guarded goal's assert/retract and other side effects from the caller,
    // and was only ever the fallback for a variable Goal/Recovery anyway (a
    // statically-callable catch/3 is rewritten inline by MetaTransform). See
    // Prelude catch/3.

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
        // DEAD PATH — must never run. call/N is dispatched IN THE LIVE ENGINE:
        // the call_builtin opcode handler sees the builtin's IsCall flag and
        // routes to BytecodeInterpreter.DispatchCall (Tier-0) — and the Tier-1
        // IL emit routes through IlMetaCallHelper.Dispatch — both of which run
        // the goal directly in this engine (so assert/retract from the called
        // goal are visible to the caller, per Phase 4 + chunks 86/88/205). This
        // builtin body (the historical isolated-sub-engine fallback) is never
        // reached. The sub-engine deep-copies the dynamic store, so if it DID
        // run, side effects from the called goal would silently not bleed back —
        // a correctness bug. Fail loudly instead of producing wrong answers.
        _ = totalArity;
        throw new InvalidOperationException(
            "call/N reached the sub-engine fallback in MetaBuiltins.CallN, but " +
            "call/N must be dispatched in the live engine by DispatchCall (Tier-0) " +
            "or IlMetaCallHelper (Tier-1). Reaching here means the IsCall meta-" +
            "dispatch routing was bypassed — a bug to fix at the dispatch site, " +
            "not here.");
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
    /// <summary><c>repeat/0</c> — succeeds, and pushes a choice point that
    /// re-succeeds on every backtrack, re-arming itself each time. The
    /// classic non-terminating generator for failure-driven loops.</summary>
    /// <summary><c>garbage_collect/0</c> (ADR-016) — mark-compacts the
    /// heap. A no-op when attributed variables are in use (the collector
    /// bails) or when there is nothing to reclaim.</summary>
    public static bool GarbageCollect(Engine engine)
    {
        engine.CollectHeap();
        return true;
    }

    public static bool Repeat(Engine engine)
    {
        ArmRepeat(engine, engine.BuiltinReturnPc);
        return true;
    }

    /// <summary>ADR-022 — runs the embedded native block named by argument 0,
    /// with its Prolog variables in registers 1.. (see the registration in
    /// <see cref="EnsureRegistered"/>).</summary>
    public static bool NativeRun(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new System.InvalidOperationException("'$native_run' requires a PrologEngine host.");
        var nameTerm = RegisterMarshalling.ReadRegisterAsTerm(engine, 0);
        if (nameTerm is not AtomTerm at)
            throw new System.InvalidOperationException("'$native_run': block name must be an atom.");
        var block = host.NativeBlock(at.Name)
            ?? throw new System.InvalidOperationException(
                $"'$native_run': native block '{at.Name}' is not registered.");
        // Compile the block to a delegate on first execution (in engine context,
        // so interop resolves to concrete methods); cache it, with the interpreter
        // as the fallback when compilation isn't possible (an unsupported
        // construct, or Native AOT). Item 2 — replaces the interpreter on the hot
        // path with JIT-compiled IL.
        if (!block.CompileTried)
        {
            block.Compiled = NativeBlockCompiler.TryCompile(
                block.Vars, block.Stmts, regOffset: 1, host.ResolveNativeInterop);
            block.CompileTried = true;
        }
        return block.Compiled is not null
            ? block.Compiled(engine)
            : NativeBlockRunner.RunBlock(engine, block.Vars, block.Stmts, regOffset: 1);
    }

    // ----- ADR-024 generic-term interop (reftype tier) -----------------------

    /// <summary>Extracts the <see cref="TermSlot"/> a register holds (a Foreign
    /// cell, surfaced as <c>'$foreign'(Id)</c> when read as a term), or null.</summary>
    private static TermSlot? ReadSlot(Engine engine, int reg)
    {
        var t = RegisterMarshalling.ReadRegisterAsTerm(engine, reg);
        if (t is CompoundTerm { Functor: "$foreign", Args.Length: 1 } ct
            && ct.Args[0] is IntTerm id)
            return engine.AsForeign<TermSlot>(Shumway.Core.Cell.Foreign((int)id.Value));
        return null;
    }

    /// <summary>ADR-024 — creates a fresh empty term slot and binds it (as a
    /// Foreign cell) to argument 0. Used to obtain a reftype where a `:- c`
    /// region's <c>reftype</c> global isn't available (tests, and any predicate
    /// that needs an ad-hoc slot).</summary>
    public static bool NewReftypeSlot(Engine engine)
        => engine.UnifyRegisterWithCell(0, engine.MakeForeign(new TermSlot()));

    /// <summary>ADR-024 — <c>fill_par(Term, RefType)</c>: store the Prolog term in
    /// the slot (term → cursor). Zero-copy at the AST level — the term is read as
    /// it currently stands.</summary>
    public static bool FillPar(Engine engine)
    {
        var slot = ReadSlot(engine, 1);
        if (slot is null) return false;
        slot.SetValue(RegisterMarshalling.ReadRegisterAsTerm(engine, 0));
        return true;
    }

    /// <summary>ADR-024 — <c>reftype_term(Term, RefType)</c>: materialize the
    /// slot's cursor to a Prolog term and unify it with argument 0 (cursor →
    /// term).</summary>
    public static bool ReftypeTerm(Engine engine)
    {
        var slot = ReadSlot(engine, 1);
        if (slot is null) return false;
        return RegisterMarshalling.UnifyRegisterWithTerm(engine, 0, slot.Materialize());
    }

    /// <summary>ADR-024 — <c>preftype(RefType)</c>: succeeds when argument 0 is a
    /// valid reftype slot.</summary>
    public static bool Preftype(Engine engine) => ReadSlot(engine, 0) is not null;

    private static void ArmRepeat(Engine engine, int returnPc)
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
        public readonly Func<Engine, int, bool> Resume;

        public RepeatCursor(int returnPc)
        {
            _returnPc = returnPc;
            Resume = Step;
        }

        private bool Step(Engine engine, int _)
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
    // consult / reconsult  (chunk 235)
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
    public static bool Consult(Engine engine)
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

        host.ConsultFile(path);
        return true;
    }

    /// <summary><c>use_module(+Spec)</c> — SWI-style library loader. With
    /// <c>library(Name)</c> loads a built-in library (currently
    /// <c>clpfd</c> and <c>clpr</c>). With an atom, behaves like
    /// <see cref="Consult"/>.</summary>
    public static bool UseModule(Engine engine)
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
    public static bool SaveState1(Engine engine)
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
    public static bool SaveState2(Engine engine)
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

    /// <summary><c>restore_state(+File)</c> — loads a snapshot previously
    /// written by <c>save_state/1,2</c>. See
    /// <see cref="PrologEngine.RestoreState"/>.</summary>
    public static bool RestoreState1(Engine engine)
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

    private static string RequireAtomPath(Engine engine, int register, string builtin)
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
    public static bool Reconsult(Engine engine)
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

    public static bool Assertz(Engine engine) => AssertImpl(engine, prepend: false);
    public static bool Asserta(Engine engine) => AssertImpl(engine, prepend: true);

    private static bool AssertImpl(Engine engine, bool prepend)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "assert: PrologEngine host required.");

        Term clauseTerm = MaterializeRegister(engine, 0);
        var clause = Shumway.Compiler.Ast.Clause.From(clauseTerm);
        // Chunk 427: Asserta/Assertz extract the head functor id anyway —
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
    public static bool RetractAllModifiable(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$retractall_modifiable'/1: PrologEngine host required.");
        int headHeap = engine.MaterializeRegisterForTrace(0);
        int fid = ReadPatternHeadFunctorId(engine, headHeap);
        return host.IsRetractAllModifiable(fid);
    }

    public static bool Retract(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "retract: PrologEngine host required.");

        // Chunk 168: read the pattern's head functor id straight from
        // the heap, without materialising the whole pattern as a
        // Term AST first. ExtractHeadFunctorIdFromClause walked the
        // freshly-built AST and re-interned the head's name —
        // measured ~7% of total Blint.pl time, all of which was
        // wasted: the heap representation already has the functor id
        // sitting one slot inside the STR.
        int patternHeap = engine.MaterializeRegisterForTrace(0);
        int patternFid = ReadPatternHeadFunctorId(engine, patternHeap);

        // Chunk 131e: ISO §7.12.2.h — retracting from a static
        // predicate is permission_error(modify, static_procedure, _),
        // not a silent failure. The check fires after the head's type
        // check (above) so type errors win precedence.
        if (!host.IsDynamic(patternFid))
            throw new Shumway.Core.PrologRuntimeException(
                "permission_error", "modify,static_procedure");

        // Chunk 421: scan the LIVE clause list directly — no snapshot copy.
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
    /// heap (chunk 168). Mirrors the ISO callability check in
    /// <see cref="ExtractHeadFunctorIdFromClause"/> but avoids the
    /// Term AST allocation — for retract's hot path the heap shape
    /// is sufficient.</summary>
    private static int ReadPatternHeadFunctorId(Engine engine, int patternHeap)
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

    private static int ReadFunctorIdFromCell(Engine engine, Cell c)
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
    /// pattern — the entry step, scanning the LIVE clause list (chunk 421:
    /// no snapshot copy; nothing can mutate the list before this scan
    /// completes). When later candidates remain it leaves a choice point
    /// whose resume retracts the following match — that is what makes
    /// <c>retract/1</c> enumerate every matching clause on backtracking.</summary>
    private static bool RetractStep(Engine engine, PrologEngine host,
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
            // Chunk 421: snapshot ONLY the remaining candidates into the
            // resume closure, here at push time (still call time, so the
            // ISO logical-update view is the same one a full up-front copy
            // captured). The live list mutates the moment the retract
            // returns; the resume must not read it.
            //
            // chunk 431: the copy lands in a pooled per-engine buffer
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

        // Chunk 423: the first step's candidates ARE the live list, so
        // matchIndex is the live index — pass it through to skip the
        // O(N) IndexOf.
        host.RemoveDynamicByReference(engine, patternFid, candidate,
            knownIndex: matchIndex);
        engine.SetHb(savedHb);
        return true;
    }

    /// <summary>chunk 431 — resume state for a backtrackable <c>retract/1</c>
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
        public readonly Func<Engine, int, bool> Resume;
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

        private bool Step(Engine engine)
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
    /// <para>Chunk 421: each trial used to materialise the WHOLE candidate
    /// clause onto the engine heap before unifying — for a keyed retract
    /// over a long predicate (Blint's <c>retract(saved_cur_line_i(Line,_))</c>
    /// over ~125 clauses) that is ~K clause materialisations per call, all
    /// but one rolled back. <see cref="DefiniteMismatch"/> now skips a
    /// candidate on a PROVEN structural mismatch (distinct atoms / ints /
    /// functors at the same position) with zero allocation; only candidates
    /// it cannot refute pay the materialise-and-unify trial.</para></summary>
    private static int FindRetractMatch(
        Engine engine, IReadOnlyList<Clause> candidates, int startIndex,
        int endExclusive, int patternHeap)
    {
        // chunk 431: endExclusive bounds the scan explicitly — a resume's
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

    /// <summary>Chunk 421 — true only when the pattern at
    /// <paramref name="heapIdx"/> PROVABLY cannot unify with the candidate
    /// AST <paramref name="ast"/>: distinct atoms, distinct inline ints,
    /// distinct principal functors, or an atomic vs a compound. Anything
    /// uncertain — variables on either side, big integers, floats vs the
    /// float table, partial strings, foreigns, depth exhausted — returns
    /// false and the caller falls back to the real materialise-and-unify
    /// trial, so this can only SKIP work, never change the outcome.</summary>
    private static bool DefiniteMismatch(Engine engine, int heapIdx, Term ast, int depth)
    {
        if (depth <= 0 || ast is VarTerm) return false;
        int idx = engine.Deref(heapIdx);
        Cell c = engine.GetHeap(idx);
        switch (c.Tag)
        {
            case Tag.Atom:
                return ast switch
                {
                    // chunk 431: cached id — this used to re-intern the
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
                        // chunk 431: functor ids are canonical (one id per
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
            // chunk 431: read-through the node's cached ids. The atom is
            // interned transient here, but every asserted clause is also
            // compiled by ClauseCompiler, whose InternAtom pins predicate
            // and literal names permanent — promotion keeps the id, so the
            // cache stays valid.
            AtomTerm a => FunctorTable.Intern(a.ResolveAtomId(), 0),
            CompoundTerm c => c.ResolveFunctorId(),
            // Chunk 131e: ISO §8.9.3 — an unbound head raises
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

    // forall/2 is now a prelude predicate (live-engine \+ (call(C), \+ call(A)));
    // the old isolated-sub-engine builtin was removed (it hid the called goals'
    // assert/retract). See Prelude forall/2.

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
        // Chunk 135: ISO §8.10.1.3 / §8.10.2.3 / §8.10.3.3 — Goal must
        // be callable. A var (after materialisation) is instantiation_error;
        // anything else non-callable is type_error(callable, _).
        if (goal is VarTerm)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (goal is not AtomTerm && goal is not CompoundTerm)
            throw new Shumway.Core.PrologRuntimeException("type_error", "callable");
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
        Canonicalize(MaterializeRegister(engine, 0), sb,
            new Dictionary<string, int>());
        return host.RegisterTablingKey(sb.ToString());
    }

    /// <summary><c>'$tbl_seen_clear'/0</c> (chunk 107) — empties the
    /// engine's tabling key set, so a later re-derivation of a subgoal is
    /// not deduplicated against answers from before a table invalidation.</summary>
    public static bool TableSeenClear(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$tbl_seen_clear'/0 requires a PrologEngine host.");
        host.ClearTablingKeys();
        return true;
    }

    /// <summary><c>'$tbl_solve_complete'(+Goal)</c> (chunk 107) — succeeds
    /// iff <paramref name="Goal"/> has at least one solution when run to a
    /// <em>complete</em> tabled evaluation. It runs in a sub-engine whose
    /// table is first abolished, so the negated subgoal's fixpoint is
    /// computed in full and in isolation — which is what makes <c>\+</c>
    /// over a tabled goal sound for a stratified program.</summary>
    public static bool TableSolveComplete(Engine engine)
    {
        if (engine.Host is not PrologEngine host)
            throw new InvalidOperationException(
                "'$tbl_solve_complete'/1 requires a PrologEngine host.");
        Term goal = MaterializeRegister(engine, 0);
        Term wrapped = new CompoundTerm(",",
            new[] { (Term)new AtomTerm("abolish_all_tables"), goal });
        var sub = host.CreateSubEngine();
        foreach (var _ in sub.QueryAll(wrapped))
            return true;
        return false;
    }

    /// <summary>Appends a structurally faithful, injective encoding of a
    /// term to <paramref name="sb"/> — length-prefixed names so no two
    /// distinct terms can collide. Variables are encoded by first-occurrence
    /// index (tracked in <paramref name="vars"/>), so the encoding is
    /// invariant under variable renaming: two variant non-ground answers
    /// (e.g. <c>p(X)</c> and <c>p(Y)</c>) canonicalise to the same string
    /// and the tabling driver deduplicates them as one answer.</summary>
    private static void Canonicalize(
        Term t, System.Text.StringBuilder sb, Dictionary<string, int> vars)
    {
        switch (t)
        {
            case VarTerm v:
                if (!vars.TryGetValue(v.Name, out int vid))
                {
                    vid = vars.Count;
                    vars[v.Name] = vid;
                }
                sb.Append('v').Append(vid).Append('.');
                break;
            case AtomTerm a:
                sb.Append('a').Append(a.Name.Length).Append('_').Append(a.Name);
                break;
            case IntTerm i:
                sb.Append('i').Append(i.Value).Append('.');
                break;
            case CompoundTerm c:
                sb.Append('c').Append(c.Functor.Length).Append('_').Append(c.Functor)
                  .Append('/').Append(c.Args.Length).Append('(');
                foreach (var arg in c.Args) Canonicalize(arg, sb, vars);
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

    // ============================================================================
    // Phase 24 chunk 266 — Arity-Prolog recorded database.
    // See RecordedDatabase.cs for the storage layer.
    // ============================================================================

    public static bool Recorda3(Engine engine) => RecordImpl(engine, atFront: true);
    public static bool Recordz3(Engine engine) => RecordImpl(engine, atFront: false);

    private static bool RecordImpl(Engine engine, bool atFront)
    {
        PrologEngine host = RequireHost(engine, atFront ? "recorda/3" : "recordz/3");
        Term key = RequireGroundKey(engine, register: 0, builtin: atFront ? "recorda/3" : "recordz/3");
        Term term = MaterializeRegister(engine, 1);
        int @ref = atFront
            ? host.Records.Recorda(key, term)
            : host.Records.Recordz(key, term);
        return engine.UnifyRegisterWithCell(2, Cell.Int(@ref));
    }

    public static bool Recorded3(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "recorded/3");
        Term key = RequireGroundKey(engine, register: 0, builtin: "recorded/3");
        var entries = host.Records.Recorded(key).ToList();
        if (entries.Count == 0) return false;
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, entries.Count, 3, returnPc,  // arity 3 (recorded/3)
            (e, i) => RecordedUnify(e, entries, i));
    }

    private static bool RecordedUnify(
        Engine engine, List<(int Ref, Term Term)> entries, int index)
    {
        var (refVal, termVal) = entries[index];
        Cell termCell = Materializer.MaterializeAsCell(engine, termVal);
        if (!engine.UnifyRegisterWithCell(1, termCell)) return false;
        if (!engine.UnifyRegisterWithCell(2, Cell.Int(refVal))) return false;
        return true;
    }

    public static bool Erase1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "erase/1");
        int @ref = RequireIntRef(engine, register: 0, builtin: "erase/1");
        return host.Records.Erase(@ref);
    }

    public static bool EraseAll1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "eraseall/1");
        Term key = RequireGroundKey(engine, register: 0, builtin: "eraseall/1");
        host.Records.EraseAll(key);
        return true;
    }

    public static bool Instance2(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "instance/2");
        int @ref = RequireIntRef(engine, register: 0, builtin: "instance/2");
        Term? stored = host.Records.Instance(@ref);
        if (stored is null) return false;
        Cell c = Materializer.MaterializeAsCell(engine, stored);
        return engine.UnifyRegisterWithCell(1, c);
    }

    public static bool KeyCount2(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "key_count/2");
        Term key = RequireGroundKey(engine, register: 0, builtin: "key_count/2");
        int count = host.Records.KeyCount(key);
        return engine.UnifyRegisterWithCell(1, Cell.Int(count));
    }

    public static bool Keys1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "keys/1");
        Cell keyCell = MaterializeRegisterAsCell(engine, 0);
        if (keyCell.Tag != Tag.Ref && keyCell.Tag != Tag.AttVar)
        {
            // Ground (or partially bound): treat as membership test.
            Term k = MaterializeRegister(engine, 0);
            return host.Records.KeyCount(k) > 0;
        }
        // Unbound: enumerate every key on backtracking.
        var keys = host.Records.AllKeys().ToList();
        if (keys.Count == 0) return false;
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, keys.Count, 1, returnPc,  // arity 1 (keys/1)
            (e, i) => KeysUnify(e, keys, i));
    }

    private static bool KeysUnify(Engine engine, List<Term> keys, int index)
    {
        Cell c = Materializer.MaterializeAsCell(engine, keys[index]);
        return engine.UnifyRegisterWithCell(0, c);
    }

    public static bool Ref1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "ref/1");
        Cell cell = MaterializeRegisterAsCell(engine, 0);
        return cell.Tag == Tag.Int && host.Records.ContainsRef((int)cell.AsInt);
    }

    public static bool Replace2(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "replace/2");
        int @ref = RequireIntRef(engine, register: 0, builtin: "replace/2");
        Term newTerm = MaterializeRegister(engine, 1);
        return host.Records.Replace(@ref, newTerm);
    }

    public static bool Nref2(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "nref/2");
        int @ref = RequireIntRef(engine, register: 0, builtin: "nref/2");
        int? next = host.Records.NextRef(@ref);
        if (next is null) return false;
        return engine.UnifyRegisterWithCell(1, Cell.Int(next.Value));
    }

    public static bool Pref2(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "pref/2");
        int @ref = RequireIntRef(engine, register: 0, builtin: "pref/2");
        int? prev = host.Records.PrevRef(@ref);
        if (prev is null) return false;
        return engine.UnifyRegisterWithCell(1, Cell.Int(prev.Value));
    }

    public static bool RecordAfter3(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "record_after/3");
        int @ref = RequireIntRef(engine, register: 0, builtin: "record_after/3");
        Term term = MaterializeRegister(engine, 1);
        int? newRef = host.Records.RecordAfter(@ref, term);
        if (newRef is null) return false;
        return engine.UnifyRegisterWithCell(2, Cell.Int(newRef.Value));
    }

    public static bool RecordBefore3(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "record_before/3");
        int @ref = RequireIntRef(engine, register: 0, builtin: "record_before/3");
        Term term = MaterializeRegister(engine, 1);
        int? newRef = host.Records.RecordBefore(@ref, term);
        if (newRef is null) return false;
        return engine.UnifyRegisterWithCell(2, Cell.Int(newRef.Value));
    }

    // ---- shared validation helpers ----

    private static PrologEngine RequireHost(Engine engine, string builtin)
        => engine.Host as PrologEngine
            ?? throw new InvalidOperationException(
                $"{builtin} requires the engine to be hosted by a PrologEngine.");

    private static Term RequireGroundKey(Engine engine, int register, string builtin)
    {
        Cell cell = MaterializeRegisterAsCell(engine, register);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        return MaterializeRegister(engine, register);
    }

    private static int RequireIntRef(Engine engine, int register, string builtin)
    {
        Cell cell = MaterializeRegisterAsCell(engine, register);
        if (cell.Tag == Tag.Ref || cell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (cell.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException(
                $"type_error(db_reference, _) /* {builtin} */");
        return (int)cell.AsInt;
    }

    // ============================================================================
    // Phase 24 chunk 267 — Edinburgh-style I/O (Arity-Prolog compatible).
    // Thin layer over the chunk-140 StreamRegistry: see/tell open a file and
    // make it the current input/output; seen/told close it and revert to
    // user_input/user_output. get/get0/put/skip operate on character codes.
    // ============================================================================

    public static bool See1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "see/1");
        string path = RequireAtomPath(engine, register: 0, builtin: "see/1");
        var streams = engine.Streams!;
        // Close any previously-see'd file before switching.
        if (!ReferenceEquals(streams.CurrentInput, streams.UserInput))
            CloseAndForget(streams, streams.CurrentInput);
        StreamHandle h;
        try
        {
            h = new StreamHandle(
                streams.NextId(), new StreamReader(path), "read", path);
        }
        catch (FileNotFoundException)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        }
        catch (DirectoryNotFoundException)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        }
        streams.Add(h);
        streams.SetCurrentInput(h);
        return true;
    }

    public static bool Seeing1(Engine engine)
    {
        var streams = engine.Streams!;
        Cell nameCell = ReferenceEquals(streams.CurrentInput, streams.UserInput)
            ? Cell.Atom(AtomTable.Intern("user", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(
                streams.CurrentInput.Filename ?? "user", permanent: true).Id);
        return engine.UnifyRegisterWithCell(0, nameCell);
    }

    public static bool Seen0(Engine engine)
    {
        var streams = engine.Streams!;
        if (!ReferenceEquals(streams.CurrentInput, streams.UserInput))
            CloseAndForget(streams, streams.CurrentInput);
        streams.SetCurrentInput(streams.UserInput);
        return true;
    }

    public static bool Tell1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "tell/1");
        string path = RequireAtomPath(engine, register: 0, builtin: "tell/1");
        var streams = engine.Streams!;
        if (!ReferenceEquals(streams.CurrentOutput, streams.UserOutput))
            CloseAndForget(streams, streams.CurrentOutput);
        StreamHandle h;
        try
        {
            h = new StreamHandle(
                streams.NextId(), new StreamWriter(path, append: false), "write", path);
        }
        catch (DirectoryNotFoundException)
        {
            throw new Shumway.Core.PrologRuntimeException(
                $"existence_error(source_sink, '{path}')");
        }
        streams.Add(h);
        streams.SetCurrentOutput(h);
        return true;
    }

    public static bool Telling1(Engine engine)
    {
        var streams = engine.Streams!;
        Cell nameCell = ReferenceEquals(streams.CurrentOutput, streams.UserOutput)
            ? Cell.Atom(AtomTable.Intern("user", permanent: true).Id)
            : Cell.Atom(AtomTable.Intern(
                streams.CurrentOutput.Filename ?? "user", permanent: true).Id);
        return engine.UnifyRegisterWithCell(0, nameCell);
    }

    public static bool Told0(Engine engine)
    {
        var streams = engine.Streams!;
        if (!ReferenceEquals(streams.CurrentOutput, streams.UserOutput))
            CloseAndForget(streams, streams.CurrentOutput);
        streams.SetCurrentOutput(streams.UserOutput);
        return true;
    }

    private static void CloseAndForget(StreamRegistry registry, StreamHandle h)
    {
        try { h.Reader?.Dispose(); h.Writer?.Flush(); h.Writer?.Dispose(); }
        catch { /* best-effort close */ }
        registry.Remove(h);
    }

    // ---- get / get0 / put / skip — character-code I/O ----

    public static bool Get1(Engine engine) => ReadPrintableCodeImpl(engine, useStreamReg: false);
    public static bool Get2(Engine engine) => ReadPrintableCodeImpl(engine, useStreamReg: true);
    public static bool Get0_1(Engine engine) => ReadAnyCodeImpl(engine, useStreamReg: false);
    public static bool Get0_2(Engine engine) => ReadAnyCodeImpl(engine, useStreamReg: true);
    public static bool Put1(Engine engine) => WriteCodeImpl(engine, useStreamReg: false);
    public static bool Put2(Engine engine) => WriteCodeImpl(engine, useStreamReg: true);
    public static bool Skip1(Engine engine) => SkipImpl(engine, useStreamReg: false);
    public static bool Skip2(Engine engine) => SkipImpl(engine, useStreamReg: true);

    private static StreamHandle ResolveInputStream(Engine engine, bool fromStreamArg)
    {
        if (!fromStreamArg)
            return engine.Streams!.CurrentInput;
        Cell streamCell = MaterializeRegisterAsCell(engine, 0);
        if (streamCell.Tag == Tag.Foreign
            && engine.AsForeign(streamCell) is StreamHandle h)
            return h;
        if (streamCell.Tag == Tag.Atom)
        {
            string alias = AtomTable.GetById(streamCell.AsAtomId)?.Name ?? "";
            var aliased = engine.Streams!.GetByAlias(alias);
            if (aliased is not null) return aliased;
        }
        throw new Shumway.Core.PrologRuntimeException(
            "type_error(stream, _)");
    }

    private static StreamHandle ResolveOutputStream(Engine engine, bool fromStreamArg)
    {
        if (!fromStreamArg)
            return engine.Streams!.CurrentOutput;
        Cell streamCell = MaterializeRegisterAsCell(engine, 0);
        if (streamCell.Tag == Tag.Foreign
            && engine.AsForeign(streamCell) is StreamHandle h)
            return h;
        if (streamCell.Tag == Tag.Atom)
        {
            string alias = AtomTable.GetById(streamCell.AsAtomId)?.Name ?? "";
            var aliased = engine.Streams!.GetByAlias(alias);
            if (aliased is not null) return aliased;
        }
        throw new Shumway.Core.PrologRuntimeException(
            "type_error(stream, _)");
    }

    private static bool ReadPrintableCodeImpl(Engine engine, bool useStreamReg)
    {
        var h = ResolveInputStream(engine, useStreamReg);
        if (!h.IsReader)
            throw new Shumway.Core.PrologRuntimeException("permission_error(input, stream)");
        // Skip codes < 32 (ASCII control / whitespace).
        int code;
        do { code = h.Reader!.Read(); }
        while (code >= 0 && code < 32);
        int regOut = useStreamReg ? 1 : 0;
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(code));
    }

    private static bool ReadAnyCodeImpl(Engine engine, bool useStreamReg)
    {
        var h = ResolveInputStream(engine, useStreamReg);
        if (!h.IsReader)
            throw new Shumway.Core.PrologRuntimeException("permission_error(input, stream)");
        int code = h.Reader!.Read();
        int regOut = useStreamReg ? 1 : 0;
        return engine.UnifyRegisterWithCell(regOut, Cell.Int(code));
    }

    private static bool WriteCodeImpl(Engine engine, bool useStreamReg)
    {
        var h = ResolveOutputStream(engine, useStreamReg);
        if (!h.IsWriter)
            throw new Shumway.Core.PrologRuntimeException("permission_error(output, stream)");
        int regCode = useStreamReg ? 1 : 0;
        Cell c = MaterializeRegisterAsCell(engine, regCode);
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error(integer, _)");
        long code = c.AsInt;
        if (code < 0 || code > char.MaxValue)
            throw new Shumway.Core.PrologRuntimeException(
                "representation_error(character_code)");
        h.Writer!.Write((char)code);
        return true;
    }

    private static bool SkipImpl(Engine engine, bool useStreamReg)
    {
        var h = ResolveInputStream(engine, useStreamReg);
        if (!h.IsReader)
            throw new Shumway.Core.PrologRuntimeException("permission_error(input, stream)");
        int regCode = useStreamReg ? 1 : 0;
        Cell c = MaterializeRegisterAsCell(engine, regCode);
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (c.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error(integer, _)");
        int target = (int)c.AsInt;
        int code;
        do { code = h.Reader!.Read(); }
        while (code >= 0 && code != target);
        return true;
    }

    public static bool Tab2(Engine engine)
    {
        var h = ResolveOutputStream(engine, fromStreamArg: true);
        if (!h.IsWriter)
            throw new Shumway.Core.PrologRuntimeException("permission_error(output, stream)");
        Cell n = MaterializeRegisterAsCell(engine, 1);
        if (n.Tag == Tag.Ref || n.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (n.Tag != Tag.Int)
            throw new Shumway.Core.PrologRuntimeException("type_error(integer, _)");
        long count = n.AsInt;
        for (long i = 0; i < count; i++) h.Writer!.Write(' ');
        return true;
    }

    // ============================================================================
    // Phase 24 chunk 268 (partial) — Arity-Prolog string<->term + search.
    // "string" in Arity means atom; these are write-/writeq-style variants of
    // term_to_atom/2, plus a backtrackable substring search.
    // ============================================================================

    public static bool StringTerm2(Engine engine) => StringTermImpl(engine, quoted: false);
    public static bool StringTermq2(Engine engine) => StringTermImpl(engine, quoted: true);

    private static bool StringTermImpl(Engine engine, bool quoted)
    {
        Cell atomCell = ResolveLocal(engine, engine.GetRegister(0));

        if (atomCell.Tag == Tag.Atom)
        {
            // Atom -> Term: parse the atom name. The parser expects a
            // clause-terminating dot; append one if the user didn't.
            string text = AtomTable.GetById(atomCell.AsAtomId)?.Name ?? "";
            string source = text.TrimEnd().EndsWith(".", StringComparison.Ordinal)
                ? text : text + ".";
            var parser = new Shumway.Compiler.Parsing.Parser(
                new Shumway.Compiler.Lexer.Lexer(source),
                Shumway.Compiler.Parsing.OperatorTable.Default());
            Term parsed = parser.ReadClauseTerm();
            Cell newCell = Materializer.MaterializeAsCell(engine, parsed);
            return engine.UnifyRegisterWithCell(1, newCell);
        }

        // Term -> Atom: render with the requested quoting style and
        // intern the result as a fresh atom.
        using var sw = new System.IO.StringWriter();
        Shumway.Builtins.TermRenderer.Render(engine, engine.GetRegister(1), sw,
            new Shumway.Builtins.TermRenderOptions
            {
                Operators = engine.Operators,
                Quoted = quoted,
            });
        string rendered = sw.ToString();
        int newAtomId = AtomTable.Intern(rendered, permanent: false).Id;
        return engine.UnifyRegisterWithCell(0, Cell.Atom(newAtomId));
    }

    public static bool StringSearch3(Engine engine)
    {
        Cell subCell = MaterializeRegisterAsCell(engine, 0);
        Cell haystackCell = MaterializeRegisterAsCell(engine, 1);
        if (subCell.Tag == Tag.Ref || subCell.Tag == Tag.AttVar
            || haystackCell.Tag == Tag.Ref || haystackCell.Tag == Tag.AttVar)
            throw new Shumway.Core.PrologRuntimeException("instantiation_error");
        if (subCell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException("type_error(atom, _)");
        if (haystackCell.Tag != Tag.Atom)
            throw new Shumway.Core.PrologRuntimeException("type_error(atom, _)");
        string sub = AtomTable.GetById(subCell.AsAtomId)?.Name ?? "";
        string hay = AtomTable.GetById(haystackCell.AsAtomId)?.Name ?? "";
        if (sub.Length == 0) return engine.UnifyRegisterWithCell(2, Cell.Int(0));

        // Walk the haystack collecting every match position so we can
        // backtrack through them via PushBuiltinChoicePoint.
        var positions = new List<int>();
        int start = 0;
        while (start <= hay.Length - sub.Length)
        {
            int idx = hay.IndexOf(sub, start, StringComparison.Ordinal);
            if (idx < 0) break;
            positions.Add(idx);
            start = idx + 1;
        }
        if (positions.Count == 0) return false;
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, positions.Count, 3, returnPc,  // arity 3 (string_search/3)
            (e, i) => engine.UnifyRegisterWithCell(2, Cell.Int(positions[i])));
    }

    // ============================================================================
    // Phase 24 chunk 271 — Arity-Prolog file-system operations.
    // Thin wrappers over System.IO. ISO error shapes for instantiation /
    // existence / permission failures so catch/3 can match them.
    // ============================================================================

    public static bool Mkdir1(Engine engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "mkdir/1");
        try { System.IO.Directory.CreateDirectory(path); }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "directory", new AtomTerm(path)));
        }
        catch (IOException ex)
        {
            throw new ShumwayPrologException(
                IsoError.SystemError(ex.Message));
        }
        return true;
    }

    public static bool Rmdir1(Engine engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "rmdir/1");
        if (!System.IO.Directory.Exists(path))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("directory", new AtomTerm(path)));
        try { System.IO.Directory.Delete(path, recursive: false); }
        catch (IOException)
        {
            // Non-empty directory or in use.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("delete", "directory", new AtomTerm(path)));
        }
        return true;
    }

    public static bool Delete1(Engine engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "delete/1");
        if (!System.IO.File.Exists(path))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("source_sink", new AtomTerm(path)));
        try { System.IO.File.Delete(path); }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("delete", "source_sink", new AtomTerm(path)));
        }
        catch (IOException ex)
        {
            throw new ShumwayPrologException(
                IsoError.SystemError(ex.Message));
        }
        return true;
    }

    public static bool Rename2(Engine engine)
    {
        string from = RequireAtomPath(engine, register: 0, builtin: "rename/2");
        string to = RequireAtomPath(engine, register: 1, builtin: "rename/2");
        if (!System.IO.File.Exists(from))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("source_sink", new AtomTerm(from)));
        if (System.IO.File.Exists(to))
            throw new ShumwayPrologException(
                IsoError.PermissionError("create", "source_sink", new AtomTerm(to)));
        try { System.IO.File.Move(from, to); }
        catch (UnauthorizedAccessException)
        {
            throw new ShumwayPrologException(
                IsoError.PermissionError("modify", "source_sink", new AtomTerm(from)));
        }
        catch (IOException ex)
        {
            throw new ShumwayPrologException(
                IsoError.SystemError(ex.Message));
        }
        return true;
    }

    public static bool ExistsFile1(Engine engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "exists_file/1");
        return System.IO.File.Exists(path);
    }

    public static bool ExistsDirectory1(Engine engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "exists_directory/1");
        return System.IO.Directory.Exists(path);
    }

    // ============================================================================
    // Phase 24 chunk 272 — pseudo-random generation.
    // ============================================================================

    public static bool Randomize1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "randomize/1");
        Cell c = MaterializeRegisterAsCell(engine, 0);
        if (c.Tag == Tag.Ref || c.Tag == Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (c.Tag != Tag.Int)
            throw new ShumwayPrologException(
                IsoError.TypeError("integer", new IntTerm(0)));
        host.Randomize((int)c.AsInt);
        return true;
    }

    public static bool Random1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "random/1");
        double v = host.Random.NextDouble();
        Cell c = Materializer.MaterializeAsCell(engine, new FloatTerm(v));
        return engine.UnifyRegisterWithCell(0, c);
    }

    public static bool RandomBetween3(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "random_between/3");
        Cell loCell = MaterializeRegisterAsCell(engine, 0);
        Cell hiCell = MaterializeRegisterAsCell(engine, 1);
        if (loCell.Tag == Tag.Ref || loCell.Tag == Tag.AttVar
            || hiCell.Tag == Tag.Ref || hiCell.Tag == Tag.AttVar)
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (loCell.Tag != Tag.Int || hiCell.Tag != Tag.Int)
            throw new ShumwayPrologException(
                IsoError.TypeError("integer", new IntTerm(0)));
        long lo = loCell.AsInt;
        long hi = hiCell.AsInt;
        if (lo > hi) return false;
        // System.Random.Next(int, int) is [min, max) — extend by one
        // to get SWI's [lo, hi] inclusive semantics. Long range guarded
        // against int overflow via NextInt64 when available.
        long v = lo + (long)(host.Random.NextDouble() * (hi - lo + 1));
        if (v > hi) v = hi;  // floating-point edge case
        return engine.UnifyRegisterWithCell(2, Cell.Int(v));
    }

    // ============================================================================
    // Phase 24 chunk 273 — expand_term/2 (DCG expansion exposed).
    // ============================================================================

    public static bool ExpandTerm2(Engine engine)
    {
        Term input = MaterializeRegister(engine, 0);
        Term result;
        if (input is CompoundTerm { Functor: "-->", Args.Length: 2 })
        {
            // Wrap as a DcgRule clause, run the same transform consult
            // uses, take the expanded clause's term back. The resulting
            // term is shaped as `:- (Head', Body')` for the user.
            var clause = new Shumway.Compiler.Ast.Clause(
                Shumway.Compiler.Ast.ClauseKind.DcgRule, input,
                new Shumway.Compiler.Lexer.SourcePosition(0, 0, 0));
            var transformed = Shumway.Compiler.Parsing.DcgTransform.Apply(new[] { clause });
            result = transformed[0].Term;
        }
        else
        {
            result = input;
        }
        Cell cell = Materializer.MaterializeAsCell(engine, result);
        return engine.UnifyRegisterWithCell(1, cell);
    }

    // ============================================================================
    // Phase 24 chunk 274 — file_list/1,2 (Arity-Prolog database dump).
    // ============================================================================

    public static bool FileList1(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "file_list/1");
        string path = RequireAtomPath(engine, register: 0, builtin: "file_list/1");
        var fids = host.ListablePredicates().Select(p => p.FunctorId).ToList();
        WritePredicatesToFile(host, path, fids);
        return true;
    }

    public static bool FileList2(Engine engine)
    {
        PrologEngine host = RequireHost(engine, "file_list/2");
        string path = RequireAtomPath(engine, register: 0, builtin: "file_list/2");
        Term spec = MaterializeRegister(engine, 1);
        var fids = ResolveFileListSpec(host, spec);
        WritePredicatesToFile(host, path, fids);
        return true;
    }

    private static List<int> ResolveFileListSpec(PrologEngine host, Term spec)
    {
        var requested = new List<(string Name, int Arity)>();
        // Accept Name/Arity directly, or a [..] list of them.
        if (spec is CompoundTerm { Functor: "/", Args.Length: 2 } single)
        {
            requested.Add(ParsePredicateIndicator(single));
        }
        else if (spec is CompoundTerm { Functor: ".", Args.Length: 2 } || spec is AtomTerm { Name: "[]" })
        {
            Term cursor = spec;
            while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
            {
                if (cons.Args[0] is not CompoundTerm { Functor: "/", Args.Length: 2 } pi)
                    throw new ShumwayPrologException(
                        IsoError.TypeError("predicate_indicator", cons.Args[0]));
                requested.Add(ParsePredicateIndicator(pi));
                cursor = cons.Args[1];
            }
            if (cursor is not AtomTerm { Name: "[]" })
                throw new ShumwayPrologException(
                    IsoError.TypeError("list", spec));
        }
        else
        {
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator_or_list", spec));
        }

        // Map (Name, Arity) → matching fids (across modules; a local pred
        // is stored as <module>$<name> so demangle when comparing).
        var fids = new List<int>();
        foreach (var (name, arity) in requested)
        {
            foreach (var (fid, _) in host.ListablePredicates())
            {
                var (atomId, fidArity) = FunctorTable.Lookup(fid);
                if (fidArity != arity) continue;
                string mangled = AtomTable.GetById(atomId)?.Name ?? "";
                if (mangled == name || PrologEngine.DemangleLocalName(mangled) == name)
                    fids.Add(fid);
            }
        }
        return fids;
    }

    private static (string Name, int Arity) ParsePredicateIndicator(CompoundTerm pi)
    {
        if (pi.Args[0] is not AtomTerm nameAtom)
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator", pi));
        if (pi.Args[1] is not IntTerm arityInt)
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator", pi));
        return (nameAtom.Name, (int)arityInt.Value);
    }

    private static void WritePredicatesToFile(PrologEngine host, string path, IList<int> fids)
    {
        using var sw = new System.IO.StreamWriter(path, append: false);
        // Emit `:- dynamic Name/Arity.` for any dynamic predicate in the
        // list so a re-consult preserves the declaration (under
        // implicit_dynamic=true the directive isn't strictly required,
        // but it documents intent and works regardless of the flag).
        var dynamicFids = new HashSet<int>();
        foreach (int fid in fids)
        {
            if (host.IsDynamic(fid)) dynamicFids.Add(fid);
        }
        foreach (int fid in dynamicFids)
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = PrologEngine.DemangleLocalName(
                AtomTable.GetById(atomId)?.Name ?? "");
            sw.WriteLine($":- dynamic {name}/{arity}.");
        }
        if (dynamicFids.Count > 0) sw.WriteLine();
        foreach (int fid in fids)
        {
            foreach (var clause in host.ClausesForListing(fid))
                ClausePortrayer.Print(sw, clause.Term);
        }
    }

    public static bool Directory6(Engine engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "directory/6");
        if (!System.IO.Directory.Exists(path))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("directory", new AtomTerm(path)));
        var entries = new System.IO.DirectoryInfo(path)
            .EnumerateFileSystemInfos()
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        if (entries.Count == 0) return false;
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, entries.Count, 6, returnPc,  // arity 6 (directory/6)
            (e, i) => Directory6Unify(e, entries, i));
    }

    private static bool Directory6Unify(
        Engine engine, List<System.IO.FileSystemInfo> entries, int index)
    {
        var info = entries[index];
        // Arity-style mode bits: ReadOnly=1, Hidden=2, System=4,
        // Directory=16, Archive=32 — .NET FileAttributes uses the
        // same numeric values so a masked cast works directly.
        const int ModeMask = 1 | 2 | 4 | 16 | 32;
        int mode = (int)info.Attributes & ModeMask;
        var t = info.LastWriteTime;
        long size = info is System.IO.FileInfo f ? f.Length : 0L;
        int nameAid = AtomTable.Intern(info.Name, permanent: false).Id;
        int timeAid = AtomTable.Intern(
            $"{t.Hour:D2}:{t.Minute:D2}:{t.Second:D2}", permanent: false).Id;
        int dateAid = AtomTable.Intern(
            $"{t.Year:D4}-{t.Month:D2}-{t.Day:D2}", permanent: false).Id;

        if (!engine.UnifyRegisterWithCell(1, Cell.Atom(nameAid))) return false;
        if (!engine.UnifyRegisterWithCell(2, Cell.Int(mode))) return false;
        if (!engine.UnifyRegisterWithCell(3, Cell.Atom(timeAid))) return false;
        if (!engine.UnifyRegisterWithCell(4, Cell.Atom(dateAid))) return false;
        if (!engine.UnifyRegisterWithCell(5, Cell.Int(size))) return false;
        return true;
    }
}

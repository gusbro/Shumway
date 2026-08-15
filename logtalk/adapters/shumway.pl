%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%
%  Logtalk backend adapter for Shumway (Prolog on .NET)
%  <https://logtalk.org/>
%
%  Written from scratch for Shumway against the Logtalk adapter interface
%  (the '$lgt_'* hook predicates every backend provides) and Shumway's own
%  builtin surface. SPDX-License-Identifier: MIT
%
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%


:- built_in.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  backend identity and capabilities
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

%  WHY SHUMWAY ANNOUNCES ITSELF AS `xsb`
%
%  Logtalk selects backend-specific code by `prolog_dialect`: its libraries
%  and tools branch on it in ~100 files, and a name Logtalk does not know
%  falls into the `:- else.` arm — which, depending on the file, prints
%  "backend Prolog compiler not supported!" (library/os), leaves random
%  without a backend, or makes lgtunit raise `resource_error(deterministic/2)`
%  and fail most tests of most suites. Announcing an unknown dialect is not a
%  neutral act of honesty; it disables the library collection.
%
%  Shumway is not (yet) one of the backends Logtalk ships support for — that
%  is Paulo Moura's call, not ours, and it is not something a user of Shumway
%  should have to patch into a Logtalk installation. So we announce the
%  supported dialect closest to our actual capabilities.
%
%  That is `xsb`, chosen by measurement. Testers gate on the dialect, and the
%  choice decides both which of them RUN and what they EXPECT:
%
%      gate                              dialects
%      coroutining                       eclipse xvm sicstus swi trealla xsb yap
%      dif                               b eclipse xvm sicstus swi trealla xsb yap
%      timeout                           b eclipse xvm sicstus swi trealla xsb yap
%      process (OS processes)            ciao eclipse gnu sicstus swi trealla xvm
%      redis (sockets)                   eclipse gnu sicstus swi trealla xvm
%      java (a JVM)                      swi yap
%      reader/csv expect LF, not CR-LF   b gnu ji sicstus swi xsb
%
%  This adapter began as a port of Logtalk's GNU Prolog adapter, and `gnu` was
%  the first choice — but it is a bad cell: it enables the two we CANNOT support
%  (OS processes, sockets) and blocks the three we CAN (dif/2, coroutining and
%  time limits are all native here), so real capability went uncredited.
%
%  `xvm` was tried next and measured WORSE: it credits the three, but it is
%  absent from the last row, so `reader` and `csv` start expecting CR-LF — the
%  opposite of what Shumway does (ADR-045 translates on text reads, as GNU and
%  SWI do). Full sweep: 36 failures against `gnu`'s 9.
%
%  `xsb` is the only dialect that satisfies every row: it credits the three,
%  keeps the LF expectation, and enables none of process/redis/java. Its
%  lgtunit `deterministic/1,2` arm needs `call_cleanup/2` rather than GNU's
%  `call_det/2`; that is one line over the engine's `setup_call_cleanup/3`
%  (below), not a missing capability — an earlier version of this adapter
%  wrongly declared that predicate unsupported.
%
%  The gap is the backend's OS predicate names, which the "Backend OS
%  compatibility layer" at the end of THIS FILE supplies — in the adapter we
%  ship, never in the Logtalk tree. It covers the `gnu` spellings too, so the
%  override below keeps working.
%
%  Override the announced dialect with the SHUMWAY_LOGTALK_DIALECT
%  environment variable, e.g. `shumway` once Logtalk knows that name:
%
%      set SHUMWAY_LOGTALK_DIALECT=shumway
%
%  Everything else — `prolog_version`, error messages, `current_prolog_flag`
%  — keeps reporting Shumway; only this one selector borrows another name.

'$lgt_prolog_feature'(prolog_dialect, Dialect) :-
	(	catch(getenv('SHUMWAY_LOGTALK_DIALECT', Name), _, fail),
		Name \== '' ->
		Dialect = Name
	;	Dialect = xsb
	).
'$lgt_prolog_feature'(prolog_version, v(Major, Minor, Patch)) :-
	current_prolog_flag(version_data, shumway(Major, Minor, Patch, _)).
'$lgt_prolog_feature'(prolog_compatible_version, @>=(v(1, 0, 0))).

% Capability set — each value is the MEASURED truth, not modesty and not
% optimism, because library code changes behavior on it (library(types) caps
% generable character codes by the unicode value; testers gate whole test
% groups on tabling/threads).
%
% - tabling: supported — the engine's `:- table` (variant tabling,
%   well-founded negation). Logtalk forwards the directive over the compiled
%   names; the table/1 meta-directive declaration below is the required half.
% - unicode: unsupported is deliberate, NOT modesty backwards. The engine is
%   BMP-clean (a code above 0xFFFF raises representation_error everywhere —
%   silently truncating used to build a DIFFERENT character), but Logtalk's
%   `unicode_full` charset generates astral codes whenever the flag is not
%   `unsupported`, and its own docs say only backends declaring `full` (SWI,
%   XVM) handle that. `bmp` was measured: arbitrary 43/43 → 40/43. Flip to
%   `full` only with real astral atoms (surrogate-pair build/decompose,
%   code-point atom_length) — that also un-skips a yaml test.
% - threads/engines/sockets: genuinely absent (single-threaded activations,
%   no socket layer).
% - modules: Logtalk-compiling Prolog MODULE FILES as objects is more than
%   ADR-038 provides (no current_module/1 reflection, M:G only shimmed).
% - coinduction: needs verified rational-tree unification end to end;
%   revisit deliberately, not as a flag flip.
'$lgt_prolog_feature'(encoding_directive, unsupported).
'$lgt_prolog_feature'(sockets, unsupported).
'$lgt_prolog_feature'(tabling, supported).
'$lgt_prolog_feature'(engines, unsupported).
'$lgt_prolog_feature'(threads, unsupported).
'$lgt_prolog_feature'(modules, unsupported).
'$lgt_prolog_feature'(coinduction, unsupported).
'$lgt_prolog_feature'(unicode, unsupported).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  default compiler flag values
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% lint flags
'$lgt_default_flag'(linter, default).
'$lgt_default_flag'(general, warning).
'$lgt_default_flag'(encodings, warning).
'$lgt_default_flag'(unknown_entities, warning).
'$lgt_default_flag'(unknown_predicates, warning).
'$lgt_default_flag'(undefined_predicates, warning).
'$lgt_default_flag'(singleton_variables, warning).
'$lgt_default_flag'(left_recursion, warning).
'$lgt_default_flag'(disjunctions, warning).
'$lgt_default_flag'(conditionals, warning).
'$lgt_default_flag'(redefined_operators, warning).
'$lgt_default_flag'(deprecated, warning).
'$lgt_default_flag'(missing_directives, warning).
'$lgt_default_flag'(duplicated_directives, warning).
'$lgt_default_flag'(trivial_goal_fails, warning).
'$lgt_default_flag'(always_true_or_false_goals, warning).
'$lgt_default_flag'(lambda_variables, warning).
'$lgt_default_flag'(grammar_rules, warning).
'$lgt_default_flag'(arithmetic_expressions, warning).
'$lgt_default_flag'(suspicious_calls, warning).
'$lgt_default_flag'(steadfastness, silent).
'$lgt_default_flag'(naming, silent).
'$lgt_default_flag'(duplicated_clauses, silent).
'$lgt_default_flag'(tail_recursive, silent).
'$lgt_default_flag'(catchall_catch, silent).
'$lgt_default_flag'(portability, silent).
'$lgt_default_flag'(redefined_built_ins, silent).
'$lgt_default_flag'(underscore_variables, dont_care).

% optional feature flags
'$lgt_default_flag'(complements, deny).
'$lgt_default_flag'(dynamic_declarations, deny).
'$lgt_default_flag'(events, deny).
'$lgt_default_flag'(context_switching_calls, allow).
'$lgt_default_flag'(settings_file, allow).

% compilation flags
'$lgt_default_flag'(scratch_directory, './lgt_tmp/').
'$lgt_default_flag'(report, on).
'$lgt_default_flag'(clean, on).
'$lgt_default_flag'(code_prefix, '$').
'$lgt_default_flag'(optimize, off).
'$lgt_default_flag'(source_data, on).
'$lgt_default_flag'(reload, changed).
'$lgt_default_flag'(debug, off).
'$lgt_default_flag'(prolog_compiler, []).
'$lgt_default_flag'(prolog_loader, []).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  file extensions recognized per kind of source
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_file_extension'(logtalk, '.lgt').
'$lgt_file_extension'(logtalk, '.logtalk').
'$lgt_file_extension'(object, '.pl').
'$lgt_file_extension'(prolog, '.pl').
'$lgt_file_extension'(prolog, '.prolog').
'$lgt_file_extension'(prolog, '.pro').


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  compiler hook queries: how the backend classifies predicates
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% no backend-specific predicates need ISO-official treatment
'$lgt_iso_predicate'(_) :-
	fail.

'$lgt_predicate_property'(Predicate, Property) :-
	predicate_property(Predicate, Property).

% predicate properties Shumway can actually report
'$lgt_prolog_predicate_property'(built_in).
'$lgt_prolog_predicate_property'(dynamic).
'$lgt_prolog_predicate_property'(static).

% consult/1 takes a goal-position argument the compiler must not touch
'$lgt_prolog_meta_predicate'(consult(_), consult(*), predicate) :- !.
% The coroutining predicates carry a goal that runs LATER, when the variable
% is bound. Without these Logtalk passes that goal through uncompiled, so it
% wakes up in `user` and cannot see the object predicates it was written
% against — an existence_error at wake-up, far from the freeze/2 that caused it.
'$lgt_prolog_meta_predicate'(freeze(_, _), freeze(*, 0), predicate) :- !.
'$lgt_prolog_meta_predicate'(when(_, _), when(*, 0), predicate) :- !.
% NB: the MODULE-QUALIFIED forwarding calls inside the library objects
% (user:freeze, when:when, time:call_with_time_limit) must NOT be declared
% meta here: their goal argument is already the caller's compiled closure,
% and a meta declaration makes Logtalk wrap it AGAIN in the forwarding
% object's own context — measured as freeze tests regressing.
% Same reason for the time-limited calls: the goal must be compiled in the
% caller's context, or it wakes up in `user` unable to see the predicates it
% was written against.
'$lgt_prolog_meta_predicate'(call_with_time_limit(_, _), call_with_time_limit(*, 0), predicate) :- !.
'$lgt_prolog_meta_predicate'(time_out(_, _, _), time_out(0, *, *), predicate) :- !.
% Declaring it here is also what tells Logtalk timed_call/2 is a BACKEND
% predicate: without it the compiler takes the library's own meta_predicate
% declaration to mean an object-local one and looks for `$timeout#0.timed_call#2`.
'$lgt_prolog_meta_predicate'(timed_call(_, _), timed_call(0, *), predicate) :- !.
'$lgt_prolog_meta_predicate'(call_with_timeout(_, _), call_with_timeout(0, *), predicate) :- !.
'$lgt_prolog_meta_predicate'(call_with_timeout(_, _, _), call_with_timeout(0, *, *), predicate) :- !.
'$lgt_prolog_meta_predicate'(_, _, _) :- fail.

% Shumway directives whose argument is a predicate indicator
'$lgt_prolog_meta_directive'(table(_), table(/)).
'$lgt_prolog_meta_directive'(built_in(_), built_in(/)).
'$lgt_prolog_meta_directive'(ensure_linked(_), ensure_linked(/)).

'$lgt_prolog_to_logtalk_meta_argument_specifier_hook'(_, _) :- fail.

'$lgt_prolog_phrase_predicate'(_) :- fail.

% goals the linter may flag as trivially true/false
'$lgt_candidate_tautology_or_falsehood_goal_hook'(is_list(_)).
'$lgt_candidate_tautology_or_falsehood_goal_hook'(succ(_, _)).

'$lgt_prolog_database_predicate'(listing(_)).

'$lgt_prolog_deprecated_built_in_predicate_hook'(_, _) :- fail.
'$lgt_prolog_deprecated_built_in_predicate_hook'(_) :- fail.

% `:- use_module/1,2` directives in dialect-conditional code. Logtalk only
% accepts a Prolog directive the adapter declares, so without these clauses a
% tester opening with `:- use_module(library(dif), [])` dies with
% `Domain error: use_module/2 is not in domain directive` — even though the
% ENGINE supports the directive fine (ADR-038). Three cases:
%
% - constraintLib (XSB's constraint library): our equivalent is
%   library(coroutining), loaded by this adapter above — dropped, nothing to do.
% - a library this engine resolves on its search path: loaded NOW (the
%   compiler may need its ops/predicates) and re-emitted verbatim so the
%   generated file records the dependency.
% - a library we don't have (SWI's filesex, apply, ...): the load fails
%   quietly and the directive is dropped — if a predicate from it is really
%   needed, its call site raises a clear existence_error later, which beats
%   aborting the whole compilation here.
'$lgt_prolog_term_expansion'((:- use_module(constraintLib, _)), []) :- !.
'$lgt_prolog_term_expansion'((:- use_module(Library)), Expansion) :- !,
	'$shumway_expand_use_module'(Library, Expansion).
'$lgt_prolog_term_expansion'((:- use_module(Library, Imports)), Expansion) :- !,
	(	'$shumway_provided_library'(Library) ->
		Expansion = []
	;	catch(use_module(Library), _, fail) ->
		Expansion = {:- use_module(Library)}
	;	nonvar(Imports), Imports = [_| _] ->
		% A module we cannot resolve, imported for SPECIFIC predicates
		% (json_path/types import unicode_property/2 from SWI's `unicode`):
		% route those names to `user`, where this adapter or the engine may
		% supply them. On this module-less backend that is what the import
		% MEANS; a predicate genuinely absent still raises a clear
		% existence_error at its call site, instead of the confusing
		% object-local '$obj#0.name#arity' one that dropping the directive
		% produced.
		Expansion = (:- uses(user, Imports))
	;	Expansion = []
	).
% no other backend-specific term/goal expansion, encodings, or string type
'$lgt_prolog_term_expansion'(_, _) :- fail.

'$shumway_expand_use_module'(Library, []) :-
	'$shumway_provided_library'(Library),
	!.
'$shumway_expand_use_module'(Library, Expansion) :-
	(	catch(use_module(Library), _, fail) ->
		Expansion = {:- use_module(Library)}
	;	Expansion = []
	).

% Libraries whose predicates this adapter already supplies. These must be
% DROPPED rather than resolved: the engine's search path can carry another
% dialect's copy (the Scryer collection ships dif.pl), and loading it on top
% of ours collides on the shared public names.
'$shumway_provided_library'(library(dif)).      % library(coroutining), above
'$shumway_provided_library'(library(when)).
'$shumway_provided_library'(library(freeze)).
'$shumway_provided_library'(library(atts)).
'$shumway_provided_library'(library(time)).     % call_with_time_limit/2, below
'$lgt_prolog_goal_expansion'(_, _) :- fail.
'$lgt_logtalk_prolog_encoding'(_, _, _) :- fail.
'$lgt_string'(_) :- fail.
'$lgt_string_codes'(_, _) :- fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  term services used by the compiler and libraries
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% lambda support: Shumway's plain copy already ignores no constraints here
'$lgt_copy_term_without_constraints'(Term, Copy) :-
	copy_term(Term, Copy).

'$lgt_normalize_error_term'(Error, Error).

% no module system exposed to Logtalk: goals pass through unqualified
'$lgt_user_module_qualification'(Goal, Goal).
'$lgt_find_visible_module_predicate'(_, _, _) :- fail.
'$lgt_current_module_predicate'(_, _) :- fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  operating-system layer
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% Shumway paths need no OS-specific spelling changes
'$lgt_prolog_os_file_name'(Path, Path).

% Path expansion = $LOGTALKHOME/$LOGTALKUSER substitution, then absolute,
% then forward slashes throughout. The core assembles paths POSIX-style;
% .NET hands back backslashed Windows paths, and a mixed 'dir\/file' join
% breaks exists_file — so everything the adapter returns is '/'-separated.
'$lgt_expand_path'(Path, ExpandedPath) :-
	'$shumway_env_prefix'(Path, Substituted),
	(	catch(absolute_file_name(Substituted, Absolute), _, fail) ->
		true
	;	Absolute = Substituted
	),
	'$shumway_forward_slashes'(Absolute, ExpandedPath).

'$shumway_forward_slashes'(Path, Slashed) :-
	(	sub_atom(Path, _, _, _, '\\') ->
		atomic_list_concat(Segments, '\\', Path),
		atomic_list_concat(Segments, '/', Slashed)
	;	Slashed = Path
	).

'$shumway_env_prefix'(Path, Substituted) :-
	(	sub_atom(Path, 0, _, _, '$LOGTALKHOME/'),
		getenv('LOGTALKHOME', Value) ->
		sub_atom(Path, 13, _, 0, Tail),
		atomic_list_concat([Value, '/', Tail], Substituted)
	;	sub_atom(Path, 0, _, _, '$LOGTALKUSER/'),
		getenv('LOGTALKUSER', Value) ->
		sub_atom(Path, 13, _, 0, Tail),
		atomic_list_concat([Value, '/', Tail], Substituted)
	;	Substituted = Path
	).

'$lgt_file_exists'(File) :-
	exists_file(File).

'$lgt_delete_file'(File) :-
	catch(delete(File), _, true).

'$lgt_directory_exists'(Directory) :-
	exists_directory(Directory).

'$lgt_current_directory'(Directory) :-
	working_directory(Current, Current),
	'$shumway_forward_slashes'(Current, Directory).

'$lgt_change_directory'(Directory) :-
	working_directory(_, Directory).

'$lgt_make_directory'(Directory) :-
	(	exists_directory(Directory) ->
		true
	;	catch(mkdir(Directory), _, true)
	).

% Scratch-file naming: a per-directory hash suffixed with the dialect, and a
% second one suffixed with a pid stand-in (Shumway sessions do not embed the
% pid in scratch names; a constant keeps names stable across runs).
'$lgt_directory_hashes'(Directory, HashDialect, HashPid) :-
	term_hash(Directory, Hash),
	atomic_list_concat(['_', Hash, '_', shumway], HashDialect),
	atomic_list_concat(['_', Hash, '_', 0], HashPid).

% Compiling a Prolog file ahead of loading is a no-op: Shumway's consult
% compiles internally, so loading IS the compilation step.
'$lgt_compile_prolog_code'(_, _, _).

'$lgt_load_prolog_code'(File, _, _) :-
	consult(File).

'$lgt_load_prolog_file'(File) :-
	consult(File).

% No modification-time tracking: a constant time means reload(changed)
% decisions fall back to the core's own bookkeeping.
'$lgt_file_modification_time'(_File, 0).

'$lgt_environment_variable'(Variable, Value) :-
	catch(getenv(Variable, Value), _, fail).

% Split a path into directory / base name / extension using sub_atom
% arithmetic. The last '/' or '\' bounds the directory (empty -> './'); the
% last '.' of the remainder starts the extension (none -> '').
'$lgt_decompose_file_name'(File, Directory, Name, Extension) :-
	'$shumway_forward_slashes'(File, Path),
	(	'$shumway_last_occurrence'(Path, '/', SlashEnd) ->
		sub_atom(Path, 0, SlashEnd, _, Directory),
		sub_atom(Path, SlashEnd, _, 0, Base)
	;	Directory = './',
		Base = Path
	),
	(	'$shumway_last_occurrence'(Base, '.', DotStart0),
		DotStart0 > 1 ->
		DotStart is DotStart0 - 1,
		sub_atom(Base, 0, DotStart, _, Name),
		sub_atom(Base, DotStart, _, 0, Extension)
	;	Name = Base,
		Extension = ''
	).

% End position (1-based, past the character) of the LAST occurrence of a
% single-character atom; fails when absent. Driven by sub_atom backtracking.
'$shumway_last_occurrence'(Atom, Char, End) :-
	findall(After, sub_atom(Atom, _, 1, After, Char), Afters),
	Afters = [_| _],
	'$shumway_min_of'(Afters, MinAfter),
	atom_length(Atom, Length),
	End is Length - MinAfter.

'$shumway_min_of'([X| Xs], Min) :-
	'$shumway_min_of'(Xs, X, Min).

'$shumway_min_of'([], Min, Min).
'$shumway_min_of'([X| Xs], Acc, Min) :-
	(	X < Acc ->
		'$shumway_min_of'(Xs, X, Min)
	;	'$shumway_min_of'(Xs, Acc, Min)
	).

'$lgt_directory_files'(_Directory, []).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  stream layer
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_open'(File, Mode, Stream, Options) :-
	open(File, Mode, Stream, Options).

'$lgt_close'(Stream) :-
	close(Stream).

% no per-stream line counting available
'$lgt_stream_current_line_number'(_Stream, 0).

% no term position tracking either: every term reads as spanning 0-0
'$lgt_read_term'(Stream, Term, Options, 0-0) :-
	read_term(Stream, Term, Options).

'$lgt_write_compiled_term'(Stream, Term, _Kind, _Path, _Line) :-
	write_canonical(Stream, Term),
	write(Stream, '.\n').

'$lgt_assertz_entity_clause'(Clause, _Kind) :-
	assertz(Clause).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  auxiliary predicates the Logtalk libraries expect from the backend
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% Term hashing (scratch-name uniqueness, library term_hash users). Any
% stable hash works; fold the canonical spelling with the classic
% multiply-by-33 accumulator, capped to 30 bits.
term_hash(Term, Hash) :-
	'$shumway_hash_term'(Term, Hash).
term_hash(Term, _Depth, _Range, Hash) :-
	'$shumway_hash_term'(Term, Hash).

'$shumway_hash_term'(Term, Hash) :-
	(	atom(Term) ->
		Spelling = Term
	;	term_to_atom(Term, Spelling)
	),
	atom_codes(Spelling, Codes),
	'$shumway_hash_fold'(Codes, 5381, Hash).

'$shumway_hash_fold'([], Hash, Hash).
'$shumway_hash_fold'([Code| Codes], Acc0, Hash) :-
	Acc is (Acc0 * 33 + Code) /\ 0x3fffffff,
	'$shumway_hash_fold'(Codes, Acc, Hash).

% setup_call_cleanup/3 is native (prelude). lgtunit's deterministic/1,2 for
% this dialect family is written over call_cleanup/2, which is the same thing
% without a setup.
call_cleanup(Goal, Cleanup) :-
	setup_call_cleanup(true, Goal, Cleanup).

'$lgt_format'(Stream, Format, Arguments) :-
	format(Stream, Format, Arguments).
'$lgt_format'(Format, Arguments) :-
	format(Format, Arguments).

% atomic_concat(+atomic, +atomic, ?atom): strict-typed concatenation some
% library objects import from `user`.
atomic_concat(A, B, Atom) :-
	(	var(A) ->
		throw(error(instantiation_error, atomic_concat/3))
	;	var(B) ->
		throw(error(instantiation_error, atomic_concat/3))
	;	\+ atomic(A) ->
		throw(error(type_error(atomic, A), atomic_concat/3))
	;	\+ atomic(B) ->
		throw(error(type_error(atomic, B), atomic_concat/3))
	;	'$shumway_as_atom'(A, AtomA),
		'$shumway_as_atom'(B, AtomB),
		atom_concat(AtomA, AtomB, Atom)
	).

'$shumway_as_atom'(Atomic, Atom) :-
	(	atom(Atomic) ->
		Atom = Atomic
	;	term_to_atom(Atomic, Atom)
	).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  {File, ...} top-level shorthand for logtalk_load/1
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

{Files} :-
	'$lgt_conjunction_to_list'(Files, List),
	logtalk_load(List).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  Backend OS compatibility layer
%
%  Logtalk's library(os) calls the announced backend's OS predicates, and
%  Shumway's own OS family carries different names — it grew from the
%  Arity/SWI side. The spellings both the `xvm` arm (what we announce) and
%  the `gnu` arm (reachable via SHUMWAY_LOGTALK_DIALECT) expect are defined
%  here, in the adapter WE ship, rather than by patching Logtalk's tree or by
%  adding foreign aliases to the engine's global builtin surface.
%
%  Both arms are kept so the override stays usable. Nothing here is engine
%  surface: these names exist only while this adapter is loaded.
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% The dialects that carry coroutining expect `dif/2`, `freeze/2`, `when/2`
% and friends to answer as plain predicates. In Shumway they are an opt-in
% library, so the adapter opts in on the program's behalf — announcing the
% capability and then not having it is worse than not announcing it.
:- use_module(library(coroutining)).

% SWI-arm code reaches plain-Prolog land with MODULE-QUALIFIED goals
% (library(timeout) calls time:call_with_time_limit/2). Shumway has no M:Goal
% (ADR-038 defers it), so the module is dropped and the goal called — correct
% for these uses, where the predicate itself is supplied by this adapter.
':'(_Module, Goal) :-
	call(Goal).

% Registered so predicate_property/2 reports meta_predicate(T) for them —
% which is where Logtalk's compiler looks when it compiles the qualified
% forwarding calls (time:call_with_time_limit) in library(timeout).
:- meta_predicate(call_with_time_limit(*, 0)).
:- meta_predicate(timed_call(0, *)).

% SWI's call_with_time_limit/2: Time in SECONDS, throws the bare atom
% time_limit_exceeded, and — per SWI's documentation — runs Goal AS ONCE/1,
% hence the cut. Over time_out/3 like the other wrappers.
call_with_time_limit(Time, Goal) :-
	MilliSeconds is truncate(Time * 1000),
	time_out(Goal, MilliSeconds, Result),
	!,
	(	Result == time_out ->
		throw(time_limit_exceeded)
	;	true
	).

% library(timeout)'s xsb arm calls XSB's timed_call/2: a goal plus an option
% list, of which only max(MilliSeconds, OnTimeout) is used — run the goal, and
% call OnTimeout instead of giving up silently if the budget runs out. Over
% our time_out/3, so it inherits the same clock-restarts-on-backtracking rule.
timed_call(Goal, Options) :-
	'$shumway_timed_call_max'(Options, MilliSeconds, OnTimeout),
	time_out(Goal, MilliSeconds, Result),
	(	Result == time_out ->
		call(OnTimeout)
	;	true
	).

'$shumway_timed_call_max'([max(MilliSeconds, OnTimeout)| _], MilliSeconds, OnTimeout) :- !.
'$shumway_timed_call_max'([_| Options], MilliSeconds, OnTimeout) :-
	'$shumway_timed_call_max'(Options, MilliSeconds, OnTimeout).

% library(timeout)'s xvm arm calls these in `user`. Shumway's own primitive is
% time_out/3 (SICStus semantics: milliseconds, nondet, clock restarts on
% backtracking); call_with_timeout/2-3 are Logtalk's SECONDS-based, committed
% shape, so they belong here rather than in the engine's predicate surface.
% Deliberately the same construction Logtalk's own sicstus arm uses — the cut
% is what makes call_with_timeout/2 deterministic over a non-deterministic Goal.
call_with_timeout(Goal, Time) :-
	MilliSeconds is truncate(Time * 1000),
	time_out(Goal, MilliSeconds, Result),
	!,
	(	Result == time_out ->
		throw(timeout(Goal))
	;	true
	).

call_with_timeout(Goal, Time, Result) :-
	MilliSeconds is truncate(Time * 1000),
	(	catch(time_out(Goal, MilliSeconds, Result0), Error, true) ->
		(	Result0 == time_out ->
			Result = timeout
		;	var(Error) ->
			Result = true
		;	Result = error(Error)
		)
	;	Result = fail
	).

% --- the swi arm ---
%
% library(os)'s SWI arm is the closest to Shumway's native surface (the OS
% family here grew from the Arity/SWI side): atomic_list_concat, copy_file,
% directory_files, exists_file/exists_directory, get_time, getenv,
% prolog_to_os_filename, shell, sleep, stamp_date_time and the SWI shapes of
% statistics/2 (cputime seconds, walltime [Ms,_]) are all native. Only the
% four below need spelling out.

% SWI's options form. relative_to(Base) is HONORED — a relative path joins
% Base before resolving, never the process CWD (an earlier shim ignored it,
% and a recursive delete over the mis-resolved path ate a test tree — path
% resolution feeding deletes has no room for "close enough"). expand(true),
% file_errors(fail) and file_type(_) change nothing here: Shumway expands
% nothing and appends no extensions, and /2 fails rather than throws.
absolute_file_name(Path, Options, ExpandedPath) :-
	(	member(relative_to(Base), Options),
		\+ '$shumway_absolute_path'(Path) ->
		atom_concat(Base, '/', Slashed),
		atom_concat(Slashed, Path, Joined),
		absolute_file_name(Joined, ExpandedPath)
	;	absolute_file_name(Path, ExpandedPath)
	).

% Absolute under either convention: rooted (`/...`) or drive-qualified
% (`C:...`). A doubled slash from joining is harmless downstream.
'$shumway_absolute_path'(Path) :-
	(	sub_atom(Path, 0, 1, _, '/') ->
		true
	;	sub_atom(Path, 1, 1, _, ':')
	).

size_file(File, Size) :-
	file_size(File, Size).

time_file(File, Time) :-
	file_modification_time(File, Time).

% Only the modes library(os) asks for; `exist` is true for a directory too,
% the others are about a regular file. An UNBOUND mode must raise (SWI does;
% the first clause would otherwise quietly unify it with `exist` and turn a
% caller's error into a plain failure), and an unknown one is a domain error.
access_file(_, Permission) :-
	var(Permission),
	!,
	throw(error(instantiation_error, access_file/2)).
access_file(Path, exist) :-
	!,
	(	exists_file(Path) ->
		true
	;	exists_directory(Path)
	).
access_file(Path, read) :- !, file_permission(Path, read).
access_file(Path, write) :- !, file_permission(Path, write).
access_file(Path, append) :- !, file_permission(Path, write).
access_file(Path, execute) :- !, file_permission(Path, execute).
access_file(_, Permission) :-
	throw(error(domain_error(access, Permission), access_file/2)).

% --- the xsb arm ---
%
% library(os)'s XSB arm rests almost entirely on one predicate family: XSB's
% path_sysop/2-3, an operation atom plus its arguments. Every operation maps
% onto something Shumway already has under a different name.

path_sysop(exists, Path) :-
	!,
	(	exists_file(Path) ->
		true
	;	exists_directory(Path)
	).
path_sysop(isdir, Path) :- !, exists_directory(Path).
path_sysop(isplain, Path) :- !, exists_file(Path).
path_sysop(readable, Path) :- !, file_permission(Path, read).
path_sysop(writable, Path) :- !, file_permission(Path, write).
path_sysop(executable, Path) :- !, file_permission(Path, execute).
path_sysop(mkdir, Path) :- !, mkdir(Path).
path_sysop(rmdir, Path) :- !, rmdir(Path).
path_sysop(rm, Path) :- !, delete(Path).
path_sysop(chdir, Path) :- !, working_directory(_, Path).
% cwd READS the current directory (Shumway's /2 is read-and-set, so passing
% the same variable twice leaves it unchanged).
path_sysop(cwd, Directory) :- !, working_directory(Directory, Directory).

% The rest of what the arm imports, in XSB's spellings.

sys_pid(PID) :-
	pid(PID).

% Backtrackable entry enumeration — the arm wraps it in findall/3.
list_directory(Path, File) :-
	directory_files(Path, Files),
	member(File, Files).

% Environment-variable expansion inside a path. Shumway resolves paths without
% a separate expansion step, so this is the identity — kept because the arm
% calls it before every absolute_file_name.
expand_atom(Atom, Atom).

% XSB reports both in SECONDS; Shumway's get_cpu_time/1 is milliseconds.
cputime(Seconds) :-
	get_cpu_time(Milliseconds),
	Seconds is Milliseconds / 1000.

walltime(Seconds) :-
	get_time(Seconds).

% Current time split into whole seconds and the leftover milliseconds.
epoch_milliseconds(Seconds, Milliseconds) :-
	get_time(Now),
	Seconds is truncate(Now),
	Milliseconds is truncate((Now - Seconds) * 1000).

get_localdate(Year, Month, Day, Hours, Minutes, Seconds) :-
	get_time(Stamp),
	stamp_date_time(Stamp, date(Year, Month, Day, Hours, Minutes, SecondsFloat, _, _, _), local),
	Seconds is integer(SecondsFloat).

sleep_ms(Milliseconds) :-
	Seconds is Milliseconds / 1000,
	sleep(Seconds).

% Only the key library(os) asks for. COMSPEC is the same probe Logtalk's own
% arms use to tell Windows from the rest.
xsb_configuration(os_type, Type) :-
	(	getenv('COMSPEC', _) ->
		Type = windows
	;	Type = unix
	).

path_sysop(rename, Old, New) :- !, rename(Old, New).
path_sysop(copy, From, To) :- !, copy_file(From, To).
path_sysop(size, Path, Size) :- !, file_size(Path, Size).
path_sysop(modtime, Path, Time) :- !, file_modification_time(Path, Time).
path_sysop(expand, Path, Expanded) :- !, absolute_file_name(Path, Expanded).

% --- the xvm arm ---

% Reads the current directory (Shumway's /2 is the SWI-style read-and-set,
% so passing the same value twice leaves it unchanged).
current_directory(Directory) :-
	working_directory(Directory, Directory).

% Creates a directory INCLUDING any missing parents, which is what Shumway's
% mkdir/1 already does.
make_directory_path(Directory) :-
	mkdir(Directory).

directory_exists(Directory) :-
	exists_directory(Directory).

% Positional, where gnu's date_time/1 wraps the same fields in dt/6.
date_time(Year, Month, Day, Hours, Minutes, Seconds) :-
	get_time(Stamp),
	stamp_date_time(Stamp, date(Year, Month, Day, Hours, Minutes, SecondsFloat, _, _, _), local),
	Seconds is integer(SecondsFloat).

wall_time(Seconds) :-
	get_time(Seconds).

% An opaque monotonic-enough stamp; library(os) only ever compares these.
time_stamp(Stamp) :-
	get_time(Stamp).

% --- shared by both arms, with the units each one expects ---

% One name, two contracts: xvm reads seconds, gnu reads milliseconds. Follow
% the ANNOUNCED dialect rather than silently hand one of them the other's
% numbers — a wrong-by-1000 duration is the kind of bug that survives a test
% suite.
cpu_time(Time) :-
	get_cpu_time(Milliseconds),
	(	'$lgt_prolog_feature'(prolog_dialect, gnu) ->
		Time = Milliseconds
	;	Time is Milliseconds / 1000
	).

% --- the gnu arm ---

prolog_pid(PID) :-
	pid(PID).

% GNU's working_directory/1 reads the current directory; Shumway's /2 is the
% SWI-style read-and-set, so pass the same value twice to leave it unchanged.
working_directory(Directory) :-
	working_directory(Directory, Directory).

change_directory(Directory) :-
	working_directory(_, Directory).

make_directory(Directory) :-
	mkdir(Directory).

delete_directory(Directory) :-
	rmdir(Directory).

delete_file(File) :-
	delete(File).

rename_file(Old, New) :-
	rename(Old, New).

% Another name with two contracts: GNU's file_exists/1 is true for ANY
% directory entry, xvm's only for a regular file. library(os) leans on the
% difference — it asks file_exists/1 before deleting, so the GNU reading would
% have it try to delete a directory as a file.
file_exists(Path) :-
	(	'$lgt_prolog_feature'(prolog_dialect, gnu) ->
		(	exists_file(Path) ->
			true
		;	exists_directory(Path)
		)
	;	exists_file(Path)
	).

% Only the four properties library(os) asks for.
file_property(Path, type(Type)) :-
	(	exists_directory(Path) ->
		Type = directory
	;	exists_file(Path),
		Type = regular
	).
file_property(Path, size(Size)) :-
	file_size(Path, Size).
file_property(Path, last_modification(Time)) :-
	file_modification_time(Path, Time).
file_property(Path, absolute_file_name(Absolute)) :-
	absolute_file_name(Path, Absolute).

environ(Variable, Value) :-
	getenv(Variable, Value).

% GNU reports real_time in MILLISECONDS too (cpu_time is shared, above).
real_time(Milliseconds) :-
	get_time(Seconds),
	Milliseconds is integer(Seconds * 1000).

% GNU's date_time/1 yields dt(Year, Month, Day, Hours, Minutes, Seconds).
date_time(dt(Year, Month, Day, Hours, Minutes, Seconds)) :-
	get_time(Stamp),
	stamp_date_time(Stamp, date(Year, Month, Day, Hours, Minutes, SecondsFloat, _, _, _), local),
	Seconds is integer(SecondsFloat).

argument_list(Arguments) :-
	current_prolog_flag(argv, Arguments).

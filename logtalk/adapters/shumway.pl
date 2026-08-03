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

'$lgt_prolog_feature'(prolog_dialect, shumway).
'$lgt_prolog_feature'(prolog_version, v(Major, Minor, Patch)) :-
	current_prolog_flag(version_data, shumway(Major, Minor, Patch, _)).
'$lgt_prolog_feature'(prolog_compatible_version, @>=(v(0, 1, 0))).

% Conservative capability set. Shumway does implement tabling, dif/2 and
% friends natively, but announcing a capability here also commits the adapter
% to the corresponding hook wiring — flip one only together with that work.
'$lgt_prolog_feature'(encoding_directive, unsupported).
'$lgt_prolog_feature'(sockets, unsupported).
'$lgt_prolog_feature'(tabling, unsupported).
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
'$lgt_prolog_meta_predicate'(_, _, _) :- fail.

% Shumway directives whose argument is a predicate indicator
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

% no backend-specific term/goal expansion, encodings, or string type
'$lgt_prolog_term_expansion'(_, _) :- fail.
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

% Not provided by this backend; a loud error beats a silent wrong answer.
setup_call_cleanup(_, _, _) :-
	throw(not_supported(setup_call_cleanup/3)).

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

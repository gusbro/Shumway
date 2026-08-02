%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%
%  Adapter file for Shumway (.NET Prolog)  -- experimental bring-up
%  Based on the GNU Prolog adapter (gnu.pl).
%
%  This file is part of Logtalk <https://logtalk.org/>
%  SPDX-License-Identifier: Apache-2.0
%
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%


:- built_in.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  missing builtins that Shumway does not (yet) provide natively
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% predicate_property/2 is now a native Shumway builtin (built_in / dynamic /
% static / defined) — no shim needed here.

% term_hash/2 and /4 -- Shumway has no term hashing; derive a cheap stable
% hash from the write-canonical atom's codes so scratch-file names are unique.

term_hash(Term, Hash) :-
	'$lgt_shumway_term_hash'(Term, Hash).
term_hash(Term, _Depth, _Range, Hash) :-
	'$lgt_shumway_term_hash'(Term, Hash).

'$lgt_shumway_term_hash'(Term, Hash) :-
	( atom(Term) -> Atom = Term ; term_to_atom(Term, Atom) ),
	atom_codes(Atom, Codes),
	'$lgt_shumway_hash_codes'(Codes, 5381, Hash).

'$lgt_shumway_hash_codes'([], Acc, Acc).
'$lgt_shumway_hash_codes'([C| Cs], Acc, Hash) :-
	Acc1 is (Acc * 33 + C) /\ 0x3fffffff,
	'$lgt_shumway_hash_codes'(Cs, Acc1, Hash).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  ISO predicates that must be defined because they are not built-in
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

% '$lgt_iso_predicate'(?callable).
'$lgt_iso_predicate'(_) :-
	fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  de facto standard predicates that might be missing
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_format'(Stream, Format, Arguments) :-
	format(Stream, Format, Arguments).
'$lgt_format'(Format, Arguments) :-
	format(Format, Arguments).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  predicate properties
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_predicate_property'(Pred, Prop) :-
	predicate_property(Pred, Prop).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  meta-predicates
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

setup_call_cleanup(_, _, _) :-
	throw(not_supported(setup_call_cleanup/3)).

'$lgt_prolog_meta_predicate'(consult(_), consult(*), predicate) :- !.
'$lgt_prolog_meta_predicate'(_, _, _) :- fail.

'$lgt_prolog_meta_directive'(built_in(_), built_in(/)).
'$lgt_prolog_meta_directive'(ensure_linked(_), ensure_linked(/)).

'$lgt_prolog_to_logtalk_meta_argument_specifier_hook'(_, _) :- fail.

'$lgt_prolog_phrase_predicate'(_) :- fail.

'$lgt_candidate_tautology_or_falsehood_goal_hook'(is_list(_)).
'$lgt_candidate_tautology_or_falsehood_goal_hook'(succ(_, _)).

'$lgt_prolog_database_predicate'(listing(_)).

'$lgt_prolog_predicate_property'(built_in).
'$lgt_prolog_predicate_property'(dynamic).
'$lgt_prolog_predicate_property'(static).

'$lgt_prolog_deprecated_built_in_predicate_hook'(_, _) :- fail.
'$lgt_prolog_deprecated_built_in_predicate_hook'(_) :- fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  file name extension predicates
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_file_extension'(logtalk, '.lgt').
'$lgt_file_extension'(logtalk, '.logtalk').
'$lgt_file_extension'(object, '.pl').
'$lgt_file_extension'(prolog, '.pl').
'$lgt_file_extension'(prolog, '.prolog').
'$lgt_file_extension'(prolog, '.pro').


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  backend Prolog compiler features
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_prolog_feature'(prolog_dialect, shumway).
'$lgt_prolog_feature'(prolog_version, v(3, 101, 0)).
'$lgt_prolog_feature'(prolog_compatible_version, @>=(v(3, 0, 0))).

'$lgt_prolog_feature'(encoding_directive, unsupported).
'$lgt_prolog_feature'(sockets, unsupported).
'$lgt_prolog_feature'(tabling, unsupported).
'$lgt_prolog_feature'(engines, unsupported).
'$lgt_prolog_feature'(threads, unsupported).
'$lgt_prolog_feature'(modules, unsupported).
'$lgt_prolog_feature'(coinduction, unsupported).
'$lgt_prolog_feature'(unicode, unsupported).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  default flag values
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_default_flag'(settings_file, allow).
'$lgt_default_flag'(linter, default).
'$lgt_default_flag'(general, warning).
'$lgt_default_flag'(encodings, warning).
'$lgt_default_flag'(unknown_entities, warning).
'$lgt_default_flag'(unknown_predicates, warning).
'$lgt_default_flag'(undefined_predicates, warning).
'$lgt_default_flag'(singleton_variables, warning).
'$lgt_default_flag'(steadfastness, silent).
'$lgt_default_flag'(naming, silent).
'$lgt_default_flag'(duplicated_clauses, silent).
'$lgt_default_flag'(left_recursion, warning).
'$lgt_default_flag'(tail_recursive, silent).
'$lgt_default_flag'(disjunctions, warning).
'$lgt_default_flag'(conditionals, warning).
'$lgt_default_flag'(catchall_catch, silent).
'$lgt_default_flag'(portability, silent).
'$lgt_default_flag'(redefined_built_ins, silent).
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
'$lgt_default_flag'(underscore_variables, dont_care).
'$lgt_default_flag'(complements, deny).
'$lgt_default_flag'(dynamic_declarations, deny).
'$lgt_default_flag'(events, deny).
'$lgt_default_flag'(context_switching_calls, allow).
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
%  operating-system access predicates  (Shumway chunk-271 file ops)
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_prolog_os_file_name'(Path, Path).

'$lgt_expand_path'(Path, ExpandedPath) :-
	'$lgt_shumway_expand_env'(Path, Expanded0),
	(	catch(absolute_file_name(Expanded0, Expanded1), _, fail) ->
		true
	;	Expanded1 = Expanded0
	),
	% Logtalk's core does POSIX ('/') path arithmetic; Shumway/.NET returns
	% native Windows ('\') paths. Normalize so a later join can't produce a
	% broken mixed 'dir\/file' path (which fails exists_file).
	'$lgt_shumway_slashify'(Expanded1, ExpandedPath).

% replace every '\' with '/' so paths the adapter hands to Logtalk are
% POSIX-style and consistent with the core's path assembly.
'$lgt_shumway_slashify'(Path, Normalized) :-
	atom_codes(Path, Codes),
	'$lgt_shumway_bs_to_fs'(Codes, NormCodes),
	atom_codes(Normalized, NormCodes).

'$lgt_shumway_bs_to_fs'([], []).
'$lgt_shumway_bs_to_fs'([0'\\| Cs], [0'/| Ns]) :- !,
	'$lgt_shumway_bs_to_fs'(Cs, Ns).
'$lgt_shumway_bs_to_fs'([C| Cs], [C| Ns]) :-
	'$lgt_shumway_bs_to_fs'(Cs, Ns).

% expand a leading $LOGTALKHOME / $LOGTALKUSER environment variable reference
'$lgt_shumway_expand_env'(Path, Expanded) :-
	(	atom_concat('$LOGTALKHOME/', Rest, Path),
		getenv('LOGTALKHOME', Home) ->
		atom_concat(Home, '/', Home1),
		atom_concat(Home1, Rest, Expanded)
	;	atom_concat('$LOGTALKUSER/', Rest, Path),
		getenv('LOGTALKUSER', User) ->
		atom_concat(User, '/', User1),
		atom_concat(User1, Rest, Expanded)
	;	Expanded = Path
	).

'$lgt_file_exists'(File) :-
	exists_file(File).

'$lgt_delete_file'(File) :-
	catch(delete(File), _, true).

'$lgt_directory_exists'(Directory) :-
	exists_directory(Directory).

'$lgt_current_directory'(Directory) :-
	working_directory(Directory0, Directory0),
	'$lgt_shumway_slashify'(Directory0, Directory).

'$lgt_change_directory'(Directory) :-
	working_directory(_, Directory).

'$lgt_make_directory'(Directory) :-
	( exists_directory(Directory) -> true ; catch(mkdir(Directory), _, true) ).

'$lgt_directory_hashes'(Directory, HashDialect, HashPid) :-
	term_hash(Directory, Hash),
	number_codes(Hash, HashCodes),
	atom_codes(shumway, DialectCodes),
	append([0'_| HashCodes], [0'_| DialectCodes], HashDialectCodes),
	atom_codes(HashDialect, HashDialectCodes),
	append([0'_| HashCodes], [0'_, 0'0], HashPidCodes),
	atom_codes(HashPid, HashPidCodes).

'$lgt_compile_prolog_code'(_, _, _).

'$lgt_load_prolog_code'(File, _, _) :-
	consult(File).

'$lgt_load_prolog_file'(File) :-
	consult(File).

'$lgt_file_modification_time'(_File, 0).

'$lgt_environment_variable'(Variable, Value) :-
	catch(getenv(Variable, Value), _, fail).

'$lgt_decompose_file_name'(File, Directory, Name, Extension) :-
	'$lgt_shumway_decompose'(File, Directory, Name, Extension).

'$lgt_directory_files'(_Directory, []).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  poor-man's file-path decomposition (atom-based; portable)
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_shumway_decompose'(File, Directory, Name, Extension) :-
	atom_codes(File, Codes),
	'$lgt_shumway_last_slash'(Codes, 0, 0, SlashPos),
	'$lgt_shumway_split_at'(Codes, SlashPos, DirCodes, RestCodes),
	( DirCodes == [] -> Directory = './' ; atom_codes(Directory, DirCodes) ),
	'$lgt_shumway_last_dot'(RestCodes, 0, -1, DotPos),
	( DotPos < 0 ->
		atom_codes(Name, RestCodes), Extension = ''
	;	'$lgt_shumway_split_at'(RestCodes, DotPos, NameCodes, ExtCodes),
		atom_codes(Name, NameCodes), atom_codes(Extension, ExtCodes)
	).

'$lgt_shumway_last_slash'([], _, Last, Last).
'$lgt_shumway_last_slash'([C| Cs], I, _, Last) :- (C =:= 0'/ ; C =:= 0'\\), !,
	I1 is I + 1, '$lgt_shumway_last_slash'(Cs, I1, I1, Last).
'$lgt_shumway_last_slash'([_| Cs], I, Acc, Last) :-
	I1 is I + 1, '$lgt_shumway_last_slash'(Cs, I1, Acc, Last).

'$lgt_shumway_last_dot'([], _, Last, Last).
'$lgt_shumway_last_dot'([0'.| Cs], I, _, Last) :- !,
	I1 is I + 1, '$lgt_shumway_last_dot'(Cs, I1, I, Last).
'$lgt_shumway_last_dot'([_| Cs], I, Acc, Last) :-
	I1 is I + 1, '$lgt_shumway_last_dot'(Cs, I1, Acc, Last).

'$lgt_shumway_split_at'(Codes, N, Left, Right) :-
	length(Left, N), append(Left, Right, Codes).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  stream line number (best effort -- Shumway lacks line_count/2)
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_stream_current_line_number'(_Stream, 0).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  open/close abstraction
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_open'(File, Mode, Stream, Options) :-
	open(File, Mode, Stream, Options).

'$lgt_close'(Stream) :-
	close(Stream).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  read_term returning term line positions (no line tracking -> 0-0)
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_read_term'(Stream, Term, Options, 0-0) :-
	read_term(Stream, Term, Options).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  dialect specific term/goal expansion
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_prolog_term_expansion'(_, _) :- fail.
'$lgt_prolog_goal_expansion'(_, _) :- fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  encoding name conversion (unsupported)
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_logtalk_prolog_encoding'(_, _, _) :- fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  lambda expression support
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_copy_term_without_constraints'(Term, Copy) :-
	copy_term(Term, Copy).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  compiled term writing/asserting hooks
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_write_compiled_term'(Stream, Term, _Kind, _Path, _Line) :-
	write_canonical(Stream, Term),
	write(Stream, '.\n').

'$lgt_assertz_entity_clause'(Clause, _Kind) :-
	assertz(Clause).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  error term normalization
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_normalize_error_term'(Error, Error).


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  string type (unsupported)
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_string'(_) :- fail.
'$lgt_string_codes'(_, _) :- fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  module qualification (no modules)
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

'$lgt_user_module_qualification'(Goal, Goal).

'$lgt_find_visible_module_predicate'(_, _, _) :- fail.
'$lgt_current_module_predicate'(_, _) :- fail.


%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
%  {}/1 shortcut for logtalk_load/logtalk_make
%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

{Files} :-
	'$lgt_conjunction_to_list'(Files, List),
	logtalk_load(List).


% atomic_concat(+atomic, +atomic, ?atom) — Logtalk adapter contract
% (library objects `uses(user, [atomic_concat/3])`, e.g. json_graph).

atomic_concat(Atomic1, Atomic2, Atom) :-
	(	var(Atomic1) ->
		throw(error(instantiation_error, atomic_concat/3))
	;	var(Atomic2) ->
		throw(error(instantiation_error, atomic_concat/3))
	;	\+ atomic(Atomic1) ->
		throw(error(type_error(atomic, Atomic1), atomic_concat/3))
	;	\+ atomic(Atomic2) ->
		throw(error(type_error(atomic, Atomic2), atomic_concat/3))
	;	'$lgt_shumway_atomic_atom'(Atomic1, Atom1),
		'$lgt_shumway_atomic_atom'(Atomic2, Atom2),
		atom_concat(Atom1, Atom2, Atom)
	).

'$lgt_shumway_atomic_atom'(Atomic, Atom) :-
	(	atom(Atomic) ->
		Atom = Atomic
	;	term_to_atom(Atomic, Atom)
	).

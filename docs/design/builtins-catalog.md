# Builtins Catalog (v1)

This document enumerates the builtin predicates implemented in Shumway v1. The selection is oriented toward grammar processing, embedded rules engines, and ISO Prolog compatibility.

## Conventions

For each builtin:

- **Signature**: `name/arity`
- **ISO**: whether it conforms to ISO Prolog standard semantics.
- **Modes**: how it can be used (which arguments must be bound at call time).
- **Description**: what it does.
- **Errors**: which standard error terms it may throw.

Mode indicators:
- `+`: argument must be bound at call.
- `-`: argument must be unbound at call.
- `?`: either bound or unbound.

## Classification

Builtins are split into two layers (per ADR-008):

- **Core**: implemented in C#. Cannot be shadowed even locally. Critical control flow, type tests, basic arithmetic.
- **Library**: implemented either in C# (for performance) or Prolog (when natural). Can be shadowed locally by a module that wants a custom implementation.

The classification is noted in each section.

---

## 1. Control flow (core)

### `true/0` [ISO]

Always succeeds, no side effects.

### `fail/0`, `false/0` [ISO]

Always fail.

### `\+ /1` (not provable) [ISO]

`\+ Goal` succeeds if Goal fails. No bindings result from Goal.

Modes: `\+(+Goal)`.

Errors: `instantiation_error` if Goal is unbound; `type_error(callable, Goal)` if not callable.

### `not/1`

Equivalent to `\+/1`. Provided for compatibility.

### `, /2` (conjunction) [ISO]

Sequential composition.

### `; /2` (disjunction) [ISO]

Alternative composition. Also handles `Cond -> Then ; Else` if-then-else.

### `-> /2` (if-then) [ISO]

`Cond -> Then` commits to the first solution of Cond, then runs Then.

### `*-> /2` (soft cut)

`Cond *-> Then ; Else` is like if-then-else but allows multiple solutions of Cond.

### `! /0` (cut) [ISO]

Discards choice points back to the predicate entry.

### `call/1`, `call/2..call/8` [ISO]

`call(Goal)` calls Goal. `call(Goal, ExtraArg1, ...)` appends extra arguments to Goal before calling.

Modes: first argument `+`.

Errors: `instantiation_error`, `type_error(callable, Goal)`.

### `catch/3` [ISO]

`catch(Goal, Catcher, Recovery)`: runs Goal; if it throws an error that unifies with Catcher, runs Recovery.

### `throw/1` [ISO]

Raises a Prolog error. The argument is the error term.

### `halt/0`, `halt/1` [ISO]

Terminates the engine. `halt/1` with an integer exit code.

In Shumway: `halt/0` raises a `HaltException` that the embedding layer catches. The engine does not actually exit the .NET process.

---

## 2. Type testing (core)

### `var/1` [ISO]

Succeeds if argument is an unbound variable.

### `nonvar/1` [ISO]

Succeeds if argument is bound (not a variable).

### `atom/1` [ISO]

Succeeds if argument is an atom.

### `atomic/1` [ISO]

Succeeds if argument is an atom, number, or string.

### `number/1` [ISO]

Succeeds if argument is an integer (including bigint) or float.

### `integer/1` [ISO]

Succeeds if argument is an integer (including bigint).

### `float/1` [ISO]

Succeeds if argument is a float.

### `compound/1` [ISO]

Succeeds if argument is a compound term (STR or LIS).

### `is_list/1` [ISO-de-facto]

Succeeds if argument is a proper list (cons cells terminated by `[]`).

### `is_pstr/1` (Shumway extension)

Succeeds if argument is a PSTR.

### `is_string/1` (Shumway extension)

Succeeds if argument is a STRING cell.

### `ground/1` [ISO]

Succeeds if argument has no unbound variables.

### `callable/1` [ISO]

Succeeds if argument is callable as a goal (atom or compound).

### `is_foreign/1` (Shumway extension)

Succeeds if argument is a foreign object.

---

## 3. Comparison and unification (core)

### `= /2` [ISO]

Unifies two terms.

### `\= /2` [ISO]

`X \= Y` succeeds if X and Y do not unify. No bindings result.

### `== /2` [ISO]

Structural equality. Does not perform unification.

### `\== /2` [ISO]

Structural inequality.

### `@< /2`, `@> /2`, `@=< /2`, `@>= /2` [ISO]

Term ordering (standard order of terms).

### `compare/3` [ISO]

`compare(Order, X, Y)` unifies Order with `<`, `=`, or `>` depending on the comparison.

### `unify_with_occurs_check/2` [ISO]

Unifies with occurs check. Slower than `=/2` but prevents infinite structures.

---

## 4. Arithmetic (core)

### `is/2` [ISO]

`X is Expr` evaluates Expr and unifies the result with X.

Supported operators and functions in Expr:

#### Binary operators
- `+`, `-`, `*`, `/`, `//` (integer division), `mod`, `rem`
- `div` (integer divide rounding to negative infinity)
- `**`, `^` (power)
- `min`, `max`
- `gcd`, `lcm`
- `>>`, `<<` (shift)
- `/\`, `\/`, `xor` (bitwise)

#### Unary operators
- `-`, `+` (sign)
- `abs`
- `sign`
- `\`(bitwise complement)

#### Functions
- `sqrt`, `cbrt`
- `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2/2`
- `exp`, `log`, `log/2`
- `floor`, `ceiling`, `round`, `truncate`
- `float`, `integer` (conversions)
- `float_integer_part`, `float_fractional_part`

#### Constants
- `pi`, `e`
- `inf`, `nan`
- `max_tagged_integer`, `min_tagged_integer`
- `epsilon`

### `=:= /2`, `=\= /2` [ISO]

Arithmetic equality and inequality.

### `< /2`, `> /2`, `=< /2`, `>= /2` [ISO]

Arithmetic comparison.

### `succ/2` [ISO]

`succ(X, Y)`: Y = X + 1, with both modes supported.

### `plus/3`

`plus(X, Y, Z)`: Z = X + Y, with various modes.

---

## 5. Atom and string manipulation (library, mostly C#)

### `atom_length/2` [ISO]

`atom_length(+Atom, -Length)`: length in characters.

### `atom_concat/3` [ISO]

`atom_concat(+A, +B, ?C)`: concatenates A and B.
`atom_concat(?A, ?B, +C)`: splits C into all possible A and B pairs (non-deterministic).

### `atom_chars/2` [ISO]

`atom_chars(+Atom, -Chars)`: list of single-char atoms.
`atom_chars(-Atom, +Chars)`: builds atom from list.

### `atom_codes/2` [ISO]

`atom_codes(+Atom, -Codes)`: list of integer codes.
`atom_codes(-Atom, +Codes)`: builds atom.

### `atom_to_term/3` [ISO]

`atom_to_term(+Atom, -Term, -Bindings)`: parses Atom as a term.

### `atom_string/2`

Conversion between Atom and STRING type (Shumway extension).

### `atom_to_pstr/2`, `pstr_to_atom/2`

Conversion between atom and PSTR.

### `char_code/2` [ISO]

`char_code(?Char, ?Code)`: maps between single-char atom and code.

### `sub_atom/5` [ISO]

`sub_atom(+Atom, ?Before, ?Length, ?After, ?Sub)`: substring extraction (non-deterministic).

### `upcase_atom/2`, `downcase_atom/2`

Case conversion.

### `atom_number/2`

`atom_number(?Atom, ?Number)`: bidirectional conversion.

---

## 6. PSTR-specific (library, C#)

See `pstr-design.md` for details.

### `pstr_length/2`

Codepoint count of a PSTR.

### `pstr_codes/2`

PSTR ↔ list of codes.

### `pstr_chars/2`

PSTR ↔ list of single-char atoms.

### `pstr_concat/3`

PSTR concatenation.

### `sub_pstr/5`

Zero-copy substring extraction.

### `pstr_to_string/2`, `string_to_pstr/2`

PSTR ↔ STRING conversion.

---

## 7. List manipulation (library, mostly Prolog)

### `length/2` [ISO]

`length(?List, ?N)`: bidirectional.

### `append/3` [ISO]

`append(?A, ?B, ?C)`: list concatenation, with all modes.

### `member/2` [ISO]

`member(?Elem, +List)`: list membership, non-deterministic.

### `memberchk/1`

Deterministic `member/2`: succeeds at most once.

### `reverse/2` [ISO]

`reverse(?L1, ?L2)`: list reversal.

### `nth0/3`, `nth1/3` [ISO-de-facto]

0-indexed and 1-indexed list access.

### `last/2`

`last(+List, -Last)`.

### `msort/2`, `sort/2`, `sort/4` [ISO]

Sort with various options.

### `list_to_set/2`

Remove duplicates from a list.

### `select/3`

`select(?Elem, +List, -Rest)`: remove an element from a list (non-deterministic).

### `permutation/2`

`permutation(?L1, ?L2)`: all permutations.

### `maplist/2..maplist/5` [ISO-de-facto]

Apply a goal to elements of one or more lists.

### `foldl/4..foldl/6`

Fold a goal over a list.

### `include/3`, `exclude/3`

Filter a list.

### `numlist/3`

`numlist(+Low, +High, -List)`: integer range as a list.

### `sum_list/2`, `max_list/2`, `min_list/2`

Aggregations.

---

## 8. Term construction and decomposition (core)

### `functor/3` [ISO]

`functor(?Term, ?Name, ?Arity)`: term's functor name and arity.

### `arg/3` [ISO]

`arg(+N, +Term, ?Value)`: N-th argument of a compound.

### `=../2` (univ) [ISO]

`Term =.. List`: bidirectional decomposition/construction.

### `copy_term/2` [ISO]

`copy_term(+Term, -Copy)`: fresh copy with fresh variables.

### `copy_term/3` [ISO]

`copy_term(+Term, -Copy, -AttrGoals)`: copy with attribute goals (relevant when attvars are implemented in Phase 4).

### `term_variables/2` [ISO]

`term_variables(+Term, -Vars)`: list of variables in the term.

### `numbervars/3` [ISO-de-facto]

`numbervars(+Term, +Start, -End)`: replace variables with `'$VAR'(N)` terms for I/O.

---

## 9. Database manipulation (core)

### `assertz/1`, `asserta/1` [ISO]

Add a clause to a dynamic predicate.

### `retract/1`, `retractall/1` [ISO]

Remove clauses.

### `clause/2` [ISO]

`clause(+Head, ?Body)`: enumerate clauses of a predicate.

### `abolish/1` [ISO]

`abolish(Name/Arity)`: remove a predicate completely.

### `current_predicate/1` [ISO]

Check if a predicate is defined.

### `predicate_property/2` [ISO]

Query properties of a predicate (dynamic, public, defined_in, etc.).

---

## 10. All solutions (library, Prolog)

### `findall/3` [ISO]

`findall(?Template, +Goal, -List)`: all solutions, with copies.

### `bagof/3` [ISO]

`bagof(?Template, +Goal, -List)`: solutions, possibly grouped by free variables.

### `setof/3` [ISO]

`setof(?Template, +Goal, -List)`: sorted unique solutions.

### `aggregate_all/3`

`aggregate_all(+Template, +Goal, -Result)`: aggregations like sum, count, bag, set.

### `forall/2` [ISO-de-facto]

`forall(+Cond, +Action)`: succeeds iff for all solutions of Cond, Action succeeds.

---

## 11. I/O basic (library, C#)

### `write/1`, `write/2` [ISO]

Write a term, with operators.

### `writeln/1`

Write a term with operators, then newline.

### `print/1`, `print/2`

Write with portray hooks (Phase 2; currently same as `write`).

### `write_term/2`, `write_term/3` [ISO]

Write with options (quoted, ignore_ops, etc.).

### `write_canonical/1`, `write_canonical/2` [ISO]

Write without operators, quoted.

### `read/1`, `read/2` [ISO]

Read a term from input.

### `read_term/2`, `read_term/3` [ISO]

Read with options.

### `nl/0`, `nl/1` [ISO]

Newline.

### `put_char/1`, `put_char/2` [ISO]

Output a character.

### `get_char/1`, `get_char/2` [ISO]

Read a character.

### `peek_char/1`, `peek_char/2` [ISO]

Lookahead.

### `tab/1`, `tab/2`

Output N spaces.

### `format/1`, `format/2`, `format/3` [de-facto]

Formatted output. Subset of SWI's format/2 supported.

---

## 12. Streams (library, C#)

### `open/3`, `open/4` [ISO]

`open(+Path, +Mode, -Stream)` where Mode is `read`, `write`, `append`.

### `close/1`, `close/2` [ISO]

Close a stream.

### `current_input/1`, `current_output/1` [ISO]

Get current input/output streams.

### `set_input/1`, `set_output/1` [ISO]

Change current streams.

### `stream_property/2` [ISO]

Query stream properties.

### `set_stream/2`

Configure stream properties.

### `at_end_of_stream/0`, `at_end_of_stream/1` [ISO]

End-of-file check.

### `read_string/5`

`read_string(+Stream, ?Length, -String)`: read up to N codes/chars into a STRING.

---

## 13. DCG (library, Prolog + compiler support)

DCGs (Definite Clause Grammars) are a fundamental tool for grammar processing. The compiler translates `-->` rules to regular Prolog clauses; the following builtins are part of the runtime.

### `phrase/2`, `phrase/3` [ISO]

`phrase(+Body, ?List)`: apply DCG Body to List.
`phrase(+Body, ?List, ?Rest)`: with explicit remainder.

### `call//1`, `call//2..call//6`

DCG meta-call. `call(NonTerm)//N` invokes a DCG non-terminal as part of a DCG body.

### `{}//1`

DCG escape to regular Prolog goal. `{ Goal }` in a DCG body runs Goal but does not consume input.

### `! //0` (DCG cut)

Cut within a DCG.

### Compiler support for `-->` 

The compiler recognizes `-->` and rewrites the clause to standard Prolog with two extra arguments (input and output difference lists). Optimized for PSTR input.

---

## 14. Flags and configuration (core)

### `set_prolog_flag/2` [ISO]

Set an engine flag (e.g., `double_quotes`, `unknown`).

### `current_prolog_flag/2` [ISO]

Query a flag.

### `prolog_flag/2`

Alias for `current_prolog_flag/2`.

### Flags supported in v1:

- `double_quotes`: `codes` (default), `chars`, `atom`, `string`, `pstr`.
- `unknown`: `error` (default), `fail`, `warning`.
- `bounded`: `false` (Shumway supports bigints).
- `max_integer`, `min_integer`: query the inline integer range.
- `integer_rounding_function`: `down` (default for `//`).
- `double_quotes_in_dcg`: how DCG handles strings.
- `occurs_check`: `false` (default), `true`, `error`.
- `strict_dynamic_declarations` (Shumway extension): auto-declare dynamic predicates or require explicit `:- dynamic`.

---

## 15. Foreign / embedding (library, C#)

### `foreign_call/3` (Shumway extension)

Invoke a foreign predicate by name. Used internally by the embedding API.

### `foreign_object/1`

Type test for foreign objects.

### `foreign_get_property/3`

`foreign_get_property(+Object, +Property, ?Value)`: access a property of a foreign object via reflection.

### `foreign_method/3`

`foreign_method(+Object, +Method, +Args)`: call a method on a foreign object.

(These low-level builtins are for power users; the typical embedding flow uses C# code with `[PrologPredicate]` attributes and the source-generated converters.)

---

## 16. Exceptions (core)

### `throw/1`, `catch/3` [ISO]

(Already listed in Control Flow.)

### Standard error terms generated by builtins:

- `instantiation_error`: an argument expected to be bound is unbound.
- `type_error(ExpectedType, Got)`: wrong type.
- `domain_error(ExpectedDomain, Got)`: value out of valid domain.
- `existence_error(ObjectType, Object)`: e.g., predicate not found.
- `permission_error(Operation, ObjectType, Object)`: e.g., modify static predicate.
- `representation_error(Flag)`: limit exceeded (max atom length, etc.).
- `evaluation_error(Reason)`: arithmetic error (`zero_divisor`, `undefined`, etc.).
- `syntax_error(Reason)`: malformed term.
- `system_error(Reason)`: I/O, resource issues.
- `resource_error(Resource)`: out of memory, stack overflow, etc.

Each is wrapped as `error(ErrorTerm, ImplDefined)` per ISO.

---

## 17. Miscellaneous (mixed)

### `between/3`

`between(+Low, +High, ?X)`: integer in [Low, High], non-deterministic on the third arg.

### `succ_list/2`

(Trivial helper, can be defined in Prolog.)

### `ignore/1`

`ignore(Goal)`: like `Goal ; true` but no choice point left.

### `once/1` [ISO]

`once(Goal)`: succeeds with the first solution of Goal, commits.

### `\\\\\\\+`, `not`: see Control Flow

### `apply/2`

`apply(+Goal, +ExtraArgs)`: deprecated form of `call/N`, kept for compatibility.

### `time/1`

`time(Goal)`: run Goal and print elapsed time.

### `statistics/2`

Query runtime statistics (CPU time, heap size, etc.).

### `current_op/3` [ISO]

Query operator definitions.

### `op/3` [ISO]

Define an operator.

---

## 18. Code loading (library, C#)

### `consult/1`, `[File]` [ISO]

Load a Prolog source file.

### `reconsult/1`

Reload a file (replace existing module).

### `load_files/1`, `load_files/2`

`load_files([file1.pl, file2.pl], [silent(true)])`: bulk loading.

### `ensure_loaded/1`

Load only if not already loaded.

### `make/0`

Reload all modified files (SWI-style; subset supported).

---

## 19. Meta-programming (library, mixed)

### `assert/1`

Alias for `assertz/1`.

### `dynamic/1`

`:- dynamic foo/2.` directive. Builtin form `dynamic(foo/2)` also accepted.

### `discontiguous/1`

`:- discontiguous foo/2.` allows non-contiguous clauses (typical of mode-organized code).

### `multifile/1`

`:- multifile foo/2.` allows the same predicate in multiple files.

(Phase 1 supports parsing of these directives; semantic enforcement is at the bundler/loader.)

---

## 20. Hashing and identity (library, C#)

### `term_hash/2`

`term_hash(+Term, -Hash)`: deterministic hash of a term (useful for memoization, hash sets).

### `variant/2`

`variant(+T1, +T2)`: succeeds if T1 and T2 are variants (same structure with renamed variables).

---

## Total

Roughly 150 builtins in v1, covering:

- ISO Prolog conformance (most of the standard).
- Practical extras (SWI-de-facto common builtins).
- Shumway-specific (PSTR, foreign, etc.).

Builtins are documented in detail with examples in `docs/api/builtins/<category>/`. This catalog is the index.

## Implementation order

Suggested order for implementation (each category builds on previous ones):

1. Type testing (`var`, `atom`, etc.) — no dependencies.
2. Comparison (`==`, `@<`, etc.) — depends on unification.
3. Arithmetic (`is/2` and comparisons) — depends on type system.
4. Term construction (`functor`, `=..`, `copy_term`) — depends on heap.
5. Control flow (`!`, `,`, `;`, `->`, `catch`, `throw`) — depends on stack and trails.
6. Atom/string (`atom_concat`, `atom_codes`) — depends on atom table.
7. Lists (`length`, `append`, `member`) — depends on lists in heap.
8. Database (`assertz`, `retract`, `clause`) — depends on predicate tables.
9. All solutions (`findall`, `bagof`, `setof`) — depends on copy_term and assertz.
10. PSTR-specific — depends on PSTR cell implementation.
11. I/O (`write`, `read`) — depends on streams.
12. DCG — depends on compiler integration.
13. Foreign / embedding — depends on stable runtime.

Each builtin gets unit tests and (where applicable) ISO conformance tests.

## See also

- ADR-008 (Module Visibility): builtins core vs library distinction.
- `pstr-design.md`: PSTR-specific builtins detail.
- ISO/IEC 13211-1 Prolog standard for ISO conformance reference.

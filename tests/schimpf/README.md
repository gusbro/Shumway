# ISO test patterns (Schimpf)

Runner for Joachim Schimpf's **ISO-Prolog test patterns** (2013, public
domain): 945 tests focused on error handling and corner cases of the
standard builtins — error classes, culprits, context indicators, stream
discipline, meta-call conversion.

The repository carries **no third-party content**: the suite is downloaded
from its author's site into `suite/`, which is git-ignored. This directory
holds only our runner and auxiliary definitions.

## Setup

Download <https://eclipseclp.org/wiki/uploads/Prolog/iso_test_js.tgz> and
extract it into `suite/` (the tarball wraps everything in an
`iso_test_js/` directory — strip it):

```
curl -sSL -o iso_test_js.tgz https://eclipseclp.org/wiki/uploads/Prolog/iso_test_js.tgz
mkdir -p suite
tar xzf iso_test_js.tgz --strip-components=1 -C suite
```

That leaves `suite/` with the suite's `harness.pl`, `iso.tst`, the
`iso_8_8.pl` / `iso_8_10.pl` databases and the data files the tests expect
in their working directory (`hello`, `scowen`, `empty`, `nowrite`).

## Running

From `suite/` (the tests open their data files relative to the cwd):

```
cd suite
echo "run_iso." | ../../../dist/Release/shumway harness.pl ../auxiliaries_shumway.pl ../driver.pl
```

Expected summary (2026-09-01):

```
summary(total(945),ok(926),failed(12),skipped(7),broken(0),malformed(8),non_test(0))
```

- **skipped 7** — tests the author marked `fixme` in `iso.tst`.
- **malformed 8** — terms Shumway's reader rejects (ECLiPSe-specific
  syntax); they are counted, not silently dropped.
- **broken 0** — no test escaped the harness protocol.

## Layout

| File | Contents |
|---|---|
| `driver.pl` | robust runner over the harness primitives: reads each term itself (the harness's own repeat/fail loop stops silently mid-file on this suite), wraps each test in a catch-all, counts malformed/broken; `run_iso/0` entry |
| `auxiliaries_shumway.pl` | the auxiliary predicates the suite requires (`iso_test_ensure_loaded/1`, `iso_test_os/1`, …), Shumway definitions — replaces the suite's ECLiPSe-oriented `auxiliaries.pl` |
| `suite/` | the downloaded suite (git-ignored) |

## Accommodations

`run_iso/0` applies two accommodations before running, both suite-era
artifacts rather than engine questions:

- `set_prolog_flag(double_quotes, codes)` — the suite predates the
  chars-default era and writes `"abc"` expecting code lists.
- The `bin` alias is pre-opened: the suite's binary-stream section begins
  with `close(bin, [force(true)])` before `bin` ever exists — an ECLiPSe
  idiom where `force(true)` swallows the existence error. Shumway (like
  SWI and Scryer) raises `existence_error(stream, bin)` there; without a
  pre-opened alias the section's `open` never runs and 10 tests cascade.

## The 12 remaining failures — documented divergences

Each was arbitrated against the ISO text and the reference engines before
deciding to keep Shumway's behavior. Test numbers are the driver's
sequential count over `iso.tst`.

| Test | Suite expects | Shumway does | Why we keep ours |
|---|---|---|---|
| 373 | `close(foo, [force(true)])` on a closed alias succeeds | `existence_error(stream, foo)` | `force(true)` covers errors *while closing* (§8.11.6); resolving the alias precedes that. SWI and Scryer raise too |
| 403 | `set_stream_position(S, 3.5)` on a closed S: `domain_error(stream_position, 3.5)` | `existence_error(stream, …)` | §8.11.9.3 lists the existence check (d) before position validity (e); when several error conditions hold the choice is implementation-defined (§7.12.1) |
| 622 | `current_op(1, 2, _)`: `type_error(atom, 2)` | `domain_error(operator_specifier, 2)` | §8.14.4.3's specifier error is domain_error whatever the term's type; GNU agrees exactly |
| 760 | `number_chars(N, [1\|_])`: `type_error(character, 1)` | `instantiation_error` | with N unbound the partial list is checked first — the outcome Neumerkel's number_chars set sanctions |
| 762 | `number_chars([], 1)`: `type_error(list, 1)` | `type_error(number, [])` | both conditions hold; the Number argument is checked first (§7.12.1 leaves the pick to the processor) |
| 816 | `number_codes(N, ['1'\|_])`: `representation_error(character_code)` | `instantiation_error` | same partial-list precedence as 760, codes spelling |
| 818 | `number_codes([], 1)`: `type_error(list, 1)` | `type_error(number, [])` | same as 762 |
| 856 | `1 rem 0`: `type_error(evaluable, rem/2)` | `evaluation_error(zero_divisor)` | rem *is* ISO (§9.1.7) and division by zero is zero_divisor; the expectation assumes an engine without rem |
| 864 | `-2 ** 3.0`: `evaluation_error(undefined)` | `-8.0` | the suite reads §9.3.1.3's "not an integer" as the exponent's *type*; GNU, SWI and Scryer all read it as the *value* and return -8.0 |
| 865 | `-2.0 ** 3.0`: `evaluation_error(undefined)` | `-8.0` | same as 864 |
| 900 | `0 / 0`: `evaluation_error(undefined)` | `evaluation_error(zero_divisor)` | §9.1.7: a zero divisor is zero_divisor, with no 0/0 special case; GNU agrees |
| 901 | `0 // 0`: `evaluation_error(undefined)` | `evaluation_error(zero_divisor)` | same as 900 |

The engine fixes this suite produced are pinned — in our own formulation —
in `tests/Shumway.Tests.IsoConformance/SchimpfArcRegressionTests.cs`.

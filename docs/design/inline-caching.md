# Inline caching at call sites — NOT BUILT

> **This approach was never implemented.** An early Tier-1 design considered a
> per-call-site inline cache — a static `CallSiteCache` field per call site, a
> `_predicateTableVersion` invalidation counter, and a `callvirt
> PredicateDelegate.Invoke` fast path. None of it shipped; those symbols do not
> exist in the codebase. This file is kept only as a redirect; the original
> proposal lives in git history.

## What ships instead: threaded-continuation dispatch

Tier-1 IL calls are dispatched by **resume markers**, not a call-site cache. A
non-tail Call from IL sets `Cp = EncodeResumeMarker(functorId, cursor)` and
`IlTailCallPending`, then returns to the interpreter's dispatch loop; the loop
recognises the marker and re-invokes the callee's `PredicateDelegate` at the
matching resume cursor. The C# stack stays O(1) regardless of Prolog call
depth, and backtracking falls out of the normal choice-point cascade. The old
recursive `RunSubroutine` machinery was deleted.

See:

- [`il-region-compilation.md`](il-region-compilation.md) — the shipped Tier-1
  dispatch and region-compilation model.
- [ADR-011](../architecture/adr/011-il-compiler-architecture.md) — IL compiler
  architecture, including the note on threaded dispatch.
- `src/Shumway.Core/Activation.Tier1.cs` — `EncodeResumeMarker` /
  `DecodeResumeMarker` / `IsResumeMarker` and the dispatch entry points.

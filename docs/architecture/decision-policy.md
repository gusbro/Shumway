# Decision policy — what is a "major decision", and where decisions live

Shumway records every significant design decision as an **Architecture Decision
Record** in [`adr/`](adr/) — one file per decision, each opening with a Status
line that tells you whether it is accepted, shipped, rejected (with the
evidence), or a prototype. The non-negotiable rules those decisions produced
are consolidated in [`invariants.md`](invariants.md).

## What counts as a major decision

If a change involves any of the following, **stop and write (or amend) an ADR
before implementing**:

- Adding a new cell tag.
- Changing the trail format.
- Adding a new top-level opcode.
- Changing the atom GC strategy.
- Changing the module resolution mechanism.
- Changing the backtracking or choice-point model.
- Introducing a new external dependency.
- Changing the threading model.
- Breaking anything in [`invariants.md`](invariants.md).

These are the areas where coherence across the codebase is critical and an
ad-hoc change breaks invariants in non-obvious places. Everything else is an
ordinary change: implement, test, commit.

## Where to look things up

- [`adr/`](adr/) — the decisions themselves, numbered sequentially with
  descriptive filenames; the Status line at the top of each is kept current.
  An ADR is a *decision record*: it may keep a superseded or never-built design
  in place, clearly marked, since its value is the history. Reference docs
  (`design/`, `guide/`, `overview.md`) are held to the opposite standard — they
  state what is true now (see the Documentation invariant in `invariants.md`).
- [`invariants.md`](invariants.md) — the consolidated invariant catalog the
  decisions add up to.
- [`overview.md`](overview.md) — the architecture tour, with a
  subsystem → ADR map at the end.
- The repository root's `CLAUDE.md` carries the maintainers' working
  quick-reference (decision → ADR table) and the phase-by-phase project log;
  it is tooling-facing, not part of this documentation set.

Historical documents (under [`../history/`](../history/)) say "a major
decision under CLAUDE.md" — that phrase predates this document; the policy
they invoke is the one above.

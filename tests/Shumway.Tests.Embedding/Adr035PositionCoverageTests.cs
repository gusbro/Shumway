using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-035 — the POSITION-COVERAGE invariant: every source line that carries a
/// user goal must have a debug stop site after the whole transform pipeline ran.
///
/// <para>Positions are what every debugger feature hangs off — stop sites, call-stack
/// lines, rewind marks, Set Next Statement targets — and the transform pipeline REBUILDS
/// goals (DCG expansion, phrase expansion, meta lowering, mode specialization, native
/// blocks). A transform that drops <c>Position</c> while rebuilding does not fail any
/// functional test: the goal still RUNS — it just silently disappears from the debugger
/// (the PhraseTransform report: <c>phrase(g(X), L)</c> expanded without its position, so
/// the call had no site, its frame showed line 0, and the DCG re-enter had no anchor).
/// This suite is the tripwire: one representative program per transform shape, asserting
/// site presence LINE BY LINE.</para></summary>
[Collection("debugger")]
public class Adr035PositionCoverageTests
{
    private readonly ITestOutputHelper _log;
    public Adr035PositionCoverageTests(ITestOutputHelper log) => _log = log;

    /// <summary>Consults <paramref name="program"/> in debug mode (line 1 is the flag
    /// directive, so the program's own lines start at 2) and asserts each of
    /// <paramref name="stoppableLines"/> owns at least one stop site ON that very line —
    /// no snapping, which is exactly how a dropped position hides. The program goes
    /// through a UNIQUE temp file, never the shared "&lt;string&gt;" pseudo-file: the
    /// site table is global and keyed (file, line), so with every test on
    /// "&lt;string&gt;" a sibling's goal on the same line satisfied the assert and a
    /// genuinely dropped position only surfaced when the test ran alone.</summary>
    private void AssertStoppable(string program, params int[] stoppableLines)
    {
        string path = Path.Combine(Path.GetTempPath(),
            "shumway_poscov_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(path, ":- set_prolog_flag(compile_mode, debug).\n" + program);
        try
        {
            var engine = new PrologEngine();
            engine.ConsultFile(path);
            // Sites are interned into the global table when a query first links the
            // compiled code; a trivial one forces it.
            engine.QueryAll("true.").ToList();

            int fileId = Shumway.Core.DebugSiteTable.InternFile(path);
            var missing = new List<int>();
            foreach (int line in stoppableLines)
                if (Shumway.Core.DebugSiteTable.SitesOnLine(fileId, line).Count == 0)
                    missing.Add(line);

            if (missing.Count > 0)
                _log.WriteLine("lines without a stop site: " + string.Join(", ", missing));
            Assert.Empty(missing);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void PlainClauses_EveryGoalLineIsStoppable() => AssertStoppable(
        //  2: run(X) :-
        //  3:     first(X),
        //  4:     Y is X + 1,
        //  5:     use(Y).
        //  6: first(1).
        //  7: use(_).
        "run(X) :-\n    first(X),\n    Y is X + 1,\n    use(Y).\nfirst(1).\nuse(_).\n",
        3, 4, 5);

    [Fact]
    public void PhraseCalls_KeepTheirLine() => AssertStoppable(
        // The reported bug: the phrase/2 and phrase/3 EXPANSIONS must keep the line.
        //  2: run(X) :-
        //  3:     phrase(g(X), [a]),
        //  4:     phrase(g(X), [a], []).
        //  5: g(1) --> [a].
        "run(X) :-\n    phrase(g(X), [a]),\n    phrase(g(X), [a], []).\ng(1) --> [a].\n",
        3, 4);

    [Fact]
    public void DcgBodies_EveryConstructKeepsItsLine() => AssertStoppable(
        //  2: g(X) -->
        //  3:     [a],
        //  4:     nt(X),
        //  5:     { X > 0 },
        //  6:     \+ [q],
        //  7:     [b].
        //  8: nt(1) --> [n].
        "g(X) -->\n    [a],\n    nt(X),\n    { X > 0 },\n    \\+ [q],\n    [b].\n" +
        "nt(1) --> [n].\n",
        3, 4, 5, 6, 7);

    [Fact]
    public void DcgCompoundHeadArg_BodyLinesStayStoppable() => AssertStoppable(
        // Under debug codegen the fail-fast lowering (leading-terminal hoist +
        // compound-head-arg defer) is OFF: the compound arg stays in the head
        // (no body goal, so line 2 binds forward like any rule head — see
        // AHitNamesTheBreakpointTheUserDrew) and the first terminal keeps its
        // own body goal ON line 3 — the site the hoist used to erase.
        //  2: g(f(X)) -->
        //  3:     [a],
        //  4:     nt(X).
        //  5: nt(1) --> [n].
        "g(f(X)) -->\n    [a],\n    nt(X).\nnt(1) --> [n].\n",
        3, 4);

    [Fact]
    public void DcgPushback_KeepsALineForTheLink() => AssertStoppable(
        // The pushback link (SFinal = [p|SMid]) belongs to the head line where `, [p]` is
        // written; the body goals sit on other lines, so line 2 is stoppable only if the
        // link goal kept that position.
        //  2: g(X), [p] -->
        //  3:     [a],
        //  4:     nt(X).
        //  5: nt(1) --> [n].
        "g(X), [p] -->\n    [a],\n    nt(X).\nnt(1) --> [n].\n",
        2, 3, 4);

    [Fact]
    public void DcgDisjunctionBranches_KeepTheirLines() => AssertStoppable(
        //  2: g(X) -->
        //  3:     (   [a], nt(X)
        //  4:     ;   [b], nt(X)
        //  5:     ),
        //  6:     [z].
        //  7: nt(1) --> [n].
        "g(X) -->\n    (   [a], nt(X)\n    ;   [b], nt(X)\n    ),\n    [z].\n" +
        "nt(1) --> [n].\n",
        3, 4, 6);

    [Fact]
    public void MetaConstructs_KeepTheirLines() => AssertStoppable(
        //  2: run(L) :-
        //  3:     findall(X, p(X), L),
        //  4:     \+ q(0),
        //  5:     once(p(_)),
        //  6:     catch(p(_), _, fail),
        //  7:     ( p(1) -> t(1) ; t(2) ).
        //  8: p(1).
        //  9: q(1).
        // 10: t(_).
        "run(L) :-\n    findall(X, p(X), L),\n    \\+ q(0),\n    once(p(_)),\n" +
        "    catch(p(_), _, fail),\n    ( p(1) -> t(1) ; t(2) ).\n" +
        "p(1).\nq(1).\nt(_).\n",
        3, 4, 5, 6, 7);

    [Fact]
    public void StaticCallN_AndSnips_KeepTheirLines() => AssertStoppable(
        //  2: run(X) :-
        //  3:     call(p, X),
        //  4:     [! p(X) !].
        //  5: p(1).
        "run(X) :-\n    call(p, X),\n    [! p(X) !].\np(1).\n",
        3, 4);
}

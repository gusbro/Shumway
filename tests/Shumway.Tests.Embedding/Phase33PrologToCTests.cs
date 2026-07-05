using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 — SWI/Edinburgh compatibility surfaced by the PrologToC corpus
/// (C:\temp\PrologToC, a real Prolog-to-C compiler): ISO include/1,
/// initialization/1, DEC-10 comma-chained mode directives, getenv/2,
/// recorded-DB 2-arg forms, display/1-2, append/2, the open-open append/3
/// mode, Edinburgh see/tell resume semantics, user_error, read/1 with live
/// operators and lexical clause-end detection.
/// </summary>
public class Phase33PrologToCTests : IDisposable
{
    private readonly string _dir;

    public Phase33PrologToCTests()
    {
        _dir = Path.Combine(Path.GetTempPath(),
            "shumway_p2c_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    private static string Slash(string p) => p.Replace('\\', '/');

    // ---------- include/1 ----------

    [Fact]
    public void Include_TextuallyIncludes_RelativeToIncludingFile()
    {
        Write("inner.pl", "inner_fact(42).\n");
        string top = Write("top.pl",
            ":- include('inner.pl').\ntop_fact(1).\n:- public go/1.\ngo(X) :- inner_fact(X).\n");
        var e = new PrologEngine();
        e.ConsultFile(top);
        Assert.True(e.Query("go(42).").Success);
    }

    [Fact]
    public void Include_OpsFromEarlierIncludeApplyToLaterSibling()
    {
        // The SWI loader-file pattern: first include defines ops, later
        // sibling INCLUDES use them. (Documented approximation: the
        // INCLUDING file itself is parsed before expansion, so only later
        // included files — not the loader's own text — see the new ops.)
        Write("ops.pl", ":- op(700, xfx, ===>).\n");
        Write("uses.pl",
            "rule(a ===> b).\n:- public q/2.\nq(X, Y) :- rule(X ===> Y).\n");
        string top = Write("loader.pl",
            ":- include('ops.pl').\n:- include('uses.pl').\n");
        var e = new PrologEngine();
        e.ConsultFile(top);
        Assert.True(e.Query("q(a, b).").Success);
    }

    [Fact]
    public void Include_Cycle_Throws()
    {
        string a = Write("a.pl", ":- include('b.pl').\n");
        Write("b.pl", ":- include('a.pl').\n");
        var e = new PrologEngine();
        Assert.ThrowsAny<Exception>(() => e.ConsultFile(a));
    }

    // ---------- initialization/1 ----------

    [Fact]
    public void Initialization_RunsAfterConsult_InSourceOrder()
    {
        string f = Write("init.pl",
            ":- dynamic seen/1.\n"
            + ":- initialization(assertz(seen(1))).\n"
            + ":- initialization(assertz(seen(2))).\n");
        var e = new PrologEngine();
        e.ConsultFile(f);
        Assert.True(e.Query("seen(1), seen(2).").Success);
    }

    [Fact]
    public void Initialization_ParenlessForm_Parses()
    {
        // `:- initialization main.` — the SWI fx 1150 operator form.
        string f = Write("initp.pl",
            ":- dynamic ran/0.\nmain :- assertz(ran).\n:- initialization main.\n");
        var e = new PrologEngine();
        e.ConsultFile(f);
        Assert.True(e.Query("ran.").Success);
    }

    [Fact]
    public void Initialization_FailingGoal_WarnsButLoadContinues()
    {
        string f = Write("initf.pl",
            ":- initialization(fail).\nok_fact(yes).\n:- public okq/0.\nokq :- ok_fact(yes).\n");
        var e = new PrologEngine();
        e.ConsultFile(f);   // must not throw
        Assert.True(e.Query("okq.").Success);
    }

    // ---------- DEC-10 comma-chained mode directive ----------

    [Fact]
    public void ModeDirective_CommaChain_Accepted()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- mode f(+, -),\n\tg(+),\n\th(?, -).\n"
            + "f(X, X).\ng(_).\nh(_, done).\n");
        Assert.True(e.Query("f(1, Y), Y == 1.").Success);
    }

    // ---------- getenv/2 ----------

    [Fact]
    public void GetEnv_SetVariable_UnifiesValue_UnsetFails()
    {
        Environment.SetEnvironmentVariable("SHUMWAY_P2C_TEST", "hello");
        try
        {
            var e = new PrologEngine();
            Assert.True(e.Query("getenv('SHUMWAY_P2C_TEST', V), V == hello.").Success);
            // Unset: FAILS (no error) so the (getenv ; default) idiom works.
            Assert.True(e.Query(
                "( getenv('SHUMWAY_P2C_NOPE_XYZ', V) -> true ; V = fallback ), V == fallback.").Success);
        }
        finally { Environment.SetEnvironmentVariable("SHUMWAY_P2C_TEST", null); }
    }

    // ---------- recorded-DB 2-arg sugar + display + append/2 ----------

    [Fact]
    public void RecordedDb_TwoArgForms()
    {
        var e = new PrologEngine();
        Assert.True(e.Query(
            "recordz(k, v1), recorda(k, v0), recorded(k, X), X == v0.").Success);
    }

    [Fact]
    public void Append2_ConcatenatesListOfLists()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("append([[1,2],[3],[4,5]], L), L == [1,2,3,4,5].").Success);
        Assert.True(e.Query("append([], L), L == [].").Success);
    }

    // ---------- user_error ----------

    [Fact]
    public void UserError_IsAWritableStreamAlias()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("write(user_error, ''), nl(user_error).").Success);
    }

    // ---------- Edinburgh see/tell resume ----------

    [Fact]
    public void See_AlreadyOpenFile_ResumesPosition()
    {
        string f = Slash(Write("data.pl", "one.\ntwo.\nthree.\n"));
        string g = Slash(Write("other.pl", "alpha.\n"));
        var e = new PrologEngine();
        // Read one term, switch to another file, come back: must RESUME at
        // term two, not restart (the PrologToC nested-include reader relies
        // on this — restarting re-read the outer file's clauses twice).
        var s = e.Query(
            $"see('{f}'), read(T1), see('{g}'), read(TA), seen, "
            + $"see('{f}'), read(T2), seen, "
            + "T1 == one, TA == alpha, T2 == two.");
        Assert.True(s.Success);
    }

    // ---------- read/1 correctness ----------

    [Fact]
    public void Read_UsesLiveOperatorTable()
    {
        char bs = (char)92;
        string f = Slash(Write("ops2.pl", "spec(?X = ?Y).\n"));
        var e = new PrologEngine();
        e.Query("op(200, fy, '?').");
        var s = e.Query($"see('{f}'), read(T), seen, T = spec(_).");
        Assert.True(s.Success);
    }

    [Fact]
    public void Read_UnivDotInsideSymbolAtom_NotClauseEnd()
    {
        // `=..` must not be split at its dots by the clause-end scanner.
        string f = Slash(Write("univ.pl", "m(?X =.. ?Y, misc).\n"));
        var e = new PrologEngine();
        e.Query("op(200, fy, '?').");
        Assert.True(e.Query($"see('{f}'), read(T), seen, T = m(_, misc).").Success);
    }

    [Fact]
    public void Read_DotInQuotedAtomAndComment_NotClauseEnd()
    {
        string f = Slash(Write("dots.pl",
            "a('x. y', \"s. t\"). % trailing. comment\nb(2).\n"));
        var e = new PrologEngine();
        var s = e.Query($"see('{f}'), read(T1), read(T2), seen, "
            + "T1 = a(_, _), T2 == b(2).");
        Assert.True(s.Success);
    }

    [Fact]
    public void Read_TrailingLayoutAtEof_YieldsEndOfFile()
    {
        string f = Slash(Write("tail.pl", "only_term(1).\n\n   % just a comment\n\n"));
        var e = new PrologEngine();
        var s = e.Query($"see('{f}'), read(T1), read(T2), seen, "
            + "T1 == only_term(1), T2 == end_of_file.");
        Assert.True(s.Success);
    }

    // ---------- open-open append/3 ----------

    [Fact]
    public void Append_OpenList_HoleClosingIdiom()
    {
        // The DEC-10 rdtok dictionary trick: close an open list's tail.
        var e = new PrologEngine();
        Assert.True(e.Query(
            "D = [a = _A, b = _B | _Hole], append(D, [], D), "
            + "D = [a = _, b = _].").Success);
    }
}

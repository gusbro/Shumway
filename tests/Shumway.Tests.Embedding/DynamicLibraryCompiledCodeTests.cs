using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-075, decisions 12 and 13: a library bundle linked with its compiled IL.
/// Its modules come in one at a time, each with its own code, and a call from
/// one module of the library into another is a dispatch by name: from the
/// compiled code of a module that is in, it finds the module that is not and
/// brings it in.
/// </summary>
public sealed class DynamicLibraryCompiledCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_dynlib_il_" + Guid.NewGuid().ToString("N"));

    public DynamicLibraryCompiledCodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ma and mb call each other; ma calls mq, which has module/2, by its
    // name. Each predicate that calls into another module does it in a
    // position that is not last, and again last.
    private static readonly (string Module, string Source)[] Modules =
    {
        ("ma", @"
            pa(1).
            pa(2).
            ca(X) :- pb(X).
            ca_sum(S) :- findall(X, pb(X), L), sum_list(L, S).
            ca_last(X, Y) :- pa(X), pb(Y).
            ca_q(X) :- mq:q(X), X > 0.
            "),
        ("mb", @"
            pb(10).
            pb(20).
            cb(X) :- pa(X).
            cb_twice(X, Y) :- pa(X), Y is X * 2.
            "),
        ("mq", @"
            :- module(mq, [q/1]).
            q(X) :- helper(X).
            helper(5).
            "),
    };

    /// <summary>Links the modules into <c>name.shum</c> as
    /// <c>shumway-link --library</c> does, with their compiled IL when
    /// <paramref name="withIl"/>.</summary>
    private void BuildLibrary(string name, bool withIl)
    {
        var objects = Modules.Select(m => ShmoCompiler.CompileSource(m.Source, m.Module, ShmoBuildMode.Release)).ToArray();
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
            IncludeCompiledIl = withIl,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, name + ".shum"),
            withIl ? link.Bytes! : BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
    }

    /// <summary>An engine whose bundle code is bound as each module comes in.</summary>
    private PrologEngine Engine()
    {
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.IlPromotion.PersistedThreshold = 0;
        e.AddLibraryDirectory(_dir);
        return e;
    }

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name).Id, arity);

    // Each succeeds with the right answer only.
    public static IEnumerable<object[]> Goals() => new[]
    {
        new object[] { "ca(X), X == 10" },
        new object[] { "ca_sum(S), S == 30" },
        new object[] { "findall(X-Y, ca_last(X, Y), L), L == [1-10, 1-20, 2-10, 2-20]" },
        new object[] { "ca_q(X), X == 5" },
        new object[] { "cb(X), X == 1" },
        new object[] { "findall(X-Y, cb_twice(X, Y), L), L == [1-2, 2-4]" },
    };

    // The first call goes into the compiled code of one module, which calls
    // another that has not come in.
    [Theory]
    [MemberData(nameof(Goals))]
    public void ACallFromCompiledCode_IntoAModuleThatIsNotIn_BringsItIn(string goal)
    {
        BuildLibrary("tiered", withIl: true);

        var separate = Engine();
        Assert.True(separate.Query("ensure_loaded(bundle(tiered)).").Success);
        Assert.True(separate.IsCodePending("ma") && separate.IsCodePending("mb") && separate.IsCodePending("mq"));
        Assert.True(separate.Query(goal + ".").Success, goal);
        Assert.True(separate.Query(goal + ".").Success, "the next query");

        // The same in the query that attaches the library.
        var one = Engine();
        Assert.True(one.Query($"ensure_loaded(bundle(tiered)), {goal}.").Success, "in the query that attaches");
    }

    // ANTI-VACUITY: each module that comes in runs its compiled code, the one
    // the first call went into and the one its compiled code called. A
    // predicate of a module without module/2 is bound under its module's
    // name.
    [Fact]
    public void AModuleThatComesIn_RunsItsCompiledCode()
    {
        BuildLibrary("tiered", withIl: true);
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(tiered)).").Success);
        Assert.False(e.IlPromotion.IsPromoted(Fid("ma$ca", 1)));
        Assert.True(e.Query("ca(X), X == 10.").Success);
        Assert.False(e.IsCodePending("ma"));
        Assert.False(e.IsCodePending("mb"));
        Assert.True(e.IsCodePending("mq"));
        Assert.True(e.IlPromotion.IsPromoted(Fid("ma$ca", 1)), "ca/1 did not run its compiled code");
        Assert.True(e.IlPromotion.IsPromoted(Fid("mb$pb", 1)), "pb/1 did not run its compiled code");
    }

    // The library without its IL gives the same answers: the oracle of the
    // cases above.
    [Theory]
    [MemberData(nameof(Goals))]
    public void TheSameLibraryAsBytecode_GivesTheSameAnswers(string goal)
    {
        BuildLibrary("plain", withIl: false);
        var e = Engine();
        Assert.True(e.Query("ensure_loaded(bundle(plain)).").Success);
        Assert.True(e.Query(goal + ".").Success, goal);
    }

    // A consulted program, on bytecode, calls into the compiled library.
    [Fact]
    public void AConsultedProgram_AndTheCompiledLibrary_CallEachOther()
    {
        BuildLibrary("tiered", withIl: true);
        var e = Engine();
        e.ConsultString(@"
            :- ensure_loaded(bundle(tiered)).
            main(X, S) :- ca(X), ca_sum(S).
            ");
        var r = e.Query("main(X, S).");
        Assert.True(r.Success);
        Assert.Equal("10", $"{r["X"]}");
        Assert.Equal("30", $"{r["S"]}");
    }
}

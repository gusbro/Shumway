using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-056, amendment: a call by its bare name to a predicate of a library
/// without a module directive is linked through the library's aliases. The
/// code that makes the call is compiled to IL as any other: the compiler finds
/// the callee through the same aliases the link uses, and calls it under the
/// callee's own functor, in its region when the callee is a member.
/// </summary>
public sealed class BareLibraryCalleeTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shumway_bare_callee_" + Guid.NewGuid().ToString("N"));

    public BareLibraryCalleeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static int Fid(string name, int arity)
        => FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    private void WriteLibrary(string library, string name = "steps")
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { ShmoCompiler.CompileSource(library, name, ShmoBuildMode.Release) },
            EntryPoints = Array.Empty<PredicateRef>(),
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
        File.WriteAllBytes(Path.Combine(_dir, name + ".shum"), BundleWriter.ToBytes(link.Bundle!, includeCompiledBytecode: true));
    }

    private PrologEngine Engine(string program, params string[] libraries)
    {
        var e = new PrologEngine { Out = new StringWriter(), Warnings = new StringWriter() };
        e.IlPromotion.Threshold = 2;
        e.AddLibraryDirectory(_dir);
        string loads = string.Concat((libraries.Length == 0 ? new[] { "steps" } : libraries)
            .Select(l => $":- use_module(library({l})).\n"));
        e.ConsultString(loads + program);
        return e;
    }

    // Runs the goal until the caller is compiled, then once more on its code.
    private static void Compile(PrologEngine e, string goal, string caller, int arity)
    {
        for (int i = 0; i < 4; i++) Assert.True(e.Query(goal).Success, goal);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotions did not settle");
        Assert.True(e.Query(goal).Success, goal);
        int fid = Fid(caller, arity);
        Assert.DoesNotContain(e.IlPromotion.UnpromotableEntries(), u => u.FunctorId == fid);
        Assert.Contains(fid, e.IlPromotion.PromotedFunctorIds());
    }

    private static CompiledPredicate Pred(int fid, params int[] callees)
    {
        var body = new byte[10];
        body[0] = (byte)Opcode.Proceed;
        var sites = callees.Select((c, i) => new CallSite(i, c, false)).ToList();
        return new CompiledPredicate(body, fid, 0, 1, sites, Array.Empty<int>());
    }

    // A leaf the compiler copies into its caller, and a rule it calls.
    [Theory]
    [InlineData("leaf", @"
step(X, Y) :- Y is X + 1.
")]
    [InlineData("rule", @"
step(X, Y) :- succ(X, Y), check(Y).
check(Y) :- Y > 0.
")]
    public void ACallerOfALibraryPredicate_IsCompiled(string tag, string library)
    {
        WriteLibrary(library);
        var e = Engine($@"
count_{tag}(N, N) :- !.
count_{tag}(I, N) :- step(I, J), count_{tag}(J, N).
");
        for (int i = 0; i < 4; i++) Assert.True(e.Query($"count_{tag}(0, 2000).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotions did not settle");
        Assert.True(e.Query($"count_{tag}(0, 2000).").Success);

        int caller = Fid($"user$count_{tag}", 2);
        Assert.DoesNotContain(e.IlPromotion.UnpromotableEntries(), u => u.FunctorId == caller);
        Assert.Contains(caller, e.IlPromotion.PromotedFunctorIds());
        Assert.True(e.Query($"count_{tag}(0, 3000).").Success, "the compiled caller answers");
    }

    // ADR-033: under guard continuations two calls of a guard by its bare
    // name share one copy of it, and the library's guard ends in a call to
    // another library's by the bare name.
    [Theory]
    [InlineData("shared")]
    [InlineData("cross")]
    public void AGuardCalledByItsBareName_IsCompiledUnderGuardContinuations(string shape)
    {
        if (shape == "shared")
            WriteLibrary(@"
cls(X, neg) :- X < 0, !.
cls(X, zero) :- X =:= 0, !.
cls(_, pos).
");
        else
        {
            WriteLibrary(@"
kind(X, neg) :- X < 0, !.
kind(X, zero) :- X =:= 0, !.
kind(_, pos).
", "kinds");
            WriteLibrary(@"
cls(X, big) :- X > 100, !.
cls(X, C) :- kind(X, C).
");
        }
        bool old = IlPredicateCompiler.CpFreeGuardContinuations;
        IlPredicateCompiler.CpFreeGuardContinuations = true;
        try
        {
            var e = Engine(@"
p(X, R) :- cls(X, C), !, R = a(C).
p(_, none).
q(X, R) :- cls(X, C), C == neg, !, R = b.
q(_, nb).
both(X, P, Q) :- p(X, P), q(X, Q).
", shape == "shared" ? new[] { "steps" } : new[] { "kinds", "steps" });
            Compile(e, "both(-5, a(neg), b), both(0, a(zero), nb), both(7, a(pos), nb).", "user$p", 2);
            Assert.Contains(Fid("user$q", 2), e.IlPromotion.PromotedFunctorIds());
            Assert.True(e.Query("both(-5, a(neg), b), both(0, a(zero), nb), both(7, a(pos), nb).").Success);
            if (shape == "cross") Assert.True(e.Query("both(500, a(big), nb).").Success);
            Assert.Single(e.QueryAll("p(-5, R)."));
        }
        finally
        {
            IlPredicateCompiler.CpFreeGuardContinuations = old;
        }
    }

    // The call site names fid 2, the map finds the predicate whose functor is
    // 3: the region holds it as 3, and the call by 2 is a branch to it.
    [Fact]
    public void ARegion_CallsAMemberByItsBareName_Inside()
    {
        var root = Pred(1, 2);
        var callee = Pred(3);
        var map = new Dictionary<int, CompiledPredicate> { [1] = root, [2] = callee, [3] = callee };
        var region = IlRegionBuilder.Build(root, map);
        Assert.Equal(new[] { 1, 3 }, region.Members.Select(m => m.FunctorId));
        Assert.Equal(3, region.CallTarget(2));
        Assert.True(region.IsIntraRegion(2));
        Assert.False(region.IsIntraRegion(4));
    }

    // A region owns a callee whose every caller it holds: a caller by the
    // bare name is one of them, and the predicate is counted once.
    [Fact]
    public void TheCallersOfAPredicate_IncludeTheCallsByItsBareName()
    {
        var callee = Pred(3, 7);
        var map = new Dictionary<int, CompiledPredicate>
        {
            [2] = callee, [5] = Pred(5, 7), [1] = Pred(1, 2), [3] = callee, [4] = Pred(4, 3), [7] = Pred(7),
        };
        var compiler = new IlPredicateCompiler();
        Assert.Equal(new[] { 1, 4 }, compiler.CallersOf(3, map)!.OrderBy(f => f));
        Assert.Null(compiler.CallersOf(2, map));
        Assert.Equal(new[] { 3, 5 }, compiler.CallersOf(7, map)!.OrderBy(f => f));
    }

    // Code that copied the predicate found by the bare name is stale when
    // either name gets new clauses.
    [Fact]
    public void ALookupByTheBareName_RecordsBothFunctors()
    {
        var callee = Pred(3);
        var recorder = new CalleeRecorder(new Dictionary<int, CompiledPredicate> { [2] = callee, [3] = callee });
        Assert.True(recorder.TryGetValue(2, out _));
        Assert.Equal(new[] { 2, 3 }, recorder.LookedUp.OrderBy(f => f));
    }

    // The compiled caller runs the library's predicate; once the program
    // defines the name itself, the caller runs the program's.
    [Fact]
    public void ACompiledCaller_FollowsTheNameWhenTheProgramDefinesIt()
    {
        WriteLibrary(@"
step(X, Y) :- Y is X + 1.
");
        var e = Engine(@"
last(I, N, I) :- I >= N, !.
last(I, N, L) :- step(I, J), last(J, N, L).
");
        for (int i = 0; i < 4; i++) Assert.True(e.Query("last(0, 2001, _).").Success);
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotions did not settle");
        Assert.Contains(Fid("user$last", 3), e.IlPromotion.PromotedFunctorIds());
        Assert.Equal("2001", e.Query("last(0, 2001, L).").Bindings["L"].ToString());

        e.ConsultString(@"
step(X, Y) :- Y is X + 2.
");
        Assert.Equal("2002", e.Query("last(0, 2001, L).").Bindings["L"].ToString());
    }
}

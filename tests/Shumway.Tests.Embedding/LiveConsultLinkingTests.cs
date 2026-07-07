using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 (Logtalk bring-up): a <c>consult/1</c> issued from INSIDE a live
/// query must make the consulted (static) predicates reachable in the SAME
/// query — Logtalk's <c>'$lgt_load_prolog_code'</c> loads each entity's
/// compiled scratch file mid-<c>'$lgt_runtime_initialization'</c> and then
/// meta-calls the freshly loaded predicates. The predicates are compiled with
/// the SAME static pipeline as a top-level consult (one code path) and
/// live-linked into the running query's code space.
/// </summary>
public class LiveConsultLinkingTests
{
    private static string WriteTemp(string text)
    {
        string path = System.IO.Path.GetTempFileName();
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    // A static predicate with a body disjunction is the canonical case that
    // broke under the old two-schemes divergence ($disj_N helper existence
    // error): MetaTransform lifts the (X=a;X=b) into a $disj auxiliary that
    // the live-link must compile and resolve just like setup does.
    [Fact]
    public void MidQueryConsult_StaticPredicateWithDisjunction_MetaCallReachesIt()
    {
        string path = WriteTemp("p(a).\np(X) :- (X = b ; X = c).\n");
        try
        {
            var engine = new PrologEngine();
            string p = path.Replace("\\", "/");
            // consult mid-query, then META-CALL p/1 (call/1 resolves the goal
            // through CurrentFunctorAddresses — the runtime dispatch path
            // Logtalk uses).
            var sols = engine.QueryAll($"consult('{p}'), findall(X, call(p(X)), Xs).");
            var list = new System.Collections.Generic.List<Solution>();
            foreach (var s in sols) { list.Add(s); break; }
            Assert.Single(list);
            var xs = list[0]["Xs"];
            // Xs = [a, b, c]
            Assert.Equal("[a,b,c]", RenderList(xs));
        }
        finally { System.IO.File.Delete(path); }
    }

    // Direct (non-meta) call to the mid-query-consulted predicate, sequenced
    // after the consult in the same query. The call goal is part of the query
    // body, compiled at setup — but it is itself a runtime dispatch through
    // the goal's functor, so the live-linked address resolves.
    [Fact]
    public void MidQueryConsult_ThenDirectCall_Succeeds()
    {
        string path = WriteTemp("greeting(hello).\ngreeting(world).\n");
        try
        {
            var engine = new PrologEngine();
            string p = path.Replace("\\", "/");
            var sol = engine.Query($"consult('{p}'), call(greeting(G)).");
            Assert.True(sol.Success);
            Assert.Equal(new AtomTerm("hello"), sol["G"]);
        }
        finally { System.IO.File.Delete(path); }
    }

    // Two files consulted in sequence in one query, where the second calls a
    // predicate the first defines: cross-batch resolution via bare aliases.
    [Fact]
    public void MidQueryConsult_TwoFiles_SecondCallsFirst()
    {
        string a = WriteTemp("base(1).\nbase(2).\n");
        string b = WriteTemp("derived(X) :- base(X).\n");
        try
        {
            var engine = new PrologEngine();
            string pa = a.Replace("\\", "/");
            string pb = b.Replace("\\", "/");
            var sol = engine.Query(
                $"consult('{pa}'), consult('{pb}'), findall(X, call(derived(X)), Xs).");
            Assert.True(sol.Success);
            Assert.Equal("[1,2]", RenderList(sol["Xs"]));
        }
        finally { System.IO.File.Delete(a); System.IO.File.Delete(b); }
    }

    private static string RenderList(Term? t)
    {
        var sb = new System.Text.StringBuilder("[");
        bool first = true;
        while (t is CompoundTerm c && c.Functor == "." && c.Args.Length == 2)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(RenderTerm(c.Args[0]));
            t = c.Args[1];
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string RenderTerm(Term? t) => t switch
    {
        AtomTerm a => a.Name,
        IntTerm i => i.Value.ToString(),
        _ => t?.ToString() ?? "?",
    };
}

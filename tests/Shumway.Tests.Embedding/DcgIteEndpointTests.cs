using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// DCG if-then-else state threading — the endpoint-merge bug: when the
/// CONDITION is a state-consuming nonterminal and the then part consumes
/// nothing (<c>( nt(X) -&gt; [] ; … )</c>), the then-branch's endpoint variable
/// occurs only inside the condition. The merge substitution used to rename the
/// then part alone — a silent no-op — leaving the if-then-else's shared
/// endpoint UNBOUND: every goal after it ran on a dangling, freshly-invented
/// state. Scryer clpz lost its whole propagator queue through exactly this
/// (its <c>( C &lt; 0 -&gt; [] ; [] )</c> bound-removal helpers), so fused
/// linear constraints stopped propagating after the first pass.
/// </summary>
public class DcgIteEndpointTests
{
    [Fact]
    public void NonterminalCondition_EmptyThen_ThreadsTheStateThrough()
    {
        // peek(X) CONSUMES one token; the then arm consumes nothing. After the
        // ITE the rest of the input must still be the real list.
        var e = new PrologEngine();
        e.ConsultString("""
            tok(X) --> [X].
            rule(R) --> ( tok(T) -> [] ; [] ), tok(R2), { R = seen(T, R2) }.
            t(R, Rest) :- phrase(rule(R), [a, b, c], Rest).
            """);
        var sol = e.Query("t(R, Rest).");
        Assert.True(sol.Success);
        Assert.Equal("seen(a, b)", sol["R"]!.ToString());
        Assert.Equal(".(c, [])", sol["Rest"]!.ToString());
    }

    [Fact]
    public void WrapperCondition_EmptyBranches_InRecursion_KeepsTheSharedState()
    {
        // The clpz shape, rephrased as an original counter grammar: a
        // comparison WRAPPER nonterminal as condition, both arms empty, inside
        // a recursive walk, with pushback state accessors around it. The
        // post-ITE writer must see the ORIGINAL state term, not a fresh one.
        var e = new PrologEngine();
        e.ConsultString("""
            lessthan(A, B) --> { A < B }.
            peek(S), [S] --> [S].
            open_gate --> peek(gate(_, Aux)), { put_attr(Aux, g, open) }.
            close_gate --> peek(gate(_, Aux)), { del_attr(Aux, g) }.
            walk([]) --> [].
            walk([N|Ns]) --> ( lessthan(N, 0) -> [] ; [] ), walk(Ns).
            scan(L) --> open_gate, ( peek(gate(id, _)) -> walk(L) ; [] ), close_gate.
            t(R) :- G = gate(id, Aux), phrase(scan([2, -1]), [G], Out),
                    (   get_attr(Aux, g, _) -> R = still_open ; R = closed(Out) ).
            """);
        var sol = e.Query("t(R).");
        Assert.True(sol.Success);
        // The bug: close_gate ran against a phantom fresh gate — the REAL
        // Aux kept its attribute and R came out still_open.
        Assert.Equal("closed", Assert.IsType<CompoundTerm>(sol["R"]).Functor);
    }

    [Fact]
    public void NonterminalCondition_TakenAndUntaken_BothKeepPosition()
    {
        var e = new PrologEngine();
        e.ConsultString("""
            even(N) --> { 0 =:= N mod 2 }.
            tag(N, T) --> ( even(N) -> { T = even } ; { T = odd } ).
            tags([], []) --> [].
            tags([N|Ns], [T|Ts]) --> tag(N, T), tags(Ns, Ts).
            t(Ts, Rest) :- phrase(tags([1,2,3], Ts), [keep], Rest).
            """);
        var sol = e.Query("t(Ts, Rest).");
        Assert.True(sol.Success);
        Assert.Equal(".(odd, .(even, .(odd, [])))", sol["Ts"]!.ToString());
        Assert.Equal(".(keep, [])", sol["Rest"]!.ToString());   // nothing consumed
    }
}

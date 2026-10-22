using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-047: a clause asserted from the heap keeps its packed text
/// packed. The clause reader gives a closed run (or a chain of segments of
/// one kind) as one string, which compiles to <c>get_pstr</c> with the
/// text in the pool, as a source literal does; expanded, every call would
/// build the text one cons cell per character.</summary>
public sealed class AssertedPackedTextTests
{
    private static int IdOf(LiteralPool<TextLiteral> pool, string text, TextKind kind)
    {
        for (int i = 0; i < pool.Count; i++)
            if (!pool.IsFree(i) && pool.Items[i].Text == text && pool.Items[i].Kind == kind) return i;
        return -1;
    }

    [Fact]
    public void APackedRunAssertedFromTheHeapStaysPacked()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(":- dynamic mifact/1.");
        var strings = engine._literalPools.Strings;
        // The query names no text: whatever the pool gains, the assert put there.
        Assert.True(engine.Query("atom_string(hello, S), assertz(mifact(S)).").Success);
        int id = IdOf(strings, "hello", TextKind.Chars);
        Assert.True(id >= 0, "the asserted clause did not keep its text packed");
        Assert.True(engine.Query("""mifact(X), X == "hello".""").Success);
        Assert.True(engine.Query("""mifact("hello").""").Success);
        Assert.True(engine.Query("mifact([h, e, l, l, o]).").Success);
        Assert.True(engine.Query("clause(mifact(X), true), atom_string(A, X), A == hello.").Success);
        // Retracted, the clause's text goes with its chunk.
        Assert.True(engine.Query("retract(mifact(_)), \\+ mifact(_).").Success);
    }

    [Fact]
    public void AChainOfSegmentsAssertsAsOneString()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(":- dynamic mifact/1.");
        var strings = engine._literalPools.Strings;
        Assert.True(engine.Query("atom_string(ab, A), atom_string(cd, B), string_concat(A, B, S), assertz(mifact(S)).").Success);
        Assert.True(IdOf(strings, "abcd", TextKind.Chars) >= 0, "the concatenation was not asserted as one packed string");
        Assert.True(engine.Query("""mifact(X), X == "abcd".""").Success);
    }

    [Fact]
    public void AListOfCharactersStaysAList()
    {
        var engine = new PrologEngine();
        engine.IlPromotion.Threshold = 0;
        engine.ConsultString(":- dynamic mifact/1.");
        var strings = engine._literalPools.Strings;
        int before = strings.Count;
        Assert.True(engine.Query("assertz(mifact([h, e, l, l, o])).").Success);
        Assert.Equal(before, strings.Count);
        Assert.True(engine.Query("""mifact(X), X == "hello".""").Success);
    }
}

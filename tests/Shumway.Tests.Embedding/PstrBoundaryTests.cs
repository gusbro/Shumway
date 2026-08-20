using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-047 decision 6: the representation is not observable at the .NET
/// boundary. A packed list and the cons list of the same content must reach C#
/// as the same thing, or the same method called down two Prolog paths gets two
/// different arguments and the storage has leaked into the contract.
/// </summary>
public class PstrBoundaryTests
{
    [Fact]
    public void APackedListAndAConsListReachCSharpAsTheSameThing()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = \"abc\", Y = [0'a, 0'b, 0'c], X == Y.");
        Assert.True(sol.Success);

        var a = engine.Query("X = \"abc\".");
        var b = engine.Query("X = [0'a, 0'b, 0'c].");
        Assert.True(a.Success && b.Success);
        Assert.Equal(b["X"], a["X"]);
    }

    [Fact]
    public void ATextListCrossesAsAList()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = \"ab\".");
        Assert.True(sol.Success);
        var c = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal(".", c.Functor);
        Assert.Equal(2, c.Args.Length);
        Assert.Equal(new IntTerm('a'), c.Args[0]);
    }

    [Fact]
    public void TryAsTextReadsAnAtomAndEitherListShape()
    {
        var engine = new PrologEngine();

        Assert.True(engine.Query("X = \"abc\".")["X"]!.TryAsText(out string packed));
        Assert.Equal("abc", packed);

        Assert.True(engine.Query("X = [0'a, 0'b, 0'c].")["X"]!.TryAsText(out string codes));
        Assert.Equal("abc", codes);

        Assert.True(engine.Query("X = [a, b, c].")["X"]!.TryAsText(out string chars));
        Assert.Equal("abc", chars);

        Assert.True(engine.Query("X = abc.")["X"]!.TryAsText(out string atom));
        Assert.Equal("abc", atom);

        // Not text: a mixed list, and a list of non-characters.
        Assert.False(engine.Query("X = [a, 98].")["X"]!.TryAsText(out _));
        Assert.False(engine.Query("X = [foo, bar].")["X"]!.TryAsText(out _));
        // A partial list is not text either.
        Assert.False(engine.Query("X = [a, b | _].")["X"]!.TryAsText(out _));
    }

    [Fact]
    public void AStringCrossesToPrologAsAnAtom()
    {
        var engine = new PrologEngine();
        Assert.IsType<AtomTerm>(engine.ToTerm("hello"));
        // …and comes back as the same C# string from either shape.
        Assert.Equal("hello", engine.Query("X = hello.").Get<string>("X"));
        Assert.Equal("hello", engine.Query("X = \"hello\".").Get<string>("X"));
        Assert.Equal("hello",
            engine.Query("X = [h,e,l,l,o].").Get<string>("X"));
    }
}

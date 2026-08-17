using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ---- [PrologTerm] types — the source generator emits the
// ToPrologTerm / FromPrologTerm methods at compile time. ----

[PrologTerm("c241_point")]
public partial record C241Point(int X, int Y);

[PrologTerm("c241_named")]
public partial record C241Named(string Name, int Age);

[PrologTerm("c241_nullary")]
public partial class C241Nullary { }

[PrologTerm("c241_holder")]
public partial record C241Holder(C241Point P, List<int> Xs);

[PrologTerm]  // no explicit functor -> uses the type name "C241Defaults" verbatim
public partial record C241Defaults(int A, int B);

[PrologTerm("c241_settable")]
public partial class C241Settable
{
    public int Count { get; set; }
    public string Tag { get; set; } = "";
}

/// <summary>
/// Chunk 241: <c>[PrologTerm]</c> attribute + Roslyn source
/// generator. Each test exercises a different shape of generated
/// converter so a regression in the generator (or in the runtime
/// convention dispatcher) fails the most likely affected case.
/// </summary>
public class Chunk241Tests
{
    [Fact]
    public void ToPrologTerm_Record_BuildsCompoundWithDeclaredFunctor()
    {
        var engine = new PrologEngine();
        var p = new C241Point(3, 4);
        var t = engine.ToTerm(p);
        var c = Assert.IsType<CompoundTerm>(t);
        Assert.Equal("c241_point", c.Functor);
        Assert.Equal(2, c.Args.Length);
        Assert.Equal(3L, ((IntTerm)c.Args[0]).Value);
        Assert.Equal(4L, ((IntTerm)c.Args[1]).Value);
    }

    [Fact]
    public void FromPrologTerm_Record_DecodesViaPositionalCtor()
    {
        var engine = new PrologEngine();
        var t = new CompoundTerm("c241_point", new Term[]
        {
            new IntTerm(10), new IntTerm(20),
        });
        var p = engine.FromTerm<C241Point>(t);
        Assert.Equal(new C241Point(10, 20), p);
    }

    [Fact]
    public void RoundTrip_RecordWithStringField()
    {
        var engine = new PrologEngine();
        var input = new C241Named("alice", 30);
        var back = engine.FromTerm<C241Named>(engine.ToTerm(input));
        Assert.Equal(input, back);
    }

    [Fact]
    public void NullaryType_RoundTripsViaAtom()
    {
        var engine = new PrologEngine();
        var input = new C241Nullary();
        var t = engine.ToTerm(input);
        Assert.Equal("c241_nullary", ((AtomTerm)t).Name);
        var back = engine.FromTerm<C241Nullary>(t);
        Assert.NotNull(back);
    }

    [Fact]
    public void NestedPrologTerm_RecurseViaEngine()
    {
        // Holder has a Point and a List<int>: tests the recursion
        // through the engine's normal converter dispatch (each
        // member goes through engine.ToTerm<T> / FromTerm<T>).
        var engine = new PrologEngine();
        var input = new C241Holder(new C241Point(1, 2), new List<int> { 10, 20, 30 });
        var t = engine.ToTerm(input);
        var back = engine.FromTerm<C241Holder>(t);
        // Record equality on List<int> is reference equality, so
        // compare members explicitly.
        Assert.Equal(input.P, back.P);
        Assert.Equal(input.Xs, back.Xs);
    }

    [Fact]
    public void DefaultFunctor_UsesTypeName()
    {
        var engine = new PrologEngine();
        var t = (CompoundTerm)engine.ToTerm(new C241Defaults(7, 8));
        Assert.Equal("C241Defaults", t.Functor);
    }

    [Fact]
    public void SettableClass_RoundTripsViaParameterlessCtor()
    {
        var engine = new PrologEngine();
        var input = new C241Settable { Count = 42, Tag = "answer" };
        var t = engine.ToTerm(input);
        var back = engine.FromTerm<C241Settable>(t);
        Assert.Equal(42, back.Count);
        Assert.Equal("answer", back.Tag);
    }

    [Fact]
    public void FromPrologTerm_WrongFunctor_Throws()
    {
        var engine = new PrologEngine();
        var bad = new CompoundTerm("not_point", new Term[]
        {
            new IntTerm(1), new IntTerm(2),
        });
        Assert.Throws<InvalidCastException>(() => engine.FromTerm<C241Point>(bad));
    }

    [Fact]
    public void FromPrologTerm_WrongArity_Throws()
    {
        var engine = new PrologEngine();
        var bad = new CompoundTerm("c241_point", new Term[] { new IntTerm(1) });
        Assert.Throws<InvalidCastException>(() => engine.FromTerm<C241Point>(bad));
    }

    [Fact]
    public void Query_GetTyped_ReturnsPrologTermInstance()
    {
        // Real query: define a Prolog clause emitting the compound
        // structure, then extract it as the C# type via Solution.Get.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public origin/1.
            origin(c241_point(0, 0)).
            """);
        var sol = engine.Query("origin(P).");
        Assert.True(sol.Success);
        var p = sol.Get<C241Point>("P");
        Assert.Equal(new C241Point(0, 0), p);
    }

    [Fact]
    public void Query_OfPrologTermStream()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public pt/1.
            pt(c241_point(1, 2)).
            pt(c241_point(3, 4)).
            """);
        var pts = engine.Query<C241Point>("pt(P).", "P").ToList();
        Assert.Equal(2, pts.Count);
        Assert.Equal(new C241Point(1, 2), pts[0]);
        Assert.Equal(new C241Point(3, 4), pts[1]);
    }
}

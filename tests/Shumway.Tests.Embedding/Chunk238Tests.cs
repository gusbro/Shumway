using System.Numerics;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 238: built-in term converters
/// (<see cref="PrologEngine.ToTerm{T}"/> /
/// <see cref="PrologEngine.FromTerm{T}"/>), custom converters via
/// <see cref="PrologEngine.RegisterConverter{T}"/>, and the typed
/// <see cref="Solution.Get{T}"/> / <see cref="Solution.TryGet{T}"/>
/// accessors. The foundation of the C# integration thread —
/// every later piece (Query&lt;T&gt;, [PrologTerm] source gen, typed
/// foreign-predicate signatures) builds on this dispatch.
/// </summary>
public class Chunk238Tests
{
    [Fact]
    public void ToTerm_BuiltInScalars_ProduceCorrectAstTypes()
    {
        var engine = new PrologEngine();
        Assert.IsType<IntTerm>(engine.ToTerm(42));
        Assert.IsType<IntTerm>(engine.ToTerm(42L));
        Assert.IsType<IntTerm>(engine.ToTerm((short)7));
        Assert.IsType<IntTerm>(engine.ToTerm((byte)9));
        Assert.IsType<FloatTerm>(engine.ToTerm(3.14));
        Assert.IsType<FloatTerm>(engine.ToTerm(3.14f));
        Assert.IsType<StringTerm>(engine.ToTerm("hello"));
        Assert.IsType<AtomTerm>(engine.ToTerm(true));
        Assert.IsType<AtomTerm>(engine.ToTerm('x'));
        Assert.IsType<BigIntTerm>(engine.ToTerm(BigInteger.Parse("99999999999999999999")));
    }

    [Fact]
    public void ToTerm_BoolMapsToTrueOrFalseAtom()
    {
        var engine = new PrologEngine();
        Assert.Equal("true", ((AtomTerm)engine.ToTerm(true)).Name);
        Assert.Equal("false", ((AtomTerm)engine.ToTerm(false)).Name);
    }

    [Fact]
    public void ToTerm_LongWithinIntRange_StaysInline()
    {
        var engine = new PrologEngine();
        var t = (IntTerm)engine.ToTerm(123456789L);
        Assert.Equal(123456789L, t.Value);
    }

    [Fact]
    public void ToTerm_BigInteger_PromotesWhenOutOfLongRange()
    {
        var engine = new PrologEngine();
        var huge = BigInteger.Parse("12345678901234567890123");
        var t = (BigIntTerm)engine.ToTerm(huge);
        Assert.Equal(huge, t.Value);
    }

    [Fact]
    public void ToTerm_TermPassthrough()
    {
        var engine = new PrologEngine();
        var input = new AtomTerm("custom");
        Assert.Same(input, engine.ToTerm<Term>(input));
        Assert.Same(input, engine.ToTerm<AtomTerm>(input));
    }

    [Fact]
    public void FromTerm_RoundTripsAllScalars()
    {
        var engine = new PrologEngine();
        Assert.Equal(42, engine.FromTerm<int>(engine.ToTerm(42)));
        Assert.Equal(42L, engine.FromTerm<long>(engine.ToTerm(42L)));
        Assert.Equal(3.14, engine.FromTerm<double>(engine.ToTerm(3.14)));
        Assert.Equal("hello", engine.FromTerm<string>(engine.ToTerm("hello")));
        Assert.True(engine.FromTerm<bool>(engine.ToTerm(true)));
        Assert.False(engine.FromTerm<bool>(engine.ToTerm(false)));
        Assert.Equal('x', engine.FromTerm<char>(engine.ToTerm('x')));
        var bi = BigInteger.Parse("99999999999999999999");
        Assert.Equal(bi, engine.FromTerm<BigInteger>(engine.ToTerm(bi)));
    }

    [Fact]
    public void FromTerm_AcceptsAtomForString()
    {
        // A term coming from a Prolog source is usually an AtomTerm,
        // not StringTerm. FromTerm<string> should accept either —
        // both are lossless.
        var engine = new PrologEngine();
        Assert.Equal("hola", engine.FromTerm<string>(new AtomTerm("hola")));
        Assert.Equal("hola", engine.FromTerm<string>(new StringTerm("hola")));
    }

    [Fact]
    public void FromTerm_IntOverflow_Throws()
    {
        var engine = new PrologEngine();
        Assert.Throws<OverflowException>(
            () => engine.FromTerm<int>(new IntTerm(long.MaxValue)));
    }

    [Fact]
    public void FromTerm_TypeMismatch_Throws()
    {
        var engine = new PrologEngine();
        Assert.Throws<InvalidCastException>(
            () => engine.FromTerm<bool>(new IntTerm(1))); // not true/false
        Assert.Throws<InvalidCastException>(
            () => engine.FromTerm<char>(new AtomTerm("xy")));
        Assert.Throws<InvalidCastException>(
            () => engine.FromTerm<int>(new FloatTerm(1.5)));
    }

    // ---- Solution.Get<T> / TryGet<T> ----

    [Fact]
    public void Solution_Get_TypedBindingExtraction()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public p/3.
            p(7, 3.5, atom_value).
            """);
        var sol = engine.Query("p(I, F, A).");
        Assert.True(sol.Success);
        Assert.Equal(7, sol.Get<int>("I"));
        Assert.Equal(3.5, sol.Get<double>("F"));
        Assert.Equal("atom_value", sol.Get<string>("A"));
    }

    [Fact]
    public void Solution_TryGet_MissingVariable_ReturnsFalse()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public p/1.
            p(42).
            """);
        var sol = engine.Query("p(X).");
        Assert.True(sol.TryGet<int>("X", out int x));
        Assert.Equal(42, x);
        Assert.False(sol.TryGet<int>("DoesNotExist", out int _));
    }

    [Fact]
    public void Solution_Get_MissingVariable_Throws()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public p/1.
            p(42).
            """);
        var sol = engine.Query("p(X).");
        Assert.Throws<KeyNotFoundException>(() => sol.Get<int>("DoesNotExist"));
    }

    // ---- Custom converters ----

    public record Money(decimal Amount, string Currency);

    [Fact]
    public void RegisterConverter_CustomType_RoundTrips()
    {
        var engine = new PrologEngine();
        engine.RegisterConverter<Money>(
            toTerm: (e, m) => new CompoundTerm("money", new Term[]
            {
                new StringTerm(m.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new AtomTerm(m.Currency),
            }),
            fromTerm: t =>
            {
                var c = (CompoundTerm)t;
                return new Money(
                    decimal.Parse(((StringTerm)c.Args[0]).Content,
                        System.Globalization.CultureInfo.InvariantCulture),
                    ((AtomTerm)c.Args[1]).Name);
            });

        var input = new Money(123.45m, "USD");
        var term = engine.ToTerm(input);
        var back = engine.FromTerm<Money>(term);
        Assert.Equal(input, back);
    }

    [Fact]
    public void RegisterConverter_OverridesBuiltin()
    {
        // Override the default string → StringTerm with atom semantics.
        var engine = new PrologEngine();
        engine.RegisterConverter<string>(
            toTerm: (e, s) => new AtomTerm(s),
            fromTerm: t => ((AtomTerm)t).Name);
        var term = engine.ToTerm("hola");
        Assert.IsType<AtomTerm>(term);
        Assert.Equal("hola", ((AtomTerm)term).Name);
    }

    [Fact]
    public void ToTerm_UnregisteredType_ThrowsWithHelpfulMessage()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.ToTerm(new System.Uri("http://example.com")));
        Assert.Contains("RegisterConverter", ex.Message);
    }

    // ---- Solution.Get<T> with a custom converter ----

    [Fact]
    public void Solution_Get_UsesCustomConverter()
    {
        var engine = new PrologEngine();
        engine.RegisterConverter<Money>(
            toTerm: (e, m) => throw new NotSupportedException("ToTerm not exercised here"),
            fromTerm: t =>
            {
                var c = (CompoundTerm)t;
                return new Money(
                    decimal.Parse(((IntTerm)c.Args[0]).Value
                        .ToString(System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.CultureInfo.InvariantCulture),
                    ((AtomTerm)c.Args[1]).Name);
            });

        engine.ConsultString("""
            :- public price/1.
            price(money(100, 'EUR')).
            """);
        var sol = engine.Query("price(M).");
        var m = sol.Get<Money>("M");
        Assert.Equal(new Money(100m, "EUR"), m);
    }
}

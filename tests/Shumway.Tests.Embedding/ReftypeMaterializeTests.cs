using Shumway.Core;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ADR-024 materializer tier — the managed Reftype snapshot. Materialize copies a
// term into a t_reftype-shaped tree; Dematerialize copies it back. These pin the
// ntype contract and the round-trip (the shared core both the native blittable
// P/Invoke backend and the managed-snapshot backend build on).
public class ReftypeMaterializeTests
{
    [Fact]
    public void Integer_RoundTrips()
    {
        var r = Reftype.Materialize(new IntTerm(42));
        Assert.Equal(Reftype.Codes.Integer, r.Ntype);
        Assert.Equal(42, r.Cint);
        Assert.Equal(new IntTerm(42), Reftype.Dematerialize(r));
    }

    [Fact]
    public void NegativeInteger_RoundTrips()
        => Assert.Equal(new IntTerm(-7),
            Reftype.Dematerialize(Reftype.Materialize(new IntTerm(-7))));

    [Fact]
    public void Float_RoundTrips()
    {
        var r = Reftype.Materialize(new FloatTerm(3.5));
        Assert.Equal(Reftype.Codes.Floating, r.Ntype);
        Assert.Equal(3.5, r.Cflt);
        Assert.Equal(new FloatTerm(3.5), Reftype.Dematerialize(r));
    }

    [Fact]
    public void Atom_RoundTrips()
    {
        var r = Reftype.Materialize(new AtomTerm("hello"));
        Assert.Equal(Reftype.Codes.Atom, r.Ntype);
        Assert.Equal("hello", r.Cstr);
        Assert.Equal(5, r.Nelem);
        Assert.Equal(new AtomTerm("hello"), Reftype.Dematerialize(r));
    }

    [Fact]
    public void String_MaterializesAsString_DematerializesToAtom()
    {
        // Arity "string" (ntype 4) and atom (ntype 3) are the same thing in Shumway:
        // a string materializes as ntype 4 but reads back as an atom.
        var r = Reftype.Materialize(new StringTerm("foo", TextKind.Codes));
        Assert.Equal(Reftype.Codes.String, r.Ntype);
        Assert.Equal("foo", r.Cstr);
        Assert.Equal(new AtomTerm("foo"), Reftype.Dematerialize(r));
    }

    [Fact]
    public void Variable_MaterializesAsUndef_DematerializesToFreshVar()
    {
        var r = Reftype.Materialize(new VarTerm("X"));
        Assert.Equal(Reftype.Codes.Undef, r.Ntype);
        Assert.IsType<VarTerm>(Reftype.Dematerialize(r));
    }

    [Fact]
    public void FlatFunctor_RoundTrips()
    {
        var t = new CompoundTerm("point", new Term[] { new IntTerm(3), new IntTerm(4) });
        var r = Reftype.Materialize(t);
        Assert.Equal(Reftype.Codes.Functor, r.Ntype);
        Assert.Equal("point", r.Cstr);       // functor name in cstr
        Assert.Equal(2, r.Nelem);            // arity in nelem
        Assert.NotNull(r.Pars);
        Assert.Equal(2, r.Pars!.Length);
        Assert.Equal(Reftype.Codes.Integer, r.Pars[0].Ntype);
        Assert.Equal(t, Reftype.Dematerialize(r));
    }

    [Fact]
    public void NestedFunctor_RoundTrips()
    {
        // f(g(1, a), 3.5, h(b)) — recursion over arguments of arguments.
        var t = new CompoundTerm("f", new Term[]
        {
            new CompoundTerm("g", new Term[] { new IntTerm(1), new AtomTerm("a") }),
            new FloatTerm(3.5),
            new CompoundTerm("h", new Term[] { new AtomTerm("b") }),
        });
        var r = Reftype.Materialize(t);
        Assert.Equal(Reftype.Codes.Functor, r.Ntype);
        Assert.Equal(Reftype.Codes.Functor, r.Pars![0].Ntype);   // nested g/2
        Assert.Equal("g", r.Pars[0].Cstr);
        Assert.Equal(2, r.Pars[0].Pars!.Length);
        Assert.Equal(t, Reftype.Dematerialize(r));               // full structural round-trip
    }

    [Fact]
    public void Atom_InFunctorArg_RoundTrips()
    {
        // An atom inside a compound is ntype 3 and reads back as the same atom.
        var t = new CompoundTerm("wrap", new Term[] { new AtomTerm("inner") });
        Assert.Equal(t, Reftype.Dematerialize(Reftype.Materialize(t)));
    }

    [Fact]
    public void CModifiesSnapshot_DematerializeReflectsIt()
    {
        // Simulates the P/Invoke case: native C builds a list into the struct, then
        // Dematerialize reads the C-built tree. Here we mutate the managed snapshot
        // directly (build cons(1, [])) and confirm Dematerialize yields the term.
        var nil = new Reftype { Ntype = Reftype.Codes.Atom, Cstr = "[]" };
        var one = new Reftype { Ntype = Reftype.Codes.Integer, Cint = 1 };
        var cons = new Reftype
        {
            Ntype = Reftype.Codes.Functor, Cstr = ".", Nelem = 2,
            Pars = new[] { one, nil },
        };
        var expected = new CompoundTerm(".", new Term[] { new IntTerm(1), new AtomTerm("[]") });
        Assert.Equal(expected, Reftype.Dematerialize(cons));
    }
}

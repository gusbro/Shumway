using System;
using System.Runtime.InteropServices;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

// ADR-024 materializer tier — the blittable native-memory t_reftype. Mirrors the
// managed Reftype core but over unmanaged memory (the form handed to a native C
// function via P/Invoke). Every test frees the graph it materializes.
public class NativeReftypeMarshalTests
{
    private static Term RoundTrip(Term t)
    {
        IntPtr p = NativeReftype.Materialize(t);
        try { return NativeReftype.Dematerialize(p); }
        finally { NativeReftype.Free(p); }
    }

    [Fact]
    public void Integer_RoundTrips() => Assert.Equal(new IntTerm(42), RoundTrip(new IntTerm(42)));

    [Fact]
    public void NegativeInteger_RoundTrips() => Assert.Equal(new IntTerm(-12345), RoundTrip(new IntTerm(-12345)));

    [Fact]
    public void Float_RoundTrips() => Assert.Equal(new FloatTerm(3.5), RoundTrip(new FloatTerm(3.5)));

    [Fact]
    public void Atom_RoundTrips() => Assert.Equal(new AtomTerm("hello"), RoundTrip(new AtomTerm("hello")));

    [Fact]
    public void String_RoundTripsToAtom() => Assert.Equal(new AtomTerm("foo"), RoundTrip(new StringTerm("foo")));

    [Fact]
    public void Var_RoundTripsToFreshVar() => Assert.IsType<VarTerm>(RoundTrip(new VarTerm("X")));

    [Fact]
    public void NestedFunctor_RoundTrips()
    {
        var t = new CompoundTerm("f", new Term[]
        {
            new CompoundTerm("g", new Term[] { new IntTerm(1), new AtomTerm("a") }),
            new FloatTerm(2.5),
            new CompoundTerm("h", new Term[] { new AtomTerm("b") }),
        });
        Assert.Equal(t, RoundTrip(t));
    }

    [Fact]
    public void StructLayout_MatchesArity()
    {
        // The blittable contract a native C function relies on: ntype at +0, nelem
        // at +8, pars at +16, crep at +24; a functor's name is in crep.cstr and its
        // args are t_reftype* in the pars array.
        IntPtr p = NativeReftype.Materialize(
            new CompoundTerm("pt", new Term[] { new IntTerm(7), new IntTerm(9) }));
        try
        {
            Assert.Equal(Reftype.Codes.Functor, Marshal.ReadInt64(p, 0));   // ntype
            Assert.Equal(2L, Marshal.ReadInt64(p, 8));                       // nelem = arity
            Assert.Equal("pt", Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(p, 24)));  // functor name (cstr)
            IntPtr pars = Marshal.ReadIntPtr(p, 16);
            IntPtr arg0 = Marshal.ReadIntPtr(pars, 0);
            Assert.Equal(Reftype.Codes.Integer, Marshal.ReadInt64(arg0, 0));
            Assert.Equal(7, Marshal.ReadInt32(arg0, 24));                    // cint
        }
        finally { NativeReftype.Free(p); }
    }

    [Fact]
    public void NativeModification_IsReflected()
    {
        // The P/Invoke case: native C writes r->crep.cint in place; Dematerialize
        // must read the modified value back.
        IntPtr p = NativeReftype.Materialize(new IntTerm(1));
        try
        {
            Marshal.WriteInt32(p, 24, 999);   // simulate: r->crep.cint = 999
            Assert.Equal(new IntTerm(999), NativeReftype.Dematerialize(p));
        }
        finally { NativeReftype.Free(p); }
    }

    [Fact]
    public void Free_OfDeepGraph_DoesNotThrow()
    {
        var deep = new CompoundTerm("a", new Term[]
        {
            new CompoundTerm("b", new Term[]
            {
                new CompoundTerm("c", new Term[] { new AtomTerm("x"), new IntTerm(3) }),
            }),
        });
        IntPtr p = NativeReftype.Materialize(deep);
        NativeReftype.Free(p);   // must walk + free the whole graph without faulting
        NativeReftype.Free(IntPtr.Zero);   // null is a no-op
    }
}

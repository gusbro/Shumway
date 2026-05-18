using Shumway.Embedding;

namespace Shumway.Tests.IsoConformance;

/// <summary>
/// ISO 13211-1, §8.3 Type testing built-in predicates. Covers
/// <c>var/1</c>, <c>nonvar/1</c>, <c>atom/1</c>, <c>integer/1</c>,
/// <c>float/1</c>, <c>number/1</c>, <c>atomic/1</c>, <c>compound/1</c>,
/// <c>is_list/1</c>, and <c>ground/1</c>.
/// </summary>
public class TypeTestsConformance
{
    private static bool Q(string src)
    {
        var engine = new PrologEngine();
        return engine.Query(src).Success;
    }

    [Fact]
    public void Var_OnUnbound_True() => Assert.True(Q("var(_)."));

    [Fact]
    public void Var_OnAtom_False() => Assert.False(Q("var(foo)."));

    [Fact]
    public void Nonvar_OnAtom_True() => Assert.True(Q("nonvar(foo)."));

    [Fact]
    public void Nonvar_OnUnbound_False() => Assert.False(Q("nonvar(_)."));

    [Fact]
    public void Atom_Cases()
    {
        Assert.True(Q("atom(foo)."));
        Assert.True(Q("atom([])."));
        Assert.False(Q("atom(42)."));
        Assert.False(Q("atom([a, b])."));
        Assert.False(Q("atom(_)."));
    }

    [Fact]
    public void Integer_Cases()
    {
        Assert.True(Q("integer(42)."));
        Assert.True(Q("integer(-7)."));
        Assert.False(Q("integer(3.14)."));
        Assert.False(Q("integer(foo)."));
    }

    [Fact]
    public void Float_Cases()
    {
        Assert.True(Q("float(3.14)."));
        Assert.False(Q("float(3)."));
    }

    [Fact]
    public void Number_AcceptsIntAndFloat()
    {
        Assert.True(Q("number(42)."));
        Assert.True(Q("number(3.14)."));
        Assert.False(Q("number(foo)."));
    }

    [Fact]
    public void Atomic_AcceptsAtomicTerms()
    {
        Assert.True(Q("atomic(foo)."));
        Assert.True(Q("atomic(42)."));
        Assert.True(Q("atomic(3.14)."));
        Assert.True(Q("atomic(\"str\")."));
        Assert.False(Q("atomic([a, b])."));
        Assert.False(Q("atomic(foo(x))."));
        Assert.False(Q("atomic(_)."));
    }

    [Fact]
    public void Compound_Cases()
    {
        Assert.True(Q("compound(foo(x))."));
        Assert.True(Q("compound([a, b])."));
        Assert.False(Q("compound(foo)."));
        Assert.False(Q("compound([])."));
        Assert.False(Q("compound(42)."));
    }

    [Fact]
    public void IsList_AcceptsProperListsOnly()
    {
        Assert.True(Q("is_list([])."));
        Assert.True(Q("is_list([1, 2, 3])."));
        Assert.False(Q("is_list([1 | _])."));
        Assert.False(Q("is_list([1 | 2])."));
        Assert.False(Q("is_list(foo)."));
    }

    [Fact]
    public void Ground_AcceptsFullyInstantiatedTerms()
    {
        Assert.True(Q("ground(foo)."));
        Assert.True(Q("ground(foo(a, b))."));
        Assert.True(Q("ground([1, 2, 3])."));
        Assert.False(Q("ground(_)."));
        Assert.False(Q("ground(foo(X))."));
        Assert.False(Q("ground([1 | X])."));
    }
}

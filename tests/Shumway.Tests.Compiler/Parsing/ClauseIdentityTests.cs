using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Xunit;

namespace Shumway.Tests.Compiler.Parsing;

/// <summary>A clause is equal only to itself: the dynamic store keys its maps
/// by clause, and two clauses with the same text are two clauses (asserting
/// a fact twice makes two of it, and retracting one leaves the other). The
/// clause's own hash only makes those maps fast; it must not make two clauses
/// one.</summary>
public class ClauseIdentityTests
{
    [Fact]
    public void TwoClausesWithTheSameText_AreTwoKeys()
    {
        var twins = new ClauseReader("f(1).\nf(1).\n").ReadAll().ToList();
        Assert.Equal(2, twins.Count);
        Assert.Equal(twins[0].Term.ToString(), twins[1].Term.ToString());

        var seen = new Dictionary<Clause, int> { [twins[0]] = 1, [twins[1]] = 2 };

        Assert.Equal(2, seen.Count);
        Assert.False(twins[0].Equals(twins[1]));
        Assert.True(twins[0].Equals(twins[0]));
        Assert.Equal(twins[0].GetHashCode(), twins[0].GetHashCode());
        Assert.NotEqual(twins[0].GetHashCode(), twins[1].GetHashCode());
    }
}

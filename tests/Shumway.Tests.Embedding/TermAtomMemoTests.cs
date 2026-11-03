using System;
using System.IO;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-003: a term of the syntax tree keeps the atom it resolved, and
/// interns its name again once that atom died, since a dead atom's id goes to
/// another name. Dead ids are poisoned here, so a stale id fails where it is
/// read.</summary>
[Collection("exclusive")]
[Trait("Concurrency", "exclusive")]
public sealed class TermAtomMemoTests
{
    private static void Run(Action body)
    {
        bool enabled = AtomTable.CollectionEnabled, poisoned = AtomTable.PoisonDeadIds;
        AtomTable.CollectionEnabled = true;
        AtomTable.PoisonDeadIds = true;
        try { body(); }
        finally
        {
            AtomTable.PoisonDeadIds = poisoned;
            AtomTable.CollectionEnabled = enabled;
        }
    }

    // A name no code interns: its atom is transient.
    private static string Fresh(string stem) => stem + "_" + Guid.NewGuid().ToString("N");

    [Fact]
    public void AnAtomTermInternsItsNameAgainOnceItsAtomDied()
    {
        string name = Fresh("memo");
        Run(() =>
        {
            var term = new AtomTerm(name);
            int first = term.ResolveAtomId();

            AtomTable.RequestCollection();

            Assert.Null(AtomTable.ProbeById(first));
            int again = term.ResolveAtomId();
            Assert.NotEqual(first, again);
            Assert.Equal(name, AtomTable.GetById(again)!.Name);
        });
    }

    [Fact]
    public void AnAtomTermSeededWithAnIdInternsItsNameAgainOnceItsAtomDied()
    {
        string name = Fresh("seeded");
        Run(() =>
        {
            int first = AtomTable.Intern(name).Id;
            var term = new AtomTerm(name, first);

            AtomTable.RequestCollection();

            int again = term.ResolveAtomId();
            Assert.NotEqual(first, again);
            Assert.Equal(name, AtomTable.GetById(again)!.Name);
        });
    }

    [Fact]
    public void ACompoundTermInternsItsFunctorAgainOnceItsNameDied()
    {
        string name = Fresh("memo_f");
        Run(() =>
        {
            var term = new CompoundTerm(name, new Term[] { new AtomTerm("a") });
            int first = term.ResolveFunctorId();

            AtomTable.RequestCollection();

            int again = term.ResolveFunctorId();
            Assert.NotEqual(first, again);
            var (atomId, arity) = FunctorTable.Lookup(again);
            Assert.Equal(name, AtomTable.GetById(atomId)!.Name);
            Assert.Equal(1, arity);
        });
    }

    [Fact]
    public void ACompoundRecordKeyIsReadBackAfterItsAtomDied()
    {
        string s = Guid.NewGuid().ToString("N");
        Run(() =>
        {
            var e = new PrologEngine { Out = new StringWriter() };
            Assert.True(e.Query($"atom_concat(ck_, '{s}', K), recordz(k(K), v, _).").Success);

            AtomTable.RequestCollection();

            Assert.True(e.Query($"keys(K), K = k(X), atom_concat(ck_, '{s}', X).").Success);
        });
    }
}

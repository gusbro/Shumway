using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A cut in a clause of a dynamic predicate prunes the call's other
/// clauses when the predicate is laid out indexed, as it does in the plain
/// chain of a cold one. The indexed layout reaches a clause's code from each
/// chain that holds it by a jump, which leaves the cut barrier where the call
/// set it.</summary>
public sealed class IndexedDynamicCutTests
{
    private static int TFid => FunctorTable.Intern(AtomTable.Intern("t", permanent: true).Id, 2);

    // The clause with the cut comes from the source, is put first by asserta,
    // or comes after the facts by assertz with one more clause after it.
    [Theory]
    [InlineData("source", "[c-3]")]
    [InlineData("asserta", "[c-3]")]
    [InlineData("assertz", "[z-0,y-1,c-3]")]
    public void ACutInAnIndexedDynamicPredicate_PrunesTheOtherClauses(string how, string expected)
    {
        const string cut = "t(A, B) :- !, A = c, B = 3";
        foreach (int threshold in new[] { 2, 1000 })
        {
            var e = new PrologEngine { Out = new StringWriter() };
            e.JitIndexing.Threshold = threshold;
            e.IlPromotion.Threshold = 0;
            e.ConsultString(":- dynamic(t/2).\n" + (how == "source" ? cut + ".\n" : "") + "t(z, 0).\nt(y, 1).\n");
            for (int i = 0; i < 3; i++) Assert.True(e.Query("t(y, _) ; true.").Success);
            Assert.True(e.Query("true.").Success);
            if (how == "asserta") Assert.True(e.Query($"asserta(({cut})).").Success);
            if (how == "assertz") Assert.True(e.Query($"assertz(({cut})), assertz(t(w, 9)).").Success);
            if (threshold == 2)
            {
                int at = e._persistentAddressesCache![TFid];
                Assert.Equal(Opcode.SwitchOnTerm, (Opcode)e._persistentProgram![at + 1]);
            }
            Assert.True(e.Query("findall(K-V, t(K, V), L), write(L), nl.").Success);
            Assert.Equal(expected, e.Out.ToString()!.Trim());
        }
    }
}

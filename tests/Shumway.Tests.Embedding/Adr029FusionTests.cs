using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-029 — the fused clause-epilogue opcodes (<c>cut_deallocate_proceed</c> /
/// <c>cut_proceed</c>) execute identically to <c>cut; deallocate_proceed</c> /
/// <c>cut; proceed</c> in every tier: the cut still commits (deterministic,
/// no extra solution on backtracking) and the clause still returns. The Tier-1
/// paths read the un-fused bytecode, so promotion is unaffected.
/// </summary>
public class Adr029FusionTests
{
    private const string Program =
        ":- public pick/2, classify/2.\n"
        // deep cut (a call before `!`) → cut_deallocate_proceed
        + "lookup(a,1).\nlookup(b,2).\nlookup(c,3).\n"
        + "pick(X,R):-lookup(X,R),!.\n"
        + "pick(_,none).\n"
        // last clause deep-cut commit
        + "classify(N,neg):-N<0,fail.\n"
        + "classify(N,pos):-check(N),!.\n"
        + "classify(_,other).\n"
        + "check(N):-N>=0.\n";

    public enum Mode { Tier0, Tier1Wam, Tier1StripWam }

    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static PrologEngine Activation(Mode mode)
    {
        if (mode == Mode.Tier0)
        {
            var e0 = new PrologEngine();
            e0.ConsultString(Program);
            return e0;
        }
        var bundle = new Bundle(new[] { new BundleEntry("adr029", Program) });
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: mode == Mode.Tier1Wam,
            includeCompiledIl: true,
            stripWam: mode == Mode.Tier1StripWam);
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.IlPromotion.IsPromoted(Fid("pick", 2)), "pick/2 must be Tier-1 IL");
        return e;
    }

    public static TheoryData<Mode> Modes => new() { Mode.Tier0, Mode.Tier1Wam, Mode.Tier1StripWam };

    [Theory]
    [MemberData(nameof(Modes))]
    public void DeepCutCommit_IsDeterministic(Mode mode)
    {
        var e = Activation(mode);
        // The cut commits: exactly one solution, the looked-up value.
        Assert.True(e.Query("pick(a, R), R == 1.").Success);
        Assert.Single(e.QueryAll("pick(a, R)."));
        Assert.Single(e.QueryAll("pick(b, R)."));
        // Miss → falls to the catch-all clause.
        Assert.True(e.Query("pick(z, R), R == none.").Success);
        Assert.Single(e.QueryAll("pick(z, R)."));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void LastClauseCutCommit_IsCorrect(Mode mode)
    {
        var e = Activation(mode);
        Assert.True(e.Query("classify(5, C), C == pos.").Success);
        Assert.Single(e.QueryAll("classify(5, C)."));      // cut commits to pos
        Assert.True(e.Query("classify(-1, C), C == other.").Success);
    }
}

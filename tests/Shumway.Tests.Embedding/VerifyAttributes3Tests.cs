using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The SICStus/Scryer per-module attribute-unification hook
/// <c>verify_attributes/3</c> — <c>verify_attributes(Var, Value, Goals)</c>, called
/// when an attributed Var is bound; it reads its own attributes (get_atts) and
/// returns Goals to run after the bind. Our engine bridges it over the same wakeup
/// machinery as the native <c>verify_attributes/4</c>, handing the hook a proxy
/// attributed variable that carries the snapshotted attribute. This is what lets a
/// Scryer attribute library (loaded verbatim) constrain unification.
/// </summary>
public class VerifyAttributes3Tests
{
    // A minimal Scryer-style attribute module: only(X, V) marks X so it may bind
    // only to V. The hook rejects any other bound value by returning [fail].
    private const string OnlyValLib =
        "only(X, V) :- put_atts(X, onlyval, val(V)).\n" +
        "verify_attributes(Var, Other, Goals) :-\n" +
        "  ( get_atts(Var, onlyval, val(V)) ->\n" +
        "     ( nonvar(Other), Other \\== V -> Goals = [fail] ; Goals = [] )\n" +
        "  ; Goals = [] ).";

    [Fact]
    public void Hook3_AllowsTheSanctionedBinding()
    {
        var e = new PrologEngine();
        e.ConsultString(OnlyValLib);
        Assert.True(e.Query("only(X, 5), X = 5.").Success);
    }

    [Fact]
    public void Hook3_RejectsADisallowedBinding()
    {
        var e = new PrologEngine();
        e.ConsultString(OnlyValLib);
        // X may bind only to 5; binding to 6 fires the hook, whose returned
        // [fail] makes the unification fail.
        Assert.False(e.Query("only(X, 5), X = 6.").Success);
    }

    [Fact]
    public void Hook3_UnconstrainedVariableStaysFlexible()
    {
        var e = new PrologEngine();
        e.ConsultString(OnlyValLib);
        // A plain variable (never marked) binds freely — the hook never runs.
        Assert.True(e.Query("plain(_) = plain(anything).").Success);
    }

    [Fact]
    public void Hook3_HookNeverFiresWithoutAttributes()
    {
        // A program that defines verify_attributes/3 but puts no attributes must
        // behave exactly like an ordinary program.
        var e = new PrologEngine();
        e.ConsultString(OnlyValLib + "\np(1).\np(2).");
        Assert.True(e.Query("p(1).").Success);
        Assert.True(e.Query("p(2).").Success);
        Assert.False(e.Query("p(3).").Success);
    }
}

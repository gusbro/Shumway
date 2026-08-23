using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Phase 33 (Logtalk bring-up): <c>read_term/3</c> honours the
/// <c>variable_names/1</c>, <c>singletons/1</c> and <c>variables/1</c> read
/// options (ISO §8.14.1). Binding these to proper lists — rather than leaving
/// them unbound — is a correctness requirement: a source loader that walks an
/// unbound singletons list with <c>member/2</c> loops forever (exactly what
/// Logtalk's linter does).
/// </summary>
public class ReadTermOptionsTests
{
    // Reads the single clause `text` from a temp file with the given read
    // options bound into the query, returning the first solution.
    private static Solution ReadWith(string text, string optionsGoal, string extract)
    {
        string path = System.IO.Path.GetTempFileName();
        System.IO.File.WriteAllText(path, text);
        try
        {
            var engine = new PrologEngine();
            string p = path.Replace("\\", "/");
            var sol = engine.Query(
                $"open('{p}', read, S), read_term(S, T, [{optionsGoal}]), close(S), {extract}.");
            Assert.True(sol.Success, "read_term/3 query failed");
            return sol;
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void VariableNames_ExcludesAnonymous_IncludesUnderscorePrefixed()
    {
        // foo(X, Y, X, _Z, _).  variable_names = [X=_, Y=_, _Z=_] (not the bare _).
        var sol = ReadWith("foo(X, Y, X, _Z, _).",
            "variable_names(VN)", "VN = [ 'X'=_, 'Y'=_, '_Z'=_ ]");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Singletons_OnceOccurringNamedVariables()
    {
        // X occurs twice (excluded); `_` is anonymous (excluded). `_Z` IS a
        // singleton — only the bare `_` is anonymous, and the "leading
        // underscore means deliberately unused" convention belongs to the
        // compiler's warning, not to read_term/2,3.
        var sol = ReadWith("foo(X, Y, X, _Z, _).",
            "singletons(Sg)", "Sg = [ 'Y'=_, '_Z'=_ ]");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Variables_AllDistinct_IncludingAnonymous_InOrder()
    {
        // foo(X, Y, X, _Z, _) has four distinct variables (X shared).
        var sol = ReadWith("foo(X, Y, X, _Z, _).",
            "variables(V)", "length(V, N)");
        Assert.Equal(new IntTerm(4), sol["N"]);
    }

    [Fact]
    public void VariableNames_PairVariableSharesTermVariable()
    {
        // The variable in the variable_names pair is the SAME variable as in the
        // read term: binding it through the pair binds the term's argument too.
        var sol = ReadWith("p(X).",
            "variable_names(['X'=V])", "V = 42, T == p(42)");
        Assert.True(sol.Success);
    }

    [Fact]
    public void Singletons_EmptyList_WhenNoSingletons()
    {
        // Every variable occurs at least twice → singletons is [].
        var sol = ReadWith("q(X, Y, X, Y).", "singletons(Sg)", "Sg == []");
        Assert.True(sol.Success);
    }
}

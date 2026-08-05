using Shumway.Compiler.Ast;
using Shumway.Embedding;

namespace Shumway.TopLevel;

/// <summary>
/// Wraps a user's goal so the top level can report residual constraints.
/// The goal is conjoined with <c>copy_term/3</c> over its named variables,
/// which hands back the attribute goals a constraint library projects — the
/// difference between answering <c>A in 6..9</c> and printing nothing useful
/// for an unbound-but-constrained variable.
/// </summary>
internal static class QueryWrapper
{
    // Hidden var names used to smuggle residual constraints out of the wrapped
    // query — long unique strings unlikely to collide with anything a user
    // types. Parsed-input vars must start with an uppercase letter or `_`;
    // these meet that rule.
    internal const string ResidualVarName = "_ReplResiduals_8a7b3c";
    internal const string CopiesVarName = "_ReplCopies_8a7b3c";

    /// <summary>Parses <paramref name="queryText"/> and returns the goal to run
    /// plus the user-visible variables, in source order. Returns false when the
    /// text does not parse — the caller then runs the raw text so the engine
    /// produces the diagnostic it always did.</summary>
    internal static bool TryWrap(
        PrologEngine engine, string queryText,
        out Term wrapped, out IReadOnlyList<string> userVars)
    {
        try
        {
            var (goal, vars) = engine.ParseGoal(queryText);
            userVars = vars;
            if (vars.Count == 0)
            {
                wrapped = goal;
                return true;
            }

            var varsList = SolutionFormatter.MakeList(
                vars.Select(n => (Term)new VarTerm(n)).ToArray());
            Term copyTerm = new CompoundTerm("copy_term", new Term[]
            {
                varsList,
                new VarTerm(CopiesVarName),
                new VarTerm(ResidualVarName),
            });
            wrapped = new CompoundTerm(",", new[] { goal, copyTerm });
            return true;
        }
        catch
        {
            wrapped = null!;
            userVars = Array.Empty<string>();
            return false;
        }
    }
}

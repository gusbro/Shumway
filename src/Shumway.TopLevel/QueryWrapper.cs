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
            Term copies = new VarTerm(CopiesVarName);
            Term residuals = new VarTerm(ResidualVarName);
            Term copyTerm = new CompoundTerm("copy_term", new Term[]
            {
                varsList, copies, residuals,
            });

            // copy_term/3 copies the WHOLE answer, on the heap, for every
            // solution -- 4.6 of the 7.8 seconds an answer of 4.5 million cells
            // took here, and far worse in a browser. It is here for residual
            // constraints, and with no attributed variable anywhere there are
            // none to find, which '$any_attvars' answers in O(1). The copies
            // are then the variables themselves, which is what the formatter's
            // name mapping wants either way.
            Term cheap = new CompoundTerm(",", new Term[]
            {
                new CompoundTerm("=", new[] { copies, varsList }),
                new CompoundTerm("=", new[] { residuals, new AtomTerm("[]") }),
            });
            Term residualStep = new CompoundTerm(";", new Term[]
            {
                new CompoundTerm("->", new Term[]
                {
                    new CompoundTerm("$any_attvars", Array.Empty<Term>()),
                    copyTerm,
                }),
                cheap,
            });
            wrapped = new CompoundTerm(",", new[] { goal, residualStep });
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

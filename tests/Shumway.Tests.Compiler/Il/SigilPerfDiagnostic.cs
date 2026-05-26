using System;
using System.Diagnostics;
using System.Text;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Compiler.Il;

/// <summary>
/// Diagnostic-only benchmark for the Sigil IL-emit slowdown on
/// large predicates. Linting Blint.pl with itself surfaces a
/// 200+ clause <c>parse_args/2</c> whose Sigil emit takes minutes;
/// the current workaround is the
/// <see cref="IlPromotionStore.MaxIlPromotionBytecodeBytes"/>
/// size cap. This test sweeps clause counts and prints the
/// compile time so we can confirm the scaling shape (super-
/// linear in clause count) and watch it improve as we replace
/// the validator.
/// </summary>
public class SigilPerfDiagnostic
{
    private readonly ITestOutputHelper _output;
    public SigilPerfDiagnostic(ITestOutputHelper output) { _output = output; }

    // Skip by default — purely diagnostic, runs slow on the
    // bigger sizes. Run via:
    //   dotnet test ... --filter "FullyQualifiedName~SigilPerfDiagnostic"
    //   in a build that comments out the Skip attribute.
    [Fact(Skip = "diagnostic — bring up locally to measure Sigil compile scaling")]
    public void Compile_TimePerClauseCount_PrintsToOutput()
    {
        // Predicate shape: N facts of arity 1, like `p(0). p(1). ... p(N-1).`
        // Compiled as switch-on-integer indexed multi-clause body.
        foreach (int n in new[] { 10, 20, 40, 80, 160, 320, 640 })
        {
            // Multi-clause atom-indexed (switch_on_atom) predicate —
            // p(a0). p(a1). ... — each clause is a single
            // get_atom_a1 + proceed, the shape the IL subset handles.
            var sb = new StringBuilder();
            for (int i = 0; i < n; i++) sb.Append("p(a").Append(i).Append(").\n");
            var clauses = new ClauseReader(sb.ToString()).ReadAll().ToList();
            var pred = new PredicateCompiler().Compile(clauses);
            int bcSize = pred.Bytecode.Length;
            var compiler = new IlPredicateCompiler();
            bool canCompile = compiler.CanCompile(pred, null);
            if (!canCompile)
            {
                _output.WriteLine($"n={n} bytecode={bcSize}B  -> CanCompile=false (skipped)");
                continue;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                _ = compiler.Compile(pred, null);
                sw.Stop();
                _output.WriteLine(
                    $"n={n} bytecode={bcSize}B  compile={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _output.WriteLine(
                    $"n={n} bytecode={bcSize}B  FAILED in {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Body-heavy shape: one clause whose body has many call/proceed
    // patterns. Mirrors what Blint's long bodies look like more closely
    // than the indexed-facts variant.
    [Fact(Skip = "diagnostic — bring up locally to measure Sigil compile scaling")]
    public void Compile_TimePerBodyLength_PrintsToOutput()
    {
        foreach (int n in new[] { 5, 10, 20, 40, 80, 160 })
        {
            // Single clause p/0 whose body calls q0, q1, ... qN.
            // q* don't need to exist for the predicate-compiler — they
            // resolve to unresolved CallTargets and the IL emitter
            // still emits real branch / call IL for each, which is
            // what stresses Sigil's ReturnTracer.
            var sb = new StringBuilder();
            sb.Append("p :- ");
            for (int i = 0; i < n; i++)
            {
                sb.Append("q").Append(i);
                if (i < n - 1) sb.Append(", ");
            }
            sb.Append(".\n");
            var clauses = new ClauseReader(sb.ToString()).ReadAll().ToList();
            // Only feed p/0 to the predicate compiler; ignore any
            // hypothetical q* clauses (there aren't any here, but the
            // filter is defensive).
            clauses = clauses.Where(c => {
                var head = c.Term is Shumway.Compiler.Ast.CompoundTerm cc && cc.Functor == ":-"
                    ? cc.Args[0]
                    : c.Term;
                string name = head switch {
                    Shumway.Compiler.Ast.AtomTerm a => a.Name,
                    Shumway.Compiler.Ast.CompoundTerm ct => ct.Functor,
                    _ => "?"
                };
                return name == "p";
            }).ToList();
            var pred = new PredicateCompiler().Compile(clauses);
            int bcSize = pred.Bytecode.Length;
            var compiler = new IlPredicateCompiler();
            bool canCompile = compiler.CanCompile(pred, null);
            if (!canCompile)
            {
                _output.WriteLine($"body n={n} bytecode={bcSize}B  -> CanCompile=false");
                continue;
            }
            var sw = Stopwatch.StartNew();
            try
            {
                _ = compiler.Compile(pred, null);
                sw.Stop();
                _output.WriteLine(
                    $"body n={n} bytecode={bcSize}B  compile={sw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _output.WriteLine(
                    $"body n={n} bytecode={bcSize}B  FAILED in {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}");
            }
        }
    }
}

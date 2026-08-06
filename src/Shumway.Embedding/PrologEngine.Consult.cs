using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>The consult pipeline (extracted component) — see
    /// <see cref="ConsultPipeline"/>. Lazy so construction needs no ctor
    /// edit; the engine forwards the surface below.</summary>
    private ConsultPipeline? _consult;
    private ConsultPipeline Consults => _consult ??= new ConsultPipeline(this);

    /// <summary>Consults a file by path — <c>.shum</c> loads a bundle,
    /// anything else consults source.</summary>
    public void ConsultFile(string path) => Consults.ConsultFile(path);

    internal void ConsultFileLive(string path, Shumway.Core.Activation liveEngine)
        => Consults.ConsultFileLive(path, liveEngine);

    /// <summary>Classical reconsult: abolishes the predicates the file
    /// defines, then consults it.</summary>
    public void ReconsultFile(string path) => Consults.ReconsultFile(path);

    /// <summary>Consults Prolog source text into this engine.</summary>
    public void ConsultString(string source) => Consults.ConsultString(source);

    /// <summary>Classical reconsult of source text: abolishes the predicates it
    /// defines, then consults it — so loading the same text twice defines it
    /// once. What an editor's "load this buffer" means.</summary>
    public void ReconsultString(string source) => Consults.ReconsultString(source);

    internal void ConsultStringInner(string source, bool recordInHistory,
        string? moduleNameFallback = null)
        => Consults.ConsultStringInner(source, recordInHistory, moduleNameFallback);

    internal void MarkModuleNonDebuggable(string moduleName)
        => Consults.MarkModuleNonDebuggable(moduleName);

    internal static List<Clause> TransformTabledPredicates(
        List<Clause> clauses, HashSet<int> tabled, HashSet<int> publics)
        => ConsultPipeline.TransformTabledPredicates(clauses, tabled, publics);

    internal static int HeadFunctorIdOf(Clause clause)
        => ConsultPipeline.HeadFunctorIdOf(clause);

    internal static bool TryReadClauseHead(Clause clause, out (string Name, int Arity) spec)
        => ConsultPipeline.TryReadClauseHead(clause, out spec);
}

using Shumway.Compiler.Ast;
using Shumway.Compiler.Modes;

namespace Shumway.Embedding;

/// <summary>
/// The accumulated state for one Prolog module loaded into a
/// <see cref="PrologEngine"/>. A module is identified by its name (set with
/// <c>:- module(name).</c>; defaults to the special <c>user</c> module when
/// the directive is absent) and owns the clauses consulted under that name
/// plus the set of functors the module has marked <c>:- public</c>.
///
/// <para>Visibility rules (per ADR-008):</para>
/// <list type="bullet">
/// <item>A functor that appears in <see cref="PublicFunctors"/> is reachable
///   under its bare name from any module — exactly one module may own each
///   public name.</item>
/// <item>Every other functor defined in the module is <em>local</em> — it
///   gets a synthetic <c>module$name</c> prefix during compilation so two
///   modules can independently use the same predicate name without
///   colliding. Local predicates are only callable from within the same
///   module.</item>
/// </list>
/// </summary>
public sealed class ModuleManifest
{
    public string Name { get; }
    public List<Clause> Clauses { get; }
    public HashSet<int> PublicFunctors { get; }

    /// <summary>ADR-038 — set when the module was declared with the two-arg
    /// <c>:- module(Name, [Exports])</c> directive. An export-qualified module
    /// contributes NOTHING to the bare-global namespace: every one of its
    /// predicates is mangled <c>Name$x</c> (so <see cref="PublicFunctors"/> stays
    /// empty and two such modules can export the same name), and only the
    /// functors in <see cref="ExportFunctors"/> are importable by other
    /// modules.</summary>
    public bool IsExportQualified { get; set; }

    /// <summary>ADR-038 — the importable surface of an export-qualified module:
    /// the bare <c>Name/Arity</c> functor ids listed in <c>:- module(Name,
    /// [Exports])</c> (∪ any <c>:- public</c> in that module). An importer of
    /// <c>p/N</c> resolves it to this module's mangled <c>Name$p/N</c>; importing
    /// a name absent here is an error. Empty for legacy bare-global modules.</summary>
    public HashSet<int> ExportFunctors { get; }

    /// <summary>ADR-038 — this module's import table: a bare functor id
    /// <c>p/N</c> this module imported → the NAME of the export-qualified module
    /// that provides it. A call to <c>p/N</c> that misses this module's own
    /// locals resolves through here to <c>Source$p/N</c> before falling back to
    /// the bare-global namespace. Built from <c>:- use_module/1,2</c>.</summary>
    public Dictionary<int, string> Imports { get; }

    /// <summary>Functors the module declares <c>:- dynamic</c>. These bypass
    /// local-functor mangling so the runtime <c>assertz</c> / <c>retract</c>
    /// store (a flat global table on the engine) can reach the same predicate
    /// from any module.</summary>
    public HashSet<int> DynamicFunctors { get; }

    /// <summary>Functors the module declares <c>:- discontiguous</c>. The
    /// metadata is stored verbatim; there is no warning yet about
    /// non-contiguous clauses, so this is a placeholder for tooling.</summary>
    public HashSet<int> DiscontiguousFunctors { get; }

    /// <summary>Functors the module declares <c>:- multifile</c>. Same
    /// status as <see cref="DiscontiguousFunctors"/> — accepted, not yet
    /// acted on.</summary>
    public HashSet<int> MultifileFunctors { get; }

    /// <summary>Per-functor mode declarations from <c>:- mode foo(+,-).</c>
    /// and <c>:- mode foo(+,-) is det.</c>. Keys are functor ids;
    /// values are the list of declarations for that functor — a
    /// predicate may declare several callable modes.
    /// Mode inference consumes these via <see cref="PrologEngine.Modes"/>;
    /// this is the foundation (parse + store + validate) and
    /// later chunks add the specialised code generation.</summary>
    public Dictionary<int, List<ModeDeclaration>> ModeDeclarations { get; }

    public ModuleManifest(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Clauses = new List<Clause>();
        PublicFunctors = new HashSet<int>();
        ExportFunctors = new HashSet<int>();
        Imports = new Dictionary<int, string>();
        DynamicFunctors = new HashSet<int>();
        DiscontiguousFunctors = new HashSet<int>();
        MultifileFunctors = new HashSet<int>();
        ModeDeclarations = new Dictionary<int, List<ModeDeclaration>>();
    }
}

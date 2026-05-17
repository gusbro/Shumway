using Shumway.Compiler.Ast;

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

    /// <summary>Functors the module declares <c>:- dynamic</c>. These bypass
    /// local-functor mangling so the runtime <c>assertz</c> / <c>retract</c>
    /// store (a flat global table on the engine) can reach the same predicate
    /// from any module.</summary>
    public HashSet<int> DynamicFunctors { get; }

    public ModuleManifest(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Clauses = new List<Clause>();
        PublicFunctors = new HashSet<int>();
        DynamicFunctors = new HashSet<int>();
    }
}

namespace Shumway.Embedding;

/// <summary>
/// Chunk 241 — marks a partial type as a Prolog-term schema so the
/// <c>Shumway.SourceGen.PrologTermGenerator</c> emits matching
/// <c>ToPrologTerm</c> / <c>FromPrologTerm</c> methods, and so the
/// <see cref="PrologEngine.ToTerm{T}"/> / <see cref="PrologEngine.FromTerm{T}"/>
/// runtime dispatcher discovers them via convention.
///
/// <example>
/// <code>
/// [PrologTerm("point")]
/// public partial record Point(int X, int Y);
///
/// // ↕ round-trips to / from the Prolog compound term
/// //     point(1, 2)
/// </code>
/// </example>
///
/// <para>The type's members are mapped to compound arguments in
/// declaration order. Records and primary-constructor types just
/// work — the generator picks up the positional ctor and uses it
/// for decoding. Plain classes / structs need a parameterless
/// constructor plus settable members; the generator initialises
/// them member-by-member.</para>
///
/// <para>Element types are converted recursively through the
/// engine's normal <see cref="PrologEngine.ToTerm{T}"/> /
/// <see cref="PrologEngine.FromTerm{T}"/> pipeline, so a member
/// typed as <c>List&lt;Point&gt;</c> works as long as <c>Point</c>
/// itself has a converter (built-in, user-registered, composite,
/// or another <c>[PrologTerm]</c> type).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct,
    Inherited = false, AllowMultiple = false)]
public sealed class PrologTermAttribute : Attribute
{
    /// <summary>The Prolog functor name. When <c>null</c> (the
    /// parameterless <c>[PrologTerm]</c> form), the C# type's name
    /// is used verbatim — case-sensitive, so a type named
    /// <c>Point</c> registers under functor <c>Point</c>, not
    /// <c>point</c>. Override when the desired Prolog atom differs
    /// from the C# convention.</summary>
    public string? Functor { get; }

    public PrologTermAttribute() { }
    public PrologTermAttribute(string functor) { Functor = functor; }
}

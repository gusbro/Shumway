namespace Shumway.Embedding;

/// <summary>
/// opts a single field or property <em>out</em> of the
/// <see cref="PrologTermAttribute"/> mapping. Useful when a
/// <c>[PrologTerm]</c> class carries .NET-side state that the
/// Prolog representation shouldn't include — auditing fields,
/// cached computations, references to non-Prolog objects.
///
/// <example>
/// <code>
/// [PrologTerm("user")]
/// public partial class User
/// {
///     public string Name { get; set; } = "";
///     public int Age { get; set; }
///
///     [PrologTermIgnore]
///     public DateTime LastSeen { get; set; }   // skipped — not in 'user(Name, Age)'
/// }
/// </code>
/// </example>
///
/// <para>The decoder side (FromPrologTerm) doesn't set ignored
/// members; for class types with a parameterless constructor that's
/// fine — they keep their default value. For record / primary-
/// constructor types, the ignored member must not appear in the
/// positional ctor (the generator can't synthesise a default for it
/// at decode time), otherwise generation fails with a clear error.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field,
    Inherited = false, AllowMultiple = false)]
public sealed class PrologTermIgnoreAttribute : Attribute
{
}

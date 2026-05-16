namespace Shumway.Builtins;

/// <summary>
/// One entry in the <see cref="BuiltinsRegistry"/>: the integer id baked into
/// <c>call_builtin</c> bytecode operands, the predicate name and arity (for
/// diagnostics), and the implementation function.
/// </summary>
public sealed class BuiltinEntry
{
    public int Id { get; }
    public string Name { get; }
    public int Arity { get; }
    public BuiltinImpl Impl { get; }

    public BuiltinEntry(int id, string name, int arity, BuiltinImpl impl)
    {
        Id = id;
        Name = name;
        Arity = arity;
        Impl = impl;
    }

    public override string ToString() => $"{Name}/{Arity} (#{Id})";
}

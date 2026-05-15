namespace Shumway.Core;

/// <summary>
/// A globally-interned Prolog atom. Identity is by <see cref="Id"/>; comparison is
/// integer equality. Constructed only by <see cref="AtomTable"/>.
/// </summary>
public sealed class Atom
{
    public int Id { get; }
    public string Name { get; }

    /// <summary>
    /// True for atoms that originate from source code, builtin names, or explicit
    /// promotion. Permanent atoms are never reclaimed by the atom GC.
    /// </summary>
    public bool IsPermanent { get; internal set; }

    internal Atom(int id, string name, bool isPermanent)
    {
        Id = id;
        Name = name;
        IsPermanent = isPermanent;
    }

    public override string ToString() => Name;
}

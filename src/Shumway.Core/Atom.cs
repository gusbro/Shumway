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

    /// <summary>BMP / astral / malformed — computed once here (interning is
    /// the single place atoms are born), so character-level builtins branch
    /// to a code-point slow path only for the rare astral-bearing atom and
    /// the common case keeps its exact unit-based O(1) code.</summary>
    public TextShape Shape { get; }

    /// <summary>Units ≡ characters: every existing unit-based operation is
    /// exact on this atom.</summary>
    public bool IsAllBmp => Shape == TextShape.Bmp;

    internal Atom(int id, string name, bool isPermanent)
    {
        Id = id;
        Name = name;
        IsPermanent = isPermanent;
        Shape = Utf16Text.Classify(name);
    }

    public override string ToString() => Name;
}

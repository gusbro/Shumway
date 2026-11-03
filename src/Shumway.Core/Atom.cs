namespace Shumway.Core;

/// <summary>
/// A globally-interned Prolog atom. Identity is by <see cref="Id"/>; comparison is
/// integer equality. Constructed only by <see cref="AtomTable"/>.
/// </summary>
public sealed class Atom
{
    public int Id { get; }
    public string Name { get; }

    /// <summary>True for the atoms the atom GC never reclaims (ADR-003): the
    /// pre-registered ones, the names the engine fixes and the atoms of a
    /// linked bundle's code.</summary>
    public bool IsPermanent { get; internal set; }

    /// <summary>BMP / astral / malformed — computed once here (interning is
    /// the single place atoms are born), so character-level builtins branch
    /// to a code-point slow path only for the rare astral-bearing atom and
    /// the common case keeps its exact unit-based O(1) code.</summary>
    public TextShape Shape { get; }

    /// <summary>Units ≡ characters: every existing unit-based operation is
    /// exact on this atom.</summary>
    public bool IsAllBmp => Shape == TextShape.Bmp;

    // ADR-003 claims, read and written by AtomTable only. Claims counts the
    // claimant lists holding the atom; Epoch is the global epoch of its last
    // intern or lost claim; State is 0 while alive, 1 once dead. NameRef is
    // its by-name entry, removed only while it still names this atom.
    internal int Claims;
    internal long Epoch;
    internal int State;
    internal WeakReference<Atom>? NameRef;

    internal Atom(int id, string name, bool isPermanent)
    {
        Id = id;
        Name = name;
        IsPermanent = isPermanent;
        Shape = Utf16Text.Classify(name);
    }

    public override string ToString() => Name;
}

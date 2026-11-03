namespace Shumway.Core;

/// <summary>
/// The atom ids one mark found (ADR-003): a bit per id below the table's bound,
/// read back as the ascending list of the transient ones, which a claimant then
/// holds. An id out of range or of no live atom is ignored: a conservatively
/// scanned slot can hold anything.
/// </summary>
public sealed class AtomMarkSet
{
    private ulong[] _bits = System.Array.Empty<ulong>();
    private int _words;
    private int[] _found = System.Array.Empty<int>();
    private int _foundCount;

    /// <summary>Empties the set for a mark over the ids the table has handed out.</summary>
    public void Reset()
    {
        int words = (AtomTable.IdBound >> 6) + 1;
        if (_bits.Length < words) _bits = new ulong[words + (words >> 2) + 16];
        else System.Array.Clear(_bits, 0, System.Math.Max(words, _words));
        _words = words;
        _foundCount = 0;
    }

    public void Add(int atomId)
    {
        int w = atomId >> 6;
        if ((uint)w < (uint)_words) _bits[w] |= 1UL << atomId;
    }

    /// <summary>Adds the name atom of a functor.</summary>
    public void AddFunctor(int functorId)
    {
        if (FunctorTable.TryLookup(functorId, out var entry)) Add(entry.AtomId);
    }

    /// <summary>Adds the atom a cell names: an atom, or a functor's name.</summary>
    public void AddCell(Cell c)
    {
        if (c.Tag == Tag.Atom) Add(c.AsAtomId);
        else if (c.Tag == Tag.Functor) AddFunctor(c.AsFunctorId);
    }

    /// <summary>The transient atoms among the ids added, ascending.</summary>
    public System.ReadOnlySpan<int> Transient()
    {
        _foundCount = AtomTable.TransientAmong(_bits, _words, ref _found);
        return new System.ReadOnlySpan<int>(_found, 0, _foundCount);
    }

    public bool Contains(int atomId)
    {
        int w = atomId >> 6;
        return (uint)w < (uint)_words && (_bits[w] & (1UL << atomId)) != 0;
    }
}

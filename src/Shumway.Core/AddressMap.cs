namespace Shumway.Core;

/// <summary>A map from a heap address to an int, for one walk over a term: open
/// addressing with linear probing over a power-of-two table, emptied by starting a
/// new generation instead of a pass over its slots, so a walk that keys three
/// cells pays for three. A key may be negative (a walk can key a cell by the
/// complement of its address).</summary>
public sealed class AddressMap
{
    private int[] _keys = new int[64];
    private int[] _values = new int[64];
    private int[] _generations = new int[64];
    private int _generation = 1;
    private int _count;
    private int _shift = 32 - 6;

    public int Count => _count;

    public void Clear()
    {
        _count = 0;
        if (++_generation == int.MaxValue)
        {
            System.Array.Clear(_generations, 0, _generations.Length);
            _generation = 1;
        }
    }

    public bool TryGetValue(int key, out int value)
    {
        int mask = _keys.Length - 1;
        for (int i = Slot(key); _generations[i] == _generation; i = (i + 1) & mask)
        {
            if (_keys[i] == key)
            {
                value = _values[i];
                return true;
            }
        }
        value = 0;
        return false;
    }

    /// <summary>Maps <paramref name="key"/>, which is not in the map yet.</summary>
    public void Add(int key, int value)
    {
        if (2 * (_count + 1) > _keys.Length) Grow();
        Insert(key, value);
        _count++;
    }

    private void Insert(int key, int value)
    {
        int mask = _keys.Length - 1;
        int i = Slot(key);
        while (_generations[i] == _generation) i = (i + 1) & mask;
        _keys[i] = key;
        _values[i] = value;
        _generations[i] = _generation;
    }

    // Fibonacci hashing: the top bits of the product, as many as the table has.
    private int Slot(int key) => (int)(unchecked((uint)key * 2654435769u) >> _shift);

    private void Grow()
    {
        int[] keys = _keys, values = _values, generations = _generations;
        int generation = _generation;
        _keys = new int[keys.Length * 2];
        _values = new int[keys.Length * 2];
        _generations = new int[keys.Length * 2];
        _shift--;
        _generation = 1;
        for (int i = 0; i < keys.Length; i++)
            if (generations[i] == generation) Insert(keys[i], values[i]);
    }
}

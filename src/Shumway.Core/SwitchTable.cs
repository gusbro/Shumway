namespace Shumway.Core;

/// <summary>
/// A read-only key → code-address lookup used by first-argument indexing
/// instructions (<c>switch_on_atom</c>, <c>switch_on_integer</c>,
/// <c>switch_on_structure</c>). The lookup key is whatever the corresponding
/// opcode reads from <c>A1</c>'s deref'd cell — an atom id, an integer
/// payload, or a functor id — and the matching value is the byte offset to
/// jump to. Unmatched keys yield <see cref="DefaultAddress"/>.
///
/// <para>For small tables (16 or fewer entries) we scan parallel arrays —
/// cache-friendly with no hash overhead. For larger tables we fall back to
/// a <see cref="Dictionary{TKey, TValue}"/>; the threshold is part of the
/// constructor's contract, not a runtime concern. Both representations are
/// immutable after construction; the linker produces shifted-address copies
/// instead of mutating in place.</para>
/// </summary>
public sealed class SwitchTable
{
    private const int LinearScanThreshold = 16;

    private readonly int[] _keys;
    private readonly int[] _values;
    private readonly Dictionary<int, int>? _dict;

    public int DefaultAddress { get; }
    public int Count => _keys.Length;
    public IReadOnlyList<int> Keys => _keys;
    public IReadOnlyList<int> Values => _values;

    public SwitchTable(int[] keys, int[] values, int defaultAddress)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);
        if (keys.Length != values.Length)
            throw new ArgumentException(
                $"keys ({keys.Length}) and values ({values.Length}) must have the same length.");

        _keys = keys;
        _values = values;
        DefaultAddress = defaultAddress;

        if (keys.Length > LinearScanThreshold)
        {
            _dict = new Dictionary<int, int>(keys.Length);
            for (int i = 0; i < keys.Length; i++) _dict[keys[i]] = values[i];
        }
    }

    public int Lookup(int key)
    {
        if (_dict is not null)
            return _dict.TryGetValue(key, out int v) ? v : DefaultAddress;

        for (int i = 0; i < _keys.Length; i++)
            if (_keys[i] == key) return _values[i];
        return DefaultAddress;
    }

    /// <summary>Returns a copy of this table with every value (including
    /// <see cref="DefaultAddress"/>) shifted by <paramref name="offset"/>.
    /// Used by the linker when relocating a predicate's predicate-local
    /// switch tables into a program-absolute layout.</summary>
    public SwitchTable WithShiftedAddresses(int offset)
    {
        var newValues = new int[_values.Length];
        for (int i = 0; i < _values.Length; i++) newValues[i] = _values[i] + offset;
        return new SwitchTable(_keys, newValues, DefaultAddress + offset);
    }

    /// <summary>Returns a new <see cref="SwitchTable"/>
    /// with an extra (key → value) entry appended. Used by the
    /// new-bucket-key assertz path on extensible-indexed dynamic
    /// predicates: an assertz of a clause whose arg-0 introduces a
    /// previously-unknown key adds that key to the predicate's atom
    /// / integer / structure switch table by replacing the table at
    /// its id with this larger one.</summary>
    public SwitchTable WithAdditionalEntry(int key, int value)
    {
        var newKeys = new int[_keys.Length + 1];
        var newValues = new int[_values.Length + 1];
        Array.Copy(_keys, newKeys, _keys.Length);
        Array.Copy(_values, newValues, _values.Length);
        newKeys[_keys.Length] = key;
        newValues[_values.Length] = value;
        return new SwitchTable(newKeys, newValues, DefaultAddress);
    }
}

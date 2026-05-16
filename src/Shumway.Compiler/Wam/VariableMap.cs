namespace Shumway.Compiler.Wam;

/// <summary>
/// Tracks the X-register slot assigned to each named variable while a clause is
/// being compiled. The first occurrence of a variable claims a slot (preferring
/// the head argument's own register when applicable); subsequent occurrences
/// look it up to emit <c>get_value_x</c> or <c>put_value_x</c> against the same
/// slot.
///
/// <para>Anonymous variables (<c>_</c>) are never registered here — each one is
/// a fresh, unconstrained binding, so the compiler emits no opcode for them in
/// head argument positions and treats them as freshly-allocated cells elsewhere.</para>
///
/// <para>Phase 1 keeps everything in X registers. The temporary-vs-permanent
/// distinction (Y registers, env frames) lands when body chunks introduce calls
/// that survive across multiple goals; until then, every name stays in X.</para>
/// </summary>
public sealed class VariableMap
{
    private readonly Dictionary<string, int> _slots = new();
    private int _nextFreeSlot;

    /// <summary>Creates a map for a clause whose head has the given arity. The
    /// argument registers <c>X[0..arity-1]</c> are reserved by the caller; the
    /// first fresh slot for a non-arg variable is <c>X[arity]</c>.</summary>
    public VariableMap(int arity) => _nextFreeSlot = arity;

    /// <summary>True if <paramref name="name"/> hasn't been bound to a slot yet.</summary>
    public bool IsNewName(string name) => !_slots.ContainsKey(name);

    /// <summary>Binds <paramref name="name"/> to the supplied slot — used when a
    /// variable's first occurrence is at a known register position (typically a
    /// head argument). Adjusts the next-free counter so this slot won't be
    /// re-handed-out.</summary>
    public void Bind(string name, int slot)
    {
        _slots[name] = slot;
        if (slot >= _nextFreeSlot) _nextFreeSlot = slot + 1;
    }

    /// <summary>Allocates the next free X slot to <paramref name="name"/> and
    /// returns it.</summary>
    public int AllocateFresh(string name)
    {
        int slot = _nextFreeSlot++;
        _slots[name] = slot;
        return slot;
    }

    /// <summary>Looks up the slot for a previously-bound variable.</summary>
    public int GetSlot(string name) => _slots[name];

    /// <summary>Number of X registers the clause needs in total (one past the
    /// highest used slot).</summary>
    public int RegisterCount => _nextFreeSlot;
}

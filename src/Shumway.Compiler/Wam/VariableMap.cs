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
/// <para>This map keeps everything in X registers; the temporary-vs-permanent
/// distinction (Y registers, env frames) is the clause compiler's job.</para>
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

    /// <summary>Allocates a fresh X slot without binding it to any name. Used as
    /// scratch space for nested-compound expansion: the worklist captures a temp
    /// slot via <c>unify_variable_x</c> in the parent compound, then expands the
    /// child compound by emitting <c>get_structure</c> against that slot.</summary>
    public int AllocateAnonymousSlot() => _nextFreeSlot++;

    /// <summary>Looks up the slot for a previously-bound variable.</summary>
    public int GetSlot(string name) => _slots[name];

    /// <summary>Number of X registers the clause needs in total (one past the
    /// highest used slot).</summary>
    public int RegisterCount => _nextFreeSlot;

    /// <summary>Returns the set of named variables currently in the map.</summary>
    public IEnumerable<string> Names => _slots.Keys;

    /// <summary>Reassigns <paramref name="name"/> to a different slot. Used by
    /// the head-var preservation pass at the head/body boundary: a head variable
    /// whose home register would be clobbered by an early body argument is
    /// moved (via <c>put_value_x</c>) to a slot beyond the body's maximum
    /// arity, and this method updates the map so subsequent body emission
    /// references the new slot.</summary>
    public void Rebind(string name, int slot) => _slots[name] = slot;

    /// <summary>Advances the next-free-slot counter so that it is at least
    /// <paramref name="minimum"/>. Used to ensure scratch / save slots land in
    /// the safe zone beyond the body's argument registers.</summary>
    public void EnsureFreeAtLeast(int minimum)
    {
        if (_nextFreeSlot < minimum) _nextFreeSlot = minimum;
    }
}

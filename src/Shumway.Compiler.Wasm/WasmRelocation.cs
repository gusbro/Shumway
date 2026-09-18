using System;
using System.Collections.Generic;
using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>What a relocated immediate stands for. Everything the compiled
/// code names outside itself is one of these; the values are process
/// state (interned ids, linked addresses, the world's module id), so a
/// module meant for another process carries the NAME and resolves it on
/// install.</summary>
public enum WasmRelocKind : byte
{
    /// <summary>A resume marker of a member: (member, offset) into its
    /// bytecode. Choice-point BPs and return continuations.</summary>
    Marker,
    /// <summary>A callee's dispatch marker, (callee, 0). The callee need
    /// not be a member.</summary>
    CallTarget,
    /// <summary>A biased bytecode address handed to the host: (member,
    /// offset).</summary>
    Address,
    /// <summary>A builtin request: (name, arity) plus the env trim.</summary>
    Builtin,
    /// <summary>An atom cell, by name.</summary>
    Atom,
    /// <summary>A functor cell, by (name, arity).</summary>
    Functor,
    /// <summary>The module's own id.</summary>
    ModuleId,
}

/// <summary>One relocation: what to resolve, and where in the module its
/// sentinel sits. A sentinel may be emitted at several sites; each site is
/// the first byte of a padded LEB (5 bytes for an i32-sized value, 10 for a
/// cell) right after its <c>i32.const</c>/<c>i64.const</c> opcode.
/// </summary>
public sealed record WasmRelocation(
    WasmRelocKind Kind,
    /// <summary>The functor/atom/builtin name; empty for ModuleId.</summary>
    string Name,
    int Arity,
    /// <summary>Marker/Address: the offset into the member's bytecode.
    /// Builtin: the env-trim size. Otherwise 0.</summary>
    int Offset,
    /// <summary>True for a cell (10-byte site), false for an int (5).</summary>
    bool Wide)
{
    public int[] Sites { get; set; } = Array.Empty<int>();
}

/// <summary>The builtin-form decisions the compiled code depends on, by
/// callee: re-checked on install, because a module compiled against one
/// decision (say, an inlined <c>=/2</c>) is wrong under another.</summary>
public sealed record WasmBuiltinEvidence(
    string Name, int Arity, bool Found, bool Direct, bool InlineUnify,
    bool InlineCompare, bool Negated, WasmTypeTest TypeTest = WasmTypeTest.None,
    bool InlineGetAttr = false, bool InlineMetaCall = false,
    bool InlineAppend = false, bool InlineBarrierCall = false);

/// <summary>A compile env that bakes a unique SENTINEL for every immediate
/// the code names outside itself and records what each one stands for, so
/// the bytes can be relocated into any process (<see
/// cref="WasmRelocatableModule"/>). Form decisions (builtin, inline) come
/// from the wrapped live env and are recorded as evidence.
///
/// <para>The sentinels are chosen so their LEB encodings have a fixed width
/// that any resolved value also fits: an int sentinel is
/// <c>0x7E00_0000 + slot</c> (5 bytes, bit 30 set), a cell sentinel has bit
/// 63 set and bit 62 clear (10 bytes). Neither can be a genuine immediate:
/// no tagged cell carries tag nibble 0x8 (Foreign never bakes), and no
/// marker or address reaches 2^30.</para></summary>
public sealed class RelocatingCompileEnv : IWasmCompileEnv
{
    public const int IntSentinelBase = 0x7E00_0000;
    public const long CellSentinelBase = unchecked((long)0x8E00_0000_0000_0000UL);
    public const int SlotMask = 0x00FF_FFFF;

    private readonly IWasmCompileEnv _inner;
    private readonly List<(string Name, int Arity, int Bias, int Length)> _members = new();
    private readonly Dictionary<int, int> _memberByFid = new();
    private readonly Dictionary<(WasmRelocKind, string, int, int), int> _slotByKey = new();
    private readonly List<WasmRelocation> _relocations = new();
    private readonly Dictionary<int, WasmBuiltinEvidence> _evidence = new();

    public RelocatingCompileEnv(IReadOnlyList<WasmGroupMember> members, IWasmCompileEnv inner)
    {
        _inner = inner;
        foreach (var m in members)
        {
            var (name, arity) = NameOf(m.Predicate.FunctorId);
            _memberByFid[m.Predicate.FunctorId] = _members.Count;
            _members.Add((name, arity, m.Bias, m.Predicate.Bytecode.Length));
        }
    }

    public IReadOnlyList<WasmRelocation> Relocations => _relocations;
    public IReadOnlyCollection<WasmBuiltinEvidence> Evidence => _evidence.Values;

    internal static (string Name, int Arity) NameOf(int functorId)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        return (AtomTable.GetById(atomId)!.Name, arity);
    }

    private int Slot(WasmRelocKind kind, string name, int arity, int offset, bool wide)
    {
        var key = (kind, name, arity, offset);
        if (!_slotByKey.TryGetValue(key, out int slot))
        {
            slot = _relocations.Count;
            if (slot > SlotMask)
                throw new WasmCompileException("too many relocations for one module");
            _slotByKey[key] = slot;
            _relocations.Add(new WasmRelocation(kind, name, arity, offset, wide));
        }
        return slot;
    }

    private int IntSentinel(WasmRelocKind kind, string name, int arity, int offset)
        => IntSentinelBase + Slot(kind, name, arity, offset, wide: false);

    private long CellSentinel(WasmRelocKind kind, string name, int arity)
        => CellSentinelBase | (uint)Slot(kind, name, arity, 0, wide: true);

    // A member-relative site: the member is found by the fid, or, for a
    // bare address, by the range its bias spans. The end of the bytecode
    // is a legal address (the cursor after a trailing call).
    private (string Name, int Arity, int Offset) MemberSite(int functorId, int address)
    {
        if (!_memberByFid.TryGetValue(functorId, out int i))
            throw new WasmCompileException($"relocation names a non-member functor {functorId}");
        var m = _members[i];
        return (m.Name, m.Arity, address - m.Bias);
    }

    // Same ownership rule as WasmRelocatableModule.OwnerOf: an address at
    // a bias belongs to that member, never to its predecessor's end.
    private (string Name, int Arity, int Offset) AddressSite(int address)
    {
        foreach (var m in _members)
            if (address == m.Bias) return (m.Name, m.Arity, 0);
        foreach (var m in _members)
            if (address > m.Bias && address <= m.Bias + m.Length)
                return (m.Name, m.Arity, address - m.Bias);
        throw new WasmCompileException($"address {address} lies in no member");
    }

    public int EncodeBp(int functorId, int address)
    {
        var (n, a, off) = MemberSite(functorId, address);
        return IntSentinel(WasmRelocKind.Marker, n, a, off);
    }

    public int EncodeReturnMarker(int functorId, int address)
    {
        var (n, a, off) = MemberSite(functorId, address);
        return IntSentinel(WasmRelocKind.Marker, n, a, off);
    }

    public int EncodeCallTarget(int calleeFunctorId)
    {
        var (n, a) = NameOf(calleeFunctorId);
        return IntSentinel(WasmRelocKind.CallTarget, n, a, 0);
    }

    public int EncodeAddress(int address)
    {
        var (n, a, off) = AddressSite(address);
        return IntSentinel(WasmRelocKind.Address, n, a, off);
    }

    public long EncodeBuiltinId(int builtinId, int envTrim)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        return CellSentinelBase
            | (uint)Slot(WasmRelocKind.Builtin, entry.Name, entry.Arity, envTrim, wide: true);
    }

    public long AtomCell(int atomId)
        => CellSentinel(WasmRelocKind.Atom, AtomTable.GetById(atomId)!.Name, 0);

    public long FunctorCell(int functorId)
    {
        var (n, a) = NameOf(functorId);
        return CellSentinel(WasmRelocKind.Functor, n, a);
    }

    public int EncodeModuleId(int moduleId) => IntSentinel(WasmRelocKind.ModuleId, "", 0, 0);

    public bool TryGetBuiltin(int calleeFunctorId, out int builtinId)
    {
        bool found = _inner.TryGetBuiltin(calleeFunctorId, out builtinId);
        if (!_evidence.ContainsKey(calleeFunctorId))
        {
            var (n, a) = NameOf(calleeFunctorId);
            bool direct = false, unify = false, compare = false, negated = false;
            bool getAttr = false, metaCall = false, appnd = false, barrierCall = false;
            var test = WasmTypeTest.None;
            if (found)
            {
                direct = _inner.IsDirectBuiltin(builtinId);
                unify = _inner.IsInlineUnify(builtinId);
                compare = _inner.IsInlineCompare(builtinId, out negated);
                _inner.TryGetInlineTypeTest(builtinId, out test);
                getAttr = _inner.IsInlineGetAttr(builtinId);
                metaCall = _inner.IsInlineMetaCall(builtinId);
                appnd = _inner.IsInlineAppend(builtinId);
                barrierCall = _inner.IsInlineBarrierCall(builtinId);
            }
            _evidence[calleeFunctorId] =
                new WasmBuiltinEvidence(n, a, found, direct, unify, compare, negated,
                                        test, getAttr, metaCall, appnd, barrierCall);
        }
        return found;
    }

    // The bytecode's CallBuiltin/ExecuteBuiltin sites ask by id without a
    // TryGetBuiltin first: record their evidence under the builtin's own
    // functor so the install re-checks those decisions too.
    private void Note(int builtinId)
    {
        var entry = Shumway.Builtins.BuiltinsRegistry.GetById(builtinId);
        int fid = FunctorTable.Intern(AtomTable.Intern(entry.Name).Id, entry.Arity);
        if (_evidence.ContainsKey(fid)) return;
        _inner.TryGetInlineTypeTest(builtinId, out var noteTest);
        _evidence[fid] = new WasmBuiltinEvidence(entry.Name, entry.Arity, true,
            _inner.IsDirectBuiltin(builtinId), _inner.IsInlineUnify(builtinId),
            _inner.IsInlineCompare(builtinId, out bool neg), neg, noteTest,
            _inner.IsInlineGetAttr(builtinId), _inner.IsInlineMetaCall(builtinId),
            _inner.IsInlineAppend(builtinId), _inner.IsInlineBarrierCall(builtinId));
    }

    public bool IsDirectBuiltin(int builtinId)
    {
        Note(builtinId);
        return _inner.IsDirectBuiltin(builtinId);
    }

    public bool IsInlineUnify(int builtinId)
    {
        Note(builtinId);
        return _inner.IsInlineUnify(builtinId);
    }

    public bool IsInlineCompare(int builtinId, out bool negated)
    {
        Note(builtinId);
        return _inner.IsInlineCompare(builtinId, out negated);
    }

    public bool IsInlineGetAttr(int builtinId)
    {
        Note(builtinId);
        return _inner.IsInlineGetAttr(builtinId);
    }

    public bool IsInlineDomSame(int builtinId)
    {
        Note(builtinId);
        return _inner.IsInlineDomSame(builtinId);
    }

    public bool IsInlineMetaCall(int builtinId)
    {
        Note(builtinId);
        return _inner.IsInlineMetaCall(builtinId);
    }

    public bool IsInlineAppend(int builtinId)
    {
        Note(builtinId);
        return _inner.IsInlineAppend(builtinId);
    }

    public bool IsInlineBarrierCall(int builtinId)
    {
        Note(builtinId);
        return _inner.IsInlineBarrierCall(builtinId);
    }

    // Handed through as the LIVE id, and only ever used as the input to
    // FunctorCell, which relocates by (name, arity). Baking the id itself
    // would be a cross-process bug: functor ids are handed out in intern
    // ORDER, so the same predicate is a different id in another process.
    public int MqualFunctorId => _inner.MqualFunctorId;

    public IReadOnlyList<(int FunctorId, int BuiltinId)> MetaCallableBuiltins
        => _inner.MetaCallableBuiltins;

    // Every form decision has to be DELEGATED here, not inherited: the
    // interface's default answers "no", so a hook added upstream and not
    // added here silently stops applying to every baked module while the
    // live path keeps it. That is how the type tests came to be open-coded
    // on the desktop and not in the browser, where the libraries run from
    // baked modules -- var/1 and number/1 still topped the browser's exit
    // ranking after the change landed.
    public bool TryGetInlineTypeTest(int builtinId, out WasmTypeTest test)
    {
        Note(builtinId);
        return _inner.TryGetInlineTypeTest(builtinId, out test);
    }
}

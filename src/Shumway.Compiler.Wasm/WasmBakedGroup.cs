using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Compiler.Wasm;

/// <summary>A group module compiled AHEAD of time (the web build bakes the
/// prelude's), together with everything needed to prove at load time that
/// the bytes are valid for THIS process. The module bakes process-local
/// values — interned resume markers, linked addresses, builtin ids, id-bearing
/// bytecode operands — so the bytes are only reusable when the loading process
/// reproduces the baking process's intern history exactly. That holds for a
/// boot that loads the same stdlib bundle through the same code; rather than
/// assume it, <see cref="Validate"/> replays the recorded evidence against
/// the live process and any mismatch rejects the bake (the caller falls back
/// to compiling, which is always correct).</summary>
public sealed class WasmBakedGroup
{
    public sealed record Member(
        int FunctorId, int Arity, string Name, int Bias, int EntryCursor,
        ulong CodeHash, double[]? FloatPool);

    public required byte[] Module { get; init; }
    public required int RegisterDemand { get; init; }
    public required IReadOnlyList<Member> Members { get; init; }
    /// <summary>Biased bytecode address to cursor, every re-entry point.</summary>
    public required IReadOnlyList<KeyValuePair<int, int>> CursorByAddress { get; init; }
    /// <summary>Every (functorId, address) → marker the compiler baked, in
    /// FIRST-INTERN order: replaying them in order against a marker pool in
    /// the bake-time state reproduces (and thereby verifies) the values.</summary>
    public required IReadOnlyList<(int Fid, int Addr, int Value)> Markers { get; init; }
    /// <summary>Every callee → builtin decision the compiler baked.</summary>
    public required IReadOnlyList<(int Fid, bool Found, int Id)> Builtins { get; init; }
    /// <summary>Table sizes at bake time: a loading process whose tables
    /// differ has a different intern history — reject cheaply.</summary>
    public required int FunctorCount { get; init; }
    public required int AtomCount { get; init; }
    /// <summary>The whole functor table at bake time, (name, arity) by id:
    /// baked operand cells carry these ids, and when the loader's table
    /// diverges this names the exact first divergence instead of leaving a
    /// mystery count.</summary>
    public required IReadOnlyList<(string Name, int Arity)> Functors { get; init; }

    /// <summary>FNV-1a over the linked bytecode and call sites: the operands
    /// carry process ids (atoms, functors), so equal hashes mean every
    /// id-bearing constant the module baked from this member still holds.</summary>
    public static ulong HashCode64(CompiledPredicate pred)
    {
        const ulong prime = 1099511628211UL;
        ulong h = 14695981039346656037UL;
        foreach (byte b in pred.Bytecode) { h ^= b; h *= prime; }
        h ^= (uint)pred.Arity; h *= prime;
        foreach (var site in pred.CallSites)
        {
            h ^= (uint)site.OpcodeOffset; h *= prime;
            h ^= (uint)site.CalleeFunctorId; h *= prime;
        }
        return h;
    }

    private static int LiveAtomCount => AtomTable.PermanentCount + AtomTable.TransientCount;

    /// <summary>Compiles the group with a recording env and captures the
    /// evidence. The caller's process state at this moment is what the bake
    /// asserts about the loading process's state at install.</summary>
    public static WasmBakedGroup Bake(IReadOnlyList<WasmGroupMember> members,
                                      IWasmCompileEnv env)
    {
        var rec = new RecordingEnv(env);
        var entry = WasmPredicateCompiler.CompileGroup(members, rec);
        var baked = new List<Member>(members.Count);
        foreach (var m in members)
        {
            int fid = m.Predicate.FunctorId;
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = AtomTable.GetById(atomId)?.Name
                ?? throw new WasmCompileException($"functor {fid} has no atom");
            baked.Add(new Member(fid, arity, name, m.Bias,
                entry.EntryCursorByFid[fid], HashCode64(m.Predicate),
                m.FloatLiterals?.ToArray()));
        }
        int fcount = FunctorTable.Count;
        var functors = new List<(string, int)>(fcount);
        for (int fid = 0; fid < fcount; fid++)
        {
            if (FunctorTable.TryLookup(fid, out var fe))
                functors.Add((AtomTable.GetById(fe.AtomId)?.Name ?? "", fe.Arity));
            else functors.Add(("", -1));
        }
        return new WasmBakedGroup
        {
            Module = entry.Module,
            RegisterDemand = entry.RegisterDemand,
            Members = baked,
            CursorByAddress = entry.CursorByAddress.ToList(),
            Markers = rec.Markers,
            Builtins = rec.Builtins,
            FunctorCount = fcount,
            AtomCount = LiveAtomCount,
            Functors = functors,
        };
    }

    /// <summary><see cref="Bake"/> over just the compilable members: a
    /// refusal drops the member (probed with INERT encodings — a probe that
    /// interned markers would pollute the pool and the final bake's recorded
    /// sequence could never replay against a virgin one).</summary>
    public static WasmBakedGroup BakeCompilable(IReadOnlyList<WasmGroupMember> members,
        IWasmCompileEnv env, Action<WasmGroupMember, string>? onRefused = null)
    {
        try
        {
            return Bake(members, env);
        }
        catch (WasmCompileException)
        {
            var probe = new ProbeEnv(env);
            var good = new List<WasmGroupMember>(members.Count);
            foreach (var m in members)
            {
                try
                {
                    WasmPredicateCompiler.CompileGroup(new[] { m }, probe);
                    good.Add(m);
                }
                catch (WasmCompileException e)
                {
                    onRefused?.Invoke(m, e.Message);
                }
            }
            return Bake(good, env);
        }
    }

    /// <summary>Real builtin/inline decisions, inert encodings: answers "does
    /// it compile?" without touching the global marker pool.</summary>
    private sealed class ProbeEnv(IWasmCompileEnv inner) : IWasmCompileEnv
    {
        public int EncodeBp(int functorId, int address) => 0;
        public int EncodeReturnMarker(int functorId, int address) => 0;
        public int EncodeCallTarget(int calleeFunctorId) => 0;
        public int EncodeDeoptPc(int bytecodePc) => bytecodePc;
        public bool TryGetBuiltin(int calleeFunctorId, out int builtinId)
            => inner.TryGetBuiltin(calleeFunctorId, out builtinId);
        public bool IsDirectBuiltin(int builtinId) => inner.IsDirectBuiltin(builtinId);
        public bool IsInlineUnify(int builtinId) => inner.IsInlineUnify(builtinId);
        public bool IsInlineCompare(int builtinId, out bool negated)
            => inner.IsInlineCompare(builtinId, out negated);
    }

    /// <summary>Replays the bake's evidence against the live process:
    /// table sizes, member identity (fid + name/arity + linked address +
    /// bytecode hash + float pool), the marker intern sequence, and the
    /// builtin decisions. True means the module's baked constants are all
    /// provably current and it may be installed as-is; false names the first
    /// divergence and the caller compiles instead.</summary>
    public bool Validate(IWasmCompileEnv env,
        IReadOnlyDictionary<int, CompiledPredicate> predsByAddress,
        Func<int, IReadOnlyList<double>?> floatPools,
        out string reason)
    {
        // Fewer atoms/functors than the bake saw = certainly a different
        // history. MORE is tolerated: ids are append-only, so extras interned
        // after the bake's shift nothing baked, and extras interned before
        // or among them shift ids the per-item checks below then catch
        // (member names, bytecode hashes, the marker replay).
        if (FunctorCount > FunctorTable.Count)
        { reason = $"functor table {FunctorTable.Count} < baked {FunctorCount}"; return false; }
        if (AtomCount > LiveAtomCount)
        { reason = $"atom table {LiveAtomCount} < baked {AtomCount}"; return false; }
        // The functor table, id by id: baked operand cells carry these ids.
        // The first divergence names exactly what this process interned
        // differently — the actionable half of any rejection.
        for (int fid = 0; fid < Functors.Count; fid++)
        {
            var (bn, ba) = Functors[fid];
            if (ba < 0) continue;
            string live = FunctorTable.TryLookup(fid, out var fe)
                ? $"{AtomTable.GetById(fe.AtomId)?.Name}/{fe.Arity}" : "unassigned";
            if (live != $"{bn}/{ba}")
            { reason = $"functor id {fid}: baked {bn}/{ba}, here {live}"; return false; }
        }
        foreach (var m in Members)
        {
            if (!predsByAddress.TryGetValue(m.Bias, out var pred))
            { reason = $"{m.Name}/{m.Arity}: nothing linked at {m.Bias}"; return false; }
            if (pred.FunctorId != m.FunctorId)
            {
                string bakedNow = "unassigned";
                if (FunctorTable.TryLookup(m.FunctorId, out var b))
                    bakedNow = $"{AtomTable.GetById(b.AtomId)?.Name}/{b.Arity}";
                reason = $"{m.Name}/{m.Arity}: functor id {pred.FunctorId}"
                    + $" != baked {m.FunctorId} (baked id is now {bakedNow})";
                return false;
            }
            var (atomId, arity) = FunctorTable.Lookup(m.FunctorId);
            if (arity != m.Arity || AtomTable.GetById(atomId)?.Name != m.Name)
            { reason = $"functor {m.FunctorId} is no longer {m.Name}/{m.Arity}"; return false; }
            if (HashCode64(pred) != m.CodeHash)
            { reason = $"{m.Name}/{m.Arity}: bytecode changed"; return false; }
            var pool = floatPools(m.FunctorId);
            if (!FloatPoolsEqual(pool, m.FloatPool))
            { reason = $"{m.Name}/{m.Arity}: float pool changed"; return false; }
        }
        // Replay IN ORDER: on a pool in the bake-time state this both interns
        // and verifies each marker; a pool that already diverged mismatches.
        foreach (var (fid, addr, value) in Markers)
            if (env.EncodeReturnMarker(fid, addr) != value)
            { reason = $"marker ({fid},{addr}) interned differently"; return false; }
        foreach (var (fid, found, id) in Builtins)
        {
            bool f = env.TryGetBuiltin(fid, out int liveId);
            if (f != found || (found && liveId != id))
            { reason = $"builtin decision for functor {fid} changed"; return false; }
        }
        reason = "";
        return true;
    }

    private static bool FloatPoolsEqual(IReadOnlyList<double>? a, double[]? b)
    {
        if (a is null || a.Count == 0) return b is null || b.Length == 0;
        if (b is null || a.Count != b.Length) return false;
        for (int i = 0; i < b.Length; i++)
            if (BitConverter.DoubleToInt64Bits(a[i]) != BitConverter.DoubleToInt64Bits(b[i]))
                return false;
        return true;
    }

    // ---- serialization (an internal build asset, not a public format) ----

    private const uint Magic = 0x53574247;   // "SWBG"
    private const int Version = 1;

    public void Write(Stream s)
    {
        using var w = new BinaryWriter(s, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic); w.Write(Version);
        w.Write(FunctorCount); w.Write(AtomCount); w.Write(RegisterDemand);
        w.Write(Functors.Count);
        foreach (var (name, arity) in Functors) { w.Write(name); w.Write(arity); }
        w.Write(Members.Count);
        foreach (var m in Members)
        {
            w.Write(m.FunctorId); w.Write(m.Arity); w.Write(m.Name);
            w.Write(m.Bias); w.Write(m.EntryCursor); w.Write(m.CodeHash);
            w.Write(m.FloatPool?.Length ?? 0);
            if (m.FloatPool is not null) foreach (double d in m.FloatPool) w.Write(d);
        }
        w.Write(CursorByAddress.Count);
        foreach (var kv in CursorByAddress) { w.Write(kv.Key); w.Write(kv.Value); }
        w.Write(Markers.Count);
        foreach (var (fid, addr, value) in Markers)
        { w.Write(fid); w.Write(addr); w.Write(value); }
        w.Write(Builtins.Count);
        foreach (var (fid, found, id) in Builtins)
        { w.Write(fid); w.Write(found); w.Write(id); }
        w.Write(Module.Length);
        w.Write(Module);
    }

    public static WasmBakedGroup Read(Stream s)
    {
        using var r = new BinaryReader(s, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a baked wasm group");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"baked group version {version}");
        int functorCount = r.ReadInt32();
        int atomCount = r.ReadInt32();
        int registerDemand = r.ReadInt32();
        int nf = r.ReadInt32();
        var functors = new List<(string, int)>(nf);
        for (int i = 0; i < nf; i++)
            functors.Add((r.ReadString(), r.ReadInt32()));
        int n = r.ReadInt32();
        var members = new List<Member>(n);
        for (int i = 0; i < n; i++)
        {
            int fid = r.ReadInt32(); int arity = r.ReadInt32(); string name = r.ReadString();
            int bias = r.ReadInt32(); int cursor = r.ReadInt32(); ulong hash = r.ReadUInt64();
            int fp = r.ReadInt32();
            double[]? pool = fp == 0 ? null : new double[fp];
            for (int k = 0; k < fp; k++) pool![k] = r.ReadDouble();
            members.Add(new Member(fid, arity, name, bias, cursor, hash, pool));
        }
        int nc = r.ReadInt32();
        var cursors = new List<KeyValuePair<int, int>>(nc);
        for (int i = 0; i < nc; i++)
            cursors.Add(new KeyValuePair<int, int>(r.ReadInt32(), r.ReadInt32()));
        int nm = r.ReadInt32();
        var markers = new List<(int, int, int)>(nm);
        for (int i = 0; i < nm; i++)
            markers.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
        int nb = r.ReadInt32();
        var builtins = new List<(int, bool, int)>(nb);
        for (int i = 0; i < nb; i++)
            builtins.Add((r.ReadInt32(), r.ReadBoolean(), r.ReadInt32()));
        byte[] module = r.ReadBytes(r.ReadInt32());
        return new WasmBakedGroup
        {
            Module = module, RegisterDemand = registerDemand, Members = members,
            CursorByAddress = cursors, Markers = markers, Builtins = builtins,
            FunctorCount = functorCount, AtomCount = atomCount, Functors = functors,
        };
    }

    /// <summary>Records what the compiler baked from the env. Marker pairs
    /// are deduplicated keeping first-intern order — replaying that order
    /// against a virgin-equivalent pool reassigns identical values.</summary>
    private sealed class RecordingEnv(IWasmCompileEnv inner) : IWasmCompileEnv
    {
        private readonly Dictionary<(int, int), int> _seenMarkers = new();
        private readonly Dictionary<int, (bool, int)> _seenBuiltins = new();
        public List<(int Fid, int Addr, int Value)> Markers { get; } = new();
        public List<(int Fid, bool Found, int Id)> Builtins { get; } = new();

        private int Record(int fid, int addr, int value)
        {
            if (_seenMarkers.TryAdd((fid, addr), value))
                Markers.Add((fid, addr, value));
            return value;
        }

        public int EncodeBp(int functorId, int address)
            => Record(functorId, address, inner.EncodeBp(functorId, address));
        public int EncodeReturnMarker(int functorId, int address)
            => Record(functorId, address, inner.EncodeReturnMarker(functorId, address));
        public int EncodeCallTarget(int calleeFunctorId)
            => Record(calleeFunctorId, 0, inner.EncodeCallTarget(calleeFunctorId));
        public int EncodeDeoptPc(int bytecodePc) => inner.EncodeDeoptPc(bytecodePc);

        public bool TryGetBuiltin(int calleeFunctorId, out int builtinId)
        {
            bool found = inner.TryGetBuiltin(calleeFunctorId, out builtinId);
            if (_seenBuiltins.TryAdd(calleeFunctorId, (found, builtinId)))
                Builtins.Add((calleeFunctorId, found, builtinId));
            return found;
        }

        public bool IsDirectBuiltin(int builtinId) => inner.IsDirectBuiltin(builtinId);
        public bool IsInlineUnify(int builtinId) => inner.IsInlineUnify(builtinId);
        public bool IsInlineCompare(int builtinId, out bool negated)
            => inner.IsInlineCompare(builtinId, out negated);
    }
}

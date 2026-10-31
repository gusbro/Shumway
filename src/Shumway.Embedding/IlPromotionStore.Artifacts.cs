using Shumway.Compiler.Il;
using Shumway.Compiler.Wam;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>
/// Compiled code installed for a predicate, and the atoms it names (ADR-003):
/// the atom operands of every predicate the compile read, and the atoms of the
/// images its methods load. The engine claims them while the code is installed
/// and while, retired, an activation can still resume into it: through a resume
/// marker of the predicate, a compiled choice point, or a method on the stack
/// that stored its artifact (<see cref="Activation._ilArtifact"/>). A bundle's
/// code is permanent: it names no artifact and claims nothing.
/// </summary>
internal sealed class Tier1Artifact
{
    public readonly int FunctorId;
    /// <summary>The delegate as the compiler created it: what a compiled choice
    /// point names, under the engine's wrapper.</summary>
    public readonly PredicateDelegate Delegate;
    /// <summary>The code of the compile, and of its continuation methods when
    /// they were compiled apart; empty for a bundle's code.</summary>
    public IlArtifact[] Codes;
    public int[] Atoms;
    /// <summary>A bundle's code: its atoms are permanent and it names no
    /// artifact, so a method of it on the stack is unseen; retired, it is kept.</summary>
    public readonly bool Permanent;
    private Func<Activation, int, bool>? _resume;

    public Tier1Artifact(int functorId, PredicateDelegate del, IlArtifact[] codes, int[] atoms, bool permanent)
    {
        FunctorId = functorId;
        Delegate = del;
        Codes = codes;
        Atoms = atoms;
        Permanent = permanent;
    }

    /// <summary>What a resume into the retired code calls (ADR-054).</summary>
    public Func<Activation, int, bool> Resume => _resume ??= (engine, cursor) => Delegate(engine, cursor);

    /// <summary>Whether an activation that stored one of <paramref name="running"/>
    /// as running, or holds one of <paramref name="held"/> in a compiled choice
    /// point, runs this code.</summary>
    public bool ReachedBy(HashSet<int> running, List<object> held)
    {
        foreach (var code in Codes)
        {
            if (running.Contains(code.Id)) return true;
            foreach (var del in code.Delegates)
                foreach (object o in held)
                    if (ReferenceEquals(o, del)) return true;
        }
        foreach (object o in held)
            if (ReferenceEquals(o, Delegate)) return true;
        return false;
    }
}

public sealed partial class IlPromotionStore
{
    /// <summary>What the engine's code claims atoms through, and gives them back
    /// through (ADR-003); null when collection is off.</summary>
    internal Action<int[]>? RetainAtoms, DropAtoms;

    /// <summary>The activations that may resume into this engine's compiled
    /// code.</summary>
    internal Func<IEnumerable<Activation>>? LiveActivations;

    // By functor: the code installed for it.
    private readonly Dictionary<int, Tier1Artifact> _artifacts = new();

    // Code evicted that an activation may still resume into; released when none
    // can. At most one per functor that is not a bundle's: a new install waits
    // for the retired one to go.
    private readonly List<Tier1Artifact> _retired = new();

    // Compiled code waiting for the retired code of its functor to go.
    private readonly Dictionary<int, DeferredInstall> _deferred = new();

    private sealed class DeferredInstall
    {
        public required IlPredicateCompiler.PgoCompileResult Result;
        public required CompiledPredicate Target;
        public required CalleeRecorder? Callees;
        public required bool IsDynamic;
        public required int Stamp, Epoch;
        public int Calls;
    }

    /// <summary>Installs that waited for a retired artifact of their functor.</summary>
    public int DeferredInstalls { get; private set; }

    /// <summary>Calls served on bytecode while their functor's install waited.</summary>
    public long DeferredCalls { get; private set; }

    /// <summary>Retired artifacts an activation may still resume into.</summary>
    public int RetiredArtifacts => _retired.Count;

    /// <summary>Retired artifacts released since the engine started.</summary>
    public int ReleasedArtifacts { get; private set; }

    /// <summary>The transient atoms the code installed for <paramref name="functorId"/>
    /// claims, ascending; empty for none. For diagnostics and tests.</summary>
    public IReadOnlyList<int> ClaimedAtoms(int functorId)
        => _artifacts.TryGetValue(functorId, out var artifact) ? artifact.Atoms : System.Array.Empty<int>();

    private int _releasing;

    private Tier1Artifact? RetiredOf(int functorId)
    {
        for (int i = _retired.Count - 1; i >= 0; i--)
            if (_retired[i].FunctorId == functorId) return _retired[i];
        return null;
    }

    private Func<Activation, int, bool>? RetiredResume(int functorId) => RetiredOf(functorId)?.Resume;

    // The artifact of code compiled at run time: the atoms of every predicate
    // the compile read and of the images it loads. IL code names its images
    // through its artifact; a wasm module, compiled from one predicate, names
    // the images that predicate's bytecode does. A bundle's code is permanent.
    private Tier1Artifact ArtifactFor(int functorId, PredicateDelegate raw, CompiledPredicate? target,
        CalleeRecorder? callees, IlPredicateCompiler.CpsCode? cps, bool permanent = false)
    {
        var codes = CodesOf(raw, cps);
        if (permanent) return new Tier1Artifact(functorId, raw, codes, System.Array.Empty<int>(), permanent: true);
        int[] atoms = AtomsOf(target, callees, codes);
        if (codes.Length == 0 && target is not null)
            atoms = Union(new List<int[]> { atoms, BytecodeImageAtoms(functorId, target) });
        return new Tier1Artifact(functorId, raw, codes, atoms, permanent: false);
    }

    // The atoms of the images a predicate's bytecode names, from the pool its
    // compile reads.
    private int[] BytecodeImageAtoms(int functorId, CompiledPredicate target)
    {
        var images = PoolsFor(functorId, target).Images;
        if (images is null) return System.Array.Empty<int>();
        var parts = new List<int[]>();
        byte[] code = target.Bytecode;
        for (int pc = 0; pc < code.Length;)
        {
            var info = OpcodeTable.Get(code[pc]);
            if (!info.IsDefined || info.Size == 0) break;
            if ((Opcode)code[pc] is Opcode.PutBlit or Opcode.GetBlit or Opcode.UnifyBlit)
            {
                int id = BytecodeIO.ReadInt32(code, pc + 1);
                if ((uint)id < (uint)images.Count) parts.Add(PrologEngine.ImageAtomIds(images[id]));
            }
            pc += info.Size;
        }
        return Union(parts);
    }

    private static IlArtifact[] CodesOf(PredicateDelegate raw, IlPredicateCompiler.CpsCode? cps)
    {
        var code = IlPredicateCompiler.ArtifactOf(raw);
        var cpsCode = cps is null ? null : IlPredicateCompiler.ArtifactOf(cps.EntryDelegate);
        if (code is null) return cpsCode is null ? System.Array.Empty<IlArtifact>() : new[] { cpsCode };
        return cpsCode is null || ReferenceEquals(cpsCode, code) ? new[] { code } : new[] { code, cpsCode };
    }

    private static int[] AtomsOf(CompiledPredicate? target, CalleeRecorder? callees, IlArtifact[] codes)
    {
        var parts = new List<int[]>();
        if (target is not null) parts.Add(CodeAtoms.Of(target).Ids);
        if (callees is not null)
            foreach (int fid in callees.LookedUp)
                if (callees.Inner.TryGetValue(fid, out var pred)) parts.Add(CodeAtoms.Of(pred).Ids);
        foreach (var code in codes)
            foreach (var image in code.Images)
                parts.Add(PrologEngine.ImageAtomIds(image));
        return Union(parts);
    }

    // The ids of ascending, distinct arrays, ascending and distinct.
    private static int[] Union(List<int[]> parts)
    {
        int[]? only = null;
        List<int>? all = null;
        foreach (var ids in parts)
        {
            if (ids.Length == 0) continue;
            if (only is null && all is null) { only = ids; continue; }
            all ??= new List<int>(only!);
            all.AddRange(ids);
        }
        if (all is null) return only ?? System.Array.Empty<int>();
        all.Sort();
        int n = 0;
        for (int i = 0; i < all.Count; i++)
            if (n == 0 || all[i] != all[n - 1]) all[n++] = all[i];
        all.RemoveRange(n, all.Count - n);
        return all.ToArray();
    }

    // The continuation methods of an installed delegate landed: their code and
    // the atoms of their images join its artifact.
    private void NoteCpsInstalled(int functorId, IlPredicateCompiler.CpsCode cps)
    {
        if (!_artifacts.TryGetValue(functorId, out var artifact)) return;
        var cpsCode = IlPredicateCompiler.ArtifactOf(cps.EntryDelegate);
        if (cpsCode is null || System.Array.IndexOf(artifact.Codes, cpsCode) >= 0) return;
        var codes = new IlArtifact[artifact.Codes.Length + 1];
        artifact.Codes.CopyTo(codes, 0);
        codes[^1] = cpsCode;
        artifact.Codes = codes;
        var parts = new List<int[]>();
        foreach (var image in cpsCode.Images) parts.Add(PrologEngine.ImageAtomIds(image));
        int[] more = Union(parts);
        if (more.Length == 0) return;
        RetainAtoms?.Invoke(more);
        artifact.Atoms = Union(new List<int[]> { artifact.Atoms, more });
    }

    // The installed code of a functor is replaced or evicted: it may still be
    // resumed into, so it stays claimed until nothing reaches it.
    private void RetireInstalled(int functorId)
    {
        if (_artifacts.Remove(functorId, out var artifact)) _retired.Add(artifact);
    }

    /// <summary>Releases every retired artifact no activation of the engine
    /// reaches: through a resume marker of its predicate, a compiled choice
    /// point of its code, or a method of its code on the stack. A bundle's code
    /// stays: it names no artifact, so a method of it on the stack is unseen.</summary>
    internal void ReleaseRetiredUnreached()
    {
        if (_retired.Count == 0 || _releasing > 0) return;
        _releasing++;
        try
        {
            var fids = new List<int>(_retired.Count);
            foreach (var a in _retired) fids.Add(a.FunctorId);
            fids.Sort();
            int n = 0;
            for (int i = 0; i < fids.Count; i++)
                if (n == 0 || fids[i] != fids[n - 1]) fids[n++] = fids[i];
            fids.RemoveRange(n, fids.Count - n);
            var fidArray = fids.ToArray();
            var reachedFid = new bool[fidArray.Length];
            var running = new HashSet<int>();
            var held = new List<object>();
            if (LiveActivations is { } live)
                foreach (var activation in live())
                {
                    activation.MarkReachedMarkers(fidArray, reachedFid);
                    if (activation.RunningArtifact is int id and not 0) running.Add(id);
                    activation.CollectIlChoicePointDelegates(held);
                }
            int kept = 0;
            for (int i = 0; i < _retired.Count; i++)
            {
                var a = _retired[i];
                bool reached = a.Permanent
                    || reachedFid[System.Array.BinarySearch(fidArray, a.FunctorId)]
                    || a.ReachedBy(running, held);
                if (reached)
                {
                    _retired[kept++] = a;
                    continue;
                }
                if (a.Atoms.Length > 0) DropAtoms?.Invoke(a.Atoms);
                ReleasedArtifacts++;
            }
            _retired.RemoveRange(kept, _retired.Count - kept);
        }
        finally { _releasing--; }
    }

    // Installs compiled code for a functor, or parks it while the functor's
    // retired code can still be resumed into: a resume marker names the functor
    // and a cursor, not a version, so two versions never serve one functor.
    private bool InstallCompiled(int functorId, IlPredicateCompiler.PgoCompileResult result,
        CompiledPredicate target, CalleeRecorder? callees, bool isDynamic, int epoch, int stamp)
    {
        ReleaseRetiredUnreached();
        if (RetiredOf(functorId) is not null)
        {
            _deferred[functorId] = new DeferredInstall
            {
                Result = result, Target = target, Callees = callees, IsDynamic = isDynamic,
                Stamp = stamp, Epoch = epoch,
            };
            DeferredInstalls++;
            return false;
        }
        var del = isDynamic ? GuardDynamicSnapshot(functorId, result.Delegate) : result.Delegate;
        InstallDelegate(functorId, del, ArtifactFor(functorId, result.Delegate, target, callees, result.Cps));
        RecordCopies(functorId, callees);
        if (result.Cps is { } cps) _cpsCode[functorId] = cps;
        if (result.ProfileKey >= 0) _pgoProfileKeys[functorId] = result.ProfileKey;
        OnPromotionInstalled?.Invoke(functorId, del);
        // A dynamic snapshot goes at its next mutation, and its aliases with it.
        if (!isDynamic) InstallMemberAliases(functorId, del, callees);
        return true;
    }

    // A call of a functor whose install waits: installed once the retired code
    // is gone, if the code still fits the program; else the counter warms it
    // again. The check walks the activations, so it runs on every 64th call.
    private PredicateDelegate? ServeDeferred(int functorId, DeferredInstall waiting)
    {
        DeferredCalls++;
        if (RetiredOf(functorId) is not null)
        {
            if ((++waiting.Calls & 63) != 0) return null;
            ReleaseRetiredUnreached();
            if (RetiredOf(functorId) is not null) return null;
        }
        _deferred.Remove(functorId);
        _mutationStamp.TryGetValue(functorId, out int stampNow);
        if (stampNow != waiting.Stamp || ChangedSince(waiting.Callees, waiting.Epoch)) return null;
        return InstallCompiled(functorId, waiting.Result, waiting.Target, waiting.Callees, waiting.IsDynamic,
                waiting.Epoch, waiting.Stamp)
            ? _delegates[functorId] : null;
    }
}

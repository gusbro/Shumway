using Shumway.Compiler.Wam;
using Shumway.Compiler.Wasm;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Wasm;

/// <summary>The relocatable bake: compile once with sentinels, install into
/// an engine whose link and module ids differ, and run exactly as the live
/// compile does (same answers, same tier counters). Tampering a relocation
/// or the builtin evidence must show.</summary>
public class WasmRelocatableModuleTests
{
    private const string Corpus = """
        color(red). color(green). color(blue).
        shape(circle(1)). shape(square(2)). shape(tri(3, 4)).
        pick(C, S) :- color(C), shape(S).
        sum([], 0).
        sum([H|T], N) :- sum(T, M), N is M + H.
        same(X, Y) :- X == Y.
        eq(X, Y) :- X = Y.
        big(X) :- X > 100.
        outside(X) :- helper(X).
        helper(z).
        """;

    // helper/1 stays off the module: outside/1's call to it is a foreign
    // CallTarget relocation, resolved through the resume table at run time.
    private static readonly string[] MemberNames =
        { "user$color", "user$shape", "user$pick", "user$sum", "user$same", "user$eq",
          "user$big", "user$outside" };

    private const string Probe = """
        findall(C-S, pick(C, S), L), sum([1,2,3], 6), same(a, a), \+ same(a, b),
        eq(f(X), f(1)), X == 1, big(101), \+ big(5), outside(z), \+ outside(y).
        """;

    /// <summary>The corpus's members in link order, with their live biases.
    /// The engine is left linked and ready for a wasm world.</summary>
    private static (PrologEngine Engine, List<WasmGroupMember> Members) Linked(
        string prefix = "")
    {
        var engine = new PrologEngine();
        engine.ConsultString(prefix + Corpus);
        var store = engine.IlPromotion;
        store.Threshold = 0;
        store.Wasm = new WasmPromotionStore(store) { Threshold = int.MaxValue };
        engine.Query("true.");
        var members = new List<WasmGroupMember>();
        foreach (var (addr, pred) in WasmPromotionStore.StaticPredicatesOf(engine))
        {
            var (name, _) = RelocatingCompileEnv.NameOf(pred.FunctorId);
            if (Array.IndexOf(MemberNames, name) >= 0)
                members.Add(new WasmGroupMember(pred, addr,
                    store.FloatPoolProvider?.Invoke(pred.FunctorId)));
        }
        Assert.Equal(MemberNames.Length, members.Count);
        return (engine, members);
    }

    private static DesktopWasmWorld InstallLive(PrologEngine engine, List<WasmGroupMember> members)
    {
        var world = new DesktopWasmWorld();
        TieredEngine.Install(world, members, new EngineWasmCompileEnv(), engine.IlPromotion);
        Bind(engine, world, members);
        return world;
    }

    private static void Bind(PrologEngine engine, DesktopWasmWorld world,
        IEnumerable<WasmGroupMember> members)
    {
        foreach (var m in members)
        {
            engine.IlPromotion.RegisterBoundDelegate(m.Predicate.FunctorId,
                new WasmTierDelegate(m.Predicate.FunctorId, world).Invoke);
            engine.IlPromotion.Wasm!.NoteInstalled(m.Predicate.FunctorId, m.Bias, m.Predicate);
        }
    }

    /// <summary>Resolves against the engine's link and installs; the world
    /// is pre-seeded so the module id is not the bake's 0.</summary>
    private static bool TryInstallRelocated(PrologEngine engine, List<WasmGroupMember> members,
        WasmRelocatableModule module, out string reason)
    {
        var world = new DesktopWasmWorld();
        // A module ahead of ours: the relocated one installs as id 1.
        var filler = new WasmGroupMember(members[0].Predicate, members[0].Bias, null);
        TieredEngine.InstallOne(world, filler, new EngineWasmCompileEnv());
        Assert.Equal(1, world.NextModuleId);
        var biasByFid = new Dictionary<int, int>();
        foreach (var m in members) biasByFid[m.Predicate.FunctorId] = m.Bias;
        if (!module.TryResolve(new EngineWasmCompileEnv(),
                fid => biasByFid.TryGetValue(fid, out int b) ? b : -1,
                world.NextModuleId, out var entry, out var entryAddr, out reason))
            return false;
        entry.InstallInto(world, entryAddr);
        Bind(engine, world, members);
        return true;
    }

    private static WasmRelocatableModule RoundTrip(WasmRelocatableModule m)
    {
        var ms = new MemoryStream(m.ToBytes());
        return WasmRelocatableModule.Read(ms);
    }

    private static string Answers(PrologEngine engine)
    {
        WasmTierDelegate.ResetDiag();
        var r = engine.Query(Probe);
        Assert.True(r.Success);
        return r.Bindings["L"].ToString()!;
    }

    private static (long Entries, long Deopts, long Builtins, long Hops) Counters()
        => (WasmTierDelegate.DiagEntries, WasmTierDelegate.DiagDeopts,
            WasmTierDelegate.DiagBuiltins, WasmTierDelegate.DiagInWasmHops);

    [DiagFact]
    public void RelocatedIntoAnotherLink_SameAnswersAndSameCounters()
    {
        var (a, membersA) = Linked();
        var module = RoundTrip(WasmRelocatableModule.Bake(membersA, new EngineWasmCompileEnv()));
        Assert.Contains(module.Relocations, r => r.Kind == WasmRelocKind.Atom && r.Name == "red");
        Assert.Contains(module.Relocations, r => r.Kind == WasmRelocKind.Functor && r.Name == "tri");
        Assert.Contains(module.Relocations, r => r.Kind == WasmRelocKind.CallTarget && r.Name == "user$helper");
        // `=`, `>` and `is` lower to WAM instructions; `==` is the one call
        // that goes through a builtin id in this corpus.
        Assert.Contains(module.Relocations, r => r.Kind == WasmRelocKind.Builtin && r.Name == "==");
        Assert.Contains(module.Relocations, r => r.Kind == WasmRelocKind.ModuleId);
        InstallLive(a, membersA);
        string live = Answers(a);
        var liveCounters = Counters();
        Assert.True(liveCounters.Entries > 0);

        // A filler predicate ahead of the corpus moves every linked base.
        var (b, membersB) = Linked("filler(1, 2, 3). filler(4, 5, 6). filler(x, y, z).\n");
        Assert.NotEqual(membersA[0].Bias, membersB[0].Bias);
        Assert.True(TryInstallRelocated(b, membersB, module, out string reason), reason);
        string relocated = Answers(b);
        Assert.Equal(live, relocated);
        Assert.StartsWith(".(-(red, circle(1))", live);
        Assert.Equal(liveCounters, Counters());
        // The tier ran it: the same delegates report the same work.
        Assert.True(a.IlPromotion.PromotedFunctorIds().Count() >= MemberNames.Length);
    }

    [Fact]
    public void TamperedAtomRelocation_ChangesTheAnswer()
    {
        var (a, membersA) = Linked();
        var module = WasmRelocatableModule.Bake(membersA, new EngineWasmCompileEnv());
        // Swap what the code's `red` and `blue` immediates resolve to: the
        // relocation, not the baked id, is what the installed code runs.
        var relocs = new List<WasmRelocation>();
        foreach (var r in module.Relocations)
        {
            string name = r.Kind == WasmRelocKind.Atom
                ? r.Name switch { "red" => "blue", "blue" => "red", _ => r.Name }
                : r.Name;
            relocs.Add(r with { Name = name });
        }
        var tampered = Tamper(module, relocs, module.Builtins);
        Assert.True(TryInstallRelocated(a, membersA, tampered, out string reason), reason);
        string answers = Answers(a);
        // Canonical form: the list starts with the swapped colour.
        Assert.StartsWith(".(-(blue, circle(1))", answers);
        Assert.DoesNotContain("-(red, circle(1))", answers.Substring(0, 30));
    }

    [Fact]
    public void ChangedBuiltinEvidence_Rejects()
    {
        var (a, membersA) = Linked();
        var module = WasmRelocatableModule.Bake(membersA, new EngineWasmCompileEnv());
        var evidence = new List<WasmBuiltinEvidence>();
        foreach (var e in module.Builtins)
            evidence.Add(e.Name == "==" ? e with { InlineCompare = !e.InlineCompare } : e);
        var tampered = Tamper(module, module.Relocations, evidence);
        Assert.False(TryInstallRelocated(a, membersA, tampered, out string reason));
        Assert.Contains("==/2", reason);
    }

    [Fact]
    public void MissingMember_Rejects()
    {
        var (a, membersA) = Linked();
        var module = WasmRelocatableModule.Bake(membersA, new EngineWasmCompileEnv());
        var fewer = membersA.GetRange(1, membersA.Count - 1);
        Assert.False(TryInstallRelocated(a, fewer, module, out string reason));
        Assert.Contains("not linked", reason);
    }

    [Fact]
    public void AnotherMailboxAbi_RejectsWithARebakeMessage()
    {
        var (_, membersA) = Linked();
        byte[] bytes = WasmRelocatableModule.Bake(membersA, new EngineWasmCompileEnv()).ToBytes();
        // The ABI stamp sits right after the magic. A module baked by an
        // engine with another mailbox layout reads shifted slots with no
        // error anywhere downstream, so the READER is the only place that
        // can catch it -- and the message must say the cure.
        bytes[4] ^= 0xFF;
        var ex = Assert.Throws<InvalidDataException>(
            () => WasmRelocatableModule.Read(new MemoryStream(bytes)));
        Assert.Contains("mailbox ABI", ex.Message);
        Assert.Contains("rebake", ex.Message);
    }

    /// <summary>A copy of the module with other relocations or evidence:
    /// through the wire, so a tampered file is what the reader sees.</summary>
    private static WasmRelocatableModule Tamper(WasmRelocatableModule m,
        IReadOnlyList<WasmRelocation> relocs, IReadOnlyList<WasmBuiltinEvidence> builtins)
    {
        var ms = new MemoryStream();
        m.Write(ms, relocs, builtins);
        ms.Position = 0;
        return WasmRelocatableModule.Read(ms);
    }
}

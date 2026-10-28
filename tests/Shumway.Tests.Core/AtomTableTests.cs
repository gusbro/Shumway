using System.Runtime.CompilerServices;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

public class AtomTableTests
{
    public AtomTableTests() => AtomTable.ResetForTesting();

    // ---------- Pre-registered atoms ----------

    [Theory]
    [InlineData(AtomTable.EmptyListId, "[]")]
    [InlineData(AtomTable.EmptyBracesId, "{}")]
    [InlineData(AtomTable.ConsFunctorId, ".")]
    [InlineData(AtomTable.TrueId, "true")]
    [InlineData(AtomTable.FalseId, "false")]
    public void PreRegistered_HasExpectedNameAndPermanence(int id, string name)
    {
        var atom = AtomTable.GetById(id);
        Assert.NotNull(atom);
        Assert.Equal(id, atom!.Id);
        Assert.Equal(name, atom.Name);
        Assert.True(atom.IsPermanent);
    }

    [Fact]
    public void PreRegistered_FindableByName()
    {
        Assert.Equal(AtomTable.EmptyListId, AtomTable.Intern("[]").Id);
        Assert.Equal(AtomTable.TrueId, AtomTable.Intern("true").Id);
    }

    [Fact]
    public void FreshTable_HasFivePreRegisteredPermanents()
    {
        Assert.Equal(AtomTable.PreRegisteredPermanentCount, AtomTable.PermanentCount);
        Assert.Equal(0, AtomTable.TransientCount);
        Assert.Equal(0, AtomTable.TransientWeakCount);
    }

    // ---------- Intern semantics ----------

    [Fact]
    public void Intern_NewName_AssignsIdAtOrAboveFirstUserId()
    {
        var atom = AtomTable.Intern("foo");
        Assert.True(atom.Id >= AtomTable.FirstUserId, $"id={atom.Id} must be >= {AtomTable.FirstUserId}");
        Assert.Equal("foo", atom.Name);
        Assert.False(atom.IsPermanent);
    }

    [Fact]
    public void Intern_SameName_ReturnsSameInstance()
    {
        var a = AtomTable.Intern("foo");
        var b = AtomTable.Intern("foo");
        Assert.Same(a, b);
    }

    [Fact]
    public void Intern_DifferentNames_AssignDifferentIds()
    {
        var a = AtomTable.Intern("foo");
        var b = AtomTable.Intern("bar");
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void Intern_WithPermanent_OnNewName_CreatesPermanent()
    {
        var atom = AtomTable.Intern("perma", permanent: true);
        Assert.True(atom.IsPermanent);
        Assert.Equal(AtomTable.PreRegisteredPermanentCount + 1, AtomTable.PermanentCount);   // 5 pre-registered + 1
        Assert.Equal(0, AtomTable.TransientCount);
    }

    [Fact]
    public void Intern_WithPermanent_OnExistingTransient_PromotesInPlace()
    {
        var transient = AtomTable.Intern("upgradable");
        Assert.False(transient.IsPermanent);
        Assert.Equal(1, AtomTable.TransientCount);

        var promoted = AtomTable.Intern("upgradable", permanent: true);
        Assert.Same(transient, promoted);
        Assert.True(promoted.IsPermanent);
        Assert.Equal(0, AtomTable.TransientCount);
        Assert.Equal(AtomTable.PreRegisteredPermanentCount + 1, AtomTable.PermanentCount);
        Assert.Equal(transient.Id, promoted.Id);  // id is preserved
    }

    [Fact]
    public void Intern_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AtomTable.Intern(null!));
    }

    // ---------- GetById ----------

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        Assert.Null(AtomTable.GetById(999_999));
    }

    [Fact]
    public void GetById_FindsTransient()
    {
        var atom = AtomTable.Intern("x");
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void GetById_FindsPermanent()
    {
        var atom = AtomTable.Intern("y", permanent: true);
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    // ---------- Claims and collection ----------

    // Every claimant has marked since anything was taken: a collection with
    // this minimum reclaims every unclaimed transient atom.
    private static int CollectAll() => AtomTable.Collect(AtomTable.BeginMark() + 1);

    [Fact]
    public void Collect_PermanentAtomsAlwaysSurvive()
    {
        CollectAll();
        Assert.Equal(AtomTable.PreRegisteredPermanentCount, AtomTable.PermanentCount);
        Assert.NotNull(AtomTable.GetById(AtomTable.EmptyListId));
        Assert.NotNull(AtomTable.GetById(AtomTable.TrueId));
    }

    [Fact]
    public void Collect_ClaimedTransient_IsRetained()
    {
        var atom = AtomTable.Intern("keep_me");
        AtomTable.ApplyClaims(System.Array.Empty<int>(), new[] { atom.Id });
        Assert.Equal(1, AtomTable.ClaimsOf(atom.Id));
        CollectAll();
        Assert.Equal(1, AtomTable.TransientCount);
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void Collect_UnclaimedTransient_Dies()
    {
        var atom = AtomTable.Intern("drop_me");
        int id = atom.Id;
        Assert.Equal(1, CollectAll());
        Assert.Equal(0, AtomTable.TransientCount);
        Assert.Equal(0, AtomTable.TransientWeakCount);
        Assert.Null(AtomTable.GetById(id));
        Assert.False(AtomTable.IsTransient(id));
        // After it died, interning the same name allocates a new id.
        Assert.NotEqual(id, AtomTable.Intern("drop_me").Id);
    }

    [Fact]
    public void Collect_TakenSinceAClaimantsLastMark_Survives()
    {
        long lastMark = AtomTable.BeginMark();
        var atom = AtomTable.Intern("taken");
        // A claimant whose last mark is older than the intern may hold it.
        AtomTable.Collect(lastMark);
        Assert.Same(atom, AtomTable.GetById(atom.Id));
        // Once every claimant has marked since, it goes.
        CollectAll();
        Assert.Null(AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void ApplyClaims_ALostClaim_StampsTheEpoch()
    {
        var atom = AtomTable.Intern("let_go");
        AtomTable.ApplyClaims(System.Array.Empty<int>(), new[] { atom.Id });
        long mark = AtomTable.BeginMark();
        AtomTable.ApplyClaims(new[] { atom.Id }, System.Array.Empty<int>());
        Assert.Equal(0, AtomTable.ClaimsOf(atom.Id));
        // Let go at epoch `mark`: a claimant that marked at `mark` may have
        // taken it from the one that let go, and has not marked since.
        AtomTable.Collect(mark);
        Assert.Same(atom, AtomTable.GetById(atom.Id));
        CollectAll();
        Assert.Null(AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void ApplyClaims_MergesTwoSortedLists()
    {
        int a = AtomTable.Intern("ca").Id, b = AtomTable.Intern("cb").Id, c = AtomTable.Intern("cc").Id;
        int[] before = new[] { a, b }, after = new[] { b, c };
        System.Array.Sort(before);
        System.Array.Sort(after);
        AtomTable.ApplyClaims(System.Array.Empty<int>(), before);
        AtomTable.ApplyClaims(before, after);
        Assert.Equal(0, AtomTable.ClaimsOf(a));
        Assert.Equal(1, AtomTable.ClaimsOf(b));
        Assert.Equal(1, AtomTable.ClaimsOf(c));
        // A permanent id in a list is not a claim.
        AtomTable.ApplyClaims(System.Array.Empty<int>(), new[] { AtomTable.TrueId });
        Assert.Equal(-1, AtomTable.ClaimsOf(AtomTable.TrueId));
    }

    [Fact]
    public void Collect_ForeignHeld_DemotesToTransientWeak()
    {
        var atom = AtomTable.Intern("foreign");
        AtomTable.RegisterForeignHold(atom);
        CollectAll();
        Assert.Equal(0, AtomTable.TransientCount);
        Assert.Equal(1, AtomTable.TransientWeakCount);
        // Still findable while C# (the test) holds the strong ref.
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void Collect_TransientWeakClaimedAgain_IsTransientAgain()
    {
        var atom = AtomTable.Intern("resurrect");
        AtomTable.RegisterForeignHold(atom);
        int id = atom.Id;
        CollectAll();
        Assert.Equal(1, AtomTable.TransientWeakCount);

        // A claimant's mark finds it again.
        AtomTable.ApplyClaims(System.Array.Empty<int>(), new[] { id });
        CollectAll();
        Assert.Equal(1, AtomTable.TransientCount);
        Assert.Equal(0, AtomTable.TransientWeakCount);
        Assert.Same(atom, AtomTable.GetById(id));
    }

    [Fact]
    public void Intern_OfATransientWeakAtom_MakesItTransientAgain()
    {
        var atom = AtomTable.Intern("taken_back");
        AtomTable.RegisterForeignHold(atom);
        CollectAll();
        Assert.Equal(1, AtomTable.TransientWeakCount);
        Assert.Same(atom, AtomTable.Intern("taken_back"));
        AtomTable.Collect(AtomTable.CurrentEpoch);
        Assert.Equal(1, AtomTable.TransientCount);
        Assert.Equal(0, AtomTable.TransientWeakCount);
    }

    [Fact]
    public void Collect_PromotedAtom_IsNotTouched()
    {
        var atom = AtomTable.Intern("perma_promoted", permanent: true);
        CollectAll();
        Assert.Equal(AtomTable.PreRegisteredPermanentCount + 1, AtomTable.PermanentCount);
        Assert.True(atom.IsPermanent);
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void Collect_IdsAreNeverReused()
    {
        var first = AtomTable.Intern("first");
        int firstId = first.Id;
        CollectAll();
        var second = AtomTable.Intern("second");
        Assert.NotEqual(firstId, second.Id);
        Assert.True(second.Id > firstId);
    }

    [Fact]
    public void Collect_TransientWeakWithCollectedAtom_IsRemoved()
    {
        // The .NET GC reclaims the atom once the table demotes it to TransientWeak
        // and no other strong reference exists. Done in a no-inline helper so the
        // JIT cannot keep the local 'atom' rooted across the GC.Collect call.
        int id = InternForeignHeldDiscardingStrongRef("collectable");
        CollectAll();
        Assert.Equal(1, AtomTable.TransientWeakCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        CollectAll();
        Assert.Equal(0, AtomTable.TransientWeakCount);
        Assert.Equal(0, AtomTable.ForeignHoldCount);
        Assert.Null(AtomTable.GetById(id));
    }

    /// <summary>A TransientWeak atom whose object .NET collected leaves a dead
    /// by-name entry; the name interned again meanwhile is a new atom with its
    /// own entry, which the collection that drops the old one must not remove.</summary>
    [Fact]
    public void Collect_DropsOnlyItsOwnNameEntry()
    {
        int oldId = InternForeignHeldDiscardingStrongRef("renamed");
        CollectAll();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var fresh = AtomTable.Intern("renamed");
        Assert.NotEqual(oldId, fresh.Id);
        AtomTable.ApplyClaims(System.Array.Empty<int>(), new[] { fresh.Id });
        CollectAll();
        Assert.Same(fresh, AtomTable.Intern("renamed"));
    }

    /// <summary>Interns racing a collector. Each interning thread is a claimant
    /// that claims nothing and marks again every few interns, publishing its
    /// epoch; the collector takes the oldest published epoch, so it keeps killing
    /// atoms every thread has let go while the threads take the same names again.
    /// What a thread took since its last mark must be alive until it marks again.</summary>
    [Fact]
    public async Task Intern_RacingCollect_NeverHandsOutADeadAtom()
    {
        const int threads = 3, names = 64, perMark = 8, marks = 3_000;
        var lastMark = new long[threads];
        for (int t = 0; t < threads; t++) lastMark[t] = AtomTable.BeginMark();
        using var stop = new CancellationTokenSource();
        int killed = 0;
        var collector = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                long min = long.MaxValue;
                for (int t = 0; t < threads; t++) min = Math.Min(min, Volatile.Read(ref lastMark[t]));
                Interlocked.Add(ref killed, AtomTable.Collect(min));
            }
        });
        var interners = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var held = new List<Atom>(perMark);
            for (int m = 0; m < marks; m++)
            {
                foreach (var atom in held)
                {
                    var got = AtomTable.GetById(atom.Id);
                    if (!ReferenceEquals(got, atom))
                        throw new InvalidOperationException($"{atom.Name} died while held"
                            + $" [got {(got is null ? "null" : $"{got.Name}/{got.Id} state {got.State}")}"
                            + $" held state {atom.State} epoch {atom.Epoch} mark {lastMark[t]} id {atom.Id}"
                            + $" again {(ReferenceEquals(AtomTable.GetById(atom.Id), atom) ? "same" : "other")}]");
                }
                held.Clear();
                Volatile.Write(ref lastMark[t], AtomTable.BeginMark());
                for (int k = 0; k < perMark; k++)
                    held.Add(AtomTable.Intern("race" + ((m * perMark + k + t) % names)));
            }
        })).ToArray();
        try
        {
            await Task.WhenAll(interners);
        }
        finally
        {
            stop.Cancel();
            await collector;
        }
        Assert.True(killed > 0, "the collector killed nothing: the race was not exercised");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InternForeignHeldDiscardingStrongRef(string name)
    {
        var atom = AtomTable.Intern(name);
        AtomTable.RegisterForeignHold(atom);
        return atom.Id;
    }

    // ---------- RegisterForeignHold edge cases ----------

    [Fact]
    public void RegisterForeignHold_PermanentAtom_IsNoOp()
    {
        var perma = AtomTable.Intern("perma", permanent: true);
        AtomTable.RegisterForeignHold(perma);
        // No entry recorded — permanents are never collected.
        Assert.Equal(0, AtomTable.ForeignHoldCount);
    }

    [Fact]
    public void RegisterForeignHold_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AtomTable.RegisterForeignHold(null!));
    }
}

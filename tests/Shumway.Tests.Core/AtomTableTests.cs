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

    // ---------- Sweep ----------

    [Fact]
    public void Sweep_PermanentAtomsAlwaysSurvive()
    {
        // Empty reachable set: every permanent must still be there.
        AtomTable.Sweep(new HashSet<int>());
        Assert.Equal(AtomTable.PreRegisteredPermanentCount, AtomTable.PermanentCount);
        Assert.NotNull(AtomTable.GetById(AtomTable.EmptyListId));
        Assert.NotNull(AtomTable.GetById(AtomTable.TrueId));
    }

    [Fact]
    public void Sweep_TransientReachable_IsRetained()
    {
        var atom = AtomTable.Intern("keep_me");
        AtomTable.Sweep(new HashSet<int> { atom.Id });
        Assert.Equal(1, AtomTable.TransientCount);
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void Sweep_TransientUnreachableNoForeign_IsDropped()
    {
        var atom = AtomTable.Intern("drop_me");
        int id = atom.Id;
        AtomTable.Sweep(new HashSet<int>());
        Assert.Equal(0, AtomTable.TransientCount);
        Assert.Equal(0, AtomTable.TransientWeakCount);
        Assert.Null(AtomTable.GetById(id));
        // After being dropped, interning the same name allocates a new id.
        Assert.NotEqual(id, AtomTable.Intern("drop_me").Id);
    }

    [Fact]
    public void Sweep_TransientForeignHeld_DemotesToTransientWeak()
    {
        var atom = AtomTable.Intern("foreign");
        AtomTable.RegisterForeignHold(atom);
        AtomTable.Sweep(new HashSet<int>());
        Assert.Equal(0, AtomTable.TransientCount);
        Assert.Equal(1, AtomTable.TransientWeakCount);
        // Still findable while C# (the test) holds the strong ref.
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void Sweep_TransientWeakBecomingReachable_IsPromotedBack()
    {
        var atom = AtomTable.Intern("resurrect");
        AtomTable.RegisterForeignHold(atom);
        int id = atom.Id;

        AtomTable.Sweep(new HashSet<int>());
        Assert.Equal(1, AtomTable.TransientWeakCount);

        // An engine starts using the atom again → mark phase reports it.
        AtomTable.Sweep(new HashSet<int> { id });
        Assert.Equal(1, AtomTable.TransientCount);
        Assert.Equal(0, AtomTable.TransientWeakCount);
        Assert.Same(atom, AtomTable.GetById(id));
    }

    [Fact]
    public void Sweep_PromotedAtom_IsNotTouchedBySweep()
    {
        var atom = AtomTable.Intern("perma_promoted", permanent: true);
        AtomTable.Sweep(new HashSet<int>());
        Assert.Equal(AtomTable.PreRegisteredPermanentCount + 1, AtomTable.PermanentCount);
        Assert.True(atom.IsPermanent);
        Assert.Same(atom, AtomTable.GetById(atom.Id));
    }

    [Fact]
    public void Sweep_IdsAreNeverReused()
    {
        var first = AtomTable.Intern("first");
        int firstId = first.Id;
        AtomTable.Sweep(new HashSet<int>());          // drops "first"
        var second = AtomTable.Intern("second");
        Assert.NotEqual(firstId, second.Id);
        Assert.True(second.Id > firstId);
    }

    [Fact]
    public void Sweep_TransientWeakWithCollectedAtom_IsRemoved()
    {
        // We rely on the .NET GC to actually reclaim the atom once the table demotes it
        // to TransientWeak and no other strong reference exists. Done in a no-inline helper
        // so the JIT cannot keep the local 'atom' rooted across the GC.Collect call.
        int id = InternForeignHeldDiscardingStrongRef("collectable");

        // First sweep with empty reachable: foreign weak ref is still alive (the atom is
        // still strongly referenced from _transientById at the moment of the sweep, so the
        // weak ref returns true). The atom is demoted to TransientWeak; _transientById drops
        // its strong ref. After this sweep no strong ref remains anywhere.
        AtomTable.Sweep(new HashSet<int>());
        Assert.Equal(1, AtomTable.TransientWeakCount);

        // Force .NET GC. There is no live strong reference to the atom — it should be reclaimed.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Next sweep detects the dead weak ref and removes the TransientWeak entry plus
        // its by-name index entry.
        AtomTable.Sweep(new HashSet<int>());
        Assert.Equal(0, AtomTable.TransientWeakCount);
        Assert.Equal(0, AtomTable.ForeignHoldCount);   // the foreign weak ref was also compacted
        Assert.Null(AtomTable.GetById(id));
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

    [Fact]
    public void Sweep_NullReachable_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AtomTable.Sweep(null!));
    }
}

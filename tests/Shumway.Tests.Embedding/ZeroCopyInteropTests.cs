using System;
using System.Collections.Generic;
using Shumway.Embedding;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The zero-copy interop hot path (see docs/guide/interop.md §4): a foreign predicate that
/// walks and builds the engine's live heap cells directly — no <see cref="Term"/> tree, no
/// <c>List&lt;T&gt;</c>, no copy. This pins both halves of the benchmark's claim without a
/// thermally-noisy wall-clock assertion:
/// <list type="bullet">
///   <item>correctness — the raw cell walking/building produces the right terms;</item>
///   <item>allocation — the zero-copy path allocates ~nothing per call, where the
///     convenience path (a <c>List&lt;long&gt;</c> foreign, decoded through the Term-AST
///     tier) allocates per element. That delta is deterministic (GC byte counts), unlike
///     timing.</item>
/// </list>
/// Benchmark-derived and iteration-heavy, so it is gated to the pre-phase-close full run.
/// The GProlog side of the original comparison needs a native toolchain and lives only in
/// the scratchpad harness; this repo test is the Shumway half.
/// </summary>
public partial class ZeroCopyInteropTests
{
    public sealed partial class Zc
    {
        static Cell Dr(Activation e, Cell c) => c.Tag == Tag.Ref ? e.GetHeap(e.Deref(c.AsHeapIndex)) : c;

        // --- zero-copy: walk / build heap cells directly ---
        [PrologPredicate("zc_intlist_in/2")]
        public static bool ZcIntIn(Activation e)
        {
            Cell c = Dr(e, e.GetRegister(0)); long sum = 0;
            while (c.Tag == Tag.Lis) { sum += Dr(e, e.GetHeap(c.AsHeapIndex)).AsInt; c = Dr(e, e.GetHeap(c.AsHeapIndex + 1)); }
            return e.UnifyRegisterWithCell(1, Cell.Int(sum));
        }
        [PrologPredicate("zc_intlist_out/2")]
        public static bool ZcIntOut(Activation e)
        {
            int n = (int)Dr(e, e.GetRegister(0)).AsInt;
            Cell tail = Cell.Atom(AtomTable.EmptyListId);
            for (int i = n; i >= 1; i--) { int p = e.AllocateHeap(2); e.SetHeap(p, Cell.Int(i)); e.SetHeap(p + 1, tail); tail = Cell.Lis(p); }
            return e.UnifyRegisterWithCell(1, tail);
        }
        [PrologPredicate("zc_atomlist_in/2")]
        public static bool ZcAtomIn(Activation e)
        {
            Cell c = Dr(e, e.GetRegister(0)); long cnt = 0;
            while (c.Tag == Tag.Lis) { _ = Dr(e, e.GetHeap(c.AsHeapIndex)).AsAtomId; cnt++; c = Dr(e, e.GetHeap(c.AsHeapIndex + 1)); }
            return e.UnifyRegisterWithCell(1, Cell.Int(cnt));
        }
        static int _itemId = -1;
        [PrologPredicate("zc_atomlist_out/2")]
        public static bool ZcAtomOut(Activation e)
        {
            if (_itemId < 0) _itemId = AtomTable.Intern("item").Id;
            int n = (int)Dr(e, e.GetRegister(0)).AsInt;
            Cell tail = Cell.Atom(AtomTable.EmptyListId);
            for (int i = 0; i < n; i++) { int p = e.AllocateHeap(2); e.SetHeap(p, Cell.Atom(_itemId)); e.SetHeap(p + 1, tail); tail = Cell.Lis(p); }
            return e.UnifyRegisterWithCell(1, tail);
        }
        [PrologPredicate("zc_term_in/2")]
        public static bool ZcTermIn(Activation e)     // rec(Id, List, Name) -> Id + length(List)
        {
            Cell rec = Dr(e, e.GetRegister(0)); int b = rec.AsHeapIndex;  // b = functor cell; args b+1..
            long id = Dr(e, e.GetHeap(b + 1)).AsInt;
            Cell lst = Dr(e, e.GetHeap(b + 2)); long cnt = 0;
            while (lst.Tag == Tag.Lis) { cnt++; lst = Dr(e, e.GetHeap(lst.AsHeapIndex + 1)); }
            return e.UnifyRegisterWithCell(1, Cell.Int(id + cnt));
        }
        static int _recFid = -1, _nameId = -1;
        [PrologPredicate("zc_term_out/3")]
        public static bool ZcTermOut(Activation e)    // (+Id, +N, -rec(Id,[item..],name_atom))
        {
            if (_itemId < 0) _itemId = AtomTable.Intern("item").Id;
            if (_recFid < 0) { _recFid = FunctorTable.Intern(AtomTable.Intern("rec").Id, 3); _nameId = AtomTable.Intern("name_atom").Id; }
            int id = (int)Dr(e, e.GetRegister(0)).AsInt, n = (int)Dr(e, e.GetRegister(1)).AsInt;
            Cell tail = Cell.Atom(AtomTable.EmptyListId);
            for (int i = 0; i < n; i++) { int p = e.AllocateHeap(2); e.SetHeap(p, Cell.Atom(_itemId)); e.SetHeap(p + 1, tail); tail = Cell.Lis(p); }
            int sb = e.AllocateHeap(4);
            e.SetHeap(sb, Cell.Functor(_recFid)); e.SetHeap(sb + 1, Cell.Int(id)); e.SetHeap(sb + 2, tail); e.SetHeap(sb + 3, Cell.Atom(_nameId));
            return e.UnifyRegisterWithCell(2, Cell.Str(sb));
        }

        // --- convenience: same work, but decoded through the Term-AST tier (allocates) ---
        [PrologPredicate("cv_intlist_in/2")]
        public static long CvIntIn(List<long> xs) { long s = 0; for (int i = 0; i < xs.Count; i++) s += xs[i]; return s; }
    }

    private static PrologEngine NewEngine()
    {
        var e = new PrologEngine();
        e.RegisterPredicates(typeof(Zc));
        e.ConsultString(@"
            mklist(N,L) :- mklist(1,N,L).
            mklist(I,N,[]) :- I > N, !.
            mklist(I,N,[I|T]) :- I1 is I+1, mklist(I1,N,T).
            % identical loop shape; only the foreign call differs
            loop_zc(0,_,A,A) :- !.
            loop_zc(K,L,A,S) :- zc_intlist_in(L,V), A1 is A+V, K1 is K-1, loop_zc(K1,L,A1,S).
            loop_cv(0,_,A,A) :- !.
            loop_cv(K,L,A,S) :- cv_intlist_in(L,V), A1 is A+V, K1 is K-1, loop_cv(K1,L,A1,S).
            bench_zc(K,S) :- mklist(100,L), loop_zc(K,L,0,S).
            bench_cv(K,S) :- mklist(100,L), loop_cv(K,L,0,S).
        ");
        return e;
    }

    private static long Ql(PrologEngine e, string goal)
    {
        foreach (var s in e.QueryAll(goal)) return s.Get<long>("R");
        throw new Exception("no solution: " + goal);
    }
    private static bool Ok(PrologEngine e, string goal)
    {
        foreach (var _ in e.QueryAll(goal)) return true;
        return false;
    }

    [Fact]
    [Trait("Category", "Slow")]   // zero-copy interop benchmark; routine gate filters Category!=Slow, full run pre-phase-close
    public void ZeroCopyCellAccess_CorrectAndAllocationLight()
    {
        var e = NewEngine();

        // --- correctness oracle: the raw cell walking/building is right ---
        Assert.Equal(60L, Ql(e, "zc_intlist_in([10,20,30], R)."));
        Assert.Equal(4L, Ql(e, "zc_atomlist_in([a,b,c,d], R)."));
        Assert.True(Ok(e, "zc_intlist_out(3, L), L == [1,2,3]."));
        Assert.True(Ok(e, "zc_atomlist_out(2, L), L == [item,item]."));
        Assert.Equal(10L, Ql(e, "zc_term_in(rec(7,[a,b,c],x), R)."));      // 7 + 3
        Assert.True(Ok(e, "zc_term_out(5,2,T), T == rec(5,[item,item],name_atom)."));

        // --- allocation guardrail: zero-copy allocates ~nothing per call; the
        //     convenience List<long> path allocates per element (Term-AST + List). ---
        const long K = 50_000;
        // oracle: both loops compute the same sum (100*101/2 * K)
        long want = 5050 * K;
        Assert.Equal(want, Ql(e, $"bench_zc({K}, R)."));
        Assert.Equal(want, Ql(e, $"bench_cv({K}, R)."));

        long zc = AllocOf(e, $"bench_zc({K}, _).");
        long cv = AllocOf(e, $"bench_cv({K}, _).");

        // identical loop shape, so the delta is the foreign's own allocation.
        Assert.True(cv > zc * 10, $"convenience should allocate >=10x zero-copy: cv={cv} zc={zc}");
        Assert.True(zc / K < 200, $"zero-copy should allocate <200 B/call, got {zc / K} B/call (zc={zc}, K={K})");
    }

    private static long AllocOf(PrologEngine e, string goal)
    {
        foreach (var _ in e.QueryAll(goal)) break;            // warm (JIT + first-time caches)
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        foreach (var _ in e.QueryAll(goal)) break;
        return GC.GetAllocatedBytesForCurrentThread() - b0;
    }
}

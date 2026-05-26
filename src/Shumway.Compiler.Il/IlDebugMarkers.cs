using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>
/// Chunk 173: post-opcode runtime assertions for the Tier-1 IL
/// compiler's debug mode. The IL emit, when
/// <see cref="IlPredicateCompiler.DebugMode"/> is on, injects a
/// call to one of these markers right after each WAM opcode's
/// regular IL emit. Each marker re-checks the WAM-level
/// post-condition of its opcode against the current engine
/// state: <c>put_value_y slot, arg</c> for example asserts
/// <c>X[arg] == Y[slot]</c> after the IL has executed.
///
/// <para>The first marker that fails surfaces immediately — the
/// helper throws an <see cref="InvalidOperationException"/>
/// carrying the predicate name, pc, opcode mnemonic, and the
/// mismatch detail. Running a known-buggy IL flow with debug
/// mode on isolates which opcode's IL emission diverged from
/// the WAM-level semantics the bytecode interpreter would have
/// implemented for the same opcode.</para>
///
/// <para>Every marker takes the owning predicate's
/// <c>functorId</c> as a constant operand baked at IL emit
/// time, so the trace always knows which predicate's IL it
/// lives in — independent of whatever sub-call nesting the
/// engine is currently in (a static "current predicate" would
/// stale-out across calls, see chunk-173's first attempt).</para>
/// </summary>
public static class IlDebugMarkers
{
    /// <summary>Toggle for the always-on traces (PreCall /
    /// PostCall) — flip off in a test that wants to assert on
    /// the throw-on-mismatch behaviour without log noise.</summary>
    public static bool LogTrace { get; set; } = true;

    private static string LabelFor(int functorId)
    {
        var (atomId, arity) = FunctorTable.Lookup(functorId);
        return (AtomTable.GetById(atomId)?.Name ?? "?") + "/" + arity;
    }

    public static void Check_PutValueY(Engine engine, int ownerFid, int slot, int arg, int pc)
    {
        var xc = engine.GetRegister(arg);
        var yc = engine.GetY(slot);
        if (xc.Tag != yc.Tag || xc.Data != yc.Data)
            Fail(ownerFid, pc, "put_value_y", $"slot={slot} arg={arg}: X[{arg}]={Describe(engine, xc)} != Y[{slot}]={Describe(engine, yc)}");
        if (LogTrace)
            System.Console.Error.WriteLine($"[il-debug] {LabelFor(ownerFid)} pc=0x{pc:X4} put_value_y slot={slot} arg={arg}: Y[{slot}]={Describe(engine, yc)} (E={engine.E})");
    }

    public static void Check_PutValueX(Engine engine, int ownerFid, int src, int arg, int pc)
    {
        var s = engine.GetRegister(src);
        var d = engine.GetRegister(arg);
        if (s.Tag != d.Tag || s.Data != d.Data)
            Fail(ownerFid, pc, "put_value_x", $"src={src} arg={arg}: X[{src}]={Describe(engine, s)} != X[{arg}]={Describe(engine, d)}");
    }

    public static void Check_GetVariableY(Engine engine, int ownerFid, int slot, int arg, int pc)
    {
        var yc = engine.GetY(slot);
        var xc = engine.GetRegister(arg);
        if (xc.Tag != yc.Tag || xc.Data != yc.Data)
            Fail(ownerFid, pc, "get_variable_y", $"slot={slot} arg={arg}: Y[{slot}]={Describe(engine, yc)} != X[{arg}]={Describe(engine, xc)}");
    }

    public static void Check_GetVariableX(Engine engine, int ownerFid, int dest, int arg, int pc)
    {
        var s = engine.GetRegister(arg);
        var d = engine.GetRegister(dest);
        if (s.Tag != d.Tag || s.Data != d.Data)
            Fail(ownerFid, pc, "get_variable_x", $"dest={dest} arg={arg}: X[{dest}]={Describe(engine, d)} != X[{arg}]={Describe(engine, s)}");
    }

    public static void Check_PutVariableY(Engine engine, int ownerFid, int slot, int arg, int pc)
    {
        var xc = engine.GetRegister(arg);
        var yc = engine.GetY(slot);
        if (xc.Tag != yc.Tag || xc.Data != yc.Data)
            Fail(ownerFid, pc, "put_variable_y", $"X[{arg}]={Describe(engine, xc)} != Y[{slot}]={Describe(engine, yc)}");
        if (xc.Tag != Tag.Ref)
            Fail(ownerFid, pc, "put_variable_y", $"X[{arg}] tag={xc.Tag} (expected Ref)");
    }

    public static void Check_PutVariableX(Engine engine, int ownerFid, int dest, int arg, int pc)
    {
        var xc = engine.GetRegister(arg);
        var dc = engine.GetRegister(dest);
        if (xc.Tag != dc.Tag || xc.Data != dc.Data)
            Fail(ownerFid, pc, "put_variable_x", $"X[{arg}]={Describe(engine, xc)} != X[{dest}]={Describe(engine, dc)}");
        if (xc.Tag != Tag.Ref)
            Fail(ownerFid, pc, "put_variable_x", $"X[{arg}] tag={xc.Tag} (expected Ref)");
    }

    public static void Check_PreCall(Engine engine, int ownerFid, int siteFunctorId, int arity, int pc)
    {
        if (!LogTrace) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("[il-debug] ").Append(LabelFor(ownerFid))
          .Append(" pc=0x").Append(pc.ToString("X4"))
          .Append(" precall->").Append(LabelFor(siteFunctorId))
          .Append(" E=").Append(engine.E)
          .Append(" B=").Append(engine.B);
        for (int i = 0; i < arity && i < 8; i++)
        {
            var c = engine.GetRegister(i);
            sb.Append(' ').Append("X[").Append(i).Append("]=").Append(Describe(engine, c));
        }
        System.Console.Error.WriteLine(sb.ToString());
    }

    public static void Check_PostCall(Engine engine, int ownerFid, int siteFunctorId, int arity, int pc)
    {
        if (!LogTrace) return;
        var sb = new System.Text.StringBuilder();
        sb.Append("[il-debug] ").Append(LabelFor(ownerFid))
          .Append(" pc=0x").Append(pc.ToString("X4"))
          .Append(" postcall<-").Append(LabelFor(siteFunctorId))
          .Append(" E=").Append(engine.E)
          .Append(" B=").Append(engine.B);
        for (int i = 0; i < arity && i < 8; i++)
        {
            var c = engine.GetRegister(i);
            sb.Append(' ').Append("X[").Append(i).Append("]=").Append(Describe(engine, c));
        }
        System.Console.Error.WriteLine(sb.ToString());
    }

    public static void Check_Allocate(Engine engine, int ownerFid, int n, int pc, int preE)
    {
        if (engine.E < 0 || engine.E == preE)
            Fail(ownerFid, pc, "allocate", $"_e did not advance from {preE} (current {engine.E})");
    }

    public static void Check_Deallocate(Engine engine, int ownerFid, int preE, int pc)
    {
        if (engine.E == preE)
            Fail(ownerFid, pc, "deallocate", $"_e unchanged at {preE} — frame chain not popped");
    }

    private static void Fail(int ownerFid, int pc, string op, string detail)
    {
        var msg = $"[il-debug] FAIL in {LabelFor(ownerFid)} pc=0x{pc:X4} op={op}: {detail}";
        System.Console.Error.WriteLine(msg);
        throw new System.InvalidOperationException(msg);
    }

    private static string Describe(Engine engine, Cell c)
    {
        switch (c.Tag)
        {
            case Tag.Atom:
                var atom = AtomTable.GetById(c.AsAtomId);
                return $"Atom({atom?.Name ?? "?"})";
            case Tag.Int:
                return $"Int({c.AsInt})";
            case Tag.Ref:
                int home = c.AsHeapIndex;
                int deref = engine.Deref(home);
                var dcell = engine.GetHeap(deref);
                if (dcell.Tag == Tag.Ref && dcell.AsHeapIndex == deref)
                    return $"Ref({home}->{deref}:UNBOUND)";
                return $"Ref({home}->{deref}:{dcell.Tag})";
            case Tag.Str:
                return $"Str(->{c.AsHeapIndex})";
            case Tag.Lis:
                return $"Lis(->{c.AsHeapIndex})";
            default:
                return $"{c.Tag}({c.Data:X})";
        }
    }
}

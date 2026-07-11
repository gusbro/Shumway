using Shumway.Core;
using Shumway.Interpreter;
using Xunit;

namespace Shumway.Tests.Interpreter;

/// <summary>
/// Chunk 121 (Phase 8, ADR-015 chunk C, bytecode-level dispatch step 2):
/// the two new opcodes — <c>enter_dynamic</c> and <c>check_visible</c>.
///
/// <para><c>enter_dynamic</c> samples the host's
/// <c>DbGeneration</c> into <see cref="Activation.CurrentViewGen"/>; the
/// surrounding <c>try_me_else</c> captures it into the CP. Every clause
/// in the chain begins with <c>check_visible &lt;born:8&gt; &lt;died:8&gt;</c>,
/// which fails (backtracks) if the call's captured view-gen is outside
/// the <c>[born, died)</c> range — the ISO logical update view at the
/// bytecode level.</para>
/// </summary>
public class Chunk121Tests
{
    private static byte[] BuildCode(params object[] tokens)
    {
        int size = 0;
        foreach (var t in tokens)
        {
            switch (t)
            {
                case Opcode: size++; break;
                case int: size += 4; break;
                case long: size += 8; break;
                default: throw new ArgumentException($"Unexpected token {t?.GetType()}");
            }
        }
        var code = new byte[size];
        int p = 0;
        foreach (var t in tokens)
        {
            switch (t)
            {
                case Opcode op: code[p++] = (byte)op; break;
                case int i: BytecodeIO.WriteInt32(code, p, i); p += 4; break;
                case long l: BytecodeIO.WriteInt64(code, p, l); p += 8; break;
            }
        }
        return code;
    }

    // ---------- enter_dynamic ----------

    [Fact]
    public void EnterDynamic_SamplesDbGenerationIntoCurrentViewGen()
    {
        var engine = new Activation();
        engine.DbGenerationProvider = () => 42L;
        var interp = new BytecodeInterpreter(engine);

        var code = BuildCode(Opcode.EnterDynamic, Opcode.Halt);
        interp.Run(code, startPc: 0);

        Assert.Equal(42L, engine.CurrentViewGen);
    }

    [Fact]
    public void EnterDynamic_WithoutProvider_StaysAtZero()
    {
        var engine = new Activation();
        // No DbGenerationProvider wired.
        var interp = new BytecodeInterpreter(engine);

        var code = BuildCode(Opcode.EnterDynamic, Opcode.Halt);
        interp.Run(code, startPc: 0);

        Assert.Equal(0L, engine.CurrentViewGen);
    }

    // ---------- check_visible ----------

    [Fact]
    public void CheckVisible_BornBeforeAndDiedAtInfinity_PassesThrough()
    {
        var engine = new Activation();
        engine.CurrentViewGen = 5;
        var interp = new BytecodeInterpreter(engine);

        // check_visible born=0 died=MaxValue → visible → fall through to halt.
        var code = BuildCode(
            Opcode.CheckVisible, 0L, long.MaxValue,
            Opcode.Halt);
        var result = interp.Run(code, startPc: 0);

        Assert.Equal(InterpreterResult.Halted, result);
    }

    // Helper: code that lays out
    //   try_me_else <retry-target>, 0
    //   check_visible <born> <died>      (17 bytes)
    //   put_atom <visibleMarker>, X0     (9 bytes)
    //   halt
    //   put_atom <invisibleMarker>, X0   (9 bytes)
    //   halt
    // Reads X0 after run: 1 if visible path taken, 2 if backtracked into
    // the retry target.
    private static byte[] VisibilityProbe(long born, long died, int retryTarget)
    {
        return BuildCode(
            Opcode.TryMeElse, retryTarget, 0,        // 0..8
            Opcode.CheckVisible, born, died,         // 9..25
            Opcode.PutAtom, 1, 0,                    // 26..34 — visible path
            Opcode.Halt,                             // 35
            Opcode.PutAtom, 2, 0,                    // 36..44 — retry path
            Opcode.Halt);                            // 45
    }

    [Fact]
    public void CheckVisible_BornAfterView_Backtracks()
    {
        var engine = new Activation();
        engine.CurrentViewGen = 5;
        var interp = new BytecodeInterpreter(engine);

        // born=10 > view=5 → invisible → backtrack to retry-target at 36.
        Assert.Equal(InterpreterResult.Halted,
            interp.Run(VisibilityProbe(born: 10, died: long.MaxValue, retryTarget: 36), 0));
        Assert.Equal(2, engine.GetRegister(0).AsAtomId);
    }

    [Fact]
    public void CheckVisible_DiedAtOrBeforeView_Backtracks()
    {
        var engine = new Activation();
        engine.CurrentViewGen = 5;
        var interp = new BytecodeInterpreter(engine);

        // died=3 ≤ view=5 → invisible → backtrack.
        Assert.Equal(InterpreterResult.Halted,
            interp.Run(VisibilityProbe(born: 0, died: 3, retryTarget: 36), 0));
        Assert.Equal(2, engine.GetRegister(0).AsAtomId);
    }

    [Fact]
    public void CheckVisible_ViewExactlyAtBorn_IsVisible()
    {
        // born ≤ G < died — edge: born == G is visible.
        var engine = new Activation();
        engine.CurrentViewGen = 7;
        var interp = new BytecodeInterpreter(engine);

        var code = BuildCode(
            Opcode.CheckVisible, 7L, long.MaxValue,
            Opcode.Halt);
        Assert.Equal(InterpreterResult.Halted, interp.Run(code, startPc: 0));
    }

    [Fact]
    public void CheckVisible_ViewExactlyAtDied_IsInvisible()
    {
        // born ≤ G < died — edge: G == died is invisible.
        var engine = new Activation();
        engine.CurrentViewGen = 10;
        var interp = new BytecodeInterpreter(engine);

        Assert.Equal(InterpreterResult.Halted,
            interp.Run(VisibilityProbe(born: 0, died: 10, retryTarget: 36), 0));
        Assert.Equal(2, engine.GetRegister(0).AsAtomId);
    }
}

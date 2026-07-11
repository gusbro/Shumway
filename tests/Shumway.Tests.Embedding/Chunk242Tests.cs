using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ---- [PrologPredicate]-decorated test types declared at namespace
// level so the generator emits the bridge in the correct partial
// hierarchy without forcing every outer xUnit class to be partial. ----

public partial class C242Math
{
    [PrologPredicate("c242_add/3")]
    public static int Add(int a, int b) => a + b;
}

public partial class C242Logger
{
    public List<string> Messages { get; } = new();

    [PrologPredicate("c242_log/1")]
    public void Log(string msg) => Messages.Add(msg);
}

public partial class C242Checks
{
    [PrologPredicate("c242_is_positive/1")]
    public static bool IsPositive(int n) => n > 0;
}

public partial class C242WithEngine
{
    public int LastSeenB;

    [PrologPredicate("c242_log_b/1")]
    public void RecordB(Activation engine, int tag) => LastSeenB = tag;
}

[PrologTerm("c242_pt")]
public partial record C242Pt(int X, int Y);

public partial class C242PtMath
{
    [PrologPredicate("c242_translate/3")]
    public static C242Pt Translate(C242Pt p, int delta)
        => new(p.X + delta, p.Y + delta);
}

public partial class C242Mixed
{
    public int Captured;

    [PrologPredicate("c242_capture/1")]
    public void Capture(int n, Activation engine) { _ = engine; Captured = n; }
}

public static partial class C242Raw
{
    [PrologPredicate("c242_raw_true/0")]
    public static bool RawTrue(Activation engine) => true;
}

public partial class C242Strings
{
    [PrologPredicate("c242_concat/3")]
    public static string Concat(string a, string b) => a + b;
}

/// <summary>
/// Chunk 242: typed-signature <c>[PrologPredicate]</c>. The chunk-
/// 237 raw <c>bool Method(Activation)</c> shape still works as before;
/// this chunk adds an ergonomic typed signature whose register
/// decoding / return encoding is filled in by the generator.
/// </summary>
public class Chunk242Tests
{
    [Fact]
    public void TypedReturn_EncodesAsLastArgument()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C242Math));
        var sols = engine.Query<int>("c242_add(2, 3, X).", "X").ToList();
        Assert.Equal(new[] { 5 }, sols);
    }

    [Fact]
    public void VoidReturn_AlwaysSucceeds_SideEffectsOnly()
    {
        var engine = new PrologEngine();
        var logger = new C242Logger();
        engine.RegisterPredicates(logger);

        Assert.True(engine.QueryAll("c242_log(hello), c242_log(world).").Any());
        Assert.Equal(new[] { "hello", "world" }, logger.Messages);
    }

    [Fact]
    public void BoolReturn_DrivesPrologSuccess()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C242Checks));
        Assert.True(engine.QueryAll("c242_is_positive(5).").Any());
        Assert.False(engine.QueryAll("c242_is_positive(-1).").Any());
    }

    [Fact]
    public void EngineParam_ThreadedThrough()
    {
        var engine = new PrologEngine();
        var sink = new C242WithEngine();
        engine.RegisterPredicates(sink);
        engine.QueryAll("c242_log_b(99).").ToList();
        Assert.Equal(99, sink.LastSeenB);
    }

    [Fact]
    public void TypedReturn_PrologTermArg_RoundTripsThroughCustomConverters()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C242PtMath));
        engine.ConsultString(":- public start/1.\nstart(c242_pt(1, 2)).\n");
        var pts = engine.Query<C242Pt>(
            "start(P), c242_translate(P, 10, R).", "R").ToList();
        Assert.Single(pts);
        Assert.Equal(new C242Pt(11, 12), pts[0]);
    }

    [Fact]
    public void EngineParam_AnyPosition_StillThreaded()
    {
        var engine = new PrologEngine();
        var sink = new C242Mixed();
        engine.RegisterPredicates(sink);
        engine.QueryAll("c242_capture(42).").ToList();
        Assert.Equal(42, sink.Captured);
    }

    [Fact]
    public void RawSignature_StillWorks_NoBridgeRequired()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C242Raw));
        Assert.True(engine.QueryAll("c242_raw_true.").Any());
    }

    [Fact]
    public void MultipleTypedArgs_AndStringReturn()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(C242Strings));
        var r = engine.QueryFirst<string>("c242_concat(hello, world, R).", "R");
        Assert.Equal("helloworld", r);
    }
}

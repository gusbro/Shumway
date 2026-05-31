using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 237: <c>[PrologPredicate]</c> + <see cref="PrologEngine.RegisterPredicates(object)"/>.
/// Auto-registration of C# methods as Prolog builtins.
/// </summary>
public class Chunk237Tests
{
    // ---- A static class with two predicates, name auto-derived from
    // the C# method name (lowercase by convention). ----
    public static class StaticPreds
    {
        [PrologPredicate(1)]
        public static bool c237_always_true(Engine engine) => true;

        [PrologPredicate("chunk237_answer", 1)]
        public static bool Answer(Engine engine)
        {
            return engine.UnifyRegisterWithCell(0, Cell.Int(42));
        }
    }

    [Fact]
    public void RegisterPredicates_StaticClass_ViaTypeof()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(StaticPreds));
        // c237_always_true/1 succeeds for any argument.
        Assert.True(engine.QueryAll("c237_always_true(foo).").Any());
        // answer/1 binds X to 42.
        var sols = engine.QueryAll("chunk237_answer(X).")
            .Select(s => s.Bindings["X"].ToString()!).ToList();
        Assert.Equal(new[] { "42" }, sols);
    }

    // Non-static container with a static [PrologPredicate] so the
    // generic overload (which forbids C# static classes) has
    // something to exercise.
    public class NonStaticContainer
    {
        [PrologPredicate("c237_yes_42", 1)]
        public static bool Yes42(Engine engine)
            => engine.UnifyRegisterWithCell(0, Cell.Int(42));
    }

    [Fact]
    public void RegisterPredicates_GenericOverload_NonStaticClass()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates<NonStaticContainer>();
        var sols = engine.QueryAll("c237_yes_42(X).")
            .Select(s => s.Bindings["X"].ToString()!).ToList();
        Assert.Equal(new[] { "42" }, sols);
    }

    // ---- Instance methods with captured state. ----
    public class CounterPreds
    {
        private int _count;

        [PrologPredicate("c237_counter_bump", 0)]
        public bool Bump(Engine engine)
        {
            _count++;
            return true;
        }

        [PrologPredicate("c237_counter_value", 1)]
        public bool Value(Engine engine)
        {
            return engine.UnifyRegisterWithCell(0, Cell.Int(_count));
        }
    }

    [Fact]
    public void RegisterPredicates_InstanceMethods_CaptureState()
    {
        var engine = new PrologEngine();
        var counter = new CounterPreds();
        engine.RegisterPredicates(counter);

        engine.QueryAll("c237_counter_bump.").ToList();
        engine.QueryAll("c237_counter_bump.").ToList();
        engine.QueryAll("c237_counter_bump.").ToList();

        var sols = engine.QueryAll("c237_counter_value(X).")
            .Select(s => s.Bindings["X"].ToString()!).ToList();
        Assert.Equal(new[] { "3" }, sols);
    }

    // ---- Predicate that throws a Prolog error to verify exception
    // propagation works for foreign-registered predicates. ----
    public static class ThrowingPreds
    {
        [PrologPredicate("c237_explode", 0)]
        public static bool Explode(Engine engine)
        {
            throw new PrologRuntimeException("type_error(my_reason, x)");
        }
    }

    [Fact]
    public void RegisteredPredicate_PrologExceptionPropagates()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(ThrowingPreds));
        var ex = Assert.Throws<PrologRuntimeException>(
            () => engine.QueryAll("c237_explode.").ToList());
        Assert.Contains("type_error", ex.Message);
    }

    [Fact]
    public void RegisteredPredicate_CatchableFromProlog()
    {
        var engine = new PrologEngine();
        engine.RegisterPredicates(typeof(ThrowingPreds));
        var sols = engine.QueryAll("catch(c237_explode, E, true).").ToList();
        Assert.Single(sols);
        // The error term is bound to E — verify it's structured.
        Assert.NotNull(sols[0].Bindings["E"]);
    }

    // ---- Failure: invalid signature ----
    public static class BadSignaturePreds
    {
        [PrologPredicate("oops", 0)]
        public static int WrongReturn(Engine engine) => 0;
    }

    [Fact]
    public void RegisterPredicates_RejectsInvalidSignature()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.RegisterPredicates(typeof(BadSignaturePreds)));
        Assert.Contains("bool Method", ex.Message);
    }

    // ---- Failure: instance method registered via Type overload ----
    public class InstanceOnlyPreds
    {
        [PrologPredicate("need_instance", 0)]
        public bool NeedInstance(Engine engine) => true;
    }

    [Fact]
    public void RegisterPredicates_InstanceMethodViaTypeOverload_Throws()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.RegisterPredicates(typeof(InstanceOnlyPreds)));
        Assert.Contains("instance method", ex.Message);
    }

    // ---- Failure: collision with existing builtin ----
    public static class CollidingPreds
    {
        [PrologPredicate("assertz", 1)]
        public static bool Hijack(Engine engine) => true;
    }

    [Fact]
    public void RegisterPredicates_RejectsCollisionWithStandardBuiltin()
    {
        var engine = new PrologEngine();
        var ex = Assert.Throws<InvalidOperationException>(
            () => engine.RegisterPredicates(typeof(CollidingPreds)));
        Assert.Contains("collides", ex.Message);
    }
}

using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

namespace Shumway.Tests.Wasm;

/// <summary>An arithmetic expression that arrives in a REGISTER, evaluated
/// inside the module.
///
/// <para><c>X is E</c> where E is built at run time is not a leaf, and the
/// fused opcodes read one cell: the module could handle a number and nothing
/// else, so an expression stepped aside. Measured on clp(Z), 198 of 402
/// deopts were exactly this, every one of them an operand whose tag was
/// Str -- which is what a constraint solver does all day.</para>
///
/// <para>The walk is ITERATIVE over two stacks growing toward each other
/// above the stack top. A term's depth is user data, so a recursive walk
/// would be a wasm stack overflow a Prolog program could cause on
/// purpose.</para>
///
/// <para>Which functors count as arithmetic comes from a table the HOST
/// derives from the evaluator's own name resolvers, so the two cannot
/// disagree -- a second list of operator names would be a second thing to
/// get wrong, and wrong silently: a module computing with the wrong operator
/// rather than declining.</para></summary>
public sealed class ArithExpressionTests(ITestOutputHelper o)
{
    private const string Corpus = """
        ev(E, R) :- R is E.

        % Depth, which is the whole point of a walk rather than a peek.
        nested(R)  :- ev(1 + 2 * 3 - 4, R).
        deeper(R)  :- ev(((1 + 2) * (3 + 4)) - ((5 - 6) * (7 + 8)), R).
        % Left and right association actually distinguished.
        assoc(R)   :- ev(100 - 10 - 1, R).
        assoc2(R)  :- ev(100 - (10 - 1), R).
        % Unary, mixed with binary.
        neg(R)     :- ev(-(3) + 10, R).
        absv(R)    :- ev(abs(-7) * 2, R).
        signv(R)   :- ev(sign(-9) + sign(0) + sign(4), R).
        % The integer operators the module implements.
        idiv(R)    :- ev(17 // 5, R).
        modv(R)    :- ev(-7 mod 3, R).
        minmax(R)  :- ev(min(3, 7) + max(3, 7), R).
        % Floats, and a mixed pair promoting.
        fl(R)      :- ev(1.5 * 2, R).
        fl2(R)     :- ev((1 + 1) / 4.0, R).
        fneg(R)    :- ev(-(2.5) + 0.5, R).
        fabs(R)    :- ev(abs(-2.5), R).
        % A variable INSIDE the expression, bound at call time.
        withvar(A, B, R) :- ev(A * A + B, R).

        % What must NOT be answered inside: division by zero, an unbound
        % operand, an operator the module does not implement, and an
        % overflowing product. All still answer, through the engine.
        dz(R)      :- catch(ev(1 // 0, _), error(E, _), true),
                      ( var(E) -> R = no ; R = E ).
        unbound(R) :- catch(ev(_ + 1, _), error(E, _), true),
                      ( var(E) -> R = no ; R = E ).
        sqrtv(R)   :- ev(sqrt(16.0) + 1, R).
        % A raw string literal does NOT process escapes, so this is the
        % bit-and operator and not a backslash followed by one.
        bitv(R)    :- ev(12 /\ 10, R).
        big(R)     :- ev(576460752303423487 * 576460752303423487, R).
        % A bignum leaf inside an otherwise ordinary expression.
        bigleaf(B, R) :- ev(B + 1, R).
        """;

    [Theory]
    [InlineData("nested(R), R == 3.")]
    [InlineData("deeper(R), R == 36.")]
    [InlineData("assoc(R), R == 89.")]
    [InlineData("assoc2(R), R == 91.")]
    [InlineData("neg(R), R == 7.")]
    [InlineData("absv(R), R == 14.")]
    [InlineData("signv(R), R == 0.")]
    [InlineData("idiv(R), R == 3.")]
    [InlineData("modv(R), R == 2.")]
    [InlineData("minmax(R), R == 10.")]
    [InlineData("fl(R), R == 3.0.")]
    [InlineData("fl2(R), R == 0.5.")]
    [InlineData("fneg(R), R == -2.0.")]
    [InlineData("fabs(R), R == 2.5.")]
    [InlineData("withvar(3, 4, R), R == 13.")]
    [InlineData("dz(R), R = evaluation_error(zero_divisor).")]
    [InlineData("unbound(R), R == instantiation_error.")]
    [InlineData("sqrtv(R), R == 5.0.")]
    [InlineData("bitv(R), R == 8.")]
    [InlineData("big(R), R =:= 576460752303423487 * 576460752303423487.")]
    [InlineData("B is 2 ^ 200, bigleaf(B, R), R =:= B + 1.")]
    public void TheTierAnswersWhatTheInterpreterAnswers(string goal)
    {
        var plain = new PrologEngine();
        plain.ConsultString(Corpus);
        Assert.True(plain.Query(goal).Success,
            "the interpreter's own answer moved");

        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success, $"the tier disagrees on {goal}");
    }

    /// <summary>A deep expression really is evaluated inside: no deopt.
    /// </summary>
    [DiagTheory]
    [InlineData("deeper(R), R == 36.")]
    [InlineData("nested(R), R == 3.")]
    [InlineData("minmax(R), R == 10.")]
    [InlineData("fl2(R), R == 0.5.")]
    [InlineData("withvar(3, 4, R), R == 13.")]
    public void AnExpressionIsEvaluatedInTheModule(string goal)
        => Assert.Equal(0L, DeoptsOf(goal));

    /// <summary>The counterproofs, in red. Each of these has an answer the
    /// module must NOT invent: an error, an operator it does not implement,
    /// a product that leaves sixty bits. Without them the tests above would
    /// pass just as well with an evaluator that guessed.</summary>
    [DiagTheory]
    [InlineData("dz(R), R = evaluation_error(zero_divisor).")]
    [InlineData("unbound(R), R == instantiation_error.")]
    [InlineData("sqrtv(R), R == 5.0.")]
    [InlineData("bitv(R), R == 8.")]
    [InlineData("big(R), R =:= 576460752303423487 * 576460752303423487.")]
    public void WhatItCannotComputeStaysTheEngines(string goal)
        => Assert.True(DeoptsOf(goal) > 0, $"{goal} was answered in the module");

    private long DeoptsOf(string goal)
    {
        var (tiered, _) = TieredEngine.Build(Corpus);
        Assert.True(tiered.Query(goal).Success);
        WasmTierDelegate.ResetDiag();
        Assert.True(tiered.Query(goal).Success, "the answer moved");
        long d = WasmTierDelegate.DiagDeopts;
        o.WriteLine($"{goal} -> deopts={d}");
        return d;
    }
}

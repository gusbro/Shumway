using System.Numerics;
using Shumway.Compiler.Ast;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>ADR-064 end to end: big integers as heap blocks through arithmetic,
/// literals under compounds in every write mode, copies, the database,
/// collection and backtracking, on Tier-0 and on Tier-1 from a bundle.</summary>
public class BigIntHeapTests
{
    private const string Program = """
        :- public lit_head/1, lit_body/1, lit_deep/1, lit_list/1, lit_top/1, lit_wide/1.
        :- public pi/3, bigloop/1, sumsq/3.
        % A big integer under a compound in a head, reached in write mode when
        % the argument arrives unbound and in read mode when it arrives built.
        lit_head(f(123456789012345678901234567890, g(1.5, 98765432109876543210987654321))).
        % The same in a body build, nested, and in a reserve-upfront build.
        lit_body(X) :- X = h(123456789012345678901234567890, k(98765432109876543210987654321), 5.5).
        lit_deep(X) :- X = a(b(c(123456789012345678901234567890)), d(e(f(-98765432109876543210987654321)))).
        lit_list(X) :- X = [123456789012345678901234567890, 2, 98765432109876543210987654321 | T], T = [].
        lit_top(123456789012345678901234567890).
        % An integer past the inline range but within a long: a block too.
        lit_wide(w(1000000000000000000, -1000000000000000000)).

        pi(0, P/Q, R/Q) :- !, R is P - 3*Q.
        pi(N, P/Q, S) :- H is 6*P + (2*N-1)^2*Q, M is N - 1, pi(M, H/P, S).

        bigloop(N) :- between(1, N, I), X is 2^70 + I, X > 0, fail.
        bigloop(_).

        sumsq(0, A, A) :- !.
        sumsq(N, A0, A) :- A1 is A0 + N*N*2^64, N1 is N - 1, sumsq(N1, A1, A).
        """;

    public enum Mode { Tier0, Tier1 }
    public static TheoryData<Mode> Modes => new() { Mode.Tier0, Mode.Tier1 };

    private static int Fid(string n, int a) =>
        FunctorTable.Intern(AtomTable.Intern(n, permanent: true).Id, a);

    private static PrologEngine Engine(Mode mode)
    {
        if (mode == Mode.Tier0)
        {
            var e0 = new PrologEngine();
            e0.ConsultString(Program);
            return e0;
        }
        var bundle = new Bundle(new[] { new BundleEntry("bigint_heap", Program) });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true, includeCompiledIl: true, stripWam: false);
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        Assert.True(e.IlPromotion.WaitForPendingPromotions(60_000), "promotion did not settle");
        // Anti-vacuity for the tier: the arithmetic predicates run as IL.
        Assert.True(e.IlPromotion.IsPromoted(Fid("pi", 3)), "pi/3 must be Tier-1 IL");
        Assert.True(e.IlPromotion.IsPromoted(Fid("sumsq", 3)), "sumsq/3 must be Tier-1 IL");
        return e;
    }

    private static BigInteger AsBig(Term t) => t switch
    {
        BigIntTerm b => b.Value,
        IntTerm i => i.Value,
        _ => throw new Xunit.Sdk.XunitException($"not an integer term: {t}"),
    };

    private static readonly BigInteger A = BigInteger.Parse("123456789012345678901234567890");
    private static readonly BigInteger B = BigInteger.Parse("98765432109876543210987654321");

    [Theory]
    [MemberData(nameof(Modes))]
    public void LiteralsUnderCompoundsBuildAndMatch(Mode mode)
    {
        var e = Engine(mode);

        var head = e.Query("lit_head(X), X = f(A, g(F, B)).");
        Assert.True(head.Success);
        Assert.Equal(A, AsBig(head["A"]!));
        Assert.Equal(B, AsBig(head["B"]!));
        Assert.True(e.Query("lit_head(f(123456789012345678901234567890, g(1.5, 98765432109876543210987654321))).").Success);
        Assert.False(e.Query("lit_head(f(123456789012345678901234567891, g(1.5, 98765432109876543210987654321))).").Success);
        Assert.False(e.Query("lit_head(f(123456789012345678901234567890, g(1.5, 1))).").Success);

        var body = e.Query("lit_body(X), X = h(A, k(B), 5.5).");
        Assert.True(body.Success);
        Assert.Equal(A, AsBig(body["A"]!));
        Assert.Equal(B, AsBig(body["B"]!));
        Assert.True(e.Query("lit_body(h(123456789012345678901234567890, k(98765432109876543210987654321), 5.5)).").Success);
        Assert.False(e.Query("lit_body(h(123456789012345678901234567890, k(1), 5.5)).").Success);

        var deep = e.Query("lit_deep(X), X = a(b(c(A)), d(e(f(B)))).");
        Assert.True(deep.Success);
        Assert.Equal(A, AsBig(deep["A"]!));
        Assert.Equal(-B, AsBig(deep["B"]!));

        var list = e.Query("lit_list(L), L = [A, 2, B].");
        Assert.True(list.Success);
        Assert.Equal(A, AsBig(list["A"]!));
        Assert.Equal(B, AsBig(list["B"]!));

        Assert.True(e.Query("lit_top(123456789012345678901234567890).").Success);
        Assert.False(e.Query("lit_top(123456789012345678901234567891).").Success);
        var top = e.Query("lit_top(X).");
        Assert.True(top.Success);
        Assert.Equal(A, AsBig(top["X"]!));

        var wide = e.Query("lit_wide(w(P, N)), P =:= 10^18, N =:= -(10^18).");
        Assert.True(wide.Success);
        Assert.Equal(BigInteger.Pow(10, 18), AsBig(wide["P"]!));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TheRecurrenceMatchesABigIntegerOracle(Mode mode)
    {
        var e = Engine(mode);
        const int n = 300;
        BigInteger p = 6, q = 1;
        for (int i = n; i > 0; i--)
        {
            BigInteger k = 2 * i - 1;
            BigInteger h = 6 * p + k * k * q;
            q = p;
            p = h;
        }
        BigInteger expectedR = p - 3 * q, expectedQ = q;
        Assert.True(expectedR > BigInteger.Pow(2, 1000), "the oracle did not reach big integers");

        var sol = e.Query($"pi({n}, 6/1, R/Q).");
        Assert.True(sol.Success);
        Assert.Equal(expectedR, AsBig(sol["R"]!));
        Assert.Equal(expectedQ, AsBig(sol["Q"]!));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ADeterministicLoopRunsInBoundedHeap(Mode mode)
    {
        var e = Engine(mode);
        // 2000 steps of the recurrence allocate well over a million cells of
        // results; the collector reclaims every dead block, so the heap in
        // use after the query is the live set, not the sum.
        var sol = e.Query("pi(2000, 6/1, R/Q), garbage_collect, statistics(global_stack, [Used, _]), R > 0, Q > 0.");
        Assert.True(sol.Success);
        long usedBytes = (long)AsBig(sol["Used"]!);
        Assert.True(usedBytes < 200_000, $"heap in use after the loop: {usedBytes} bytes");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AFailureDrivenLoopReclaimsThroughBacktracking(Mode mode)
    {
        var e = Engine(mode);
        var sol = e.Query("statistics(global_stack, [Before, _]), bigloop(20000), statistics(global_stack, [After, _]).");
        Assert.True(sol.Success);
        long before = (long)AsBig(sol["Before"]!), after = (long)AsBig(sol["After"]!);
        Assert.True(after - before < 10_000, $"the failure-driven loop left {after - before} bytes on the heap");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AccumulationOverManyStepsIsExact(Mode mode)
    {
        var e = Engine(mode);
        BigInteger expected = 0;
        for (int i = 1; i <= 500; i++) expected += (BigInteger)i * i * BigInteger.Pow(2, 64);
        var sol = e.Query("sumsq(500, 0, A).");
        Assert.True(sol.Success);
        Assert.Equal(expected, AsBig(sol["A"]!));
    }

    [Theory]
    [InlineData(@"X is 2^70, Y is -X, Z is X+Y, Z == 0")]
    [InlineData(@"X is 2^70 - 2^70 + 5, X == 5")]
    [InlineData(@"X is -(2^59), integer(X), X == -576460752303423488")]
    [InlineData(@"X is 2^59, Y is X - 1, Y == 576460752303423487, X > Y")]
    [InlineData(@"X is (2^100) >> 40, X =:= 2^60")]
    [InlineData(@"X is 5 << 100, X =:= 5 * 2^100")]
    [InlineData(@"X is -(2^100) >> 3, X =:= -(2^97)")]
    [InlineData(@"X is -(2^100+1) >> 1, Y is -(2^99) - 1, X =:= Y")]
    [InlineData(@"2^70 > 2^69, -(2^70) < 0, 2^70 =:= 2^70, 2^70 =\= 2^70+1, -(2^70) < -(2^69)")]
    [InlineData(@"X is 2^70, compare(O, X, 1), O == (>), Y is -(2^70), compare(O2, Y, 1), O2 == (<)")]
    [InlineData(@"X is 2^70, Y is -(2^70), msort([X, 1, Y, 0], L), L == [Y, 0, 1, X]")]
    [InlineData(@"X is 2^70, number_codes(X, Cs), atom_codes(A, Cs), A == '1180591620717411303424'")]
    [InlineData(@"atom_number('1267650600228229401496703205376', X), X =:= 2^100")]
    [InlineData(@"findall(X, (between(1, 3, I), X is 2^70 + I), L), sum_list(L, S), S =:= 3*2^70 + 6")]
    [InlineData(@"assertz(big_fact(1267650600228229401496703205376)), big_fact(X), X =:= 2^100, retract(big_fact(_))")]
    [InlineData(@"X is 2^70, Y = f(X), copy_term(Y, Z), Z = f(W), W == X")]
    [InlineData(@"X is 2^70, garbage_collect, X =:= 2^70, Y is X + 1, garbage_collect, Y =:= 2^70 + 1")]
    [InlineData(@"X is 2^200 * 2^200, X =:= 2^400")]
    [InlineData(@"X is (2^200 + 1) * (2^200 - 1), X =:= 2^400 - 1")]
    [InlineData(@"X is 2^70 mod 7, X =:= 2, Y is 2^70 // 3, Y =:= 393530540239137101141")]
    [InlineData(@"X is gcd(2^70, 2^35), X =:= 2^35, Y is msb(2^70), Y == 70")]
    [InlineData(@"X is 2^62 + 2^62, X =:= 2^63")]
    [InlineData(@"X is 1000000000000000000 * 100, X =:= 10^20")]
    [InlineData(@"X is max(2^70, 2^71), X =:= 2^71, Y is max(2^70, 2^71) + 1, Y =:= 2^71 + 1")]
    [InlineData(@"X is min(2^70, 2^71) * 3, X =:= 3 * 2^70")]
    [InlineData(@"X is abs(-(2^70)), X =:= 2^70, Y is sign(-(2^70)), Y == -1")]
    [InlineData(@"succ(X, 1267650600228229401496703205376), X =:= 2^100 - 1")]
    [InlineData(@"plus(X, 1, 1267650600228229401496703205376), X =:= 2^100 - 1")]
    [InlineData(@"flag(k, _, 2^70), flag(k, V, V + 1), V =:= 2^70, get_flag(k, W), W =:= 2^70 + 1")]
    [InlineData(@"X is 2^70, term_to_atom(f(X), A), term_to_atom(T, A), T = f(Y), Y == X")]
    [InlineData(@"X is 2^64, Y is X * X, Z is Y - X * X, Z == 0")]
    [InlineData(@"X is 2^70, Y is X, Y == X")]
    [InlineData(@"X is 2^70, Y is X * 0, Y == 0")]
    [InlineData(@"X is (2^70 + 1) - (2^70 + 2), X == -1")]
    [InlineData(@"X is 2^300, Y is X - X, Y == 0")]
    [InlineData(@"X is 2^120 >> 60, X =:= 2^60, Y is 2^120 >> 120, Y == 1, Z is 2^120 >> 121, Z == 0")]
    [InlineData(@"X is 2^70, Y is 2^71, X @< Y, Y @> X, X @> 5, 1.0 @< X")]
    [InlineData(@"catch((X is 2^70, _ is X // 0), error(evaluation_error(zero_divisor), _), true)")]
    [InlineData(@"X is 2^70, Y is 2^70, Z is X * Y, W is Z // Y, W =:= X")]
    [InlineData(@"X is 2^70, Y is float(X), Y =:= 1180591620717411303424.0")]
    [InlineData(@"X is 2^80, Y is X rdiv 2^79, Y =:= 2")]
    // Deferred products (a Scaled value): fused into a sum, combined, computed
    // for every other consumer, written straight onto the heap when final.
    [InlineData(@"P is 2^100 + 7, Q is 2^90 + 3, X is 6*P + 11*Q, X =:= 6*(2^100 + 7) + 11*(2^90 + 3)")]
    [InlineData(@"P is 2^100 + 7, Q is 2^90 + 3, X is 6*P - 11*Q, X =:= 6*(2^100 + 7) - 11*(2^90 + 3)")]
    [InlineData(@"P is 2^100 + 7, Q is 2^90 + 3, X is 11*Q - 6*P, X =:= 11*(2^90 + 3) - 6*(2^100 + 7)")]
    [InlineData(@"P is 2^100 + 7, X is 6*P + P, X =:= 7*(2^100 + 7)")]
    [InlineData(@"P is 2^100 + 7, X is P + 6*P, X =:= 7*(2^100 + 7)")]
    [InlineData(@"P is 2^100 + 7, X is 6*P + 5, X =:= 6*(2^100 + 7) + 5")]
    [InlineData(@"P is 2^100 + 7, X is 6*P - 6*P, X == 0")]
    [InlineData(@"P is 2^100, X is 3*P - 2*P, X =:= 2^100")]
    [InlineData(@"P is 2^100, X is (2*P)*3, X =:= 6*2^100, Y is 3*(2*P), Y =:= 6*2^100")]
    [InlineData(@"P is 2^100, M is 2^59 - 1, X is (M*P)*M, X =:= (2^59 - 1)*(2^59 - 1)*2^100")]
    [InlineData(@"P is 2^100 + 1, X is 2*P, X =:= 2^101 + 2")]
    [InlineData(@"P is 2^100 + 1, X is -2*P, X =:= -(2^101 + 2), Y is 2 * -P, Y =:= -(2^101 + 2)")]
    [InlineData(@"P is 2^100 + 1, X is 0*P, X == 0, Y is P*0, Y == 0")]
    [InlineData(@"P is 2^100 + 1, X is (2*P) // 7, X =:= (2^101 + 2) // 7")]
    [InlineData(@"P is 2^100 + 1, X is (2*P) mod 7, X =:= (2^101 + 2) mod 7")]
    [InlineData(@"P is 2^100 + 1, X is max(2*P, 3*P), X =:= 3*(2^100 + 1), Y is min(2*P, 3*P) + 1, Y =:= 2*(2^100 + 1) + 1")]
    [InlineData(@"P is 2^100 + 1, X is abs(-3*P), X =:= 3*(2^100 + 1), Y is sign(-3*P), Y == -1, Z is -(3*P), Z =:= -(3*(2^100 + 1))")]
    [InlineData(@"P is 2^100 + 1, 2*P > P, 3*P > 2*P, 2*P =:= P + P, -(2*P) < P")]
    [InlineData(@"P is 2^100 + 1, X is (2*P) >> 1, X =:= 2^100 + 1, Y is (2*P) << 1, Y =:= 2^102 + 4")]
    [InlineData(@"P is 2^100 + 1, X is float(2*P), X =:= 2.0 * (2^100 + 1)")]
    [InlineData(@"P is 2^100 + 1, X is (2*P)*(3*P), X =:= 6*(2^100 + 1)*(2^100 + 1)")]
    [InlineData(@"P is 2^100 + 1, X is 2*P, Y is X, Y == X, Z is 2*P, Z == X")]
    [InlineData(@"X is -(2^59) // 1, Y is 2 * (2^59 - 1) - (2^59 - 1) - (2^59 - 1), Y == 0")]
    [InlineData(@"P is 2^60, X is P * 1, X =:= 2^60, Y is -1 * P, Y =:= -(2^60), Z is (-(2^59)) * 1, Z == -576460752303423488")]
    // Division by a small divisor on the linear kernel, the four ISO
    // operations and both signs, checked by identity against the kernels that
    // the oracle tests pin, and against the bridge past the kernel's range.
    [InlineData(@"P is 2^100 + 13, Q is P // 7, R is P mod 7, Q * 7 + R =:= P, R >= 0, R < 7, S is P rem 7, S == R, D is P div 7, D == Q")]
    [InlineData(@"P is -(2^100 + 13), Q is P // 7, R is P rem 7, Q * 7 + R =:= P, R =< 0, R > -7, M is P mod 7, M >= 0, M < 7, D is P div 7, D * 7 + M =:= P, D =:= Q - 1")]
    [InlineData(@"P is 2^100 + 13, Q is P // -7, R is P rem -7, Q * -7 + R =:= P, R >= 0, M is P mod -7, M =< 0, M > -7, D is P div -7, D * -7 + M =:= P, D =:= Q - 1")]
    [InlineData(@"P is -(2^100 + 13), Q is P // -7, R is P rem -7, Q * -7 + R =:= P, R =< 0, M is P mod -7, M == R, D is P div -7, D == Q, Q > 0")]
    [InlineData(@"P is 7 * 2^100, Q is P // 7, Q =:= 2^100, M is P mod 7, M == 0, R is P rem -7, R == 0, D is P div -7, D =:= -(2^100)")]
    [InlineData(@"P is 2^100 + 13, X is P // 1, X == P, Y is P mod 1, Y == 0, Z is P // -1, Z =:= -P")]
    [InlineData(@"P is 2^100 + 13, B is 2^30, Q is P // B, R is P mod B, Q * B + R =:= P, R >= 0, R < B, Q2 is P // 1073741823, R2 is P mod 1073741823, Q2 * 1073741823 + R2 =:= P")]
    [InlineData(@"P is 2^100 + 13, Q is P // 1000003, R is P mod 1000003, Q * 1000003 + R =:= P, R >= 0, R < 1000003")]
    [InlineData(@"P is 2^59, Q is P // 2, Q =:= 2^58, integer(Q), M is -(2^60) mod 7, M >= 0, M < 7")]
    [InlineData(@"catch((P is 2^100, _ is P // 0), error(evaluation_error(zero_divisor), _), true), catch((P2 is 2^100, _ is P2 mod 0), error(evaluation_error(zero_divisor), _), true)")]
    // Two large operands multiply by the split kernel (Karatsuba): 2^6000 is a
    // hundred limbs, past the threshold; the checks go through other kernels
    // (shifts, small products, the division bridge).
    [InlineData(@"A is 3^5000, B is 7^3000, X is A * B, Y is X // B, Y =:= A, Z is X mod B, Z == 0")]
    [InlineData(@"X is (2^7000 + 3) * (2^9000 + 5), X =:= 2^16000 + 5*2^7000 + 3*2^9000 + 15")]
    [InlineData(@"A is 2^6000 + 1, B is 2^6000 - 1, X is A * B, X =:= 2^12000 - 1")]
    // One block on both sides takes the square kernel; the value is checked by
    // the general product of a copy that is a different block.
    [InlineData(@"A is 3^7000, X is A * A, B is A + 0, C is B + 1, Y is A * (C - 1), X =:= Y, Z is X // A, Z =:= A")]
    [InlineData(@"A is -(3^7000), X is A * A, X > 0, Y is -A, Z is Y * Y, X =:= Z")]
    [InlineData(@"A is 3^7000, X is -A * A, X < 0, Y is A * -A, Y =:= X, Z is (-A) * (-A), Z > 0, Z =:= -X")]
    [InlineData(@"A is 2^70 + 1, X is A * A, X =:= 2^140 + 2^71 + 1")]
    [InlineData(@"A is 2^6000 + 1, X is A * A, X =:= 2^12000 + 2^6001 + 1, Y is -A * A, Y =:= -X")]
    [InlineData(@"A is 2^6000 + 1, B is 2^6000 - 1, X is (3 * A) * (5 * B), X =:= 15 * (2^12000 - 1)")]
    [InlineData(@"A is 2^6000 + 1, B is 2^70 + 1, X is A * B, X =:= 2^6070 + 2^6000 + 2^70 + 1")]
    // The long lane of ^, and its overflow hand-off.
    [InlineData(@"X is 7^20, X =:= 79792266297612001")]
    [InlineData(@"X is (-3)^3, X == -27, Y is (-3)^4, Y == 81")]
    [InlineData(@"X is 2^62, X =:= 4611686018427387904")]
    [InlineData(@"X is 2^63, X =:= 9223372036854775808, Y is 3^40, Y =:= 12157665459056928801")]
    [InlineData(@"X is 0^0, X == 1, Y is 0^5, Y == 0, Z is (-1)^64, Z == 1, W is (-1)^63, W == -1")]
    [InlineData(@"X is 10^18, X =:= 1000000000000000000, Y is 10^19, Y =:= 10000000000000000000")]
    [InlineData(@"X is (-2)^63, X =:= -9223372036854775808, Y is (-2)^64, Y =:= 2^64")]
    public void ArithmeticAndTermOperationsHold(string goal)
    {
        var e = new PrologEngine();
        Assert.True(e.Query(goal + ".").Success, goal);
    }
}

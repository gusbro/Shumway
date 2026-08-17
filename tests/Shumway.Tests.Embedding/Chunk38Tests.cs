using System.Numerics;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 38: BigInteger arithmetic via the existing <c>Tag.BigInt</c>
/// side-table, source-level BigInteger literals via the new
/// <c>get_bigint</c> / <c>put_bigint</c> / <c>unify_bigint</c> opcodes
/// (see ADR-013), plus an optional pre-compiled bytecode payload per bundle
/// entry. The bytecode payload is opaque to the runtime today — Phase 2 will
/// wire it into <see cref="PrologEngine.SetupQuery"/> to skip re-compilation
/// on consult — but the codec must already round-trip cleanly.
/// </summary>
public class Chunk38Tests
{
    private static Term Int(long v) => new IntTerm(v);
    private static Term Big(BigInteger v) => new BigIntTerm(v);

    private const string TenToThe24 = "1000000 * 1000000 * 1000000 * 1000000";   // 10^24
    private const string TenToThe18 = "(1000000 * 1000000) * 1000000";           // 10^18
    private const string TenToThe12 = "1000000 * 1000000";                       // 10^12

    // ============================================================================
    // BigInteger arithmetic
    // ============================================================================

    [Fact]
    public void BigInt_Multiply_OverflowsToBigInt()
    {
        // 10^24 overflows long but lives happily in BigInteger.
        var engine = new PrologEngine();
        var sol = engine.Query($"X is {TenToThe24}.");
        Assert.True(sol.Success);
        var expected = BigInteger.Pow(10, 24);
        Assert.Equal(Big(expected), sol["X"]);
    }

    [Fact]
    public void BigInt_Add_OverflowsToBigInt()
    {
        // 10^18 + 10^18 = 2 * 10^18, which is between long.MaxValue (~9.22e18)
        // and 10^19 — actually fits in long, so use 10^18 + 10^18 + 10^18 = 3e18
        // (also fits long). Build something that overflows: 10^18 * 10 = 10^19.
        var engine = new PrologEngine();
        var sol = engine.Query($"X is ({TenToThe18}) * 10.");
        Assert.True(sol.Success);
        Assert.Equal(Big(BigInteger.Pow(10, 19)), sol["X"]);
    }

    [Fact]
    public void BigInt_Negate_FlipsBigIntSign()
    {
        var engine = new PrologEngine();
        var sol = engine.Query($"X is {TenToThe24}, Y is -X.");
        Assert.True(sol.Success);
        Assert.Equal(Big(-BigInteger.Pow(10, 24)), sol["Y"]);
    }

    [Fact]
    public void BigInt_Subtract_OverflowsToBigInt()
    {
        var engine = new PrologEngine();
        var sol = engine.Query($"X is 0 - {TenToThe24}.");
        Assert.True(sol.Success);
        Assert.Equal(Big(-BigInteger.Pow(10, 24)), sol["X"]);
    }

    [Fact]
    public void BigInt_CollapsesToInlineWhenResultFits()
    {
        // 10^24 // 10^18 = 10^6, well within the inline 60-bit range — the
        // result must come back as a plain IntTerm, not a BigIntTerm.
        var engine = new PrologEngine();
        var sol = engine.Query($"X is ({TenToThe24}) // ({TenToThe18}).");
        Assert.True(sol.Success);
        Assert.Equal(Int(1_000_000L), sol["X"]);
    }

    [Fact]
    public void BigInt_TypeChecksMatchInteger()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query($"X is {TenToThe24}, integer(X).").Success);
        Assert.True(engine.Query($"X is {TenToThe24}, number(X).").Success);
        Assert.False(engine.Query($"X is {TenToThe24}, float(X).").Success);
    }

    [Fact]
    public void BigInt_ComparesAcrossInlineAndBig()
    {
        var engine = new PrologEngine();
        Assert.True(engine.Query($"X is {TenToThe24}, X > 5.").Success);
        Assert.True(engine.Query($"X is 0 - ({TenToThe24}), X < 5.").Success);
        // Big vs big.
        Assert.True(engine.Query(
            $"X is {TenToThe24}, Y is ({TenToThe18}) * 1000, X > Y.").Success);
    }

    [Fact]
    public void BigInt_MixedWithFloatPromotesToFloat()
    {
        var engine = new PrologEngine();
        var sol = engine.Query($"X is ({TenToThe24}) * 0.5.");
        Assert.True(sol.Success);
        var f = Assert.IsType<FloatTerm>(sol["X"]);
        Assert.Equal(0.5 * 1e24, f.Value, 3);
    }

    [Fact]
    public void BigInt_Unify_RoundTripsThroughVar()
    {
        var engine = new PrologEngine();
        // The materialiser sends BigInts that fit the 60-bit range back as
        // plain ints; values outside it stay as BigIntTerm.
        var inside = engine.Query("X is 1000.");
        Assert.Equal(Int(1000), inside["X"]);

        var outside = engine.Query($"X is {TenToThe24}.");
        Assert.Equal(Big(BigInteger.Pow(10, 24)), outside["X"]);
    }

    [Fact]
    public void BigInt_IntegerDivAndMod_StayOnBigPath()
    {
        var engine = new PrologEngine();
        var q = engine.Query($"X is ({TenToThe24}) // 7, Y is ({TenToThe24}) mod 7.");
        Assert.True(q.Success);
        var expectedQ = BigInteger.Pow(10, 24) / 7;
        var expectedR = BigInteger.Pow(10, 24) % 7;
        Assert.Equal(expectedQ, ToBig(q["X"]!));
        Assert.Equal(expectedR, ToBig(q["Y"]!));
    }

    // ============================================================================
    // Source-level BigInteger literals (ADR-013: get_bigint / put_bigint / unify_bigint)
    // ============================================================================

    [Fact]
    public void BigIntLiteral_InQuery_UnifiesWithVar()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X = 1000000000000000000000.");   // 10^21
        Assert.True(sol.Success);
        Assert.Equal(Big(BigInteger.Pow(10, 21)), sol["X"]);
    }

    [Fact]
    public void BigIntLiteral_InArithmetic_RoundTrips()
    {
        var engine = new PrologEngine();
        var sol = engine.Query("X is 1000000000000000000000 + 1.");
        Assert.True(sol.Success);
        Assert.Equal(Big(BigInteger.Pow(10, 21) + 1), sol["X"]);
    }

    [Fact]
    public void BigIntLiteral_NegativeLiteralCollapse()
    {
        // The parser's neg-literal collapse also handles BigInt-sized values.
        var engine = new PrologEngine();
        var sol = engine.Query("X = -1000000000000000000000.");
        Assert.True(sol.Success);
        Assert.Equal(Big(-BigInteger.Pow(10, 21)), sol["X"]);
    }

    [Fact]
    public void BigIntLiteral_LongOutsideInt32_CompilesViaBigIntPool()
    {
        // Values that fit in long but not int32 — exactly the case that used
        // to throw CheckInt32 — must now compile and round-trip.
        // 2^40 = 1099511627776
        var engine = new PrologEngine();
        var sol = engine.Query("X = 1099511627776.");
        Assert.True(sol.Success);
        Assert.Equal(Int(1099511627776L), sol["X"]);
    }

    [Fact]
    public void BigIntLiteral_InFact_RoundTripsThroughHead()
    {
        var engine = new PrologEngine();
        engine.ConsultString("big(1000000000000000000000).");
        var sol = engine.Query("big(X).");
        Assert.True(sol.Success);
        Assert.Equal(Big(BigInteger.Pow(10, 21)), sol["X"]);
    }

    [Fact]
    public void BigIntLiteral_InClauseHead_MatchesOnUnification()
    {
        var engine = new PrologEngine();
        engine.ConsultString("""
            is_huge(1000000000000000000000) :- !, true.
            is_huge(_) :- fail.
            """);
        Assert.True(engine.Query("is_huge(1000000000000000000000).").Success);
        Assert.False(engine.Query("is_huge(42).").Success);
    }

    [Fact]
    public void BigIntLiteral_InCompoundSubArg_RoundTrips()
    {
        // Sub-arg position in a compound exercises the unify_bigint write-mode
        // path — single-cell, so no PreEmitMultiCellLiterals dance.
        var engine = new PrologEngine();
        var sol = engine.Query("X = pair(1000000000000000000000, b).");
        Assert.True(sol.Success);
        var c = Assert.IsType<CompoundTerm>(sol["X"]);
        Assert.Equal("pair", c.Functor);
        Assert.Equal(Big(BigInteger.Pow(10, 21)), c.Args[0]);
    }

    private static BigInteger ToBig(Term t) => t switch
    {
        IntTerm i => new BigInteger(i.Value),
        BigIntTerm b => b.Value,
        _ => throw new InvalidOperationException($"Expected integer-shaped term, got {t.GetType().Name}."),
    };

    // ============================================================================
    // Bundle bytecode persistence
    // ============================================================================

    [Fact]
    public void Bundle_WithoutCompiledBytecode_RoundTripsAsBefore()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", "hello(world)."),
        });

        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: false);
        var loaded = BundleReader.FromBytes(bytes);

        Assert.Single(loaded.Entries);
        Assert.Equal("user", loaded.Entries[0].ModuleName);
        Assert.Equal("hello(world).", loaded.Entries[0].Source);
        Assert.Null(loaded.Entries[0].CompiledBytecode);
    }

    [Fact]
    public void Bundle_WithCompiledBytecode_EmbedsCodecOutput()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", "fact1(a). fact1(b). fact1(c)."),
        });

        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);
        var loaded = BundleReader.FromBytes(bytes);

        Assert.Single(loaded.Entries);
        Assert.NotNull(loaded.Entries[0].CompiledBytecode);
        // The blob starts with the codec's magic so the reader can sniff it.
        var blob = loaded.Entries[0].CompiledBytecode!;
        Assert.Equal((byte)'S', blob[0]);
        Assert.Equal((byte)'M', blob[1]);
        Assert.Equal((byte)'C', blob[2]);
        Assert.Equal((byte)'M', blob[3]);
    }

    [Fact]
    public void Bundle_CompiledBytecode_DecodesBackToMatchingModule()
    {
        var clauses = new ClauseReader(
            new Lexer("p(a). p(b). q(X) :- p(X)."),
            OperatorTable.Default()).ReadAll()
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        var original = new ModuleCompiler().Compile(clauses);

        byte[] encoded = CompiledModuleCodec.Encode(original);
        var decoded = CompiledModuleCodec.Decode(encoded);

        Assert.Equal(original.Predicates.Count, decoded.Predicates.Count);
        for (int i = 0; i < original.Predicates.Count; i++)
        {
            Assert.Equal(original.Predicates[i].Bytecode, decoded.Predicates[i].Bytecode);
            Assert.Equal(original.Predicates[i].Arity, decoded.Predicates[i].Arity);
            Assert.Equal(original.Predicates[i].ClauseCount, decoded.Predicates[i].ClauseCount);
            Assert.Equal(original.Predicates[i].FunctorId, decoded.Predicates[i].FunctorId);
        }
        Assert.Equal(original.StringLiterals, decoded.StringLiterals);
        Assert.Equal(original.FloatLiterals, decoded.FloatLiterals);
    }

    [Fact]
    public void Bundle_CompiledBytecode_PreservesCallSitesAndDispatch()
    {
        var clauses = new ClauseReader(
            new Lexer("foo(1). foo(2). foo(3). bar(X) :- foo(X)."),
            OperatorTable.Default()).ReadAll()
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        var original = new ModuleCompiler().Compile(clauses);

        byte[] encoded = CompiledModuleCodec.Encode(original);
        var decoded = CompiledModuleCodec.Decode(encoded);

        for (int i = 0; i < original.Predicates.Count; i++)
        {
            var op = original.Predicates[i];
            var dp = decoded.Predicates[i];
            Assert.Equal(op.CallSites.Count, dp.CallSites.Count);
            for (int s = 0; s < op.CallSites.Count; s++)
            {
                Assert.Equal(op.CallSites[s].OpcodeOffset, dp.CallSites[s].OpcodeOffset);
                Assert.Equal(op.CallSites[s].CalleeFunctorId, dp.CallSites[s].CalleeFunctorId);
                Assert.Equal(op.CallSites[s].IsExecute, dp.CallSites[s].IsExecute);
            }
            Assert.Equal(op.DispatchSites, dp.DispatchSites);
            Assert.Equal(op.SwitchTableIdSites, dp.SwitchTableIdSites);
        }
    }

    [Fact]
    public void Bundle_CompiledBytecode_PreservesSwitchTables()
    {
        var clauses = new ClauseReader(
            new Lexer("col(red). col(green). col(blue). col(yellow)."),
            OperatorTable.Default()).ReadAll()
            .Where(c => c.Kind != ClauseKind.Directive)
            .ToList();
        var original = new ModuleCompiler().Compile(clauses);

        byte[] encoded = CompiledModuleCodec.Encode(original);
        var decoded = CompiledModuleCodec.Decode(encoded);

        for (int i = 0; i < original.Predicates.Count; i++)
        {
            var op = original.Predicates[i];
            var dp = decoded.Predicates[i];
            Assert.Equal(op.SwitchTables.Count, dp.SwitchTables.Count);
            for (int t = 0; t < op.SwitchTables.Count; t++)
            {
                Assert.Equal(op.SwitchTables[t].Count, dp.SwitchTables[t].Count);
                Assert.Equal(op.SwitchTables[t].DefaultAddress, dp.SwitchTables[t].DefaultAddress);
                Assert.Equal(op.SwitchTables[t].Keys, dp.SwitchTables[t].Keys);
                Assert.Equal(op.SwitchTables[t].Values, dp.SwitchTables[t].Values);
            }
        }
    }

    [Fact]
    public void Bundle_CompiledBytecode_RejectsWrongMagic()
    {
        byte[] bad = new byte[] { 0, 0, 0, 0, 1, 0, 0, 0 };
        Assert.Throws<InvalidDataException>(() => CompiledModuleCodec.Decode(bad));
    }

    [Fact]
    public void Bundle_LoadIgnoresCompiledBlobInPhase1()
    {
        var bundle = new Bundle(new[]
        {
            new BundleEntry("user", ":- public greet/1.\ngreet(world)."),
        });

        byte[] withBlob = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);
        byte[] without = BundleWriter.ToBytes(bundle, includeCompiledBytecode: false);

        var engineA = new PrologEngine();
        engineA.LoadBundle(BundleReader.FromBytes(withBlob));
        Assert.True(engineA.Query("greet(world).").Success);

        var engineB = new PrologEngine();
        engineB.LoadBundle(BundleReader.FromBytes(without));
        Assert.True(engineB.Query("greet(world).").Success);
    }
}

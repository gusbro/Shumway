using System.Reflection;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Il;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 71 — compiled IL bundles via
/// <see cref="System.Reflection.Emit.PersistedAssemblyBuilder"/>. The
/// bundler can emit a .NET assembly (.dll bytes) holding pre-compiled
/// IL for every IL-eligible predicate; <c>LoadBundle</c> loads the
/// assembly and binds each method as a <c>PredicateDelegate</c>, so
/// the engine skips the runtime Sigil emit step entirely.
///
/// <para>The chunk-71 MVP covers the single-clause-leaf shape (the
/// same shape <see cref="PersistedIlBuilder.CanPersist"/> filters
/// on). Multi-clause / meta-CP shapes still fall back to chunk 45's
/// load-time Sigil path; they're a follow-up extension.</para>
/// </summary>
public class Chunk71Tests
{
    private static int Fid(string name, int arity) =>
        FunctorTable.Intern(AtomTable.Intern(name, permanent: true).Id, arity);

    [Fact]
    public void PersistedIl_RoundTripsSimpleFact()
    {
        // Direct PersistedIlBuilder call: compile foo., emit the .dll,
        // re-load via Assembly.Load(bytes), bind the method as a
        // PredicateDelegate, and invoke it.
        var clauses = new ClauseReader("foo.").ReadAll().ToList();
        var pred = new PredicateCompiler().Compile(clauses);
        var predicates = new Dictionary<int, CompiledPredicate>
        {
            { pred.FunctorId, pred }
        };
        var (dllBytes, entries) = PersistedIlBuilder.Build("Chunk71Asm_foo", predicates);
        Assert.True(dllBytes.Length > 0);
        Assert.Single(entries);

        var asm = Assembly.Load(dllBytes);
        var type = asm.GetType(PersistedIlBuilder.TypeName);
        Assert.NotNull(type);
        var method = type!.GetMethod(entries[0].MethodName);
        Assert.NotNull(method);
        var del = method!.CreateDelegate<PredicateDelegate>();
        var engine = new Engine();
        Assert.True(del(engine, 0));
    }

    [Fact]
    public void CanPersist_AcceptsLeafSingleClause()
    {
        var clauses = new ClauseReader("foo.").ReadAll().ToList();
        var pred = new PredicateCompiler().Compile(clauses);
        Assert.True(PersistedIlBuilder.CanPersist(pred));
    }

    [Fact]
    public void CanPersist_RejectsMultiClause()
    {
        // Multi-clause predicates need self-referential IL CPs which
        // the MVP doesn't support yet.
        var clauses = new ClauseReader("foo. foo.").ReadAll().ToList();
        var pred = new PredicateCompiler().Compile(clauses);
        Assert.False(PersistedIlBuilder.CanPersist(pred));
    }

    [Fact]
    public void Bundle_WithCompiledIl_LoadsAndExecutesPersisted()
    {
        // End-to-end: build a Bundle from source, write it with
        // includeCompiledIl: true, decode it, LoadBundle it, query the
        // predicate, and verify IlPromotion already has it bound.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("greet", ":- public hello/0.\nhello.")
        });

        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var roundtripped = BundleReader.FromBytes(bytes);
        Assert.NotNull(roundtripped.Entries[0].CompiledIl);
        Assert.True(roundtripped.Entries[0].CompiledIl!.Length > 0);

        var engine = new PrologEngine();
        engine.LoadBundle(roundtripped);
        Assert.True(engine.IlPromotion.IsPromoted(Fid("hello", 0)));
        Assert.True(engine.Query("hello.").Success);
    }

    [Fact]
    public void Bundle_WithCompiledIl_HandlesMixedPredicates()
    {
        // A source with both persistable (single-clause leaf) and
        // non-persistable (multi-clause) predicates. The .dll holds
        // only the persistable ones; the rest still IL-warm from the
        // bytecode blob via chunk-45.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("mixed",
                ":- public solo/0.\n" +
                ":- public many/1.\n" +
                "solo.\n" +
                "many(a).\n" +
                "many(b).\n")
        });

        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var roundtripped = BundleReader.FromBytes(bytes);
        var engine = new PrologEngine();
        engine.LoadBundle(roundtripped);
        // Both predicates promoted, regardless of which path.
        Assert.True(engine.IlPromotion.IsPromoted(Fid("solo", 0)));
        Assert.True(engine.IlPromotion.IsPromoted(Fid("many", 1)));
        // Both still answer correctly.
        Assert.True(engine.Query("solo.").Success);
        Assert.Equal(2, engine.QueryAll("many(_).").Count());
    }

    [Fact]
    public void Bundle_WithoutCompiledIl_StillWorks()
    {
        // Backwards compat: bundle without --with-compiled-il loads
        // exactly like before, via the chunk-45 IL warm path.
        var bundle = new Bundle(new[]
        {
            new BundleEntry("plain", ":- public hi/0.\nhi.")
        });
        byte[] bytes = BundleWriter.ToBytes(bundle, includeCompiledBytecode: true);
        var roundtripped = BundleReader.FromBytes(bytes);
        Assert.Null(roundtripped.Entries[0].CompiledIl);
        var engine = new PrologEngine();
        engine.LoadBundle(roundtripped);
        Assert.True(engine.Query("hi.").Success);
    }

    [Fact]
    public void PersistedIl_AnswersSameAsRuntimeIl()
    {
        // Invariance: a persisted-IL bundle and a runtime-emitted IL
        // engine must produce the same answers.
        const string src = ":- public ok/1.\nok(blue).\n";

        // Persisted path.
        var bundle = new Bundle(new[] { new BundleEntry("inv", src) });
        var rt = BundleReader.FromBytes(BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true));
        var persistedEngine = new PrologEngine();
        persistedEngine.LoadBundle(rt);

        // Runtime path.
        var runtimeEngine = new PrologEngine();
        runtimeEngine.IlPromotion.Threshold = 1;
        runtimeEngine.ConsultString(src);
        runtimeEngine.Query("ok(blue).");

        // Both engines yield the same answers.
        Assert.True(persistedEngine.Query("ok(blue).").Success);
        Assert.True(runtimeEngine.Query("ok(blue).").Success);
        Assert.False(persistedEngine.Query("ok(red).").Success);
        Assert.False(runtimeEngine.Query("ok(red).").Success);
    }

    [Fact]
    public void PersistedIl_HeadMatchOnAtomArg_StillCorrect()
    {
        // The persisted IL must handle get_atom against an argument
        // register (chunk 8's head-match opcodes are part of every
        // single-clause-leaf shape).
        var bundle = new Bundle(new[]
        {
            new BundleEntry("check",
                ":- public check/1.\ncheck(ok).")
        });
        var rt = BundleReader.FromBytes(BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true));
        var engine = new PrologEngine();
        engine.LoadBundle(rt);
        Assert.True(engine.Query("check(ok).").Success);
        Assert.False(engine.Query("check(nope).").Success);
    }
}

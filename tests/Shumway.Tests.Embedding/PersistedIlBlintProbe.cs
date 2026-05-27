using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// In-process repro: build a persisted-IL bundle in one engine, then
/// LoadBundle in a fresh engine, all within the same .NET process.
/// If the persisted IL works in-process but fails cross-process,
/// the bug is functor-id drift between build and run. If it fails
/// in-process too, the bug is in the IL emit itself.
/// </summary>
public class PersistedIlBlintProbe
{
    [Fact]
    public void RetractallPersisted_InProcess()
    {
        const string src =
            ":- public test/0.\n"
            + ":- dynamic fact/1.\n"
            + "test :-\n"
            + "    assertz(fact(a)),\n"
            + "    assertz(fact(b)),\n"
            + "    retractall(fact(_)),\n"
            + "    ( fact(_) -> X = failed ; X = ok ),\n"
            + "    X = ok.\n";

        var bundle = new Bundle(new[] { new BundleEntry("retra", src) });
        // ToBytes builds IL via a sub-engine, all in this process.
        byte[] bytes = BundleWriter.ToBytes(bundle,
            includeCompiledBytecode: true, includeCompiledIl: true);
        var rt = BundleReader.FromBytes(bytes);

        var engine = new PrologEngine();
        engine.LoadBundle(rt);
        Assert.True(engine.Query("test.").Success);
    }
}

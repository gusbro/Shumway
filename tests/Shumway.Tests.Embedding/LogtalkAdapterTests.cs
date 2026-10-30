using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>Hooks of the Logtalk adapter shipped in <c>logtalk/adapters/shumway.pl</c>,
/// consulted on their own. <c>logtalk_load_context/2</c>, which Logtalk's
/// compiler provides, is stubbed with the entity being compiled.</summary>
public class LogtalkAdapterTests
{
    private static string AdapterPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
            dir = dir.Parent!;
        return Path.Combine(dir!.FullName, "logtalk", "adapters", "shumway.pl").Replace("\\", "/");
    }

    private static PrologEngine Adapter(string entityType)
    {
        var engine = new PrologEngine();
        engine.ConsultFile(AdapterPath());
        engine.ConsultString($"logtalk_load_context(entity_type, {entityType}).");
        return engine;
    }

    // A clause for a predicate the engine has as a builtin is an error and is
    // ignored, and a directive the engine lacks only warns: either way the
    // adapter claims something the engine does not do. Loading reports nothing.
    [Fact]
    public void TheAdapterLoadsWithoutErrorsOrWarnings()
    {
        var engine = new PrologEngine();
        var warnings = new StringWriter();
        engine.Warnings = warnings;
        engine.ConsultFile(AdapterPath());
        Assert.Equal("", warnings.ToString());
    }

    // logtalk_make/0-1 reloads the files whose time changed since they were
    // loaded; a constant time made it reload nothing.
    [Fact]
    public void TheFileModificationTimeIsTheFilesOwn()
    {
        string file = Path.Combine(Path.GetTempPath(), "lgt_mtime_" + Guid.NewGuid().ToString("N") + ".pl");
        File.WriteAllText(file, "a.\n");
        try
        {
            var engine = Adapter("object");
            string path = file.Replace("\\", "/");

            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            Assert.True(engine.Query($"'$lgt_file_modification_time'('{path}', T), T =:= 1577934245.").Success);
            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 2, 3, 4, 7, DateTimeKind.Utc));
            Assert.True(engine.Query($"'$lgt_file_modification_time'('{path}', T), T =:= 1577934247.").Success);
        }
        finally { File.Delete(file); }
    }

    // A module Logtalk compiles as an object names other modules compiled as
    // objects; the core turns its use_module/2 into uses/2 on them. Routing the
    // imports to `user` sent `user::contained/2` instead of `lists::contained/2`.
    [Fact]
    public void UseModuleInAModuleCompiledAsAnObjectIsLeftToTheCore()
    {
        var engine = Adapter("module");

        Assert.False(engine.Query(
            @"'$lgt_prolog_term_expansion'((:- use_module(lists_object, [contained/2])), _).").Success);
    }

    [Fact]
    public void UseModuleOfAnUnknownModuleInAnObjectStillGoesToUser()
    {
        var engine = Adapter("object");

        Assert.True(engine.Query(
            @"'$lgt_prolog_term_expansion'((:- use_module(no_such_module, [p/1])), E),
              E == (:- uses(user, [p/1])).").Success);
    }

    // Logtalk compiles coinductive predicates, and runs the cyclic-term cases
    // of its tests/prolog battery, only when coinduction is announced. The
    // announcement holds only while these terminate on rational trees.
    [Fact]
    public void CoinductionIsAnnouncedAndCyclicTermsAreHandled()
    {
        var engine = Adapter("object");

        Assert.True(engine.Query(
            @"'$lgt_prolog_feature'(coinduction, supported),
              X = f(X, a), Y = f(Y, a), X = Y, X == Y, compare(=, X, Y),
              L1 = [1, 2|L1], L2 = [1, 2, 1, 2|L2], L1 == L2,
              copy_term(X-V, C-W), C = f(C1, a), C1 = f(_, a), W \== V,
              term_variables(f(X, V), Vs), Vs == [V],
              findall(X, true, [F]), F = f(_, a).").Success);
    }
}

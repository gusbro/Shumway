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
}

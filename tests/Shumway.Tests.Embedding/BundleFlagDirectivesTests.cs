using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>A bundle sets, when it loads, the flags its source set with
/// <c>:- set_prolog_flag/2</c>, as the consult of that source does: a flag
/// such as <c>lenient_escapes</c> governs what the program reads at run time.
/// Issue #259: an executable built from a bundle read <c>ά</c> as a
/// syntax error that the consulted source read fine.</summary>
public sealed class BundleFlagDirectivesTests
{
    // reads/1 reads the text a('ά'), built at run time: the file's own
    // reader does not take lenient escapes while it reads the file.
    private const string Source = @":- set_prolog_flag(lenient_escapes, true).
:- set_prolog_flag(double_quotes, atom).
reads(T) :- atom_codes(A, [97, 40, 39, 92, 117, 48, 51, 65, 67, 39, 41]), term_to_atom(T, A).
";

    private static void SetsTheFlags(PrologEngine e)
    {
        Assert.True(e.Query("current_prolog_flag(lenient_escapes, true).").Success);
        Assert.True(e.Query("reads(T), T = a(X), atom_codes(X, [940]).").Success);
        Assert.True(e.Query(@"X = ""ab"", atom(X).").Success);
    }

    // Not Directory.CreateTempSubdirectory: the suite builds for .NET Framework too.
    private static string NewTempDir(string prefix)
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            prefix + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    private static PrologEngine Load(ShmoObject[] objects)
    {
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = objects,
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));
        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));
        e.PromoteBareBundleModulesToUser();
        return e;
    }

    [Fact]
    public void ACompiledFile_ItsBundleSetsTheFlagsAtLoad()
    {
        var r = ShmoCompiler.TryCompileSource(Source, "flags259c");
        Assert.True(r.Success, string.Join("; ", r.Errors));
        var roundTrip = ShmoReader.FromBytes(ShmoWriter.ToBytes(r.Object!));
        Assert.Equal(new[]
        {
            new ShmoFlagSetting("lenient_escapes", "true"),
            new ShmoFlagSetting("double_quotes", "atom"),
        }, roundTrip.Flags);

        SetsTheFlags(Load(new[] { roundTrip }));
    }

    [Fact]
    public void AFileLinkedThroughAConsult_ItsBundleSetsTheFlagsAtLoad()
    {
        string dir = NewTempDir("shumway-flags259-");
        try
        {
            string root = System.IO.Path.Combine(dir, "flags259.pl");
            System.IO.File.WriteAllText(root, Source);
            var errors = new System.Collections.Generic.List<ShmoCompileError>();
            var objects = ShmoViaConsult.Compile(
                root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
            Assert.Empty(errors);
            var own = Assert.Single(objects, o => o.ModuleName == "flags259");
            Assert.Contains(new ShmoFlagSetting("lenient_escapes", "true"), own.Object.Flags);

            SetsTheFlags(Load(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(objects, o => o.Object))));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    // Under the consult, a flag a goal sets is recorded as well: an
    // initialization goal's, and one a directive sets through a predicate.
    private const string SetByGoals = @":- initialization(set_prolog_flag(lenient_escapes, true)).
:- set_depth.
set_depth :- set_prolog_flag(answer_max_depth, 7).
reads(T) :- atom_codes(A, [97, 40, 39, 92, 117, 48, 51, 65, 67, 39, 41]), term_to_atom(T, A).
";

    [Fact]
    public void AFileLinkedThroughAConsult_ItsBundleSetsTheFlagsItsGoalsSet()
    {
        string dir = NewTempDir("shumway-flags259g-");
        try
        {
            string root = System.IO.Path.Combine(dir, "flags259g.pl");
            System.IO.File.WriteAllText(root, SetByGoals);
            var errors = new System.Collections.Generic.List<ShmoCompileError>();
            var objects = ShmoViaConsult.Compile(
                root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
            Assert.Empty(errors);
            var own = Assert.Single(objects, o => o.ModuleName == "flags259g");
            Assert.Contains(new ShmoFlagSetting("lenient_escapes", "true"), own.Object.Flags);
            Assert.Contains(new ShmoFlagSetting("answer_max_depth", "7"), own.Object.Flags);

            var e = Load(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(objects, o => o.Object)));
            Assert.True(e.Query("current_prolog_flag(lenient_escapes, true).").Success);
            Assert.True(e.Query("reads(T), T = a(X), atom_codes(X, [940]).").Success);
            Assert.True(e.Query("current_prolog_flag(answer_max_depth, 7).").Success);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    // A bundle compiled through the consult ends with the flags the consult of
    // its source ends with. The load sets them in the order the calls ran,
    // which decides the last value: a directive as the consult reads it, an
    // initialization goal once the file is in, whichever comes first in the
    // text. A call the engine refused is reported by the consult, so by the
    // compile, and is not in the bundle: its load is silent.
    [Theory]
    [InlineData(@":- set_prolog_flag(double_quotes, codes).
:- initialization(set_prolog_flag(double_quotes, atom)).
", 0)]
    [InlineData(@":- initialization(set_prolog_flag(double_quotes, atom)).
:- set_prolog_flag(double_quotes, codes).
", 0)]
    [InlineData(@":- set_prolog_flag(no_such_flag, on).
:- set_prolog_flag(unknown, bogus).
", 2)]
    public void ABundleLinkedThroughAConsult_EndsAsTheConsultEnds(string directives, int refused)
    {
        string source = directives + "k259.\n";
        var consultWarnings = new System.IO.StringWriter();
        var consulted = new PrologEngine { Warnings = consultWarnings };
        consulted.ConsultString(source);
        Assert.Equal(refused, Lines(consultWarnings).Length);

        string dir = NewTempDir("shumway-flags259o-");
        try
        {
            string root = System.IO.Path.Combine(dir, "flags259o.pl");
            System.IO.File.WriteAllText(root, source);
            var errors = new System.Collections.Generic.List<ShmoCompileError>();
            var compileWarnings = new System.IO.StringWriter();
            var objects = ShmoViaConsult.Compile(root, System.Array.Empty<string>(),
                ShmoBuildMode.Release, errors, warnings: compileWarnings);
            Assert.Empty(errors);
            Assert.Equal(Balls(consultWarnings), Balls(compileWarnings));
            var link = ShmoLinker.Link(new LinkConfig
            {
                Objects = System.Linq.Enumerable.ToArray(
                    System.Linq.Enumerable.Select(objects, o => o.Object)),
                Library = true,
                AllowUndefined = true,
            });
            Assert.True(link.Success, string.Join("; ", link.Diagnostics));
            var loadWarnings = new System.IO.StringWriter();
            var loaded = new PrologEngine { Warnings = loadWarnings };
            loaded.LoadBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));

            foreach (string flag in new[] { "double_quotes", "unknown" })
            {
                string query = $"current_prolog_flag({flag}, V).";
                Assert.Equal(consulted.Query(query).Bindings["V"].ToString(),
                    loaded.Query(query).Bindings["V"].ToString());
            }
            Assert.Equal("", loadWarnings.ToString());
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    private static string[] Lines(System.IO.StringWriter w) =>
        w.ToString().Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries);

    // The error each warning reports, without what raised it.
    private static string[] Balls(System.IO.StringWriter w) =>
        System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(Lines(w),
            line => line.Substring(line.IndexOf(": error(", System.StringComparison.Ordinal) + 2)));

    // A directive sets a flag as the runtime builtin does: a flag the engine
    // does not know, or a value outside the flag's domain, is reported with the
    // builtin's error, and the load goes on.
    [Fact]
    public void ADirectiveTheEngineRefuses_IsReportedAsTheBuiltinsError()
    {
        var w = new System.IO.StringWriter();
        var e = new PrologEngine { Warnings = w };
        e.ConsultString(@":- set_prolog_flag(no_such_flag, on).
:- call(set_prolog_flag, no_such_flag, on).
:- set_prolog_flag(unknown, bogus).
:- call(set_prolog_flag, unknown, bogus).
:- set_prolog_flag(answer_max_depth, deep).
:- call(set_prolog_flag, answer_max_depth, deep).
k259.
");
        string[] balls = Balls(w);
        // The directives as the consult reads them; the goals once the file is in.
        string[] errors =
        {
            "error(domain_error(prolog_flag, no_such_flag), set_prolog_flag/2)",
            "error(domain_error(flag_value, unknown+bogus), set_prolog_flag/2)",
            "error(type_error(integer, deep), set_prolog_flag/2)",
        };
        Assert.Equal(System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(errors, errors)), balls);
        Assert.True(e.Query("k259.").Success);
        Assert.True(e.Query("current_prolog_flag(unknown, error).").Success);
    }

    // A bundle compiled from the file alone carries every directive, and its
    // load reports one the engine refuses as the consult of the file does.
    [Fact]
    public void ACompiledFile_ItsBundleReportsWhatTheConsultReports()
    {
        const string source = @":- set_prolog_flag(no_such_flag, on).
:- set_prolog_flag(lenient_escapes, true).
k259.
";
        var consultWarnings = new System.IO.StringWriter();
        new PrologEngine { Warnings = consultWarnings }.ConsultString(source);

        var r = ShmoCompiler.TryCompileSource(source, "flags259r");
        Assert.True(r.Success, string.Join("; ", r.Errors));
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { r.Object! },
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));
        var loadWarnings = new System.IO.StringWriter();
        var loaded = new PrologEngine { Warnings = loadWarnings };
        loaded.LoadBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));

        Assert.Single(Lines(consultWarnings));
        Assert.Equal(Balls(consultWarnings), Balls(loadWarnings));
        Assert.True(loaded.Query("current_prolog_flag(lenient_escapes, true).").Success);
    }

    // SWI's own flags, which its library sources set, are accepted without
    // effect while a library loads under the swi dialect; anywhere else they
    // are unknown flags.
    [Fact]
    public void AnSwiLibrary_SetsSwisOwnFlagsWithoutEffect()
    {
        const string flags = @":- set_prolog_flag(generate_debug_info, false).
:- set_prolog_flag(quasi_quotations, true).
:- set_prolog_flag(float_format, '~15g|').
";
        string dir = NewTempDir("shumway-flags259s-");
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "swiflags259.pl"),
                ":- module(swiflags259, [p259/1]).\n" + flags + "p259(1).\n");
            var w = new System.IO.StringWriter();
            var e = new PrologEngine { Warnings = w };
            e.AddLibraryDirectory(dir, "swi");
            e.ConsultString(":- use_module(library(swiflags259)).\n");
            Assert.Equal("", w.ToString());
            Assert.True(e.Query("p259(1).").Success);
            Assert.True(e.Query("catch(current_prolog_flag(generate_debug_info, _), "
                + "error(domain_error(prolog_flag, generate_debug_info), _), true).").Success);

            var plain = new System.IO.StringWriter();
            new PrologEngine { Warnings = plain }.ConsultString(flags);
            Assert.Equal(new[]
            {
                "error(domain_error(prolog_flag, generate_debug_info), set_prolog_flag/2)",
                "error(domain_error(prolog_flag, quasi_quotations), set_prolog_flag/2)",
                "error(domain_error(prolog_flag, float_format), set_prolog_flag/2)",
            }, Balls(plain));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    // A dialect library's load restores double_quotes and the reader's flags
    // when it ends, so what it sets of them is not the bundle's to carry; any
    // other flag it sets stays set, after the consult and after the load.
    [Fact]
    public void ADialectLibrary_ItsBundleSetsOnlyTheFlagsItsLoadLeaves()
    {
        string lib = NewTempDir("shumway-flags259d-");
        string dir = NewTempDir("shumway-flags259e-");
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(lib, "swidq259.pl"), @":- module(swidq259, [p259/1]).
:- set_prolog_flag(double_quotes, codes).
:- set_prolog_flag(answer_max_depth, 9).
p259(1).
");
            const string root = @":- use_module(library(swidq259)).
k259.
";
            var consulted = new PrologEngine();
            consulted.AddLibraryDirectory(lib, "swi");
            consulted.ConsultString(root);

            string rootPath = System.IO.Path.Combine(dir, "flags259e.pl");
            System.IO.File.WriteAllText(rootPath, root);
            var errors = new System.Collections.Generic.List<ShmoCompileError>();
            var objects = ShmoViaConsult.Compile(rootPath, new[] { "swi:" + lib },
                ShmoBuildMode.Release, errors, warnings: System.IO.TextWriter.Null);
            Assert.Empty(errors);
            var flags = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.SelectMany(
                objects, o => o.Object.Flags));
            Assert.Equal(new[] { new ShmoFlagSetting("answer_max_depth", "9") }, flags);
            var loaded = Load(System.Linq.Enumerable.ToArray(
                System.Linq.Enumerable.Select(objects, o => o.Object)));

            Assert.True(consulted.Query("current_prolog_flag(double_quotes, chars).").Success);
            foreach (string flag in new[] { "double_quotes", "answer_max_depth" })
            {
                string query = $"current_prolog_flag({flag}, V).";
                Assert.Equal(consulted.Query(query).Bindings["V"].ToString(),
                    loaded.Query(query).Bindings["V"].ToString());
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(lib, recursive: true); } catch (System.IO.IOException) { }
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    [Fact]
    public void ABundlesEntryKeepsItsFlagsThroughTheFile()
    {
        var r = ShmoCompiler.TryCompileSource(Source, "flags259b");
        Assert.True(r.Success, string.Join("; ", r.Errors));
        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { r.Object! },
            Library = true,
            AllowUndefined = true,
        });
        Assert.True(link.Success, string.Join("; ", link.Diagnostics));
        var bundle = BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!));
        var entry = Assert.Single(bundle.Entries, x => x.ModuleName == "flags259b");
        Assert.Equal(new[]
        {
            new ShmoFlagSetting("lenient_escapes", "true"),
            new ShmoFlagSetting("double_quotes", "atom"),
        }, entry.Flags);
    }

    // Guards the tests above: an engine that has not loaded the bundle reads
    // strictly, so it is the load that changed the flags.
    [Fact]
    public void WithoutTheBundle_TheEngineReadsStrictly()
    {
        var e = new PrologEngine();
        e.ConsultString("reads(T) :- atom_codes(A, [97, 40, 39, 92, 117, 48, 51, 65, 67, 39, 41]), term_to_atom(T, A).\n");
        Assert.True(e.Query("current_prolog_flag(lenient_escapes, false).").Success);
        Assert.False(e.Query("current_prolog_flag(answer_max_depth, 7).").Success);
        Assert.True(e.Query("catch(reads(_), error(syntax_error(_), _), true).").Success);
        Assert.False(e.Query(@"X = ""ab"", atom(X).").Success);
    }
}

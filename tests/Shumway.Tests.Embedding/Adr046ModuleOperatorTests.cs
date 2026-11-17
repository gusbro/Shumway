using System.Linq;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// ADR-046 — module-scoped operator tables. Each module's ops live in a
/// layer over the <c>user</c> table; <c>op(P,T,user:N)</c> escapes to
/// global; export-list <c>op/3</c> entries install into importers;
/// module-less text keeps exact ISO (user-table) behaviour.
/// </summary>
public class Adr046ModuleOperatorTests
{
    [Fact]
    public void ModuleOp_DoesNotLeakToUserText()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m46a, []).\n"
            + ":- op(700, xfx, ~~~>).\n"
            + "seen(X) :- X = (a ~~~> b).\n");
        // The module's own clause parsed with the op…
        Assert.True(e.Query("m46a:seen('~~~>'(a, b)).").Success);
        // …but user-level reading does not see it.
        Assert.False(e.Query("current_op(700, xfx, '~~~>').").Success);
        Assert.False(e.Query(
            "catch(atom_to_term('a ~~~> b', _, _), _, fail).").Success);
    }

    [Fact]
    public void AnOperatorErrorInsideAModuleNamesTheBuiltinTheProgramCalled()
    {
        // op/3 and current_op/3 in module code run as module-aware
        // rewrites; their errors still name op/3 and current_op/3 (Scryer's
        // conformity suite catches error(_, op/3)).
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m46e, []).\n"
            + "bad_op :- op(1000, xfy, ',').\n"
            + "bad_current :- current_op(1201, xfx, _).\n");
        Assert.True(e.Query(
            "catch((m46e:bad_op, fail), "
            + "error(permission_error(modify, operator, ','), op/3), true).").Success);
        Assert.True(e.Query(
            "catch((m46e:bad_current, fail), "
            + "error(domain_error(operator_priority, 1201), current_op/3), true).").Success);
    }

    [Fact]
    public void UserQualifiedOp_IsGlobalFromInsideAModule()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m46b, []).\n"
            + ":- op(700, xfx, user:(==>>)).\n");
        Assert.True(e.Query("current_op(700, xfx, '==>>').").Success);
        Assert.True(e.Query("atom_to_term('a ==>> b', T, _), T = '==>>'(a, b).").Success);
    }

    [Fact]
    public void ExportedOp_InstallsIntoImporter_AndIntoUserOnTopLevelImport()
    {
        string dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "shumway-adr046-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "oplib46.pl"),
                ":- module(oplib46, [mk/2, op(700, xfx, +->)]).\n"
                + "mk(A +-> B, pair(A, B)).\n");
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir);
            // Mid-consult import: the op is active for the rest of the file.
            e.ConsultString(
                ":- use_module(library(oplib46)).\n"
                + "route(R) :- mk(a +-> b, R).\n");
            Assert.True(e.Query("route(pair(a, b)).").Success);
            // Top-level import put it in user (SWI behaviour).
            Assert.True(e.Query("current_op(700, xfx, '+->').").Success);
        }
        finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void LocalRemoval_TombstonesTheInheritedOp_ForTheModuleOnly()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m46c, []).\n"
            + ":- op(0, xfx, ==).\n"
            + "probe(ok) :- \\+ current_op(_, xfx, ==).\n");
        // Inside the module, == is gone (tombstone hides the user def)…
        Assert.True(e.Query("m46c:probe(ok).").Success);
        // …outside it is untouched.
        Assert.True(e.Query("current_op(700, xfx, ==).").Success);
    }

    [Fact]
    public void RuntimeOpAndCurrentOp_UseTheModuleContext()
    {
        var e = new PrologEngine();
        e.ConsultString(
            ":- module(m46d, []).\n"
            + ":- public def46/0, see46/0.\n"
            + "def46 :- op(650, xfx, rtx46).\n"
            + "see46 :- current_op(650, xfx, rtx46), current_op(700, xfx, =).\n");
        Assert.True(e.Query("def46.").Success);
        // The module's view has it (plus the inherited user ops)…
        Assert.True(e.Query("see46.").Success);
        // …the user view does not.
        Assert.False(e.Query("current_op(650, xfx, rtx46).").Success);
    }

    [Fact]
    public void BareText_KeepsIsoGlobalBehaviour()
    {
        var e = new PrologEngine();
        e.ConsultString(":- op(700, xfx, iso46op).\n");
        Assert.True(e.Query("current_op(700, xfx, iso46op).").Success);
        Assert.True(e.Query("atom_to_term('a iso46op b', T, _), T = iso46op(a, b).").Success);
    }

    // A program that declares an operator while it runs and then reads with
    // it, as Logtalk's compiler does for an entity's `uses` of an operator.
    private const string DeclaresThenReads =
        "declare46 :- op(700, xfx, zz46).\n"
        + "sees46(P-T) :- current_op(P, T, zz46).\n"
        + "reads46(X) :- term_to_atom(X, 'a zz46 b').\n";

    private const string DeclaresThenReadsQuery =
        "declare46, sees46(700-xfx), reads46(zz46(a, b)), current_op(700, xfx, zz46).";

    [Fact]
    public void RuntimeOp_InAModule1Text_IsUsers_SoItsOwnReadsSeeIt()
    {
        var e = new PrologEngine();
        e.ConsultString(":- module(m46f).\n:- public declare46/0, sees46/1, reads46/1.\n"
            + DeclaresThenReads);

        Assert.True(e.Query(DeclaresThenReadsQuery).Success);
    }

    [Fact]
    public void RuntimeOp_InABareFileLinkedThroughAConsult_IsUsers_SoItsOwnReadsSeeIt()
    {
        // The link gives the bare file a module named after it, as it does
        // Logtalk's logtalk.pl; its text is still bare.
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "shumway-op46-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            string root = System.IO.Path.Combine(dir, "ops46.pl");
            System.IO.File.WriteAllText(root, DeclaresThenReads);
            var errors = new System.Collections.Generic.List<ShmoCompileError>();
            var objects = ShmoViaConsult.Compile(
                root, System.Array.Empty<string>(), ShmoBuildMode.Release, errors);
            Assert.Empty(errors);
            Assert.Contains(objects, o => o.ModuleName == "ops46");
            var link = ShmoLinker.Link(new LinkConfig
            {
                Objects = objects.Select(o => o.Object).ToArray(),
                Library = true,
                AllowUndefined = true,
            });
            Assert.True(link.Success, string.Join("; ", link.Diagnostics));

            var e = new PrologEngine();
            e.LoadBundle(BundleReader.FromBytes(BundleWriter.ToBytes(link.Bundle!)));
            e.PromoteBareBundleModulesToUser();

            Assert.True(e.Query(DeclaresThenReadsQuery).Success);
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RuntimeOp_InAModule2Text_StaysInTheModule_WhichItsCurrentOpSees()
    {
        var e = new PrologEngine();
        e.ConsultString(":- module(m46g, [declare46/0, sees46/1]).\n" + DeclaresThenReads);

        Assert.True(e.Query("m46g:declare46, m46g:sees46(700-xfx).").Success);
        Assert.False(e.Query("current_op(_, _, zz46).").Success);
    }

    [Fact]
    public void Bundle_ScopesPrivateOps_AndReAdvertisesExportedOnes()
    {
        var r = ShmoCompiler.TryCompileSource(
            ":- module(oplibz, [mk/2, op(700, xfx, +=>)]).\n"
            + ":- op(600, xfx, privz).\n"
            + "mk(A +=> B, pair(A, B)).\n"
            + "secret(1 privz 2).\n",
            "oplibz");
        Assert.True(r.Success, string.Join("; ", r.Errors));
        // Export marker survives the .shmo round-trip ('*' type suffix).
        var rt = ShmoReader.FromBytes(ShmoWriter.ToBytes(r.Object!));
        Assert.Contains(rt.Operators, o => o.Name == "+=>" && o.Type.EndsWith("*"));
        Assert.Contains(rt.Operators, o => o.Name == "privz" && !o.Type.EndsWith("*"));

        var link = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { rt },
            EntryPoints = new[] { new PredicateRef("mk", 2) },
        });
        Assert.True(link.Success);
        var bytes = BundleWriter.ToBytes(link.Bundle!);

        var e = new PrologEngine();
        e.LoadBundle(BundleReader.FromBytes(bytes));
        // Neither op leaks to user at load…
        Assert.False(e.Query("current_op(700, xfx, '+=>').").Success);
        Assert.False(e.Query("current_op(600, xfx, privz).").Success);
        // …a use_module of the loaded module imports the exported one only.
        Assert.True(e.Query("use_module(library(oplibz)).").Success);
        Assert.True(e.Query("current_op(700, xfx, '+=>').").Success);
        Assert.False(e.Query("current_op(600, xfx, privz).").Success);
        Assert.True(e.Query("mk(a +=> b, pair(a, b)).").Success);
    }
}

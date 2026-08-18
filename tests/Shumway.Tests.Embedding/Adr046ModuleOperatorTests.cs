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
        Assert.True(e.Query("seen('~~~>'(a, b)).").Success);
        // …but user-level reading does not see it.
        Assert.False(e.Query("current_op(700, xfx, '~~~>').").Success);
        Assert.False(e.Query(
            "catch(atom_to_term('a ~~~> b', _, _), _, fail).").Success);
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
            // Mid-consult import: the op is active for the REST of the file.
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
        Assert.True(e.Query("probe(ok).").Success);
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
        // …a use_module of the LOADED module imports the exported one only.
        Assert.True(e.Query("use_module(library(oplibz)).").Success);
        Assert.True(e.Query("current_op(700, xfx, '+=>').").Success);
        Assert.False(e.Query("current_op(600, xfx, privz).").Success);
        Assert.True(e.Query("mk(a +=> b, pair(a, b)).").Success);
    }
}

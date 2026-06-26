using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// The full --dll build path (dotnet build of the generated class library,
// then loading it in a consumer) is verified manually — it spawns a child
// build and is too heavy for the unit suite, matching the --exe precedent.
// These tests cover the novel, user-chosen logic: the factory namespace /
// class-name inference from the DLL filename.
public class LibraryEmitterTests
{
    [Theory]
    [InlineData("Greeter.dll", "Greeter")]
    [InlineData("greeter.dll", "Greeter")]                 // first letter capitalised
    [InlineData("Acme.Rules.dll", "Acme.Rules")]           // dotted → namespace segments
    [InlineData("acme.rules.dll", "Acme.Rules")]
    [InlineData("my-rules.dll", "My_rules")]               // hyphen → underscore
    [InlineData("123abc.dll", "_123abc")]                  // leading digit guarded
    [InlineData("/tmp/out/Foo.Bar.dll", "Foo.Bar")]        // directory stripped
    public void InferNamespace_derivesFactoryNamespaceFromFilename(string path, string expected)
        => Assert.Equal(expected, LibraryEmitter.InferNamespace(path));

    [Fact]
    public void InferNamespace_emptyNameFallsBackToDefault()
        => Assert.Equal("ShumwayProgram", LibraryEmitter.InferNamespace(".dll"));

    [Theory]
    [InlineData("Bundle", "Bundle")]
    [InlineData("my class", "my_class")]
    [InlineData("9lives", "_9lives")]
    [InlineData("a.b", "a_b")]                              // dot is not an identifier char here
    public void SanitiseIdentifier_producesValidCSharpIdentifier(string input, string expected)
        => Assert.Equal(expected, LibraryEmitter.SanitiseIdentifier(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void SanitiseIdentifier_blankIsNull(string? input)
        => Assert.Null(LibraryEmitter.SanitiseIdentifier(input));

    [Fact]
    public void BundleResourceName_isStableLogicalName()
        // The factory's GetManifestResourceStream(...) and the csproj <LogicalName>
        // must agree on this exact string; pin it so a rename can't silently break load.
        => Assert.Equal("shumway.bundle", LibraryEmitter.BundleResourceName);
}

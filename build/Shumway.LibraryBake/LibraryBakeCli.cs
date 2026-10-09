using Shumway.Embedding;

namespace Shumway.LibraryBake;

/// <summary><c>shumway-librarybake &lt;lib.pl&gt; -o &lt;lib.shum&gt; [--wasm] [--il]</c>:
/// the bundle of one engine library, linked whole (every predicate a root)
/// and uncompressed (the browser has no Brotli codec), optionally with its
/// wasm module or its Tier-1 IL baked in. The IL binds on the framework of
/// the build that baked it: the .NET Framework build bakes the net48
/// flavour's.</summary>
public static class LibraryBakeCli
{
    public static int Main(string[] args)
    {
        string? source = null, output = null;
        bool wasm = false, il = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": output = args[++i]; break;
                case "--wasm": wasm = true; break;
                case "--il": il = true; break;
                default: source = args[i]; break;
            }
        }
        if (source is null || output is null)
        {
            Console.Error.WriteLine("usage: shumway-librarybake <lib.pl> -o <lib.shum> [--wasm] [--il]");
            return 2;
        }
#if NETFRAMEWORK
        if (wasm)
        {
            Console.Error.WriteLine(
                "shumway-librarybake: --wasm is not available in the .NET Framework build.");
            return 2;
        }
#endif

        // The module name is the file name: clpfd.pl declares `:- module(clpfd).`
        string name = Path.GetFileNameWithoutExtension(source);
        var obj = ShmoCompiler.CompileSource(File.ReadAllText(source), name);
        BundleFormat.DisableCompression = true;
        // A library that imports another (reif uses coroutining's dif/2) is
        // baked before the libraries' assembly exists: the other's bundle is
        // already in the output directory, baked before it, and its source
        // next to this one.
        LibraryBundles.UseBakeDirectories(
            Path.GetDirectoryName(Path.GetFullPath(output))!,
            Path.GetDirectoryName(Path.GetFullPath(source))!);
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            Library = true,
            IncludeCompiledIl = il,
#if !NETFRAMEWORK
            WasmBaker = wasm
                ? b => Shumway.Compiler.Wasm.WasmBundleTier.Bake(b, stdlib: false,
                    msg => Console.Error.WriteLine("shumway-librarybake: " + msg))
                : null,
#endif
        });
        foreach (var d in result.Diagnostics)
            Console.Error.WriteLine($"shumway-librarybake: {source}: {d.Severity} {d.Code}: {d.Message}");
        if (!result.Success || result.Bytes is null)
            return 1;
        File.WriteAllBytes(output, result.Bytes);
        return 0;
    }
}

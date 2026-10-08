using Shumway.Compiler.Wasm;
using Shumway.Embedding;

namespace Shumway.LibraryBake;

/// <summary><c>shumway-librarybake &lt;lib.pl&gt; -o &lt;lib.shum&gt; [--wasm]</c>:
/// the bundle of one engine library, linked whole (every predicate a root)
/// and uncompressed (the browser has no Brotli codec), optionally with its
/// wasm module baked in.</summary>
public static class LibraryBakeCli
{
    public static int Main(string[] args)
    {
        string? source = null, output = null;
        bool wasm = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o": output = args[++i]; break;
                case "--wasm": wasm = true; break;
                default: source = args[i]; break;
            }
        }
        if (source is null || output is null)
        {
            Console.Error.WriteLine("usage: shumway-librarybake <lib.pl> -o <lib.shum> [--wasm]");
            return 2;
        }

        // The module name is the file name: clpfd.pl declares `:- module(clpfd).`
        string name = Path.GetFileNameWithoutExtension(source);
        var obj = ShmoCompiler.CompileSource(File.ReadAllText(source), name);
        BundleFormat.DisableCompression = true;
        var result = ShmoLinker.Link(new LinkConfig
        {
            Objects = new[] { obj },
            Library = true,
            WasmBaker = wasm
                ? b => WasmBundleTier.Bake(b, stdlib: false,
                    msg => Console.Error.WriteLine("shumway-librarybake: " + msg))
                : null,
        });
        foreach (var d in result.Diagnostics)
            Console.Error.WriteLine($"shumway-librarybake: {source}: {d.Severity} {d.Code}: {d.Message}");
        if (!result.Success || result.Bytes is null)
            return 1;
        File.WriteAllBytes(output, result.Bytes);
        return 0;
    }
}

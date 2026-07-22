using System;
using Shumway.Embedding.Debugging.Dap;

namespace Shumway.Dap;

/// <summary>ADR-036 — <c>shumway-dap</c>: the debug adapter VS Code launches. All it
/// does is hand its stdio to <see cref="DapProxy"/> — stdout is the DAP channel, so
/// every diagnostic goes to stderr, and nothing else may ever write to the console.</summary>
internal static class DapCli
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] is "-h" or "--help"))
        {
            Console.Error.WriteLine(
                "shumway-dap — Debug Adapter Protocol glue between VS Code and a Shumway\n"
                + "debuggee's --dap endpoint. Not run by hand: the VS Code extension\n"
                + "launches it and speaks DAP over its stdio.\n"
                + "\n"
                + "  launch config: starts `shumway --dap <port> <program>` in the\n"
                + "                 integrated terminal (runInTerminal) and connects.\n"
                + "  attach config: connects to a running debuggee's port.\n"
                + "\n"
                + "  SHUMWAY_DEBUG_DIAG=1   log adapter diagnostics to stderr.");
            return 0;
        }

        bool diag = Environment.GetEnvironmentVariable("SHUMWAY_DEBUG_DIAG") == "1";
        var proxy = new DapProxy(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput(),
            diag ? line => Console.Error.WriteLine("shumway-dap: " + line) : null);
        proxy.Run();
        return 0;
    }
}

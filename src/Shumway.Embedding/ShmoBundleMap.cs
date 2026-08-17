using System.Globalization;
using System.Text;

namespace Shumway.Embedding;

/// <summary>
/// Generates a human-readable map file describing what landed in a
/// linked <see cref="Bundle"/>. Inspired by C-toolchain linker map
/// files: per-module sizes, exported / dynamic predicate lists,
/// the reachability summary, and final totals. Useful for size
/// audits, "did this module actually export what I expected?", and
/// "why did this module get dropped from the bundle?" forensics.
/// </summary>
public static class ShmoBundleMap
{
    public static string GenerateText(IReadOnlyList<ShmoObject> objects,
        IReadOnlyList<PredicateRef> entryPoints,
        LinkResult result)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(entryPoints);
        ArgumentNullException.ThrowIfNull(result);

        var sb = new StringBuilder();
        sb.AppendLine("# Shumway link map");
        sb.AppendLine($"# shumway version: {ShumwayVersion.Current}");
        sb.AppendLine($"# generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z");
        sb.AppendLine($"# success: {result.Success}");
        if (result.Bytes is not null)
            sb.AppendLine($"# bundle size: {result.Bytes.Length} bytes");
        sb.AppendLine();

        sb.AppendLine("## Entry points");
        if (entryPoints.Count == 0)
            sb.AppendLine("  (none)");
        else
            foreach (var ep in entryPoints)
                sb.AppendLine($"  {ep}");
        sb.AppendLine();

        sb.AppendLine("## Modules in the bundle");
        long totalSource = 0, totalBytecode = 0;
        var reached = new HashSet<string>(result.ReachedModules);
        foreach (var obj in objects)
        {
            if (!reached.Contains(obj.ModuleName)) continue;
            int sourceBytes = System.Text.Encoding.UTF8.GetByteCount(obj.Source);
            int bytecodeBytes = obj.Bytecode.Length;
            totalSource += sourceBytes;
            totalBytecode += bytecodeBytes;

            sb.AppendLine($"### {obj.ModuleName}  [{obj.BuildMode.ToString().ToLowerInvariant()}]");
            sb.AppendLine($"  source bytes:   {sourceBytes,10:N0}");
            sb.AppendLine($"  bytecode bytes: {bytecodeBytes,10:N0}");

            int locals = 0, publics = 0, dynamics = 0;
            var publicList = new List<string>();
            var dynamicList = new List<string>();
            foreach (var d in obj.Defined)
            {
                switch (d.Visibility)
                {
                    case PredicateVisibility.Public:
                        publics++; publicList.Add(d.Indicator.ToString()); break;
                    case PredicateVisibility.Dynamic:
                        dynamics++; dynamicList.Add(d.Indicator.ToString()); break;
                    default:
                        locals++; break;
                }
            }
            sb.AppendLine($"  predicates:     {obj.Defined.Count,10:N0}  "
                + $"(public={publics}, dynamic={dynamics}, local={locals})");
            if (publicList.Count > 0)
            {
                sb.AppendLine("  public:");
                foreach (var p in publicList.OrderBy(s => s, StringComparer.Ordinal))
                    sb.AppendLine($"    {p}");
            }
            if (dynamicList.Count > 0)
            {
                sb.AppendLine("  dynamic:");
                foreach (var p in dynamicList.OrderBy(s => s, StringComparer.Ordinal))
                    sb.AppendLine($"    {p}");
            }
            if (obj.EnsureLinked.Count > 0)
            {
                sb.AppendLine("  ensure_linked:");
                foreach (var p in obj.EnsureLinked.OrderBy(p => p.ToString(), StringComparer.Ordinal))
                    sb.AppendLine($"    {p}");
            }
            sb.AppendLine();
        }

        if (result.UnreachableModules.Count > 0)
        {
            sb.AppendLine("## Modules dropped (unreachable)");
            foreach (var name in result.UnreachableModules)
                sb.AppendLine($"  {name}");
            sb.AppendLine();
        }

        if (result.MissingPredicates.Count > 0)
        {
            sb.AppendLine("## Missing predicates");
            foreach (var m in result.MissingPredicates
                     .OrderBy(p => p.ToString(), StringComparer.Ordinal))
                sb.AppendLine($"  {m}");
            sb.AppendLine();
        }

        // Local predicates shadowing another linked module's public — the C
        // `static`-shadows-global shape. Legal (the local wins inside its own
        // module); always listed here, opt-in console warning via --warn-shadow.
        var publicOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var obj in objects)
            if (reached.Contains(obj.ModuleName))
                foreach (var d in obj.Defined)
                    if (d.Visibility == PredicateVisibility.Public)
                        publicOwner[d.Indicator.ToString()] = obj.ModuleName;
        var shadowLines = new List<string>();
        foreach (var obj in objects)
        {
            if (!reached.Contains(obj.ModuleName)) continue;
            foreach (var d in obj.Defined)
            {
                if (d.Visibility != PredicateVisibility.Local) continue;
                string key = d.Indicator.ToString();
                if (publicOwner.TryGetValue(key, out var owner) && owner != obj.ModuleName)
                    shadowLines.Add(
                        $"  {key}  local in '{obj.ModuleName}' shadows public from '{owner}'");
            }
        }
        if (shadowLines.Count > 0)
        {
            sb.AppendLine("## Local predicates shadowing a public (inside their module the local wins)");
            foreach (var line in shadowLines.OrderBy(s => s, StringComparer.Ordinal))
                sb.AppendLine(line);
            sb.AppendLine();
        }

        sb.AppendLine("## Reached predicates");
        sb.AppendLine($"  count: {result.ReachedPredicates.Count}");
        sb.AppendLine();

        sb.AppendLine("## Totals");
        sb.AppendLine($"  modules included:   {result.ReachedModules.Count}");
        sb.AppendLine($"  modules dropped:    {result.UnreachableModules.Count}");
        sb.AppendLine($"  source bytes:       {totalSource:N0}");
        sb.AppendLine($"  bytecode bytes:     {totalBytecode:N0}");
        if (result.Bytes is not null)
            sb.AppendLine($"  bundle bytes:       {result.Bytes.Length:N0}");

        return sb.ToString();
    }

    public static void WriteToFile(IReadOnlyList<ShmoObject> objects,
        IReadOnlyList<PredicateRef> entryPoints,
        LinkResult result,
        string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllText(path, GenerateText(objects, entryPoints, result),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}

using System.Text.RegularExpressions;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// The instruction-set reference documents each opcode's ENCODING, so a number
/// in it that disagrees with the table is not a stale detail — it is wrong
/// about the bytes on disk.
///
/// <para>It drifted once already: an earlier renumbering left 50 of its 60
/// headers naming the wrong opcode, silently, for as long as nobody compared
/// them. This is the comparison.</para>
/// </summary>
public class InstructionSetDocTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shumway.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate the repository root (Shumway.slnx).");
        return dir.FullName;
    }

    [Fact]
    public void EveryDocumentedOpcodeNumberMatchesTheTable()
    {
        string path = Path.Combine(RepoRoot(), "docs", "design", "wam-instruction-set.md");
        Assert.True(File.Exists(path), $"{path} is missing.");

        var byMnemonic = new Dictionary<string, Opcode>();
        foreach (Opcode op in Enum.GetValues<Opcode>())
        {
            var info = OpcodeTable.Get(op);
            if (info.IsDefined && info.Mnemonic is { Length: > 0 })
                byMnemonic[info.Mnemonic] = op;
        }

        var wrong = new List<string>();
        int checkedCount = 0;
        foreach (Match m in Regex.Matches(
            File.ReadAllText(path), @"^### ([a-z_0-9]+) \(0x([0-9A-Fa-f]+)\)", RegexOptions.Multiline))
        {
            string mnemonic = m.Groups[1].Value;
            if (!byMnemonic.TryGetValue(mnemonic, out Opcode op))
            {
                wrong.Add($"{mnemonic}: documented but not in the opcode table");
                continue;
            }
            checkedCount++;
            int documented = Convert.ToInt32(m.Groups[2].Value, 16);
            if (documented != (int)op)
                wrong.Add($"{mnemonic}: doc says 0x{documented:X2}, table says 0x{(int)op:X2}");
        }

        Assert.True(checkedCount > 40, $"only {checkedCount} opcode headers found — did the doc's shape change?");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}

using System.Text;

namespace Shumway.Embedding;

/// <summary>
/// Reads a Shumway bundle file into an in-memory <see cref="Bundle"/>.
/// Format errors (bad magic, unsupported version, truncated data) throw
/// <see cref="InvalidDataException"/> with a descriptive message.
/// </summary>
public static class BundleReader
{
    public static Bundle ReadFromFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FromBytes(File.ReadAllBytes(path));
    }

    public static Bundle FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        byte[] magic = br.ReadBytes(4);
        if (magic.Length != 4 || !magic.AsSpan().SequenceEqual(BundleFormat.Magic))
            throw new InvalidDataException(
                "Bundle: magic bytes don't match 'SHUM' — not a Shumway bundle.");

        uint version = br.ReadUInt32();
        if (version != 1 && version != 2)
            throw new InvalidDataException(
                $"Bundle: format version {version} is not supported by this runtime "
                + $"(expected 1 or 2).");

        uint moduleCount = br.ReadUInt32();
        var entries = new BundleEntry[moduleCount];
        for (uint i = 0; i < moduleCount; i++)
        {
            string name = ReadLengthPrefixedUtf8(br);
            string source = ReadLengthPrefixedUtf8(br);
            uint compiledLength = br.ReadUInt32();
            byte[]? compiled = null;
            if (compiledLength > 0)
            {
                compiled = br.ReadBytes((int)compiledLength);
                if (compiled.Length != compiledLength)
                    throw new InvalidDataException(
                        $"Bundle: truncated compiled-bytecode section (expected "
                        + $"{compiledLength} bytes, got {compiled.Length}).");
            }
            uint compiledIlLength = br.ReadUInt32();
            byte[]? compiledIl = null;
            if (compiledIlLength > 0)
            {
                compiledIl = br.ReadBytes((int)compiledIlLength);
                if (compiledIl.Length != compiledIlLength)
                    throw new InvalidDataException(
                        $"Bundle: truncated compiled-IL section (expected "
                        + $"{compiledIlLength} bytes, got {compiledIl.Length}).");
            }
            // V2+: per-predicate visibility metadata. Empty list for V1.
            List<ShmoDefinedPredicate>? defined = null;
            if (version >= 2)
            {
                uint definedCount = br.ReadUInt32();
                defined = new List<ShmoDefinedPredicate>((int)definedCount);
                for (uint j = 0; j < definedCount; j++)
                {
                    string predName = ReadLengthPrefixedUtf8(br);
                    uint arity = br.ReadUInt32();
                    byte vis = br.ReadByte();
                    defined.Add(new ShmoDefinedPredicate(
                        new PredicateRef(predName, (int)arity),
                        (PredicateVisibility)vis));
                }
            }
            entries[i] = new BundleEntry(name, source, compiled, compiledIl, defined);
        }
        return new Bundle(entries);
    }

    private static string ReadLengthPrefixedUtf8(BinaryReader br)
    {
        uint length = br.ReadUInt32();
        byte[] bytes = br.ReadBytes((int)length);
        if (bytes.Length != length)
            throw new InvalidDataException(
                "Bundle: truncated string section (expected "
                + $"{length} bytes, got {bytes.Length}).");
        return Encoding.UTF8.GetString(bytes);
    }
}

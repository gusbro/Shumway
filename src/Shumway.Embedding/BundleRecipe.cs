using System.Text;

namespace Shumway.Embedding;

/// <summary>ADR-076: how a <c>.shum</c> or a <c>.shmo</c> was built, held
/// after the stamp where every format reads it. An engine that accepts the
/// file's rules version (<see cref="Accepts"/>) can build the file again from
/// it when the stamp is not its own.
///
/// <para>Under rules 1 a recipe is a sequence of fields, each a tag byte, a
/// length and that many bytes. A reader skips a tag it does not know, so a
/// field can be added within rules 1; a change to what a field means raises
/// the rules version.</para></summary>
public sealed class BundleRecipe
{
    /// <summary>The rules this engine builds files under.</summary>
    public const int CurrentRules = 1;

    /// <summary>Whether this engine rebuilds a file built under
    /// <paramref name="rules"/>.</summary>
    public static bool Accepts(int rules) => rules == CurrentRules;

    /// <summary>The tool that built the file: <c>shumway-link</c>,
    /// <c>shumway-lib</c>, <c>shumway-compile</c>, <c>page</c> for a library a
    /// page compiled, <c>save_state</c>, or <c>host</c> for a bundle a host
    /// wrote with <see cref="BundleWriter"/>.</summary>
    public string Tool { get; init; } = "host";

    /// <summary>The recipe of a file a host wrote: nothing to build it from.</summary>
    public static BundleRecipe Host { get; } = new();

    private enum Field : byte
    {
        Tool = 1,
    }

    internal byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WriteText(bw, Field.Tool, Tool);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>The recipe in <paramref name="bytes"/>, under rules 1.</summary>
    internal static BundleRecipe FromBytes(ReadOnlySpan<byte> bytes)
    {
        string tool = "host";
        int at = 0;
        while (at < bytes.Length)
        {
            if (bytes.Length - at < 5) throw Short();
            var tag = (Field)bytes[at];
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(at + 1));
            at += 5;
            if (length < 0 || length > bytes.Length - at) throw Short();
            ReadOnlySpan<byte> value = bytes.Slice(at, length);
            at += length;
            switch (tag)
            {
                case Field.Tool:
                    tool = Encoding.UTF8.GetString(value.ToArray());
                    break;
                // A field of a later engine under rules 1: skipped.
            }
        }
        return new BundleRecipe { Tool = tool };
    }

    private static void WriteText(BinaryWriter bw, Field tag, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        bw.Write((byte)tag);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static InvalidDataException Short()
        => new("The recipe of a compiled file is cut short.");
}

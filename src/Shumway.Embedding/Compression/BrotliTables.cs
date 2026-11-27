using System.IO.Compression;

namespace Shumway.Embedding.Compression;

/// <summary>The data of the Brotli format (RFC 7932): the context lookup
/// tables of section 7.1, the length codes of sections 5 and 6, the static
/// dictionary of appendix A and the word transforms of appendix B. Each was
/// taken from the RFC and checked against the length and CRC-32 it states
/// (<c>BrotliDecoderTests</c> checks them again). The dictionary is a
/// resource compressed with Deflate, read the first time a stream refers to
/// it.</summary>
internal static class BrotliTables
{
    /// <summary>A word transform: a prefix, one elementary transform, a
    /// suffix. <see cref="Kind"/> is 0 Identity, 1 FermentFirst, 2 FermentAll,
    /// 3 to 11 OmitFirst1 to OmitFirst9, 12 to 20 OmitLast1 to OmitLast9, the
    /// numbering of appendix B.</summary>
    internal sealed class Transform
    {
        public readonly int Kind;
        public readonly byte[] Prefix;
        public readonly byte[] Suffix;

        public Transform(int kind, string prefix, string suffix)
        {
            Kind = kind;
            Prefix = Bytes(prefix);
            Suffix = Bytes(suffix);
        }

        // Every character of a transform is below 128.
        private static byte[] Bytes(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            return bytes;
        }
    }

    // Section 7.1: the context ID of a literal in the UTF8 and Signed modes.
    private static readonly byte[] Lut0 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 0, 0, 4, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        8, 12, 16, 12, 12, 20, 12, 16, 24, 28, 12, 12, 32, 12, 36, 12,
        44, 44, 44, 44, 44, 44, 44, 44, 44, 44, 32, 32, 24, 40, 28, 12,
        12, 48, 52, 52, 52, 48, 52, 52, 52, 48, 52, 52, 52, 52, 52, 48,
        52, 52, 52, 52, 52, 48, 52, 52, 52, 52, 52, 24, 12, 28, 12, 12,
        12, 56, 60, 60, 60, 56, 60, 60, 60, 56, 60, 60, 60, 60, 60, 56,
        60, 60, 60, 60, 60, 56, 60, 60, 60, 60, 60, 24, 12, 28, 12, 0,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1, 0, 1,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
        2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3, 2, 3,
    };

    private static readonly byte[] Lut1 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1,
        1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 1, 1, 1, 1, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
    };

    private static readonly byte[] Lut2 =
    {
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7,
    };

    private static readonly int[] NdBits = { 0, 0, 0, 0, 10, 10, 11, 11, 10, 10, 10, 10, 10, 9, 9, 8, 7, 7, 8, 7, 7, 6, 6, 5, 5 };

    private static readonly Transform[] Transforms =
    {
        new(0, "", ""),   // 0: Identity
        new(0, "", " "),   // 1: Identity
        new(0, " ", " "),   // 2: Identity
        new(3, "", ""),   // 3: OmitFirst1
        new(1, "", " "),   // 4: FermentFirst
        new(0, "", " the "),   // 5: Identity
        new(0, " ", ""),   // 6: Identity
        new(0, "s ", " "),   // 7: Identity
        new(0, "", " of "),   // 8: Identity
        new(1, "", ""),   // 9: FermentFirst
        new(0, "", " and "),   // 10: Identity
        new(4, "", ""),   // 11: OmitFirst2
        new(12, "", ""),   // 12: OmitLast1
        new(0, ", ", " "),   // 13: Identity
        new(0, "", ", "),   // 14: Identity
        new(1, " ", " "),   // 15: FermentFirst
        new(0, "", " in "),   // 16: Identity
        new(0, "", " to "),   // 17: Identity
        new(0, "e ", " "),   // 18: Identity
        new(0, "", "\u0022"),   // 19: Identity
        new(0, "", "."),   // 20: Identity
        new(0, "", "\u0022>"),   // 21: Identity
        new(0, "", "\u000a"),   // 22: Identity
        new(14, "", ""),   // 23: OmitLast3
        new(0, "", "]"),   // 24: Identity
        new(0, "", " for "),   // 25: Identity
        new(5, "", ""),   // 26: OmitFirst3
        new(13, "", ""),   // 27: OmitLast2
        new(0, "", " a "),   // 28: Identity
        new(0, "", " that "),   // 29: Identity
        new(1, " ", ""),   // 30: FermentFirst
        new(0, "", ". "),   // 31: Identity
        new(0, ".", ""),   // 32: Identity
        new(0, " ", ", "),   // 33: Identity
        new(6, "", ""),   // 34: OmitFirst4
        new(0, "", " with "),   // 35: Identity
        new(0, "", "'"),   // 36: Identity
        new(0, "", " from "),   // 37: Identity
        new(0, "", " by "),   // 38: Identity
        new(7, "", ""),   // 39: OmitFirst5
        new(8, "", ""),   // 40: OmitFirst6
        new(0, " the ", ""),   // 41: Identity
        new(15, "", ""),   // 42: OmitLast4
        new(0, "", ". The "),   // 43: Identity
        new(2, "", ""),   // 44: FermentAll
        new(0, "", " on "),   // 45: Identity
        new(0, "", " as "),   // 46: Identity
        new(0, "", " is "),   // 47: Identity
        new(18, "", ""),   // 48: OmitLast7
        new(12, "", "ing "),   // 49: OmitLast1
        new(0, "", "\u000a\u0009"),   // 50: Identity
        new(0, "", ":"),   // 51: Identity
        new(0, " ", ". "),   // 52: Identity
        new(0, "", "ed "),   // 53: Identity
        new(11, "", ""),   // 54: OmitFirst9
        new(9, "", ""),   // 55: OmitFirst7
        new(17, "", ""),   // 56: OmitLast6
        new(0, "", "("),   // 57: Identity
        new(1, "", ", "),   // 58: FermentFirst
        new(19, "", ""),   // 59: OmitLast8
        new(0, "", " at "),   // 60: Identity
        new(0, "", "ly "),   // 61: Identity
        new(0, " the ", " of "),   // 62: Identity
        new(16, "", ""),   // 63: OmitLast5
        new(20, "", ""),   // 64: OmitLast9
        new(1, " ", ", "),   // 65: FermentFirst
        new(1, "", "\u0022"),   // 66: FermentFirst
        new(0, ".", "("),   // 67: Identity
        new(2, "", " "),   // 68: FermentAll
        new(1, "", "\u0022>"),   // 69: FermentFirst
        new(0, "", "=\u0022"),   // 70: Identity
        new(0, " ", "."),   // 71: Identity
        new(0, ".com/", ""),   // 72: Identity
        new(0, " the ", " of the "),   // 73: Identity
        new(1, "", "'"),   // 74: FermentFirst
        new(0, "", ". This "),   // 75: Identity
        new(0, "", ","),   // 76: Identity
        new(0, ".", " "),   // 77: Identity
        new(1, "", "("),   // 78: FermentFirst
        new(1, "", "."),   // 79: FermentFirst
        new(0, "", " not "),   // 80: Identity
        new(0, " ", "=\u0022"),   // 81: Identity
        new(0, "", "er "),   // 82: Identity
        new(2, " ", " "),   // 83: FermentAll
        new(0, "", "al "),   // 84: Identity
        new(2, " ", ""),   // 85: FermentAll
        new(0, "", "='"),   // 86: Identity
        new(2, "", "\u0022"),   // 87: FermentAll
        new(1, "", ". "),   // 88: FermentFirst
        new(0, " ", "("),   // 89: Identity
        new(0, "", "ful "),   // 90: Identity
        new(1, " ", ". "),   // 91: FermentFirst
        new(0, "", "ive "),   // 92: Identity
        new(0, "", "less "),   // 93: Identity
        new(2, "", "'"),   // 94: FermentAll
        new(0, "", "est "),   // 95: Identity
        new(1, " ", "."),   // 96: FermentFirst
        new(2, "", "\u0022>"),   // 97: FermentAll
        new(0, " ", "='"),   // 98: Identity
        new(1, "", ","),   // 99: FermentFirst
        new(0, "", "ize "),   // 100: Identity
        new(2, "", "."),   // 101: FermentAll
        new(0, "\u00c2\u00a0", ""),   // 102: Identity
        new(0, " ", ","),   // 103: Identity
        new(1, "", "=\u0022"),   // 104: FermentFirst
        new(2, "", "=\u0022"),   // 105: FermentAll
        new(0, "", "ous "),   // 106: Identity
        new(2, "", ", "),   // 107: FermentAll
        new(1, "", "='"),   // 108: FermentFirst
        new(1, " ", ","),   // 109: FermentFirst
        new(2, " ", "=\u0022"),   // 110: FermentAll
        new(2, " ", ", "),   // 111: FermentAll
        new(2, "", ","),   // 112: FermentAll
        new(2, "", "("),   // 113: FermentAll
        new(2, "", ". "),   // 114: FermentAll
        new(2, " ", "."),   // 115: FermentAll
        new(2, "", "='"),   // 116: FermentAll
        new(2, " ", ". "),   // 117: FermentAll
        new(1, " ", "=\u0022"),   // 118: FermentFirst
        new(2, " ", "='"),   // 119: FermentAll
        new(1, " ", "='"),   // 120: FermentFirst
    };

    internal static byte[] ContextLut0 => Lut0;
    internal static byte[] ContextLut1 => Lut1;
    internal static byte[] ContextLut2 => Lut2;
    internal static Transform[] WordTransforms => Transforms;

    /// <summary>Section 8: log2 of the number of dictionary words of each
    /// length, 0 below 4.</summary>
    internal static int[] DictionaryBits => NdBits;

    /// <summary>Section 8: where the words of each length start in the
    /// dictionary.</summary>
    internal static readonly int[] DictionaryOffsets = Offsets();

    private static int[] Offsets()
    {
        var offsets = new int[25];
        for (int length = 0; length < 24; length++)
            offsets[length + 1] = offsets[length] + (length < 4 ? 0 : length << NdBits[length]);
        return offsets;
    }

    internal const int DictionarySize = 122784;

    private static byte[]? _dictionary;

    /// <summary>Appendix A, read from the resource on first use.</summary>
    internal static byte[] Dictionary
    {
        get
        {
            byte[]? dictionary = Volatile.Read(ref _dictionary);
            if (dictionary is not null) return dictionary;
            dictionary = LoadDictionary();
            return Interlocked.CompareExchange(ref _dictionary, dictionary, null) ?? dictionary;
        }
    }

    private static byte[] LoadDictionary()
    {
        const string name = "Shumway.Embedding.Compression.brotli-dictionary.deflate";
        using Stream packed = typeof(BrotliTables).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The resource {name} is missing.");
        using var inflate = new DeflateStream(packed, CompressionMode.Decompress);
        var dictionary = new byte[DictionarySize];
        int at = 0;
        while (at < dictionary.Length)
        {
            int read = inflate.Read(dictionary, at, dictionary.Length - at);
            if (read == 0) throw new InvalidDataException($"The resource {name} is short.");
            at += read;
        }
        return dictionary;
    }

    // Section 5: insert lengths and copy lengths, base and extra bits of
    // each code.
    internal static readonly int[] InsertBase =
    {
        0, 1, 2, 3, 4, 5, 6, 8, 10, 14, 18, 26, 34, 50, 66, 98,
        130, 194, 322, 578, 1090, 2114, 6210, 22594,
    };
    internal static readonly int[] InsertExtra =
    {
        0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5,
        6, 7, 8, 9, 10, 12, 14, 24,
    };
    internal static readonly int[] CopyBase =
    {
        2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 18, 22, 30, 38, 54,
        70, 102, 134, 198, 326, 582, 1094, 2118,
    };
    internal static readonly int[] CopyExtra =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4,
        5, 5, 6, 7, 8, 9, 10, 24,
    };

    // Section 5: the first insert and copy length code of each of the eleven
    // cells of 64 insert-and-copy codes.
    internal static readonly int[] CellInsert = { 0, 0, 0, 0, 8, 8, 0, 16, 8, 16, 16 };
    internal static readonly int[] CellCopy = { 0, 8, 0, 8, 0, 8, 16, 0, 16, 8, 16 };

    // Section 6: block counts, base and extra bits of each code.
    internal static readonly int[] BlockCountBase =
    {
        1, 5, 9, 13, 17, 25, 33, 41, 49, 65, 81, 97, 113, 145, 177, 209,
        241, 305, 369, 497, 753, 1265, 2289, 4337, 8433, 16625,
    };
    internal static readonly int[] BlockCountExtra =
    {
        2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5,
        6, 6, 7, 8, 9, 10, 11, 12, 13, 24,
    };

    // Section 3.5: the order the code lengths of the code length alphabet
    // come in.
    internal static readonly int[] CodeLengthOrder =
    {
        1, 2, 3, 4, 0, 5, 17, 6, 16, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    };
}

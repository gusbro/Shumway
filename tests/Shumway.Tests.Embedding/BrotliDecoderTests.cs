using System.IO.Compression;
using System.Security.Cryptography;
using Shumway.Embedding;
using Shumway.Embedding.Compression;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// <see cref="ManagedBrotliDecoder"/>, Shumway's own Brotli decoder (RFC 7932) for
/// the runtimes whose .NET has none. Where .NET has an encoder, it is the
/// oracle: every stream it writes decodes to what it was given, at every
/// quality and at the window sizes a stream can name. A stream that is cut
/// short or damaged is refused with <see cref="InvalidDataException"/> and
/// nothing else.
/// </summary>
public sealed class BrotliDecoderTests
{
    // RFC 7932, appendix C.
    private static uint Crc32(byte[] bytes)
    {
        uint crc = 0xffffffffu;
        foreach (byte b in bytes)
        {
            uint c = (crc ^ b) & 0xff;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            crc = c ^ (crc >> 8);
        }
        return crc ^ 0xffffffffu;
    }

    [Fact]
    public void TheTables_AreTheRfcs()
    {
        byte[] dictionary = BrotliTables.Dictionary;
        Assert.Equal(122784, dictionary.Length);
        Assert.Equal(0x5136cb04u, Crc32(dictionary));
        Assert.Equal(dictionary.Length,
            BrotliTables.DictionaryOffsets[24] + (24 << BrotliTables.DictionaryBits[24]));
        Assert.Equal(0x8e91efb7u, Crc32(BrotliTables.ContextLut0));
        Assert.Equal(0xd01a32f4u, Crc32(BrotliTables.ContextLut1));
        Assert.Equal(0x0dd7a0d6u, Crc32(BrotliTables.ContextLut2));

        // Appendix B: each transform as its prefix and a zero, its kind, its
        // suffix and a zero.
        var check = new List<byte>();
        foreach (BrotliTables.Transform t in BrotliTables.WordTransforms)
        {
            check.AddRange(t.Prefix);
            check.Add(0);
            check.Add((byte)t.Kind);
            check.AddRange(t.Suffix);
            check.Add(0);
        }
        Assert.Equal(121, BrotliTables.WordTransforms.Length);
        Assert.Equal(648, check.Count);
        Assert.Equal(0x3d965f81u, Crc32(check.ToArray()));
    }

    // A grammar and a page of text in several scripts, compressed by .NET's
    // encoder at its smallest size: dictionary words, their transforms and
    // backward copies. What it decodes to is known by its digest.
    private const string Fixture =
        "G9sIAJyHsTMimV9qvRKnsdOhm+0s070n9Eh61DRUtmdKTYGafPpi+0Rzpr7aCpBUAGmxbKnUVdwGyAAa0A6xPXt769pSet/7" +
        "tEpgoGloAAyAAQgGwjyG8x1egRVg1BeeFHR6xSBUWqHO/KnrvwMwE2by7fYmPOlNBGEEufSmboFUxF4CncSd7PfezrfDFgo+" +
        "AJB5qzgQW2JYR7HRx1UpCefpGelOuEWYj7IWF4AYzDmmOVJZmnXHcpjGFhceGyoIMDfD7hP3eIduT/2CCgKCyscvIQEa/vH5" +
        "cgKZtpOxU28dFkYTARiZmTlxnLU5DuPE1+5obleTFNZBW1x73qM2tN3nywloBjJjWdcHDCWDq/h37vranGFNAz3WNDCuzI8m" +
        "OJ+WZxxphvloq/8PwU4CP+NZ4iDe83sB6OdR809P0IyyJh8Yi/OD5rYfTOjjKxu2mhO6QEVpxhu6rAmCiClZMyWI2/A5FCe5" +
        "xXxMFfH7GQUJ8LBrx5jmTcYVx53f83u7urA10HWQu4vrrDkJTtbsBDtr0i+ZfxK3Ig/V6Or8FQFaN9GTtqm7uyA8r0Ubrfhz" +
        "xx08qvsuWTVPWb3JYchKbklUFtQYVIiKMs5FXpa+2zuC1M74+8M+bo1Ud3goWBqhe2Dxdm9LAAEOquFMnU9RfSHgyuRrIxb8" +
        "sYjH6iyzjhB3C+dR2bOFSfcRu9Y5sCXjt2TxlmzjUnCA5fwfUN9y6z52lVefK441NdlybcicbJIb7Nl2n6y2Eg74wj2+eAvM" +
        "3/xkJqFUPFIpjrG52+cZW8CCfPEoM7dCrv6KKUZuKp3J5vKFJKYEpSlTQCftoitylHfz7ytIEVmaSX5R+x45U+70kpJEwlKB" +
        "6vLQCH1Wv35aLFisYrHTIu4jwt5izfvRND7mFBfEh09inlePzm3VFTIhRU5cExIMdVq1poNiHtBhJzQRptS/QOWhcV04HP9b" +
        "WE+9CkuU/X2iCA==";

    private const string FixtureSha256 = "5b0e278b75d5cb6d11fdc39d912cf935f0033b00bebcbcde3c4b2976fdeb9a51";

    private static string Sha256(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2")));
    }

    [Fact]
    public void AStreamDotNetWrote_DecodesToWhatItWasGiven()
    {
        byte[] stream = Convert.FromBase64String(Fixture);
        byte[] decoded = ManagedBrotliDecoder.Decompress(stream, 0, stream.Length);
        Assert.Equal(2268, decoded.Length);
        Assert.Equal(FixtureSha256, Sha256(decoded));
    }

    [Fact]
    public void AStreamCutShortOrDamaged_IsRefused()
    {
        byte[] stream = Convert.FromBase64String(Fixture);
        for (int length = 0; length < stream.Length; length++)
            Assert.Throws<InvalidDataException>(() => ManagedBrotliDecoder.Decompress(stream, 0, length));

        var random = new Random(7932);
        int refused = 0;
        for (int round = 0; round < 2000; round++)
        {
            var damaged = (byte[])stream.Clone();
            int flips = 1 + random.Next(3);
            for (int f = 0; f < flips; f++)
                damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
            try
            {
                ManagedBrotliDecoder.Decompress(damaged, 0, damaged.Length);
            }
            catch (InvalidDataException)
            {
                refused++;
            }
        }
        // Nearly every damaged stream is refused; none fails any other way.
        Assert.True(refused > 1500, $"only {refused} of 2000 damaged streams were refused");
    }

    // A stream written here, bit by bit after RFC 7932: one meta-block whose
    // only command copies a word of the static dictionary under a transform.
    private sealed class Bits
    {
        private readonly List<byte> _bytes = new();
        private int _used = 8;

        public void Write(int value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_used == 8) { _bytes.Add(0); _used = 0; }
                if (((value >> i) & 1) != 0) _bytes[_bytes.Count - 1] |= (byte)(1 << _used);
                _used++;
            }
        }

        // A simple prefix code of one symbol: read with no bits.
        public void OneSymbol(int symbol, int alphabetBits)
        {
            Write(1, 2);
            Write(0, 2);
            Write(symbol, alphabetBits);
        }

        public byte[] ToArray() => _bytes.ToArray();
    }

    private static byte[] WordStream(int length, int index, int transform, int outputLength)
    {
        // The copy length code of the word's length, and its extra bits.
        int copyCode = Array.FindLastIndex(BrotliTables.CopyBase, b => b <= length);
        int copyExtra = length - BrotliTables.CopyBase[copyCode];
        // Insert code 0 with an explicit distance: cells 2, 3 and 6.
        int command = copyCode < 8 ? 128 + copyCode : copyCode < 16 ? 192 + copyCode - 8 : 384 + copyCode - 16;
        // Nothing written yet: a distance of 1 + word id is the word.
        int distance = 1 + (index | (transform << BrotliTables.DictionaryBits[length]));
        int dcode = -1, dbits = 0, dextra = 0;
        for (int code = 16; code < 64 && dcode < 0; code++)
        {
            int x = code - 16;
            int bits = 1 + (x >> 1);
            int offset = ((2 + (x & 1)) << bits) - 4;
            if (distance - 1 - offset >= 0 && distance - 1 - offset < 1 << bits)
            {
                dcode = code;
                dbits = bits;
                dextra = distance - 1 - offset;
            }
        }
        Assert.True(dcode >= 0);

        var w = new Bits();
        w.Write(0, 1);                       // WBITS 16
        w.Write(1, 1);                       // ISLAST
        w.Write(0, 1);                       // not ISLASTEMPTY
        w.Write(0, 2);                       // four nibbles
        w.Write(outputLength - 1, 16);       // MLEN - 1
        w.Write(0, 1); w.Write(0, 1); w.Write(0, 1);   // one block type of each
        w.Write(0, 2);                       // NPOSTFIX
        w.Write(0, 4);                       // NDIRECT
        w.Write(0, 2);                       // context mode LSB6
        w.Write(0, 1); w.Write(0, 1);        // one literal tree, one distance tree
        w.OneSymbol(0, 8);                   // literals
        w.OneSymbol(command, 10);            // insert-and-copy
        w.OneSymbol(dcode, 6);               // distances
        w.Write(copyExtra, BrotliTables.CopyExtra[copyCode]);
        w.Write(dextra, dbits);
        return w.ToArray();
    }

    // Section 8, written again from the RFC for the test.
    private static byte[] Transformed(byte[] word, BrotliTables.Transform t)
    {
        byte[] body = t.Kind switch
        {
            >= 3 and <= 11 => word.Skip(Math.Min(t.Kind - 2, word.Length)).ToArray(),
            >= 12 => word.Take(Math.Max(0, word.Length - (t.Kind - 11))).ToArray(),
            _ => (byte[])word.Clone(),
        };
        int Ferment(int pos)
        {
            if (body[pos] < 192)
            {
                if (body[pos] >= 'a' && body[pos] <= 'z') body[pos] ^= 32;
                return 1;
            }
            if (body[pos] < 224)
            {
                if (pos + 1 < body.Length) body[pos + 1] ^= 32;
                return 2;
            }
            if (pos + 2 < body.Length) body[pos + 2] ^= 5;
            return 3;
        }
        if (t.Kind == 1 && body.Length > 0) Ferment(0);
        if (t.Kind == 2) for (int i = 0; i < body.Length;) i += Ferment(i);
        return t.Prefix.Concat(body).Concat(t.Suffix).ToArray();
    }

    // Every transform of appendix B, on words of three lengths.
    [Fact]
    public void EveryWordTransform_DecodesAsTheRfcSays()
    {
        byte[] dictionary = BrotliTables.Dictionary;
        for (int transform = 0; transform < 121; transform++)
            foreach (int length in new[] { 12, 17, 24 })
            {
                int index = (transform * 13 + length) & ((1 << BrotliTables.DictionaryBits[length]) - 1);
                int at = BrotliTables.DictionaryOffsets[length] + index * length;
                byte[] word = dictionary.AsSpan(at, length).ToArray();
                byte[] expected = Transformed(word, BrotliTables.WordTransforms[transform]);
                byte[] stream = WordStream(length, index, transform, expected.Length);
                byte[] decoded = ManagedBrotliDecoder.Decompress(stream, 0, stream.Length);
                Assert.True(expected.AsSpan().SequenceEqual(decoded), $"transform {transform}, length {length}");
            }
    }

#if !NETFRAMEWORK
    private static byte[] Compress(byte[] input, int quality, int window)
    {
        var output = new byte[BrotliEncoder.GetMaxCompressedLength(input.Length)];
        Assert.True(BrotliEncoder.TryCompress(input, output, out int written, quality, window));
        return output.AsSpan(0, written).ToArray();
    }

    private static void RoundTrips(byte[] input, int quality, int window)
    {
        byte[] stream = Compress(input, quality, window);
        byte[] decoded = ManagedBrotliDecoder.Decompress(stream, 0, stream.Length);
        Assert.True(input.AsSpan().SequenceEqual(decoded),
            $"{input.Length} bytes at quality {quality}, window {window}: decoded {decoded.Length}");
    }

    private static byte[] Text(int size, int seed)
    {
        string[] words =
        {
            "the", "of", "and", "information", "government", "University", "time", "people",
            "Prolog", "clause", "module", "predicate", "<div class=\"", "\">", "http://", ".html",
            "fox", "jumps", "over", "lazy", "dog", ",", ".", "\n", "  ", "(", ")", "café", "日本語",
        };
        var random = new Random(seed);
        var text = new System.Text.StringBuilder();
        while (text.Length < size)
        {
            string w = words[random.Next(words.Length)];
            text.Append(random.Next(4) == 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w);
            text.Append(random.Next(3) == 0 ? " " : "");
        }
        return System.Text.Encoding.UTF8.GetBytes(text.ToString());
    }

    private static byte[] Noise(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // Copies from far back: repeated blocks of noise, some past a small
    // window, so the decoder tells a copy from a dictionary word by the
    // window and by what it has produced.
    private static byte[] FarRepeats(int size, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[size];
        random.NextBytes(bytes.AsSpan(0, Math.Min(size, 70000)));
        for (int at = 70000; at < size;)
        {
            int length = Math.Min(size - at, 50 + random.Next(4000));
            int from = random.Next(at - length > 0 ? at - length : 1);
            Array.Copy(bytes, from, bytes, at, length);
            at += length;
        }
        return bytes;
    }

    [Fact]
    public void SmallInputs_RoundTrip_AtEveryQuality()
    {
        byte[][] inputs =
        {
            Array.Empty<byte>(),
            new byte[] { 42 },
            Noise(300, 1),
            Text(3000, 2),
            System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcabd", 500))),
            new byte[5000],
        };
        foreach (byte[] input in inputs)
            for (int quality = 0; quality <= 11; quality++)
                foreach (int window in new[] { 10, 16, 22, 24 })
                    RoundTrips(input, quality, window);
    }

    // Long dictionary words with their first or last letters cut, each once,
    // so the encoder writes them as words of the dictionary transformed: the
    // OmitFirst and OmitLast transforms.
    [Fact]
    public void CutDictionaryWords_RoundTrip()
    {
        byte[] dictionary = BrotliTables.Dictionary;
        var text = new List<byte>();
        int word = 0;
        foreach (int cut in new[] { 1, 2, 3, 4, 5, 6, 7, 9 })
            for (int length = 12; length <= 18; length++)
                for (int k = 0; k < 6; k++, word++)
                {
                    int index = (word * 37) & ((1 << BrotliTables.DictionaryBits[length]) - 1);
                    int at = BrotliTables.DictionaryOffsets[length] + index * length;
                    bool first = word % 2 == 0;
                    for (int i = first ? cut : 0; i < (first ? length : length - cut); i++) text.Add(dictionary[at + i]);
                    text.Add((byte)' ');
                }
        foreach (int quality in new[] { 9, 10, 11 })
            RoundTrips(text.ToArray(), quality, 22);
    }

    [Fact]
    public void LargeInputs_RoundTrip()
    {
        byte[][] inputs = { Text(600_000, 3), Noise(300_000, 4), FarRepeats(1_500_000, 5) };
        foreach (byte[] input in inputs)
            foreach (int quality in new[] { 1, 5, 9, 11 })
                foreach (int window in new[] { 16, 22, 24 })
                    RoundTrips(input, quality, window);
    }

    [Fact]
    public void TheFixture_IsWhatDotNetDecodes()
    {
        byte[] stream = Convert.FromBase64String(Fixture);
        using var input = new MemoryStream(stream);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        Assert.Equal(FixtureSha256, Sha256(output.ToArray()));
    }

    // A bundle linked here is compressed by .NET; read with Shumway's own
    // decoder it is the same bundle, and Uncompressed gives its raw form.
    [Fact]
    public void ACompressedBundle_ReadsWithTheOwnDecoder()
    {
        string source = string.Concat(Enumerable.Range(0, 400).Select(i => $"fact({i}, word_{i % 37}).\n"))
            + "count(N) :- findall(X, fact(X, _), L), length(L, N).\n";
        bool disabled = BundleFormat.DisableCompression;
        try
        {
            byte[] Link(bool compress)
            {
                BundleFormat.DisableCompression = !compress;
                var link = ShmoLinker.Link(new LinkConfig
                {
                    Objects = new[] { ShmoCompiler.CompileSource(source, "app", ShmoBuildMode.Release) },
                    EntryPoints = new[] { new PredicateRef("count", 1) },
                });
                Assert.True(link.Success, string.Join("; ", link.Diagnostics.Select(d => d.Message)));
                return link.Bytes!;
            }
            byte[] image = Link(compress: true);
            byte[] raw = Link(compress: false);
            Assert.Equal(BundleFormat.CompressionBrotli, image[BundleFormat.FlagAt(image)]);
            Assert.Equal(BundleFormat.CompressionNone, raw[BundleFormat.FlagAt(raw)]);

            BundleFormat.ForceOwnBrotliDecoder = true;
            try
            {
                int reads = BundleFormat.OwnBrotliReads;
                var engine = new PrologEngine { Out = new StringWriter() };
                engine.LoadBundle(BundleReader.FromBytes(image));
                Assert.Equal(reads + 1, BundleFormat.OwnBrotliReads);
                var s = engine.Query("count(N).");
                Assert.True(s.Success);
                Assert.Equal("400", s["N"]!.ToString());

                byte[] plain = BundleFormat.Uncompressed(image);
                Assert.Equal(raw, plain);
                var again = new PrologEngine { Out = new StringWriter() };
                again.LoadBundle(BundleReader.FromBytes(plain));
                Assert.Equal("400", again.Query("count(N).")["N"]!.ToString());
                Assert.Same(plain, BundleFormat.Uncompressed(plain));
            }
            finally { BundleFormat.ForceOwnBrotliDecoder = false; }
        }
        finally { BundleFormat.DisableCompression = disabled; }
    }
#endif
}

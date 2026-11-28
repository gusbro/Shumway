using System.Runtime.CompilerServices;

namespace Shumway.Embedding.Compression;

/// <summary>A Brotli decoder (RFC 7932), for the runtimes whose .NET has no
/// Brotli codec: a browser and .NET Framework. It decodes a whole stream held
/// in memory, the body of a bundle, and refuses with
/// <see cref="InvalidDataException"/> a stream the format rejects.</summary>
internal static class ManagedBrotliDecoder
{
    /// <summary>Decodes the Brotli stream in <paramref name="input"/> from
    /// <paramref name="offset"/> on. Bytes after the end of the stream are not
    /// read.</summary>
    public static byte[] Decompress(byte[] input, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > input.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        var reader = new Reader(input, offset, count);
        reader.Run();
        return reader.Result();
    }

    /// <summary><see cref="Decompress"/>, as a stream over the decoder's own
    /// buffer, which is not copied.</summary>
    public static MemoryStream DecompressToStream(byte[] input, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset > input.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));
        var reader = new Reader(input, offset, count);
        reader.Run();
        return reader.ResultStream();
    }

    /// <summary>A prefix code as a lookup table: 256 entries on the next eight
    /// bits of the stream, and second-level tables for the longer codes. An
    /// entry is <c>(symbol &lt;&lt; 8) | length</c>, or for a second-level
    /// table <c>(start &lt;&lt; 8) | 0x80 | bits</c>.</summary>
    private sealed class PrefixCode
    {
        public readonly int[] Table;
        public PrefixCode(int[] table) => Table = table;
    }

    private const int RootBits = 8;
    private const int MaxCodeLength = 15;
    private const int Unfilled = -1;

    private sealed class Reader
    {
        private readonly byte[] _in;
        private int _pos;
        private readonly int _end;
        private ulong _bits;
        private int _count;
        // Zero bits added past the end of the input: reading one is a stream
        // that ended early.
        private int _pad;

        private byte[] _out;
        private int _length;

        // Section 4: the last four distances, _last the index of the last.
        private readonly int[] _distances = { 16, 15, 11, 4 };
        private int _last = 3;

        private int _window;

        public Reader(byte[] input, int offset, int count)
        {
            _in = input;
            _pos = offset;
            _end = offset + count;
            _out = new byte[Math.Max(1024, count < int.MaxValue / 4 ? count * 4 : count)];
        }

        public byte[] Result()
        {
            if (_out.Length == _length) return _out;
            var result = new byte[_length];
            Buffer.BlockCopy(_out, 0, result, 0, _length);
            return result;
        }

        public MemoryStream ResultStream()
            => new(_out, 0, _length, writable: false, publiclyVisible: true);

        public void Run()
        {
            _window = (1 << ReadWindowBits()) - 16;
            bool last;
            do
            {
                last = ReadBits(1) == 1;
                if (last && ReadBits(1) == 1)
                {
                    ExpectZeroFill();
                    break;
                }
                int nibbles = ReadBits(2);
                if (nibbles == 3)
                {
                    SkipMetadata();
                    continue;
                }
                nibbles += 4;
                int length = 0;
                for (int i = 0; i < nibbles; i++)
                {
                    int nibble = ReadBits(4);
                    if (i == nibbles - 1 && nibbles > 4 && nibble == 0)
                        throw Invalid("a meta-block length with a last nibble of zero");
                    length |= nibble << (4 * i);
                }
                length += 1;
                if (!last && ReadBits(1) == 1)
                {
                    CopyUncompressed(length);
                    continue;
                }
                MetaBlock(length);
                if (last) ExpectZeroFill();
            }
            while (!last);
        }

        // ---- bits -------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Refill()
        {
            while (_count <= 56)
            {
                if (_pos < _end) _bits |= (ulong)_in[_pos++] << _count;
                else _pad += 8;
                _count += 8;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Drop(int n)
        {
            _bits >>= n;
            _count -= n;
            if (_count < _pad) throw Invalid("a stream that ends early");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadBits(int n)
        {
            if (n == 0) return 0;
            if (_count < n) Refill();
            int value = (int)(_bits & ((1UL << n) - 1));
            Drop(n);
            return value;
        }

        // The fill bits up to a byte boundary are zero.
        private void ExpectZeroFill()
        {
            int fill = (_count - _pad) & 7;
            if (ReadBits(fill) != 0) throw Invalid("fill bits that are not zero");
        }

        // A run of whole bytes, from a byte boundary: what the bit buffer
        // holds first, then the input.
        private void ReadBytes(byte[] into, int at, int n)
        {
            while (n > 0 && _count - _pad >= 8)
            {
                into[at++] = (byte)_bits;
                Drop(8);
                n--;
            }
            if (n == 0) return;
            if (_end - _pos < n) throw Invalid("a stream that ends early");
            Buffer.BlockCopy(_in, _pos, into, at, n);
            _pos += n;
        }

        private void SkipBytes(int n)
        {
            while (n > 0 && _count - _pad >= 8)
            {
                Drop(8);
                n--;
            }
            if (_end - _pos < n) throw Invalid("a stream that ends early");
            _pos += n;
        }

        // ---- headers ----------------------------------------------------

        // Section 9.1.
        private int ReadWindowBits()
        {
            if (ReadBits(1) == 0) return 16;
            int n = ReadBits(3);
            if (n != 0) return 17 + n;
            n = ReadBits(3);
            if (n == 1) throw Invalid("a window size of the reserved pattern");
            return n == 0 ? 17 : 8 + n;
        }

        // Section 9.2: an empty meta-block that holds metadata.
        private void SkipMetadata()
        {
            if (ReadBits(1) != 0) throw Invalid("a reserved bit that is not zero");
            int bytes = ReadBits(2);
            int skip = 0;
            for (int i = 0; i < bytes; i++)
            {
                int b = ReadBits(8);
                if (i == bytes - 1 && bytes > 1 && b == 0)
                    throw Invalid("a metadata length with a last byte of zero");
                skip |= b << (8 * i);
            }
            if (bytes > 0) skip += 1;
            ExpectZeroFill();
            SkipBytes(skip);
        }

        private void CopyUncompressed(int length)
        {
            ExpectZeroFill();
            Reserve(length);
            ReadBytes(_out, _length, length);
            _length += length;
        }

        // Section 9.2: NBLTYPES, NTREES, 1 to 256.
        private int ReadCount256()
        {
            if (ReadBits(1) == 0) return 1;
            int n = ReadBits(3);
            if (n == 0) return 2;
            return (1 << n) + ReadBits(n) + 1;
        }

        // ---- prefix codes -----------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int ReadSymbol(PrefixCode code)
        {
            if (_count < MaxCodeLength) Refill();
            int[] table = code.Table;
            ulong bits = _bits;
            int entry = table[(int)(bits & ((1 << RootBits) - 1))];
            if ((entry & 0x80) == 0)
            {
                Drop(entry & 0x7F);
                return entry >> 8;
            }
            int sub = entry & 0x7F;
            entry = table[(entry >> 8) + (int)((bits >> RootBits) & ((1UL << sub) - 1))];
            if (entry == Unfilled) throw Invalid("a code that is not in its prefix code");
            Drop(RootBits + (entry & 0x7F));
            return entry >> 8;
        }

        private static int AlphabetBits(int size)
        {
            int bits = 0;
            while ((1 << bits) < size) bits++;
            return bits;
        }

        // Sections 3.4 and 3.5.
        private PrefixCode ReadPrefixCode(int alphabetSize)
        {
            var lengths = new int[alphabetSize];
            int kind = ReadBits(2);
            if (kind == 1)
            {
                int symbols = ReadBits(2) + 1;
                int bits = AlphabetBits(alphabetSize);
                var listed = new int[symbols];
                for (int i = 0; i < symbols; i++)
                {
                    int s = ReadBits(bits);
                    if (s >= alphabetSize) throw Invalid("a symbol outside its alphabet");
                    for (int j = 0; j < i; j++)
                        if (listed[j] == s) throw Invalid("a symbol listed twice");
                    listed[i] = s;
                }
                switch (symbols)
                {
                    case 1:
                        return Single(listed[0]);
                    case 2:
                        lengths[listed[0]] = 1;
                        lengths[listed[1]] = 1;
                        break;
                    case 3:
                        lengths[listed[0]] = 1;
                        lengths[listed[1]] = 2;
                        lengths[listed[2]] = 2;
                        break;
                    default:
                        if (ReadBits(1) == 0)
                        {
                            for (int i = 0; i < 4; i++) lengths[listed[i]] = 2;
                        }
                        else
                        {
                            lengths[listed[0]] = 1;
                            lengths[listed[1]] = 2;
                            lengths[listed[2]] = 3;
                            lengths[listed[3]] = 3;
                        }
                        break;
                }
                return Build(lengths);
            }

            // The code lengths of the code length alphabet, then the code
            // lengths of the alphabet in that code.
            var lengthLengths = new int[18];
            int space = 32;
            int used = 0;
            for (int i = kind; i < 18 && space > 0; i++)
            {
                int v = ReadCodeLengthLength();
                lengthLengths[BrotliTables.CodeLengthOrder[i]] = v;
                if (v != 0)
                {
                    space -= 32 >> v;
                    used++;
                }
            }
            if (used == 0 || (used > 1 && space != 0))
                throw Invalid("a code length code that is not complete");
            PrefixCode lengthCode = Build(lengthLengths);

            int previous = 8;
            int repeat = 0;
            int repeatLength = 0;
            int left = 1 << MaxCodeLength;
            int symbol = 0;
            while (symbol < alphabetSize && left > 0)
            {
                int c = ReadSymbol(lengthCode);
                if (c < 16)
                {
                    repeat = 0;
                    lengths[symbol++] = c;
                    if (c != 0)
                    {
                        previous = c;
                        left -= (1 << MaxCodeLength) >> c;
                    }
                    continue;
                }
                int extra = c == 16 ? 2 : 3;
                int newLength = c == 16 ? previous : 0;
                if (newLength != repeatLength)
                {
                    repeat = 0;
                    repeatLength = newLength;
                }
                int before = repeat;
                if (repeat > 0) repeat = (repeat - 2) << extra;
                repeat += ReadBits(extra) + 3;
                int added = repeat - before;
                if (symbol + added > alphabetSize) throw Invalid("code lengths past their alphabet");
                for (int k = 0; k < added; k++) lengths[symbol++] = repeatLength;
                if (repeatLength != 0) left -= added * ((1 << MaxCodeLength) >> repeatLength);
            }
            if (left != 0) throw Invalid("a prefix code that is not complete");
            return Build(lengths);
        }

        // Section 3.5: the code of the code lengths of the code length
        // alphabet, with bits read in order: 00 is 0, 10 is 4, 01 is 3,
        // 110 is 2, 1110 is 1, 1111 is 5.
        private int ReadCodeLengthLength()
        {
            if (_count < 4) Refill();
            int peek = (int)(_bits & 15);
            switch (peek & 3)
            {
                case 0: Drop(2); return 0;
                case 1: Drop(2); return 4;
                case 2: Drop(2); return 3;
            }
            if ((peek & 4) == 0) { Drop(3); return 2; }
            if ((peek & 8) == 0) { Drop(4); return 1; }
            Drop(4);
            return 5;
        }

        // A code of one symbol, read with no bits.
        private static PrefixCode Single(int symbol)
        {
            var table = new int[1 << RootBits];
            int entry = symbol << 8;
            for (int i = 0; i < table.Length; i++) table[i] = entry;
            return new PrefixCode(table);
        }

        // Section 3.2: the canonical code of a sequence of code lengths, as a
        // table on bits in the order the stream holds them, the first bit of
        // a code lowest.
        private static PrefixCode Build(int[] lengths)
        {
            var count = new int[MaxCodeLength + 1];
            int symbols = 0;
            int lastSymbol = 0;
            for (int s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] == 0) continue;
                count[lengths[s]]++;
                symbols++;
                lastSymbol = s;
            }
            if (symbols == 0) throw Invalid("a prefix code with no symbol");
            if (symbols == 1) return Single(lastSymbol);

            var next = new int[MaxCodeLength + 1];
            int code = 0;
            for (int bits = 1; bits <= MaxCodeLength; bits++)
            {
                code = (code + count[bits - 1]) << 1;
                next[bits] = code;
            }

            var codes = new int[lengths.Length];
            var subBits = new int[1 << RootBits];
            for (int s = 0; s < lengths.Length; s++)
            {
                int len = lengths[s];
                if (len == 0) continue;
                int reversed = Reverse(next[len]++, len);
                codes[s] = reversed;
                if (len > RootBits)
                {
                    int root = reversed & ((1 << RootBits) - 1);
                    subBits[root] = Math.Max(subBits[root], len - RootBits);
                }
            }

            int size = 1 << RootBits;
            var start = new int[1 << RootBits];
            for (int root = 0; root < start.Length; root++)
            {
                if (subBits[root] == 0) continue;
                start[root] = size;
                size += 1 << subBits[root];
            }
            var table = new int[size];
            for (int i = 0; i < table.Length; i++) table[i] = Unfilled;
            for (int root = 0; root < start.Length; root++)
                if (subBits[root] != 0) table[root] = (start[root] << 8) | 0x80 | subBits[root];

            for (int s = 0; s < lengths.Length; s++)
            {
                int len = lengths[s];
                if (len == 0) continue;
                int reversed = codes[s];
                if (len <= RootBits)
                {
                    for (int k = reversed; k < 1 << RootBits; k += 1 << len) table[k] = (s << 8) | len;
                    continue;
                }
                int root2 = reversed & ((1 << RootBits) - 1);
                int width = subBits[root2];
                int extra = len - RootBits;
                for (int k = reversed >> RootBits; k < 1 << width; k += 1 << extra)
                    table[start[root2] + k] = (s << 8) | extra;
            }
            for (int i = 0; i < 1 << RootBits; i++)
                if (table[i] == Unfilled) throw Invalid("a prefix code that is not complete");
            return new PrefixCode(table);
        }

        private static int Reverse(int code, int length)
        {
            int reversed = 0;
            for (int i = 0; i < length; i++)
            {
                reversed = (reversed << 1) | (code & 1);
                code >>= 1;
            }
            return reversed;
        }

        // ---- a compressed meta-block ------------------------------------

        // Section 7.3.
        private byte[] ReadContextMap(int size, int trees)
        {
            var map = new byte[size];
            if (trees < 2) return map;
            int rleMax = ReadBits(1) == 1 ? ReadBits(4) + 1 : 0;
            PrefixCode code = ReadPrefixCode(trees + rleMax);
            for (int i = 0; i < size;)
            {
                int c = ReadSymbol(code);
                if (c == 0)
                {
                    map[i++] = 0;
                }
                else if (c <= rleMax)
                {
                    int run = (1 << c) + ReadBits(c);
                    if (i + run > size) throw Invalid("a context map past its size");
                    i += run;
                }
                else
                {
                    map[i++] = (byte)(c - rleMax);
                }
            }
            if (ReadBits(1) == 1) InverseMoveToFront(map);
            return map;
        }

        private static void InverseMoveToFront(byte[] values)
        {
            var mtf = new byte[256];
            for (int i = 0; i < 256; i++) mtf[i] = (byte)i;
            for (int i = 0; i < values.Length; i++)
            {
                int index = values[i];
                byte value = mtf[index];
                values[i] = value;
                for (; index > 0; index--) mtf[index] = mtf[index - 1];
                mtf[0] = value;
            }
        }

        // The block types and counts of one category (section 6).
        private sealed class Blocks
        {
            public int Types = 1;
            public PrefixCode? TypeCode;
            public PrefixCode? CountCode;
            public int Count;
            public int Type;
            public int Previous = 1;
        }

        private Blocks ReadBlocks()
        {
            var b = new Blocks { Types = ReadCount256() };
            if (b.Types < 2)
            {
                // One type: never a switch.
                b.Count = int.MaxValue;
                return b;
            }
            b.TypeCode = ReadPrefixCode(b.Types + 2);
            b.CountCode = ReadPrefixCode(26);
            b.Count = ReadBlockCount(b.CountCode);
            return b;
        }

        private int ReadBlockCount(PrefixCode code)
        {
            int c = ReadSymbol(code);
            return BrotliTables.BlockCountBase[c] + ReadBits(BrotliTables.BlockCountExtra[c]);
        }

        private void SwitchBlock(Blocks b)
        {
            int t = ReadSymbol(b.TypeCode!);
            if (t == 0) t = b.Previous;
            else if (t == 1) t = b.Type + 1 < b.Types ? b.Type + 1 : 0;
            else t -= 2;
            b.Previous = b.Type;
            b.Type = t;
            b.Count = ReadBlockCount(b.CountCode!);
        }

        private void MetaBlock(int length)
        {
            Blocks literals = ReadBlocks();
            Blocks commands = ReadBlocks();
            Blocks distances = ReadBlocks();

            int postfix = ReadBits(2);
            int direct = ReadBits(4) << postfix;
            int postfixMask = (1 << postfix) - 1;

            var modes = new int[literals.Types];
            for (int i = 0; i < modes.Length; i++) modes[i] = ReadBits(2);

            int literalTrees = ReadCount256();
            byte[] literalMap = ReadContextMap(64 * literals.Types, literalTrees);
            int distanceTrees = ReadCount256();
            byte[] distanceMap = ReadContextMap(4 * distances.Types, distanceTrees);

            var literalCodes = new PrefixCode[literalTrees];
            for (int i = 0; i < literalTrees; i++) literalCodes[i] = ReadPrefixCode(256);
            var commandCodes = new PrefixCode[commands.Types];
            for (int i = 0; i < commands.Types; i++) commandCodes[i] = ReadPrefixCode(704);
            var distanceCodes = new PrefixCode[distanceTrees];
            int distanceAlphabet = 16 + direct + (48 << postfix);
            for (int i = 0; i < distanceTrees; i++) distanceCodes[i] = ReadPrefixCode(distanceAlphabet);

            byte[] lut0 = BrotliTables.ContextLut0;
            byte[] lut1 = BrotliTables.ContextLut1;
            byte[] lut2 = BrotliTables.ContextLut2;

            int end = _length + length;
            Reserve(length);
            byte[] output = _out;
            int mode = modes[0];
            int literalBase = 0;

            while (_length < end)
            {
                if (commands.Count == 0) SwitchBlock(commands);
                commands.Count--;
                int command = ReadSymbol(commandCodes[commands.Type]);
                int cell = command >> 6;
                int insertCode = BrotliTables.CellInsert[cell] + ((command >> 3) & 7);
                int copyCode = BrotliTables.CellCopy[cell] + (command & 7);
                int insert = BrotliTables.InsertBase[insertCode] + ReadBits(BrotliTables.InsertExtra[insertCode]);
                int copy = BrotliTables.CopyBase[copyCode] + ReadBits(BrotliTables.CopyExtra[copyCode]);

                if (insert > end - _length) throw Invalid("literals past the meta-block");
                int at = _length;
                int p1 = at > 0 ? output[at - 1] : 0;
                int p2 = at > 1 ? output[at - 2] : 0;
                for (int j = 0; j < insert; j++)
                {
                    if (literals.Count == 0)
                    {
                        SwitchBlock(literals);
                        mode = modes[literals.Type];
                        literalBase = 64 * literals.Type;
                    }
                    literals.Count--;
                    int context = mode switch
                    {
                        0 => p1 & 0x3f,
                        1 => p1 >> 2,
                        2 => lut0[p1] | lut1[p2],
                        _ => (lut2[p1] << 3) | lut2[p2],
                    };
                    int literal = ReadSymbol(literalCodes[literalMap[literalBase + context]]);
                    output[at++] = (byte)literal;
                    p2 = p1;
                    p1 = literal;
                }
                _length = at;
                if (_length == end) break;

                int distance;
                bool fromRing = command < 128;
                if (fromRing)
                {
                    distance = _distances[_last];
                }
                else
                {
                    if (distances.Count == 0) SwitchBlock(distances);
                    distances.Count--;
                    int context = copy > 4 ? 3 : copy - 2;
                    int dcode = ReadSymbol(distanceCodes[distanceMap[4 * distances.Type + context]]);
                    distance = Distance(dcode, direct, postfix, postfixMask);
                    fromRing = dcode == 0;
                }

                int reach = Math.Min(_window, _length);
                if (distance <= reach)
                {
                    if (!fromRing) _distances[_last = (_last + 1) & 3] = distance;
                    if (copy > end - _length) throw Invalid("a copy past the meta-block");
                    int from = _length - distance;
                    int to = _length;
                    if (distance >= copy)
                    {
                        Buffer.BlockCopy(output, from, output, to, copy);
                    }
                    else
                    {
                        for (int k = 0; k < copy; k++) output[to + k] = output[from + k];
                    }
                    _length += copy;
                }
                else
                {
                    Word(distance - reach - 1, copy, end);
                }
            }
        }

        // Section 4.
        private int Distance(int dcode, int direct, int postfix, int postfixMask)
        {
            int d;
            if (dcode < 16)
            {
                int last = _distances[_last];
                int second = _distances[(_last + 3) & 3];
                d = dcode switch
                {
                    0 => last,
                    1 => second,
                    2 => _distances[(_last + 2) & 3],
                    3 => _distances[(_last + 1) & 3],
                    4 => last - 1,
                    5 => last + 1,
                    6 => last - 2,
                    7 => last + 2,
                    8 => last - 3,
                    9 => last + 3,
                    10 => second - 1,
                    11 => second + 1,
                    12 => second - 2,
                    13 => second + 2,
                    14 => second - 3,
                    _ => second + 3,
                };
                if (d <= 0) throw Invalid("a distance of zero or less");
                return d;
            }
            if (dcode < 16 + direct) return dcode - 15;
            int x = dcode - direct - 16;
            int bits = 1 + (x >> (postfix + 1));
            int high = x >> postfix;
            int low = x & postfixMask;
            int offset = ((2 + (high & 1)) << bits) - 4;
            return ((offset + ReadBits(bits)) << postfix) + low + direct + 1;
        }

        // Section 8: a word of the static dictionary, transformed.
        private void Word(int wordId, int length, int end)
        {
            if (length < 4 || length > 24) throw Invalid("a dictionary word of a length it has none of");
            int bits = BrotliTables.DictionaryBits[length];
            int index = wordId & ((1 << bits) - 1);
            int transformId = wordId >> bits;
            if (transformId > 120) throw Invalid("a word transform past the last");
            byte[] dictionary = BrotliTables.Dictionary;
            int offset = BrotliTables.DictionaryOffsets[length] + index * length;
            BrotliTables.Transform t = BrotliTables.WordTransforms[transformId];

            int skip = 0;
            int keep = length;
            if (t.Kind >= 3 && t.Kind <= 11) skip = Math.Min(t.Kind - 2, length);
            else if (t.Kind >= 12) keep = Math.Max(0, length - (t.Kind - 11));
            int wordLength = keep - skip;
            if (wordLength < 0) wordLength = 0;
            int total = t.Prefix.Length + wordLength + t.Suffix.Length;
            if (total > end - _length) throw Invalid("a dictionary word past the meta-block");

            byte[] output = _out;
            int at = _length;
            Buffer.BlockCopy(t.Prefix, 0, output, at, t.Prefix.Length);
            at += t.Prefix.Length;
            int wordAt = at;
            if (wordLength > 0) Buffer.BlockCopy(dictionary, offset + skip, output, at, wordLength);
            at += wordLength;
            if (t.Kind == 1)
            {
                if (wordLength > 0) Ferment(output, wordAt, wordLength, 0);
            }
            else if (t.Kind == 2)
            {
                for (int i = 0; i < wordLength;) i += Ferment(output, wordAt, wordLength, i);
            }
            Buffer.BlockCopy(t.Suffix, 0, output, at, t.Suffix.Length);
            _length = at + t.Suffix.Length;
        }

        // Section 8: Ferment, on the word at output[start..start + length).
        private static int Ferment(byte[] output, int start, int length, int pos)
        {
            int b = output[start + pos];
            if (b < 192)
            {
                if (b >= 97 && b <= 122) output[start + pos] = (byte)(b ^ 32);
                return 1;
            }
            if (b < 224)
            {
                if (pos + 1 < length) output[start + pos + 1] ^= 32;
                return 2;
            }
            if (pos + 2 < length) output[start + pos + 2] ^= 5;
            return 3;
        }

        // ---- output -----------------------------------------------------

        private void Reserve(int more)
        {
            long need = (long)_length + more;
            if (need <= _out.Length) return;
            if (need > int.MaxValue - 64) throw Invalid("output larger than this decoder holds");
            long grown = Math.Max(need, Math.Min((long)_out.Length * 2, int.MaxValue - 64));
            var bigger = new byte[grown];
            Buffer.BlockCopy(_out, 0, bigger, 0, _length);
            _out = bigger;
        }
    }

    private static InvalidDataException Invalid(string what)
        => new($"Brotli stream: {what}.");
}

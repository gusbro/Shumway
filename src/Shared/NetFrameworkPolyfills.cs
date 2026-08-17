#if NETFRAMEWORK
// Types the C# compiler expects to find but .NET Framework does not ship.
//
// None of these carry behaviour: `init` accessors and `[DoesNotReturn]` are
// compiler-only, and the compiler only needs the type to EXIST to emit or read
// the metadata. Declaring them here is the standard way to use those features on
// a runtime that predates them, and costs nothing at run time.
//
// Deliberately not a NuGet package: three empty declarations are smaller than a
// dependency, and a dependency would also have to be redistributed.

namespace System.Runtime.CompilerServices
{
    /// <summary>What the compiler emits into the modreq of an <c>init</c>
    /// accessor. Its presence is the whole contract.</summary>
    internal static class IsExternalInit { }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Lets a parameter default to the SOURCE TEXT of another argument,
    /// which is how ThrowIfNull below knows the name to blame.</summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName)
            => ParameterName = parameterName;

        public string ParameterName { get; }
    }
}

namespace System
{
    /// <summary>Statics .NET Framework's own types lack, added back where they
    /// belong as C# 14 STATIC EXTENSION MEMBERS — extending a type rather than
    /// an instance, which older C# could not do. That is what lets every call
    /// site stay exactly as it is, on both targets, with no conditional
    /// compilation anywhere but here.
    ///
    /// <para>In namespace <c>System</c> deliberately: extension lookup only
    /// finds a provider whose namespace is imported, and <c>using System</c> is
    /// the one import every file has.</para></summary>
    internal static class BclStaticPolyfills
    {
        extension(ArgumentNullException)
        {
            public static void ThrowIfNull(
                object? argument,
                [Runtime.CompilerServices.CallerArgumentExpression(nameof(argument))]
                string? paramName = null)
            {
                if (argument is null) throw new ArgumentNullException(paramName);
            }
        }

        extension(ArgumentException)
        {
            public static void ThrowIfNullOrEmpty(
                string? argument,
                [Runtime.CompilerServices.CallerArgumentExpression(nameof(argument))]
                string? paramName = null)
            {
                if (argument is null) throw new ArgumentNullException(paramName);
                if (argument.Length == 0)
                    throw new ArgumentException("The value cannot be empty.", paramName);
            }
        }

        extension(OperatingSystem)
        {
            /// <summary>.NET Framework runs on exactly one OS.</summary>
            public static bool IsWindows() => true;

            public static bool IsBrowser() => false;

            public static bool IsMacOS() => false;

            public static bool IsLinux() => false;

            public static bool IsFreeBSD() => false;
        }

        extension(Text.Encoding)
        {
            /// <summary>ISO-8859-1, framework-inbox by codepage.</summary>
            public static Text.Encoding Latin1 => Text.Encoding.GetEncoding(28591);
        }

        extension(int)
        {
            /// <summary>The span TryParse. ToString allocates, but every caller
            /// is parsing a name or a header, not a token stream.</summary>
            public static bool TryParse(ReadOnlySpan<char> s, out int result)
                => int.TryParse(s.ToString(), out result);
        }

        extension(GC)
        {
            /// <summary>Cumulative allocated bytes. Framework's equivalent is the
            /// AppDomain monitoring counter; enabling monitoring is one-way and
            /// process-wide, flipped lazily on first use.</summary>
            public static long GetTotalAllocatedBytes(bool precise = false)
            {
                if (!AppDomain.MonitoringIsEnabled) AppDomain.MonitoringIsEnabled = true;
                return AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            }
        }

        extension(Math)
        {
            public static int Clamp(int value, int min, int max)
                => value < min ? min : value > max ? max : value;

            public static long Clamp(long value, long min, long max)
                => value < min ? min : value > max ? max : value;

            public static double Clamp(double value, double min, double max)
                => value < min ? min : value > max ? max : value;
        }

        extension(Environment)
        {
            /// <summary>Monotonic milliseconds that do not wrap in 25 days.</summary>
            public static long TickCount64
                => System.Diagnostics.Stopwatch.GetTimestamp() * 1000L
                   / System.Diagnostics.Stopwatch.Frequency;

            public static int ProcessId
                => System.Diagnostics.Process.GetCurrentProcess().Id;

            public static string? ProcessPath
                => System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        }

        extension(BitConverter)
        {
            public static float Int32BitsToSingle(int value)
                => new FloatIntUnion { Int = value }.Float;

            public static int SingleToInt32Bits(float value)
                => new FloatIntUnion { Float = value }.Int;
        }

        extension(Security.Cryptography.SHA256)
        {
            public static byte[] HashData(byte[] source)
            {
                using var sha = Security.Cryptography.SHA256.Create();
                return sha.ComputeHash(source);
            }
        }
    }

    /// <summary>Bit-reinterpretation without unsafe code: two fields on the
    /// same bytes.</summary>
    [Runtime.InteropServices.StructLayout(Runtime.InteropServices.LayoutKind.Explicit)]
    internal struct FloatIntUnion
    {
        [Runtime.InteropServices.FieldOffset(0)] public int Int;
        [Runtime.InteropServices.FieldOffset(0)] public float Float;
    }

    /// <summary>The .NET 5+ public reference-equality comparer.</summary>
    internal sealed class ReferenceEqualityComparer
        : Collections.Generic.IEqualityComparer<object?>,
          Collections.IEqualityComparer
    {
        private ReferenceEqualityComparer() { }

        public static ReferenceEqualityComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object? obj)
            => Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>Re-opened to keep the extension blocks above tidy; C# merges
    /// partial-free static classes per compilation, so this second class exists
    /// only to host the collection extensions.</summary>
    internal static class BclStaticPolyfills2
    {
        /// <summary>ConditionalWeakTable.AddOrUpdate (.NET Core 2.0+): remove
        /// then add, which is what the real one does without the lock we cannot
        /// reach.</summary>
        public static void AddOrUpdate<TKey, TValue>(
            this Runtime.CompilerServices.ConditionalWeakTable<TKey, TValue> table,
            TKey key, TValue value)
            where TKey : class where TValue : class?
        {
            table.Remove(key);
            table.Add(key, value!);
        }

        /// <summary>Task.WaitAsync (.NET 6+), the timeout half: completes with
        /// the task, or throws <see cref="TimeoutException"/>. The task itself
        /// keeps running past the timeout — same as the real one.</summary>
        public static async Threading.Tasks.Task WaitAsync(
            this Threading.Tasks.Task task, TimeSpan timeout)
        {
            if (await Threading.Tasks.Task.WhenAny(
                    task, Threading.Tasks.Task.Delay(timeout)) != task)
                throw new TimeoutException();
            await task;
        }

        extension(Convert)
        {
            public static string ToHexString(byte[] bytes)
            {
                var chars = new char[bytes.Length * 2];
                for (int i = 0; i < bytes.Length; i++)
                {
                    chars[i * 2] = "0123456789ABCDEF"[bytes[i] >> 4];
                    chars[i * 2 + 1] = "0123456789ABCDEF"[bytes[i] & 0xF];
                }
                return new string(chars);
            }
        }

        extension(Array)
        {
            public static void Fill<T>(T[] array, T value)
            {
                for (int i = 0; i < array.Length; i++) array[i] = value;
            }

            /// <summary>The one-argument Clear — the framework only has the
            /// three-argument form.</summary>
            public static void Clear(Array array)
                => Array.Clear(array, 0, array.Length);
        }
    }

    /// <summary><c>System.HashCode</c>, reduced to the static Combine shapes
    /// this codebase calls. The same KIND of mix, not the same numbers — nothing
    /// here persists a hash, so only the distribution matters.</summary>
    internal static class HashCode
    {
        private static int Mix(int acc, object? value)
        {
            unchecked { return acc * 31 + (value?.GetHashCode() ?? 0); }
        }

        public static int Combine<T1, T2>(T1 a, T2 b)
            => Mix(Mix(17, a), b);

        public static int Combine<T1, T2, T3>(T1 a, T2 b, T3 c)
            => Mix(Mix(Mix(17, a), b), c);

        public static int Combine<T1, T2, T3, T4>(T1 a, T2 b, T3 c, T4 d)
            => Mix(Mix(Mix(Mix(17, a), b), c), d);
    }
}

namespace System.Collections.Generic
{
    /// <summary>Instance conveniences Framework's collections lack:
    /// KeyValuePair deconstruction (`foreach (var (k, v) in dict)`) and
    /// <c>Dictionary.TryAdd</c>.</summary>
    internal static class CollectionPolyfills
    {
        public static void Deconstruct<TKey, TValue>(
            this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }

        public static bool TryAdd<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
            where TKey : notnull
        {
            if (dictionary.ContainsKey(key)) return false;
            dictionary.Add(key, value);
            return true;
        }

        public static TValue? GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary, TKey key)
            where TKey : notnull
            => dictionary.TryGetValue(key, out TValue? value) ? value : default;

        public static TValue GetValueOrDefault<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
            where TKey : notnull
            => dictionary.TryGetValue(key, out TValue? value) ? value : defaultValue;

        /// <summary><c>Remove(key, out value)</c> — the instance Remove takes one
        /// argument, so a two-argument call falls through to this.</summary>
        public static bool Remove<TKey, TValue>(
            this Dictionary<TKey, TValue> dictionary, TKey key, out TValue value)
            where TKey : notnull
        {
            if (dictionary.TryGetValue(key, out value!))
            {
                dictionary.Remove(key);
                return true;
            }
            return false;
        }
    }
}

namespace System
{
    /// <summary>Instance conveniences newer BCLs added to string. The char
    /// overloads matter beyond convenience on modern .NET (no culture, no
    /// allocation); here they just have to exist and mean the same thing.</summary>
    internal static class StringPolyfills
    {
        public static bool StartsWith(this string s, char c)
            => s.Length > 0 && s[0] == c;

        public static bool EndsWith(this string s, char c)
            => s.Length > 0 && s[s.Length - 1] == c;

        public static bool Contains(this string s, char c)
            => s.IndexOf(c) >= 0;

        public static string[] Split(this string s, char separator, int count)
            => s.Split(new[] { separator }, count);

        public static string[] Split(this string s, char separator, StringSplitOptions options)
            => s.Split(new[] { separator }, options);

        public static string[] Split(this string s, string separator,
            StringSplitOptions options = StringSplitOptions.None)
            => s.Split(new[] { separator }, options);
    }
}

namespace System.Linq
{
    /// <summary>The tuple-projecting Zip (.NET Core 3.0+).</summary>
    internal static class LinqPolyfills
    {
        public static IEnumerable<(TFirst First, TSecond Second)> Zip<TFirst, TSecond>(
            this IEnumerable<TFirst> first, IEnumerable<TSecond> second)
            => first.Zip(second, (a, b) => (a, b));
    }
}

namespace System.Reflection
{
    /// <summary>The generic CreateDelegate (.NET 5+): the cast the caller would
    /// otherwise write, in the place they expect not to have to.</summary>
    internal static class MethodInfoPolyfills
    {
        public static T CreateDelegate<T>(this MethodInfo method) where T : Delegate
            => (T)method.CreateDelegate(typeof(T));

        public static T CreateDelegate<T>(this MethodInfo method, object? target) where T : Delegate
            => (T)method.CreateDelegate(typeof(T), target);
    }
}

namespace System.Runtime.InteropServices
{
    /// <summary>The .NET Core native-library loader, backed by the Win32 API —
    /// which is the only platform .NET Framework runs on, so LoadLibrary IS the
    /// general case here rather than the Windows special case.</summary>
    internal static class NativeLibrary
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr module);

        public static IntPtr Load(string libraryPath)
        {
            IntPtr handle = LoadLibraryW(libraryPath);
            if (handle == IntPtr.Zero)
                throw new DllNotFoundException(
                    $"Unable to load DLL '{libraryPath}' (error {Marshal.GetLastWin32Error()}).");
            return handle;
        }

        public static bool TryLoad(string libraryPath, out IntPtr handle)
        {
            handle = LoadLibraryW(libraryPath);
            return handle != IntPtr.Zero;
        }

        public static IntPtr GetExport(IntPtr handle, string name)
        {
            IntPtr export = GetProcAddress(handle, name);
            if (export == IntPtr.Zero)
                throw new EntryPointNotFoundException($"Unable to find entry point '{name}'.");
            return export;
        }

        public static bool TryGetExport(IntPtr handle, string name, out IntPtr address)
        {
            address = GetProcAddress(handle, name);
            return address != IntPtr.Zero;
        }

        public static void Free(IntPtr handle)
        {
            if (handle != IntPtr.Zero) FreeLibrary(handle);
        }
    }
}

namespace System.Numerics
{
    /// <summary>The two bit-scan operations the arithmetic evaluator uses.
    /// Plain loops rather than intrinsics — Framework's JIT has no BMI
    /// lowering to hand them to anyway, and these sit on the bignum slow
    /// path, not the integer fast lane.</summary>
    internal static class BitOperations
    {
        public static int LeadingZeroCount(ulong value)
        {
            if (value == 0) return 64;
            int count = 0;
            while ((value & 0x8000_0000_0000_0000UL) == 0) { count++; value <<= 1; }
            return count;
        }

        public static int TrailingZeroCount(ulong value)
        {
            if (value == 0) return 64;
            int count = 0;
            while ((value & 1) == 0) { count++; value >>= 1; }
            return count;
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>The metadata behind the `required` keyword. Compiler-only, like
    /// IsExternalInit: the enforcement happens at compile time in whoever
    /// constructs the object, and the attributes just record the contract.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct
                    | AttributeTargets.Field | AttributeTargets.Property,
                    Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
            => FeatureName = featureName;

        public string FeatureName { get; }
        public bool IsOptional { get; init; }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Pairs with `required`: a constructor so marked promises it set
    /// them all, which frees its callers from the object-initializer rule.</summary>
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }

    /// <summary>Marks a method that never returns, so the compiler's definite-
    /// assignment and reachability analysis can rely on it.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class DoesNotReturnAttribute : Attribute { }
}

namespace System
{
    /// <summary>What `span[offset..]` compiles to. Only the members the
    /// compiler emits are here — the slicing itself is Span's own.</summary>
    internal readonly struct Index
    {
        private readonly int _value;   // negative-encoded when from the end

        public Index(int value, bool fromEnd = false)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            _value = fromEnd ? ~value : value;
        }

        public int Value => _value < 0 ? ~_value : _value;
        public bool IsFromEnd => _value < 0;

        public static Index Start => new(0);
        public static Index End => new(0, fromEnd: true);
        public static Index FromStart(int value) => new(value);
        public static Index FromEnd(int value) => new(value, fromEnd: true);
        public static implicit operator Index(int value) => new(value);

        public int GetOffset(int length) => IsFromEnd ? length - Value : Value;
    }

    internal readonly struct Range
    {
        public Range(Index start, Index end) { Start = start; End = end; }

        public Index Start { get; }
        public Index End { get; }

        public static Range StartAt(Index start) => new(start, Index.End);
        public static Range EndAt(Index end) => new(Index.Start, end);
        public static Range All => new(Index.Start, Index.End);

        /// <summary>The compiler calls this to turn a range into a slice.</summary>
        public (int Offset, int Length) GetOffsetAndLength(int length)
        {
            int start = Start.GetOffset(length);
            int end = End.GetOffset(length);
            if ((uint)end > (uint)length || (uint)start > (uint)end)
                throw new ArgumentOutOfRangeException(nameof(length));
            return (start, end - start);
        }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    /// <summary>Trimming metadata, all of it. .NET Framework has no trimmer, so
    /// every one of these is inert here — they exist so the annotated code
    /// compiles unchanged. The annotations were earned on the wasm target
    /// (phase 38) and must not be lost to this one.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field,
                    Inherited = false)]
    internal sealed class FeatureSwitchDefinitionAttribute : Attribute
    {
        public FeatureSwitchDefinitionAttribute(string switchName)
            => SwitchName = switchName;

        public string SwitchName { get; }
    }

    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0002 | PublicParameterlessConstructor,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None,
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.ReturnValue
                    | AttributeTargets.GenericParameter | AttributeTargets.Parameter
                    | AttributeTargets.Property | AttributeTargets.Method
                    | AttributeTargets.Class | AttributeTargets.Interface
                    | AttributeTargets.Struct, Inherited = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes)
            => MemberTypes = memberTypes;

        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor
                    | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute : Attribute
    {
        public RequiresUnreferencedCodeAttribute(string message) => Message = message;

        public string Message { get; }
        public string? Url { get; set; }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class UnconditionalSuppressMessageAttribute : Attribute
    {
        public UnconditionalSuppressMessageAttribute(string category, string checkId)
        {
            Category = category;
            CheckId = checkId;
        }

        public string Category { get; }
        public string CheckId { get; }
        public string? Scope { get; set; }
        public string? Target { get; set; }
        public string? MessageId { get; set; }
        public string? Justification { get; set; }
    }
}
#endif

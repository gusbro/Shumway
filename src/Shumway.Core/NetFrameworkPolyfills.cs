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

namespace Shumway.Core
{
    /// <summary>Statics .NET Framework's own types lack.
    ///
    /// <para><c>ArgumentNullException.ThrowIfNull</c> is added back where it
    /// belongs, as a C# 14 STATIC EXTENSION MEMBER — extending a type rather
    /// than an instance, which older C# could not do. That is what lets 176
    /// call sites stay exactly as they are, on both targets, with no
    /// conditional compilation anywhere but here.</para>
    ///
    /// <para>The other two extend nothing (they would have to be added to
    /// <c>Array</c> and <c>HashCode</c>, whose shapes differ) and are named
    /// plainly.</para></summary>
    internal static class Compat
    {
        extension(ArgumentNullException)
        {
            public static void ThrowIfNull(
                object? argument,
                [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(argument))]
                string? paramName = null)
            {
                if (argument is null) throw new ArgumentNullException(paramName);
            }
        }

        extension(ArgumentException)
        {
            public static void ThrowIfNullOrEmpty(
                string? argument,
                [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(argument))]
                string? paramName = null)
            {
                if (argument is null) throw new ArgumentNullException(paramName);
                if (argument.Length == 0)
                    throw new ArgumentException("The value cannot be empty.", paramName);
            }
        }

        /// <summary><c>Array.Fill</c>.</summary>
        public static void Fill<T>(T[] array, T value)
        {
            for (int i = 0; i < array.Length; i++) array[i] = value;
        }

        /// <summary><c>HashCode.Combine</c> — the same shape of mix, not the same
        /// numbers. Nothing here persists a hash, so only the distribution
        /// matters.</summary>
        public static int Combine<T1, T2>(T1 a, T2 b)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (a?.GetHashCode() ?? 0);
                return h * 31 + (b?.GetHashCode() ?? 0);
            }
        }
    }
}

namespace System.Collections.Generic
{
    /// <summary>`foreach (var (key, value) in dictionary)`. Framework's
    /// KeyValuePair has no Deconstruct; .NET Core's does.</summary>
    internal static class KeyValuePairDeconstruction
    {
        public static void Deconstruct<TKey, TValue>(
            this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
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
    /// <summary>Trimming metadata. .NET Framework has no trimmer, so this is
    /// inert here — it exists so the annotated code compiles unchanged.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field,
                    Inherited = false)]
    internal sealed class FeatureSwitchDefinitionAttribute : Attribute
    {
        public FeatureSwitchDefinitionAttribute(string switchName)
            => SwitchName = switchName;

        public string SwitchName { get; }
    }
}
#endif

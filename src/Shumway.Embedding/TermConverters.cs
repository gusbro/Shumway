using System.Numerics;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>
/// built-in T ↔ <see cref="Term"/> converters used by
/// <see cref="PrologEngine.ToTerm{T}"/>,
/// <see cref="PrologEngine.FromTerm{T}"/> and
/// <see cref="Solution.Get{T}"/> when no user-registered converter is
/// present for the type. Dispatch is via <c>typeof(T)</c> equality so
/// the JIT specialises each generic instantiation down to a single
/// branch — no boxing on the primitive paths.
///
/// <para>The set is deliberately the .NET-primitive scalars: int, long,
/// double, float, bool, char, string, BigInteger, plus a Term-identity
/// passthrough that lets callers write
/// <c>engine.FromTerm&lt;Term&gt;(t)</c> uniformly. Composite mappings
/// (lists, tuples, dictionaries, nullable, user records) belong in a
/// follow-up chunk together with the source generator.</para>
/// </summary>
internal static class TermConverters
{
    /// <summary>Default-direction ToTerm. Returns <c>true</c> when
    /// <typeparamref name="T"/> is a built-in supported scalar and
    /// fills <paramref name="result"/>; <c>false</c> when the type is
    /// not built-in (the caller falls back to user converters or
    /// raises).</summary>
    public static bool TryToTerm<T>(T value, out Term result)
    {
        // Reference-typed Term passthrough first: a caller passing a
        // ready Term (or one of its subclasses) gets it back unchanged.
        if (value is Term direct)
        {
            result = direct;
            return true;
        }

        if (typeof(T) == typeof(int))
        {
            result = new IntTerm((int)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(long))
        {
            result = ToIntOrBig((long)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(short))
        {
            result = new IntTerm((short)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(byte))
        {
            result = new IntTerm((byte)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(uint))
        {
            result = new IntTerm((uint)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(ulong))
        {
            ulong u = (ulong)(object)value!;
            result = u <= long.MaxValue
                ? ToIntOrBig((long)u)
                : new BigIntTerm(new BigInteger(u));
            return true;
        }
        if (typeof(T) == typeof(double))
        {
            result = new FloatTerm((double)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(float))
        {
            result = new FloatTerm((float)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(bool))
        {
            result = new AtomTerm((bool)(object)value! ? "true" : "false");
            return true;
        }
        if (typeof(T) == typeof(char))
        {
            result = new AtomTerm(((char)(object)value!).ToString());
            return true;
        }
        if (typeof(T) == typeof(string))
        {
            // A .NET string is text as a VALUE, and text as a value is an atom
            // (ADR-047 decision 6). A caller who wants text as a SEQUENCE asks
            // for a list; there is no third thing at the boundary.
            result = new AtomTerm((string)(object)value!);
            return true;
        }
        if (typeof(T) == typeof(BigInteger))
        {
            BigInteger bi = (BigInteger)(object)value!;
            result = bi >= long.MinValue && bi <= long.MaxValue
                ? ToIntOrBig((long)bi)
                : new BigIntTerm(bi);
            return true;
        }

        result = null!;
        return false;
    }

    /// <summary>Default-direction FromTerm. Returns <c>true</c> when
    /// <typeparamref name="T"/> is a built-in supported scalar and
    /// fills <paramref name="result"/>; <c>false</c> when the type is
    /// not built-in. A type mismatch (e.g. asking for <c>int</c> from
    /// a <see cref="FloatTerm"/>) throws — the converter only declines
    /// when it doesn't know the target type at all.</summary>
    public static bool TryFromTerm<T>(Term term, out T result)
    {
        // Term-typed pass-through (covers T = Term and any subclass).
        if (typeof(Term).IsAssignableFrom(typeof(T)))
        {
            if (term is T t)
            {
                result = t;
                return true;
            }
            throw new InvalidCastException(
                $"FromTerm<{typeof(T).Name}> received a {term.GetType().Name}; "
                + "the term is not of the requested AST subclass.");
        }

        if (typeof(T) == typeof(int))
        {
            long l = AsLong(term);
            if (l < int.MinValue || l > int.MaxValue)
                throw new OverflowException(
                    $"Integer term {l} does not fit in int.");
            result = (T)(object)(int)l;
            return true;
        }
        if (typeof(T) == typeof(long))
        {
            result = (T)(object)AsLong(term);
            return true;
        }
        if (typeof(T) == typeof(short))
        {
            long l = AsLong(term);
            if (l < short.MinValue || l > short.MaxValue)
                throw new OverflowException(
                    $"Integer term {l} does not fit in short.");
            result = (T)(object)(short)l;
            return true;
        }
        if (typeof(T) == typeof(byte))
        {
            long l = AsLong(term);
            if (l < byte.MinValue || l > byte.MaxValue)
                throw new OverflowException(
                    $"Integer term {l} does not fit in byte.");
            result = (T)(object)(byte)l;
            return true;
        }
        if (typeof(T) == typeof(uint))
        {
            long l = AsLong(term);
            if (l < 0 || l > uint.MaxValue)
                throw new OverflowException(
                    $"Integer term {l} does not fit in uint.");
            result = (T)(object)(uint)l;
            return true;
        }
        if (typeof(T) == typeof(ulong))
        {
            BigInteger b = AsBig(term);
            if (b < 0 || b > ulong.MaxValue)
                throw new OverflowException(
                    $"Integer term {b} does not fit in ulong.");
            result = (T)(object)(ulong)b;
            return true;
        }
        if (typeof(T) == typeof(double))
        {
            result = (T)(object)AsDouble(term);
            return true;
        }
        if (typeof(T) == typeof(float))
        {
            double d = AsDouble(term);
            result = (T)(object)(float)d;
            return true;
        }
        if (typeof(T) == typeof(bool))
        {
            if (term is AtomTerm a)
            {
                if (a.Name == "true") { result = (T)(object)true; return true; }
                if (a.Name == "false") { result = (T)(object)false; return true; }
            }
            throw new InvalidCastException(
                $"Expected the atom 'true' or 'false' for FromTerm<bool>, got {term}.");
        }
        if (typeof(T) == typeof(char))
        {
            if (term is AtomTerm a && a.Name.Length == 1)
            {
                result = (T)(object)a.Name[0];
                return true;
            }
            throw new InvalidCastException(
                $"FromTerm<char> requires a single-character atom, got {term}.");
        }
        if (typeof(T) == typeof(string))
        {
            // An atom, or a list of characters or codes — packed or in cons
            // cells, which reach here identically (ADR-047 decision 6). All of
            // them are text, and all of them give the same C# string, so the
            // same method called down two Prolog paths gets the same argument.
            if (term.TryAsText(out string text)) { result = (T)(object)text; return true; }
            throw new InvalidCastException(
                $"FromTerm<string> expects an atom or a list of text, got {term}.");
        }
        if (typeof(T) == typeof(BigInteger))
        {
            result = (T)(object)AsBig(term);
            return true;
        }

        result = default!;
        return false;
    }

    private static Term ToIntOrBig(long value) => new IntTerm(value);

    private static long AsLong(Term term) => term switch
    {
        IntTerm n => n.Value,
        BigIntTerm b when b.Value >= long.MinValue && b.Value <= long.MaxValue
            => (long)b.Value,
        BigIntTerm b => throw new OverflowException(
            $"Integer term {b.Value} does not fit in long; use FromTerm<BigInteger>."),
        _ => throw new InvalidCastException(
            $"Expected an integer term, got {term.GetType().Name}."),
    };

    private static BigInteger AsBig(Term term) => term switch
    {
        IntTerm n => new BigInteger(n.Value),
        BigIntTerm b => b.Value,
        _ => throw new InvalidCastException(
            $"Expected an integer term, got {term.GetType().Name}."),
    };

    private static double AsDouble(Term term) => term switch
    {
        FloatTerm f => f.Value,
        IntTerm n => (double)n.Value,
        BigIntTerm b => (double)b.Value,
        RationalTerm r => (double)r.Num / (double)r.Den,
        _ => throw new InvalidCastException(
            $"Expected a numeric term, got {term.GetType().Name}."),
    };
}

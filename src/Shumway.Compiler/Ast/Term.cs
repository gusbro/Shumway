using Shumway.Compiler.Lexer;

namespace Shumway.Compiler.Ast;

/// <summary>
/// Base type for the parser's output. A <see cref="Term"/> mirrors Prolog's notion of
/// a syntactic term: atoms, variables, numbers, strings, and compound terms. List
/// syntax (<c>[a, b | T]</c>) and curly-brace syntax (<c>{X}</c>) are desugared into
/// compound terms with functors <c>"."</c> and <c>"{}"</c> respectively.
///
/// <para>Subclasses define <see cref="object.Equals(object?)"/> and
/// <see cref="object.GetHashCode"/> on their structural payload, deliberately
/// excluding <see cref="Position"/> from equality so that two terms with the same
/// shape but different source locations compare equal — vital for tests.</para>
/// </summary>
public abstract class Term
{
    /// <summary>Where in the source the term begins. Excluded from value equality.</summary>
    public SourcePosition Position { get; init; }
}

public sealed class AtomTerm : Term
{
    public string Name { get; }

    // Lazily-cached AtomTable id, stored as id+1 so the field's
    // default (0) can mean "not yet resolved" — atom id 0 itself is valid
    // (it's "[]"). Deliberately NOT part of Equals/GetHashCode: two
    // AtomTerms with the same Name compare equal whether or not either
    // has resolved its id. Atom ids are stable for the atom's lifetime
    // (ADR-003), so a cached id never goes stale; racing writers from two
    // engines store the same value (int writes are atomic).
    private int _atomIdPlusOne;

    public AtomTerm(string name) => Name = name;

    /// <summary>Constructor for builders that already hold the
    /// atom's interned id (e.g. <c>TermReader.Materialize</c> reading a heap
    /// cell), seeding the cache so later consumers skip the by-name intern.</summary>
    public AtomTerm(string name, int atomId)
    {
        Name = name;
        _atomIdPlusOne = atomId + 1;
    }

    /// <summary>The atom's global <c>AtomTable</c> id, interned
    /// (transient tier) on first use and cached on the node. Callers that
    /// require the atom pinned permanent must still promote it themselves
    /// via <c>AtomTable.Intern(name, permanent: true)</c> — promotion keeps
    /// the id, so the cache stays valid across tier changes.</summary>
    public int ResolveAtomId()
    {
        int plusOne = _atomIdPlusOne;
        if (plusOne != 0) return plusOne - 1;
        int id = Shumway.Core.AtomTable.Intern(Name).Id;
        _atomIdPlusOne = id + 1;
        return id;
    }

    public override bool Equals(object? obj) => obj is AtomTerm o && Name == o.Name;
    public override int GetHashCode() => HashCode.Combine(typeof(AtomTerm), Name);
    public override string ToString() => Name;
}

public sealed class VarTerm : Term
{
    public string Name { get; }
    public VarTerm(string name) => Name = name;

    public override bool Equals(object? obj) => obj is VarTerm o && Name == o.Name;
    public override int GetHashCode() => HashCode.Combine(typeof(VarTerm), Name);
    public override string ToString() => Name;
}

public sealed class IntTerm : Term
{
    public long Value { get; }
    public IntTerm(long value) => Value = value;

    public override bool Equals(object? obj) => obj is IntTerm o && Value == o.Value;
    public override int GetHashCode() => HashCode.Combine(typeof(IntTerm), Value);
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>An integer too large for the 60-bit inline cell range. Materialised
/// onto the heap via the engine's BigInteger side-table (<c>Tag.BigInt</c>).
/// Surfaces at the AST layer whenever an arithmetic operation overflows
/// <see cref="IntTerm.Value"/>'s long range or whenever the heap is read back
/// and finds a BIGINT cell.</summary>
public sealed class BigIntTerm : Term
{
    public System.Numerics.BigInteger Value { get; }
    public BigIntTerm(System.Numerics.BigInteger value) => Value = value;

    public override bool Equals(object? obj) => obj is BigIntTerm o && Value == o.Value;
    public override int GetHashCode() => HashCode.Combine(typeof(BigIntTerm), Value);
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class FloatTerm : Term
{
    public double Value { get; }
    public FloatTerm(double value) => Value = value;

    public override bool Equals(object? obj) => obj is FloatTerm o && Value == o.Value;
    public override int GetHashCode() => HashCode.Combine(typeof(FloatTerm), Value);
    public override string ToString() => Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class StringTerm : Term
{
    public string Content { get; }
    public StringTerm(string content) => Content = content;

    public override bool Equals(object? obj) => obj is StringTerm o && Content == o.Content;
    public override int GetHashCode() => HashCode.Combine(typeof(StringTerm), Content);
    public override string ToString() => $"\"{Content}\"";
}

public sealed class CompoundTerm : Term
{
    public string Functor { get; }
    public Term[] Args { get; }

    // Lazily-cached FunctorTable id (stored as id+1; functor
    // id 0 is valid, so 0 means "not yet resolved"). Excluded from value
    // equality, same rationale as AtomTerm's cached atom id.
    private int _functorIdPlusOne;

    public CompoundTerm(string functor, Term[] args)
    {
        Functor = functor;
        Args = args;
    }

    /// <summary>Constructor for builders that already hold the
    /// compound's interned functor id (e.g. <c>TermReader</c> reading a
    /// FUNCTOR heap cell), seeding the cache.</summary>
    public CompoundTerm(string functor, Term[] args, int functorId)
    {
        Functor = functor;
        Args = args;
        _functorIdPlusOne = functorId + 1;
    }

    /// <summary>The compound's global <c>FunctorTable</c> id,
    /// interned on first use (atom transient-tier) and cached on the node.
    /// Functor ids are canonical — one id per (atom, arity) pair — so a
    /// direct id comparison is equivalent to comparing name and arity.</summary>
    public int ResolveFunctorId()
    {
        int plusOne = _functorIdPlusOne;
        if (plusOne != 0) return plusOne - 1;
        int id = Shumway.Core.FunctorTable.Intern(
            Shumway.Core.AtomTable.Intern(Functor).Id, Args.Length);
        _functorIdPlusOne = id + 1;
        return id;
    }

    public override bool Equals(object? obj)
    {
        if (obj is not CompoundTerm o) return false;
        if (Functor != o.Functor) return false;
        if (Args.Length != o.Args.Length) return false;
        for (int i = 0; i < Args.Length; i++)
            if (!Args[i].Equals(o.Args[i])) return false;
        return true;
    }

    public override int GetHashCode() => HashCode.Combine(typeof(CompoundTerm), Functor, Args.Length);

    public override string ToString()
    {
        if (Args.Length == 0) return Functor;
        return $"{Functor}({string.Join(", ", Args.Select(a => a.ToString()))})";
    }
}

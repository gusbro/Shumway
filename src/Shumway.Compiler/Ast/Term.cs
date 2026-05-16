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
    public AtomTerm(string name) => Name = name;

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

    public CompoundTerm(string functor, Term[] args)
    {
        Functor = functor;
        Args = args;
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

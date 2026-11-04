using System;
using System.IO;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>atomic_list_concat/2,3 join in one pass and split in one scan:
/// one atom interned for a join, one per part for a split, where the Prolog
/// folds interned an atom per element (and per separator) and the split
/// re-enumerated every occurrence of the separator for each part. The
/// rest pins every mode and error of both predicates.</summary>
public class AtomicListConcatLinearTests
{
    private static bool Holds(string query) => new PrologEngine().Query(query).Success;

    [Fact]
    public void AJoinInternsOneAtom_NotOnePerElement()
    {
        // The folds interned the text of each number and two intermediates
        // per element; now the result is the only new atom. Measured as the
        // difference between a 2,000-element join and a 20-element one in
        // the same engine, after a first join has paid whatever a first
        // call interns; the atom GC can only lower a count in between.
        var e = new PrologEngine();
        long Interned(int n, string join) => e.Query(
            $"numlist(1, {n}, L), statistics(atoms, [A0, _]), {join}, "
            + "statistics(atoms, [A1, _]), D is A1 - A0.").Get<long>("D");
        Interned(20, "atomic_list_concat(L, '-', _)");
        long small = Interned(20, "atomic_list_concat(L, '-', _)");
        long big = Interned(2000, "atomic_list_concat(L, '-', _)");
        Assert.True(big - small <= 2, $"a 2,000-element join interned {big} atoms, a 20-element one {small}");
        Interned(20, "atomic_list_concat(L, _)");
        small = Interned(20, "atomic_list_concat(L, _)");
        big = Interned(2000, "atomic_list_concat(L, _)");
        Assert.True(big - small <= 2, $"a 2,000-element join interned {big} atoms, a 20-element one {small}");
        Assert.True(Holds(
            "numlist(1, 2000, L), atomic_list_concat(L, '-', R), atom_length(R, Len), Len == 8892, "
            + "atomic_list_concat(L, R2), atom_length(R2, Len2), Len2 == 6893."));
    }

    [Fact]
    public void ASplitOfFortyThousandPartsIsOneScan()
    {
        // The split used to enumerate every remaining occurrence of the
        // separator for each part (n²/2 sub_atom solutions: 8·10⁸ here,
        // three minutes) and intern the remaining tail at every step. One
        // scan takes milliseconds, and interns one atom per part. The
        // deadline is far above that and well below the quadratic scan.
        var e = new PrologEngine();
        bool? result = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                result = e.Query(
                    "numlist(1, 40000, L), atomic_list_concat(L, '-', R), "
                    + "statistics(atoms, [A0, _]), atomic_list_concat(Parts, '-', R), "
                    + "statistics(atoms, [A1, _]), D is A1 - A0, D =< 40100, "
                    + "maplist(atom_number, Parts, Ns), Ns == L.").Success;
            }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "the split did not finish in a minute");
        Assert.Null(error);
        Assert.True(result);
    }

    [Theory]
    [InlineData("atomic_list_concat([foo, bar, baz], A), A == foobarbaz.")]
    [InlineData("atomic_list_concat([a, 1, 2.5, -3, 'B c', [], 1.0e10], A), A == 'a12.5-3B c[]10000000000.0'.")]
    [InlineData("atomic_list_concat([a, 123456789012345678901234567890], A), A == 'a123456789012345678901234567890'.")]
    [InlineData("atomic_list_concat([-0.5, x], A), A == '-0.5x'.")]
    [InlineData("atomic_list_concat([], A), A == ''.")]
    [InlineData("atomic_list_concat([a, b], ab).")]
    [InlineData("\\+ atomic_list_concat([a, b], abc).")]
    [InlineData("atomic_list_concat([a, b, c], '-', A), A == 'a-b-c'.")]
    [InlineData("atomic_list_concat([a, b, c], 1, A), A == a1b1c.")]
    [InlineData("atomic_list_concat([a, b, c], 2.5, A), A == 'a2.5b2.5c'.")]
    [InlineData("atomic_list_concat([a, b, c], '', A), A == abc.")]
    [InlineData("atomic_list_concat([], '-', A), A == ''.")]
    [InlineData("atomic_list_concat([x], '-', A), A == x.")]
    [InlineData("atomic_list_concat([a, b], '-', 'a-b').")]
    [InlineData("\\+ atomic_list_concat([a, b], '-', 'a+b').")]
    public void JoinModes(string query) => Assert.True(Holds(query));

    [Theory]
    [InlineData("catch(atomic_list_concat([a, b], 1), error(type_error(atom, 1), atomic_list_concat/2), true).")]
    [InlineData("catch(atomic_list_concat([a, _], _), error(instantiation_error, atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat([a|_], _), error(instantiation_error, atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat(_, _), error(instantiation_error, atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat(foo, _), error(type_error(list, foo), atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat([f(x)], _), error(type_error(atomic, f(x)), atomic_list_concat/3), true).")]
    // A packed string is a list (ADR-047), not an atomic.
    [InlineData("catch(atomic_list_concat([\"ab\"], _), error(type_error(atomic, [a, b]), atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat([a, b], _, _), error(instantiation_error, atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat([a, b], f(x), _), error(type_error(atomic, f(x)), atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat([a, b], \"-\", _), error(type_error(atomic, [-]), atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat([a, b], '-', 1), error(type_error(atom, 1), atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat([a|_], '-', 'a-b'), error(instantiation_error, atomic_list_concat/3), true).")]
    public void JoinErrors(string query) => Assert.True(Holds(query));

    [Theory]
    [InlineData("atomic_list_concat(L, '-', 'a-b-c'), L == [a, b, c].")]
    [InlineData("atomic_list_concat(L, '-', '-a--b-'), L == ['', a, '', b, ''].")]
    [InlineData("atomic_list_concat(L, '-', ''), L == [''].")]
    [InlineData("atomic_list_concat(L, '-', abc), L == [abc].")]
    [InlineData("atomic_list_concat(L, aa, aaa), L == ['', a].")]
    [InlineData("atomic_list_concat(L, ', ', 'one, two, three'), L == [one, two, three].")]
    [InlineData("atomic_list_concat(L, '😀', 'a😀b'), L == [a, b].")]
    [InlineData("atomic_list_concat(L, ab, 'xabyabz'), L == [x, y, z].")]
    public void SplitModes(string query) => Assert.True(Holds(query));

    [Theory]
    // The split direction needs atoms on both sides, in any dialect.
    [InlineData("catch(atomic_list_concat(_, '-', 123), error(type_error(atom, 123), _), true).")]
    [InlineData("catch(atomic_list_concat(_, '-', f(x)), error(type_error(atom, f(x)), _), true).")]
    [InlineData("catch(atomic_list_concat(_, 1, 'a1b'), error(type_error(atom, 1), _), true).")]
    // An empty or unbound separator is the join direction, which wants the list.
    [InlineData("catch(atomic_list_concat(_, '', 'a-b'), error(instantiation_error, atomic_list_concat/3), true).")]
    [InlineData("catch(atomic_list_concat(_, _, 'a-b'), error(instantiation_error, atomic_list_concat/3), true).")]
    public void SplitErrors(string query) => Assert.True(Holds(query));

    [Fact]
    public void SplitErrorsNameTheCalledPredicate()
    {
        // The scan is no longer sub_atom/5's, so neither is the context.
        Assert.True(Holds(
            "catch(atomic_list_concat(_, '-', 123), error(type_error(atom, 123), atomic_list_concat/3), true)."));
        Assert.True(Holds(
            "catch(atomic_list_concat(_, 1, 'a1b'), error(type_error(atom, 1), atomic_list_concat/3), true)."));
    }

    [Fact]
    public void AnSwiCallerGetsTheSameErrors()
    {
        // ADR-040 leniency reaches atom_concat/3, not these: unchanged.
        string tmp = Path.Combine(Path.GetTempPath(), "alc-swi-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "alcmod.pl"),
                ":- module(alcmod, [split_num/2, join_str/1]).\n"
                + "split_num(A, L) :- atomic_list_concat(L, 1, A).\n"
                + "join_str(A) :- atomic_list_concat([a, \"bc\"], A).\n");
            var e = new PrologEngine();
            e.AddLibraryDirectory(tmp, "swi");
            e.ConsultString(":- use_module(library(alcmod)).");
            Assert.True(e.Query(
                "catch(split_num('a1b', _), error(type_error(atom, 1), _), true).").Success);
            Assert.True(e.Query(
                "catch(join_str(_), error(type_error(atomic, _), atomic_list_concat/3), true).").Success);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}

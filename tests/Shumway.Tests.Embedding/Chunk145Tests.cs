using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// Chunk 145: SWI/GProlog-compat predicates a user program (in the
/// 2016 GProlog year-arithmetic puzzle script) needed:
/// <c>nb_setval/2</c>, <c>nb_getval/2</c>, <c>nb_current/2</c>,
/// <c>b_setval/2</c>, <c>b_getval/2</c>, <c>get_time/1</c>,
/// <c>stamp_date_time/3</c>, <c>name/2</c>,
/// <c>read_term_from_atom/3</c>, plus the historical
/// <c>assert/1</c> alias for <c>assertz/1</c>.
/// </summary>
public class Chunk145Tests
{
    private static AtomTerm Atom(string n) => new(n);
    private static IntTerm Int(long v) => new(v);

    // ---------- nb_setval / nb_getval ----------

    [Fact]
    public void NbSetval_NbGetval_Roundtrip()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("nb_setval(counter, 42), nb_getval(counter, X), X == 42.").Success);
    }

    [Fact]
    public void NbSetval_AcrossQueries_Persists()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("nb_setval(saved, hello).").Success);
        var sol = e.Query("nb_getval(saved, V).");
        Assert.True(sol.Success);
        Assert.Equal(Atom("hello"), sol["V"]);
    }

    [Fact]
    public void NbGetval_Unset_RaisesExistenceError()
    {
        var e = new PrologEngine();
        var sol = e.Query("catch(nb_getval(no_such, _), error(existence_error(_, _), _), true).");
        Assert.True(sol.Success);
    }

    [Fact]
    public void NbCurrent_OnUnset_FailsRatherThanThrows()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("nb_current(no_such, _).").Success);
    }

    // ---------- get_time ----------

    [Fact]
    public void GetTime_ReturnsRecentEpoch()
    {
        var e = new PrologEngine();
        // The time is a float; pin only that it's plausible
        // (post-2024-01-01 epoch ~= 1.7e9 seconds).
        var sol = e.Query("get_time(T), T > 1700000000.0.");
        Assert.True(sol.Success);
    }

    // ---------- stamp_date_time ----------

    [Fact]
    public void StampDateTime_ProducesDateCompound()
    {
        var e = new PrologEngine();
        // 0 = the Unix epoch; UTC time zone.
        var sol = e.Query(
            "stamp_date_time(0.0, date(Y, Mo, D, H, _Mi, _S, _Off, _Tz, _DST), 'UTC').");
        Assert.True(sol.Success);
        Assert.Equal(Int(1970), sol["Y"]);
        Assert.Equal(Int(1), sol["Mo"]);
        Assert.Equal(Int(1), sol["D"]);
        Assert.Equal(Int(0), sol["H"]);
    }

    // ---------- name/2 ----------

    [Fact]
    public void Name2_AtomToCodes()
    {
        var e = new PrologEngine();
        var sol = e.Query("name(abc, Codes).");
        Assert.True(sol.Success);
        // [97, 98, 99] — pin elementally.
        Assert.True(e.Query("name(abc, [97, 98, 99]).").Success);
    }

    [Fact]
    public void Name2_IntToCodes()
    {
        var e = new PrologEngine();
        Assert.True(e.Query("name(42, [52, 50]).").Success);  // '4' = 52, '2' = 50
    }

    [Fact]
    public void Name2_CodesToAtom()
    {
        var e = new PrologEngine();
        var sol = e.Query("name(X, [104, 105]).");  // "hi"
        Assert.True(sol.Success);
        Assert.Equal(Atom("hi"), sol["X"]);
    }

    [Fact]
    public void Name2_NumericCodes_ParseToNumber()
    {
        var e = new PrologEngine();
        var sol = e.Query("name(X, [49, 50, 51]).");  // "123"
        Assert.True(sol.Success);
        Assert.Equal(Int(123), sol["X"]);
    }

    // ---------- read_term_from_atom/3 ----------

    [Fact]
    public void ReadTermFromAtom3_OptionsListIgnored()
    {
        var e = new PrologEngine();
        var sol = e.Query("read_term_from_atom('foo(1, 2)', T, []).");
        Assert.True(sol.Success);
        var t = Assert.IsType<CompoundTerm>(sol["T"]);
        Assert.Equal("foo", t.Functor);
    }

    // ---------- assert/1 alias ----------

    [Fact]
    public void Assert1_IsAliasForAssertz()
    {
        var e = new PrologEngine();
        e.ConsultString(":- dynamic d/1.");
        Assert.True(e.Query("assert(d(1)), assert(d(2)).").Success);
        var xs = e.QueryAll("d(X).").Select(s => s["X"]).ToList();
        Assert.Equal(new[] { Int(1), Int(2) }, xs);
    }
}

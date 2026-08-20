using Shumway.Core;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

// ---- A class with both included and ignored members. ----
[PrologTerm("c243_user")]
public partial class C243User
{
    public string Name { get; set; } = "";
    public int Age { get; set; }

    // Skipped from the Prolog representation. Round-tripping keeps
    // .NET-side state in C# only; on decode, this stays at its
    // default value.
    [PrologTermIgnore]
    public DateTime LastSeen { get; set; }

    [PrologTermIgnore]
    public string InternalNote { get; set; } = "default-note";
}

// ---- Ignored field. ----
[PrologTerm("c243_box")]
public partial class C243Box
{
    public int Value { get; set; }

    [PrologTermIgnore]
    public int Cached;  // public field — would normally be picked up
}

/// <summary>
/// Chunk 243: <c>[PrologTermIgnore]</c> — opts a single field or
/// property out of the <c>[PrologTerm]</c> mapping. The compound
/// term's arity excludes ignored members; the decoder leaves them
/// at their .NET default value.
/// </summary>
public class Chunk243Tests
{
    [Fact]
    public void Encoder_OmitsIgnoredMember()
    {
        var engine = new PrologEngine();
        var u = new C243User
        {
            Name = "alice",
            Age = 30,
            LastSeen = new DateTime(2026, 1, 1),
            InternalNote = "should-not-leak",
        };
        var t = (CompoundTerm)engine.ToTerm(u);
        Assert.Equal("c243_user", t.Functor);
        Assert.Equal(2, t.Args.Length);  // Name + Age only
        Assert.Equal("alice", ((AtomTerm)t.Args[0]).Name);
        Assert.Equal(30L, ((IntTerm)t.Args[1]).Value);
    }

    [Fact]
    public void Decoder_LeavesIgnoredMembersAtDefault()
    {
        var engine = new PrologEngine();
        var t = new CompoundTerm("c243_user", new Term[]
        {
            new StringTerm("bob", TextKind.Codes),
            new IntTerm(42),
        });
        var u = engine.FromTerm<C243User>(t);
        Assert.Equal("bob", u.Name);
        Assert.Equal(42, u.Age);
        Assert.Equal(default(DateTime), u.LastSeen);
        Assert.Equal("default-note", u.InternalNote);  // C# initialiser, not Prolog
    }

    [Fact]
    public void IgnoredField_AlsoSkipped()
    {
        var engine = new PrologEngine();
        var b = new C243Box { Value = 7, Cached = 999 };
        var t = (CompoundTerm)engine.ToTerm(b);
        Assert.Equal("c243_box", t.Functor);
        Assert.Single(t.Args);
        Assert.Equal(7L, ((IntTerm)t.Args[0]).Value);
    }

    [Fact]
    public void RoundTrip_PreservesPrologFields_DropsIgnored()
    {
        var engine = new PrologEngine();
        var u = new C243User
        {
            Name = "carol",
            Age = 25,
            LastSeen = new DateTime(2026, 5, 30),
            InternalNote = "lost-on-roundtrip",
        };
        var back = engine.FromTerm<C243User>(engine.ToTerm(u));
        Assert.Equal(u.Name, back.Name);
        Assert.Equal(u.Age, back.Age);
        // Ignored members come back as defaults (not as u's value).
        Assert.NotEqual(u.LastSeen, back.LastSeen);
        Assert.NotEqual(u.InternalNote, back.InternalNote);
    }

    [Fact]
    public void QueryGet_DecodesIgnoringExtraFields()
    {
        // The Prolog source defines c243_user/2 — the arity matches
        // the post-ignore mapping.
        var engine = new PrologEngine();
        engine.ConsultString("""
            :- public people/1.
            people(c243_user(diana, 31)).
            """);
        var sol = engine.Query("people(P).");
        var u = sol.Get<C243User>("P");
        Assert.Equal("diana", u.Name);
        Assert.Equal(31, u.Age);
    }
}

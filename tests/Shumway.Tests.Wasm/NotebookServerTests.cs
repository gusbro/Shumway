using System.Text.Json;
using System.Text.Json.Nodes;
using Shumway.Jupyter;
using Shumway.WebHost;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-067: what the notebook server does that the reference's
/// answers do not say: its transport, the text of its errors, the page's
/// input, and what one session keeps from another.</summary>
public sealed class NotebookServerTests : IDisposable
{
    private readonly NotebookHarness _notebooks = new();

    public void Dispose() => _notebooks.Dispose();

    private static string Call(int id, string code)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "call", @params = new { code } });

    private static JsonNode Answer(NotebookServer server, string request)
    {
        string? line = NotebookHarness.Ask(server, request);
        Assert.NotNull(line);
        return JsonNode.Parse(line)!;
    }

    /// <summary>The entry of the first query of a cell.</summary>
    private static JsonNode First(NotebookServer server, string code)
        => Answer(server, Call(1, code))["result"]!["1"]!;

    private static string Message(JsonNode entry)
        => (string)entry["error"]!["data"]!["prolog_message"]!;

    [Theory]
    [InlineData(@"X is foo + 1.", "error: type_error(evaluable, foo/0) in is/2")]
    [InlineData(@"atom_length(X, Y).", "error: instantiation_error in atom_length/2")]
    [InlineData(@"no_such_predicate_here(1).", "error: existence_error(procedure, no_such_predicate_here/1)")]
    [InlineData(@"throw(my_error).", "error: my_error")]
    [InlineData(@"throw(f('A b', X)).", "error: f('A b', _)")]
    [InlineData(@"W = $Unknown.", "$Unknown has no value from an earlier query")]
    public void AnError_HasTheTextATopLevelShows(string code, string expected)
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode entry = First(server, code);

        Assert.Equal(-4712, (int)entry["error"]!["code"]!);
        string message = System.Text.RegularExpressions.Regex.Replace(Message(entry), @"_G?\d+", "_");
        Assert.Equal(expected, message);
    }

    [Fact]
    public void ASyntaxError_NamesItsPlaceInTheCell()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode answer = Answer(server, Call(7, "X = 1.\nfoo(."));

        Assert.Equal(7, (int)answer["id"]!);
        Assert.Equal(-4712, (int)answer["error"]!["code"]!);
        string message = (string)answer["error"]!["data"]!["prolog_message"]!;
        Assert.StartsWith("syntax error: 2:", message);
    }

    [Fact]
    public void TheProtocol_IsNamed()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode answer = Answer(server, """{"jsonrpc":"2.0","id":0,"method":"protocol"}""");

        Assert.Equal(1, (int)answer["result"]!["version"]!);
        Assert.Equal("shumway", (string)answer["result"]!["implementation"]!);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("42")]
    [InlineData("""{"id":3}""")]
    [InlineData("")]
    public void ALineThatIsNoRequest_IsRefused_AndTheServerGoesOn(string line)
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode refused = Answer(server, line);
        JsonNode next = First(server, "X = 1.");

        Assert.Contains((int)refused["error"]!["code"]!, new[] { -32600, -32700 });
        Assert.Equal("1", (string)next["bindings"]!["X"]!);
    }

    [Fact]
    public void Text_GoesThroughJsonBothWays()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        // A quote, a backslash, a tab, a control character, a line end, and
        // a character above the basic plane, which JSON sends as two escapes.
        const string code = "X = 'q\"\\\\\\t', atom_length('\U0001F600', N), put_char('\\x1\\'), nl, write('é\U0001F600').";

        JsonNode entry = First(server, code);

        Assert.Equal("success", (string)entry["status"]!);
        Assert.Equal("1", (string)entry["bindings"]!["N"]!);
        Assert.Equal("'q\"\\\\\\t'", (string)entry["bindings"]!["X"]!);
        Assert.Equal("\u0001\né\U0001F600", (string)entry["output"]!);
    }

    [Fact]
    public void OutputOfAQuery_IsItsOwn()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode answer = Answer(server, Call(1, "write(one), nl, nl.\nwrite(user_error, two).\ntrue."))["result"]!;

        // One final line end goes, as in the reference; the rest is the query's.
        Assert.Equal("one\n", (string)answer["1"]!["output"]!);
        Assert.Equal("two", (string)answer["2"]!["output"]!);
        Assert.Equal("", (string)answer["3"]!["output"]!);
    }

    [Fact]
    public void ACellThatReads_AsksThePage_AndASecondRequestWaitsItsTurn()
    {
        var page = new NotebookHarness.Page();
        using var asked = new ManualResetEventSlim();
        page.OnAsk = asked.Set;
        (NotebookServer server, HostSession session) = _notebooks.Open("a", page);

        Task<string?> answer = server.Request(Call(1, "read(X)."));
        Assert.True(asked.Wait(NotebookHarness.Patience), "the page was not asked for input");
        Assert.Throws<InvalidOperationException>(() => { _ = server.Request(Call(2, "true.")); });
        session.Input.Supply("hello(world).");

        JsonNode entry = JsonNode.Parse(NotebookHarness.Result(answer)!)!["result"]!["1"]!;
        Assert.Equal("hello(world)", (string)entry["bindings"]!["X"]!);
    }

    [Fact]
    public void ACancelledCell_StopsTheServer()
    {
        (NotebookServer server, HostSession session) = _notebooks.Open("a");
        Assert.Equal("1", (string)First(server, "X = 1.")["bindings"]!["X"]!);

        Task<string?> answer = server.Request(Call(2, "repeat, fail."));
        // Seen at a safe point of the search, whenever it arrives.
        session.Cancel();

        Assert.Null(NotebookHarness.Result(answer));
        Assert.True(NotebookHarness.Finished(server.Ended));
        Assert.Null(NotebookHarness.Ask(server, Call(3, "true.")));
    }

    [Fact]
    public void Close_EndsTheLoop()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        Assert.Equal("1", (string)First(server, "X = 1.")["bindings"]!["X"]!);

        server.Close();

        Assert.True(NotebookHarness.Finished(server.Ended));
        Assert.Null(server.Failure);
        Assert.Null(NotebookHarness.Ask(server, Call(2, "true.")));
    }

    [Fact]
    public void ASession_KeepsItsValuesAndItsOutputToItself()
    {
        NotebookServer a = _notebooks.Open("a").Server;
        NotebookServer b = _notebooks.Open("b").Server;

        First(a, "X = from_a, assertz(seen(a)).");
        JsonNode value = First(b, "Y = $X.");
        JsonNode fact = First(b, "seen(Who).");
        First(a, "member(X, [1, 2, 3]).");
        JsonNode retry = First(b, "retry.");
        JsonNode mine = First(a, "retry.");

        Assert.Equal("$X has no value from an earlier query", Message(value));
        Assert.Contains("existence_error", Message(fact));
        Assert.Equal(-4713, (int)retry["error"]!["code"]!);
        Assert.Equal("2", (string)mine["bindings"]!["X"]!);
    }

    [Fact]
    public void ACellMagic_WritesItsFileInTheSessionsDirectory()
    {
        NotebookServer a = _notebooks.Open("a").Server;
        NotebookServer b = _notebooks.Open("b").Server;

        JsonNode saved = Answer(a, Call(1, "%%save notes.txt\nfirst line\nsecond ("))["result"]!;
        First(a, "%%file facts.pl\nfact(1).");
        First(a, "%%file+ facts.pl\nfact(2).");
        JsonNode mine = First(a, "findall(X, fact(X), L).");
        JsonNode theirs = First(b, "fact(X).");

        Assert.Empty(saved.AsObject());
        string Text(string name) => File.ReadAllText(Path.Combine(_notebooks.DirectoryOf("a"), name)).Replace("\r\n", "\n");
        Assert.Equal("first line\nsecond (\n", Text("notes.txt"));
        Assert.Equal("fact(1).\nfact(2).\n", Text("facts.pl"));
        Assert.Equal("[1,2]", (string)mine["bindings"]!["L"]!);
        Assert.Contains("existence_error", Message(theirs));
        Assert.False(File.Exists(Path.Combine(_notebooks.DirectoryOf("b"), "facts.pl")));
    }

    [Fact]
    public void ACellThatDoesNotParse_IsAnError_AndLoadsNothing()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode load = First(server, "%%user\nloaded(1).\nbroken(.\nloaded(2).");
        JsonNode query = First(server, "loaded(X).");
        // The load of a program, not of a cell, still reports and goes on.
        JsonNode consult = First(server, "consult('user.pl'), findall(X, loaded(X), L).");

        Assert.Equal(-4712, (int)load["error"]!["code"]!);
        Assert.StartsWith("syntax error: ", Message(load));
        Assert.Contains("user.pl:2:", Message(load));
        Assert.Contains("existence_error", Message(query));
        Assert.StartsWith("[1", (string)consult["bindings"]!["L"]!);
        Assert.Contains("syntax error: ", (string)consult["output"]!);
    }

    [Fact]
    public void ASavedTable_IsAFileWithItsValuesQuotedWhereTheyMustBe()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        string Text(string name) => File.ReadAllText(Path.Combine(_notebooks.DirectoryOf("a"), name)).Replace("\r\n", "\n");

        First(server, @"print_and_save_table(member(X-Y, [1-plain, 2-'a,b', 3-'say ""hi""', 4-'two words']), csv, 'out.csv').");
        First(server, "%%tsv out.tsv\nmember(X-Y, [1-'a,b', 2-'tab\\there']).");

        // A value is its printed form: the quotes of an atom are part of it.
        Assert.Equal("X,Y\n1,plain\n2,\"'a,b'\"\n3,\"'say \"\"hi\"\"'\"\n4,'two words'\n", Text("out.csv"));
        Assert.Equal("X\tY\n1\t'a,b'\n2\t'tab\\there'\n", Text("out.tsv"));
    }

    [Fact]
    public void ATableAskedForByAProgram_NamesItsColumnsWithLetters()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        First(server, "%%user\nshow(L) :- jupyter:print_table(member(_-_, L)).\nboth(L) :- print_table(member(_, L)), write(done).");

        JsonNode pairs = First(server, "show([1-a, 2-b]).");
        JsonNode both = First(server, "both([x]), jupyter:print_table([[1, 2]], ['P', 'Q']).");

        Assert.Equal("""{"ValuesLists":[["1","a"],["2","b"]],"VariableNames":["A","B"]}""", pairs["print_table"]!.ToJsonString());
        // The latest table of a query is the one its answer carries.
        Assert.Equal("""{"ValuesLists":[["1","2"]],"VariableNames":["P","Q"]}""", both["print_table"]!.ToJsonString());
        Assert.Equal("done", (string)both["output"]!);
    }

    [Fact]
    public void ADeterministicQuery_IsNotKeptAlive()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        long Stack() => long.Parse((string)First(server, "statistics(local_stack, [Used, _]).")["bindings"]!["Used"]!);

        // A query that left no choice point holds no loop of its own: the
        // stack of a notebook does not grow with its cells.
        long before = Stack();
        for (int cell = 0; cell < 20; cell++)
        {
            string code = string.Join('\n', Enumerable.Range(0, 100).Select(n => $"X{n} = {n}, atom_length(abc, _)."));
            Assert.Equal("success", (string)Answer(server, Call(cell, code))["result"]!["100"]!["status"]!);
        }
        long after = Stack();
        // Queries left alive, to show the measure sees a loop when there is one.
        for (int cell = 0; cell < 3; cell++)
        {
            string code = string.Join('\n', Enumerable.Range(0, 100).Select(n => $"member(X{n}, [{n}, z])."));
            Assert.Equal("success", (string)Answer(server, Call(cell, code))["result"]!["100"]!["status"]!);
        }
        long alive = Stack();

        Assert.True(after - before < 4096, $"2000 deterministic queries grew the stack by {after - before} bytes");
        Assert.True(alive - after > 300 * 64, $"300 live queries grew the stack by only {alive - after} bytes");
    }
}

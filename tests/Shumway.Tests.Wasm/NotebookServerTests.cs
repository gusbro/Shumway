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

    // Text as it is, where the default writes < and ' as escapes.
    private static readonly JsonSerializerOptions Plain =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

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

    /// <summary>A call as a kernel sends it from the notebook
    /// <paramref name="notebook"/>.</summary>
    private static string CallFrom(string notebook, string code)
        => JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "call", @params = new { code, notebook } });

    private string TextIn(string folder, string name)
        => File.ReadAllText(Path.Combine(_notebooks.DirectoryOf(folder), name)).Replace("\r\n", "\n");

    [Theory]
    [InlineData("notes.ipynb", "notes.user.pl")]
    [InlineData("two words.ipynb", "two words.user.pl")]
    [InlineData("untitled", "untitled.user.pl")]
    [InlineData("", "user.pl")]
    public void TheUserFile_IsNamedAfterTheNotebook(string notebook, string file)
    {
        NotebookServer server = _notebooks.Open("a").Server;

        Answer(server, CallFrom(notebook, "%%user\nnamed(1)."));

        Assert.Equal("named(1).\n", TextIn("a", file));
        Assert.Single(Directory.GetFiles(_notebooks.DirectoryOf("a")));
    }

    [Fact]
    public void TwoNotebooksInOneDirectory_KeepTheirUserFilesApart()
    {
        NotebookServer a = _notebooks.Open("a", folder: "shared").Server;
        NotebookServer b = _notebooks.Open("b", folder: "shared").Server;

        Answer(a, CallFrom("first.ipynb", "%%user\nmine(a)."));
        Answer(b, CallFrom("second.ipynb", "%%user\nmine(b)."));
        Answer(b, CallFrom("second.ipynb", "%%user+\nmine(c)."));
        JsonNode inA = Answer(a, CallFrom("first.ipynb", "findall(X, mine(X), L)."))["result"]!["1"]!;
        JsonNode inB = Answer(b, CallFrom("second.ipynb", "findall(X, mine(X), L)."))["result"]!["1"]!;

        Assert.Equal("[a]", (string)inA["bindings"]!["L"]!);
        Assert.Equal("[b,c]", (string)inB["bindings"]!["L"]!);
        Assert.Equal("mine(a).\n", TextIn("shared", "first.user.pl"));
        Assert.Equal("mine(b).\nmine(c).\n", TextIn("shared", "second.user.pl"));
        Assert.False(File.Exists(Path.Combine(_notebooks.DirectoryOf("shared"), "user.pl")));
    }

    [Fact]
    public void ARenamedNotebook_TakesItsUserFileAlong()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        Answer(server, CallFrom("before.ipynb", "%%user\np(1)."));
        Answer(server, CallFrom("after.ipynb", "%%user+\np(2)."));
        JsonNode all = Answer(server, CallFrom("after.ipynb", "findall(X, p(X), L)."))["result"]!["1"]!;

        Assert.Equal("[1,2]", (string)all["bindings"]!["L"]!);
        Assert.Equal("p(1).\np(2).\n", TextIn("a", "after.user.pl"));
        Assert.False(File.Exists(Path.Combine(_notebooks.DirectoryOf("a"), "before.user.pl")));
    }

    [Fact]
    public void ARenameOntoAFileThatExists_LeavesBothFilesAsTheyWere()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        File.WriteAllText(Path.Combine(_notebooks.DirectoryOf("a"), "taken.user.pl"), "q(0).\n");

        Answer(server, CallFrom("mine.ipynb", "%%user\nq(1)."));
        Answer(server, CallFrom("taken.ipynb", "%%user+\nq(2)."));

        Assert.Equal("q(1).\n", TextIn("a", "mine.user.pl"));
        Assert.Equal("q(0).\nq(2).\n", TextIn("a", "taken.user.pl"));
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
    public void WhatAQueryDisplays_ComesWithItsAnswer_InTheOrderShown()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        First(server, "%%user\n" + @"dot(C) :- atom_concat('<svg><circle fill=""', C, A), atom_concat(A, '""/></svg>', Svg), jupyter:display('image/svg+xml', Svg).");

        JsonNode two = First(server, @"dot(red), jupyter:display(""text/markdown"", ""**done**""), X = 1.");
        JsonNode none = First(server, "X = 2.");

        Assert.Equal(
            """[{"mime":"image/svg+xml","data":"<svg><circle fill=\"red\"/></svg>"},{"mime":"text/markdown","data":"**done**"}]""",
            two["display_data"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        Assert.Equal("1", (string)two["bindings"]!["X"]!);
        // What one query displayed is not the next one's.
        Assert.Null(none["display_data"]);
    }

    [Theory]
    [InlineData(@"jupyter:display(_, text).", "instantiation_error")]
    [InlineData(@"jupyter:display('text/html', 12).", "type_error(text")]
    [InlineData(@"jupyter:display('text/html', [a|_]).", "type_error(text")]
    public void WhatIsNotText_IsNotDisplayed(string code, string expected)
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode answer = First(server, code);

        Assert.Equal("error", (string)answer["status"]!);
        Assert.Contains(expected, answer["error"]!.ToJsonString());
    }

    private static string Output(NotebookServer server, int id, string code)
        => (string)Answer(server, Call(id, code))["result"]!["1"]!["output"]!;

    [Fact]
    public void WhatASessionRemembers_IsShown()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        Answer(server, Call(1, "X = f(a), Y = [1, 2]."));
        Answer(server, Call(2, "member(Z, [p, q])."));
        Answer(server, Call(3, "fail."));
        Answer(server, Call(4, "X = 'B c'."));

        string bindings = Output(server, 5, "%bindings");
        string queries = Output(server, 6, "%queries");
        string some = Output(server, 7, "print_queries([2, 4]).");
        string time = Output(server, 8, "jupyter:print_query_time.");

        // The latest value of each, in the order they were first given one.
        Assert.Equal("$X = 'B c'\n$Y = [1,2]\n$Z = p", bindings);
        // The queries that had a solution, as they were written.
        Assert.Equal("X=f(a),Y=[1,2],\nmember(Z,[p,q]),\nX='B c',\njupyter:print_variable_bindings.", queries);
        Assert.Equal("member(Z,[p,q]),\nX='B c'.", some);
        Assert.Matches(@"^Query:   print_queries\(\[2,4\]\)\nRuntime: [0-9.eE+-]+ s$", time);
    }

    [Fact]
    public void TheDirectoryOfASession_IsItsOwn()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        string directory = _notebooks.DirectoryOf("a");
        string process = Directory.GetCurrentDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "inner"));

        string before = Output(server, 1, "%pwd");
        string after = Output(server, 2, "jupyter:cd(inner), jupyter:pwd.");
        Answer(server, Call(3, "%%save here.txt\ntext"));

        Assert.Equal($"Working directory: '{directory.Replace('\\', '/')}/'", before);
        Assert.Equal($"Working directory: '{directory.Replace('\\', '/')}/inner/'", after);
        Assert.True(File.Exists(Path.Combine(directory, "inner", "here.txt")));
        Assert.Equal(process, Directory.GetCurrentDirectory());
    }

    [Fact]
    public void TheVersions_AreTheEngines()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        string versions = Output(server, 1, "%versions");
        JsonNode parts = First(server, "jupyter:version(Major, Minor, Patch, Status).")["bindings"]!;
        string number = (string)Answer(server, """{"jsonrpc":"2.0","id":0,"method":"version"}""")["result"]!;

        Assert.Equal($"Shumway Prolog {number}\nShumway notebook server {number}", versions);
        Assert.Equal(number, $"{(string)parts["Major"]!}.{(string)parts["Minor"]!}.{(string)parts["Patch"]!}");
        Assert.NotEqual("", (string)parts["Status"]!);
    }

    [Fact]
    public void EveryHelperAndEveryMagic_IsDescribed()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        string[] helpers =
        {
            "halt/0", "retry/0", "help/0", "magic/0", "version/0", "version/4", "versions/0", "pwd/0", "cd/1",
            "print_variable_bindings/0", "print_queries/0", "print_queries/1", "print_query_time/0",
            "print_table/1", "print_table/2", "print_and_save_table/3", "trace/1", "display/2",
            "update_completion_data/0",
        };
        string[] magics =
        {
            "%%user", "%%user+", "%%file FILE", "%%file+ FILE", "%%load FILE", "%%save FILE", "%%table",
            "%%csv FILE", "%%tsv FILE", "%%highlight",
            "%bindings", "%queries", "%pwd", "%help", "%versions", "%flags", "%magic",
        };

        JsonObject docs = Answer(server, """{"jsonrpc":"2.0","id":0,"method":"jupyter_predicate_docs"}""")["result"]!.AsObject();
        string help = Output(server, 1, "%help");
        string magic = Output(server, 2, "%magic");
        // A helper called with what it does not take is told by its own name.
        JsonNode called = First(server, "jupyter:no_such_helper.");

        Assert.Equal(helpers.Select(helper => "jupyter:" + helper).Order(), docs.Select(doc => doc.Key).Order());
        foreach (var (key, text) in docs)
        {
            string name = key["jupyter:".Length..key.IndexOf('/')];
            Assert.StartsWith("jupyter:" + name, (string)text!);
            Assert.Contains((string)text!, help);
        }
        foreach (string line in magics) Assert.Contains($"\n    {line}\n", magic);
        Assert.Contains("existence_error", Message(called));
    }

    [Fact]
    public void TheFlags_AreATable()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode table = First(server, "%flags")["print_table"]!;

        Assert.Equal("""["Name","Value"]""", table["VariableNames"]!.ToJsonString());
        Assert.Contains(table["ValuesLists"]!.AsArray(), row => row!.ToJsonString() == """["bounded","false"]""");
        Assert.Contains(table["ValuesLists"]!.AsArray(), row => (string)row![0]! == "double_quotes");
    }

    [Theory]
    [InlineData(@"X in 1..5.", "{}", @"[""X in 1..5""]")]
    [InlineData(@"X in 1..5, X #> 2.", "{}", @"[""X in 3..5""]")]
    [InlineData(@"X in 1..3, Y in 1..3, X #< Y.", "{}", @"[""X in 1..2"",""X#<Y"",""Y in 2..3""]")]
    [InlineData(@"X in 1..5, L = [X, a].", @"{""L"":""[X,a]""}", @"[""X in 1..5""]")]
    [InlineData(@"X in 1..5, Y = X.", @"{""Y"":""X""}", @"[""X in 1..5""]")]
    [InlineData(@"dif(X, a).", "{}", @"[""dif(X,a)""]")]
    [InlineData(@"freeze(X, write('A b')).", "{}", @"[""freeze(X,write('A b'))""]")]
    [InlineData(@"X in 1..5, X = 3.", @"{""X"":""3""}", null)]
    [InlineData(@"X = f(_), Y = 1.", @"{""X"":""f(_)"",""Y"":""1""}", null)]
    public void TheConstraintsLeftOnAQuerysVariables_AreItsResidualGoals(string code, string bindings, string? residual)
    {
        NotebookServer server = _notebooks.Open("a").Server;
        First(server, "use_module(library(clpfd)), use_module(library(coroutining)).");

        JsonNode entry = First(server, code);

        Assert.Equal("success", (string)entry["status"]!);
        Assert.Equal(bindings, System.Text.RegularExpressions.Regex.Replace(entry["bindings"]!.ToJsonString(), @"_G?\d+", "_"));
        Assert.Equal(residual, entry["residual_goals"]?.ToJsonString(Plain));
    }

    [Fact]
    public void ResidualGoals_FollowEachSolution()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        First(server, "use_module(library(clpfd)).");

        JsonNode first = First(server, "X in 1..9, member(Low, [3, 7]), X #> Low.");
        JsonNode second = First(server, "retry.");

        Assert.Equal("""["X in 4..9"]""", first["residual_goals"]!.ToJsonString());
        Assert.Equal("""["X in 8..9"]""", second["residual_goals"]!.ToJsonString());
    }

    [Theory]
    [InlineData("foo(X) :- bar(X).")]
    [InlineData(":- dynamic(foo/1).")]
    [InlineData("greeting --> [hello].")]
    public void AClauseInAnOrdinaryCell_IsAnErrorThatSaysWhereClausesGo(string clause)
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode answer = Answer(server, Call(1, $"X = 1.\n{clause}\nY = 2."))["result"]!;

        Assert.Equal("1", (string)answer["1"]!["bindings"]!["X"]!);
        Assert.Equal(-4712, (int)answer["2"]!["error"]!["code"]!);
        Assert.Equal("A clause is not a query. To define predicates, start the cell with %%user.", Message(answer["2"]!));
        Assert.Equal("2", (string)answer["3"]!["bindings"]!["Y"]!);
    }

    private const string Append = "%%user\napp([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).";

    private static string Unnamed(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"_G?\d+", "_");

    [Fact]
    public void TraceOfAGoal_PrintsItsPorts_AndRunsItOnce()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        First(server, Append);

        JsonNode traced = First(server, "jupyter:trace(app([1], [2], L)), write(after).");
        JsonNode failed = First(server, "jupyter:trace(app([1], [2], [3])), write(after).");
        JsonNode first = First(server, "jupyter:trace(member(X, [a, b])).");
        JsonNode again = First(server, "retry.");

        Assert.Equal("[1,2]", (string)traced["bindings"]!["L"]!);
        Assert.Equal(
            "   Call: (1) app([1], [2], _)\n   Call: (1) app([], [2], _)\n   Exit: (1) app([], [2], [2])\nafter",
            Unnamed((string)traced["output"]!));
        // A goal that fails does not fail the query.
        Assert.Equal("success", (string)failed["status"]!);
        Assert.Equal("   Call: (1) app([1], [2], [3])\n   Fail: (1) app([1], [2], [3])\nafter", (string)failed["output"]!);
        Assert.Equal("a", (string)first["bindings"]!["X"]!);
        Assert.Equal(-4711, (int)again["error"]!["code"]!);
    }

    [Fact]
    public void TheTracer_IsOffWhenAQueryEnds()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        First(server, Append);

        JsonNode raised = First(server, "jupyter:trace(( app([1], [2], L), X is foo + L )).");
        JsonNode afterAnError = First(server, "app([1], [2], L).");
        JsonNode leftOn = First(server, "trace, app([1], [2], L).");
        JsonNode afterASolution = First(server, "app([1], [2], L).");
        JsonNode failedOn = First(server, "trace, app([1], [2], [3]).");
        JsonNode afterAFailure = First(server, "app([1], [2], L).");
        JsonNode alone = First(server, "trace.");

        Assert.Equal(-4712, (int)raised["error"]!["code"]!);
        Assert.StartsWith("   Call: (1) app([1], [2], _)\n", Unnamed((string)raised["error"]!["data"]!["output"]!));
        Assert.Equal("", (string)afterAnError["output"]!);
        // Of the query's own goals, and nothing of what the server runs after them.
        Assert.Equal(
            "   Call: (1) app([1], [2], _)\n   Call: (1) app([], [2], _)\n   Exit: (1) app([], [2], [2])",
            Unnamed((string)leftOn["output"]!));
        Assert.Equal("", (string)afterASolution["output"]!);
        Assert.Equal(-4711, (int)failedOn["error"]!["code"]!);
        Assert.Equal("", (string)afterAFailure["output"]!);
        Assert.Equal(-4712, (int)alone["error"]!["code"]!);
        Assert.Equal(
            "The tracer does not stay on after a query. To trace a goal, call jupyter:trace(Goal).",
            Message(alone));
    }

    [Fact]
    public void TheNamesToCompleteWith_AreTheHelpersTheEnginesAndTheSessions()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        string[] Names() => First(server, "jupyter:update_completion_data.")["predicate_atoms"]!
            .AsArray().Select(name => (string)name!).ToArray();

        string[] before = Names();
        First(server, "%%user\napp([], L, L).\napp([H|T], L, [H|R]) :- app(T, L, R).\n'a b'(1).\nnow.");
        string[] after = Names();

        // The kernel asks before any cell runs, and reads the names of the first answer.
        Assert.Contains("jupyter:print_table(A)", before);
        Assert.Contains("jupyter:trace(A)", before);
        Assert.Contains("append(A,B,C)", before);
        Assert.Contains("atom_length(A,B)", before);
        Assert.Contains("halt", before);
        Assert.DoesNotContain("app(A,B,C)", before);
        Assert.DoesNotContain(before, name => name.Contains('$'));
        Assert.DoesNotContain(before, name => name.StartsWith("jupyter_", StringComparison.Ordinal));
        Assert.Equal(before.Length, before.Distinct().Count());
        // What the session defined since, written so that it reads back.
        Assert.Equal(new[] { "'a b'(A)", "app(A,B,C)", "now" }, after.Except(before).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheNamesToCompleteWith_HaveTheExportsOfTheLoadedModules()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        string[] Names() => First(server, "jupyter:update_completion_data.")["predicate_atoms"]!
            .AsArray().Select(name => (string)name!).ToArray();

        string[] before = Names();
        // n is loaded by m, so user does not import it; m is loaded by the cell, so user does.
        First(server, "%%save n.pl\n:- module(n, [npub/0]).\nnpub.\nnpriv.");
        Assert.Equal("success", (string)First(server, "%%file m.pl\n:- module(m, [mpub/1]).\n:- use_module(n).\nmpub(1).\nmpriv(2).")["status"]!);
        string[] after = Names();

        Assert.Equal(new[] { "mpub(A)", "n:npub" }, after.Except(before).Order(StringComparer.Ordinal));
        // What a cell can call, as it calls it.
        Assert.Equal("success", (string)First(server, "mpub(X), n:npub.")["status"]!);
        // The server's own modules are not offered, the helpers of jupyter but as helpers.
        Assert.DoesNotContain(after, name => name.StartsWith("jupyter_", StringComparison.Ordinal));
        Assert.DoesNotContain(after, name => name.Contains("print_table") && !name.StartsWith("jupyter:", StringComparison.Ordinal));
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

using System.Text.Json;
using System.Text.Json.Nodes;
using Shumway.Jupyter;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-074: a solution that left choice points says which query
/// can be asked for the next, <c>more</c> asks the latest query for several,
/// and <c>stop</c> ends a query by cutting its choice points.</summary>
public sealed class NotebookMoreSolutionsTests : IDisposable
{
    private readonly NotebookHarness _notebooks = new();

    public void Dispose() => _notebooks.Dispose();

    private static JsonNode Ask(NotebookServer server, string method, object parameters)
    {
        string? line = NotebookHarness.Ask(server,
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }));
        Assert.NotNull(line);
        return JsonNode.Parse(line)!;
    }

    private static JsonNode Call(NotebookServer server, string code)
        => Ask(server, "call", new { code })["result"]!;

    private static JsonNode More(NotebookServer server, int query, int count)
        => Ask(server, "more", new { query, count });

    private static JsonNode Stop(NotebookServer server, int query)
        => Ask(server, "stop", new { query });

    private static string Alive(JsonNode result) => result["alive"]!.ToJsonString();

    private static string? Binding(JsonNode? entry, string name) => (string?)entry?["bindings"]?[name];

    [Fact]
    public void ASolutionThatLeftChoicePoints_NamesItsQuery_AndALastOneDoesNot()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode open = Call(server, @"member(X, [a, b, c]).");
        JsonNode last = Call(server, @"Y = 1.");
        JsonNode other = Call(server, @"member(Z, [p, q]).");

        int first = (int)open["1"]!["alive"]!;
        int second = (int)other["1"]!["alive"]!;
        Assert.Equal("a", Binding(open["1"], "X"));
        Assert.Equal($"[{first}]", Alive(open));
        // A query that left no choice point is not alive, and the one before it still is.
        Assert.Null(last["1"]!["alive"]);
        Assert.Equal($"[{first}]", Alive(last));
        Assert.NotEqual(first, second);
        Assert.Equal($"[{first},{second}]", Alive(other));
    }

    [Fact]
    public void More_GivesUpToCountSolutions_AndSaysWhenTheQueryHasNoMore()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        JsonNode open = Call(server, @"between(1, 6, X), write(got(X)).");
        int query = (int)open["1"]!["alive"]!;

        JsonNode two = More(server, query, 2)["result"]!;
        JsonNode rest = More(server, query, 5)["result"]!;
        JsonNode gone = More(server, query, 1);

        Assert.Equal("2", Binding(two["1"], "X"));
        Assert.Equal("got(2)", (string)two["1"]!["output"]!);
        Assert.Equal("3", Binding(two["2"], "X"));
        Assert.Null(two["3"]);
        Assert.Equal(query, (int)two["2"]!["alive"]!);
        Assert.Equal($"[{query}]", Alive(two));
        // Three were left: the last of them left no choice point.
        Assert.Equal("4", Binding(rest["1"], "X"));
        Assert.Equal("6", Binding(rest["3"], "X"));
        Assert.Null(rest["3"]!["alive"]);
        Assert.Null(rest["4"]);
        Assert.Equal("[]", Alive(rest));
        Assert.Equal(-4713, (int)gone["error"]!["code"]!);
    }

    [Fact]
    public void More_OnAQueryWithNoNextSolution_AnswersAFailure_AndTheQueryIsAliveNoMore()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        JsonNode open = Call(server, @"( X = a ; X = b ; fail ).");
        int query = (int)open["1"]!["alive"]!;

        JsonNode rest = More(server, query, 5)["result"]!;

        Assert.Equal("b", Binding(rest["1"], "X"));
        Assert.Equal(query, (int)rest["1"]!["alive"]!);
        Assert.Equal(-4711, (int)rest["2"]!["error"]!["code"]!);
        Assert.Null(rest["3"]);
        Assert.Equal("[]", Alive(rest));
    }

    [Fact]
    public void More_OnAnExceptionInTheQuery_AnswersTheError_AndTheQueryIsAliveNoMore()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        JsonNode open = Call(server, @"( X = a ; X = b ; throw(no_more) ).");
        int query = (int)open["1"]!["alive"]!;

        JsonNode rest = More(server, query, 5)["result"]!;

        Assert.Equal("b", Binding(rest["1"], "X"));
        Assert.Equal(-4712, (int)rest["2"]!["error"]!["code"]!);
        Assert.Equal("[]", Alive(rest));
    }

    [Fact]
    public void More_IsForTheLatestQueryAlive()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        int older = (int)Call(server, @"member(X, [a, b]).")["1"]!["alive"]!;
        int latest = (int)Call(server, @"member(Y, [1, 2]).")["1"]!["alive"]!;

        JsonNode notLatest = More(server, older, 1);
        JsonNode unknown = More(server, latest + 100, 1);
        JsonNode noCount = Ask(server, "more", new { query = latest });
        JsonNode none = More(server, latest, 0);
        JsonNode next = More(server, latest, 1)["result"]!;

        Assert.Equal(-4714, (int)notLatest["error"]!["code"]!);
        Assert.Equal(-4713, (int)unknown["error"]!["code"]!);
        Assert.Equal(-32602, (int)noCount["error"]!["code"]!);
        Assert.Equal(-32602, (int)none["error"]!["code"]!);
        // None of them moved the query on.
        Assert.Equal("2", Binding(next["1"], "Y"));
    }

    [Fact]
    public void Stop_EndsTheLatestQuery_AndItsCleanupRuns()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        Call(server, @"nb_setval(cleaned, no).");
        JsonNode open = Call(server, @"setup_call_cleanup(true, member(X, [a, b, c]), nb_setval(cleaned, yes)).");
        int query = (int)open["1"]!["alive"]!;
        JsonNode before = Call(server, @"nb_getval(cleaned, V).");

        JsonNode stopped = Stop(server, query)["result"]!;
        JsonNode after = Call(server, @"nb_getval(cleaned, V).");
        JsonNode more = More(server, query, 1);
        JsonNode retry = Call(server, @"retry.");
        JsonNode again = Stop(server, query)["result"]!;

        Assert.Equal("no", Binding(before["1"], "V"));
        Assert.Equal("[]", Alive(stopped));
        Assert.Equal("yes", Binding(after["1"], "V"));
        Assert.Equal(-4713, (int)more["error"]!["code"]!);
        // The query is gone: a retry does not reach its second solution. It
        // answers for a query of this loop that left no choice point.
        Assert.Null(Binding(retry["1"], "X"));
        Assert.Equal(-4711, (int)retry["1"]!["error"]!["code"]!);
        // To stop a query that is not alive is not an error.
        Assert.Equal("[]", Alive(again));
    }

    [Fact]
    public void Stop_OfAnOlderQuery_EndsItWhenItBecomesTheLatest_AndItGivesNoSolutionInBetween()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        int older = (int)Call(server, @"member(X, [a, b]).")["1"]!["alive"]!;
        int latest = (int)Call(server, @"between(1, 2, Y).")["1"]!["alive"]!;

        JsonNode stopped = Stop(server, older)["result"]!;
        JsonNode asked = More(server, older, 1);
        JsonNode rest = More(server, latest, 5)["result"]!;
        JsonNode retry = Call(server, @"retry.");
        JsonNode fresh = Call(server, @"member(Z, [p, q]).");

        // The older one is no longer a query to ask, while the later one is.
        Assert.Equal($"[{latest}]", Alive(stopped));
        Assert.Equal(-4713, (int)asked["error"]!["code"]!);
        Assert.Equal("2", Binding(rest["1"], "Y"));
        Assert.Equal("[]", Alive(rest));
        // With the later one out of solutions the older has ended: a retry
        // does not reach its second solution.
        Assert.Null(Binding(retry["1"], "X"));
        Assert.Equal("error", (string)retry["1"]!["status"]!);
        Assert.Equal($"[{(int)fresh["1"]!["alive"]!}]", Alive(fresh));
    }

    [Fact]
    public void Stop_OfTheLatest_LeavesTheQueryUnderItToBeAsked()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        int older = (int)Call(server, @"member(X, [a, b]).")["1"]!["alive"]!;
        int latest = (int)Call(server, @"member(Y, [1, 2]).")["1"]!["alive"]!;

        JsonNode stopped = Stop(server, latest)["result"]!;
        JsonNode next = More(server, older, 1)["result"]!;

        Assert.Equal($"[{older}]", Alive(stopped));
        Assert.Equal("b", Binding(next["1"], "X"));
    }

    [Fact]
    public void ACellOfSeveralQueries_SaysWhichAreAliveAfterIt()
    {
        NotebookServer server = _notebooks.Open("a").Server;

        JsonNode cell = Call(server, "member(X, [a, b]).\nY = 1.\nmember(Z, [p, q]).");

        int first = (int)cell["1"]!["alive"]!;
        int third = (int)cell["3"]!["alive"]!;
        Assert.Null(cell["2"]!["alive"]);
        Assert.Equal($"[{first},{third}]", Alive(cell));
    }

    [Fact]
    public void Retry_StillGivesTheNextSolutionOfTheLatestQuery()
    {
        NotebookServer server = _notebooks.Open("a").Server;
        int query = (int)Call(server, @"member(X, [a, b, c]).")["1"]!["alive"]!;

        JsonNode retried = Call(server, @"retry.");
        JsonNode next = More(server, query, 1)["result"]!;

        Assert.Equal("b", Binding(retried["1"], "X"));
        Assert.Equal(query, (int)retried["1"]!["alive"]!);
        Assert.Equal("c", Binding(next["1"], "X"));
    }
}

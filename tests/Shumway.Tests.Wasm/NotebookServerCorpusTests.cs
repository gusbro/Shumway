using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Shumway.Jupyter;

namespace Shumway.Tests.Wasm;

/// <summary>ADR-067 D8: the notebook server answers the corpus as the
/// reference does. Each session of <c>tests/jupyter/corpus</c> is sent to our
/// server line by line and its responses are compared with the ones recorded
/// from the reference. A difference kept on purpose is declared in
/// <see cref="Intended"/>, with its reason.</summary>
public sealed partial class NotebookServerCorpusTests : IDisposable
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "jupyter-corpus");

    private readonly NotebookHarness _notebooks = new();

    public void Dispose() => _notebooks.Dispose();

    /// <summary>What our server answers where it differs from the reference
    /// on purpose: the session, the request (from 1) and the reason.</summary>
    private static readonly Dictionary<(string Session, int Request), (string Reason, string Ours)> Intended = new()
    {
        [("002-malformed", 2)] = (
            "A line that is not JSON gets a parse error. The reference stops answering.",
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Invalid JSON was received by the server.","data":{"prolog_message":""}}}"""),
        [("002-malformed", 3)] = (
            "The server is still there after a line that is not JSON.",
            """{"jsonrpc":"2.0","id":1,"result":{"1":{"status":"success","type":"query","bindings":{},"output":""}}}"""),
        [("003-queries", 4)] = (
            "A variable that is another one shows that variable, not an atom with its name.",
            """{"jsonrpc":"2.0","id":4,"result":{"1":{"status":"success","type":"query","bindings":{"Y":"X"},"output":""}}}"""),
        [("008-variables", 2)] = (
            "A variable and an atom of the same name can be told apart.",
            """{"jsonrpc":"2.0","id":2,"result":{"1":{"status":"success","type":"query","bindings":{"Y":"X","Z":"'X'"},"output":""}}}"""),
        [("008-variables", 3)] = (
            "A variable that is another one shows that variable, not an atom with its name.",
            """{"jsonrpc":"2.0","id":3,"result":{"1":{"status":"success","type":"query","bindings":{"Y":"X","Z":"X"},"output":""}}}"""),
        [("008-variables", 4)] = (
            "A variable that is another one shows that variable, not an atom with its name.",
            """{"jsonrpc":"2.0","id":4,"result":{"1":{"status":"success","type":"query","bindings":{"X":"f(Y)","Z":"Y"},"output":""}}}"""),
        [("008-variables", 6)] = (
            "Naming a variable does not turn its other occurrences into atoms.",
            """{"jsonrpc":"2.0","id":6,"result":{"1":{"status":"success","type":"query","bindings":{"Y":"X","W":"g(X,X,'X')"},"output":""}}}"""),
        [("010-previous-bindings", 15)] = (
            "In a query that uses both X and $X, X is the query's own variable.",
            """{"jsonrpc":"2.0","id":15,"result":{"1":{"status":"success","type":"query","bindings":{"X":"1","Y":"g(_,1)"},"output":""}}}"""),
        [("014-file-magics", 16)] = (
            "A clause that does not parse is an error of the cell, with the reader's message as its text. The reference answers a failure and puts the message in the output.",
            """{"jsonrpc":"2.0","id":16,"result":{"1":{"status":"error","error":{"code":-4712,"message":"Exception","data":{"prolog_message":""}}}}}"""),
        [("015-tables", 13)] = (
            "A helper is a goal like any other: it works inside a conjunction. In the reference it has to be the whole query.",
            """{"jsonrpc":"2.0","id":13,"result":{"1":{"status":"success","type":"query","bindings":{"X":"1"},"output":"","print_table":{"ValuesLists":[["1","1"],["1","2"]],"VariableNames":["X","Y"]}}}}"""),
        [("016-saved-tables", 2)] = (
            "The server writes the file of a saved table, in the session's directory. In the reference the client writes it.",
            """{"jsonrpc":"2.0","id":2,"result":{"1":{"status":"success","type":"query","bindings":{"R":"written"},"output":""}}}"""),
        [("016-saved-tables", 6)] = (
            "A format that is neither csv nor tsv is an error. The reference passes it on to its client.",
            """{"jsonrpc":"2.0","id":6,"result":{"1":{"status":"error","error":{"code":-4712,"message":"Exception","data":{"prolog_message":""}}}}}"""),
        [("018-predicate-docs", 2)] = (
            "No log file is kept, and the answer is a boolean. The reference answers the text true.",
            """{"jsonrpc":"2.0","id":0,"result":false}"""),
    };

    /// <summary>Requests whose answer carries text about the system it runs
    /// on (its versions, its help, its flags, its directory, how it writes
    /// the name of a helper): the answers are compared without that text.
    /// What ours says is checked in <see cref="NotebookServerTests"/>.</summary>
    private static readonly HashSet<(string Session, int Request)> OwnText = new()
    {
        ("017-line-magics", 4),     // %queries: jupyter:Name here, jupyter::Name there
        ("017-line-magics", 5),     // %pwd
        ("017-line-magics", 6),     // %versions
        ("017-line-magics", 7),     // %magic
        ("017-line-magics", 8),     // %help
        ("017-line-magics", 9),     // %flags: the flags of each system
        ("017-line-magics", 16),    // print_queries
        ("017-line-magics", 18),    // print_query_time
        ("017-line-magics", 19),    // pwd
        ("017-line-magics", 20),    // version
        ("018-predicate-docs", 1),  // the description of each helper
        ("021-trace", 2),           // the trace: the reference prints none on this engine
        ("021-trace", 3),
        ("021-trace", 4),
        ("021-trace", 6),           // the trace up to the error
    };

    private static List<string> SessionNames()
        => Directory.GetFiles(Corpus, "*.requests.jsonl")
            .Select(file => Path.GetFileName(file)[..^".requests.jsonl".Length])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public static TheoryData<string> Sessions()
    {
        var sessions = new TheoryData<string>();
        foreach (string name in SessionNames()) sessions.Add(name);
        return sessions;
    }

    [Fact]
    public void TheCorpus_IsThere()
    {
        List<string> sessions = SessionNames();
        Assert.True(sessions.Count >= 20, $"only {sessions.Count} sessions in {Corpus}");
        foreach (string session in sessions)
        {
            Assert.NotEmpty(Lines(session, "requests"));
            Assert.True(File.Exists(Path.Combine(Corpus, session + ".expected.jsonl")), session);
        }
        foreach (var key in Intended.Keys)
            Assert.Contains(key.Session, sessions);
        foreach (var key in OwnText)
            Assert.Contains(key.Session, sessions);
    }

    [Theory]
    [MemberData(nameof(Sessions))]
    public void ASession_IsAnsweredAsTheReferenceAnswersIt(string session)
    {
        string[] requests = Lines(session, "requests");
        string[] expected = Lines(session, "expected");
        NotebookServer server = _notebooks.Open(session).Server;
        var differences = new List<string>();
        for (int i = 0; i < requests.Length; i++)
        {
            string? ours = NotebookHarness.Ask(server, requests[i]);
            string? theirs = i < expected.Length ? expected[i] : null;
            string? wanted = theirs;
            if (Intended.TryGetValue((session, i + 1), out var intended))
            {
                // An entry the reference now agrees with has to go.
                Assert.False(Same(intended.Ours, theirs, requests[i]),
                    $"{session} #{i + 1}: the reference answers as we do, so the difference is no longer one");
                wanted = intended.Ours;
            }
            bool ownText = OwnText.Contains((session, i + 1));
            // An entry whose text the reference now writes as we do has to go.
            if (ownText)
                Assert.False(Same(ours, theirs, requests[i]),
                    $"{session} #{i + 1}: the texts are the same, so they need not be left out");
            if (!Same(ours, wanted, requests[i], ownText))
                differences.Add($"#{i + 1} {requests[i]}\n   ours:   {ours ?? "(no answer)"}\n   wanted: {wanted ?? "(no answer)"}");
        }
        Assert.True(differences.Count == 0, $"{session}:\n{string.Join("\n", differences)}");
    }

    private static string[] Lines(string session, string kind)
        => File.ReadAllLines(Path.Combine(Corpus, $"{session}.{kind}.jsonl"))
            .Where(line => line.Length > 0).ToArray();

    private static bool Same(string? ours, string? theirs, string request, bool ownText = false)
    {
        if (ours is null || theirs is null) return ours is null && theirs is null;
        bool version = request.Contains("\"method\":\"version\"", StringComparison.Ordinal);
        JsonNode? left = Comparable(JsonNode.Parse(ours), version);
        JsonNode? right = Comparable(JsonNode.Parse(theirs), version);
        return ownText
            ? JsonNode.DeepEquals(WithoutOwnText(left), WithoutOwnText(right))
            : JsonNode.DeepEquals(left, right);
    }

    /// <summary>A response without what a helper writes about the system it
    /// runs on: the output of a query, the table it shows, and the result of
    /// a method that is not a call.</summary>
    private static JsonNode? WithoutOwnText(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject pairs:
                var result = new JsonObject();
                foreach (var (key, value) in pairs)
                {
                    // Left out, not blanked: an error has an output only
                    // when something was written before it.
                    if (key is "output" or "print_table") continue;
                    bool own = key == "result" && value is JsonObject answer && !answer.ContainsKey("1");
                    result[key] = own ? "own text" : WithoutOwnText(value);
                }
                return result;
            case JsonArray items:
                return new JsonArray(items.Select(WithoutOwnText).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>A response with what is not compared taken out:
    /// <list type="bullet">
    /// <item>the text of an error message, which is each system's own, and
    /// the name of its key, which is the variant's (ADR-067 D5);</item>
    /// <item>the number in the name of a variable that has no name;</item>
    /// <item>the version of the engine the recording was made with;</item>
    /// <item>the reference's empty list as a JSON array: every binding is
    /// text here;</item>
    /// <item>the residual goals of a query, a key only our server sends: the
    /// reference says nothing of a constrained variable.</item>
    /// </list></summary>
    private static JsonNode? Comparable(JsonNode? node, bool version, string? under = null)
    {
        switch (node)
        {
            case JsonObject pairs:
                var result = new JsonObject();
                foreach (var (key, value) in pairs)
                {
                    if (key is "prolog_message" or "logtalk_message") result["message_text"] = "";
                    else if (key == "residual_goals") continue;
                    else if (version && key == "result" && value is JsonValue) result[key] = "version";
                    else result[key] = Comparable(value, version, under == "bindings" ? "binding" : key);
                }
                return result;
            case JsonArray { Count: 0 } when under == "binding":
                return JsonValue.Create("[]");
            case JsonArray items:
                return new JsonArray(items.Select(item => Comparable(item, version)).ToArray());
            case JsonValue value when value.TryGetValue(out string? text):
                return JsonValue.Create(UnnamedVariable().Replace(text, "_"));
            default:
                return node?.DeepClone();
        }
    }

    [GeneratedRegex(@"\b_G?\d+\b")]
    private static partial Regex UnnamedVariable();
}

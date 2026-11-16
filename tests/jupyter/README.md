# The notebook server's differential corpus

ADR-067 D8: the notebook server is our own, and a differential test says what
is correct. This folder holds the sessions that test is made of and what the
reference answered to each.

## The files

| File | What it is | Written by |
|---|---|---|
| `corpus/NAME.cells` | one session: the cells and raw lines sent, in order | hand |
| `corpus/NAME.requests.jsonl` | the JSON-RPC lines our server is sent | `record-reference.ps1` |
| `corpus/NAME.expected.jsonl` | the lines the reference answered | `record-reference.ps1` |

A `.cells` file is a list of sections:

```
### call
member(X, [a, b, c]).
### call
retry.
### raw
{"jsonrpc":"2.0","id":0,"method":"backend"}
```

- `### call`: the lines below are a cell's code. It is sent as a `call`
  request whose `id` counts the calls of the session from 1.
- `### reference`: after a call, the code the reference gets in its place,
  where the two servers are written differently on purpose.
- `### raw`: the line below is sent as it is.

## The reference

Hercutalk's server, running on desktop Shumway through Logtalk. Neither tree
is part of this repository. The recording here was made with:

| Tree | Version |
|---|---|
| Hercutalk | 0.47.1 (`73e2175`) |
| Logtalk | 3.103.0-b01 (`aff5dd7`) |

## Recording

```
dotnet build -c Release
pwsh tests/jupyter/record-reference.ps1 -Logtalk <logtalk3> -Hercutalk <logtalk-jupyter-kernel>
pwsh tests/jupyter/record-reference.ps1 -Logtalk ... -Hercutalk ... -Only 004-retry   # one session
```

- Each session runs on a fresh server, in an empty directory that is deleted
  afterwards.
- The directories of the machine are replaced by `$CWD`, `$LOGTALK` and
  `$HERCUTALK` in what is recorded.
- A session that answered a different number of lines than it was sent is
  reported: read it before keeping the recording. On Shumway the reference
  answers one more line, a parse error, when its input ends; the comparison
  stops at the last request. After `halt` it answers nothing (`011-halt`).

## The same server on another Prolog

An answer of the reference mixes what its server does with what the Prolog
under it does. To tell them apart, run the corpus through the same server on
another Prolog and compare:

```
pwsh tests/jupyter/record-reference.ps1 -Logtalk ... -Hercutalk ... -Backend swi -Exe <swipl.exe> -OutDir <somewhere>
```

`-Backend` takes `shumway` (the default), `swi` or `sicstus`. With another
backend nothing is written to the corpus, and `-OutDir` is required.

## The comparison

`tests/Shumway.Tests.Wasm/NotebookServerCorpusTests.cs` sends each session's
requests to our server and compares its responses with the expected file.
Not compared: the text of an error message and the name of its key, the
number in the name of an unnamed variable, and the engine's version.

An expected file is never edited by hand. A difference our server keeps on
purpose is declared in that test, with its reason, and the test fails if the
reference comes to agree with us.

```
dotnet test tests/Shumway.Tests.Wasm/ -c Release --filter "FullyQualifiedName~NotebookServer"
```

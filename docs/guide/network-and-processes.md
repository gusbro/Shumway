# Network and processes

Three libraries talk to the world outside the program:

| Library | For |
|---|---|
| `library(sockets)` | TCP clients and servers, and UDP |
| `library(tls)` | Encrypted connections over a socket stream |
| `library(process)` | Running other programs and talking to them through pipes |

They load like any other library, and their names stay out of the global
namespace:

```prolog
:- use_module(library(sockets)).
```

A browser has no sockets and cannot start programs, so in WebShumway the libraries
load and each predicate raises `system_error`. The exception is `is_process/1`, a test
that fails.

## TCP

```prolog
% A client.
socket_client_open('example.org':80, S, []),
put_bytes(S, Request),
flush_output(S),
get_byte(S, B),
close(S).

% A server, one connection at a time.
socket_server_open(8080, Server),
socket_server_accept(Server, Client, S, [type(text)]),
read(S, Question),
writeq(S, answer(Question)), put_char(S, '.'), nl(S),
close(S),
socket_server_close(Server).
```

One stream goes both ways. Writes stay in a buffer until `flush_output/1` or
`close/1`.

### Addresses

| Form | Means |
|---|---|
| `Host:Port`, `inet(Host, Port)` | A host and a port |
| `Port` | The local machine to connect to, every interface to listen on |
| `unix(Path)` | A Unix domain socket. Not on .NET Framework |

`Host` is a name or a literal address, IPv4 or IPv6, such as `'::1'`.

`Port` is an integer, an atom of digits or a service name such as `http`. A
name comes from the system's services file, and that file varies: Windows has
no `redis`, for example, so portable code uses numbers. To listen on a free
port, leave `Port` as a variable: it becomes the port's number, always an
integer.

### Predicates

| Predicate | |
|---|---|
| `socket_client_open(+Address, -Stream, +Options)` | Connects and opens a stream |
| `socket_server_open(?Address, -Server)`, `/3` | Listens |
| `socket_server_accept(+Server, -Client, -Stream, +Options)` | Waits for a connection. `Client` is the address it came from |
| `socket_server_close(+Server)` | Stops listening |
| `socket_shutdown(+Stream, +How)` | Ends `read`, `write` or `read_write`, and leaves the other direction open |
| `socket_select(+Servers, -ServersReady, +Inputs, -InputsReady, +Outputs, -OutputsReady, +Timeout)` | Waits until something can go on without waiting |
| `current_host(?Host)`, `current_hostname(?Host)` | The name of this machine |

### Options of a stream

| Option | |
|---|---|
| `type(binary)`, `type(text)` | Binary is the default |
| `encoding(Name)` | Asks for text in that encoding. See below |
| `eof_action(Action)` | As in `open/4`. The default is `error` |
| `alias(Name)` | As in `open/4` |
| `timeout(Seconds)` | How long to connect, accept, read or write before `timeout_error` |
| `nodelay(Boolean)`, `keepalive(Boolean)` | The TCP settings of those names |

For `socket_server_open/3`: `reuseaddr(Boolean)`, `loopback(Boolean)`,
`backlog(N)`. Both take `numeric_nodename(Boolean)` and
`numeric_servname(Boolean)`, which refuse names.

### Serving several clients

The engine has no threads for a program to start, so a server written in Prolog
has two forms. Taking one connection at a time is enough for a local tool. With
`socket_select/7` one thread can watch them all:

```prolog
serve(Server, Clients) :-
    socket_select([Server], New, Clients, Ready, [], _, off),
    accept_new(New, Clients, Clients1),
    answer_all(Ready, Clients1, Clients2),
    serve(Server, Clients2).
```

A stream counts as ready when a read will not wait, which includes its end. Each
entry of a list can be `Term-Stream`, and the term comes back with it. Any stream
can be an input, a pipe from a program among them, and so can a UDP socket.

A server that has to answer many clients at once belongs in the host: C# takes
the connections and rents an engine from `EnginePool` for each request.

## Text and the other end

Nobody can know what encoding the other end of a socket or a pipe uses. So:

- A stream with no `encoding/1` uses the default encoding and never raises on what
  it reads. Bytes that are not valid read as U+FFFD, and the rest is intact.
- With `encoding(Name)` the program is stating what comes. Bytes that are not valid
  raise `representation_error(character)`.
- `type(binary)` gives the bytes.

Newlines on a socket arrive as they were sent, since a network protocol does not
depend on the platform of either end. On a pipe from a program, CR-LF reads as a
newline on Windows, as in any text file there.

## Waiting

A read, an accept or a wait for a process takes as long as the other end takes.
Cancelling the query interrupts it, and so does `time_out/3`. Whatever would have
come is not lost: the next read gets it.

## UDP

```prolog
udp_socket(S),
udp_bind(S, Port),
udp_receive(S, Data, From, [as(atom)]),
udp_send(S, Data, From, []),
udp_close(S).
```

| Predicate | |
|---|---|
| `udp_socket(-Socket)`, `/2` | Opens a socket. Options: `broadcast(Boolean)`, `reuseaddr(Boolean)` |
| `udp_bind(+Socket, ?Address)` | Gives it a local address |
| `udp_send(+Socket, +Data, +To, +Options)` | Sends one datagram. Data is text, or a list of bytes with `type(binary)` |
| `udp_receive(+Socket, -Data, -From, +Options)` | Waits for one. `From` is `Host:Port`, ready for `udp_send/4` |
| `udp_close(+Socket)` | Closes it |

`udp_receive/4` also takes `as(atom)`, `as(string)`, `as(chars)` or `as(codes)`, and
`timeout(Seconds)`. Text follows the rules above.

## TLS

```prolog
socket_client_open('example.org':443, S0, []),
tls_client_context(Context, [hostname('example.org')]),
tls_client_negotiate(Context, S0, S),
```

A negotiation turns an open stream into an encrypted one. `S0` is gone after it,
whether it worked or not. `S` keeps its type, encoding, alias and end of stream
action. Since the stream is already open, a protocol can start in the clear and
upgrade, as SMTP does: a negotiation with bytes already read from the stream raises
`permission_error(negotiate, stream, S0)`, because they belong to the handshake.

| Predicate | Options |
|---|---|
| `tls_client_context(-Context, +Options)` | `hostname(Name)`, `verify(Boolean)`, `cacert_file(File)`, and a certificate as below |
| `tls_client_negotiate(+Context, +Stream0, -Stream)` | |
| `tls_server_context(-Context, +Options)` | A certificate is required |
| `tls_server_negotiate(+Context, +Stream0, -Stream)` | |

A client verifies the server's certificate unless `verify(false)`. `hostname/1`
defaults to the host it connected to. `cacert_file/1` names an authority to trust,
for a private one.

A certificate and its key come from `pkcs12_file(File)`, from `pkcs12(Bytes)` with
the contents of that file, or from `certificate_file(File)` and `key_file(File)`
in PEM form, which .NET Framework does not take. `password(Text)` opens them.

A failed handshake raises `system_error`, and so does a certificate that does not
verify.

## Processes

```prolog
process_create(path(git), ['log', '-1'], [stdout(pipe(Out)), process(P)]),
read_term(Out, T, []),
close(Out),
process_wait(P, Status).
```

| Predicate | |
|---|---|
| `process_create(+Executable, +Arguments, +Options)`, `/2` | Starts a program and goes on without waiting |
| `process_wait(+Process, -Status)`, `/3` | Waits for it. `Status` is `exit(Code)`, `killed(Signal)` or `timeout` |
| `process_kill(+Process)`, `/2` | Sends a signal, `SIGKILL` by default. A number, `'SIGTERM'` or `term` |
| `process_group_kill(+Process)`, `/2` | Ends it and what it started. Only `SIGKILL`: .NET can do no other to a tree |
| `process_id(-Pid)`, `process_id(+Process, -Pid)` | The system's id of this process, or of that one |
| `is_process(@Term)` | True for a reference not released yet |
| `process_release(+Process)` | Lets go of a reference. The process goes on |
| `process_which(+Executable, -File)` | The file a name stands for. Fails if there is none |

`Executable` is a file, or `path(Name)` to look up on the search path. A name with no
directory is looked up as well.

An element of `Arguments` is an atom, a number, text, `file(Name)` to put a path in
the system's form, or a list of those to join into one argument.

### Options

| Option | |
|---|---|
| `stdin(Spec)`, `stdout(Spec)`, `stderr(Spec)` | Where each stream goes |
| `process(P)` | A reference to wait for it later |
| `wait(Status)` | Waits for it before going on |
| `cwd(Directory)` | Where it runs |
| `environment(List)` | Adds `Name=Value` to the environment |
| `env(List)` | Replaces the environment with these |
| `priority(N)` | From -20 to 19, lower is more urgent |
| `window(Boolean)` | `true` opens a window of its own, with no pipes. Windows only |
| `detached(Boolean)` | On Windows, keeps a Ctrl+C at the console from reaching it. A child outlives its parent anyway |

`Spec` is one of:

| Spec | |
|---|---|
| `std` | Shares the standard stream of this process. The default |
| `null` | Discards the output, and reads as empty |
| `file(Name)` | Reads from or writes to a file |
| `pipe(S)`, `pipe(S, Options)` | A new stream to talk to it, text by default. Options: `type/1`, `encoding/1`, `alias/1`, `eof_action/1` |
| `stream(S)` | A stream the program already has: a file, a pipe or a socket |

Without `process/1` or `wait/1` nothing asks about the process again, and no reference
is kept.

Read a program's output while it runs, and close its input when done: a program
stalls when a pipe nobody reads fills up. Reading stdout and stderr one after the
other can stall too. `socket_select/7` takes pipes and tells which one has data.

### Joining streams

`stream(S)` connects a program to a stream that is already open, which joins two
programs or sends the output to a log the program also writes:

```prolog
% git log | sort
process_create(path(git), [log], [stdout(pipe(Out)), process(A)]),
process_create(path(sort), [], [stdin(stream(Out)), stdout(pipe(Sorted)), process(B)]),
close(Out),
```

The program can close `S` right after starting the child. Bytes go as they are, in no
encoding. Before starting the child, the stream is flushed. `process_wait/2` returns
when the output has arrived, up to two seconds after the child ends, in case something
it left running holds it open.

A stream with no bytes under it, such as the writer a host gave to `user_output`, raises
`domain_error(file_stream, S)`. While a child reads from `S`, the program must not.

The child sees a pipe and not the file itself, since .NET gives no way to hand it the
handle. A program that seeks in its output, or asks whether it is a file, can tell.

### Status

A process ended by a signal this program sent reports `killed(N)`, on Windows too,
where any signal ends it. .NET gives only an exit code, 128 plus the signal on Unix,
so a signal sent from outside reads as `exit(137)`, the same as a program that exits
with 137.

### Errors

| Case | Error |
|---|---|
| The program does not exist | `existence_error(source_sink, Executable)` |
| It may not run | `permission_error(execute, source_sink, Executable)` |
| A reference already released | `existence_error(process, P)` |
| Something else as a reference | `domain_error(process, X)` |
| A signal with no such name | `domain_error(signal, X)` |
| An option it does not know | `domain_error(process_create_option, X)` |

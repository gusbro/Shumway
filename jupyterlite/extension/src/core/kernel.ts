// A JupyterLite kernel over a session of the runtime (ADR-067, ADR-068). It
// maps Jupyter's messages to request lines of the notebook server and shows
// its answers. Nothing here names a language: what is the variant's comes in
// as IVariant (ADR-073).

import { PathExt } from '@jupyterlab/coreutils';
import type { Contents, KernelMessage, Session } from '@jupyterlab/services';
import { BaseKernel, type IKernel } from '@jupyterlite/services';

import {
  ANSWER, ASK, setQueries, type IAnswer, type IAsk, type IAsked
} from './queries';
import {
  completions, documentation, GRAPHVIZ, nameAt, nameBefore, render, type IOutput, type IRendered, type ITexts
} from './render';
import { DRIVE, runtime, RuntimeStopped, type IRuntime } from './runtime';

/** What makes a kernel the Prolog one or the Logtalk one. */
export interface IVariant {
  /** The server a session loads: a parameter of SessionCreate. */
  bundle: string;
  /** The protocol version this kernel speaks. */
  protocol: number;
  /** The query that answers with the names to complete with. */
  completionQuery: string;
  implementation: string;
  languageInfo: KernelMessage.IInfoReply['language_info'];
  texts: ITexts;
}

/** An output with a graph, with the graph drawn beside its DOT. A graph
 *  that cannot be drawn is shown as its DOT, with why. */
async function drawn(output: IOutput, noticePrefix: string): Promise<IOutput> {
  const dot = output[GRAPHVIZ];
  if (typeof dot !== 'string') return output;
  try {
    const { drawDot } = await import('./graph');
    return { ...output, 'image/svg+xml': drawDot(dot) };
  } catch (reason) {
    return { 'text/plain': `${noticePrefix}The graph could not be drawn: ${reason}\n${dot}` };
  }
}

const request = (id: number, method: string, params?: object): string =>
  JSON.stringify(params ? { jsonrpc: '2.0', id, method, params } : { jsonrpc: '2.0', id, method });

type Reply = KernelMessage.IExecuteReplyMsg['content'];
type Show = (output: IOutput) => void;

let sessions = 0;

export class ServerKernel extends BaseKernel implements IKernel {
  constructor(
    options: IKernel.IOptions, variant: IVariant, contents: Contents.IManager, sessions: Session.IManager
  ) {
    super(options);
    this._variant = variant;
    this._contents = contents;
    this._sessions = sessions;
    // The directory of the notebook, as the engine sees it. A location may
    // carry the name of its drive.
    const location = options.location.includes(':')
      ? options.location.slice(options.location.indexOf(':') + 1) : options.location;
    this._directory = location ? `${DRIVE}/${location}` : DRIVE;
    this._started = this._start();
  }

  /** Resolved when the session answered or was refused; it does not reject:
   *  why there is no session is told by the first cell. */
  get ready(): Promise<void> {
    return this._started;
  }

  dispose(): void {
    if (this.isDisposed) return;
    super.dispose();
    setQueries(this.id, null);
    this._runtime?.onInput(this.id, null);
    void this._runtime?.api.SessionDispose(this.id);
  }

  /** Stops the query that is running. The server is that query, so the
   *  session ends with it; the next cell starts another (ADR-067 D5). A
   *  cell interrupted before its query left runs nothing. */
  async interrupt(): Promise<void> {
    if (!this._runtime) return;
    if (!this._running) {
      // JupyterLite hands a cell to the kernel a little after it is sent: an
      // interrupt can come first, and is for that cell all the same.
      this._interruptedAt = Date.now();
      return;
    }
    this._interrupted = true;
    try {
      await this._runtime.watched(this._runtime.api.SessionCancel(this.id));
    } catch { /* the running cell says the runtime stopped */ }
  }

  // --- the session -------------------------------------------------------

  private async _start(): Promise<void> {
    try {
      this._runtime = await runtime(this._contents);
      await this._open();
    } catch (reason) {
      this._refusal = reason instanceof Error ? reason.message : String(reason);
    }
  }

  /** Opens the session and asks the server what the kernel needs from it. */
  private async _open(): Promise<void> {
    const { api, onInput, watched } = this._runtime!;
    this._refusal = null;
    this._stopped = false;
    const refused = await watched(api.SessionCreate(this.id, this._variant.bundle, this._directory));
    if (refused) {
      // The limit is met sooner than it looks: a kernel outlives its
      // notebook's tab.
      this._refusal = /limit/i.test(refused)
        ? `${refused} A kernel keeps running after its notebook is closed: shut one down in the `
          + '"Running Terminals and Kernels" panel, then restart this one.'
        : refused;
      return;
    }
    // A request that takes no input, as the page's own for more solutions,
    // ends the input of a goal that reads.
    onInput(this.id, () => {
      if (this._allowInput) this.inputRequest({ prompt: '', password: false });
      else void api.SessionSupplyEndOfFile(this.id);
    });
    this._session = `${this.id}/${++sessions}`;
    this._cells.clear();
    this._shown.clear();
    this._took([]);

    const protocol = await this._ask(request(0, 'protocol'));
    if (protocol?.result?.version !== this._variant.protocol) {
      this._refusal = `The server speaks protocol ${JSON.stringify(protocol?.result?.version)} `
        + `and this kernel speaks ${this._variant.protocol}.`;
      return;
    }
    this._version = String((await this._ask(request(0, 'version')))?.result ?? '');
    this._tier = await watched(api.SessionTier(this.id));
    await this._refreshPredicates();
    this._docs = (await this._ask(request(0, 'jupyter_predicate_docs')))?.result ?? {};
  }

  /** One request and its response; null when the server has stopped. */
  private async _ask(line: string): Promise<any> {
    const runtime = this._runtime!;
    const answer = await runtime.watched(runtime.api.SessionRequest(this.id, line));
    return answer === '' ? null : JSON.parse(answer);
  }

  /** The file name of the notebook this kernel runs, after which the server
   *  names the notebook's own files; null for a console. Asked at each cell:
   *  a notebook can be renamed while its kernel runs. A console that shares
   *  the kernel has a session too, which is not the notebook's. */
  private async _notebook(): Promise<string | null> {
    try {
      await this._sessions.refreshRunning();
      for (const model of this._sessions.running())
        if (model.kernel?.id === this.id && model.path.endsWith('.ipynb'))
          return PathExt.basename(model.path);
    } catch { /* the server's own name */ }
    return null;
  }

  private async _refreshPredicates(): Promise<void> {
    const answer = await this._ask(request(0, 'call', { code: this._variant.completionQuery }));
    const names = answer ? render(answer, this._variant.texts).predicates : null;
    if (names) this._predicates = names;
    this._predicatesStale = false;
  }

  // --- Jupyter's requests ------------------------------------------------

  async kernelInfoRequest(): Promise<KernelMessage.IInfoReplyMsg['content']> {
    await this._started;
    const name = this._variant.languageInfo.name;
    return {
      status: 'ok',
      protocol_version: '5.3',
      implementation: this._variant.implementation,
      implementation_version: this._version,
      language_info: { ...this._variant.languageInfo, version: this._version },
      banner: `${name} on Shumway ${this._version}${this._tier ? ', ' + this._tier : ''}`,
      help_links: []
    };
  }

  async executeRequest(
    content: KernelMessage.IExecuteRequestMsg['content']
  ): Promise<KernelMessage.IExecuteReplyMsg['content']> {
    this._sentAt = Date.parse(this.parentHeader?.date ?? '') || Date.now();
    await this._started;
    const { texts } = this._variant;
    const show = (output: IOutput) => this.displayData({ data: output as any, metadata: {} });
    const failed = (ename: string, text: string): KernelMessage.IExecuteReplyMsg['content'] => ({
      status: 'error', execution_count: this.executionCount, ename, evalue: '', traceback: [text]
    });
    this._allowInput = content.allow_stdin !== false;

    // The page's own request, for more solutions of a query or to end it
    // (ADR-074): not a cell, and it opens no session.
    const asked = (content.user_expressions as { [key: string]: unknown } | undefined)?.[ASK];
    if (typeof asked === 'string') return this._asked(asked, show);

    if (this._runtime?.stopped) {
      const text = texts.errorPrefix + this._runtime.stopped;
      show({ 'text/plain': '\x1b[31;1m' + text });
      return failed('runtime stopped', text);
    }
    if (this._stopped && this._runtime) {
      // After halt, an interrupt or an exception the server did not handle.
      try {
        await this._runtime.watched(this._runtime.api.SessionDispose(this.id));
        await this._open();
      } catch (reason) {
        this._refusal = reason instanceof Error ? reason.message : String(reason);
      }
    }
    if (this._refusal || !this._runtime) {
      const text = texts.errorPrefix + (this._refusal ?? 'The runtime did not start.');
      show({ 'text/plain': '\x1b[31;1m' + text });
      return failed('no session', text);
    }

    try {
      return await this._cell(content, show, failed);
    } catch (reason) {
      if (!(reason instanceof RuntimeStopped)) throw reason;
      const text = texts.errorPrefix + reason.message;
      show({ 'text/plain': '\x1b[31;1m' + text });
      return failed('runtime stopped', text);
    }
  }

  /** A cell, from the files it reads to the outputs it shows. Throws
   *  RuntimeStopped when the runtime stops answering. */
  private async _cell(
    content: KernelMessage.IExecuteRequestMsg['content'],
    show: (output: IOutput) => void,
    failed: (ename: string, text: string) => KernelMessage.IExecuteReplyMsg['content']
  ): Promise<KernelMessage.IExecuteReplyMsg['content']> {
    const { texts } = this._variant;
    const { mirror, libraries } = this._runtime!;
    const { mirrored, quietly } = this._steps(show);

    // What changed in the file browser and the editor is there for the cell,
    // and so is a collection imported in another tab.
    await mirrored(() => mirror.pull());
    await quietly(() => libraries.sync());
    const notebook = await this._notebook();
    if (this._interruptedAt >= this._sentAt) {
      const text = texts.noticePrefix
        + 'Interrupted before the query started: nothing ran, and the session keeps what it had loaded.';
      show({ 'text/plain': text });
      return failed('interrupted', text);
    }
    // A cell run again ends the queries its run before left alive.
    const cellId = this._cellId();
    await this._endQueriesOf(cellId);
    const answer = await this._exchange(request(this.executionCount, 'call',
      notebook ? { code: content.code, notebook } : { code: content.code }), show);

    if (answer === null) {
      const text = this._ended();
      show({ 'text/plain': text });
      return failed(this._interrupted ? 'interrupted' : 'stopped', text);
    }

    const rendered = render(answer, texts);
    await this._show(rendered, show, cellId);
    if (rendered.predicates) this._predicates = rendered.predicates;
    if (rendered.error)
      return { status: 'error', execution_count: this.executionCount, evalue: '', ...rendered.error };
    return { status: 'ok', execution_count: this.executionCount, payload: [], user_expressions: {} };
  }

  /** The steps around a request that a failure of does not end it: the
   *  files, which say so in the cell, and the libraries. Both throw
   *  RuntimeStopped. */
  private _steps(show: Show) {
    const { watched } = this._runtime!;
    const { texts } = this._variant;
    const mirrored = async (copy: () => Promise<unknown>) => {
      try {
        await watched(copy());
      } catch (reason) {
        if (reason instanceof RuntimeStopped) throw reason;
        show({ 'text/plain': `${texts.noticePrefix}The files could not be brought up to date: ${reason}` });
      }
    };
    // A collection the page could not bring is one the cell does not find,
    // and the cell says so.
    const quietly = async (step: () => Promise<unknown>) => {
      try {
        await watched(step());
      } catch (reason) {
        if (reason instanceof RuntimeStopped) throw reason;
      }
    };
    return { mirrored, quietly };
  }

  /** A request line and its response, null when the server has stopped;
   *  what the request wrote is then there for the file browser. One call
   *  for the request, the engine's files and what it compiled: every call
   *  into the runtime is one more for WebKit's threads to stop taking. */
  private async _exchange(line: string, show: Show): Promise<any> {
    const { mirror, libraries, watched, api } = this._runtime!;
    const { mirrored, quietly } = this._steps(show);
    this._running = true;
    this._interrupted = false;
    let call: { answer: string; files: unknown; compiled: string };
    const since = mirror.since();
    try {
      call = JSON.parse(await watched(api.SessionCall(this.id, line, DRIVE)));
    } finally {
      this._running = false;
    }
    await mirrored(() => mirror.push({ since, files: call.files }));
    await quietly(() => libraries.keep(call.compiled));
    this._predicatesStale = true;
    return call.answer === '' ? null : JSON.parse(call.answer);
  }

  // --- the queries alive (ADR-074) -----------------------------------------

  /** The cell the request being answered came from, when it says. */
  private _cellId(): string | null {
    const id = (this.parent?.metadata as { cellId?: unknown } | undefined)?.cellId;
    return typeof id === 'string' ? id : null;
  }

  /** The queries alive are these: the page draws by them. */
  private _took(alive: number[]): void {
    this._alive = alive;
    for (const query of [...this._shown.keys()]) if (!alive.includes(query)) this._shown.delete(query);
    for (const [cell, queries] of [...this._cells])
      if (!queries.some(query => alive.includes(query))) this._cells.delete(cell);
    setQueries(this.id, { session: this._session, alive: [...alive], shown: new Map(this._shown) });
  }

  /** The session ended: no query of it is left. Answers what to say. */
  private _ended(): string {
    this._stopped = true;
    this._alive = [];
    this._shown.clear();
    this._cells.clear();
    setQueries(this.id, null);
    return this._variant.texts.noticePrefix + (this._interrupted
      ? 'Interrupted. The session ended with the query: the next cell starts a new one, '
        + 'and what this one had loaded has to be loaded again.'
      : 'The server stopped. The next cell starts a new session.');
  }

  /** Ends the queries the run before of a cell left alive. */
  private async _endQueriesOf(cellId: string | null): Promise<void> {
    const left = cellId ? this._cells.get(cellId) : undefined;
    if (!cellId || !left) return;
    this._cells.delete(cellId);
    for (const query of left) {
      if (!this._alive.includes(query)) continue;
      const answer = await this._ask(request(0, 'stop', { query }));
      if (Array.isArray(answer?.result?.alive)) this._took(answer.result.alive.map(Number));
    }
  }

  /** Shows the outputs of a response, and takes note of its queries: an
   *  answer says which of its query's solutions it is, and of which
   *  session. */
  private async _show(rendered: IRendered, show: Show, cellId: string | null): Promise<void> {
    for (const output of rendered.outputs) {
      const answer = output[ANSWER] as { query: number; text: string } | undefined;
      if (answer) {
        const solution = (this._shown.get(answer.query) ?? 0) + 1;
        this._shown.set(answer.query, solution);
        // The cell a query's first solution is under is the one whose next
        // run ends it. Not a cell that retried it: running `retry.` again
        // is how the next solution is asked for.
        if (solution === 1 && cellId) this._cells.set(cellId, [...(this._cells.get(cellId) ?? []), answer.query]);
        const named: IAnswer = { session: this._session, query: answer.query, solution, text: answer.text };
        output[ANSWER] = named;
      }
      show(await drawn(output, this._variant.texts.noticePrefix));
    }
    if (rendered.stopped) this._ended();
    else if (rendered.alive) this._took(rendered.alive);
  }

  /** The page asks a query for more solutions, or ends it. The solutions
   *  are shown as a cell's are; the reply says whether the query can still
   *  be asked. */
  private async _asked(text: string, show: Show): Promise<Reply> {
    const reply = (asked: IAsked): Reply => ({
      status: 'ok', execution_count: this.executionCount, payload: [], user_expressions: { [ASK]: asked as any }
    });
    if (this._runtime?.stopped || this._stopped || this._refusal || !this._runtime) {
      setQueries(this.id, null);
      return reply({ alive: false, ended: true });
    }
    let ask: IAsk;
    try {
      ask = JSON.parse(text);
    } catch {
      return reply({ alive: false });
    }
    const query = 'more' in ask ? ask.more.query : ask.stop.query;
    try {
      const answer = await this._exchange('more' in ask
        ? request(this.executionCount, 'more', ask.more)
        : request(this.executionCount, 'stop', ask.stop), show);
      if (answer === null) {
        show({ 'text/plain': this._ended() });
        return reply({ alive: false, ended: true });
      }
      if (answer.error) {
        // Nothing ran: the query is not one to ask, or not the latest.
        if (answer.error.code === -4713) this._took(this._alive.filter(alive => alive !== query));
      } else if ('stop' in ask) {
        this._took((answer.result?.alive ?? []).map(Number));
      } else {
        await this._show(render(answer, this._variant.texts), show, null);
      }
      return reply({ alive: this._alive.includes(query), ended: this._stopped });
    } catch (reason) {
      if (!(reason instanceof RuntimeStopped)) throw reason;
      setQueries(this.id, null);
      show({ 'text/plain': '\x1b[31;1m' + this._variant.texts.errorPrefix + reason.message });
      return reply({ alive: false, ended: true });
    }
  }

  async completeRequest(
    content: KernelMessage.ICompleteRequestMsg['content']
  ): Promise<KernelMessage.ICompleteReplyMsg['content']> {
    await this._started;
    // The names the last cell defined, when the server is free to be asked:
    // it takes one request at a time.
    if (this._predicatesStale && !this._running && !this._stopped && !this._refusal) {
      try { await this._refreshPredicates(); } catch { /* the names it had */ }
    }
    const name = nameBefore(content.code, content.cursor_pos);
    return {
      status: 'ok',
      matches: completions(this._predicates, name),
      cursor_start: content.cursor_pos - name.length,
      cursor_end: content.cursor_pos,
      metadata: {}
    };
  }

  async inspectRequest(
    content: KernelMessage.IInspectRequestMsg['content']
  ): Promise<KernelMessage.IInspectReplyMsg['content']> {
    await this._started;
    const found = documentation(this._docs, nameAt(content.code, content.cursor_pos));
    return { status: 'ok', found: found !== null, data: (found ?? {}) as any, metadata: {} };
  }

  async isCompleteRequest(): Promise<KernelMessage.IIsCompleteReplyMsg['content']> {
    return { status: 'unknown' };
  }

  async commInfoRequest(): Promise<KernelMessage.ICommInfoReplyMsg['content']> {
    return { status: 'ok', comms: {} };
  }

  /** The line a goal that reads was waiting for. */
  inputReply(content: KernelMessage.IInputReplyMsg['content']): void {
    const api = this._runtime?.api;
    if (!api) return;
    if (content.status === 'ok') void api.SessionSupplyInput(this.id, content.value);
    else void api.SessionSupplyEndOfFile(this.id);
  }

  async commOpen(): Promise<void> { /* no comms */ }
  async commMsg(): Promise<void> { /* no comms */ }
  async commClose(): Promise<void> { /* no comms */ }

  private _variant: IVariant;
  private _contents: Contents.IManager;
  private _sessions: Session.IManager;
  private _directory: string;
  private _started: Promise<void>;
  private _runtime: IRuntime | null = null;
  /** Why there is no session, when there is none. */
  private _refusal: string | null = null;
  /** The server ended: the next cell opens another session. */
  private _stopped = false;
  private _running = false;
  private _interrupted = false;
  /** When the cell being answered was sent, and when an interrupt came that
   *  found no query running: one after the other stops the cell before its
   *  query leaves. */
  private _sentAt = 0;
  private _interruptedAt = -1;
  /** The session's name among the kernel's, and its queries alive (oldest
   *  first), how many solutions of each were shown, and which each cell
   *  left alive. */
  private _session = '';
  private _alive: number[] = [];
  private _shown = new Map<number, number>();
  private _cells = new Map<string, number[]>();
  /** Whether the request being answered takes input. */
  private _allowInput = true;
  private _version = '';
  private _tier = '';
  private _predicates: string[] = [];
  private _predicatesStale = false;
  private _docs: { [predicate: string]: string } = {};
}

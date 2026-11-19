// A JupyterLite kernel over a session of the runtime (ADR-067, ADR-068). It
// maps Jupyter's messages to request lines of the notebook server and shows
// its answers. Nothing here names a language: what is the variant's comes in
// as IVariant (ADR-073).

import { PathExt } from '@jupyterlab/coreutils';
import type { Contents, KernelMessage, Session } from '@jupyterlab/services';
import { BaseKernel, type IKernel } from '@jupyterlite/services';

import {
  completions, documentation, nameAt, nameBefore, render, type IOutput, type ITexts
} from './render';
import { DRIVE, runtime, type IRuntime } from './runtime';

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

const request = (id: number, method: string, params?: object): string =>
  JSON.stringify(params ? { jsonrpc: '2.0', id, method, params } : { jsonrpc: '2.0', id, method });

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
    this._runtime?.onInput(this.id, null);
    void this._runtime?.api.SessionDispose(this.id);
  }

  /** Stops the query that is running. The server is that query, so the
   *  session ends with it; the next cell starts another (ADR-067 D5). */
  async interrupt(): Promise<void> {
    if (!this._runtime || !this._running) return;
    this._interrupted = true;
    await this._runtime.api.SessionCancel(this.id);
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
    const { api, onInput } = this._runtime!;
    this._refusal = null;
    this._stopped = false;
    const refused = await api.SessionCreate(this.id, this._variant.bundle, this._directory);
    if (refused) {
      // The limit is met sooner than it looks: a kernel outlives its
      // notebook's tab.
      this._refusal = /limit/i.test(refused)
        ? `${refused} A kernel keeps running after its notebook is closed: shut one down in the `
          + '"Running Terminals and Kernels" panel, then restart this one.'
        : refused;
      return;
    }
    onInput(this.id, () => this.inputRequest({ prompt: '', password: false }));

    const protocol = await this._ask(request(0, 'protocol'));
    if (protocol?.result?.version !== this._variant.protocol) {
      this._refusal = `The server speaks protocol ${JSON.stringify(protocol?.result?.version)} `
        + `and this kernel speaks ${this._variant.protocol}.`;
      return;
    }
    this._version = String((await this._ask(request(0, 'version')))?.result ?? '');
    this._tier = await api.SessionTier(this.id);
    await this._refreshPredicates();
    this._docs = (await this._ask(request(0, 'jupyter_predicate_docs')))?.result ?? {};
  }

  /** One request and its response; null when the server has stopped. */
  private async _ask(line: string): Promise<any> {
    const answer = await this._runtime!.api.SessionRequest(this.id, line);
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
    await this._started;
    const { texts } = this._variant;
    const show = (output: IOutput) => this.displayData({ data: output as any, metadata: {} });
    const failed = (ename: string, text: string): KernelMessage.IExecuteReplyMsg['content'] => ({
      status: 'error', execution_count: this.executionCount, ename, evalue: '', traceback: [text]
    });

    if (this._stopped && this._runtime) {
      // After halt, an interrupt or an exception the server did not handle.
      try {
        await this._runtime.api.SessionDispose(this.id);
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

    const { mirror, libraries } = this._runtime;
    const mirrored = async (copy: () => Promise<unknown>) => {
      try {
        await copy();
      } catch (reason) {
        show({ 'text/plain': `${texts.noticePrefix}The files could not be brought up to date: ${reason}` });
      }
    };
    // A collection the page could not bring is one the cell does not find,
    // and the cell says so.
    const quietly = async (step: () => Promise<unknown>) => {
      try { await step(); } catch { /* see above */ }
    };

    // What changed in the file browser and the editor is there for the cell,
    // and so is a collection imported in another tab.
    await mirrored(() => mirror.pull());
    await quietly(() => libraries.sync());
    const notebook = await this._notebook();
    this._running = true;
    this._interrupted = false;
    let answer: any;
    try {
      answer = await this._ask(request(this.executionCount, 'call',
        notebook ? { code: content.code, notebook } : { code: content.code }));
    } finally {
      this._running = false;
    }
    // And what the cell wrote is there for the file browser.
    await mirrored(() => mirror.push());
    await quietly(() => libraries.keep());
    this._predicatesStale = true;

    if (answer === null) {
      this._stopped = true;
      const text = texts.noticePrefix + (this._interrupted
        ? 'Interrupted. The session ended with the query: the next cell starts a new one, '
          + 'and what this one had loaded has to be loaded again.'
        : 'The server stopped. The next cell starts a new session.');
      show({ 'text/plain': text });
      return failed(this._interrupted ? 'interrupted' : 'stopped', text);
    }

    const rendered = render(answer, texts);
    rendered.outputs.forEach(show);
    if (rendered.predicates) this._predicates = rendered.predicates;
    if (rendered.stopped) this._stopped = true;
    if (rendered.error)
      return { status: 'error', execution_count: this.executionCount, evalue: '', ...rendered.error };
    return { status: 'ok', execution_count: this.executionCount, payload: [], user_expressions: {} };
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
  private _version = '';
  private _tier = '';
  private _predicates: string[] = [];
  private _predicatesStale = false;
  private _docs: { [predicate: string]: string } = {};
}

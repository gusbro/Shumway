// The answer of a query that can give another solution, drawn with a top
// level's choices under it (ADR-074): Next, 5, All and Stop. The solutions
// asked for are added to the cell the query is in, so they are saved with
// the notebook, and Stop acts between two solutions only.

import type { JupyterFrontEnd } from '@jupyterlab/application';
import type { IRenderMime } from '@jupyterlab/rendermime-interfaces';
import type { Kernel, KernelMessage } from '@jupyterlab/services';
import { Widget } from '@lumino/widgets';

import {
  ANSWER, ASK, askingOf, onQueries, queriesOf, setAsking, toNextFive,
  type IAnswer, type IAsk, type IAsked
} from './core/queries';

/** How long the solutions asked for may take after Stop before the answer
 *  says that only an interrupt ends them. */
const LATE_MS = 5000;

/** The outputs of a cell, as much of their model as is used here. */
interface IOutputs {
  readonly length: number;
  get(index: number): { toJSON(): any; data?: { [mime: string]: unknown } };
  set(index: number, value: any): void;
  add(value: any): number;
}

/** Where an answer is: its cell's outputs, the kernel of its notebook, and
 *  the notebook and the cell themselves, as much of them as is used here. */
interface IPlace {
  outputs: IOutputs;
  kernel: Kernel.IKernelConnection;
  notebook: any;
  cell: any;
}

/** The notebook cell that holds `node`, found through the shell and not
 *  through the notebook's own package: an answer anywhere else, as in a
 *  console or in a view of an output, has no place and no buttons. */
function placeOf(app: JupyterFrontEnd, node: HTMLElement): IPlace | null {
  for (const widget of app.shell.widgets('main')) {
    const panel = widget as any;
    const cells = panel?.content?.widgets;
    const kernel = panel?.sessionContext?.session?.kernel;
    if (!Array.isArray(cells) || !kernel) continue;
    for (const cell of cells) {
      if (cell?.node?.contains(node) && cell.model?.outputs)
        return { outputs: cell.model.outputs, kernel, notebook: panel.content, cell };
    }
  }
  return null;
}

/** Sends the kernel a request of the page's own and answers what it says of
 *  the query. The solutions it shows go to the cell's outputs. */
async function ask(place: IPlace, asked: IAsk): Promise<IAsked> {
  const future = place.kernel.requestExecute({
    code: '', silent: true, store_history: false, allow_stdin: false, stop_on_error: false,
    user_expressions: { [ASK]: JSON.stringify(asked) }
  });
  future.onIOPub = (message: KernelMessage.IIOPubMessage) => {
    if (message.header.msg_type !== 'display_data') return;
    const content = (message as KernelMessage.IDisplayDataMsg).content;
    place.outputs.add({ output_type: 'display_data', data: content.data, metadata: content.metadata ?? {} });
  };
  const reply = await future.done;
  const said = (reply.content as { user_expressions?: { [key: string]: unknown } }).user_expressions?.[ASK];
  return (said as IAsked | undefined) ?? { alive: false, ended: true };
}

/** The last solution shown of a query ends with a full stop once the query
 *  was stopped, as the answer does at a top level after Enter. */
function endWithFullStop(outputs: IOutputs, answer: IAnswer): void {
  for (let index = outputs.length - 1; index >= 0; index--) {
    const output = outputs.get(index).toJSON();
    const named = output?.data?.[ANSWER] as IAnswer | undefined;
    if (!named || named.session !== answer.session || named.query !== answer.query) continue;
    const ended = (text: string) => text.replace(/ ;$/, '.');
    outputs.set(index, {
      ...output,
      data: { ...output.data, 'text/plain': ended(String(output.data['text/plain'] ?? '')), [ANSWER]: { ...named, text: ended(named.text) } }
    });
    return;
  }
}

/** After a button was pressed, the notebook is where running the cell
 *  leaves it: the cell below is the active one, a new one when there was
 *  none, and the keys are the notebook's again. The button that had them is
 *  gone when its answer is drawn again. */
function advance(app: JupyterFrontEnd, place: IPlace): void {
  const { notebook, cell } = place;
  const index: number = notebook.widgets.indexOf(cell);
  if (index < 0) return;
  notebook.mode = 'command';
  if (index === notebook.widgets.length - 1) {
    notebook.activeCellIndex = index;
    void app.commands.execute('notebook:insert-cell-below');
  } else {
    notebook.activeCellIndex = index + 1;
    notebook.deselectAll?.();
  }
  notebook.activate?.();
}

type Choice = 'next' | 'five' | 'all' | 'stop';

/** What a button asks for. Stop pressed while several solutions are being
 *  asked for only says so: the one asking ends the query when they come. */
async function choose(place: IPlace, answer: IAnswer, choice: Choice): Promise<void> {
  const kernelId = place.kernel.id;
  const query = answer.query;
  const asking = askingOf(kernelId);
  if (asking) {
    if (choice === 'stop' && asking.query === query && asking.several && !asking.stopping) {
      setAsking(kernelId, { ...asking, stopping: true });
      setTimeout(() => {
        const now = askingOf(kernelId);
        if (now && now.query === query && now.stopping) setAsking(kernelId, { ...now, late: true });
      }, LATE_MS);
    }
    return;
  }
  const stop = async () => {
    const said = await ask(place, { stop: { query } });
    if (!said.alive && !said.ended) endWithFullStop(place.outputs, answer);
  };
  setAsking(kernelId, { query, several: choice === 'five' || choice === 'all', stopping: false, late: false });
  try {
    if (choice === 'stop') {
      await stop();
    } else {
      let said: IAsked;
      do {
        const shown = queriesOf(kernelId)?.shown.get(query) ?? answer.solution;
        said = await ask(place, { more: { query, count: choice === 'next' ? 1 : toNextFive(shown) } });
      } while (choice === 'all' && said.alive && !askingOf(kernelId)?.stopping);
      if (said.alive && askingOf(kernelId)?.stopping) await stop();
    }
  } finally {
    setAsking(kernelId, null);
  }
}

const BUTTONS: { choice: Choice; label: string; title: string }[] = [
  { choice: 'next', label: 'Next', title: 'The next solution (;)' },
  { choice: 'five', label: '5', title: 'On to the next multiple of five solutions' },
  { choice: 'all', label: 'All', title: 'Every remaining solution' },
  { choice: 'stop', label: 'Stop', title: 'End the query: no more solutions of it' }
];

// The answers of each query that are drawn in the page, and the one of
// them with the latest solution, which is the one that has the buttons.
// Not the latest solution asked for: an output area stops drawing its
// outputs after some tens of them, and the buttons, Stop among them, must
// stay where they can be pressed.
const drawn = new Map<string, Set<Answer>>();
const holders = new Map<string, Answer>();
const keyOf = (answer: IAnswer): string => `${answer.session}#${answer.query}`;

/** The renderer of an answer. */
export class Answer extends Widget implements IRenderMime.IRenderer {
  constructor(app: JupyterFrontEnd) {
    super();
    this._app = app;
    this.addClass('shumway-answer');
    this._stopListening = onQueries(() => this._draw());
  }

  async renderModel(model: IRenderMime.IMimeModel): Promise<void> {
    this._leave();
    this._answer = model.data[ANSWER] as unknown as IAnswer;
    if (this.isAttached) this._join();
    this._draw();
  }

  dispose(): void {
    this._leave();
    this._stopListening();
    super.dispose();
  }

  /** In the page: the cell it is in can be found now. */
  protected onAfterAttach(): void {
    this._join();
    this._draw();
  }

  protected onBeforeDetach(): void {
    this._leave();
  }

  /** Among the answers of its query drawn in the page; the one with the
   *  latest solution holds the buttons. */
  private _join(): void {
    const answer = this._answer;
    if (!answer || this._joined) return;
    this._joined = true;
    const key = keyOf(answer);
    if (!drawn.has(key)) drawn.set(key, new Set());
    drawn.get(key)!.add(this);
    const holder = holders.get(key);
    if (!holder || holder._answer!.solution < answer.solution) {
      holders.set(key, this);
      holder?._draw();
    }
  }

  private _leave(): void {
    const answer = this._answer;
    if (!answer || !this._joined) return;
    this._joined = false;
    const key = keyOf(answer);
    const others = drawn.get(key);
    others?.delete(this);
    if (holders.get(key) !== this) return;
    let latest: Answer | null = null;
    for (const other of others ?? []) if (!latest || other._answer!.solution > latest._answer!.solution) latest = other;
    if (latest) holders.set(key, latest);
    else { holders.delete(key); drawn.delete(key); }
    latest?._draw();
  }

  /** Draws the text once, and the buttons again only when what they show
   *  changed: every answer in the page hears of every change. */
  private _draw(): void {
    const answer = this._answer;
    if (!answer || this.isDisposed) return;
    if (!this._text) {
      this._text = document.createElement('pre');
      this._text.className = 'shumway-answer-text';
      this.node.replaceChildren(this._text);
    }
    this._text.textContent = answer.text;

    // An answer stays in its cell: the cell is looked for once.
    if (!this._place && this.isAttached) this._place = placeOf(this._app, this.node);
    const place = this._place;
    const queries = place ? queriesOf(place.kernel.id) : null;
    // Only the latest solution drawn of a query alive, of this session.
    const shown = !!place && !!queries && queries.session === answer.session
      && queries.alive.includes(answer.query) && holders.get(keyOf(answer)) === this;
    const latest = shown && queries!.alive[queries!.alive.length - 1] === answer.query;
    const asking = shown ? askingOf(place!.kernel.id) : null;
    const mine = asking?.query === answer.query;
    const state = !shown ? '' : [latest, !!asking, mine, asking?.several, asking?.stopping, asking?.late].join();
    if (state === this._state) return;
    this._state = state;
    while (this._text.nextSibling) this._text.nextSibling.remove();
    if (!shown) return;

    const bar = document.createElement('div');
    bar.className = 'shumway-answer-choices';
    // Says, to a reader of the page that is not looking at it, that the
    // query is being asked: solutions are on their way.
    bar.setAttribute('aria-busy', String(mine));
    for (const { choice, label, title } of BUTTONS) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'shumway-answer-choice';
      button.dataset.choice = choice;
      button.textContent = choice === 'stop' && mine && asking?.stopping ? 'Stopping…' : label;
      button.title = latest ? title : 'A later query is still open: its solutions come first';
      button.disabled = !latest || (!!asking && !(choice === 'stop' && mine && asking.several && !asking.stopping));
      button.addEventListener('click', () => {
        void choose(place!, answer, choice);
        advance(this._app, place!);
      });
      bar.appendChild(button);
    }
    this.node.appendChild(bar);
    if (mine && asking?.late) {
      const late = document.createElement('div');
      late.className = 'shumway-answer-late';
      late.textContent = 'The solutions being computed have not come. '
        + 'Only Interrupt, in the Kernel menu, ends them now, and it restarts the session.';
      this.node.appendChild(late);
    }
  }

  private _app: JupyterFrontEnd;
  private _answer: IAnswer | null = null;
  private _text: HTMLElement | null = null;
  private _place: IPlace | null = null;
  private _joined = false;
  /** What the buttons last drawn showed; null before the first draw. */
  private _state: string | null = null;
  private _stopListening: () => void;
}

/** Asks the next solution of the notebook's latest query alive, which is
 *  the one that can be asked, whichever cell is the active one: after a cell
 *  runs, or a button is pressed, the active cell is the one below. Answers
 *  whether there was one to ask. */
export function nextSolution(app: JupyterFrontEnd): boolean {
  const panel = app.shell.currentWidget as any;
  const cells = panel?.content?.widgets;
  const kernel: Kernel.IKernelConnection | undefined = panel?.sessionContext?.session?.kernel;
  if (!Array.isArray(cells) || !kernel) return false;
  const queries = queriesOf(kernel.id);
  if (!queries || queries.alive.length === 0 || askingOf(kernel.id)) return false;
  const query = queries.alive[queries.alive.length - 1];
  // The latest solution shown of that query, in whichever cell it is: under
  // the query's own, or under one that retried it.
  let latest: { place: IPlace; answer: IAnswer } | null = null;
  for (const cell of cells) {
    const outputs: IOutputs | undefined = cell?.model?.outputs;
    for (let index = 0; outputs && index < outputs.length; index++) {
      const answer = outputs.get(index).data?.[ANSWER] as IAnswer | undefined;
      if (!answer || answer.session !== queries.session || answer.query !== query) continue;
      if (!latest || answer.solution > latest.answer.solution)
        latest = { place: { outputs, kernel, notebook: panel.content, cell }, answer };
    }
  }
  if (!latest) return false;
  void choose(latest.place, latest.answer, 'next');
  return true;
}

/** The look of an answer, in the theme's colours. */
export function addStyles(): void {
  if (document.getElementById('shumway-answer-styles')) return;
  const style = document.createElement('style');
  style.id = 'shumway-answer-styles';
  style.textContent = `
.shumway-answer-text {
  margin: 0;
  font-weight: bold;
  font-family: var(--jp-code-font-family);
  font-size: var(--jp-code-font-size);
  line-height: var(--jp-code-line-height);
  white-space: pre-wrap;
  color: var(--jp-content-font-color1);
  background: none;
  border: none;
  padding: 0;
}
.shumway-answer-choices { display: flex; gap: 6px; margin: 4px 0 2px; }
.shumway-answer-choice {
  font-family: var(--jp-ui-font-family);
  font-size: var(--jp-ui-font-size1);
  color: var(--jp-ui-font-color1);
  background: var(--jp-layout-color2);
  border: 1px solid var(--jp-border-color1);
  border-radius: 3px;
  padding: 1px 10px;
  cursor: pointer;
}
.shumway-answer-choice:hover:not([disabled]) { background: var(--jp-layout-color3); }
.shumway-answer-choice[disabled] { color: var(--jp-ui-font-color3); cursor: default; }
.shumway-answer-late {
  font-family: var(--jp-ui-font-family);
  font-size: var(--jp-ui-font-size1);
  color: var(--jp-warn-color1);
  margin-top: 2px;
}`;
  document.head.appendChild(style);
}

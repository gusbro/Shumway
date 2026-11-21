// What a response of the server looks like in a notebook
// (docs/design/jupyterlite-protocol.md, sections 3 and 4). No Jupyter and no
// browser in here: a response goes in, the outputs to show come out.

import { plotOf, VEGA_LITE } from './data';

/** The texts that are the variant's own. */
export interface ITexts {
  /** A query that succeeded and bound nothing. */
  success: string;
  /** A query that failed. */
  failure: string;
  errorPrefix: string;
  noticePrefix: string;
}

/** One output of a cell: a MIME bundle. */
export interface IOutput {
  [mime: string]: unknown;
}

export interface IRendered {
  outputs: IOutput[];
  /** The first error of the cell, when one of its terms failed or raised. */
  error: { ename: string; traceback: string[] } | null;
  /** The server stopped: by halt, or by an exception it did not handle. */
  stopped: boolean;
  /** The names to complete with, when the answer brought them. */
  predicates: string[] | null;
}

const BOLD = '\x1b[1m';
const RED_BOLD = '\x1b[31;1m';

const plain = (text: string, style = ''): IOutput => ({ 'text/plain': style + text });

/** A markdown table, and the same as text for where markdown is not shown. */
function table(names: unknown[], rows: unknown[][]): IOutput {
  const line = (cells: unknown[]) => cells.map(cell => String(cell)).join(' | ') + ' | ';
  const text = [line(names), names.map(() => ':- | ').join(''), ...rows.map(line)].join('\n');
  // In markdown these start a formula, a strikethrough and a column.
  const cell = (value: unknown) => String(value).replace(/[$~|]/g, c => '\\' + c);
  const markdown = [
    line(names.map(cell)),
    names.map(() => ':- | ').join(''),
    ...rows.map(row => line(row.map(cell)))
  ].join('\n');
  return { 'text/plain': text, 'text/markdown': markdown };
}

const isJson = (mime: string) => /[/+]json$/.test(mime);

/** What a plot is, as text: what a reader without Vega-Lite sees. */
function plotSummary(data: { type?: unknown; title?: unknown }): string {
  const title = typeof data.title === 'string' ? data.title
    : data.title && typeof data.title === 'object' ? String((data.title as { label?: unknown }).label ?? '') : '';
  return `${String(data.type ?? 'a')} plot${title ? `: ${title}` : ''}`;
}

/** The registered MIME type of DOT text. */
export const GRAPHVIZ = 'text/vnd.graphviz';

/** What `jupyter:display/2` asked to show. */
function displayed(item: { mime?: unknown; data?: unknown }, texts: ITexts): IOutput {
  const mime = String(item.mime ?? 'text/plain');
  const data = String(item.data ?? '');
  if (!isJson(mime)) return { 'text/plain': data, [mime]: data };
  try {
    return { 'text/plain': data, [mime]: JSON.parse(data) };
  } catch (reason) {
    return plain(`${texts.errorPrefix}${mime} was given text that is not JSON: ${reason}`, RED_BOLD);
  }
}

/** The additional data of an answer (section 4). */
function additional(entry: any, texts: ITexts, rendered: IRendered): void {
  for (const key of ['print_table', 'print_and_save_table']) {
    const shown = entry[key];
    if (shown) rendered.outputs.push(table(shown.VariableNames ?? [], shown.ValuesLists ?? []));
  }
  for (const item of entry.display_data ?? []) rendered.outputs.push(displayed(item, texts));
  // A plot of show_data/1: its Vega-Lite specification, which JupyterLab
  // draws, and the options it did not draw, named.
  if (entry.show_data !== null && typeof entry.show_data === 'object') {
    try {
      const { spec, ignored } = plotOf(entry.show_data);
      rendered.outputs.push({ [VEGA_LITE]: spec, 'text/plain': plotSummary(entry.show_data) });
      if (ignored.length > 0)
        rendered.outputs.push(plain(`${texts.noticePrefix}Not drawn here: ${ignored.join(', ')}.`));
    } catch (reason) {
      rendered.outputs.push(plain(`${texts.errorPrefix}The plot could not be drawn: ${
        reason instanceof Error ? reason.message : reason}`, RED_BOLD));
    }
  }
  // A graph is its DOT here; the kernel draws it (graph.ts) before it shows.
  for (const key of ['print_transition_graph', 'print_sld_tree']) {
    const dot = entry[key];
    if (typeof dot === 'string') rendered.outputs.push({ 'text/plain': dot, [GRAPHVIZ]: dot });
  }
  if (Array.isArray(entry.predicate_atoms)) rendered.predicates = entry.predicate_atoms.map(String);
}

/** How an answer ends when the server says which queries are alive
 *  (ADR-074), as a top level's does: with ` ;` when the query can give
 *  another solution, and with a full stop when it cannot. */
const ending = (entry: any, marked: boolean): string =>
  !marked ? '' : typeof entry?.alive === 'number' ? ' ;' : '.';

function success(entry: any, texts: ITexts, rendered: IRendered, marked: boolean): void {
  additional(entry, texts, rendered);
  if (entry.output) rendered.outputs.push(plain(String(entry.output)));
  if (entry.type !== 'query') return;

  const lines: string[] = [];
  for (const [name, value] of Object.entries(entry.bindings ?? {})) {
    if (value !== null && typeof value === 'object') {
      if ('dom' in (value as object)) lines.push(`${name} in ${(value as any).dom}`);
    } else {
      lines.push(`${name} = ${value}`);
    }
  }
  for (const goal of entry.residual_goals ?? []) lines.push(String(goal));
  rendered.outputs.push(plain((lines.length > 0 ? lines.join(',\n') : texts.success) + ending(entry, marked), BOLD));
}

/** The text of an error, under the key of whichever server sent it. */
const message = (data: any): string => String(data?.prolog_message ?? data?.logtalk_message ?? '');

function failed(error: any, texts: ITexts, rendered: IRendered, marked = false): void {
  const data = error?.data ?? {};
  additional(data, texts, rendered);
  let output = '';
  if (data.output) {
    output = String(data.output);
    rendered.outputs.push(plain(output));
  }

  let ename = 'error';
  let text: string;
  switch (error?.code) {
    case -4711:
      ename = 'failure';
      text = message(data) || texts.failure + (marked ? '.' : '');
      break;
    case -4712:
      ename = 'exception';
      text = message(data);
      break;
    case -4715:
      ename = 'unhandled exception';
      text = message(data) + '\n' + texts.errorPrefix + 'The server stopped. The next cell starts a new session.';
      rendered.stopped = true;
      break;
    default:
      text = texts.errorPrefix + String(error?.message ?? 'The server answered with no result.');
  }
  rendered.outputs.push(plain(text, RED_BOLD));
  rendered.error ??= { ename, traceback: [output ? output + '\n' + text : text] };
}

/** The outputs of one response to a `call`. */
export function render(response: any, texts: ITexts): IRendered {
  const rendered: IRendered = { outputs: [], error: null, stopped: false, predicates: null };
  // A cell that does not parse is an error for the whole request.
  if (response?.error) {
    failed(response.error, texts, rendered);
    return rendered;
  }
  const result = response?.result;
  if (result === null || typeof result !== 'object') return rendered;   // an empty cell
  // A server that says which queries are alive has its answers end as a
  // top level's do. One that does not, the Logtalk variant's, has them as
  // they were.
  const marked = Array.isArray(result.alive);

  for (let index = 1; String(index) in result; index++) {
    const entry = result[String(index)];
    if (entry.status === 'halt') {
      rendered.outputs.push(plain(texts.noticePrefix + 'Halted. The next cell starts a new session.'));
      rendered.stopped = true;
    } else if (entry.status === 'error') {
      failed(entry.error, texts, rendered, marked);
    } else if (entry.status === 'success') {
      success(entry, texts, rendered, marked);
    }
  }
  return rendered;
}

// --- completion and inspection -------------------------------------------

const NAME = /[A-Za-z0-9_:$]/;

/** The name the cursor is at the end of. */
export function nameBefore(code: string, cursor: number): string {
  let start = cursor;
  while (start > 0 && NAME.test(code[start - 1])) start--;
  return code.slice(start, cursor);
}

/** The name the cursor is on. */
export function nameAt(code: string, cursor: number): string {
  let end = cursor;
  while (end < code.length && NAME.test(code[end])) end++;
  return nameBefore(code, cursor) + code.slice(cursor, end);
}

/** The predicates a name completes to: the ones it starts first. */
export function completions(predicates: string[], name: string): string[] {
  if (name === '') return [];
  const starting = predicates.filter(predicate => predicate.startsWith(name));
  const holding = predicates.filter(predicate => !predicate.startsWith(name) && predicate.includes(name));
  return [...starting, ...holding];
}

/** The documentation of the helpers a name matches, or null. */
export function documentation(docs: { [predicate: string]: string }, name: string): IOutput | null {
  if (name === '') return null;
  const texts = Object.keys(docs).filter(predicate => predicate.includes(name)).map(predicate => docs[predicate]);
  if (texts.length === 0) return null;
  const escaped = (text: string) =>
    text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/\$/g, '&#36;').replace(/\n/g, '<br>');
  return {
    'text/plain': texts.join('\n\n'),
    'text/markdown': texts.map(text => `<pre>${escaped(text)}</pre>`).join('\n\n')
  };
}

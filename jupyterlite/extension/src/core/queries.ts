// The queries of a kernel's session that can give another solution
// (ADR-074), as the kernel knows them and as the page draws them. The
// kernel and the part of the extension that draws an answer run in one
// page: this is what they share, with no message between them.

/** The MIME type of an answer that says which query gave it. Its plain text
 *  is beside it, for a front end that does not draw it. */
export const ANSWER = 'application/vnd.shumway.answer+json';

/** The key, in an execute request's `user_expressions`, under which the page
 *  asks a query for more solutions or ends it; and, in the reply's, under
 *  which the kernel answers. The request has no code and no history. */
export const ASK = 'shumway.queries';

/** What an answer says of itself. */
export interface IAnswer {
  /** The session that gave it: one of another session has no buttons. */
  session: string;
  query: number;
  /** Which of the query's solutions it is, from 1. */
  solution: number;
  text: string;
}

/** What the page asks of a query, as JSON under ASK. */
export type IAsk = { more: { query: number; count: number } } | { stop: { query: number } };

/** What the kernel answers under ASK. */
export interface IAsked {
  /** Whether the query can still be asked. */
  alive: boolean;
  /** The session ended, or the runtime stopped: no query of it is left. */
  ended?: boolean;
}

export interface IQueries {
  /** Changes with every session of the kernel. */
  session: string;
  /** The queries alive, oldest first: the last is the one that can be asked. */
  alive: number[];
  /** How many solutions of each query alive were shown. */
  shown: Map<number, number>;
}

/** What the page is asking a kernel's query for. */
export interface IAsking {
  query: number;
  /** Several solutions are being asked for, which Stop can end between two. */
  several: boolean;
  /** Stop was pressed: the query ends when the solutions asked for come. */
  stopping: boolean;
  /** They have not come in a while. */
  late: boolean;
}

const queries = new Map<string, IQueries>();
const asking = new Map<string, IAsking>();
const listeners = new Set<() => void>();

const changed = (): void => {
  for (const listener of [...listeners]) listener();
};

/** The queries of a kernel's session, or null when it has no session. */
export const queriesOf = (kernelId: string): IQueries | null => queries.get(kernelId) ?? null;

export function setQueries(kernelId: string, state: IQueries | null): void {
  if (state) queries.set(kernelId, state);
  else queries.delete(kernelId);
  changed();
}

export const askingOf = (kernelId: string): IAsking | null => asking.get(kernelId) ?? null;

export function setAsking(kernelId: string, state: IAsking | null): void {
  if (state) asking.set(kernelId, state);
  else asking.delete(kernelId);
  changed();
}

/** Calls `listener` when the queries or what is asked of them change;
 *  answers what stops it. */
export function onQueries(listener: () => void): () => void {
  listeners.add(listener);
  return () => { listeners.delete(listener); };
}

/** How many more solutions bring the count shown to the next multiple of
 *  five: four after one, five after five, as a top level's `f`. */
export const toNextFive = (shown: number): number => 5 - (shown % 5);

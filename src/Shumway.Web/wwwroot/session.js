// The async seam.
//
// The runtime does not live on this thread — the app is built with threads, so
// .NET runs on its own worker and every call to it is a message and a reply.
// That is what keeps the page drawable while a search runs. This module is the
// facade the UI talks to; nothing above it needs to know where the engine is.

import { dotnet } from './_framework/dotnet.js'

/** Reply tags from queryNext, matching WebShumwayApp. */
export const SOLUTION = 's';
export const LAST = 'l';
export const FAILED = 'f';
export const ERROR = 'e';

let engine = null;

/**
 * Starts the runtime and the engine.
 * @param {(text: string) => void} onOutput receives everything Prolog writes,
 *        as it is written — a program that prints while it searches should be
 *        watchable while it runs.
 */
export async function boot(onOutput, onAskForInput) {
  const { setModuleImports, getConfig, getAssemblyExports, runMain } = await dotnet.create();
  setModuleImports('main.js', { ui: { write: onOutput, askForInput: onAskForInput } });
  engine = (await getAssemblyExports(getConfig().mainAssemblyName)).Shumway.Web.WebShumwayApp;
  await runMain();
  return engine.Boot();
}

/** The raw exports, for the workspace module — which speaks to the engine's
 *  filesystem rather than to its solver, so it gets its own facade. */
export const exports = () => engine;

/** Loads the editor's buffer, replacing what it defines. Resolves to null on
 *  success, or the diagnostic. */
export const consult = async (source) => engine.ConsultBuffer(source);

/** Begins a query. Resolves to null when it started, or the diagnostic —
 *  a syntax error surfaces here, because the engine parses before it runs. */
export const start = async (queryText) => engine.QueryStart(queryText);

/** Takes the next solution: `{ tag, text }`. The engine runs this on a pool
 *  thread, so awaiting it does not block the page. */
export async function next(width = 80) {
  const reply = await engine.QueryNext(width);
  return { tag: reply[0], text: reply.slice(1) };
}

/** Abandons the running query. Returns immediately — it sets the cancellation
 *  token, which the engine observes at its next safe point, so the pending
 *  `next()` resolves shortly afterwards. */
export const cancel = async () => engine.QueryCancel();

/** Hands a waiting `read/1` a line of input. */
export const supplyInput = async (text) => engine.SupplyInput(text);

/** Ends the input stream: a waiting read gets `end_of_file`. */
export const supplyEndOfFile = async () => engine.SupplyEndOfFile();

/** Predicate names starting with `prefix`. */
export async function complete(prefix) {
  const s = engine.Complete(prefix);
  return s.length === 0 ? [] : s.split('\n');
}

/** Packs a program and a query into a fragment-safe string. */
export const shareEncode = (program, query) => engine.ShareEncode(program, query);

/** Unpacks one, or null if the text is not a valid share. */
export async function shareDecode(encoded) {
  const packed = await engine.ShareDecode(encoded);
  if (packed === null) return null;
  const nl = packed.indexOf('\n');
  const programLength = Number(packed.slice(0, nl));
  const rest = packed.slice(nl + 1);
  return { program: rest.slice(0, programLength), query: rest.slice(programLength) };
}

/** Flat [start, length, kind, …] spans covering `source`, from the engine's lexer. */
export async function highlight(source) {
  const packed = await engine.Highlight(source);
  return packed.length === 0 ? [] : packed.split(',').map(Number);
}

/** CSS-class names indexed by span kind. */
export const highlightKinds = async () => (await engine.HighlightKinds()).split(',');

// The async seam.
//
// The engine's exports are synchronous because it runs on this thread today.
// The UI never calls them; it calls THIS facade, which is async. Moving the
// engine to a Web Worker is then a change of transport, not of contract: these
// functions become postMessage round-trips and no UI code changes.

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
export async function boot(onOutput) {
  const { setModuleImports, getConfig, getAssemblyExports, runMain } = await dotnet.create();
  setModuleImports('main.js', { ui: { write: onOutput } });
  engine = (await getAssemblyExports(getConfig().mainAssemblyName)).Shumway.Web.WebShumwayApp;
  await runMain();
  return engine.Boot();
}

/** Loads Prolog source. Resolves to null on success, or the diagnostic. */
export const consult = async (source) => engine.Consult(source);

/** Begins a query. Resolves to null when it started, or the diagnostic —
 *  a syntax error surfaces here, because the engine parses before it runs. */
export const start = async (queryText) => engine.QueryStart(queryText);

/** Takes the next solution: `{ tag, text }`. */
export async function next(width = 80) {
  const reply = engine.QueryNext(width);
  return { tag: reply[0], text: reply.slice(1) };
}

/** Abandons the running query. The engine stops at its next safe point. */
export const cancel = async () => engine.QueryCancel();

/** Predicate names starting with `prefix`. */
export async function complete(prefix) {
  const s = engine.Complete(prefix);
  return s.length === 0 ? [] : s.split('\n');
}

/** Flat [start, length, kind, …] spans covering `source`, from the engine's lexer. */
export const highlight = async (source) => engine.Highlight(source);

/** CSS-class names indexed by span kind. */
export const highlightKinds = () => engine.HighlightKinds().split(',');
